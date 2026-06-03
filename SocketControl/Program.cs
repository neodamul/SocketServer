using System;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;
using SocketControl.Model;

namespace SocketControl;

internal static class Program
{
    private static void Main(string[] args)
    {
        LogConfigurator.Configure();
        string configPath = args.Length > 0 ? args[0] : "config.json";
        ControlServerConfigFile config = SocketConfigLoader.Load<ControlServerConfigFile>(configPath);
        SecureSocketConnection.Configure(config.Security);

        using ControlServer server = new(config);
        if (!server.Start())
        {
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"ControlServer started at {config.ControlServer.Host}:{server.Port}");
        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();
    }
}
