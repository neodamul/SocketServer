using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketDashboard.Model;
using SocketSample.Shared;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class DashboardServerServiceTests
{
    [TestMethod]
    public void GetStatusReturnsRunningServerStatusTest()
    {
        using DashboardServerService service = new(0, CreateUnavailableControlEndpoint());

        DashboardServerStatus status = service.GetStatus();

        Assert.IsTrue(status.StartSucceeded);
        Assert.IsTrue(status.Server.IsSocketInitialized);
        Assert.IsTrue(status.Server.IsBound);
        Assert.IsTrue(status.Server.IsListening);
        Assert.IsTrue(status.Server.IsAcceptLoopRunning);
        Assert.AreEqual(TcpServer.DefaultMaxConnections, status.Server.MaxConnections);
        Assert.AreEqual(TcpServer.DefaultPendingAcceptCount, status.Server.PendingAcceptCount);
        Assert.AreEqual(TcpServer.DefaultIdleTimeoutSeconds, status.Server.IdleTimeoutSeconds);
        Assert.AreEqual(0, status.Server.TotalRejectedClients);
        Assert.AreEqual(0, status.Server.TotalIdleTimeoutClients);
        Assert.AreEqual(SocketFactory.ListenBacklog, status.Server.ListenBacklog);
        Assert.AreEqual(SocketFactory.NoDelay, status.Server.NoDelay);
        Assert.AreEqual(SocketMessageFrame.MaxPayloadLength, status.Server.MaxPayloadLength);
        Assert.IsTrue(status.Server.SocketAsyncEventArgsTotalCreatedCount >= SocketAsyncEventArgsFactory.InitialPoolSize);
        Assert.IsTrue(status.Server.SocketAsyncEventArgsHighWatermarkInUseCount >= 0);
        Assert.IsTrue(status.Server.Port > 0);
        Assert.AreEqual(1, status.Cluster.ServerCount);
        Assert.AreEqual(1, status.ControlServers.Count);
        Assert.AreEqual("127.0.0.1", status.ControlServers.First().Host);
        Assert.AreEqual(1, status.ControlServers.First().Port);
        Assert.IsFalse(status.ControlServers.First().IsHealthy);
    }

    [TestMethod]
    public void HealthAndMetricsEndpointsExposeOperationalStateTest()
    {
        using DashboardServerService service = new(0, CreateUnavailableControlEndpoint());

        DashboardHealthStatus liveness = service.GetLiveness();
        DashboardHealthStatus readiness = service.GetReadiness();
        DashboardMetrics metrics = service.GetMetrics();

        Assert.IsTrue(liveness.IsHealthy);
        Assert.AreEqual("Alive", liveness.Status);
        Assert.IsTrue(readiness.IsHealthy);
        Assert.AreEqual("Ready", readiness.Status);
        Assert.AreEqual(1, metrics.ServerCount);
        Assert.AreEqual(1, metrics.HealthyServerCount);
        Assert.AreEqual(TcpServer.DefaultMaxConnections, metrics.TotalMaxConnections);
        Assert.IsTrue(metrics.SocketAsyncEventArgsAvailableCount >= 0);
        Assert.IsTrue(metrics.SocketAsyncEventArgsHighWatermarkInUseCount >= 0);
    }

    private static EndpointConfig CreateUnavailableControlEndpoint()
    {
        return new EndpointConfig { Host = "127.0.0.1", Port = 1 };
    }

    [TestMethod]
    public void DashboardStaticAssetsConfigureSelectableThirtySecondRefreshTest()
    {
        string solutionRoot = FindSolutionRoot();
        string indexHtml = File.ReadAllText(Path.Combine(solutionRoot, "SocketDashboard/wwwroot/index.html"));
        string appJs = File.ReadAllText(Path.Combine(solutionRoot, "SocketDashboard/wwwroot/app.js"));
        string program = File.ReadAllText(Path.Combine(solutionRoot, "SocketDashboard/Program.cs"));

        Assert.IsTrue(indexHtml.Contains("id=\"refreshIntervalSeconds\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("class=\"metrics summary-capacity\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("class=\"metrics summary-counts\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("id=\"controlServerCount\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("id=\"socketServerCount\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("id=\"dashboardServerCount\"", StringComparison.Ordinal));
        Assert.IsFalse(indexHtml.Contains("id=\"serverInventoryCount\"", StringComparison.Ordinal));
        Assert.IsFalse(indexHtml.Contains("id=\"controlServerInventoryCount\"", StringComparison.Ordinal));
        Assert.IsFalse(indexHtml.Contains("id=\"controlServers\"", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<th>Instance</th>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<th>Max Conn</th>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<th>Current Conn</th>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<th>Available Conn</th>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<option value=\"30\" selected>30s</option>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<option value=\"5\">5s</option>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<option value=\"10\">10s</option>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("<option value=\"60\">60s</option>", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("Selected Server", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("Selected Details", StringComparison.Ordinal));
        Assert.IsTrue(indexHtml.Contains("id=\"selectedServerName\"", StringComparison.Ordinal));
        Assert.IsTrue(
            indexHtml.IndexOf("summary-capacity", StringComparison.Ordinal) <
                indexHtml.IndexOf("summary-counts", StringComparison.Ordinal));
        Assert.IsTrue(
            indexHtml.IndexOf("Server Inventory", StringComparison.Ordinal) <
                indexHtml.IndexOf("Selected Server", StringComparison.Ordinal));
        Assert.IsTrue(
            indexHtml.IndexOf("Socket Runtime", StringComparison.Ordinal) <
                indexHtml.IndexOf("Selected Details", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("const DEFAULT_REFRESH_SECONDS = 30;", StringComparison.Ordinal));
        Assert.IsFalse(appJs.Contains("renderControlServers(status.controlServers)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("function buildControlServerRow(server)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("renderServers(status.cluster.servers, server, status.controlServers)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("function renderSelectedServer(server)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("const SERVER_TYPE_ORDER = {", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("Dashboard: 0", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("ControlServer: 1", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("SocketServer: 2", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("function sortInventoryRows(servers)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("leftTypeOrder - rightTypeOrder", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("String(left.instanceId).localeCompare(String(right.instanceId)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("const rows = sortInventoryRows([dashboardRow, ...controlRows, ...socketRows].filter(Boolean));", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("data-row-key", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("selected-row", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("fields.clusterServers?.addEventListener(\"click\"", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("fields.controlServerCount.textContent = controlRows.length;", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("fields.socketServerCount.textContent = socketRows.length;", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("fields.dashboardServerCount.textContent = dashboardRow ? 1 : 0;", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("function healthText(value)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains(".map(buildSocketServerRow)", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("getRefreshIntervalMilliseconds()", StringComparison.Ordinal));
        Assert.IsTrue(appJs.Contains("scheduleRefresh()", StringComparison.Ordinal));
        Assert.IsFalse(appJs.Contains("setInterval(refresh, 1000)", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("UseStaticFiles(new StaticFileOptions", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("CacheControl = \"no-store, no-cache, must-revalidate\"", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("Pragma = \"no-cache\"", StringComparison.Ordinal));
        Assert.IsTrue(program.Contains("Expires = \"0\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void GetStatusUsesControlServerClusterStatusWhenAvailableTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(controlServer.Start());
        controlServer.Registry.Upsert(new ServerRegisterRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = 7,
            InstanceId = "server-dashboard",
            Name = "server-dashboard",
            Host = "127.0.0.1",
            Port = 5107,
            PortRangeStart = 5100,
            PortRangeEnd = 5199,
            MaxConnections = 123,
            PendingAcceptCount = 10,
            IdleTimeoutSeconds = 90
        }, "control-dashboard");

        using DashboardServerService service = new(
            0,
            new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port });

        DashboardServerStatus status = service.GetStatus();

        Assert.AreEqual(1, status.Cluster.ServerCount);
        Assert.AreEqual(123, status.Cluster.TotalMaxConnections);
        Assert.AreEqual("server-dashboard", status.Cluster.Servers.First().InstanceId);
        Assert.AreEqual("dashboardServer", status.Server.InstanceId);
        Assert.IsTrue(status.Server.IsListening);
        Assert.AreEqual(1, status.ControlServers.Count);
        Assert.IsTrue(status.ControlServers.First().IsHealthy);
        Assert.AreEqual("Healthy", status.ControlServers.First().Status);
        Assert.AreEqual(1, status.ControlServers.First().ServerCount);
    }

    [TestMethod]
    public void GetStatusMergesControlServerSnapshotsByLatestHeartbeatTest()
    {
        using ControlServer staleControlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard-stale",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(staleControlServer.Start());

        using ControlServer freshControlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard-fresh",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(freshControlServer.Start());

        staleControlServer.Registry.Upsert(CreateHeartbeat(
            "server-dashboard-merge",
            1,
            DateTimeOffset.UtcNow.AddMinutes(-5)),
            "control-dashboard-stale",
            new ControlHealthThreshold());
        freshControlServer.Registry.Upsert(CreateHeartbeat(
            "server-dashboard-merge",
            3,
            DateTimeOffset.UtcNow),
            "control-dashboard-fresh",
            new ControlHealthThreshold());

        using DashboardServerService service = new(
            0,
            new[]
            {
                new EndpointConfig { Host = "127.0.0.1", Port = staleControlServer.Port },
                new EndpointConfig { Host = "127.0.0.1", Port = freshControlServer.Port }
            });

        DashboardServerStatus status = service.GetStatus();

        Assert.AreEqual(1, status.Cluster.ServerCount);
        Assert.AreEqual(1, status.Cluster.HealthyServerCount);
        Assert.AreEqual(3, status.Cluster.TotalCurrentConnections);
        Assert.AreEqual(7, status.Cluster.TotalAvailableConnections);
        Assert.AreEqual("server-dashboard-merge", status.Cluster.Servers.First().InstanceId);
        Assert.AreEqual(ServerHealthState.Healthy, status.Cluster.Servers.First().Health);
    }

    [TestMethod]
    public void GetStatusRetainsLastClusterSnapshotWhenControlServerQueryFailsTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard-cache",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(controlServer.Start());
        controlServer.Registry.Upsert(CreateHeartbeat(
            "server-dashboard-cache",
            2,
            DateTimeOffset.UtcNow),
            "control-dashboard-cache",
            new ControlHealthThreshold());

        int controlPort = controlServer.Port;
        using DashboardServerService service = new(
            0,
            new EndpointConfig { Host = "127.0.0.1", Port = controlPort });

        DashboardServerStatus healthyStatus = service.GetStatus();
        Assert.AreEqual(1, healthyStatus.Cluster.ServerCount);
        Assert.IsTrue(healthyStatus.ControlServers.First().IsHealthy);

        controlServer.Stop();

        DashboardServerStatus cachedStatus = service.GetStatus();

        Assert.AreEqual(1, cachedStatus.Cluster.ServerCount);
        Assert.AreEqual(2, cachedStatus.Cluster.TotalCurrentConnections);
        Assert.AreEqual("server-dashboard-cache", cachedStatus.Cluster.Servers.First().InstanceId);
        Assert.IsFalse(cachedStatus.ControlServers.First().IsHealthy);
        Assert.AreEqual(1, cachedStatus.ControlServers.First().ServerCount);
        Assert.AreEqual(2, cachedStatus.ControlServers.First().TotalCurrentConnections);
    }

    [TestMethod]
    public async Task DashboardStatusReflectsSampleClientMessageThroughControlServerTest()
    {
        SocketSecurityConfig security = new()
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            AuthenticationTimeoutMilliseconds = 30000
        };
        SecureSocketConnection.Configure(security);

        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-dashboard-e2e",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5,
                DegradedCpuPercent = 101,
                DegradedMemoryPercent = 101,
                DegradedStoragePercent = 101
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer socketServer = new(
            70,
            "server-dashboard-e2e",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "server-dashboard-e2e");
        Assert.IsTrue(socketServer.BindInPortRange(0, 0));
        Assert.IsTrue(socketServer.Listen());
        Assert.IsTrue(socketServer.StartClientAcceptLoop());

        using ControlServerReporter reporter = new(
            socketServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port } },
            "socket-cluster-1",
            0,
            0);
        await reporter.RegisterAsync();
        reporter.StartHeartbeatLoop(TimeSpan.FromMilliseconds(100));

        using DashboardServerService dashboard = new(
            0,
            new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port });
        DashboardServerStatus registeredStatus = await WaitForDashboardStatusAsync(
            dashboard,
            status => status.Cluster.Servers.Any(server => server.InstanceId == "server-dashboard-e2e"));
        Assert.AreEqual("server-dashboard-e2e", registeredStatus.Cluster.Servers.First().InstanceId);
        Assert.AreNotEqual("dashboardServer", registeredStatus.Cluster.Servers.First().InstanceId);

        using SampleSocketClientSession sourceClient = new();
        using SampleSocketClientSession targetClient = new();
        sourceClient.Configure(CreateSampleSettings(701, "sample-source-e2e", controlServer.Port, security));
        targetClient.Configure(CreateSampleSettings(702, "sample-target-e2e", controlServer.Port, security));

        Assert.IsTrue(await sourceClient.ConnectAsync());
        Assert.IsTrue(await targetClient.ConnectAsync());
        Assert.IsTrue(await sourceClient.RegisterAsync());
        Assert.IsTrue(await targetClient.RegisterAsync());

        DashboardServerStatus clientStatus = await WaitForDashboardStatusAsync(
            dashboard,
            status => status.Cluster.TotalSessionCount == 2 &&
                status.Cluster.TotalCurrentConnections == 2);

        Assert.AreEqual(2, clientStatus.Cluster.TotalSessionCount);
        Assert.AreEqual(2, clientStatus.Cluster.TotalCurrentConnections);
        Assert.AreEqual(8, clientStatus.Cluster.TotalAvailableConnections);
        Assert.AreEqual("server-dashboard-e2e", clientStatus.Cluster.Servers.First().InstanceId);

        bool sent = await sourceClient.SendMessageAsync(702, "dashboard-e2e-message");

        Assert.IsTrue(sent);
        await WaitForSampleStateAsync(
            targetClient,
            state => state.LastReceivedMessage == "701: dashboard-e2e-message");
        await WaitForSampleStateAsync(
            sourceClient,
            state => state.Status == "Message delivered to 702");
        Assert.AreEqual("Message delivered to 702", sourceClient.GetState().Status);
        Assert.AreEqual("Message received", targetClient.GetState().Status);
        Assert.AreEqual("701: dashboard-e2e-message", targetClient.GetState().LastReceivedMessage);
    }

    private static SampleClientSettings CreateSampleSettings(
        int clientId,
        string clientName,
        int controlPort,
        SocketSecurityConfig security)
    {
        return new SampleClientSettings
        {
            ClientId = clientId,
            ClientName = clientName,
            Host = "127.0.0.1",
            Port = controlPort,
            UseControlServer = true,
            ReceiveTimeoutSeconds = 3,
            Security = security
        };
    }

    private static ServerHeartbeatRequest CreateHeartbeat(
        string instanceId,
        int currentConnections,
        DateTimeOffset sentAt)
    {
        return new ServerHeartbeatRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = 77,
            InstanceId = instanceId,
            Host = "127.0.0.1",
            Port = 5177,
            MaxConnections = 10,
            CurrentConnections = currentConnections,
            ResourceUsage = new ResourceUsageSnapshot
            {
                CpuUsagePercent = 10,
                MemoryUsagePercent = 20,
                StorageUsagePercent = 30,
                CapturedAt = sentAt
            },
            SentAt = sentAt
        };
    }

    private static async Task<DashboardServerStatus> WaitForDashboardStatusAsync(
        DashboardServerService dashboard,
        Func<DashboardServerStatus, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        DashboardServerStatus status;
        do
        {
            status = dashboard.GetStatus();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for dashboard status.");
        return status;
    }

    private static async Task WaitForSampleStateAsync(
        SampleSocketClientSession client,
        Func<SampleClientState, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        SampleClientState state;
        do
        {
            state = client.GetState();
            if (predicate(state))
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        state = client.GetState();
        Assert.Fail(
            $"Timed out waiting for sample client state. " +
            $"clientId={state.ClientId}, status={state.Status}, " +
            $"lastReceived={state.LastReceivedMessage}, error={state.LastError}");
    }

    private static string FindSolutionRoot()
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

        throw new DirectoryNotFoundException("SocketServer.sln was not found from the test output path.");
    }
}
