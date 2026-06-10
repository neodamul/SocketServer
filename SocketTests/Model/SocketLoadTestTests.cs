using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketLoadTest;

namespace SocketTests.Model;

[TestClass]
public class SocketLoadTestTests
{
    [TestInitialize]
    public void Initialize()
    {
        SocketFactory.Configure(new SocketOperationConfig
        {
            ConnectTimeoutSeconds = 30,
            ReadTimeoutSeconds = 30,
            WriteTimeoutSeconds = 30
        });
        SecureSocketConnection.Configure(new SocketSecurityConfig
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            RequireClientCertificate = false,
            AuthenticationTimeoutMilliseconds = 30000
        });
    }

    [TestMethod]
    public void ParseMessageTestOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--clients", "20",
                "--start-client-id", "200",
                "--batch-size", "5",
                "--hold-seconds", "0",
                "--port", "0",
                "--message-test",
                "--message-rounds", "3",
                "--ramp-delay-ms", "5",
                "--expected-connected", "10",
                "--healthcheck-timeout-seconds", "3",
                "--message-timeout-seconds", "4"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(20, options.Clients);
        Assert.AreEqual(200, options.StartClientId);
        Assert.AreEqual(5, options.BatchSize);
        Assert.AreEqual(0, options.HoldSeconds);
        Assert.AreEqual(0, options.Port);
        Assert.IsTrue(options.MessageTest);
        Assert.AreEqual(3, options.MessageRounds);
        Assert.AreEqual(5, options.RampDelayMilliseconds);
        Assert.AreEqual(10, options.ExpectedConnected);
        Assert.AreEqual(3, options.HealthCheckTimeoutSeconds);
        Assert.AreEqual(4, options.MessageTimeoutSeconds);
    }

    [TestMethod]
    public void ParseMessageTestRejectsValueTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--message-test=true" },
            out _,
            out string error));

        Assert.AreEqual("--message-test does not accept a value.", error);
    }

    [TestMethod]
    public void ParseLoadTestProfileOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--profile", "soak-10k",
                "--hold-seconds", "1",
                "--report-file", "load-report.json"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual("soak-10k", options.Profile);
        Assert.AreEqual(10000, options.Clients);
        Assert.AreEqual(100, options.BatchSize);
        Assert.AreEqual(1, options.HoldSeconds);
        Assert.AreEqual(10000, options.ExpectedConnected);
        Assert.AreEqual("load-report.json", options.ReportFile);
    }

    [TestMethod]
    public void ParseLoadTestProfileRejectsUnknownProfileTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--profile", "unknown" },
            out _,
            out string error));

        Assert.AreEqual("Unknown profile: unknown", error);
    }

    [TestMethod]
    public void ParseLoadTestUiOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--ui",
                "--ui-port", "10060",
                "--clients", "4",
                "--batch-size", "4",
                "--host", "127.0.0.1",
                "--port", "10000",
                "--use-control-server"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.IsTrue(options.UiMode);
        Assert.AreEqual(10060, options.UiPort);
        Assert.AreEqual(4, options.Clients);
        Assert.AreEqual(4, options.BatchSize);
        Assert.AreEqual("127.0.0.1", options.Host);
        Assert.AreEqual(10000, options.Port);
        Assert.IsTrue(options.UseControlServer);
    }

    [TestMethod]
    public void LoadTestUiInitialStateExposesMetricsAndTargetsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[] { "--ui" },
            out LoadTestOptions options,
            out string error));
        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual(10060, options.UiPort);

        LoadTestUiService service = new(options);
        LoadTestUiState state = service.GetState();

        Assert.IsFalse(state.IsRunning);
        Assert.AreEqual("Idle", state.Status);
        Assert.AreEqual(0, state.ConnectedNow);
        Assert.AreEqual(0, state.TargetServers.Count);
        Assert.AreEqual(0, state.Clients.Count);
        Assert.AreEqual(0, state.Counters.Attempted);
    }

    [TestMethod]
    public void DefaultSecurityRequiresClientCertificateTest()
    {
        SocketSecurityConfig security = Program.CreateDefaultSecurityConfig();

        Assert.AreEqual("Tls", security.TransportMode);
        Assert.AreEqual("Auto", security.TlsProtocol);
        Assert.IsFalse(security.RequireTls13);
        Assert.IsTrue(security.RequireClientCertificate);
    }

    [TestMethod]
    public async Task LoadTestUiStopInvalidatesPendingRunTest()
    {
        using TcpListenerScope listener = new();
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--ui",
                "--clients", "20",
                "--batch-size", "20",
                "--host", "127.0.0.1",
                "--port", listener.Port.ToString(),
                "--external-server"
            },
            out LoadTestOptions options,
            out string error));
        Assert.AreEqual(string.Empty, error);

        LoadTestUiService service = new(options);
        await service.StartAsync(new LoadTestUiStartRequest());
        await Task.Delay(250);

        await service.StopAsync();
        LoadTestUiState stopped = service.GetState();

        Assert.IsFalse(stopped.IsRunning);
        Assert.AreEqual("Stopped", stopped.Status);

        await service.StartAsync(new LoadTestUiStartRequest
        {
            Clients = 1,
            BatchSize = 1,
            Host = "127.0.0.1",
            Port = 1,
            UseControlServer = false
        });

        LoadTestUiState restarted = service.GetState();
        Assert.IsTrue(restarted.IsRunning);

        await service.StopAsync();
    }

    [TestMethod]
    public async Task RunMessageLoadTestWithTwoClientsTest()
    {
        int exitCode = await Program.RunAsync(new[]
        {
            "--clients", "2",
            "--batch-size", "2",
            "--hold-seconds", "0",
            "--port", "0",
            "--message-test",
            "--message-rounds", "1"
        });

        Assert.AreEqual(0, exitCode);
    }

    [TestMethod]
    public async Task RunLoadTestWritesSummaryReportTest()
    {
        string reportFile = Path.Combine(Path.GetTempPath(), $"socket-load-report-{Guid.NewGuid():N}.json");
        try
        {
            int exitCode = await Program.RunAsync(new[]
            {
                "--clients", "1",
                "--start-client-id", "300",
                "--batch-size", "1",
                "--hold-seconds", "0",
                "--port", "0",
                "--report-file", reportFile
            });

            Assert.AreEqual(0, exitCode);
            Assert.IsTrue(File.Exists(reportFile));

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(reportFile));
            JsonElement root = document.RootElement;
            Assert.AreEqual("custom", root.GetProperty("Profile").GetString());
            Assert.AreEqual(1, root.GetProperty("Clients").GetInt32());
            Assert.AreEqual(300, root.GetProperty("StartClientId").GetInt32());
            Assert.AreEqual(1, root.GetProperty("Connected").GetInt32());
            Assert.AreEqual(1, root.GetProperty("HealthCheckSuccess").GetInt32());
        }
        finally
        {
            if (File.Exists(reportFile))
            {
                File.Delete(reportFile);
            }
        }
    }

    private sealed class TcpListenerScope : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cancellation = new();
        private readonly List<System.Net.Sockets.TcpClient> clients = new();
        private readonly Task acceptTask;

        public TcpListenerScope()
        {
            this.listener = new TcpListener(IPAddress.Loopback, 0);
            this.listener.Start();
            this.Port = ((IPEndPoint)this.listener.LocalEndpoint).Port;
            this.acceptTask = Task.Run(() => this.AcceptLoopAsync(this.cancellation.Token));
        }

        public int Port { get; }

        public void Dispose()
        {
            this.cancellation.Cancel();
            this.listener.Stop();
            lock (this.clients)
            {
                foreach (System.Net.Sockets.TcpClient client in this.clients)
                {
                    client.Dispose();
                }

                this.clients.Clear();
            }

            this.acceptTask.Wait(TimeSpan.FromSeconds(1));
            this.cancellation.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    System.Net.Sockets.TcpClient client = await this.listener.AcceptTcpClientAsync(cancellationToken);
                    lock (this.clients)
                    {
                        this.clients.Add(client);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (SocketException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
    }
}
