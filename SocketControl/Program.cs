using System;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;
using SocketControl.Model;

namespace SocketControl;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        LogConfigurator.Configure();
        string configPath = args.Length > 0 ? args[0] : "config.json";
        ControlServerConfigFile config = SocketConfigLoader.Load<ControlServerConfigFile>(configPath);
        SocketFactory.Configure(config.SocketOptions);
        SecureSocketConnection.Configure(config.Security);

        using ControlServer server = new(config);
        if (!server.Start())
        {
            Environment.ExitCode = 1;
            return;
        }

        Console.WriteLine($"ControlServer started at {config.ControlServer.Host}:{server.Port}");
        Console.WriteLine("Press Ctrl+C to stop.");
        await WaitForShutdownAsync();
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
}
