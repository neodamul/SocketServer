using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketClient.Model;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketControl.Model;
using SocketSample.Shared;
using SocketServer.Model;
using AddressFamily = System.Net.Sockets.AddressFamily;
using NetSocket = System.Net.Sockets.Socket;
using SocketException = System.Net.Sockets.SocketException;

namespace SocketTests.Model;

[TestClass]
public class ControlServerIntegrationTests
{
    [TestMethod]
    public async Task ClientReceivesRouteAndConnectsToSocketServerTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("route");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(1, "server-route");
        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);
        await WaitForClusterAsync(controls.ControlB, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);

        using TcpClient client = new(7, "client-7");
        Assert.IsTrue(await client.ConnectViaControlServerAsync("127.0.0.1", controls.ControlA.Port));
        Assert.IsTrue(await client.SendHealthCheckAsync());
        (bool received, HealthCheckMessage message) = await WithTimeoutAsync(client.TryReceiveHealthCheckAsync());

        Assert.IsTrue(received);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
        ClusterStatusSnapshot routeStatus = await WaitForClusterAsync(
            controls.ControlA,
            status => status.TotalReservedConnections == 0 && status.TotalSessionCount == 1);
        Assert.AreEqual(0, routeStatus.TotalReservedConnections);
        Assert.AreEqual(40, routeStatus.TotalAvailableConnections);
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

        using SocketServerCluster servers = CreateStartedSocketServerCluster(2, "server-peer");
        servers.AttachReporters(new[] { new EndpointConfig { Host = "127.0.0.1", Port = primaryControl.Port } });
        await servers.RegisterAsync();

        ClusterStatusSnapshot peerStatus = await WaitForClusterAsync(
            peerControl,
            status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);
        Assert.AreEqual(4, peerStatus.ServerCount);

        using TcpClient client = new(8, "client-8");
        Assert.IsTrue(await client.ConnectViaControlServerAsync("127.0.0.1", peerControl.Port));
        Assert.IsTrue(await client.SendHealthCheckAsync());
        (bool received, HealthCheckMessage message) = await WithTimeoutAsync(client.TryReceiveHealthCheckAsync());
        Assert.IsTrue(received);
        Assert.AreEqual(HealthCheckMessageType.Pong, message.Type);
    }

    [TestMethod]
    public async Task PeerControlServerPeriodicSnapshotSyncRecoversMissedEventsTest()
    {
        int primaryPort = GetAvailablePort();
        using ControlServer peerControl = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-peer-periodic",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                PeerSnapshotSyncIntervalSeconds = 1
            },
            Peers = { new EndpointConfig { Host = "127.0.0.1", Port = primaryPort } }
        });
        Assert.IsTrue(peerControl.Start());

        using ControlServer primaryControl = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-primary-periodic",
                Host = "127.0.0.1",
                Port = primaryPort,
                PeerSyncPort = 0
            }
        });
        Assert.IsTrue(primaryControl.Start());

        using SocketServerCluster servers = CreateStartedSocketServerCluster(9, "server-periodic");
        servers.AttachReporters(new[] { new EndpointConfig { Host = "127.0.0.1", Port = primaryControl.Port } });
        await servers.RegisterAsync();
        await WaitForClusterAsync(primaryControl, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);

        ClusterStatusSnapshot peerStatus = await WaitForClusterAsync(
            peerControl,
            status => status.ServerCount == 4 && status.TotalAvailableConnections == 40,
            timeoutSeconds: 6);
        Assert.AreEqual(4, peerStatus.ServerCount);
        Assert.AreEqual(40, peerStatus.TotalAvailableConnections);
    }

    [TestMethod]
    public async Task ClientMessageDeliveredBetweenClientsOnSameSocketServerTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("same-server");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(30, "server-same");
        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);

        TcpServer socketServer = servers.Servers[0];
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
        Assert.AreEqual(socketServer.InstanceId, ack.TargetInstanceId);
    }

    [TestMethod]
    public async Task ClientMessageRelayedBetweenClientsOnDifferentSocketServersTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("relay");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(40, "server-relay");
        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4);
        await WaitForClusterAsync(controls.ControlB, status => status.ServerCount == 4);

        TcpServer sourceServer = servers.Servers[0];
        TcpServer targetServer = servers.Servers[3];
        using TcpClient sourceClient = new(41, "source-41", "127.0.0.1", sourceServer.GetPort());
        using TcpClient targetClient = new(42, "target-42", "127.0.0.1", targetServer.GetPort());
        Assert.IsTrue(sourceClient.Connect());
        Assert.IsTrue(targetClient.Connect());
        Assert.IsTrue(await sourceClient.RegisterClientAsync());
        Assert.IsTrue(await targetClient.RegisterClientAsync());
        await WaitForClientLocationCountAsync(controls.ControlA, 2);
        await WaitForClientLocationCountAsync(controls.ControlB, 2);

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
        Assert.AreEqual(targetServer.InstanceId, ack.TargetInstanceId);
    }

    [TestMethod]
    public async Task SocketServerBroadcastsRelayToKnownServersWhenControlLocationIsMissingTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("broadcast");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(100, "server-broadcast");
        TcpServer sourceServer = servers.Servers[0];
        TcpServer targetServer = servers.Servers[3];
        using TcpClient sourceClient = new(101, "source-101", "127.0.0.1", sourceServer.GetPort());
        using TcpClient targetClient = new(102, "target-102", "127.0.0.1", targetServer.GetPort());

        Assert.IsTrue(sourceClient.Connect());
        Assert.IsTrue(targetClient.Connect());
        Assert.IsTrue(await sourceClient.RegisterClientAsync());
        Assert.IsTrue(await targetClient.RegisterClientAsync());

        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4 && status.TotalSessionCount == 0);
        await WaitForClusterAsync(controls.ControlB, status => status.ServerCount == 4 && status.TotalSessionCount == 0);

        Task<(bool Success, ClientMessageAck Ack, ClientMessageError Error)> sendTask =
            sourceClient.SendClientMessageAsync(102, "broadcast-relay-message");
        (bool delivered, ClientMessageDelivery delivery) =
            await WithTimeoutAsync(targetClient.TryReceiveClientMessageAsync());
        (bool acked, ClientMessageAck ack, ClientMessageError error) =
            await WithTimeoutAsync(sendTask);

        Assert.IsTrue(delivered);
        Assert.AreEqual((uint)101, delivery.SourceClientId);
        Assert.AreEqual((uint)102, delivery.TargetClientId);
        Assert.AreEqual("broadcast-relay-message", delivery.Content);
        Assert.IsTrue(acked);
        Assert.IsNotNull(ack);
        Assert.IsNull(error);
        Assert.AreEqual(targetServer.InstanceId, ack.TargetInstanceId);
    }

    [TestMethod]
    public async Task SocketServerRefreshesRelayListAndBroadcastsAcrossAllKnownServersTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("broadcast-all");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(120, "server-broadcast-all");
        TcpServer sourceServer = servers.Servers[0];
        TcpServer targetServer = servers.Servers[3];
        using TcpClient sourceClient = new(121, "source-121", "127.0.0.1", sourceServer.GetPort());
        using TcpClient targetClient = new(122, "target-122", "127.0.0.1", targetServer.GetPort());

        Assert.IsTrue(sourceClient.Connect());
        Assert.IsTrue(targetClient.Connect());
        Assert.IsTrue(await sourceClient.RegisterClientAsync());
        Assert.IsTrue(await targetClient.RegisterClientAsync());

        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4 && status.TotalSessionCount == 0);
        await WaitForClusterAsync(controls.ControlB, status => status.ServerCount == 4 && status.TotalSessionCount == 0);

        int relayServerCount = await sourceServer.RefreshRelayServersFromControlServersAsync();
        Assert.AreEqual(3, relayServerCount);
        Assert.AreEqual(3, sourceServer.RelayServerCount);

        Task<(bool Success, ClientMessageAck Ack, ClientMessageError Error)> sendTask =
            sourceClient.SendClientMessageAsync(122, "broadcast-all-relay-message");
        (bool delivered, ClientMessageDelivery delivery) =
            await WithTimeoutAsync(targetClient.TryReceiveClientMessageAsync());
        (bool acked, ClientMessageAck ack, ClientMessageError error) =
            await WithTimeoutAsync(sendTask);

        Assert.IsTrue(delivered);
        Assert.AreEqual((uint)121, delivery.SourceClientId);
        Assert.AreEqual((uint)122, delivery.TargetClientId);
        Assert.AreEqual("broadcast-all-relay-message", delivery.Content);
        Assert.IsTrue(acked);
        Assert.IsNotNull(ack);
        Assert.IsNull(error);
        Assert.AreEqual(targetServer.InstanceId, ack.TargetInstanceId);

        ClusterStatusSnapshot statusAfterRelay = await WaitForClusterAsync(
            controls.ControlA,
            status => status.ServerCount == 4 &&
                status.TotalSessionCount == 1);
        Assert.AreEqual(1, statusAfterRelay.TotalSessionCount);
        Assert.AreEqual(1, controls.ControlA.Registry.ClientLocations.Count);
    }

    [TestMethod]
    public async Task ControlServerMarksSocketServerUnavailableWhenControlChannelDisconnectsTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("disconnect");
        using SocketServerCluster servers = CreateStartedSocketServerCluster(60, "server-disconnect");
        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.ServerCount == 4 && status.HealthyServerCount == 4 && status.TotalAvailableConnections == 40);

        servers.Reporters[0].Stop();

        ClusterStatusSnapshot disconnectedStatus = await WaitForClusterAsync(
            controls.ControlA,
            status => status.ServerCount == 4 &&
                status.HealthyServerCount == 3 &&
                status.TotalAvailableConnections == 30);
        Assert.AreEqual(3, disconnectedStatus.HealthyServerCount);
        Assert.AreEqual(30, disconnectedStatus.TotalAvailableConnections);
    }

    [TestMethod]
    public async Task ControlServerCleanupSchedulerClosesStaleControlConnectionTest()
    {
        using ControlServerPair controls = CreateStartedControlPair("cleanup", heartbeatTimeoutSeconds: 1);
        using SocketServerCluster servers = CreateStartedSocketServerCluster(70, "server-cleanup");
        servers.AttachReporters(controls.Endpoints);
        await servers.RegisterAsync();
        await WaitForClusterAsync(controls.ControlA, status => status.HealthyServerCount == 4 && status.TotalAvailableConnections == 40);
        await WaitForConditionAsync(() => controls.ControlA.ActiveConnectionCount >= 4);

        await WaitForConditionAsync(() => controls.ControlA.ActiveConnectionCount == 0, timeoutSeconds: 4);
        ClusterStatusSnapshot staleStatus = await WaitForClusterAsync(
            controls.ControlA,
            status => status.ServerCount == 4 &&
                status.HealthyServerCount == 0 &&
                status.TotalAvailableConnections == 0);
        Assert.AreEqual(0, staleStatus.HealthyServerCount);

        servers.StartHeartbeatLoops(TimeSpan.FromMilliseconds(100));
        ClusterStatusSnapshot restoredStatus = await WaitForClusterAsync(
            controls.ControlA,
            status => status.HealthyServerCount == 4 && status.TotalAvailableConnections == 40,
            timeoutSeconds: 10);
        Assert.AreEqual(4, restoredStatus.HealthyServerCount);
    }

    [TestMethod]
    public async Task ReporterDirectlyReportsToHealthyControlWhenAnotherEndpointStallsTest()
    {
        using NetSocket stalledListener = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        stalledListener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        stalledListener.Listen(SocketFactory.ListenBacklog);
        int stalledPort = ((IPEndPoint)stalledListener.LocalEndPoint!).Port;
        using CancellationTokenSource stalledCancellation = new();
        List<NetSocket> stalledSockets = new();
        Task stalledAcceptTask = AcceptAndHoldSocketsAsync(stalledListener, stalledSockets, stalledCancellation.Token);

        using ControlServer healthyControl = new(new ControlServerConfigFile
        {
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = "control-healthy",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5
            }
        });
        Assert.IsTrue(healthyControl.Start());

        using SocketServerCluster servers = CreateStartedSocketServerCluster(80, "server-healthy");
        EndpointConfig[] controlEndpoints =
        {
            new() { Host = "127.0.0.1", Port = stalledPort },
            new() { Host = "127.0.0.1", Port = healthyControl.Port }
        };
        servers.AttachReporters(controlEndpoints);

        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Task registerTask = servers.RegisterAsync();
        ClusterStatusSnapshot status = await WaitForClusterAsync(
            healthyControl,
            snapshot => snapshot.ServerCount == 4 && snapshot.HealthyServerCount == 4);

        Assert.AreEqual(40, status.TotalAvailableConnections);
        Task completedTask = await Task.WhenAny(registerTask, Task.Delay(TimeSpan.FromSeconds(4)));
        Assert.AreSame(registerTask, completedTask);
        await registerTask;
        Assert.IsTrue(DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(4));

        stalledCancellation.Cancel();
        stalledListener.Dispose();
        NetSocket[] socketsToDispose;
        lock (stalledSockets)
        {
            socketsToDispose = stalledSockets.ToArray();
        }

        foreach (NetSocket socket in socketsToDispose)
        {
            socket.Dispose();
        }

        await WithTimeoutAsync(stalledAcceptTask);
    }

    [TestMethod]
    public async Task ActiveActiveControlsRouteFourServersAndPlatformSampleClientsMessageTest()
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

        using SocketServerCluster servers = CreateStartedSocketServerCluster(90, "server-active");
        servers.AttachReporters(controlEndpoints);

        await servers.RegisterAsync();
        servers.StartHeartbeatLoops(TimeSpan.FromMilliseconds(500));

        await WaitForClusterAsync(controlA, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);
        await WaitForClusterAsync(controlB, status => status.ServerCount == 4 && status.TotalAvailableConnections == 40);

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

        await WaitForClusterAsync(controlA, status => status.TotalSessionCount == 4);
        await WaitForClusterAsync(controlB, status => status.TotalSessionCount == 4);
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
            status => status.ServerCount == 4 &&
                status.TotalSessionCount == 4,
            timeoutSeconds: 15);
        Assert.AreEqual(4, finalStatus.ServerCount);
        Assert.AreEqual(4, finalStatus.TotalSessionCount);
    }

    private static async Task<ClusterStatusSnapshot> WaitForClusterAsync(
        ControlServer controlServer,
        Func<ClusterStatusSnapshot, bool> predicate,
        int timeoutSeconds = 5)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
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

        Assert.Fail(
            $"Timed out waiting for cluster status. " +
            $"servers={status.ServerCount}, healthy={status.HealthyServerCount}, " +
            $"sessions={status.TotalSessionCount}, current={status.TotalCurrentConnections}, " +
            $"reserved={status.TotalReservedConnections}, available={status.TotalAvailableConnections}");
        return status;
    }

    private static int GetAvailablePort()
    {
        using NetSocket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
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

    private static ControlServerPair CreateStartedControlPair(
        string nodeIdSuffix,
        SocketSecurityConfig security = null!,
        int heartbeatTimeoutSeconds = 30)
    {
        ControlServer controlA = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = $"control-{nodeIdSuffix}-a",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5,
                HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds
            }
        });
        Assert.IsTrue(controlA.Start());

        ControlServer controlB = new(new ControlServerConfigFile
        {
            Security = security,
            ControlServer = new ControlServerNodeConfig
            {
                ClusterId = "socket-cluster-1",
                NodeId = $"control-{nodeIdSuffix}-b",
                Host = "127.0.0.1",
                Port = 0,
                PeerSyncPort = 0,
                RouteReservationSeconds = 5,
                HeartbeatTimeoutSeconds = heartbeatTimeoutSeconds
            },
            Peers = { new EndpointConfig { Host = "127.0.0.1", Port = controlA.Port } }
        });
        Assert.IsTrue(controlB.Start());

        return new ControlServerPair(controlA, controlB);
    }

    private static SocketServerCluster CreateStartedSocketServerCluster(
        int firstServerId,
        string instanceIdPrefix,
        int count = 4)
    {
        List<TcpServer> servers = new();
        for (int index = 0; index < count; index++)
        {
            servers.Add(CreateStartedSocketServer(firstServerId + index, $"{instanceIdPrefix}-{index + 1}"));
        }

        return new SocketServerCluster(servers);
    }

    private sealed class ControlServerPair : IDisposable
    {
        public ControlServerPair(ControlServer controlA, ControlServer controlB)
        {
            this.ControlA = controlA;
            this.ControlB = controlB;
            this.Endpoints = new[]
            {
                new EndpointConfig { Host = "127.0.0.1", Port = controlA.Port },
                new EndpointConfig { Host = "127.0.0.1", Port = controlB.Port }
            };
        }

        public ControlServer ControlA { get; }

        public ControlServer ControlB { get; }

        public EndpointConfig[] Endpoints { get; }

        public void Dispose()
        {
            this.ControlB.Dispose();
            this.ControlA.Dispose();
        }
    }

    private sealed class SocketServerCluster : IDisposable
    {
        public SocketServerCluster(IReadOnlyList<TcpServer> servers)
        {
            this.Servers = servers;
        }

        public IReadOnlyList<TcpServer> Servers { get; }

        public List<ControlServerReporter> Reporters { get; } = new();

        public void AttachReporters(EndpointConfig[] controlEndpoints)
        {
            foreach (TcpServer server in this.Servers)
            {
                this.Reporters.Add(CreateReporter(server, controlEndpoints));
            }
        }

        public Task RegisterAsync()
        {
            return Task.WhenAll(this.Reporters.Select(reporter => reporter.RegisterAsync()));
        }

        public void StartHeartbeatLoops(TimeSpan interval)
        {
            foreach (ControlServerReporter reporter in this.Reporters)
            {
                reporter.StartHeartbeatLoop(interval);
            }
        }

        public void Dispose()
        {
            foreach (ControlServerReporter reporter in this.Reporters)
            {
                reporter.Dispose();
            }

            foreach (TcpServer server in this.Servers)
            {
                server.Dispose();
            }
        }
    }

    private static async Task AcceptAndHoldSocketsAsync(
        NetSocket listener,
        List<NetSocket> acceptedSockets,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                NetSocket socket = await SocketAsyncEventArgsTransport.AcceptAsync(listener);
                if (socket != null)
                {
                    lock (acceptedSockets)
                    {
                        acceptedSockets.Add(socket);
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
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

    private static async Task WithTimeoutAsync(Task task)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        if (completed != task)
        {
            Assert.Fail("Timed out waiting for task.");
        }

        await task;
    }
}
