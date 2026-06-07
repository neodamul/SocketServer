using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
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
            Port = 5001,
            UseControlServer = false,
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

        Assert.IsTrue(await source.ConnectAsync());
        Assert.IsTrue(await target.ConnectAsync());
        Assert.IsTrue(source.GetState().IsRegistered);
        Assert.IsTrue(target.GetState().IsRegistered);

        Assert.IsTrue(await source.SendMessageAsync(302, "sample-message"));
        await WaitUntilAsync(() => target.GetState().LastReceivedMessage == "301: sample-message");

        Assert.AreEqual("302", target.GetState().ClientId.ToString());
        Assert.AreEqual("301: sample-message", target.GetState().LastReceivedMessage);
    }

    [TestMethod]
    public async Task SampleClientSessionStartsHealthCheckAfterRegisterTest()
    {
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

        Assert.IsTrue(await client.ConnectAsync());
        Assert.IsTrue(client.GetState().IsRegistered);

        await Task.Delay(TimeSpan.FromSeconds(4));

        SampleClientState state = client.GetState();
        Assert.IsTrue(state.IsConnected);
        Assert.IsTrue(state.IsRegistered);
        Assert.AreEqual(1, server.GetConnectedClientCount());
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
        Assert.IsTrue(config.Contains("auto-connect", StringComparison.Ordinal));
        Assert.IsTrue(config.Contains("target-client-id", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("SampleConfig.fromProcessArguments()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("config.autoConnect", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("startReceiveLoop()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("startHealthCheckLoop()", StringComparison.Ordinal));
        Assert.IsTrue(view.Contains("sendHealthCheck()", StringComparison.Ordinal));
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
        Assert.IsTrue(sampleSession.Contains("SocketClientSession", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AndroidNativeProtocolValidationScriptPassesTest()
    {
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
        Assert.IsTrue(sampleConfig.Contains("useControlServer", StringComparison.Ordinal));
        Assert.IsTrue(client.Contains("ProtoCodec.resolveRouteHost(route.host, config.host)", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("isLoopbackHost", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("\"localhost\".equals(value)", StringComparison.Ordinal));
        Assert.IsTrue(codec.Contains("\"::1\".equals(value)", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("receiveLoopGeneration", StringComparison.Ordinal));
        Assert.IsTrue(activity.Contains("isActiveReceiveLoop(generation)", StringComparison.Ordinal));
        Assert.IsFalse(activity.Contains("receiveLoopRunning", StringComparison.Ordinal));
        Assert.IsTrue(readme.Contains("the Android app replaces it with the original ControlServer host", StringComparison.Ordinal));
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
                AuthenticationTimeoutMilliseconds = 5000
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail("Condition was not satisfied in time.");
    }
}
