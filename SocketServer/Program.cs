using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketServer;
class Program
{
    static async Task Main(string[] args)
    {
        LogConfigurator.Configure();
        SocketLogger logger = SocketLogManager.GetLogger<Program>();
        logger.Info("SocketServer console starting.");

        string configPath = args.FirstOrDefault(arg => arg.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) ?? "config.json";
        SocketServerConfigFile config = SocketConfigLoader.Load<SocketServerConfigFile>(configPath);
        SocketFactory.Configure(config.SocketOptions);
        SecureSocketConnection.Configure(config.Security);
        SocketCommon.Model.SocketAsyncEventArgsFactory.Configure(
            config.SocketAsyncEventArgsPool.InitialSize,
            config.SocketAsyncEventArgsPool.GrowthSize,
            config.SocketAsyncEventArgsPool.MaxRetained);
        int? selectedServerId = ReadServerId(args);
        bool runAll = args.Contains("--all", StringComparer.OrdinalIgnoreCase);
        SocketServerInstanceConfig[] configuredServers = config.Servers.ToArray();
        if (!runAll && !selectedServerId.HasValue && configuredServers.Length > 1)
        {
            const string message = "Multiple SocketServer instances are configured. Specify --server-id N for one process per instance, or --all to intentionally run every instance in this process.";
            logger.Warn(message);
            Console.Error.WriteLine(message);
            Environment.ExitCode = 1;
            return;
        }

        SocketServerInstanceConfig[] serverConfigs = (runAll
            ? configuredServers
            : selectedServerId.HasValue
                ? configuredServers.Where(server => server.ServerId == selectedServerId.Value)
                : configuredServers).ToArray();
        if (serverConfigs.Length == 0)
        {
            string message = selectedServerId.HasValue
                ? $"No SocketServer instance is configured for --server-id {selectedServerId.Value}."
                : "No SocketServer instances are configured.";
            logger.Warn(message);
            Console.Error.WriteLine(message);
            Environment.ExitCode = 1;
            return;
        }

        List<TcpServer> servers = new();
        List<ControlServerReporter> reporters = new();

        foreach (SocketServerInstanceConfig serverConfig in serverConfigs)
        {
            SocketSecurityConfigValidator.ValidateServerBinding(config.Security, serverConfig.BindHost);

            TcpServer server = new(
                serverConfig.ServerId,
                serverConfig.Name,
                serverConfig.BindHost,
                0,
                serverConfig.MaxConnections,
                serverConfig.PendingAcceptCount,
                TimeSpan.FromSeconds(serverConfig.IdleTimeoutSeconds),
                instanceId: serverConfig.InstanceId);
            server.ConfigureClientLocationCache(config.ClientLocationCache);

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

        Console.WriteLine("Press Ctrl+C to stop.");
        await WaitForShutdownAsync();

        foreach (ControlServerReporter reporter in reporters)
        {
            reporter.Dispose();
        }

        foreach (TcpServer server in servers)
        {
            server.Dispose();
        }
    }

    private static Task WaitForShutdownAsync()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            completion.TrySetResult();
        };

        Console.CancelKeyPress += handler;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => completion.TrySetResult();
        return completion.Task;
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
