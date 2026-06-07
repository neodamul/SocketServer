using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketSample.Shared;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class SocketSampleClientTests
{
    [TestMethod]
    public void SampleClientSettingsCloneCopiesSecuritySettingsTest()
    {
        SampleClientSettings settings = new()
        {
            ClientId = 12,
            ClientName = "sample",
            Host = "127.0.0.1",
            Port = 25001,
            UseControlServer = false,
            ControlEndpoints =
            {
                new EndpointConfig { Host = "127.0.0.1", Port = 25001 },
                new EndpointConfig { Host = "127.0.0.1", Port = 25002 }
            },
            ReceiveTimeoutSeconds = 3,
            HealthCheckIntervalSeconds = 5,
            Security = new SocketSecurityConfig
            {
                Profile = "EdgeTerminated",
                TransportMode = "MessageEncryption",
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = true,
                CertificateDirectory = "/tmp/socket-sample",
                CertificatePasswordEnvironmentVariable = "SOCKET_SAMPLE_PASSWORD",
                CertificateRenewBeforeDays = 7,
                RootCertificateLifetimeYears = 9,
                ModuleCertificateLifetimeYears = 4,
                AuthenticationTimeoutMilliseconds = 1500,
                MessageEncryptionSecretEnvironmentVariable = "SOCKET_SAMPLE_MESSAGE_SECRET",
                TrustedNetwork = true
            }
        };

        SampleClientSettings clone = settings.Clone();
        clone.Security.TlsProtocol = "Tls13";

        Assert.AreEqual(12, clone.ClientId);
        Assert.AreEqual(5, clone.HealthCheckIntervalSeconds);
        Assert.AreEqual(2, clone.ControlEndpoints.Count);
        clone.ControlEndpoints[0].Host = "changed";
        Assert.AreEqual("127.0.0.1", settings.ControlEndpoints[0].Host);
        Assert.AreEqual("EdgeTerminated", clone.Security.Profile);
        Assert.AreEqual("MessageEncryption", clone.Security.TransportMode);
        Assert.AreEqual("Auto", settings.Security.TlsProtocol);
        Assert.AreEqual("Tls13", clone.Security.TlsProtocol);
        Assert.IsTrue(clone.Security.RequireClientCertificate);
        Assert.AreEqual("SOCKET_SAMPLE_MESSAGE_SECRET", clone.Security.MessageEncryptionSecretEnvironmentVariable);
        Assert.IsTrue(clone.Security.TrustedNetwork);
    }

    [TestMethod]
    public async Task SampleClientSessionSendsAndReceivesMessageTest()
    {
        using TestProgress progress = TestProgress.Start(nameof(SampleClientSessionSendsAndReceivesMessageTest));
        progress.Step("starting in-process socket server");
        using TcpServer server = new(
            30,
            "sample-server",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "sample-server");
        Assert.IsTrue(server.BindInPortRange(0, 0));
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(server.StartClientAcceptLoop());

        using SampleSocketClientSession source = new();
        using SampleSocketClientSession target = new();
        source.Configure(CreateSettings(301, server.GetPort()));
        target.Configure(CreateSettings(302, server.GetPort()));

        progress.Step("connecting and auto-registering sample clients");
        Assert.IsTrue(await source.ConnectAsync());
        Assert.IsTrue(await target.ConnectAsync());
        Assert.IsTrue(source.GetState().IsRegistered);
        Assert.IsTrue(target.GetState().IsRegistered);

        progress.Step("sending client-to-client sample message");
        Assert.IsTrue(await source.SendMessageAsync(302, "sample-message"));
        await WaitUntilAsync(
            "target sample client receives source message",
            () => target.GetState().LastReceivedMessage == "301: sample-message",
            progress);

        Assert.AreEqual("302", target.GetState().ClientId.ToString());
        Assert.AreEqual("301: sample-message", target.GetState().LastReceivedMessage);
    }

    [TestMethod]
    public async Task SampleClientSessionStartsHealthCheckAfterRegisterTest()
    {
        using TestProgress progress = TestProgress.Start(nameof(SampleClientSessionStartsHealthCheckAfterRegisterTest));
        progress.Step("starting idle-timeout socket server");
        using TcpServer server = new(
            31,
            "sample-health-server",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(2),
            instanceId: "sample-health-server");
        Assert.IsTrue(server.BindInPortRange(0, 0));
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(server.StartClientAcceptLoop());

        using SampleSocketClientSession client = new();
        SampleClientSettings settings = CreateSettings(303, server.GetPort());
        settings.HealthCheckIntervalSeconds = 1;
        client.Configure(settings);

        progress.Step("connecting sample client and verifying registration");
        Assert.IsTrue(await client.ConnectAsync());
        Assert.IsTrue(client.GetState().IsRegistered);

        await progress.WaitAsync(
            "healthcheck retention window",
            Task.Delay(TimeSpan.FromSeconds(4)),
            TimeSpan.FromSeconds(5));

        SampleClientState state = client.GetState();
        Assert.IsTrue(state.IsConnected);
        Assert.IsTrue(state.IsRegistered);
        Assert.AreEqual(1, server.GetConnectedClientCount());
    }

    [TestMethod]
    public async Task SampleClientSessionFallsBackAcrossControlEndpointsTest()
    {
        using TestProgress progress = TestProgress.Start(nameof(SampleClientSessionFallsBackAcrossControlEndpointsTest));
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "sample-control-fallback",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer server = new(
            32,
            "sample-control-server",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "sample-control-server");
        Assert.IsTrue(server.BindInPortRange(0, 0));
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(server.StartClientAcceptLoop());

        await SendServerHeartbeatAsync(
            controlServer,
            CreateServerHeartbeat(32, "sample-control-server", server.GetPort()));
        await WaitUntilAsync(
            "sample ControlServer sees SocketServer",
            () => controlServer.GetClusterStatus().ServerCount == 1,
            progress);

        using SampleSocketClientSession client = new();
        SampleClientSettings settings = CreateSettings(304, controlServer.Port);
        settings.UseControlServer = true;
        settings.ControlEndpoints.Add(new EndpointConfig { Host = "bad-control.invalid", Port = 10000 });
        settings.ControlEndpoints.Add(new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port });
        client.Configure(settings);

        Assert.IsTrue(await client.ConnectAsync());
        SampleClientState state = client.GetState();
        Assert.IsTrue(state.IsRegistered);
        Assert.AreEqual($"127.0.0.1:{server.GetPort()}", state.ConnectedServer);
    }

    [TestMethod]
    public void NativeSampleProjectFilesAreIncludedTest()
    {
        string root = FindRepositoryRoot();

        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj/project.pbxproj")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.macOS/SocketSampleMac.xcodeproj/project.pbxproj")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.AppleShared/SocketMessageProtector.swift")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/MainActivity.java")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/NativeSocketClient.java")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/SocketMessageProtector.java")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradlew")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradle/wrapper/gradle-wrapper.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradle/wrapper/gradle-wrapper.properties")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/validate.sh")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "Samples/SocketSample.Mobile/SocketSample.Mobile.csproj")));
    }

    [TestMethod]
    public void AppleNativeSampleAcceptsLaunchArgumentsTest()
    {
        string root = FindRepositoryRoot();
        string config = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.AppleShared/SampleConfig.swift"));
        string view = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.AppleShared/SampleContentView.swift"));
        string readme = File.ReadAllText(Path.Combine(root, "Samples/README.md"));

        Assert.IsTrue(config.Contains("fromProcessArguments", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("targetClientIdFromProcessArguments", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("client-id", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("use-control-server", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("control-endpoints", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("parseControlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("effectiveControlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("auto-connect", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("target-client-id", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("SampleConfig.fromProcessArguments()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("config.autoConnect", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("Control Endpoints", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("startReceiveLoop()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("startHealthCheckLoop()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("sendHealthCheck()", StringComparison.Ordinal));
        Assert.IsTrue(File.ReadAllText(Path.Combine(root, "Samples/SocketSample.AppleShared/NativeSocketClient.swift"))
            .Contains("resolveRouteHost(route.host, controlHost: controlEndpoint.host)", StringComparison.Ordinal));
        Assert.IsFalse(view.Contains("Button(\"Register\")", StringComparison.Ordinal));
        Assert.IsFalse(view.Contains("Button(\"Receive\")", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("-derivedDataPath Samples/SocketSample.macOS/build", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("open -n Samples/SocketSample.macOS/build/Build/Products/Debug/SocketSampleMac.app --args --client-id 101", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("--use-control-server true --auto-connect true", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("--target-client-id 101", StringComparison.Ordinal));
    }

    [TestMethod]
    public void DotNetSampleWebUiUsesDynamicPortByDefaultTest()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Net/Program.cs"));
        string clientSession = File.ReadAllText(Path.Combine(root, "SocketClient/Model/SocketClientSession.cs"));
        string sampleSession = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Shared/SampleSocketClientSession.cs"));

        Assert.IsTrue(program.Contains("http://127.0.0.1:0", StringComparison.Ordinal));
        Assert.IsFalse(program.Contains("http://127.0.0.1:5090", StringComparison.Ordinal));
        Assert.IsFalse(program.Contains("registerClient()", StringComparison.Ordinal));
        Assert.IsFalse(program.Contains("receiveMessage()", StringComparison.Ordinal));
        Assert.IsFalse(program.Contains("/api/receive", StringComparison.Ordinal));
        Assert.IsTrue(clientSession.Contains("ConnectAndRegisterAsync", StringComparison.Ordinal));
        Assert.IsTrue(clientSession.Contains("StartReceiveLoop()", StringComparison.Ordinal));
        Assert.IsTrue(clientSession.Contains("ConnectedEndpoint", StringComparison.Ordinal));
        Assert.IsTrue(sampleSession.Contains("controlEndpoints.Length > 0", StringComparison.Ordinal));
        Assert.IsTrue(sampleSession.Contains("ResolveControlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(sampleSession.Contains("catch (SocketException)", StringComparison.Ordinal));
        Assert.IsTrue(sampleSession.Contains("ConnectedServer", StringComparison.Ordinal));
        Assert.IsTrue(sampleSession.Contains("SocketClientSession", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("controlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("parseEndpoints", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AndroidNativeProtocolValidationScriptPassesTest()
    {
        using TestProgress progress = TestProgress.Start(nameof(AndroidNativeProtocolValidationScriptPassesTest));
        string root = FindRepositoryRoot();
        string sampleRoot = Path.Combine(root, "Samples/SocketSample.Android");
        string scriptPath = Path.Combine(sampleRoot, "validate.sh");

        if (!CommandExists("javac"))
        {
            Assert.Inconclusive("javac is required to validate Android native protocol sources.");
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { scriptPath, "--protocol-only" },
                WorkingDirectory = sampleRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };

        progress.Step("starting Android protocol validation script");
        process.Start();
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(timeout.Token),
                outputTask.WaitAsync(timeout.Token),
                errorTask.WaitAsync(timeout.Token));
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            Assert.Fail("Android native protocol validation script timed out after 30 seconds.");
        }

        string output = outputTask.Result;
        string error = errorTask.Result;

        Assert.AreEqual(0, process.ExitCode, output + error);
    }

    [TestMethod]
    public void TestProgressWritesTimestampedStepLogsTest()
    {
        using StringWriter writer = new();
        using (TestProgress progress = TestProgress.Start("progress-format", writer))
        {
            progress.Step("example stage");
        }

        string log = writer.ToString();
        Assert.IsTrue(log.Contains("[test-progress]", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("progress-format START", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("progress-format STEP 1", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("example stage", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("total=", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("since-last=", StringComparison.Ordinal));
        Assert.IsTrue(log.Contains("progress-format END", StringComparison.Ordinal));
    }

    [TestMethod]
    public void AndroidNativeSampleUsesControlRouteHostFallbackAndReceiveLoopGenerationTest()
    {
        string root = FindRepositoryRoot();
        string config = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/res/raw/config.json"));
        string sampleConfig = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/SampleConfig.java"));
        string client = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/NativeSocketClient.java"));
        string codec = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/ProtoCodec.java"));
        string activity = File.ReadAllText(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/MainActivity.java"));
        string readme = File.ReadAllText(Path.Combine(root, "Samples/README.md"));

        Assert.IsTrue(config.Contains("\"useControlServer\": true", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("\"controlEndpoints\"", StringComparison.Ordinal));
        Assert.IsTrue(sampleConfig.Contains("useControlServer", StringComparison.Ordinal));
        Assert.IsTrue(sampleConfig.Contains("effectiveControlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(sampleConfig.Contains("parseControlEndpoints", StringComparison.Ordinal));
        Assert.IsTrue(client.Contains("config.effectiveControlEndpoints()", StringComparison.Ordinal));
        Assert.IsTrue(client.Contains("ProtoCodec.resolveRouteHost(route.host, endpoint.host)", StringComparison.Ordinal));
        Assert.IsTrue(client.Contains("connectedServer()", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("isLoopbackHost", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("\"localhost\".equals(value)", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("\"::1\".equals(value)", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("receiveLoopGeneration", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("isActiveReceiveLoop(generation)", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("Control Endpoints", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("Connected Server", StringComparison.Ordinal));
        Assert.IsFalse(activity.Contains("receiveLoopRunning", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("the Android app replaces it with the ControlServer host that returned the route", StringComparison.Ordinal));
    }

    private static SampleClientSettings CreateSettings(int clientId, int port)
    {
        return new SampleClientSettings
        {
            ClientId = clientId,
            ClientName = $"sample-client-{clientId}",
            Host = "127.0.0.1",
            Port = port,
            UseControlServer = false,
            ReceiveTimeoutSeconds = 3,
            Security = new SocketSecurityConfig
            {
                TlsProtocol = "Auto",
                RequireTls13 = false,
                AuthenticationTimeoutMilliseconds = 30000
            }
        };
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SocketServer.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static bool CommandExists(string command)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return true;
            }
        }

        return false;
    }

    private static ServerHeartbeatRequest CreateServerHeartbeat(
        int serverId,
        string instanceId,
        int port)
    {
        return new ServerHeartbeatRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = serverId,
            InstanceId = instanceId,
            Host = "127.0.0.1",
            Port = port,
            MaxConnections = 10,
            CurrentConnections = 0,
            ResourceUsage = new ResourceUsageSnapshot
            {
                CpuUsagePercent = 1,
                MemoryUsagePercent = 1,
                StorageUsagePercent = 1
            },
            SentAt = DateTimeOffset.UtcNow
        };
    }

    private static async Task SendServerHeartbeatAsync(
        ControlServer controlServer,
        ServerHeartbeatRequest request)
    {
        using Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        await SocketFactory.ConnectAsync(socket, IPAddress.Loopback, controlServer.Port);
        using SecureSocketConnection connection =
            await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketServer");

        (bool success, _) = await ControlProtocol.SendAndReceiveAsync(
            connection,
            0,
            ControlMessageIds.ServerHeartbeat,
            request,
            timeoutMilliseconds: 5000);
        Assert.IsTrue(success);
    }

    private static async Task WaitUntilAsync(string operation, Func<bool> condition, TestProgress? progress = null)
    {
        progress?.Step($"{operation} waiting");
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                progress?.Step($"{operation} satisfied");
                return;
            }

            await Task.Delay(100);
        }

        progress?.Step($"{operation} timed out");
        Assert.Fail($"{operation} was not satisfied in time.");
    }
}
