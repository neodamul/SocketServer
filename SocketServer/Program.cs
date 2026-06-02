using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketServer.Model;

namespace SocketServer;
class Program
{
    static async Task Main(string[] args)
    {
        LogConfigurator.Configure();
        SocketLogger logger = SocketLogManager.GetLogger<Program>();
        logger.Info("SocketServer console starting.");

        string configPath = args.FirstOrDefault(arg => arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? "socketserver.json";
        SocketServerConfigFile config = SocketConfigLoader.Load<SocketServerConfigFile>(configPath);
        int? selectedServerId = ReadServerId(args);
        bool runAll = args.Contains("--all", StringComparer.OrdinalIgnoreCase) || !selectedServerId.HasValue;

        IEnumerable<SocketServerInstanceConfig> serverConfigs = runAll
            ? config.Servers
            : config.Servers.Where(server => server.ServerId == selectedServerId.Value);

        List<TcpServer> servers = new();
        List<ControlServerReporter> reporters = new();

        foreach (SocketServerInstanceConfig serverConfig in serverConfigs)
        {
            TcpServer server = new(
                serverConfig.ServerId,
                serverConfig.Name,
                serverConfig.BindHost,
                0,
                serverConfig.MaxConnections,
                serverConfig.PendingAcceptCount,
                TimeSpan.FromSeconds(serverConfig.IdleTimeoutSeconds),
                instanceId: serverConfig.InstanceId);

            if (!server.BindInPortRange(serverConfig.PortRangeStart, serverConfig.PortRangeEnd) ||
                !server.Listen() ||
                !server.StartClientAcceptLoop())
            {
                logger.Warn($"SocketServer instance start failed. serverId={serverConfig.ServerId}, instanceId={serverConfig.InstanceId}");
                server.Dispose();
                continue;
            }

            ControlServerReporter reporter = new(
                server,
                config.ControlServers,
                "socket-cluster-1",
                serverConfig.PortRangeStart,
                serverConfig.PortRangeEnd);
            await reporter.RegisterAsync();
            reporter.StartHeartbeatLoop(TimeSpan.FromSeconds(serverConfig.HeartbeatIntervalSeconds));

            servers.Add(server);
            reporters.Add(reporter);
            Console.WriteLine($"SocketServer {server.InstanceId} started at {server.GetIpAddress()}:{server.GetPort()}");
        }

        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();

        foreach (ControlServerReporter reporter in reporters)
        {
            reporter.Dispose();
        }

        foreach (TcpServer server in servers)
        {
            server.Dispose();
        }
    }

    private static int? ReadServerId(string[] args)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], "--server-id", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[index + 1], out int serverId))
            {
                return serverId;
            }
        }

        return null;
    }
}
