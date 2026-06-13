using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Diagnostics;
using SocketCommon.Model;
using SocketControl.Model;

namespace SocketTests.Model;

[TestClass]
public class ControlProtocolTests
{
    [TestMethod]
    public void RouteRequestEncodeDecodeTest()
    {
        RouteRequest request = new()
        {
            ClientId = 77,
            PreferredServerId = 2,
            RoutingPolicy = "MostAvailableConnections"
        };

        SocketMessageFrame frame = ControlProtocol.CreateFrame(77, ControlMessageIds.RouteRequest, request);
        bool decoded = ControlProtocol.TryDecode(frame, ControlMessageIds.RouteRequest, out RouteRequest decodedRequest);

        Assert.IsTrue(decoded);
        Assert.AreNotEqual((byte)'{', frame.Payload[0]);
        Assert.IsTrue(frame.Payload.Length < Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)));
        Assert.AreEqual((uint)77, decodedRequest.ClientId);
        Assert.AreEqual(2, decodedRequest.PreferredServerId);
        Assert.AreEqual("MostAvailableConnections", decodedRequest.RoutingPolicy);
    }

    [TestMethod]
    public void ClientMessageEncodeDecodeUsesBinaryPayloadTest()
    {
        ClientMessageSendRequest request = ClientMessageProtocol.CreateSendRequest(11, 12, "hello");

        SocketMessageFrame frame = ClientMessageProtocol.CreateFrame(11, ClientMessageIds.ClientMessageSend, request);
        bool decoded = ClientMessageProtocol.TryDecodeSendRequest(frame, out ClientMessageSendRequest decodedRequest);

        Assert.IsTrue(decoded);
        Assert.AreNotEqual((byte)'{', frame.Payload[0]);
        Assert.IsTrue(frame.Payload.Length < Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request)));
        Assert.AreEqual((uint)11, decodedRequest.SourceClientId);
        Assert.AreEqual((uint)12, decodedRequest.TargetClientId);
        Assert.AreEqual("hello", decodedRequest.Content);
    }

    [TestMethod]
    public void ClientRegisterAckEncodeDecodePreservesRetryAfterSecondsTest()
    {
        ClientRegisterAck ack = new()
        {
            ClientId = 11,
            Success = false,
            ErrorMessage = "Duplicate clientId is already connected.",
            RetryAfterSeconds = 90
        };

        SocketMessageFrame frame = ClientMessageProtocol.CreateFrame(11, ClientMessageIds.ClientRegisterAck, ack);
        bool decoded = ClientMessageProtocol.TryDecodeRegisterAck(frame, out ClientRegisterAck decodedAck);

        Assert.IsTrue(decoded);
        Assert.AreEqual((uint)11, decodedAck.ClientId);
        Assert.IsFalse(decodedAck.Success);
        Assert.AreEqual("Duplicate clientId is already connected.", decodedAck.ErrorMessage);
        Assert.AreEqual(90, decodedAck.RetryAfterSeconds);
    }

    [TestMethod]
    public void ControlRelayBatchEncodeDecodePreservesItemsTest()
    {
        SessionEventMessage session = new()
        {
            ClusterId = "socket-cluster-1",
            SessionId = 10,
            ClientId = 99,
            ServerId = 1,
            InstanceId = "server-1",
            State = "Connected",
            Version = 7,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastReceivedAt = DateTimeOffset.UtcNow
        };
        SocketMessageFrame sessionFrame = ControlProtocol.CreateFrame(0, ControlMessageIds.SessionSummaryUpsert, session);
        ControlRelayBatchMessage batch = new()
        {
            Items =
            {
                new ControlRelayBatchItem
                {
                    ClientId = sessionFrame.ClientId,
                    MessageId = sessionFrame.MessageId,
                    Payload = sessionFrame.Payload,
                    PayloadType = nameof(SessionEventMessage)
                }
            }
        };

        SocketMessageFrame frame = ControlProtocol.CreateFrame(0, ControlMessageIds.ControlRelayBatch, batch);
        bool decoded = ControlProtocol.TryDecode(frame, ControlMessageIds.ControlRelayBatch, out ControlRelayBatchMessage decodedBatch);

        Assert.IsTrue(decoded);
        Assert.AreEqual(1, decodedBatch.Items.Count);
        Assert.AreEqual(ControlMessageIds.SessionSummaryUpsert, decodedBatch.Items[0].MessageId);
        SocketMessageFrame decodedItemFrame = new(decodedBatch.Items[0].ClientId, decodedBatch.Items[0].MessageId, decodedBatch.Items[0].Payload);
        Assert.IsTrue(ControlProtocol.TryDecode(decodedItemFrame, ControlMessageIds.SessionSummaryUpsert, out SessionEventMessage decodedSession));
        Assert.AreEqual((uint)99, decodedSession.ClientId);
        Assert.AreEqual("server-1", decodedSession.InstanceId);
    }

    [TestMethod]
    public void ControlRelayBatchFitCheckRejectsOversizedBatchTest()
    {
        ControlRelayBatchItem[] items = Enumerable.Range(1, 80)
            .Select(index => new ControlRelayBatchItem
            {
                ClientId = (uint)index,
                MessageId = ControlMessageIds.SessionSummaryUpsert,
                Payload = Enumerable.Repeat((byte)index, 80).ToArray(),
                PayloadType = nameof(SessionEventMessage)
            })
            .ToArray();

        Assert.IsFalse(ControlServer.CanFitControlRelayBatchFrame(items, DateTimeOffset.UtcNow));
        Assert.IsTrue(ControlServer.CanFitControlRelayBatchFrame(items.Take(10).ToArray(), DateTimeOffset.UtcNow));
    }

    [TestMethod]
    public void ServerRelayBatchEncodeDecodePreservesResultsTest()
    {
        ClientMessageSendRequest request = ClientMessageProtocol.CreateSendRequest(11, 22, "hello");
        ServerRelayBatchMessage batch = new()
        {
            Items =
            {
                ClientMessageProtocol.CreateRelay("socket-cluster-1", "server-1", request)
            }
        };

        SocketMessageFrame frame = ClientMessageProtocol.CreateFrame(11, ServerRelayMessageIds.ServerRelayBatch, batch);
        Assert.IsTrue(ClientMessageProtocol.TryDecodeRelayBatch(frame, out ServerRelayBatchMessage decodedBatch));
        Assert.AreEqual(1, decodedBatch.Items.Count);
        Assert.AreEqual(request.MessageToken, decodedBatch.Items[0].MessageToken);
        Assert.AreEqual((uint)22, decodedBatch.Items[0].TargetClientId);

        ServerRelayBatchResult result = new()
        {
            Items =
            {
                new ServerRelayBatchResultItem
                {
                    ItemIndex = 0,
                    MessageToken = request.MessageToken,
                    Success = true,
                    TargetInstanceId = "server-2"
                }
            }
        };
        SocketMessageFrame resultFrame = ClientMessageProtocol.CreateFrame(11, ServerRelayMessageIds.ServerRelayBatchResult, result);
        Assert.IsTrue(ClientMessageProtocol.TryDecodeRelayBatchResult(resultFrame, out ServerRelayBatchResult decodedResult));
        Assert.AreEqual(1, decodedResult.Items.Count);
        Assert.AreEqual(0, decodedResult.Items[0].ItemIndex);
        Assert.IsTrue(decodedResult.Items[0].Success);
        Assert.AreEqual("server-2", decodedResult.Items[0].TargetInstanceId);
    }

    [TestMethod]
    public void ClusterStatusSnapshotEncodeDecodePreservesControlServerResourceUsageTest()
    {
        ClusterStatusSnapshot status = new()
        {
            ServerCount = 1,
            HealthyServerCount = 1,
            TotalCurrentConnections = 2,
            ControlServerResourceUsage = new ResourceUsageSnapshot
            {
                CpuUsagePercent = 12.5,
                MemoryUsagePercent = 45.5,
                StorageUsagePercent = 67.5,
                CapturedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
            },
            UpdatedAt = DateTimeOffset.UtcNow
        };

        SocketMessageFrame frame = ControlProtocol.CreateFrame(0, ControlMessageIds.RegistrySnapshotResponse, status);
        bool decoded = ControlProtocol.TryDecode(frame, ControlMessageIds.RegistrySnapshotResponse, out ClusterStatusSnapshot decodedStatus);

        Assert.IsTrue(decoded);
        Assert.IsNotNull(decodedStatus.ControlServerResourceUsage);
        Assert.AreEqual(12.5, decodedStatus.ControlServerResourceUsage!.CpuUsagePercent, 0.0001);
        Assert.AreEqual(45.5, decodedStatus.ControlServerResourceUsage!.MemoryUsagePercent, 0.0001);
        Assert.AreEqual(67.5, decodedStatus.ControlServerResourceUsage!.StorageUsagePercent, 0.0001);
        Assert.IsTrue(decodedStatus.ControlServerResourceUsage!.CapturedAt > DateTimeOffset.MinValue);
    }

    [TestMethod]
    public void ClusterStatusSnapshotEncodeDecodeKeepsMissingControlServerResourceUsageUnknownTest()
    {
        ClusterStatusSnapshot status = new()
        {
            ServerCount = 1,
            HealthyServerCount = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        SocketMessageFrame frame = ControlProtocol.CreateFrame(0, ControlMessageIds.RegistrySnapshotResponse, status);
        bool decoded = ControlProtocol.TryDecode(frame, ControlMessageIds.RegistrySnapshotResponse, out ClusterStatusSnapshot decodedStatus);

        Assert.IsTrue(decoded);
        Assert.IsNull(decodedStatus.ControlServerResourceUsage);
    }

    [TestMethod]
    public void SocketClientConnectionConfigDefaultsReconnectDelaysTest()
    {
        SocketClientConnectionConfig config = new();

        Assert.AreEqual(30, config.ReconnectRetrySeconds);
        Assert.AreEqual(90, config.DuplicateRejectBackoffSeconds);
    }

    [TestMethod]
    public void SocketFactoryResolveAddressSupportsDnsHostTest()
    {
        Assert.AreEqual(
            AddressFamily.InterNetwork,
            SocketFactory.ResolveAddress("localhost", AddressFamily.InterNetwork).AddressFamily);
    }

    [TestMethod]
    public void BackendRegistrySelectsMostAvailableServerTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateRegister(2, "server-2", 5201, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 90, 100), "control-1", new ControlHealthThreshold());
        registry.Upsert(CreateHeartbeat(2, "server-2", 5201, 10, 100), "control-1", new ControlHealthThreshold());

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(5));

        Assert.IsTrue(response.Success);
        Assert.AreEqual(2, response.ServerId);
        ClusterStatusSnapshot status = registry.GetStatus();
        Assert.AreEqual(200, status.TotalMaxConnections);
        Assert.AreEqual(100, status.TotalCurrentConnections);
        Assert.AreEqual(1, status.TotalReservedConnections);
        Assert.AreEqual(99, status.TotalAvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryStoresHeartbeatTrafficCountersTest()
    {
        BackendServerRegistry registry = new();
        ServerHeartbeatRequest heartbeat = CreateHeartbeat(1, "server-1", 5101, 2, 10);
        heartbeat.TotalAcceptedClients = 3;
        heartbeat.TotalClosedClients = 1;
        heartbeat.TotalRejectedClients = 2;
        heartbeat.TotalIdleTimeoutClients = 1;
        heartbeat.TotalReceivedMessages = 11;
        heartbeat.TotalSentMessages = 12;
        heartbeat.TotalReceivedMessageBytes = 130;
        heartbeat.TotalSentMessageBytes = 220;
        heartbeat.ListenBacklog = 128;
        heartbeat.PendingAcceptCount = 4;
        heartbeat.IdleTimeoutSeconds = 90;
        heartbeat.NoDelay = true;
        heartbeat.MaxPayloadLength = 4096;
        heartbeat.SocketAsyncEventArgsAvailableCount = 7;
        heartbeat.SocketAsyncEventArgsTotalCreatedCount = 9;
        heartbeat.SocketAsyncEventArgsInUseCount = 3;
        heartbeat.SocketAsyncEventArgsHighWatermarkInUseCount = 5;
        heartbeat.SocketAsyncEventArgsGrowthCount = 1;

        SocketMessageFrame heartbeatFrame = ControlProtocol.CreateFrame(0, ControlMessageIds.ServerHeartbeat, heartbeat);
        Assert.IsTrue(ControlProtocol.TryDecode(heartbeatFrame, ControlMessageIds.ServerHeartbeat, out ServerHeartbeatRequest decodedHeartbeat));
        BackendServerSnapshot snapshot = registry.Upsert(decodedHeartbeat, "control-1", new ControlHealthThreshold());

        Assert.AreEqual(3, snapshot.TotalAcceptedClients);
        Assert.AreEqual(1, snapshot.TotalClosedClients);
        Assert.AreEqual(2, snapshot.TotalRejectedClients);
        Assert.AreEqual(1, snapshot.TotalIdleTimeoutClients);
        Assert.AreEqual(11, snapshot.TotalReceivedMessages);
        Assert.AreEqual(12, snapshot.TotalSentMessages);
        Assert.AreEqual(130, snapshot.TotalReceivedMessageBytes);
        Assert.AreEqual(220, snapshot.TotalSentMessageBytes);
        Assert.AreEqual(128, snapshot.ListenBacklog);
        Assert.AreEqual(4, snapshot.PendingAcceptCount);
        Assert.AreEqual(90, snapshot.IdleTimeoutSeconds);
        Assert.IsTrue(snapshot.NoDelay);
        Assert.AreEqual(4096, snapshot.MaxPayloadLength);
        Assert.AreEqual(7, snapshot.SocketAsyncEventArgsAvailableCount);
        Assert.AreEqual(9, snapshot.SocketAsyncEventArgsTotalCreatedCount);
        Assert.AreEqual(3, snapshot.SocketAsyncEventArgsInUseCount);
        Assert.AreEqual(5, snapshot.SocketAsyncEventArgsHighWatermarkInUseCount);
        Assert.AreEqual(1, snapshot.SocketAsyncEventArgsGrowthCount);

        SocketMessageFrame snapshotFrame = ControlProtocol.CreateFrame(0, ControlMessageIds.ServerRegistryUpsert, snapshot);
        Assert.IsTrue(ControlProtocol.TryDecode(snapshotFrame, ControlMessageIds.ServerRegistryUpsert, out BackendServerSnapshot decodedSnapshot));
        Assert.AreEqual(130, decodedSnapshot.TotalReceivedMessageBytes);
        Assert.AreEqual(220, decodedSnapshot.TotalSentMessageBytes);
        Assert.AreEqual(128, decodedSnapshot.ListenBacklog);
        Assert.IsTrue(decodedSnapshot.NoDelay);
        Assert.AreEqual(4096, decodedSnapshot.MaxPayloadLength);
        Assert.AreEqual(3, decodedSnapshot.SocketAsyncEventArgsInUseCount);
    }

    [TestMethod]
    public void BackendRegistryReportsRegisteredAndStaleConnectionCountsTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 10), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 3, 10), "control-1", new ControlHealthThreshold());
        registry.UpsertSession(new SessionEventMessage
        {
            ClusterId = "socket-cluster-1",
            ServerId = 1,
            InstanceId = "server-1",
            SessionId = 700,
            ClientId = 101,
            State = "Connected",
            Version = 1,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastReceivedAt = DateTimeOffset.UtcNow
        });
        registry.UpsertSession(new SessionEventMessage
        {
            ClusterId = "socket-cluster-1",
            ServerId = 1,
            InstanceId = "server-1",
            SessionId = 701,
            ClientId = 102,
            State = "ServerDisconnected",
            Version = 2,
            ConnectedAt = DateTimeOffset.UtcNow,
            LastReceivedAt = DateTimeOffset.UtcNow
        });

        ClusterStatusSnapshot status = registry.GetStatus();
        BackendServerSnapshot snapshot = status.Servers.Single();

        Assert.AreEqual(1, status.TotalSessionCount);
        Assert.AreEqual(3, snapshot.CurrentConnections);
        Assert.AreEqual(1, snapshot.RegisteredSessionCount);
        Assert.AreEqual(2, snapshot.StaleConnectionCount);

        SocketMessageFrame snapshotFrame = ControlProtocol.CreateFrame(0, ControlMessageIds.ServerRegistryUpsert, snapshot);
        Assert.IsTrue(ControlProtocol.TryDecode(snapshotFrame, ControlMessageIds.ServerRegistryUpsert, out BackendServerSnapshot decodedSnapshot));
        Assert.AreEqual(1, decodedSnapshot.RegisteredSessionCount);
        Assert.AreEqual(2, decodedSnapshot.StaleConnectionCount);
    }

    [TestMethod]
    public void BackendRegistryServerRegisterDoesNotRefreshExpiredHeartbeatTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(30));
        ServerRegisterRequest register = CreateRegister(1, "server-1", 5101, 10);
        register.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        BackendServerSnapshot registered = registry.Upsert(register, "control-1");
        ClusterStatusSnapshot status = registry.GetStatus();

        Assert.AreEqual(register.StartedAt, registered.LastHeartbeatAt);
        Assert.AreEqual(0, status.HealthyServerCount);
        Assert.AreEqual(0, status.TotalAvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryServerRegisterRefreshPreservesHeartbeatCountersTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(90));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        DateTimeOffset heartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        ServerRegisterRequest register = CreateRegister(1, "server-1", 5101, 10);
        register.StartedAt = startedAt;

        registry.Upsert(register, "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 8, 10, heartbeatAt), "control-1", new ControlHealthThreshold());
        registry.Upsert(register, "control-1");

        BackendServerSnapshot snapshot = registry.Servers.Single();
        Assert.AreEqual(heartbeatAt, snapshot.LastHeartbeatAt);
        Assert.AreEqual(8, snapshot.CurrentConnections);
        Assert.AreEqual(2, snapshot.AvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryRandomizesOnlyEqualScoreRouteCandidatesTest()
    {
        BackendServerSnapshot[] candidates =
        [
            CreateSnapshot(1, "server-a", availableConnections: 10, currentConnections: 0),
            CreateSnapshot(2, "server-b", availableConnections: 10, currentConnections: 0),
            CreateSnapshot(3, "server-c", availableConnections: 9, currentConnections: 0),
            CreateSnapshot(4, "server-d", availableConnections: 10, currentConnections: 1)
        ];

        BackendServerSnapshot? selected = BackendServerRegistry.SelectRouteCandidate(
            candidates,
            maxExclusive =>
            {
                Assert.AreEqual(2, maxExclusive);
                return 1;
            });

        Assert.IsNotNull(selected);
        Assert.AreEqual("server-b", selected.InstanceId);
    }

    [TestMethod]
    public void BackendRegistryReservationUpsertIsIdempotentTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 10), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 2, 10), "control-1", new ControlHealthThreshold());

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 77 }, "control-1", TimeSpan.FromSeconds(30));
        Assert.IsTrue(response.Success);
        RouteReservationMessage reservation = registry.Reservations.Single();

        registry.UpsertReservation(reservation);
        registry.UpsertReservation(reservation);
        ClusterStatusSnapshot reservedStatus = registry.GetStatus();

        Assert.AreEqual(1, reservedStatus.TotalReservedConnections);
        Assert.AreEqual(7, reservedStatus.TotalAvailableConnections);

        RouteReservationMessage? releasedReservation = registry.ReleaseReservationFor(77, "server-1");
        ClusterStatusSnapshot releasedStatus = registry.GetStatus();

        Assert.IsNotNull(releasedReservation);
        Assert.AreEqual(0, releasedStatus.TotalReservedConnections);
        Assert.AreEqual(8, releasedStatus.TotalAvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryStatusRecalculatesStaleReservationCountTest()
    {
        BackendServerRegistry registry = new();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        registry.UpsertPeerSnapshot(new BackendServerSnapshot
        {
            ClusterId = "socket-cluster-1",
            SourceControlNodeId = "control-peer",
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            MaxConnections = 10,
            CurrentConnections = 3,
            ReservedConnections = 2,
            AvailableConnections = 5,
            Health = ServerHealthState.Healthy,
            ResourceUsage = new ResourceUsageSnapshot(),
            Version = 10,
            StartedAt = now.AddMinutes(-1),
            LastHeartbeatAt = now,
            UpdatedAt = now
        });

        ClusterStatusSnapshot status = registry.GetStatus();

        Assert.AreEqual(3, status.TotalCurrentConnections);
        Assert.AreEqual(0, status.TotalReservedConnections);
        Assert.AreEqual(7, status.TotalAvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryExcludesExpiredHeartbeatTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromMilliseconds(10));
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100, DateTimeOffset.UtcNow.AddSeconds(-1)), "control-1", new ControlHealthThreshold());

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(5));

        Assert.IsFalse(response.Success);
    }

    [TestMethod]
    public void BackendRegistryPeerSnapshotUsesHeartbeatRecencyOverLocalVersionTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromMilliseconds(10));
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100, DateTimeOffset.UtcNow.AddSeconds(-1)), "control-1", new ControlHealthThreshold());
        _ = registry.GetStatus();

        BackendServerSnapshot expired = registry.Servers.Single();
        Assert.AreEqual(ServerHealthState.Unhealthy, expired.Health);
        Assert.AreEqual(0, expired.CurrentConnections);

        DateTimeOffset freshHeartbeat = DateTimeOffset.UtcNow;
        BackendServerSnapshot peerSnapshot = new()
        {
            ClusterId = "socket-cluster-1",
            SourceControlNodeId = "control-peer",
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            PortRangeStart = 5101,
            PortRangeEnd = 5101,
            MaxConnections = 100,
            CurrentConnections = 4,
            ReservedConnections = 0,
            AvailableConnections = 96,
            Health = ServerHealthState.Healthy,
            ResourceUsage = new ResourceUsageSnapshot(),
            Version = Math.Max(1, expired.Version - 1),
            StartedAt = freshHeartbeat.AddMinutes(-1),
            LastHeartbeatAt = freshHeartbeat,
            UpdatedAt = freshHeartbeat
        };

        registry.UpsertPeerSnapshot(peerSnapshot);
        ClusterStatusSnapshot status = registry.GetStatus();

        Assert.AreEqual(1, status.HealthyServerCount);
        Assert.AreEqual(4, status.TotalCurrentConnections);
        Assert.AreEqual(96, status.TotalAvailableConnections);
    }

    [TestMethod]
    public void BackendRegistryClearsInstanceSessionsWhenServerRestartsTest()
    {
        BackendServerRegistry registry = new();
        ServerRegisterRequest firstRegister = CreateRegister(1, "server-1", 5101, 100);
        firstRegister.StartedAt = DateTimeOffset.UtcNow.AddSeconds(-10);
        registry.Upsert(firstRegister, "control-1");
        registry.UpsertSession(new SessionEventMessage
        {
            ClusterId = "socket-cluster-1",
            ClientId = 77,
            ServerId = 1,
            InstanceId = "server-1",
            SessionId = 700,
            State = "Connected",
            Version = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()
        });

        Assert.AreEqual(1, registry.GetStatus().TotalSessionCount);

        ServerRegisterRequest restartRegister = CreateRegister(1, "server-1", 5101, 100);
        restartRegister.StartedAt = DateTimeOffset.UtcNow;
        registry.Upsert(restartRegister, "control-1");
        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });

        Assert.AreEqual(0, status.TotalSessionCount);
        Assert.IsFalse(location.Success);
    }

    [TestMethod]
    public void BackendRegistryDoesNotReviveClosedSessionWithStaleUpdateTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100), "control-1", new ControlHealthThreshold());
        DateTimeOffset connectedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        SessionEventMessage opened = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        SessionEventMessage closed = CreateSessionEvent("server-1", 700, 77, "Closed", 2000);
        SessionEventMessage lateUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        opened.ConnectedAt = connectedAt;
        closed.ConnectedAt = connectedAt;
        lateUpdate.ConnectedAt = connectedAt;

        registry.UpsertSession(opened);
        registry.RemoveSession(closed);
        registry.UpsertSession(lateUpdate);
        registry.UpsertClientLocation(new ClientLocationMessage
        {
            ClusterId = "socket-cluster-1",
            ClientId = 77,
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            SessionId = 700,
            State = "Updated",
            Version = 1500,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });

        Assert.AreEqual(0, status.TotalSessionCount);
        Assert.IsFalse(location.Success);
    }

    [TestMethod]
    public void BackendRegistryAllowsNewerSessionAfterCloseTombstoneTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());
        DateTimeOffset oldConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        SessionEventMessage oldSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        oldSession.ConnectedAt = oldConnectedAt;
        registry.UpsertSession(oldSession);
        SessionEventMessage oldClose = CreateSessionEvent("server-1", 700, 77, "Closed", 2000);
        oldClose.ConnectedAt = oldConnectedAt;
        registry.RemoveSession(oldClose);
        SessionEventMessage newSession = CreateSessionEvent("server-1", 700, 78, "Updated", 2500);
        newSession.ConnectedAt = DateTimeOffset.UtcNow;
        registry.UpsertSession(newSession);
        SessionEventMessage staleUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        staleUpdate.ConnectedAt = oldConnectedAt;
        registry.UpsertSession(staleUpdate);

        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 78
        });

        Assert.AreEqual(1, status.TotalSessionCount);
        Assert.IsTrue(location.Success);
        Assert.AreEqual(78u, location.TargetClientId);
    }

    [TestMethod]
    public void BackendRegistryZeroHeartbeatOnlyPrunesSessionsInactiveBeforeCutoffTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        SessionEventMessage oldSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        oldSession.LastReceivedAt = cutoff.AddSeconds(-1);
        registry.UpsertSession(oldSession);

        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100, cutoff), "control-1", new ControlHealthThreshold());
        SessionEventMessage staleUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        staleUpdate.LastReceivedAt = cutoff.AddTicks(-1);
        registry.UpsertSession(staleUpdate);

        Assert.AreEqual(0, registry.GetStatus().TotalSessionCount);
        SessionEventMessage activeUpdate = CreateSessionEvent("server-1", 701, 77, "Updated", 2000);
        activeUpdate.LastReceivedAt = cutoff;
        registry.UpsertSession(activeUpdate);

        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });
        Assert.AreEqual(1, status.TotalSessionCount);
        Assert.IsTrue(location.Success);
    }

    [TestMethod]
    public void BackendRegistryPeerZeroSnapshotOnlyPrunesSessionsInactiveBeforeCutoffTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        SessionEventMessage oldSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        oldSession.LastReceivedAt = cutoff.AddSeconds(-1);
        registry.UpsertSession(oldSession);

        BackendServerSnapshot zeroSnapshot = CreateSnapshot(1, "server-1", 100, 0);
        zeroSnapshot.LastHeartbeatAt = cutoff;
        registry.ImportSnapshot(new ClusterStatusSnapshot
        {
            Servers = new[] { zeroSnapshot }
        });
        SessionEventMessage activeUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        activeUpdate.LastReceivedAt = cutoff.AddSeconds(1);
        registry.UpsertSession(activeUpdate);

        Assert.AreEqual(1, registry.GetStatus().TotalSessionCount);
    }

    [TestMethod]
    public void BackendRegistryPeerZeroSnapshotRejectsUnknownSessionBeforeCutoffTest()
    {
        BackendServerRegistry registry = new();
        DateTimeOffset cutoff = DateTimeOffset.UtcNow;
        BackendServerSnapshot zeroSnapshot = CreateSnapshot(1, "server-1", 100, 0);
        zeroSnapshot.LastHeartbeatAt = cutoff;
        registry.ImportSnapshot(new ClusterStatusSnapshot
        {
            Servers = new[] { zeroSnapshot }
        });

        SessionEventMessage staleUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        staleUpdate.LastReceivedAt = cutoff.AddTicks(-1);
        registry.UpsertSession(staleUpdate);

        Assert.AreEqual(0, registry.GetStatus().TotalSessionCount);
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });
        Assert.IsFalse(location.Success);

        SessionEventMessage boundaryUpdate = CreateSessionEvent("server-1", 701, 78, "Updated", 1500);
        boundaryUpdate.LastReceivedAt = cutoff;
        registry.UpsertSession(boundaryUpdate);

        Assert.AreEqual(1, registry.GetStatus().TotalSessionCount);
    }

    [TestMethod]
    public void BackendRegistryStatusAndClientLookupPruneExpiredSessionsTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromMilliseconds(10));
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());
        SessionEventMessage expired = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        expired.LastReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        registry.UpsertSession(expired);

        Assert.AreEqual(0, registry.GetStatus().TotalSessionCount);
        SessionEventMessage staleUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        staleUpdate.ConnectedAt = expired.ConnectedAt;
        staleUpdate.LastReceivedAt = expired.LastReceivedAt;
        registry.UpsertSession(staleUpdate);
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });

        Assert.IsFalse(location.Success);
    }

    [TestMethod]
    public void BackendRegistryAllowsFutureSessionAfterMaxVersionTombstoneTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());

        DateTimeOffset oldConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        SessionEventMessage oldSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        oldSession.ConnectedAt = oldConnectedAt;
        oldSession.LastReceivedAt = oldConnectedAt.AddSeconds(30);
        registry.UpsertSession(oldSession);

        SessionEventMessage oldClose = CreateSessionEvent("server-1", 700, 77, "Closed", long.MaxValue);
        oldClose.ConnectedAt = oldSession.ConnectedAt;
        oldClose.LastReceivedAt = oldSession.LastReceivedAt;
        registry.RemoveSession(oldClose);

        SessionEventMessage newSession = CreateSessionEvent("server-1", 700, 78, "Updated", 1500);
        newSession.ConnectedAt = DateTimeOffset.UtcNow;
        newSession.LastReceivedAt = newSession.ConnectedAt;
        registry.UpsertSession(newSession);

        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 78
        });

        Assert.AreEqual(1, status.TotalSessionCount);
        Assert.IsTrue(location.Success);
    }

    [TestMethod]
    public void BackendRegistryRevivesSameConnectionWhenNewerUpdateArrivesAfterCleanupTombstoneTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(30));
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());
        DateTimeOffset oldConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        SessionEventMessage expired = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        expired.ConnectedAt = oldConnectedAt;
        expired.LastReceivedAt = oldConnectedAt.AddSeconds(30);
        registry.UpsertSession(expired);

        Assert.AreEqual(0, registry.GetStatus().TotalSessionCount);

        SessionEventMessage lateUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 999999);
        lateUpdate.ConnectedAt = expired.ConnectedAt;
        lateUpdate.LastReceivedAt = DateTimeOffset.UtcNow;
        registry.UpsertSession(lateUpdate);

        Assert.AreEqual(1, registry.GetStatus().TotalSessionCount);

        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });
        Assert.IsTrue(location.Success);
    }

    [TestMethod]
    public void BackendRegistryKeepsReusedSessionWhenOldConnectionEventsArriveLateTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());

        DateTimeOffset oldConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        SessionEventMessage oldSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
        oldSession.ConnectedAt = oldConnectedAt;
        oldSession.LastReceivedAt = oldConnectedAt.AddSeconds(30);
        registry.UpsertSession(oldSession);

        SessionEventMessage oldClose = CreateSessionEvent("server-1", 700, 77, "Closed", 2000);
        oldClose.ConnectedAt = oldSession.ConnectedAt;
        oldClose.LastReceivedAt = oldSession.LastReceivedAt;
        registry.RemoveSession(oldClose);

        SessionEventMessage newSession = CreateSessionEvent("server-1", 700, 78, "Updated", 1500);
        newSession.ConnectedAt = DateTimeOffset.UtcNow;
        newSession.LastReceivedAt = newSession.ConnectedAt;
        registry.UpsertSession(newSession);

        SessionEventMessage lateOldUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 999999);
        lateOldUpdate.ConnectedAt = oldSession.ConnectedAt;
        lateOldUpdate.LastReceivedAt = DateTimeOffset.UtcNow;
        registry.UpsertSession(lateOldUpdate);
        SessionEventMessage lateOldClose = CreateSessionEvent("server-1", 700, 77, "Closed", 1000000);
        lateOldClose.ConnectedAt = oldSession.ConnectedAt;
        lateOldClose.LastReceivedAt = lateOldUpdate.LastReceivedAt;
        registry.RemoveSession(lateOldClose);

        ClusterStatusSnapshot status = registry.GetStatus();
        ClientLocationResponse oldLocation = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });
        ClientLocationResponse newLocation = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 78
        });

        Assert.AreEqual(1, status.TotalSessionCount);
        Assert.IsFalse(oldLocation.Success);
        Assert.IsTrue(newLocation.Success);
    }

    [TestMethod]
    public void BackendRegistryRejectsStaleRelayedLocationAfterSessionReuseTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());

        SessionEventMessage newSession = CreateSessionEvent("server-1", 700, 78, "Updated", 1500);
        newSession.ConnectedAt = DateTimeOffset.UtcNow;
        registry.UpsertSession(newSession);

        registry.UpsertClientLocation(new ClientLocationMessage
        {
            ClusterId = "socket-cluster-1",
            ClientId = 77,
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            SessionId = 700,
            State = "Updated",
            Version = 999999,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ClientLocationResponse oldLocation = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });
        ClientLocationResponse newLocation = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 78
        });

        Assert.IsFalse(oldLocation.Success);
        Assert.IsTrue(newLocation.Success);
    }

    [TestMethod]
    public void BackendRegistryIgnoresStaleRelayedLocationRemoveForLiveSessionTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());

        SessionEventMessage newSession = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
        newSession.ConnectedAt = DateTimeOffset.UtcNow;
        registry.UpsertSession(newSession);
        registry.RemoveClientLocation(new ClientLocationMessage
        {
            ClusterId = "socket-cluster-1",
            ClientId = 77,
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            SessionId = 700,
            State = "Closed",
            Version = 999999,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });

        Assert.IsTrue(location.Success);
    }

    [TestMethod]
    public void BackendRegistryFileStoreRestoresSessionCloseTombstoneTest()
    {
        string path = CreateRegistryPath();
        try
        {
            BackendServerRegistry registry = new(TimeSpan.FromSeconds(90), new FileBackendRegistryStore(path));
            registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
            registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100), "control-1", new ControlHealthThreshold());
            DateTimeOffset connectedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            SessionEventMessage opened = CreateSessionEvent("server-1", 700, 77, "Updated", 1000);
            opened.ConnectedAt = connectedAt;
            registry.UpsertSession(opened);
            SessionEventMessage closed = CreateSessionEvent("server-1", 700, 77, "Closed", 2000);
            closed.ConnectedAt = connectedAt;
            registry.RemoveSession(closed);

            BackendServerRegistry restored = new(TimeSpan.FromSeconds(90), new FileBackendRegistryStore(path));
            SessionEventMessage lateUpdate = CreateSessionEvent("server-1", 700, 77, "Updated", 1500);
            lateUpdate.ConnectedAt = connectedAt;
            restored.UpsertSession(lateUpdate);

            Assert.AreEqual(0, restored.GetStatus().TotalSessionCount);
        }
        finally
        {
            DeleteRegistryPath(path);
        }
    }

    [TestMethod]
    public void BackendRegistryClearsInstanceSessionsWhenPeerSnapshotShowsRestartTest()
    {
        BackendServerRegistry registry = new();
        DateTimeOffset firstStart = DateTimeOffset.UtcNow.AddSeconds(-10);
        registry.UpsertPeerSnapshot(new BackendServerSnapshot
        {
            ClusterId = "socket-cluster-1",
            SourceControlNodeId = "control-peer",
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            MaxConnections = 100,
            CurrentConnections = 1,
            AvailableConnections = 99,
            Health = ServerHealthState.Healthy,
            ResourceUsage = new ResourceUsageSnapshot(),
            Version = 1,
            StartedAt = firstStart,
            LastHeartbeatAt = firstStart.AddSeconds(1),
            UpdatedAt = firstStart.AddSeconds(1)
        });
        registry.UpsertSession(new SessionEventMessage
        {
            ClusterId = "socket-cluster-1",
            ClientId = 77,
            ServerId = 1,
            InstanceId = "server-1",
            SessionId = 700,
            State = "Connected",
            Version = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()
        });

        Assert.AreEqual(1, registry.GetStatus().TotalSessionCount);

        DateTimeOffset restarted = DateTimeOffset.UtcNow;
        registry.UpsertPeerSnapshot(new BackendServerSnapshot
        {
            ClusterId = "socket-cluster-1",
            SourceControlNodeId = "control-peer",
            ServerId = 1,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 5101,
            MaxConnections = 100,
            CurrentConnections = 0,
            AvailableConnections = 100,
            Health = ServerHealthState.Healthy,
            ResourceUsage = new ResourceUsageSnapshot(),
            Version = 2,
            StartedAt = restarted,
            LastHeartbeatAt = restarted,
            UpdatedAt = restarted
        });

        Assert.AreEqual(0, registry.GetStatus().TotalSessionCount);
    }

    [TestMethod]
    public void BackendRegistryExcludesDegradedServerTest()
    {
        BackendServerRegistry registry = new();
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        ServerHeartbeatRequest heartbeat = CreateHeartbeat(1, "server-1", 5101, 0, 100);
        heartbeat.ResourceUsage.CpuUsagePercent = 95;
        registry.Upsert(heartbeat, "control-1", new ControlHealthThreshold { DegradedCpuPercent = 85 });

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(5));

        Assert.IsFalse(response.Success);
    }

    [TestMethod]
    public void BackendRegistryServerRegisterRefreshPreservesHeartbeatHealthTest()
    {
        BackendServerRegistry registry = new();
        ServerRegisterRequest register = CreateRegister(1, "server-1", 5101, 100);
        registry.Upsert(register, "control-1");
        ServerHeartbeatRequest heartbeat = CreateHeartbeat(1, "server-1", 5101, 0, 100);
        heartbeat.ResourceUsage.CpuUsagePercent = 95;
        registry.Upsert(heartbeat, "control-1", new ControlHealthThreshold { DegradedCpuPercent = 85 });

        registry.Upsert(register, "control-1");
        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(5));

        Assert.IsFalse(response.Success);
        Assert.AreEqual(ServerHealthState.Degraded, registry.Servers.Single().Health);
    }

    [TestMethod]
    public void BackendRegistryFileStoreRestoresServerAndClientLocationTest()
    {
        string path = CreateRegistryPath();
        try
        {
            BackendServerRegistry registry = new(TimeSpan.FromSeconds(90), new FileBackendRegistryStore(path));
            registry.Upsert(CreateRegister(1, "server-1", 5101, 10), "control-1");
            registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 3, 10), "control-1", new ControlHealthThreshold());
            registry.UpsertSession(new SessionEventMessage
            {
                ClusterId = "socket-cluster-1",
                ClientId = 77,
                ServerId = 1,
                InstanceId = "server-1",
                SessionId = 700,
                State = "Connected",
                Version = 10
            });
            RouteResponse route = registry.Resolve(new RouteRequest { ClientId = 88 }, "control-1", TimeSpan.FromMinutes(1));

            Assert.IsTrue(route.Success);

            BackendServerRegistry restored = new(TimeSpan.FromSeconds(90), new FileBackendRegistryStore(path));
            ClusterStatusSnapshot status = restored.GetStatus();
            ClientLocationResponse location = restored.ResolveClientLocation(new ClientLocationRequest
            {
                SourceClientId = 1,
                TargetClientId = 77
            });

            Assert.AreEqual(1, status.ServerCount);
            Assert.AreEqual(1, status.HealthyServerCount);
            Assert.AreEqual(3, status.TotalCurrentConnections);
            Assert.AreEqual(0, status.TotalReservedConnections);
            Assert.AreEqual(7, status.TotalAvailableConnections);
            Assert.IsTrue(location.Success);
            Assert.AreEqual("server-1", location.InstanceId);
        }
        finally
        {
            DeleteRegistryPath(path);
        }
    }

    [TestMethod]
    public void BackendRegistryCoalescesRepeatedSessionUpdateSavesTest()
    {
        CountingBackendRegistryStore store = new();
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(90), store);
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 1, 100), "control-1", new ControlHealthThreshold());
        registry.UpsertSession(CreateSessionEvent("server-1", 700, 77, "opened", 1));
        int saveCountAfterOpen = store.SaveCount;

        for (int version = 2; version < 100; version++)
        {
            registry.UpsertSession(CreateSessionEvent("server-1", 700, 77, "updated", version));
        }

        ClientLocationResponse location = registry.ResolveClientLocation(new ClientLocationRequest
        {
            SourceClientId = 1,
            TargetClientId = 77
        });

        Assert.IsTrue(location.Success);
        Assert.AreEqual(saveCountAfterOpen, store.SaveCount);
    }

    [TestMethod]
    public void BackendRegistryCoalescesRouteReservationSavesTest()
    {
        CountingBackendRegistryStore store = new();
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(90), store);
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        int saveCountAfterRegister = store.SaveCount;

        for (uint clientId = 1; clientId <= 50; clientId++)
        {
            RouteResponse response = registry.Resolve(new RouteRequest { ClientId = clientId }, "control-1", TimeSpan.FromSeconds(10));
            Assert.IsTrue(response.Success);
        }

        Assert.AreEqual(saveCountAfterRegister, store.SaveCount);
    }

    [TestMethod]
    public void BackendRegistryDoesNotPersistRouteReservationsTest()
    {
        CountingBackendRegistryStore store = new();
        BackendServerRegistry registry = new(TimeSpan.FromSeconds(90), store);
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(10));

        Assert.IsTrue(response.Success);
        Assert.AreEqual(1, registry.Reservations.Count);
        Assert.AreEqual(0, store.LastState.Reservations.Count);
        Assert.AreEqual(0, store.LastState.Servers.Single().ReservedConnections);
        Assert.AreEqual(100, store.LastState.Servers.Single().AvailableConnections);

        BackendServerRegistry restored = new(TimeSpan.FromSeconds(90), store);
        Assert.AreEqual(0, restored.Reservations.Count);
    }

    [TestMethod]
    public void BackendRegistryFileStoreNormalizesExpiredServersOnRestoreTest()
    {
        string path = CreateRegistryPath();
        try
        {
            DateTimeOffset staleHeartbeat = DateTimeOffset.UtcNow.AddMinutes(-5);
            FileBackendRegistryStore store = new(path);
            store.Save(new BackendRegistryState
            {
                Version = 500,
                Servers =
                {
                    new BackendServerSnapshot
                    {
                        ClusterId = "socket-cluster-1",
                        SourceControlNodeId = "control-1",
                        ServerId = 1,
                        InstanceId = "server-1",
                        Host = "127.0.0.1",
                        Port = 5101,
                        MaxConnections = 10,
                        CurrentConnections = 5,
                        ReservedConnections = 1,
                        AvailableConnections = 4,
                        Health = ServerHealthState.Healthy,
                        ResourceUsage = new ResourceUsageSnapshot(),
                        Version = 300,
                        StartedAt = staleHeartbeat,
                        LastHeartbeatAt = staleHeartbeat,
                        UpdatedAt = staleHeartbeat
                    }
                },
                Reservations =
                {
                    new RouteReservationMessage
                    {
                        ReservationId = "control-1-501",
                        ClientId = 88,
                        ServerId = 1,
                        InstanceId = "server-1",
                        SourceControlNodeId = "control-1",
                        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(1)
                    }
                },
                Sessions =
                {
                    new SessionEventMessage
                    {
                        ClusterId = "socket-cluster-1",
                        ClientId = 77,
                        ServerId = 1,
                        InstanceId = "server-1",
                        SessionId = 700,
                        State = "Connected",
                        Version = 10
                    }
                },
                ClientLocations =
                {
                    new ClientLocationMessage
                    {
                        ClusterId = "socket-cluster-1",
                        ClientId = 77,
                        ServerId = 1,
                        InstanceId = "server-1",
                        Host = "127.0.0.1",
                        Port = 5101,
                        SessionId = 700,
                        State = "Connected",
                        Version = 10,
                        UpdatedAt = staleHeartbeat
                    }
                }
            });

            BackendServerRegistry restored = new(TimeSpan.FromSeconds(1), new FileBackendRegistryStore(path));
            ClusterStatusSnapshot status = restored.GetStatus();
            ClientLocationResponse location = restored.ResolveClientLocation(new ClientLocationRequest
            {
                SourceClientId = 1,
                TargetClientId = 77
            });
            BackendRegistryState restoredState = restored.SnapshotState();

            Assert.AreEqual(1, status.ServerCount);
            Assert.AreEqual(0, status.HealthyServerCount);
            Assert.AreEqual(0, status.TotalCurrentConnections);
            Assert.AreEqual(0, status.TotalReservedConnections);
            Assert.AreEqual(0, status.TotalAvailableConnections);
            Assert.IsFalse(location.Success);
            Assert.AreEqual(0, restoredState.Reservations.Count);
            Assert.AreEqual(0, restoredState.Sessions.Count);
            Assert.AreEqual(0, restoredState.ClientLocations.Count);
            Assert.AreEqual("Closed", restoredState.SessionTombstones.Single().State);
            Assert.IsTrue(restoredState.Version >= 500);
        }
        finally
        {
            DeleteRegistryPath(path);
        }
    }

    [TestMethod]
    public void ResourceUsageProviderReturnsPercentValuesTest()
    {
        ResourceUsageSnapshot snapshot = new ResourceUsageProvider().Capture();

        Assert.IsTrue(snapshot.CpuUsagePercent >= 0 && snapshot.CpuUsagePercent <= 100);
        Assert.IsTrue(snapshot.MemoryUsagePercent >= 0 && snapshot.MemoryUsagePercent <= 100);
        Assert.IsTrue(snapshot.StorageUsagePercent >= 0 && snapshot.StorageUsagePercent <= 100);
    }

    [TestMethod]
    public void ResourceUsageProviderUsesMachineMetricsInsteadOfProcessMetricsTest()
    {
        string source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "SocketCommon/Diagnostics/ResourceUsageProvider.cs"));

        Assert.IsFalse(source.Contains("Process.GetCurrentProcess", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("TotalProcessorTime", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("WorkingSet64", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("GC.GetGCMemoryInfo", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("/proc/stat", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("host_statistics64", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("GlobalMemoryStatusEx", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("MachHostPort", StringComparison.Ordinal));
        Assert.IsTrue(source.Contains("HostVmInfo64Count = 62", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ResourceUsageProviderMacVmInfoIndexesMatchNaturalTSlotsTest()
    {
        Type type = typeof(ResourceUsageProvider);

        Assert.AreEqual(0, ReadPrivateConstInt(type, "VmFreeCount"));
        Assert.AreEqual(2, ReadPrivateConstInt(type, "VmInactiveCount"));
        Assert.AreEqual(23, ReadPrivateConstInt(type, "VmSpeculativeCount"));
    }

    [TestMethod]
    public void ResourceUsageProviderReturnsNonZeroMachineMemoryAndStorageTest()
    {
        ResourceUsageSnapshot snapshot = new ResourceUsageProvider().Capture();

        Assert.IsTrue(snapshot.MemoryUsagePercent > 0 && snapshot.MemoryUsagePercent <= 100);
        Assert.IsTrue(snapshot.StorageUsagePercent > 0 && snapshot.StorageUsagePercent <= 100);
    }

    [TestMethod]
    public void ResourceUsageProviderCpuUsageCalculationUsesSampleDeltaTest()
    {
        double cpuUsagePercent = ResourceUsageProvider.CalculateCpuUsagePercent(
            new ResourceUsageProvider.CpuSample(90, 100),
            new ResourceUsageProvider.CpuSample(100, 200));

        Assert.AreEqual(90, cpuUsagePercent, 0.0001);
    }

    [TestMethod]
    public void ResourceUsageProviderCpuUsageCalculationRejectsInvalidSamplesTest()
    {
        Assert.AreEqual(
            0,
            ResourceUsageProvider.CalculateCpuUsagePercent(
                new ResourceUsageProvider.CpuSample(0, 0),
                new ResourceUsageProvider.CpuSample(100, 200)));

        Assert.AreEqual(
            0,
            ResourceUsageProvider.CalculateCpuUsagePercent(
                new ResourceUsageProvider.CpuSample(90, 100),
                new ResourceUsageProvider.CpuSample(0, 0)));

        Assert.AreEqual(
            0,
            ResourceUsageProvider.CalculateCpuUsagePercent(
                new ResourceUsageProvider.CpuSample(10, 100),
                new ResourceUsageProvider.CpuSample(5, 90)));
    }

    [TestMethod]
    public void PortRangeValidationTest()
    {
        Assert.IsTrue(SocketConfigLoader.IsValidPortRange(5100, 5199));
        Assert.IsTrue(SocketConfigLoader.IsValidPortRange(0, 0));
        Assert.IsFalse(SocketConfigLoader.IsValidPortRange(5199, 5100));
        Assert.IsFalse(SocketConfigLoader.IsValidPortRange(-1, 5100));
    }

    [TestMethod]
    public void EdgeTerminatedProfileAllowsOnlyTrustedInternalBindHostsTest()
    {
        SocketSecurityConfig security = new()
        {
            Profile = "EdgeTerminated",
            TrustedNetwork = true
        };

        SocketSecurityConfigValidator.ValidateServerBinding(security, "127.0.0.1");
        SocketSecurityConfigValidator.ValidateServerBinding(security, "localhost");
        SocketSecurityConfigValidator.ValidateServerBinding(security, "10.10.1.5");
        SocketSecurityConfigValidator.ValidateServerBinding(security, "172.16.1.5");
        SocketSecurityConfigValidator.ValidateServerBinding(security, "192.168.1.5");

        Assert.ThrowsException<InvalidOperationException>(
            () => SocketSecurityConfigValidator.ValidateServerBinding(security, "0.0.0.0"));
        Assert.ThrowsException<InvalidOperationException>(
            () => SocketSecurityConfigValidator.ValidateServerBinding(security, "8.8.8.8"));
        Assert.ThrowsException<InvalidOperationException>(
            () => SocketSecurityConfigValidator.ValidateServerBinding(security, "socket.example.com"));
    }

    [TestMethod]
    public void EdgeTerminatedProfileServerBindingRequiresTrustedNetworkTest()
    {
        SocketSecurityConfig security = new()
        {
            Profile = "EdgeTerminated",
            TrustedNetwork = false
        };

        Assert.ThrowsException<InvalidOperationException>(
            () => SocketSecurityConfigValidator.ValidateServerBinding(security, "127.0.0.1"));
    }

    private static ServerRegisterRequest CreateRegister(int serverId, string instanceId, int port, int maxConnections)
    {
        return new ServerRegisterRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = serverId,
            InstanceId = instanceId,
            Host = "127.0.0.1",
            Port = port,
            PortRangeStart = port,
            PortRangeEnd = port,
            MaxConnections = maxConnections,
            PendingAcceptCount = 10,
            IdleTimeoutSeconds = 90
        };
    }

    private static ServerHeartbeatRequest CreateHeartbeat(
        int serverId,
        string instanceId,
        int port,
        int currentConnections,
        int maxConnections)
    {
        return CreateHeartbeat(serverId, instanceId, port, currentConnections, maxConnections, DateTimeOffset.UtcNow);
    }

    private static ServerHeartbeatRequest CreateHeartbeat(
        int serverId,
        string instanceId,
        int port,
        int currentConnections,
        int maxConnections,
        DateTimeOffset sentAt)
    {
        return new ServerHeartbeatRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = serverId,
            InstanceId = instanceId,
            Host = "127.0.0.1",
            Port = port,
            Health = ServerHealthState.Healthy,
            CurrentConnections = currentConnections,
            MaxConnections = maxConnections,
            ResourceUsage = new ResourceUsageSnapshot
            {
                CpuUsagePercent = 10,
                MemoryUsagePercent = 10,
                StorageUsagePercent = 10
            },
            SentAt = sentAt
        };
    }

    private static BackendServerSnapshot CreateSnapshot(
        int serverId,
        string instanceId,
        int availableConnections,
        int currentConnections)
    {
        return new BackendServerSnapshot
        {
            ClusterId = "socket-cluster-1",
            SourceControlNodeId = "control-1",
            ServerId = serverId,
            InstanceId = instanceId,
            Host = "127.0.0.1",
            Port = 5100 + serverId,
            MaxConnections = availableConnections + currentConnections,
            CurrentConnections = currentConnections,
            ReservedConnections = 0,
            AvailableConnections = availableConnections,
            Health = ServerHealthState.Healthy,
            ResourceUsage = new ResourceUsageSnapshot(),
            Version = serverId,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private static SessionEventMessage CreateSessionEvent(
        string instanceId,
        long sessionId,
        uint clientId,
        string state,
        long version)
    {
        return new SessionEventMessage
        {
            ClusterId = "socket-cluster-1",
            ServerId = 1,
            InstanceId = instanceId,
            SessionId = sessionId,
            ClientId = clientId,
            State = state,
            Version = version,
            ConnectedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            LastReceivedAt = DateTimeOffset.UtcNow
        };
    }

    private static string CreateRegistryPath()
    {
        return Path.Combine(Path.GetTempPath(), $"socket-registry-{Guid.NewGuid():N}.json");
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

    private static int ReadPrivateConstInt(Type type, string fieldName)
    {
        FieldInfo? field = type.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field);
        return (int)field.GetRawConstantValue()!;
    }

    private static void DeleteRegistryPath(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        string temporaryPath = $"{path}.tmp";
        if (File.Exists(temporaryPath))
        {
            File.Delete(temporaryPath);
        }
    }

    private sealed class CountingBackendRegistryStore : IBackendRegistryStore
    {
        public int SaveCount { get; private set; }

        public BackendRegistryState LastState { get; private set; } = new();

        public BackendRegistryState Load()
        {
            return this.LastState;
        }

        public void Save(BackendRegistryState state)
        {
            this.LastState = state;
            this.SaveCount++;
        }
    }
}
