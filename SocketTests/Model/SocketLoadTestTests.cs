using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketLoadTest;
using SocketServer.Model;

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
    public void ParseSourceIpOptionsTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--source-ips", "127.0.0.1"
            },
            out LoadTestOptions options,
            out string error));

        Assert.AreEqual(string.Empty, error);
        Assert.AreEqual("127.0.0.1", options.SourceIps);
        CollectionAssert.AreEqual(
            new[] { IPAddress.Parse("127.0.0.1") },
            LoadTestOptions.ParseSourceIpAddresses(options.SourceIps));
    }

    [TestMethod]
    public void ParseSourceIpOptionsRejectsInvalidAddressTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--source-ips", "127.0.0.1,not-an-ip" },
            out _,
            out string error));

        Assert.AreEqual("--source-ips contains an invalid IP address: not-an-ip", error);
    }

    [TestMethod]
    public void ParseSourceIpOptionsRejectsIpv6AddressTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--source-ips", "::1" },
            out _,
            out string error));

        Assert.AreEqual("--source-ips supports IPv4 addresses only: ::1", error);
    }

    [TestMethod]
    public void ParseSourceIpOptionsRejectsUnboundAddressTest()
    {
        Assert.IsFalse(LoadTestOptions.TryParse(
            new[] { "--source-ips", "127.0.0.2" },
            out _,
            out string error));

        Assert.AreEqual("--source-ips contains an address that cannot be bound on this host: 127.0.0.2", error);
    }

    [TestMethod]
    public void SelectSourceIpAddressUsesClientIdRoundRobinTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--start-client-id", "10"
            },
            out LoadTestOptions options,
            out string error));
        Assert.AreEqual(string.Empty, error);

        IPAddress[] sourceIps =
        {
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("127.0.0.2")
        };

        Assert.AreEqual(IPAddress.Parse("127.0.0.1"), Program.SelectSourceIpAddress(options, sourceIps, 10));
        Assert.AreEqual(IPAddress.Parse("127.0.0.2"), Program.SelectSourceIpAddress(options, sourceIps, 11));
        Assert.AreEqual(IPAddress.Parse("127.0.0.1"), Program.SelectSourceIpAddress(options, sourceIps, 12));
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
        Assert.AreEqual(0, state.CertificateWarmupTotal);
        Assert.AreEqual(0, state.CertificateWarmupCompleted);
    }

    [TestMethod]
    public async Task LoadTestCertificateWarmupCreatesSharedClientCertificateByDefaultTest()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"socket-loadtest-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int maxProgress = 0;

        try
        {
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                TransportMode = "Tls",
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = true,
                CertificateDirectory = directory,
                AuthenticationTimeoutMilliseconds = 30000
            });

            Assert.IsTrue(LoadTestCertificateWarmup.IsRequired);
            Assert.AreEqual("SocketClient", LoadTestCertificateWarmup.GetClientCertificateModuleName(1201));
            Assert.AreEqual(1, LoadTestCertificateWarmup.GetRequiredCertificateCount(new[] { 1201, 1202, 1201 }));

            LoadTestCertificateWarmupResult result = await LoadTestCertificateWarmup.WarmUpAsync(
                new[] { 1201, 1202, 1201 },
                maxConcurrency: 2,
                onCompleted: completed => UpdateMax(ref maxProgress, completed),
                CancellationToken.None);

            Assert.AreEqual(1, result.Total);
            Assert.AreEqual(1, result.Completed);
            Assert.AreEqual(1, Volatile.Read(ref maxProgress));
            Assert.IsTrue(File.Exists(LocalCertificateStore.GetCertificatePath("SocketClient")));
        }
        finally
        {
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                TransportMode = "Tls",
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = false,
                AuthenticationTimeoutMilliseconds = 30000
            });

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task LoadTestCertificateWarmupCreatesPerClientCertificatesWhenStrictBindingTest()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            $"socket-loadtest-certs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        int maxProgress = 0;

        try
        {
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                TransportMode = "Tls",
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = true,
                EnforceClientCertificateId = true,
                CertificateDirectory = directory,
                AuthenticationTimeoutMilliseconds = 30000
            });

            Assert.IsTrue(LoadTestCertificateWarmup.IsRequired);
            Assert.AreEqual("SocketClient-1201", LoadTestCertificateWarmup.GetClientCertificateModuleName(1201));
            Assert.AreEqual(2, LoadTestCertificateWarmup.GetRequiredCertificateCount(new[] { 1201, 1202, 1201 }));

            LoadTestCertificateWarmupResult result = await LoadTestCertificateWarmup.WarmUpAsync(
                new[] { 1201, 1202, 1201 },
                maxConcurrency: 2,
                onCompleted: completed => UpdateMax(ref maxProgress, completed),
                CancellationToken.None);

            Assert.AreEqual(2, result.Total);
            Assert.AreEqual(2, result.Completed);
            Assert.AreEqual(2, Volatile.Read(ref maxProgress));
            Assert.IsTrue(File.Exists(LocalCertificateStore.GetCertificatePath("SocketClient-1201")));
            Assert.IsTrue(File.Exists(LocalCertificateStore.GetCertificatePath("SocketClient-1202")));
        }
        finally
        {
            SecureSocketConnection.Configure(new SocketSecurityConfig
            {
                TransportMode = "Tls",
                TlsProtocol = "Auto",
                RequireTls13 = false,
                RequireClientCertificate = false,
                AuthenticationTimeoutMilliseconds = 30000
            });
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public void LoadTestUiConnectRetryDelayBacksOffToThirtySecondsTest()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), LoadTestUiService.GetConnectRetryDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(2), LoadTestUiService.GetConnectRetryDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(4), LoadTestUiService.GetConnectRetryDelay(3));
        Assert.AreEqual(TimeSpan.FromSeconds(8), LoadTestUiService.GetConnectRetryDelay(4));
        Assert.AreEqual(TimeSpan.FromSeconds(16), LoadTestUiService.GetConnectRetryDelay(5));
        Assert.AreEqual(TimeSpan.FromSeconds(30), LoadTestUiService.GetConnectRetryDelay(6));
        Assert.AreEqual(TimeSpan.FromSeconds(30), LoadTestUiService.GetConnectRetryDelay(10));
    }

    [TestMethod]
    public void DefaultSecurityRequiresClientCertificateTest()
    {
        SocketSecurityConfig security = Program.CreateDefaultSecurityConfig();

        Assert.AreEqual("Tls", security.TransportMode);
        Assert.AreEqual("Auto", security.TlsProtocol);
        Assert.IsFalse(security.RequireTls13);
        Assert.IsTrue(security.RequireClientCertificate);
        Assert.IsFalse(security.EnforceClientCertificateId);
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
    public async Task LoadTestUiRetriesTransientConnectFailuresTest()
    {
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--ui",
                "--clients", "1",
                "--batch-size", "1",
                "--host", "127.0.0.1",
                "--port", "1",
                "--external-server"
            },
            out LoadTestOptions options,
            out string error));
        Assert.AreEqual(string.Empty, error);

        LoadTestUiService service = new(options);
        await service.StartAsync(new LoadTestUiStartRequest());

        LoadTestUiState retrying = await WaitForStateAsync(
            service,
            state => state.Counters.Attempted >= 2,
            TimeSpan.FromSeconds(5));

        Assert.IsTrue(retrying.IsRunning);
        Assert.AreEqual("Connecting", retrying.Status);
        Assert.AreEqual(0, retrying.Counters.Connected);
        Assert.IsTrue(retrying.Counters.ConnectFail >= 1);
        Assert.AreEqual(0, retrying.Counters.RegisterFail);
        Assert.AreEqual(0, retrying.Counters.HealthCheckFail);

        await service.StopAsync();
    }

    [TestMethod]
    public async Task LoadTestUiCountsRegisterRejectionAsFinalFailureTest()
    {
        TcpServer server = new(1, "load-ui-test-server", "127.0.0.1", 0);
        SocketClientSession existing = new();
        try
        {
            Assert.IsTrue(server.Start());
            Assert.IsTrue(server.StartClientAcceptLoop());
            Assert.IsTrue(await existing.ConnectAndRegisterAsync(
                900,
                "existing-900",
                "127.0.0.1",
                server.GetPort(),
                false,
                HealthCheckProtocol.KeepAliveIntervalSeconds,
                30,
                90));

            Assert.IsTrue(LoadTestOptions.TryParse(
                new[]
                {
                    "--ui",
                    "--clients", "1",
                    "--start-client-id", "900",
                    "--batch-size", "1",
                    "--host", "127.0.0.1",
                    "--port", server.GetPort().ToString(),
                    "--external-server"
                },
                out LoadTestOptions options,
                out string error));
            Assert.AreEqual(string.Empty, error);

            LoadTestUiService service = new(options);
            await service.StartAsync(new LoadTestUiStartRequest());

            LoadTestUiState rejected = await WaitForStateAsync(
                service,
                state => state.Counters.RegisterFail >= 1,
                TimeSpan.FromSeconds(10));

            Assert.IsTrue(rejected.IsRunning);
            Assert.AreEqual(0, rejected.ConnectedNow);
            Assert.AreEqual(0, rejected.Counters.ConnectFail);
            Assert.AreEqual(1, rejected.Counters.RegisterFail);

            await Task.Delay(TimeSpan.FromSeconds(2));
            LoadTestUiState afterDelay = service.GetState();
            Assert.AreEqual(1, afterDelay.Counters.RegisterFail);
            Assert.AreEqual(0, service.ActiveConnectWorkerCount);

            await service.StopAsync();
        }
        finally
        {
            existing.Dispose();
            server.End();
        }
    }

    [TestMethod]
    public async Task LoadTestUiRetriesRegisterUnavailableFailuresTest()
    {
        using NoAckRegisterServer server = new();
        Assert.IsTrue(LoadTestOptions.TryParse(
            new[]
            {
                "--ui",
                "--clients", "1",
                "--batch-size", "1",
                "--host", "127.0.0.1",
                "--port", server.Port.ToString(),
                "--external-server"
            },
            out LoadTestOptions options,
            out string error));
        Assert.AreEqual(string.Empty, error);

        LoadTestUiService service = new(options);
        try
        {
            await service.StartAsync(new LoadTestUiStartRequest());

            LoadTestUiState retrying = await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 2,
                TimeSpan.FromSeconds(10));

            Assert.IsTrue(retrying.IsRunning);
            Assert.AreEqual("Connecting", retrying.Status);
            Assert.AreEqual(0, retrying.ConnectedNow);
            Assert.AreEqual(0, retrying.Counters.RegisterFail);
            Assert.IsTrue(retrying.Counters.ConnectFail >= 1);
            Assert.IsTrue(server.AcceptedConnections >= 1);
        }
        finally
        {
            await service.StopAsync();
        }
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

    [TestMethod]
    public async Task LoadTestUiApplyScalesClientsIncrementallyTest()
    {
        TcpServer server = new(1, "loadtest-ui-server", "127.0.0.1", 0);
        Assert.IsTrue(server.BindInPortRange(0, 0));
        Assert.IsTrue(server.Listen());
        Assert.IsTrue(server.StartClientAcceptLoop());
        int port = server.GetPort();

        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);

        try
        {
            // Initial run: connect clients 1-2.
            await service.ApplyAsync(Request(port, clients: 2));
            await WaitForConnectedAsync(service, 2);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, ConnectedIds(service));

            // Increase to 4: only 3-4 are added; 1-2 stay connected (not churned).
            await service.ApplyAsync(Request(port, clients: 4));
            await WaitForConnectedAsync(service, 4);
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3, 4 }, ConnectedIds(service));

            // Decrease to 1: only the surplus 2-4 are removed.
            await service.ApplyAsync(Request(port, clients: 1));
            await WaitForConnectedAsync(service, 1);
            CollectionAssert.AreEquivalent(new[] { 1 }, ConnectedIds(service));
        }
        finally
        {
            await service.StopAsync();
            server.End();
        }
    }

    [TestMethod]
    public async Task LoadTestUiScaleDownStopsPendingRetriesForRemovedClientsTest()
    {
        int port = GetAvailablePort();
        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);
        TcpServer? server = null;

        try
        {
            await service.ApplyAsync(Request(port, clients: 2));
            await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 2,
                TimeSpan.FromSeconds(5));

            await service.ApplyAsync(Request(port, clients: 1));

            server = new TcpServer(1, "loadtest-ui-server", "127.0.0.1", 0);
            Assert.IsTrue(server.BindInPortRange(port, port));
            Assert.IsTrue(server.Listen());
            Assert.IsTrue(server.StartClientAcceptLoop());

            await WaitForConnectedAsync(service, 1);
            await Task.Delay(TimeSpan.FromSeconds(2));

            LoadTestUiState state = service.GetState();
            Assert.AreEqual(1, state.ConnectedNow);
            Assert.AreEqual(1, state.Counters.Connected);
            CollectionAssert.AreEquivalent(new[] { 1 }, ConnectedIds(service));
        }
        finally
        {
            await service.StopAsync();
            server?.End();
        }
    }

    [TestMethod]
    public async Task LoadTestUiScaleUpWithPendingRetriesDoesNotBlockLaterApplyTest()
    {
        int port = GetAvailablePort();
        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);

        try
        {
            await service.ApplyAsync(Request(port, clients: 1));
            await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 1,
                TimeSpan.FromSeconds(5));

            await AssertCompletesAsync(service.ApplyAsync(Request(port, clients: 2)), TimeSpan.FromSeconds(2));
            await AssertCompletesAsync(service.ApplyAsync(Request(port, clients: 1)), TimeSpan.FromSeconds(2));
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [TestMethod]
    public async Task LoadTestUiScaleUpKeepsSingleRunWideConnectAttemptLimitTest()
    {
        int port = GetAvailablePort();
        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);

        try
        {
            await service.ApplyAsync(new LoadTestUiStartRequest
            {
                Clients = 2,
                StartClientId = 1,
                BatchSize = 2,
                Host = "127.0.0.1",
                Port = port,
                UseControlServer = false
            });
            await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 2,
                TimeSpan.FromSeconds(5));

            Assert.AreEqual(2, service.ConnectAttemptLimit);

            await service.ApplyAsync(new LoadTestUiStartRequest
            {
                Clients = 4,
                StartClientId = 1,
                BatchSize = 10,
                Host = "127.0.0.1",
                Port = port,
                UseControlServer = false
            });

            Assert.AreEqual(2, service.ConnectAttemptLimit);
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [TestMethod]
    public async Task LoadTestUiScaleUpQueuesOnlyOnePendingBatchTest()
    {
        int port = GetAvailablePort();
        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);

        try
        {
            await service.ApplyAsync(new LoadTestUiStartRequest
            {
                Clients = 2,
                StartClientId = 1,
                BatchSize = 10,
                Host = "127.0.0.1",
                Port = port,
                UseControlServer = false
            });
            await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 2,
                TimeSpan.FromSeconds(5));

            await service.ApplyAsync(new LoadTestUiStartRequest
            {
                Clients = 100,
                StartClientId = 1,
                BatchSize = 10,
                Host = "127.0.0.1",
                Port = port,
                UseControlServer = false
            });
            await WaitForActiveConnectWorkersAsync(service, 10);

            await service.ApplyAsync(new LoadTestUiStartRequest
            {
                Clients = 200,
                StartClientId = 1,
                BatchSize = 10,
                Host = "127.0.0.1",
                Port = port,
                UseControlServer = false
            });
            await Task.Delay(200);

            Assert.IsTrue(
                service.ActiveConnectWorkerCount <= 10,
                $"Expected repeated scale-up to keep connect workers within the batch limit, observed {service.ActiveConnectWorkerCount}.");
            Assert.IsTrue(
                service.ActiveConnectQueueCount <= 1,
                $"Expected repeated scale-up to keep at most one active dispatcher, observed {service.ActiveConnectQueueCount}.");
        }
        finally
        {
            await service.StopAsync();
        }
    }

    [TestMethod]
    public async Task LoadTestUiRapidScaleDownAndUpDoesNotDuplicatePendingRetriesTest()
    {
        int port = GetAvailablePort();
        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out string error));
        Assert.AreEqual(string.Empty, error);
        LoadTestUiService service = new(options);
        TcpServer? server = null;

        try
        {
            await service.ApplyAsync(Request(port, clients: 2));
            await WaitForStateAsync(
                service,
                state => state.Counters.Attempted >= 2,
                TimeSpan.FromSeconds(5));

            await service.ApplyAsync(Request(port, clients: 1));
            await service.ApplyAsync(Request(port, clients: 2));

            server = new TcpServer(1, "loadtest-ui-server", "127.0.0.1", 0);
            Assert.IsTrue(server.BindInPortRange(port, port));
            Assert.IsTrue(server.Listen());
            Assert.IsTrue(server.StartClientAcceptLoop());

            await WaitForConnectedAsync(service, 2);
            await Task.Delay(TimeSpan.FromSeconds(2));

            LoadTestUiState state = service.GetState();
            Assert.AreEqual(2, state.ConnectedNow);
            Assert.AreEqual(2, state.Counters.Connected);
            Assert.AreEqual(0, state.Counters.RegisterFail);
            CollectionAssert.AreEquivalent(new[] { 1, 2 }, ConnectedIds(service));
        }
        finally
        {
            await service.StopAsync();
            server?.End();
        }
    }

    [TestMethod]
    public async Task LoadTestUiApplyTransportChangeRestartsRunTest()
    {
        TcpServer first = new(1, "loadtest-ui-server-a", "127.0.0.1", 0);
        Assert.IsTrue(first.BindInPortRange(0, 0));
        Assert.IsTrue(first.Listen());
        Assert.IsTrue(first.StartClientAcceptLoop());
        TcpServer second = new(2, "loadtest-ui-server-b", "127.0.0.1", 0);
        Assert.IsTrue(second.BindInPortRange(0, 0));
        Assert.IsTrue(second.Listen());
        Assert.IsTrue(second.StartClientAcceptLoop());

        Assert.IsTrue(LoadTestOptions.TryParse(new[] { "--ui" }, out LoadTestOptions options, out _));
        LoadTestUiService service = new(options);

        try
        {
            await service.ApplyAsync(Request(first.GetPort(), clients: 2));
            await WaitForConnectedAsync(service, 2);

            // Changing the target port cannot migrate live connections -> full restart on the new server.
            await service.ApplyAsync(Request(second.GetPort(), clients: 3));
            await WaitForConnectedAsync(service, 3);
            CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, ConnectedIds(service));
        }
        finally
        {
            await service.StopAsync();
            first.End();
            second.End();
        }
    }

    private static LoadTestUiStartRequest Request(int port, int clients)
    {
        return new LoadTestUiStartRequest
        {
            Clients = clients,
            StartClientId = 1,
            BatchSize = clients,
            Host = "127.0.0.1",
            Port = port,
            UseControlServer = false,
            RampDelayMilliseconds = 0
        };
    }

    private static void UpdateMax(ref int target, int value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (value <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, value, current) != current);
    }

    private static int[] ConnectedIds(LoadTestUiService service)
    {
        return service.GetState().Clients
            .Where(client => client.IsConnected)
            .Select(client => client.ClientId)
            .OrderBy(id => id)
            .ToArray();
    }

    private static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForConnectedAsync(LoadTestUiService service, int expected)
    {
        for (int attempt = 0; attempt < 150; attempt++)
        {
            LoadTestUiState state = service.GetState();
            if (state.Clients.Count == expected && state.Clients.Count(client => client.IsConnected) == expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected {expected} connected clients, observed {service.GetState().Clients.Count}.");
    }

    private static async Task AssertCompletesAsync(Task task, TimeSpan timeout)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        Assert.AreSame(task, completed);
        await task;
    }

    private static async Task WaitForActiveConnectWorkersAsync(LoadTestUiService service, int expected)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (service.ActiveConnectWorkerCount >= expected)
            {
                return;
            }

            await Task.Delay(100);
        }

        Assert.Fail($"Expected at least {expected} active connect workers, observed {service.ActiveConnectWorkerCount}.");
    }

    private static async Task<LoadTestUiState> WaitForStateAsync(
        LoadTestUiService service,
        Func<LoadTestUiState, bool> predicate,
        TimeSpan timeout)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
        LoadTestUiState state;
        do
        {
            state = service.GetState();
            if (predicate(state))
            {
                return state;
            }

            await Task.Delay(100);
        }
        while (DateTimeOffset.UtcNow < deadline);

        return state;
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

    private sealed class NoAckRegisterServer : IDisposable
    {
        private readonly Socket listener;
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task acceptTask;
        private int acceptedConnections;

        public NoAckRegisterServer()
        {
            this.listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            this.listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            this.listener.Listen(128);
            this.Port = ((IPEndPoint)this.listener.LocalEndPoint!).Port;
            this.acceptTask = Task.Run(() => this.AcceptLoopAsync(this.cancellation.Token));
        }

        public int Port { get; }

        public int AcceptedConnections => Volatile.Read(ref this.acceptedConnections);

        public void Dispose()
        {
            this.cancellation.Cancel();
            this.listener.Dispose();
            this.acceptTask.Wait(TimeSpan.FromSeconds(1));
            this.cancellation.Dispose();
        }

        private async Task AcceptLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Socket? socket = null;
                try
                {
                    socket = await this.listener.AcceptAsync(cancellationToken);
                    Interlocked.Increment(ref this.acceptedConnections);
                    _ = Task.Run(() => this.HandleConnectionAsync(socket, cancellationToken), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    socket?.Dispose();
                    return;
                }
                catch (ObjectDisposedException)
                {
                    socket?.Dispose();
                    return;
                }
                catch (SocketException)
                {
                    socket?.Dispose();
                    return;
                }
            }
        }

        private async Task HandleConnectionAsync(Socket socket, CancellationToken cancellationToken)
        {
            try
            {
                using SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateServerAsync(socket, "SocketLoadTestNoAckServer");
                await SocketMessageFrame.TryReceiveAsync(connection);
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or SocketException)
            {
            }
        }
    }
}
