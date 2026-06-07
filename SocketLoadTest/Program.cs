using System.Diagnostics;
using System.Text.Json;
using SocketClient.Model;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketLoadTest;

internal static class Program
{
    private const int HealthCheckTimeoutSeconds = 10;
    private const int MessageTimeoutSeconds = 10;

    private static Task<int> Main(string[] args)
    {
        return RunAsync(args);
    }

    internal static async Task<int> RunAsync(string[] args)
    {
        if (!LoadTestOptions.TryParse(args, out LoadTestOptions options, out string error))
        {
            Console.Error.WriteLine(error);
            PrintUsage();
            return 1;
        }

        LogConfigurator.Configure(ResolveLogConfigFileName());
        SocketFactory.Configure(new SocketOperationConfig());
        SecureSocketConnection.Configure(CreateDefaultSecurityConfig());
        if (options.UiMode)
        {
            await LoadTestUiHost.RunAsync(options);
            return 0;
        }

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
            $"Starting load test: clients={options.Clients}, batch-size={options.BatchSize}, hold-seconds={options.HoldSeconds}, endpoint={options.Host}:{options.Port}, external-server={options.ExternalServer}, use-control-server={options.UseControlServer}, message-test={options.MessageTest}, message-rounds={options.MessageRounds}, ramp-delay-ms={options.RampDelayMilliseconds}, expected-connected={options.ExpectedConnected}");

        Stopwatch stopwatch = Stopwatch.StartNew();
        LoadTestCounters counters = new();
        List<ConnectedLoadClient> connectedClients = new(options.Clients);

        try
        {
            int lastClientId = options.StartClientId + options.Clients - 1;
            for (int firstClientId = options.StartClientId; firstClientId <= lastClientId; firstClientId += options.BatchSize)
            {
                int batchCount = Math.Min(options.BatchSize, lastClientId - firstClientId + 1);
                Stopwatch batchStopwatch = Stopwatch.StartNew();
                PrintDebug(
                    $"batch start first-client-id={firstClientId}, count={batchCount}, " +
                    $"elapsed={stopwatch.Elapsed}");
                ClientAttemptResult[] results = await ConnectBatchAsync(options, firstClientId, batchCount, counters);
                batchStopwatch.Stop();

                foreach (ClientAttemptResult result in results)
                {
                    if (result.Client != null)
                    {
                        connectedClients.Add(new ConnectedLoadClient(result.ClientId, result.Client));
                    }
                }

                PrintDebug(
                    $"batch complete first-client-id={firstClientId}, count={batchCount}, " +
                    $"connected-clients={connectedClients.Count}, batch-elapsed={batchStopwatch.Elapsed}, " +
                    $"total-elapsed={stopwatch.Elapsed}");
                PrintProgress(counters, stopwatch.Elapsed);
                if (options.RampDelayMilliseconds > 0)
                {
                    PrintDebug($"ramp delay start delay-ms={options.RampDelayMilliseconds}");
                    await Task.Delay(options.RampDelayMilliseconds);
                    PrintDebug($"ramp delay complete delay-ms={options.RampDelayMilliseconds}");
                }
            }

            if (options.MessageTest)
            {
                PrintDebug("message test stage start");
                await RunMessageTestAsync(options, connectedClients, counters);
                PrintDebug("message test stage complete");
                PrintProgress(counters, stopwatch.Elapsed);
            }

            if (options.HoldSeconds > 0)
            {
                foreach (ConnectedLoadClient client in connectedClients)
                {
                    client.Client.StartHealthCheckLoop();
                }

                Console.WriteLine($"Holding {connectedClients.Count} connected clients for {options.HoldSeconds} seconds.");
                PrintDebug($"hold stage start connected-clients={connectedClients.Count}, seconds={options.HoldSeconds}");
                await Task.Delay(TimeSpan.FromSeconds(options.HoldSeconds));
                PrintDebug($"hold stage complete connected-clients={connectedClients.Count}, seconds={options.HoldSeconds}");
            }
        }
        finally
        {
            foreach (ConnectedLoadClient client in connectedClients)
            {
                client.Client.Dispose();
            }

            server?.End();
        }

