using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Diagnostics;
using System.IO;
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
            Security = new SocketSecurityConfig
            {
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = true,
                CertificateDirectory = "/tmp/socket-sample",
                CertificatePasswordEnvironmentVariable = "SOCKET_SAMPLE_PASSWORD",
                CertificateRenewBeforeDays = 7,
                RootCertificateLifetimeYears = 9,
                ModuleCertificateLifetimeYears = 4,
                AuthenticationTimeoutMilliseconds = 1500
            }
        };

        SampleClientSettings clone = settings.Clone();
        clone.Security.TlsProtocol = "Tls13";

        Assert.AreEqual(12, clone.ClientId);
        Assert.AreEqual("Auto", settings.Security.TlsProtocol);
        Assert.AreEqual("Tls13", clone.Security.TlsProtocol);
        Assert.IsTrue(clone.Security.RequireClientCertificate);
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
        Assert.IsTrue(await source.RegisterAsync());
        Assert.IsTrue(await target.RegisterAsync());

        Assert.IsTrue(await source.SendMessageAsync(302, "sample-message"));
        SocketCommon.Model.ClientMessageDelivery? delivery = await target.ReceiveMessageAsync();

        Assert.IsNotNull(delivery);
        Assert.AreEqual((uint)301, delivery.SourceClientId);
        Assert.AreEqual((uint)302, delivery.TargetClientId);
        Assert.AreEqual("sample-message", delivery.Content);
        Assert.AreEqual("302", target.GetState().ClientId.ToString());
        Assert.AreEqual("301: sample-message", target.GetState().LastReceivedMessage);
    }

    [TestMethod]
    public void NativeSampleProjectFilesAreIncludedTest()
    {
        string root = FindRepositoryRoot();

        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.iOS/SocketSampleiOS.xcodeproj/project.pbxproj")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.macOS/SocketSampleMac.xcodeproj/project.pbxproj")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/MainActivity.java")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/app/src/main/java/com/neodamul/socketsample/NativeSocketClient.java")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradlew")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradle/wrapper/gradle-wrapper.jar")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/gradle/wrapper/gradle-wrapper.properties")));
        Assert.IsTrue(File.Exists(Path.Combine(root, "Samples/SocketSample.Android/validate.sh")));
        Assert.IsFalse(File.Exists(Path.Combine(root, "Samples/SocketSample.Mobile/SocketSample.Mobile.csproj")));
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
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.AreEqual(0, process.ExitCode, output + error);
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
}
