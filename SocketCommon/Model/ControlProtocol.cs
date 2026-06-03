using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public static class ControlMessageIds
{
    public const uint ServerRegister = 1000;
    public const uint ServerRegisterAck = 1001;
    public const uint ServerHeartbeat = 1002;
    public const uint ServerUnregister = 1003;
    public const uint ServerHeartbeatAck = 1004;
    public const uint SessionOpened = 1100;
    public const uint SessionUpdated = 1101;
    public const uint SessionClosed = 1102;
    public const uint RouteRequest = 1200;
    public const uint RouteResponse = 1201;
    public const uint RouteResolveFailed = 1202;
    public const uint ClientLocationRequest = 1210;
    public const uint ClientLocationResponse = 1211;
    public const uint ClientLocationNotFound = 1212;
    public const uint ControlRegister = 1400;
    public const uint ControlRegisterAck = 1401;
    public const uint ControlHeartbeat = 1402;
    public const uint ServerRegistryUpsert = 1410;
    public const uint ServerRegistryRemove = 1411;
    public const uint SessionSummaryUpsert = 1420;
    public const uint SessionSummaryRemove = 1421;
    public const uint ClientLocationUpsert = 1422;
    public const uint ClientLocationRemove = 1423;
    public const uint RouteReservationUpsert = 1430;
    public const uint RouteReservationRelease = 1431;
    public const uint RegistrySnapshotRequest = 1440;
    public const uint RegistrySnapshotResponse = 1441;
}

public enum ServerHealthState
{
    Unknown,
    Healthy,
    Degraded,
    Unhealthy
}

public class ResourceUsageSnapshot
{
    public double CpuUsagePercent { get; set; }

    public double MemoryUsagePercent { get; set; }

