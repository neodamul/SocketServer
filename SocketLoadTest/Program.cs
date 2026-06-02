using System.Diagnostics;
using SocketClient.Model;
using SocketCommon.Logging;
using SocketServer.Model;

namespace SocketLoadTest;

internal static class Program
{
    private const int HealthCheckTimeoutSeconds = 10;

    private static async Task<int> Main(string[] args)
    {
        if (!LoadTestOptions.TryParse(args, out LoadTestOptions options, out string error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        LogConfigurator.Configure(ResolveLogConfigFileName());

        using TcpServer? server = options.ExternalServer || options.UseControlServer
            ? null
            : StartInProcessServer(options);

        if (!options.ExternalServer && !options.UseControlServer && server == null)
        {
            return 1;
        }

        if (server != null)
        {
            options = options with { Port = server.GetPort() };
        }

        Console.WriteLine(
            $"Starting load test: clients={options.Clients}, batch-size={options.BatchSize}, hold-seconds={options.HoldSeconds}, endpoint={options.Host}:{options.Port}, external-server={options.ExternalServer}, use-control-server={options.UseControlServer}");

        Stopwatch stopwatch = Stopwatch.StartNew();
        LoadTestCounters counters = new();
        List<TcpClient> connectedClients = new(options.Clients);

        try
        {
            for (int firstClientId = 1; firstClientId <= options.Clients; firstClientId += options.BatchSize)
            {
                int batchCount = Math.Min(options.BatchSize, options.Clients - firstClientId + 1);
                ClientAttemptResult[] results = await ConnectBatchAsync(options, firstClientId, batchCount, counters);

                foreach (ClientAttemptResult result in results)
                {
                    if (result.Client != null)
                    {
                        connectedClients.Add(result.Client);
                    }
                }

                PrintProgress(counters, stopwatch.Elapsed);
            }

            if (options.HoldSeconds > 0)
            {
                Console.WriteLine($"Holding {connectedClients.Count} connected clients for {options.HoldSeconds} seconds.");
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
            }
        }
        finally
        {
            foreach (TcpClient client in connectedClients)
            {
                client.Dispose();
            }

            server?.End();
        }

        stopwatch.Stop();
        PrintSummary(counters, stopwatch.Elapsed);
        return counters.HealthCheckFail == 0 && counters.ConnectFail == 0 ? 0 : 2;
    }

    private static TcpServer? StartInProcessServer(LoadTestOptions options)
    {
        TcpServer server = new(0, "load-test-server", options.Host, options.Port);
        if (!server.Start() || !server.StartClientAcceptLoop())
        {
            Console.Error.WriteLine($"Failed to start in-process server at {options.Host}:{options.Port}.");
            server.End();
            return null;
        }

        Console.WriteLine($"Started in-process server at {server.GetIpAddress()}:{server.GetPort()}.");
        return server;
    }

    private static async Task<ClientAttemptResult[]> ConnectBatchAsync(
        LoadTestOptions options,
        int firstClientId,
        int batchCount,
        LoadTestCounters counters)
    {
        Task<ClientAttemptResult>[] tasks = new Task<ClientAttemptResult>[batchCount];
        for (int index = 0; index < batchCount; index++)
        {
            int clientId = firstClientId + index;
            tasks[index] = Task.Run(() => ConnectClientAsync(options, clientId, counters));
        }

        return await Task.WhenAll(tasks);
    }

    private static async Task<ClientAttemptResult> ConnectClientAsync(
        LoadTestOptions options,
        int clientId,
        LoadTestCounters counters)
    {
        Interlocked.Increment(ref counters.Attempted);

        TcpClient client = new(clientId, $"load-client-{clientId}", options.Host, options.Port);
        try
        {
            bool connected = options.UseControlServer
                ? await client.ConnectViaControlServerAsync(options.Host, options.Port)
                : client.Connect();
            if (!connected)
            {
                Interlocked.Increment(ref counters.ConnectFail);
                Interlocked.Increment(ref counters.HealthCheckFail);
                client.Dispose();
                return ClientAttemptResult.Failed;
            }

            Interlocked.Increment(ref counters.Connected);
            bool healthCheckSucceeded = await SendAndReceiveHealthCheckAsync(client);
            if (healthCheckSucceeded)
            {
                Interlocked.Increment(ref counters.HealthCheckSuccess);
                client.StartHealthCheckLoop();
                return new ClientAttemptResult(client);
            }

            Interlocked.Increment(ref counters.HealthCheckFail);
            client.Dispose();
            return ClientAttemptResult.Failed;
        }
        catch (Exception exception) when (exception is TimeoutException or InvalidOperationException)
        {
            Interlocked.Increment(ref counters.HealthCheckFail);
            client.Dispose();
            return ClientAttemptResult.Failed;
        }
    }

    private static async Task<bool> SendAndReceiveHealthCheckAsync(TcpClient client)
    {
        if (!await client.SendHealthCheckAsync())
        {
            return false;
        }

        Task<(bool Success, SocketCommon.Model.HealthCheckMessage Message)> receiveTask =
            client.TryReceiveHealthCheckAsync();
        Task completedTask = await Task.WhenAny(
            receiveTask,
            Task.Delay(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds)));

        if (completedTask != receiveTask)
        {
            return false;
        }

        (bool success, SocketCommon.Model.HealthCheckMessage message) = await receiveTask;
        return success && message.Type == SocketCommon.Model.HealthCheckMessageType.Pong;
    }

    private static string ResolveLogConfigFileName()
    {
        string[] candidates =
        [
            "log4net.load-test.config",
            "log4net.loadtest.config",
            "log4net.config"
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, candidate)))
            {
                return candidate;
            }
        }

        return "log4net.config";
    }

    private static void PrintProgress(LoadTestCounters counters, TimeSpan elapsed)
    {
        Console.WriteLine(
            $"Progress: attempted={counters.Attempted}, connected={counters.Connected}, healthcheck-success={counters.HealthCheckSuccess}, healthcheck-fail={counters.HealthCheckFail}, elapsed={elapsed}");
    }

    private static void PrintSummary(LoadTestCounters counters, TimeSpan elapsed)
    {
        Console.WriteLine("Load test complete.");
        Console.WriteLine($"Attempted: {counters.Attempted}");
        Console.WriteLine($"Connected: {counters.Connected}");
        Console.WriteLine($"Connect failed: {counters.ConnectFail}");
        Console.WriteLine($"Healthcheck succeeded: {counters.HealthCheckSuccess}");
        Console.WriteLine($"Healthcheck failed: {counters.HealthCheckFail}");
        Console.WriteLine($"Elapsed: {elapsed}");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project SocketLoadTest -- [--clients N] [--batch-size N] [--hold-seconds N] [--host IP] [--port N] [--external-server] [--use-control-server]");
    }
}

