using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
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
