using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketSample.Shared;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ControlServerIntegrationTests
{
    [TestMethod]
    public async Task ClientReceivesRouteAndConnectsToSocketServerTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-test",
                Host = "127.0.0.1",
                Port = 0,
                RouteReservationSeconds = 5
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer socketServer = new(
            1,
            "server-1-a",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "server-1-a");
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
        await WaitForClusterAsync(controlServer, status => status.TotalAvailableConnections == 10);

        using TcpClient client = new(7, "client-7");
        Assert.IsTrue(await client.ConnectViaControlServerAsync("127.0.0.1", controlServer.Port));
        Assert.IsTrue(await client.SendHealthCheckAsync());
        (bool received, HealthCheckMessage message) = await WithTimeoutAsync(client.TryReceiveHealthCheckAsync());

        Assert.IsTrue(received);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        ClusterStatusSnapshot routeStatus = await WaitForClusterAsync(
            controlServer,
            status => status.TotalReservedConnections == 0 && status.TotalSessionCount == 1);
        Assert.AreEqual(0, routeStatus.TotalReservedConnections);
        Assert.AreEqual(10, routeStatus.TotalAvailableConnections);
        Assert.AreEqual(1, routeStatus.TotalSessionCount);
    }

    [TestMethod]
    public async Task PeerControlServerReceivesRegistryUpsertAndRoutesClientTest()
    {
        using ControlServer peerControl = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-peer",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(peerControl.Start());

        using ControlServer primaryControl = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-primary",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0
            },
            Peers = { new EndpointConfig { Host = "127.0.0.1", Port = peerControl.Port } }
        });
        Assert.IsTrue(primaryControl.Start());

        using TcpServer socketServer = new(
            2,
            "server-2-a",
            "127.0.0.1",
            0,
            maxConnections: 20,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "server-2-a");
        Assert.IsTrue(socketServer.BindInPortRange(0, 0));
        Assert.IsTrue(socketServer.Listen());
        Assert.IsTrue(socketServer.StartClientAcceptLoop());

        using ControlServerReporter reporter = new(
            socketServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = primaryControl.Port } },
            "socket-cluster-1",
            0,
            0);
        await reporter.RegisterAsync();

        ClusterStatusSnapshot peerStatus = await WaitForClusterAsync(
            peerControl,
            status => status.TotalAvailableConnections == 20);
        Assert.AreEqual(1, peerStatus.ServerCount);

        using TcpClient client = new(8, "client-8");
        Assert.IsTrue(await client.ConnectViaControlServerAsync("127.0.0.1", peerControl.Port));
        Assert.IsTrue(await client.SendHealthCheckAsync());
        (bool received, HealthCheckMessage message) = await WithTimeoutAsync(client.TryReceiveHealthCheckAsync());
        Assert.IsTrue(received);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
    }

    [TestMethod]
    public async Task ClientMessageDeliveredBetweenClientsOnSameSocketServerTest()
    {
        using TcpServer socketServer = new(
            3,
            "server-3-a",
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: "server-3-a");
        Assert.IsTrue(socketServer.BindInPortRange(0, 0));
        Assert.IsTrue(socketServer.Listen());
        Assert.IsTrue(socketServer.StartClientAcceptLoop());

        using TcpClient sourceClient = new(31, "source-31", "127.0.0.1", socketServer.GetPort());
        using TcpClient targetClient = new(32, "target-32", "127.0.0.1", socketServer.GetPort());

        Assert.IsTrue(sourceClient.Connect());
        Assert.IsTrue(targetClient.Connect());
        Assert.IsTrue(await sourceClient.RegisterClientAsync());
        Assert.IsTrue(await targetClient.RegisterClientAsync());

        Task<(bool Success, ClientMessageAck Ack, ClientMessageError Error)> sendTask =
            sourceClient.SendClientMessageAsync(32, "same-server-message");
        (bool delivered, ClientMessageDelivery delivery) =
            await WithTimeoutAsync(targetClient.TryReceiveClientMessageAsync());
        (bool acked, ClientMessageAck ack, ClientMessageError error) =
            await WithTimeoutAsync(sendTask);

        Assert.IsTrue(delivered);
        Assert.AreEqual((uint)31, delivery.SourceClientId);
        Assert.AreEqual((uint)32, delivery.TargetClientId);
        Assert.AreEqual("same-server-message", delivery.Content);
        Assert.IsTrue(acked);
        Assert.IsNotNull(ack);
        Assert.IsNull(error);
        Assert.AreEqual("server-3-a", ack.TargetInstanceId);
    }

    [TestMethod]
    public async Task ClientMessageRelayedBetweenClientsOnDifferentSocketServersTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-relay",
                Host = "127.0.0.1",
                Port = 0,
                RouteReservationSeconds = 5
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer sourceServer = CreateStartedSocketServer(4, "server-4-a");
        using TcpServer targetServer = CreateStartedSocketServer(5, "server-5-a");
        using ControlServerReporter sourceReporter = new(
            sourceServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port } },
            "socket-cluster-1",
            0,
            0);
        using ControlServerReporter targetReporter = new(
            targetServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port } },
            "socket-cluster-1",
            0,
            0);

        await sourceReporter.RegisterAsync();
        await targetReporter.RegisterAsync();
        await WaitForClusterAsync(controlServer, status => status.ServerCount == 2);

        using TcpClient sourceClient = new(41, "source-41", "127.0.0.1", sourceServer.GetPort());
        using TcpClient targetClient = new(42, "target-42", "127.0.0.1", targetServer.GetPort());
        Assert.IsTrue(sourceClient.Connect());
        Assert.IsTrue(targetClient.Connect());
        Assert.IsTrue(await sourceClient.RegisterClientAsync());
        Assert.IsTrue(await targetClient.RegisterClientAsync());
        await WaitForClientLocationCountAsync(controlServer, 2);

        Task<(bool Success, ClientMessageAck Ack, ClientMessageError Error)> sendTask =
            sourceClient.SendClientMessageAsync(42, "cross-server-message");
        (bool delivered, ClientMessageDelivery delivery) =
            await WithTimeoutAsync(targetClient.TryReceiveClientMessageAsync());
        (bool acked, ClientMessageAck ack, ClientMessageError error) =
            await WithTimeoutAsync(sendTask);

        Assert.IsTrue(delivered);
        Assert.AreEqual((uint)41, delivery.SourceClientId);
        Assert.AreEqual((uint)42, delivery.TargetClientId);
        Assert.AreEqual("cross-server-message", delivery.Content);
        Assert.IsTrue(acked);
        Assert.IsNotNull(ack);
        Assert.IsNull(error);
        Assert.AreEqual("server-5-a", ack.TargetInstanceId);
    }

    [TestMethod]
    public async Task ControlServerMarksSocketServerUnavailableWhenControlChannelDisconnectsTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-disconnect",
                Host = "127.0.0.1",
                Port = 0,
                RouteReservationSeconds = 5
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer socketServer = CreateStartedSocketServer(6, "server-6-a");
        using ControlServerReporter reporter = new(
            socketServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port } },
            "socket-cluster-1",
            0,
            0);

        await reporter.RegisterAsync();
        await WaitForClusterAsync(controlServer, status => status.HealthyServerCount == 1 && status.TotalAvailableConnections == 10);

        reporter.Stop();

        ClusterStatusSnapshot disconnectedStatus = await WaitForClusterAsync(
            controlServer,
            status => status.ServerCount == 1 &&
                status.HealthyServerCount == 0 &&
                status.TotalAvailableConnections == 0);
        Assert.AreEqual(0, disconnectedStatus.HealthyServerCount);
        Assert.AreEqual(0, disconnectedStatus.TotalAvailableConnections);
    }

    [TestMethod]
    public async Task ControlServerCleanupSchedulerClosesStaleControlConnectionTest()
    {
        using ControlServer controlServer = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-cleanup",
                Host = "127.0.0.1",
                Port = 0,
                RouteReservationSeconds = 5,
                HeartbeatTimeoutSeconds = 1
            }
        });
        Assert.IsTrue(controlServer.Start());

        using TcpServer socketServer = CreateStartedSocketServer(7, "server-7-a");
        using ControlServerReporter reporter = new(
            socketServer,
            new[] { new EndpointConfig { Host = "127.0.0.1", Port = controlServer.Port } },
            "socket-cluster-1",
            0,
            0);

        await reporter.RegisterAsync();
        await WaitForClusterAsync(controlServer, status => status.HealthyServerCount == 1 && status.TotalAvailableConnections == 10);
        await WaitForConditionAsync(() => controlServer.ActiveConnectionCount == 1);

        await WaitForConditionAsync(() => controlServer.ActiveConnectionCount == 0, timeoutSeconds: 4);
        ClusterStatusSnapshot staleStatus = await WaitForClusterAsync(
            controlServer,
            status => status.ServerCount == 1 &&
                status.HealthyServerCount == 0 &&
                status.TotalAvailableConnections == 0);
        Assert.AreEqual(0, staleStatus.HealthyServerCount);

        reporter.StartHeartbeatLoop(TimeSpan.FromMilliseconds(100));
        ClusterStatusSnapshot restoredStatus = await WaitForClusterAsync(
            controlServer,
            status => status.HealthyServerCount == 1 && status.TotalAvailableConnections == 10);
        Assert.AreEqual(1, restoredStatus.HealthyServerCount);
    }

    [TestMethod]
    public async Task ActiveActiveControlsRouteThreeServersAndPlatformSampleClientsMessageTest()
    {
        SocketSecurityConfig security = new()
        {
            TransportMode = "Tls",
            TlsProtocol = "Auto",
            RequireTls13 = false,
            AuthenticationTimeoutMilliseconds = 5000
        };
        SecureSocketConnection.Configure(security);

        using ControlServer controlA = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-active-a",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5
            }
        });
        Assert.IsTrue(controlA.Start());

        using ControlServer controlB = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-active-b",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5
            },
            Peers = { new EndpointConfig { Host = "127.0.0.1", Port = controlA.Port } }
        });
        Assert.IsTrue(controlB.Start());

        EndpointConfig[] controlEndpoints =
        {
            new() { Host = "127.0.0.1", Port = controlA.Port },
            new() { Host = "127.0.0.1", Port = controlB.Port }
        };

        using TcpServer serverA = CreateStartedSocketServer(80, "server-active-a");
        using TcpServer serverB = CreateStartedSocketServer(81, "server-active-b");
        using TcpServer serverC = CreateStartedSocketServer(82, "server-active-c");
        using ControlServerReporter reporterA = CreateReporter(serverA, controlEndpoints);
        using ControlServerReporter reporterB = CreateReporter(serverB, controlEndpoints);
        using ControlServerReporter reporterC = CreateReporter(serverC, controlEndpoints);

        await Task.WhenAll(reporterA.RegisterAsync(), reporterB.RegisterAsync(), reporterC.RegisterAsync());
        reporterA.StartHeartbeatLoop(TimeSpan.FromMilliseconds(100));
        reporterB.StartHeartbeatLoop(TimeSpan.FromMilliseconds(100));
        reporterC.StartHeartbeatLoop(TimeSpan.FromMilliseconds(100));

        await WaitForClusterAsync(controlA, status => status.ServerCount == 3 && status.TotalAvailableConnections == 30);
        await WaitForClusterAsync(controlB, status => status.ServerCount == 3 && status.TotalAvailableConnections == 30);

        using SampleSocketClientSession dotnetClient = new();
        using SampleSocketClientSession iosClient = new();
        using SampleSocketClientSession macosClient = new();
        using SampleSocketClientSession androidClient = new();

        dotnetClient.Configure(CreatePlatformClientSettings(901, "sample-dotnet-client", controlA.Port, security));
        iosClient.Configure(CreatePlatformClientSettings(902, "sample-ios-client", controlB.Port, security));
        macosClient.Configure(CreatePlatformClientSettings(903, "sample-macos-client", controlA.Port, security));
        androidClient.Configure(CreatePlatformClientSettings(904, "sample-android-client", controlB.Port, security));

        bool[] connectResults = await Task.WhenAll(
            dotnetClient.ConnectAsync(),
            iosClient.ConnectAsync(),
            macosClient.ConnectAsync(),
            androidClient.ConnectAsync());
        Assert.IsTrue(connectResults.All(result => result));

        bool[] registerResults = await Task.WhenAll(
            dotnetClient.RegisterAsync(),
            iosClient.RegisterAsync(),
            macosClient.RegisterAsync(),
            androidClient.RegisterAsync());
        Assert.IsTrue(registerResults.All(result => result));

        await WaitForClusterAsync(controlA, status => status.TotalSessionCount == 4 && status.TotalCurrentConnections == 4);
        await WaitForClusterAsync(controlB, status => status.TotalSessionCount == 4 && status.TotalCurrentConnections == 4);
        await WaitForClientLocationCountAsync(controlA, 4);
        await WaitForClientLocationCountAsync(controlB, 4);

        Task<ClientMessageDelivery?> iosReceiveTask = iosClient.ReceiveMessageAsync();
        Task<ClientMessageDelivery?> macosReceiveTask = macosClient.ReceiveMessageAsync();
        Task<bool> dotnetSendTask = dotnetClient.SendMessageAsync(902, "dotnet-to-ios");
        Task<bool> androidSendTask = androidClient.SendMessageAsync(903, "android-to-macos");

        Assert.IsTrue(await WithTimeoutAsync(dotnetSendTask));
        Assert.IsTrue(await WithTimeoutAsync(androidSendTask));

        ClientMessageDelivery? iosDelivery = await WithTimeoutAsync(iosReceiveTask);
        ClientMessageDelivery? macosDelivery = await WithTimeoutAsync(macosReceiveTask);

        Assert.IsNotNull(iosDelivery);
        Assert.AreEqual((uint)901, iosDelivery!.SourceClientId);
        Assert.AreEqual((uint)902, iosDelivery.TargetClientId);
        Assert.AreEqual("dotnet-to-ios", iosDelivery.Content);
        Assert.AreEqual("901: dotnet-to-ios", iosClient.GetState().LastReceivedMessage);

        Assert.IsNotNull(macosDelivery);
        Assert.AreEqual((uint)904, macosDelivery!.SourceClientId);
        Assert.AreEqual((uint)903, macosDelivery.TargetClientId);
        Assert.AreEqual("android-to-macos", macosDelivery.Content);
        Assert.AreEqual("904: android-to-macos", macosClient.GetState().LastReceivedMessage);

        ClusterStatusSnapshot finalStatus = await WaitForClusterAsync(
            controlA,
            status => status.TotalSessionCount == 4 && status.TotalCurrentConnections == 4);
        Assert.AreEqual(3, finalStatus.ServerCount);
        Assert.AreEqual(4, finalStatus.TotalCurrentConnections);
        Assert.AreEqual(26, finalStatus.TotalAvailableConnections);
    }

    private static async Task<ClusterStatusSnapshot> WaitForClusterAsync(
        ControlServer controlServer,
        Func<ClusterStatusSnapshot, bool> predicate)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        ClusterStatusSnapshot status;
        do
        {
            status = controlServer.GetClusterStatus();
            if (predicate(status))
            {
                return status;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for cluster status.");
        return status;
    }

    private static TcpServer CreateStartedSocketServer(int serverId, string instanceId)
    {
        TcpServer socketServer = new(
            serverId,
            instanceId,
            "127.0.0.1",
            0,
            maxConnections: 10,
            pendingAcceptCount: 2,
            idleTimeout: TimeSpan.FromSeconds(30),
            instanceId: instanceId);
        Assert.IsTrue(socketServer.BindInPortRange(0, 0));
        Assert.IsTrue(socketServer.Listen());
        Assert.IsTrue(socketServer.StartClientAcceptLoop());
        return socketServer;
    }

    private static ControlServerReporter CreateReporter(TcpServer server, EndpointConfig[] controlEndpoints)
    {
        return new ControlServerReporter(
            server,
            controlEndpoints,
            "socket-cluster-1",
            0,
            0);
    }

    private static SampleClientSettings CreatePlatformClientSettings(
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
            ReceiveTimeoutSeconds = 5,
            Security = security
        };
    }

    private static async Task WaitForClientLocationCountAsync(ControlServer controlServer, int expectedCount)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        do
        {
            if (controlServer.Registry.ClientLocations.Count == expectedCount)
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for client location count.");
    }

    private static async Task WaitForConditionAsync(Func<bool> predicate, int timeoutSeconds = 5)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        do
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(25);
        }
        while (DateTimeOffset.UtcNow < deadline);

        Assert.Fail("Timed out waiting for condition.");
    }

    private static async Task<T> WithTimeoutAsync<T>(Task<T> task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for task.");
        }

        return await task;
    }
}