    public double StorageUsagePercent { get; set; }

    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ServerRegisterRequest
{
    public string ClusterId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public int PortRangeStart { get; set; }

    public int PortRangeEnd { get; set; }

    public int MaxConnections { get; set; }

    public int PendingAcceptCount { get; set; }

    public int IdleTimeoutSeconds { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ServerRegisterAck
{
    public string ClusterId { get; set; } = "";

    public string ControlNodeId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ServerHeartbeatRequest
{
    public string ClusterId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public ServerHealthState Health { get; set; } = ServerHealthState.Healthy;

    public int MaxConnections { get; set; }

    public int CurrentConnections { get; set; }

    public int ReservedConnections { get; set; }

    public int AvailableConnections { get; set; }

    public ResourceUsageSnapshot ResourceUsage { get; set; } = new();

    public long TotalAcceptedClients { get; set; }

    public long TotalClosedClients { get; set; }

    public long TotalRejectedClients { get; set; }

    public long TotalIdleTimeoutClients { get; set; }

    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ServerHeartbeatAck
{
    public string ClusterId { get; set; } = "";

    public string ControlNodeId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class SessionEventMessage
{
    public string ClusterId { get; set; } = "";

    public long SessionId { get; set; }

    public uint ClientId { get; set; }

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string RemoteEndPoint { get; set; } = "";

    public DateTimeOffset ConnectedAt { get; set; }

    public DateTimeOffset LastReceivedAt { get; set; }

    public string State { get; set; } = "";

    public long Version { get; set; }
}

public class RouteRequest
{
    public uint ClientId { get; set; }

    public int? PreferredServerId { get; set; }

    public string RoutingPolicy { get; set; } = "MostAvailableConnections";
}

public class RouteResponse
{
    public bool Success { get; set; }

    public string ReservationId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string ErrorMessage { get; set; } = "";
}

public class ClientLocationRequest
{
    public uint SourceClientId { get; set; }

    public uint TargetClientId { get; set; }
}

public class ClientLocationResponse
{
    public bool Success { get; set; }

    public uint TargetClientId { get; set; }

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public long SessionId { get; set; }

    public string State { get; set; } = "";

    public string ErrorMessage { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class BackendServerSnapshot
{
    public string ClusterId { get; set; } = "";

    public string SourceControlNodeId { get; set; } = "";

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Name { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public int PortRangeStart { get; set; }

    public int PortRangeEnd { get; set; }

    public int MaxConnections { get; set; }

    public int CurrentConnections { get; set; }

    public int ReservedConnections { get; set; }

    public int AvailableConnections { get; set; }

    public ServerHealthState Health { get; set; }

    public ResourceUsageSnapshot ResourceUsage { get; set; } = new();

    public long Version { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset LastHeartbeatAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class RouteReservationMessage
{
    public string ReservationId { get; set; } = "";

    public uint ClientId { get; set; }

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public DateTimeOffset ExpiresAt { get; set; }

    public string SourceControlNodeId { get; set; } = "";
}

public class ClientLocationMessage
{
    public string ClusterId { get; set; } = "";

    public uint ClientId { get; set; }

    public int ServerId { get; set; }

    public string InstanceId { get; set; } = "";

    public string Host { get; set; } = "";

    public int Port { get; set; }

    public long SessionId { get; set; }

    public string State { get; set; } = "";

    public long Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class ClusterStatusSnapshot
{
    public int ServerCount { get; init; }

    public int HealthyServerCount { get; init; }

    public int TotalMaxConnections { get; init; }

    public int TotalCurrentConnections { get; init; }

    public int TotalReservedConnections { get; init; }

    public int TotalAvailableConnections { get; init; }

    public int TotalSessionCount { get; init; }

    public double AverageCpuUsagePercent { get; init; }

    public double AverageMemoryUsagePercent { get; init; }

    public double AverageStorageUsagePercent { get; init; }

    public IReadOnlyCollection<BackendServerSnapshot> Servers { get; init; } = Array.Empty<BackendServerSnapshot>();

    public DateTimeOffset UpdatedAt { get; init; }
}

public class ControlAckMessage
{
    public string ClusterId { get; set; } = "";

    public string ControlNodeId { get; set; } = "";

    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class RegistrySnapshotRequest
{
    public DateTimeOffset RequestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class ControlProtocol
{
    public static SocketMessageFrame CreateFrame<T>(uint clientId, uint messageId, T payload)
    {
        byte[] bytes = ProtobufPayloadSerializer.Encode(payload);
        return new SocketMessageFrame(clientId, messageId, bytes);
    }

    public static bool TryDecode<T>(SocketMessageFrame frame, uint expectedMessageId, out T payload)
    {
        payload = default;
        if (frame == null || frame.MessageId != expectedMessageId)
        {
            return false;
        }

        return ProtobufPayloadSerializer.TryDecode(frame.Payload, out payload);
    }

    public static Task<bool> SendAsync<T>(Socket socket, uint clientId, uint messageId, T payload)
    {
        return SocketMessageFrame.SendAsync(socket, CreateFrame(clientId, messageId, payload));
    }

    public static Task<bool> SendAsync<T>(SecureSocketConnection connection, uint clientId, uint messageId, T payload)
    {
        return SocketMessageFrame.SendAsync(connection, CreateFrame(clientId, messageId, payload));
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveAsync<T>(
        Socket socket,
        uint clientId,
        uint messageId,
        T payload,
        int timeoutMilliseconds = 5000)
    {
        if (!await SendAsync(socket, clientId, messageId, payload))
        {
            return (false, null);
        }

        Task<(bool Success, SocketMessageFrame Frame)> receiveTask = SocketMessageFrame.TryReceiveAsync(socket);
        Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMilliseconds));
        if (completedTask != receiveTask)
        {
            return (false, null);
        }

        return await receiveTask;
    }

    public static async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveAsync<T>(
        SecureSocketConnection connection,
        uint clientId,
        uint messageId,
        T payload,
        int timeoutMilliseconds = 5000)
    {
        if (!await SendAsync(connection, clientId, messageId, payload))
        {
            return (false, null);
        }

        Task<(bool Success, SocketMessageFrame Frame)> receiveTask = SocketMessageFrame.TryReceiveAsync(connection);
        Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(timeoutMilliseconds));
        if (completedTask != receiveTask)
        {
            return (false, null);
        }

        return await receiveTask;
    }
}