internal sealed class LoadTestCounters
{
    public int Attempted;
    public int Connected;
    public int ConnectFail;
    public int HealthCheckSuccess;
    public int HealthCheckFail;
}

internal sealed record ClientAttemptResult(TcpClient? Client)
{
    public static readonly ClientAttemptResult Failed = new((TcpClient?)null);
}

internal sealed record LoadTestOptions(
    int Clients,
    int BatchSize,
    int HoldSeconds,
    string Host,
    int Port,
    bool ExternalServer,
    bool UseControlServer)
{
    public static bool TryParse(string[] args, out LoadTestOptions options, out string error)
    {
        options = new LoadTestOptions(
            Clients: 10000,
            BatchSize: 100,
            HoldSeconds: 60,
            Host: "127.0.0.1",
            Port: 5000,
            ExternalServer: false,
            UseControlServer: false);
        error = string.Empty;

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            string? value = null;
            int separatorIndex = arg.IndexOf('=');
            if (separatorIndex >= 0)
            {
                value = arg[(separatorIndex + 1)..];
                arg = arg[..separatorIndex];
            }

            switch (arg)
            {
                case "--clients":
                    if (!TryReadInt(args, ref index, value, arg, 0, int.MaxValue, out int clients, out error))
                    {
                        return false;
                    }

                    options = options with { Clients = clients };
                    break;

                case "--batch-size":
                    if (!TryReadInt(args, ref index, value, arg, 1, int.MaxValue, out int batchSize, out error))
                    {
                        return false;
                    }

                    options = options with { BatchSize = batchSize };
                    break;

                case "--hold-seconds":
                    if (!TryReadInt(args, ref index, value, arg, 0, int.MaxValue, out int holdSeconds, out error))
                    {
                        return false;
                    }

                    options = options with { HoldSeconds = holdSeconds };
                    break;

                case "--host":
                    if (!TryReadString(args, ref index, value, arg, out string host, out error))
                    {
                        return false;
                    }

                    options = options with { Host = host };
                    break;

                case "--port":
                    if (!TryReadInt(args, ref index, value, arg, 0, 65535, out int port, out error))
                    {
                        return false;
                    }

                    options = options with { Port = port };
                    break;

                case "--external-server":
                    if (value != null)
                    {
                        error = "--external-server does not accept a value.";
                        return false;
                    }

                    options = options with { ExternalServer = true };
                    break;

                case "--use-control-server":
                    if (value != null)
                    {
                        error = "--use-control-server does not accept a value.";
                        return false;
                    }

                    options = options with { UseControlServer = true, ExternalServer = true };
                    break;

                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        return true;
    }

    private static bool TryReadString(
        string[] args,
        ref int index,
        string? inlineValue,
        string argName,
        out string value,
        out string error)
    {
        string? rawValue = inlineValue ?? ReadNext(args, ref index);
        if (!string.IsNullOrWhiteSpace(rawValue))
        {
            value = rawValue;
            error = string.Empty;
            return true;
        }

        value = string.Empty;
        error = $"{argName} requires a non-empty value.";
        return false;
    }

    private static bool TryReadInt(
        string[] args,
        ref int index,
        string? inlineValue,
        string argName,
        int minValue,
        int maxValue,
        out int value,
        out string error)
    {
        string? rawValue = inlineValue ?? ReadNext(args, ref index);
        if (int.TryParse(rawValue, out value) && value >= minValue && value <= maxValue)
        {
            error = string.Empty;
            return true;
        }

        error = $"{argName} requires an integer from {minValue} to {maxValue}.";
        return false;
    }

    private static string? ReadNext(string[] args, ref int index)
    {
        if (index + 1 >= args.Length)
        {
            return null;
        }

        index++;
        return args[index];
    }
}
