using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
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
        using DashboardServerService service = new(0);

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
    }

    [TestMethod]
    public void HealthAndMetricsEndpointsExposeOperationalStateTest()
    {
        using DashboardServerService service = new(0);

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
    }

    [TestMethod]
    public async Task DashboardStatusReflectsSampleClientMessageThroughControlServerTest()
    {
        SocketSecurityConfig security = new()
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            AuthenticationTimeoutMilliseconds = 5000
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
                RouteReservationSeconds = 5
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

        Task<ClientMessageDelivery?> receiveTask = targetClient.ReceiveMessageAsync();
        bool sent = await sourceClient.SendMessageAsync(702, "dashboard-e2e-message");
        ClientMessageDelivery? delivery = await receiveTask;

        Assert.IsTrue(sent);
        Assert.IsNotNull(delivery);
        Assert.AreEqual((uint)701, delivery!.SourceClientId);
        Assert.AreEqual((uint)702, delivery.TargetClientId);
        Assert.AreEqual("dashboard-e2e-message", delivery.Content);
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
}
