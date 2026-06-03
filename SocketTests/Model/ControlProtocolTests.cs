using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
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
    public void BackendRegistryExcludesExpiredHeartbeatTest()
    {
        BackendServerRegistry registry = new(TimeSpan.FromMilliseconds(10));
        registry.Upsert(CreateRegister(1, "server-1", 5101, 100), "control-1");
        registry.Upsert(CreateHeartbeat(1, "server-1", 5101, 0, 100, DateTimeOffset.UtcNow.AddSeconds(-1)), "control-1", new ControlHealthThreshold());

        RouteResponse response = registry.Resolve(new RouteRequest { ClientId = 1 }, "control-1", TimeSpan.FromSeconds(5));

        Assert.IsFalse(response.Success);
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
    public void BackendRegistryFileStoreRestoresServerReservationAndClientLocationTest()
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
            Assert.AreEqual(1, status.TotalReservedConnections);
            Assert.AreEqual(6, status.TotalAvailableConnections);
            Assert.IsTrue(location.Success);
            Assert.AreEqual("server-1", location.InstanceId);
        }
        finally
        {
            DeleteRegistryPath(path);
        }
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
            Assert.AreEqual("ServerDisconnected", restoredState.Sessions.Single().State);
            Assert.AreEqual("ServerDisconnected", restoredState.ClientLocations.Single().State);
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
    public void PortRangeValidationTest()
    {
        Assert.IsTrue(SocketConfigLoader.IsValidPortRange(5100, 5199));
        Assert.IsTrue(SocketConfigLoader.IsValidPortRange(0, 0));
        Assert.IsFalse(SocketConfigLoader.IsValidPortRange(5199, 5100));
        Assert.IsFalse(SocketConfigLoader.IsValidPortRange(-1, 5100));
    }

    private static ServerRegisterRequest CreateRegister(int serverId, string instanceId, int port, int maxConnections)
    {
        return new ServerRegisterRequest
        {
            ClusterId = "socket-cluster-1",
            ServerId = serverId,
            InstanceId = instanceId,
            Name = instanceId,
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

    private static string CreateRegistryPath()
    {
        return Path.Combine(Path.GetTempPath(), $"socket-registry-{Guid.NewGuid():N}.json");
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
}