        stopwatch.Stop();
        PrintSummary(counters, stopwatch.Elapsed);
        WriteReport(options, counters, stopwatch.Elapsed);
        if (options.ExpectedConnected > 0 && counters.Connected < options.ExpectedConnected)
        {
            Console.Error.WriteLine($"Expected at least {options.ExpectedConnected} connected clients, but {counters.Connected} connected.");
            return 2;
        }

        return counters.HealthCheckFail == 0 &&
            counters.ConnectFail == 0 &&
            counters.RegisterFail == 0 &&
            counters.MessageFail == 0
                ? 0
                : 2;
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
                client.Dispose();
                return ClientAttemptResult.Failed;
            }

            Interlocked.Increment(ref counters.Connected);
            (bool registerReceived, SocketCommon.Model.ClientRegisterAck registerAck) =
                await client.RegisterClientWithAckAsync();
            if (!registerReceived || !registerAck.Success)
            {
                Interlocked.Increment(ref counters.RegisterFail);
                client.Dispose();
                return ClientAttemptResult.Failed;
            }

            bool healthCheckSucceeded = await SendAndReceiveHealthCheckAsync(client, options.HealthCheckTimeoutSeconds);
            if (healthCheckSucceeded)
            {
                Interlocked.Increment(ref counters.HealthCheckSuccess);
                if (!options.MessageTest && options.HoldSeconds > 0)
                {
                    client.StartHealthCheckLoop();
                }

                return new ClientAttemptResult(clientId, client);
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

    private static async Task RunMessageTestAsync(
        LoadTestOptions options,
        IReadOnlyList<ConnectedLoadClient> clients,
        LoadTestCounters counters)
    {
        if (clients.Count < 2)
        {
            Interlocked.Increment(ref counters.MessageFail);
            Console.Error.WriteLine("Message test requires at least two connected clients.");
            return;
        }

        int pairCount = clients.Count / 2;
        Console.WriteLine($"Starting message test: pairs={pairCount}, rounds={options.MessageRounds}.");
        for (int round = 1; round <= options.MessageRounds; round++)
        {
            Stopwatch roundStopwatch = Stopwatch.StartNew();
            PrintDebug($"message round start round={round}, pairs={pairCount}");
            Task[] tasks = new Task[pairCount];
            for (int pairIndex = 0; pairIndex < pairCount; pairIndex++)
            {
                ConnectedLoadClient source = clients[pairIndex * 2];
                ConnectedLoadClient target = clients[(pairIndex * 2) + 1];
                tasks[pairIndex] = RunMessagePairAsync(options, round, source, target, counters);
            }

            await Task.WhenAll(tasks);
            roundStopwatch.Stop();
            PrintDebug($"message round complete round={round}, elapsed={roundStopwatch.Elapsed}");
        }
    }

    private static async Task RunMessagePairAsync(
        LoadTestOptions options,
        int round,
        ConnectedLoadClient source,
        ConnectedLoadClient target,
        LoadTestCounters counters)
    {
        string content = $"load-message-{round}-{source.ClientId}-to-{target.ClientId}";
        Interlocked.Increment(ref counters.MessageAttempted);

        Task<(bool Success, SocketCommon.Model.ClientMessageDelivery Delivery)> receiveTask =
            target.Client.TryReceiveClientMessageAsync();
        Task<(bool Success, SocketCommon.Model.ClientMessageAck Ack, SocketCommon.Model.ClientMessageError Error)> sendTask =
            source.Client.SendClientMessageAsync((uint)target.ClientId, content);

        Task completedReceiveTask = await Task.WhenAny(
            receiveTask,
            Task.Delay(TimeSpan.FromSeconds(options.MessageTimeoutSeconds)));
        Task completedSendTask = await Task.WhenAny(
            sendTask,
            Task.Delay(TimeSpan.FromSeconds(options.MessageTimeoutSeconds)));

        if (completedReceiveTask != receiveTask || completedSendTask != sendTask)
        {
            Interlocked.Increment(ref counters.MessageFail);
            PrintDebug(
                $"message pair timeout round={round}, source={source.ClientId}, target={target.ClientId}, " +
                $"receive-complete={completedReceiveTask == receiveTask}, send-complete={completedSendTask == sendTask}, " +
                $"timeout-seconds={options.MessageTimeoutSeconds}");
            return;
        }

        (bool deliverySuccess, SocketCommon.Model.ClientMessageDelivery delivery) = await receiveTask;
        (bool ackSuccess, SocketCommon.Model.ClientMessageAck ack, SocketCommon.Model.ClientMessageError error) = await sendTask;

        if (!deliverySuccess ||
            !ackSuccess ||
            delivery.SourceClientId != (uint)source.ClientId ||
            delivery.TargetClientId != (uint)target.ClientId ||
            delivery.Content != content ||
            ack.TargetClientId != (uint)target.ClientId ||
            error != null)
        {
            Interlocked.Increment(ref counters.MessageFail);
            PrintDebug(
                $"message pair failed round={round}, source={source.ClientId}, target={target.ClientId}, " +
                $"delivery-success={deliverySuccess}, ack-success={ackSuccess}, ack-target={ack?.TargetClientId}");
            return;
        }

        Interlocked.Increment(ref counters.MessageSuccess);
    }

    private static async Task<bool> SendAndReceiveHealthCheckAsync(TcpClient client, int timeoutSeconds = HealthCheckTimeoutSeconds)
    {
        if (!await client.SendHealthCheckAsync())
        {
            return false;
        }

        Task<(bool Success, SocketCommon.Model.HealthCheckMessage Message)> receiveTask =
            client.TryReceiveHealthCheckAsync();
        Task completedTask = await Task.WhenAny(
            receiveTask,
            Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)));

        if (completedTask != receiveTask)
        {
            PrintDebug($"healthcheck timeout timeout-seconds={timeoutSeconds}");
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

    internal static SocketSecurityConfig CreateDefaultSecurityConfig()
    {
        return new SocketSecurityConfig
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            RequireClientCertificate = true,
            AuthenticationTimeoutMilliseconds = 30000
        };
    }

    private static void PrintProgress(LoadTestCounters counters, TimeSpan elapsed)
    {
        Console.WriteLine(
            $"Progress: attempted={counters.Attempted}, connected={counters.Connected}, healthcheck-success={counters.HealthCheckSuccess}, healthcheck-fail={counters.HealthCheckFail}, registered-fail={counters.RegisterFail}, message-success={counters.MessageSuccess}, message-fail={counters.MessageFail}, elapsed={elapsed}");
    }

    private static void PrintDebug(string message)
    {
        Console.WriteLine($"[load-test-debug] {DateTimeOffset.UtcNow:O} {message}");
    }

    private static void PrintSummary(LoadTestCounters counters, TimeSpan elapsed)
    {
        Console.WriteLine("Load test complete.");
        Console.WriteLine($"Attempted: {counters.Attempted}");
        Console.WriteLine($"Connected: {counters.Connected}");
        Console.WriteLine($"Connect failed: {counters.ConnectFail}");
        Console.WriteLine($"Healthcheck succeeded: {counters.HealthCheckSuccess}");
        Console.WriteLine($"Healthcheck failed: {counters.HealthCheckFail}");
        Console.WriteLine($"Register failed: {counters.RegisterFail}");
        Console.WriteLine($"Message attempted: {counters.MessageAttempted}");
        Console.WriteLine($"Message succeeded: {counters.MessageSuccess}");
        Console.WriteLine($"Message failed: {counters.MessageFail}");
        Console.WriteLine($"Elapsed: {elapsed}");
    }

    private static void WriteReport(LoadTestOptions options, LoadTestCounters counters, TimeSpan elapsed)
    {
        if (string.IsNullOrWhiteSpace(options.ReportFile))
        {
            return;
        }

        string? directory = Path.GetDirectoryName(options.ReportFile);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        LoadTestReport report = new()
        {
            Profile = options.Profile,
            Clients = options.Clients,
            BatchSize = options.BatchSize,
            HoldSeconds = options.HoldSeconds,
            Host = options.Host,
            Port = options.Port,
            ExternalServer = options.ExternalServer,
            UseControlServer = options.UseControlServer,
            MessageTest = options.MessageTest,
            MessageRounds = options.MessageRounds,
            RampDelayMilliseconds = options.RampDelayMilliseconds,
            ExpectedConnected = options.ExpectedConnected,
            StartClientId = options.StartClientId,
            Attempted = counters.Attempted,
            Connected = counters.Connected,
            ConnectFail = counters.ConnectFail,
            HealthCheckSuccess = counters.HealthCheckSuccess,
            HealthCheckFail = counters.HealthCheckFail,
            RegisterFail = counters.RegisterFail,
            MessageAttempted = counters.MessageAttempted,
            MessageSuccess = counters.MessageSuccess,
            MessageFail = counters.MessageFail,
            ElapsedMilliseconds = elapsed.TotalMilliseconds,
            CompletedAt = DateTimeOffset.UtcNow
        };

        File.WriteAllText(
            options.ReportFile,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report written: {options.ReportFile}");
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            "Usage: dotnet run --project SocketLoadTest -- [--ui] [--ui-port N] [--profile smoke|soak-1k|soak-10k|soak-50k|message-1k] [--clients N] [--start-client-id N] [--batch-size N] [--hold-seconds N] [--host IP] [--port N] [--external-server] [--use-control-server] [--message-test] [--message-rounds N] [--ramp-delay-ms N] [--expected-connected N] [--healthcheck-timeout-seconds N] [--message-timeout-seconds N] [--report-file PATH]");
    }
}

internal sealed class LoadTestCounters
{
    public int Attempted;
    public int Connected;
    public int ConnectFail;
    public int HealthCheckSuccess;
    public int HealthCheckFail;
    public int RegisterFail;
    public int MessageAttempted;
    public int MessageSuccess;
    public int MessageFail;
}

internal sealed record ClientAttemptResult(int ClientId, TcpClient? Client)
{
    public static readonly ClientAttemptResult Failed = new(0, (TcpClient?)null);
}

internal sealed record ConnectedLoadClient(int ClientId, TcpClient Client);

internal sealed record LoadTestOptions(
    int Clients,
    int BatchSize,
    int HoldSeconds,
    string Host,
    int Port,
    bool ExternalServer,
    bool UseControlServer,
    bool MessageTest,
    int MessageRounds,
    int RampDelayMilliseconds,
    int StartClientId,
    int ExpectedConnected,
    int HealthCheckTimeoutSeconds,
    int MessageTimeoutSeconds,
    string Profile,
    string ReportFile,
    bool UiMode,
    int UiPort)
{
    public static bool TryParse(string[] args, out LoadTestOptions options, out string error)
    {
        options = new LoadTestOptions(
            Clients: 10000,
            BatchSize: 100,
            HoldSeconds: 60,
            Host: "127.0.0.1",
            Port: 10000,
            ExternalServer: false,
            UseControlServer: false,
            MessageTest: false,
            MessageRounds: 1,
            RampDelayMilliseconds: 0,
            StartClientId: 1,
            ExpectedConnected: 0,
            HealthCheckTimeoutSeconds: 10,
            MessageTimeoutSeconds: 10,
            Profile: "custom",
            ReportFile: "",
            UiMode: false,
            UiPort: 10060);
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
                case "--profile":
                    if (!TryReadString(args, ref index, value, arg, out string profile, out error))
                    {
                        return false;
                    }

                    if (!TryCreateProfile(profile, options, out options, out error))
                    {
                        return false;
                    }

                    break;

                case "--clients":
                    if (!TryReadInt(args, ref index, value, arg, 0, int.MaxValue, out int clients, out error))
                    {
                        return false;
                    }

                    options = options with { Clients = clients };
                    break;

                case "--start-client-id":
                    if (!TryReadInt(args, ref index, value, arg, 1, int.MaxValue, out int startClientId, out error))
                    {
                        return false;
                    }

                    options = options with { StartClientId = startClientId };
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

                case "--message-test":
                    if (value != null)
                    {
                        error = "--message-test does not accept a value.";
                        return false;
                    }

                    options = options with { MessageTest = true };
                    break;

                case "--message-rounds":
                    if (!TryReadInt(args, ref index, value, arg, 1, int.MaxValue, out int messageRounds, out error))
                    {
                        return false;
                    }

                    options = options with { MessageRounds = messageRounds };
                    break;

                case "--ramp-delay-ms":
                    if (!TryReadInt(args, ref index, value, arg, 0, int.MaxValue, out int rampDelayMilliseconds, out error))
                    {
                        return false;
                    }

                    options = options with { RampDelayMilliseconds = rampDelayMilliseconds };
                    break;

                case "--expected-connected":
                    if (!TryReadInt(args, ref index, value, arg, 0, int.MaxValue, out int expectedConnected, out error))
                    {
                        return false;
                    }

                    options = options with { ExpectedConnected = expectedConnected };
                    break;

                case "--healthcheck-timeout-seconds":
                    if (!TryReadInt(args, ref index, value, arg, 1, int.MaxValue, out int healthCheckTimeoutSeconds, out error))
                    {
                        return false;
                    }

                    options = options with { HealthCheckTimeoutSeconds = healthCheckTimeoutSeconds };
                    break;

                case "--message-timeout-seconds":
                    if (!TryReadInt(args, ref index, value, arg, 1, int.MaxValue, out int messageTimeoutSeconds, out error))
                    {
                        return false;
                    }

                    options = options with { MessageTimeoutSeconds = messageTimeoutSeconds };
                    break;

                case "--report-file":
                    if (!TryReadString(args, ref index, value, arg, out string reportFile, out error))
                    {
                        return false;
                    }

                    options = options with { ReportFile = reportFile };
                    break;

                case "--ui":
                    if (value != null)
                    {
                        error = "--ui does not accept a value.";
                        return false;
                    }

                    options = options with { UiMode = true };
                    break;

                case "--ui-port":
                    if (!TryReadInt(args, ref index, value, arg, 0, 65535, out int uiPort, out error))
                    {
                        return false;
                    }

                    options = options with { UiPort = uiPort };
                    break;

                default:
                    error = $"Unknown argument: {arg}";
                    return false;
            }
        }

        return true;
    }

    private static bool TryCreateProfile(
        string profile,
        LoadTestOptions current,
        out LoadTestOptions options,
        out string error)
    {
        options = profile.ToLowerInvariant() switch
        {
            "smoke" => current with
            {
                Profile = "smoke",
                Clients = 100,
                BatchSize = 100,
                HoldSeconds = 10,
                ExpectedConnected = 100
            },
            "soak-1k" => current with
            {
                Profile = "soak-1k",
                Clients = 1000,
                BatchSize = 100,
                HoldSeconds = 300,
                ExpectedConnected = 1000
            },
            "soak-10k" => current with
            {
                Profile = "soak-10k",
                Clients = 10000,
                BatchSize = 100,
                HoldSeconds = 600,
                ExpectedConnected = 10000
            },
            "soak-50k" => current with
            {
                Profile = "soak-50k",
                Clients = 50000,
                BatchSize = 100,
                HoldSeconds = 900,
                ExpectedConnected = 50000
            },
            "message-1k" => current with
            {
                Profile = "message-1k",
                Clients = 1000,
                BatchSize = 100,
                HoldSeconds = 0,
                MessageTest = true,
                MessageRounds = 1,
                ExpectedConnected = 1000
            },
            _ => current
        };

        if (ReferenceEquals(options, current) && !string.Equals(profile, "custom", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown profile: {profile}";
            return false;
        }

        error = string.Empty;
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

internal sealed class LoadTestReport
{
    public string Profile { get; init; } = "";

    public int Clients { get; init; }

    public int BatchSize { get; init; }

    public int HoldSeconds { get; init; }

    public string Host { get; init; } = "";

    public int Port { get; init; }

    public bool ExternalServer { get; init; }

    public bool UseControlServer { get; init; }

    public bool MessageTest { get; init; }

    public int MessageRounds { get; init; }

    public int RampDelayMilliseconds { get; init; }

    public int ExpectedConnected { get; init; }

    public int StartClientId { get; init; }

    public int Attempted { get; init; }

    public int Connected { get; init; }

    public int ConnectFail { get; init; }

    public int HealthCheckSuccess { get; init; }

    public int HealthCheckFail { get; init; }

    public int RegisterFail { get; init; }

    public int MessageAttempted { get; init; }

    public int MessageSuccess { get; init; }

    public int MessageFail { get; init; }

    public double ElapsedMilliseconds { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}
