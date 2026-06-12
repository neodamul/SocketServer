using System;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using SocketCommon.Protobuf;

namespace SocketCommon.Model;

internal static class ProtobufPayloadSerializer
{
    public static byte[] Encode<T>(T payload)
    {
        return ToProtoMessage(payload).ToByteArray();
    }

    public static bool TryDecode<T>(byte[] bytes, out T payload)
    {
        payload = default;
        try
        {
            object decoded = Decode(typeof(T), bytes ?? Array.Empty<byte>());
            if (decoded is T typed)
            {
                payload = typed;
                return true;
            }

            return false;
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    private static IMessage ToProtoMessage<T>(T payload)
    {
        return payload switch
        {
            HealthCheckMessage value => ToProto(value),
            HelloWorldRequest value => ToProto(value),
            HelloWorldResponse value => ToProto(value),
            ResourceUsageSnapshot value => ToProto(value),
            ServerRegisterRequest value => ToProto(value),
            ServerRegisterAck value => ToProto(value),
            ServerHeartbeatRequest value => ToProto(value),
            ServerHeartbeatAck value => ToProto(value),
            SessionEventMessage value => ToProto(value),
            RouteRequest value => ToProto(value),
            RouteResponse value => ToProto(value),
            ClientLocationRequest value => ToProto(value),
            ClientLocationResponse value => ToProto(value),
            BackendServerSnapshot value => ToProto(value),
            RouteReservationMessage value => ToProto(value),
            ClientLocationMessage value => ToProto(value),
            ClusterStatusSnapshot value => ToProto(value),
            ControlAckMessage value => ToProto(value),
            RegistrySnapshotRequest value => ToProto(value),
            ControlRelayBatchMessage value => ToProto(value),
            ClientRegisterRequest value => ToProto(value),
            ClientRegisterAck value => ToProto(value),
            ClientMessageSendRequest value => ToProto(value),
            ClientMessageDelivery value => ToProto(value),
            ClientMessageAck value => ToProto(value),
            ClientMessageError value => ToProto(value),
            ServerRelayMessage value => ToProto(value),
            ServerRelayBatchMessage value => ToProto(value),
            ServerRelayBatchResult value => ToProto(value),
            _ => throw new NotSupportedException($"Unsupported protobuf payload type: {typeof(T).FullName}")
        };
    }

    private static object Decode(Type type, byte[] bytes)
    {
        if (type == typeof(HealthCheckMessage))
        {
            return FromProto(ProtoHealthCheckMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(HelloWorldRequest))
        {
            return FromProto(ProtoHelloWorldRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(HelloWorldResponse))
        {
            return FromProto(ProtoHelloWorldResponse.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ResourceUsageSnapshot))
        {
            return FromProto(ProtoResourceUsageSnapshot.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerRegisterRequest))
        {
            return FromProto(ProtoServerRegisterRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerRegisterAck))
        {
            return FromProto(ProtoServerRegisterAck.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerHeartbeatRequest))
        {
            return FromProto(ProtoServerHeartbeatRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerHeartbeatAck))
        {
            return FromProto(ProtoServerHeartbeatAck.Parser.ParseFrom(bytes));
        }

        if (type == typeof(SessionEventMessage))
        {
            return FromProto(ProtoSessionEventMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(RouteRequest))
        {
            return FromProto(ProtoRouteRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(RouteResponse))
        {
            return FromProto(ProtoRouteResponse.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientLocationRequest))
        {
            return FromProto(ProtoClientLocationRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientLocationResponse))
        {
            return FromProto(ProtoClientLocationResponse.Parser.ParseFrom(bytes));
        }

        if (type == typeof(BackendServerSnapshot))
        {
            return FromProto(ProtoBackendServerSnapshot.Parser.ParseFrom(bytes));
        }

        if (type == typeof(RouteReservationMessage))
        {
            return FromProto(ProtoRouteReservationMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientLocationMessage))
        {
            return FromProto(ProtoClientLocationMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClusterStatusSnapshot))
        {
            return FromProto(ProtoClusterStatusSnapshot.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ControlAckMessage))
        {
            return FromProto(ProtoControlAckMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(RegistrySnapshotRequest))
        {
            return FromProto(ProtoRegistrySnapshotRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ControlRelayBatchMessage))
        {
            return FromProto(ProtoControlRelayBatchMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientRegisterRequest))
        {
            return FromProto(ProtoClientRegisterRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientRegisterAck))
        {
            return FromProto(ProtoClientRegisterAck.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientMessageSendRequest))
        {
            return FromProto(ProtoClientMessageSendRequest.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientMessageDelivery))
        {
            return FromProto(ProtoClientMessageDelivery.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientMessageAck))
        {
            return FromProto(ProtoClientMessageAck.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ClientMessageError))
        {
            return FromProto(ProtoClientMessageError.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerRelayMessage))
        {
            return FromProto(ProtoServerRelayMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerRelayBatchMessage))
        {
            return FromProto(ProtoServerRelayBatchMessage.Parser.ParseFrom(bytes));
        }

        if (type == typeof(ServerRelayBatchResult))
        {
            return FromProto(ProtoServerRelayBatchResult.Parser.ParseFrom(bytes));
        }

        throw new NotSupportedException($"Unsupported protobuf payload type: {type.FullName}");
    }

    private static ProtoHealthCheckMessage ToProto(HealthCheckMessage value)
    {
        return new ProtoHealthCheckMessage
        {
            Status = value.Status ?? "",
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static HealthCheckMessage FromProto(ProtoHealthCheckMessage value)
    {
        return string.IsNullOrEmpty(value.Status)
            ? new HealthCheckMessage(0, HealthCheckMessageType.Ping, "")
            : new HealthCheckMessage(0, HealthCheckMessageType.Pong, value.Status);
    }

    private static ProtoHelloWorldRequest ToProto(HelloWorldRequest value)
    {
        return new ProtoHelloWorldRequest
        {
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static HelloWorldRequest FromProto(ProtoHelloWorldRequest value)
    {
        return new HelloWorldRequest();
    }

    private static ProtoHelloWorldResponse ToProto(HelloWorldResponse value)
    {
        return new ProtoHelloWorldResponse
        {
            Message = value.Message ?? "",
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static HelloWorldResponse FromProto(ProtoHelloWorldResponse value)
    {
        return new HelloWorldResponse(0, value.Message);
    }

    private static ProtoResourceUsageSnapshot ToProto(ResourceUsageSnapshot value)
    {
        return new ProtoResourceUsageSnapshot
        {
            CpuUsagePercent = value.CpuUsagePercent,
            MemoryUsagePercent = value.MemoryUsagePercent,
            StorageUsagePercent = value.StorageUsagePercent,
            CapturedAtUnixMs = ToUnixMs(value.CapturedAt)
        };
    }

    private static ResourceUsageSnapshot FromProto(ProtoResourceUsageSnapshot value)
    {
        return new ResourceUsageSnapshot
        {
            CpuUsagePercent = value.CpuUsagePercent,
            MemoryUsagePercent = value.MemoryUsagePercent,
            StorageUsagePercent = value.StorageUsagePercent,
            CapturedAt = FromUnixMs(value.CapturedAtUnixMs)
        };
    }

    private static ProtoServerRegisterRequest ToProto(ServerRegisterRequest value)
    {
        return new ProtoServerRegisterRequest
        {
            ClusterId = value.ClusterId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Name = value.Name ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            PortRangeStart = value.PortRangeStart,
            PortRangeEnd = value.PortRangeEnd,
            MaxConnections = value.MaxConnections,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            StartedAtUnixMs = ToUnixMs(value.StartedAt)
        };
    }

    private static ServerRegisterRequest FromProto(ProtoServerRegisterRequest value)
    {
        return new ServerRegisterRequest
        {
            ClusterId = value.ClusterId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Name = value.Name,
            Host = value.Host,
            Port = value.Port,
            PortRangeStart = value.PortRangeStart,
            PortRangeEnd = value.PortRangeEnd,
            MaxConnections = value.MaxConnections,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            StartedAt = FromUnixMs(value.StartedAtUnixMs)
        };
    }

    private static ProtoServerRegisterAck ToProto(ServerRegisterAck value)
    {
        return new ProtoServerRegisterAck
        {
            ClusterId = value.ClusterId ?? "",
            ControlNodeId = value.ControlNodeId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            ReceivedAtUnixMs = ToUnixMs(value.ReceivedAt)
        };
    }

    private static ServerRegisterAck FromProto(ProtoServerRegisterAck value)
    {
        return new ServerRegisterAck
        {
            ClusterId = value.ClusterId,
            ControlNodeId = value.ControlNodeId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            ReceivedAt = FromUnixMs(value.ReceivedAtUnixMs)
        };
    }

    private static ProtoServerHeartbeatRequest ToProto(ServerHeartbeatRequest value)
    {
        return new ProtoServerHeartbeatRequest
        {
            ClusterId = value.ClusterId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            Health = ToProto(value.Health),
            MaxConnections = value.MaxConnections,
            CurrentConnections = value.CurrentConnections,
            ReservedConnections = value.ReservedConnections,
            AvailableConnections = value.AvailableConnections,
            ResourceUsage = ToProto(value.ResourceUsage ?? new ResourceUsageSnapshot()),
            TotalAcceptedClients = value.TotalAcceptedClients,
            TotalClosedClients = value.TotalClosedClients,
            TotalRejectedClients = value.TotalRejectedClients,
            TotalIdleTimeoutClients = value.TotalIdleTimeoutClients,
            TotalReceivedMessages = value.TotalReceivedMessages,
            TotalSentMessages = value.TotalSentMessages,
            TotalReceivedMessageBytes = value.TotalReceivedMessageBytes,
            TotalSentMessageBytes = value.TotalSentMessageBytes,
            ListenBacklog = value.ListenBacklog,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            NoDelay = value.NoDelay,
            MaxPayloadLength = value.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = value.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = value.SocketAsyncEventArgsTotalCreatedCount,
            SocketAsyncEventArgsInUseCount = value.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = value.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = value.SocketAsyncEventArgsGrowthCount,
            SentAtUnixMs = ToUnixMs(value.SentAt)
        };
    }

    private static ServerHeartbeatRequest FromProto(ProtoServerHeartbeatRequest value)
    {
        return new ServerHeartbeatRequest
        {
            ClusterId = value.ClusterId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Host = value.Host,
            Port = value.Port,
            Health = FromProto(value.Health),
            MaxConnections = value.MaxConnections,
            CurrentConnections = value.CurrentConnections,
            ReservedConnections = value.ReservedConnections,
            AvailableConnections = value.AvailableConnections,
            ResourceUsage = value.ResourceUsage == null ? new ResourceUsageSnapshot() : FromProto(value.ResourceUsage),
            TotalAcceptedClients = value.TotalAcceptedClients,
            TotalClosedClients = value.TotalClosedClients,
            TotalRejectedClients = value.TotalRejectedClients,
            TotalIdleTimeoutClients = value.TotalIdleTimeoutClients,
            TotalReceivedMessages = value.TotalReceivedMessages,
            TotalSentMessages = value.TotalSentMessages,
            TotalReceivedMessageBytes = value.TotalReceivedMessageBytes,
            TotalSentMessageBytes = value.TotalSentMessageBytes,
            ListenBacklog = value.ListenBacklog,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            NoDelay = value.NoDelay,
            MaxPayloadLength = value.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = value.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = value.SocketAsyncEventArgsTotalCreatedCount,
            SocketAsyncEventArgsInUseCount = value.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = value.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = value.SocketAsyncEventArgsGrowthCount,
            SentAt = FromUnixMs(value.SentAtUnixMs)
        };
    }

    private static ProtoServerHeartbeatAck ToProto(ServerHeartbeatAck value)
    {
        return new ProtoServerHeartbeatAck
        {
            ClusterId = value.ClusterId ?? "",
            ControlNodeId = value.ControlNodeId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            ReceivedAtUnixMs = ToUnixMs(value.ReceivedAt)
        };
    }

    private static ServerHeartbeatAck FromProto(ProtoServerHeartbeatAck value)
    {
        return new ServerHeartbeatAck
        {
            ClusterId = value.ClusterId,
            ControlNodeId = value.ControlNodeId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            ReceivedAt = FromUnixMs(value.ReceivedAtUnixMs)
        };
    }

    private static ProtoSessionEventMessage ToProto(SessionEventMessage value)
    {
        return new ProtoSessionEventMessage
        {
            ClusterId = value.ClusterId ?? "",
            SessionId = value.SessionId,
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            RemoteEndPoint = value.RemoteEndPoint ?? "",
            ConnectedAtUnixMs = ToUnixMs(value.ConnectedAt),
            LastReceivedAtUnixMs = ToUnixMs(value.LastReceivedAt),
            State = value.State ?? "",
            Version = value.Version
        };
    }

    private static SessionEventMessage FromProto(ProtoSessionEventMessage value)
    {
        return new SessionEventMessage
        {
            ClusterId = value.ClusterId,
            SessionId = value.SessionId,
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            RemoteEndPoint = value.RemoteEndPoint,
            ConnectedAt = FromUnixMs(value.ConnectedAtUnixMs),
            LastReceivedAt = FromUnixMs(value.LastReceivedAtUnixMs),
            State = value.State,
            Version = value.Version
        };
    }

    private static ProtoRouteRequest ToProto(RouteRequest value)
    {
        ProtoRouteRequest request = new()
        {
            ClientId = value.ClientId,
            RoutingPolicy = value.RoutingPolicy ?? ""
        };
        if (value.PreferredServerId.HasValue)
        {
            request.PreferredServerId = value.PreferredServerId.Value;
        }

        return request;
    }

    private static RouteRequest FromProto(ProtoRouteRequest value)
    {
        return new RouteRequest
        {
            ClientId = value.ClientId,
            PreferredServerId = value.HasPreferredServerId ? value.PreferredServerId : null,
            RoutingPolicy = value.RoutingPolicy
        };
    }

    private static ProtoRouteResponse ToProto(RouteResponse value)
    {
        return new ProtoRouteResponse
        {
            Success = value.Success,
            ReservationId = value.ReservationId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            ExpiresAtUnixMs = ToUnixMs(value.ExpiresAt),
            ErrorMessage = value.ErrorMessage ?? ""
        };
    }

    private static RouteResponse FromProto(ProtoRouteResponse value)
    {
        return new RouteResponse
        {
            Success = value.Success,
            ReservationId = value.ReservationId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Host = value.Host,
            Port = value.Port,
            ExpiresAt = FromUnixMs(value.ExpiresAtUnixMs),
            ErrorMessage = value.ErrorMessage
        };
    }

    private static ProtoClientLocationRequest ToProto(ClientLocationRequest value)
    {
        return new ProtoClientLocationRequest
        {
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId
        };
    }

    private static ClientLocationRequest FromProto(ProtoClientLocationRequest value)
    {
        return new ClientLocationRequest
        {
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId
        };
    }

    private static ProtoClientLocationResponse ToProto(ClientLocationResponse value)
    {
        return new ProtoClientLocationResponse
        {
            Success = value.Success,
            TargetClientId = value.TargetClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            SessionId = value.SessionId,
            State = value.State ?? "",
            ErrorMessage = value.ErrorMessage ?? "",
            UpdatedAtUnixMs = ToUnixMs(value.UpdatedAt)
        };
    }

    private static ClientLocationResponse FromProto(ProtoClientLocationResponse value)
    {
        return new ClientLocationResponse
        {
            Success = value.Success,
            TargetClientId = value.TargetClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Host = value.Host,
            Port = value.Port,
            SessionId = value.SessionId,
            State = value.State,
            ErrorMessage = value.ErrorMessage,
            UpdatedAt = FromUnixMs(value.UpdatedAtUnixMs)
        };
    }

    private static ProtoBackendServerSnapshot ToProto(BackendServerSnapshot value)
    {
        return new ProtoBackendServerSnapshot
        {
            ClusterId = value.ClusterId ?? "",
            SourceControlNodeId = value.SourceControlNodeId ?? "",
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Name = value.Name ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            PortRangeStart = value.PortRangeStart,
            PortRangeEnd = value.PortRangeEnd,
            MaxConnections = value.MaxConnections,
            CurrentConnections = value.CurrentConnections,
            RegisteredSessionCount = value.RegisteredSessionCount,
            StaleConnectionCount = value.StaleConnectionCount,
            ReservedConnections = value.ReservedConnections,
            AvailableConnections = value.AvailableConnections,
            Health = ToProto(value.Health),
            ResourceUsage = ToProto(value.ResourceUsage ?? new ResourceUsageSnapshot()),
            Version = value.Version,
            StartedAtUnixMs = ToUnixMs(value.StartedAt),
            LastHeartbeatAtUnixMs = ToUnixMs(value.LastHeartbeatAt),
            UpdatedAtUnixMs = ToUnixMs(value.UpdatedAt),
            TotalAcceptedClients = value.TotalAcceptedClients,
            TotalClosedClients = value.TotalClosedClients,
            TotalRejectedClients = value.TotalRejectedClients,
            TotalIdleTimeoutClients = value.TotalIdleTimeoutClients,
            TotalReceivedMessages = value.TotalReceivedMessages,
            TotalSentMessages = value.TotalSentMessages,
            TotalReceivedMessageBytes = value.TotalReceivedMessageBytes,
            TotalSentMessageBytes = value.TotalSentMessageBytes,
            ListenBacklog = value.ListenBacklog,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            NoDelay = value.NoDelay,
            MaxPayloadLength = value.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = value.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = value.SocketAsyncEventArgsTotalCreatedCount,
            SocketAsyncEventArgsInUseCount = value.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = value.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = value.SocketAsyncEventArgsGrowthCount
        };
    }

    private static BackendServerSnapshot FromProto(ProtoBackendServerSnapshot value)
    {
        return new BackendServerSnapshot
        {
            ClusterId = value.ClusterId,
            SourceControlNodeId = value.SourceControlNodeId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Name = value.Name,
            Host = value.Host,
            Port = value.Port,
            PortRangeStart = value.PortRangeStart,
            PortRangeEnd = value.PortRangeEnd,
            MaxConnections = value.MaxConnections,
            CurrentConnections = value.CurrentConnections,
            RegisteredSessionCount = value.RegisteredSessionCount,
            StaleConnectionCount = value.StaleConnectionCount,
            ReservedConnections = value.ReservedConnections,
            AvailableConnections = value.AvailableConnections,
            Health = FromProto(value.Health),
            ResourceUsage = value.ResourceUsage == null ? new ResourceUsageSnapshot() : FromProto(value.ResourceUsage),
            Version = value.Version,
            StartedAt = FromUnixMs(value.StartedAtUnixMs),
            LastHeartbeatAt = FromUnixMs(value.LastHeartbeatAtUnixMs),
            UpdatedAt = FromUnixMs(value.UpdatedAtUnixMs),
            TotalAcceptedClients = value.TotalAcceptedClients,
            TotalClosedClients = value.TotalClosedClients,
            TotalRejectedClients = value.TotalRejectedClients,
            TotalIdleTimeoutClients = value.TotalIdleTimeoutClients,
            TotalReceivedMessages = value.TotalReceivedMessages,
            TotalSentMessages = value.TotalSentMessages,
            TotalReceivedMessageBytes = value.TotalReceivedMessageBytes,
            TotalSentMessageBytes = value.TotalSentMessageBytes,
            ListenBacklog = value.ListenBacklog,
            PendingAcceptCount = value.PendingAcceptCount,
            IdleTimeoutSeconds = value.IdleTimeoutSeconds,
            NoDelay = value.NoDelay,
            MaxPayloadLength = value.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = value.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = value.SocketAsyncEventArgsTotalCreatedCount,
            SocketAsyncEventArgsInUseCount = value.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = value.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = value.SocketAsyncEventArgsGrowthCount
        };
    }

    private static ProtoRouteReservationMessage ToProto(RouteReservationMessage value)
    {
        return new ProtoRouteReservationMessage
        {
            ReservationId = value.ReservationId ?? "",
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            ExpiresAtUnixMs = ToUnixMs(value.ExpiresAt),
            SourceControlNodeId = value.SourceControlNodeId ?? ""
        };
    }

    private static RouteReservationMessage FromProto(ProtoRouteReservationMessage value)
    {
        return new RouteReservationMessage
        {
            ReservationId = value.ReservationId,
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            ExpiresAt = FromUnixMs(value.ExpiresAtUnixMs),
            SourceControlNodeId = value.SourceControlNodeId
        };
    }

    private static ProtoClientLocationMessage ToProto(ClientLocationMessage value)
    {
        return new ProtoClientLocationMessage
        {
            ClusterId = value.ClusterId ?? "",
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId ?? "",
            Host = value.Host ?? "",
            Port = value.Port,
            SessionId = value.SessionId,
            State = value.State ?? "",
            Version = value.Version,
            UpdatedAtUnixMs = ToUnixMs(value.UpdatedAt)
        };
    }

    private static ClientLocationMessage FromProto(ProtoClientLocationMessage value)
    {
        return new ClientLocationMessage
        {
            ClusterId = value.ClusterId,
            ClientId = value.ClientId,
            ServerId = value.ServerId,
            InstanceId = value.InstanceId,
            Host = value.Host,
            Port = value.Port,
            SessionId = value.SessionId,
            State = value.State,
            Version = value.Version,
            UpdatedAt = FromUnixMs(value.UpdatedAtUnixMs)
        };
    }

    private static ProtoClusterStatusSnapshot ToProto(ClusterStatusSnapshot value)
    {
        ProtoClusterStatusSnapshot snapshot = new()
        {
            ServerCount = value.ServerCount,
            HealthyServerCount = value.HealthyServerCount,
            TotalMaxConnections = value.TotalMaxConnections,
            TotalCurrentConnections = value.TotalCurrentConnections,
            TotalReservedConnections = value.TotalReservedConnections,
            TotalAvailableConnections = value.TotalAvailableConnections,
            TotalSessionCount = value.TotalSessionCount,
            AverageCpuUsagePercent = value.AverageCpuUsagePercent,
            AverageMemoryUsagePercent = value.AverageMemoryUsagePercent,
            AverageStorageUsagePercent = value.AverageStorageUsagePercent,
            UpdatedAtUnixMs = ToUnixMs(value.UpdatedAt)
        };
        if (value.ControlServerResourceUsage != null)
        {
            snapshot.ControlServerResourceUsage = ToProto(value.ControlServerResourceUsage);
        }

        foreach (BackendServerSnapshot server in value.Servers ?? Array.Empty<BackendServerSnapshot>())
        {
            snapshot.Servers.Add(ToProto(server));
        }

        return snapshot;
    }

    private static ClusterStatusSnapshot FromProto(ProtoClusterStatusSnapshot value)
    {
        List<BackendServerSnapshot> servers = new();
        foreach (ProtoBackendServerSnapshot server in value.Servers)
        {
            servers.Add(FromProto(server));
        }

        return new ClusterStatusSnapshot
        {
            ServerCount = value.ServerCount,
            HealthyServerCount = value.HealthyServerCount,
            TotalMaxConnections = value.TotalMaxConnections,
            TotalCurrentConnections = value.TotalCurrentConnections,
            TotalReservedConnections = value.TotalReservedConnections,
            TotalAvailableConnections = value.TotalAvailableConnections,
            TotalSessionCount = value.TotalSessionCount,
            AverageCpuUsagePercent = value.AverageCpuUsagePercent,
            AverageMemoryUsagePercent = value.AverageMemoryUsagePercent,
            AverageStorageUsagePercent = value.AverageStorageUsagePercent,
            ControlServerResourceUsage = value.ControlServerResourceUsage == null ? null : FromProto(value.ControlServerResourceUsage),
            Servers = servers,
            UpdatedAt = FromUnixMs(value.UpdatedAtUnixMs)
        };
    }

    private static ProtoControlAckMessage ToProto(ControlAckMessage value)
    {
        return new ProtoControlAckMessage
        {
            ClusterId = value.ClusterId ?? "",
            ControlNodeId = value.ControlNodeId ?? "",
            ReceivedAtUnixMs = ToUnixMs(value.ReceivedAt)
        };
    }

    private static ControlAckMessage FromProto(ProtoControlAckMessage value)
    {
        return new ControlAckMessage
        {
            ClusterId = value.ClusterId,
            ControlNodeId = value.ControlNodeId,
            ReceivedAt = FromUnixMs(value.ReceivedAtUnixMs)
        };
    }

    private static ProtoRegistrySnapshotRequest ToProto(RegistrySnapshotRequest value)
    {
        return new ProtoRegistrySnapshotRequest
        {
            RequestedAtUnixMs = ToUnixMs(value.RequestedAt)
        };
    }

    private static RegistrySnapshotRequest FromProto(ProtoRegistrySnapshotRequest value)
    {
        return new RegistrySnapshotRequest
        {
            RequestedAt = FromUnixMs(value.RequestedAtUnixMs)
        };
    }

    private static ProtoControlRelayBatchMessage ToProto(ControlRelayBatchMessage value)
    {
        ProtoControlRelayBatchMessage message = new()
        {
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
        foreach (ControlRelayBatchItem item in value.Items ?? Enumerable.Empty<ControlRelayBatchItem>())
        {
            message.Items.Add(new ProtoControlRelayBatchItem
            {
                ClientId = item.ClientId,
                MessageId = item.MessageId,
                Payload = ByteString.CopyFrom(item.Payload ?? Array.Empty<byte>()),
                PayloadType = item.PayloadType ?? ""
            });
        }

        return message;
    }

    private static ControlRelayBatchMessage FromProto(ProtoControlRelayBatchMessage value)
    {
        return new ControlRelayBatchMessage
        {
            Items = value.Items.Select(item => new ControlRelayBatchItem
            {
                ClientId = item.ClientId,
                MessageId = item.MessageId,
                Payload = item.Payload.ToByteArray(),
                PayloadType = item.PayloadType
            }).ToList(),
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoClientRegisterRequest ToProto(ClientRegisterRequest value)
    {
        return new ProtoClientRegisterRequest
        {
            ClientId = value.ClientId,
            RegisteredAtUnixMs = ToUnixMs(value.RegisteredAt)
        };
    }

    private static ClientRegisterRequest FromProto(ProtoClientRegisterRequest value)
    {
        return new ClientRegisterRequest
        {
            ClientId = value.ClientId,
            RegisteredAt = FromUnixMs(value.RegisteredAtUnixMs)
        };
    }

    private static ProtoClientRegisterAck ToProto(ClientRegisterAck value)
    {
        return new ProtoClientRegisterAck
        {
            ClientId = value.ClientId,
            Success = value.Success,
            ErrorMessage = value.ErrorMessage ?? "",
            RegisteredAtUnixMs = ToUnixMs(value.RegisteredAt),
            RetryAfterSeconds = value.RetryAfterSeconds
        };
    }

    private static ClientRegisterAck FromProto(ProtoClientRegisterAck value)
    {
        return new ClientRegisterAck
        {
            ClientId = value.ClientId,
            Success = value.Success,
            ErrorMessage = value.ErrorMessage,
            RegisteredAt = FromUnixMs(value.RegisteredAtUnixMs),
            RetryAfterSeconds = value.RetryAfterSeconds
        };
    }

    private static ProtoClientMessageSendRequest ToProto(ClientMessageSendRequest value)
    {
        return new ProtoClientMessageSendRequest
        {
            MessageToken = value.MessageToken ?? "",
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content ?? "",
            TtlSeconds = value.TtlSeconds,
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static ClientMessageSendRequest FromProto(ProtoClientMessageSendRequest value)
    {
        return new ClientMessageSendRequest
        {
            MessageToken = value.MessageToken,
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content,
            TtlSeconds = value.TtlSeconds,
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoClientMessageDelivery ToProto(ClientMessageDelivery value)
    {
        return new ProtoClientMessageDelivery
        {
            MessageToken = value.MessageToken ?? "",
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content ?? "",
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static ClientMessageDelivery FromProto(ProtoClientMessageDelivery value)
    {
        return new ClientMessageDelivery
        {
            MessageToken = value.MessageToken,
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content,
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoClientMessageAck ToProto(ClientMessageAck value)
    {
        return new ProtoClientMessageAck
        {
            MessageToken = value.MessageToken ?? "",
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Delivered = value.Delivered,
            TargetInstanceId = value.TargetInstanceId ?? "",
            DeliveredAtUnixMs = ToUnixMs(value.DeliveredAt)
        };
    }

    private static ClientMessageAck FromProto(ProtoClientMessageAck value)
    {
        return new ClientMessageAck
        {
            MessageToken = value.MessageToken,
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Delivered = value.Delivered,
            TargetInstanceId = value.TargetInstanceId,
            DeliveredAt = FromUnixMs(value.DeliveredAtUnixMs)
        };
    }

    private static ProtoClientMessageError ToProto(ClientMessageError value)
    {
        return new ProtoClientMessageError
        {
            MessageToken = value.MessageToken ?? "",
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            ErrorCode = value.ErrorCode ?? "",
            ErrorMessage = value.ErrorMessage ?? "",
            FailedAtUnixMs = ToUnixMs(value.FailedAt)
        };
    }

    private static ClientMessageError FromProto(ProtoClientMessageError value)
    {
        return new ClientMessageError
        {
            MessageToken = value.MessageToken,
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            ErrorCode = value.ErrorCode,
            ErrorMessage = value.ErrorMessage,
            FailedAt = FromUnixMs(value.FailedAtUnixMs)
        };
    }

    private static ProtoServerRelayMessage ToProto(ServerRelayMessage value)
    {
        return new ProtoServerRelayMessage
        {
            ClusterId = value.ClusterId ?? "",
            SourceInstanceId = value.SourceInstanceId ?? "",
            MessageToken = value.MessageToken ?? "",
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content ?? "",
            TtlSeconds = value.TtlSeconds,
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
    }

    private static ServerRelayMessage FromProto(ProtoServerRelayMessage value)
    {
        return new ServerRelayMessage
        {
            ClusterId = value.ClusterId,
            SourceInstanceId = value.SourceInstanceId,
            MessageToken = value.MessageToken,
            SourceClientId = value.SourceClientId,
            TargetClientId = value.TargetClientId,
            Content = value.Content,
            TtlSeconds = value.TtlSeconds,
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoServerRelayBatchMessage ToProto(ServerRelayBatchMessage value)
    {
        ProtoServerRelayBatchMessage message = new()
        {
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
        foreach (ServerRelayMessage item in value.Items ?? Enumerable.Empty<ServerRelayMessage>())
        {
            message.Items.Add(ToProto(item));
        }

        return message;
    }

    private static ServerRelayBatchMessage FromProto(ProtoServerRelayBatchMessage value)
    {
        return new ServerRelayBatchMessage
        {
            Items = value.Items.Select(FromProto).ToList(),
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoServerRelayBatchResult ToProto(ServerRelayBatchResult value)
    {
        ProtoServerRelayBatchResult result = new()
        {
            CreatedAtUnixMs = ToUnixMs(value.CreatedAt)
        };
        foreach (ServerRelayBatchResultItem item in value.Items ?? Enumerable.Empty<ServerRelayBatchResultItem>())
        {
            result.Items.Add(new ProtoServerRelayBatchResultItem
            {
                ItemIndex = item.ItemIndex,
                MessageToken = item.MessageToken ?? "",
                Success = item.Success,
                TargetInstanceId = item.TargetInstanceId ?? "",
                ErrorCode = item.ErrorCode ?? "",
                ErrorMessage = item.ErrorMessage ?? ""
            });
        }

        return result;
    }

    private static ServerRelayBatchResult FromProto(ProtoServerRelayBatchResult value)
    {
        return new ServerRelayBatchResult
        {
            Items = value.Items.Select(item => new ServerRelayBatchResultItem
            {
                ItemIndex = item.ItemIndex,
                MessageToken = item.MessageToken,
                Success = item.Success,
                TargetInstanceId = item.TargetInstanceId,
                ErrorCode = item.ErrorCode,
                ErrorMessage = item.ErrorMessage
            }).ToList(),
            CreatedAt = FromUnixMs(value.CreatedAtUnixMs)
        };
    }

    private static ProtoServerHealthState ToProto(ServerHealthState value)
    {
        return value switch
        {
            ServerHealthState.Healthy => ProtoServerHealthState.ProtoServerHealthHealthy,
            ServerHealthState.Degraded => ProtoServerHealthState.ProtoServerHealthDegraded,
            ServerHealthState.Unhealthy => ProtoServerHealthState.ProtoServerHealthUnhealthy,
            _ => ProtoServerHealthState.ProtoServerHealthUnknown
        };
    }

    private static ServerHealthState FromProto(ProtoServerHealthState value)
    {
        return value switch
        {
            ProtoServerHealthState.ProtoServerHealthHealthy => ServerHealthState.Healthy,
            ProtoServerHealthState.ProtoServerHealthDegraded => ServerHealthState.Degraded,
            ProtoServerHealthState.ProtoServerHealthUnhealthy => ServerHealthState.Unhealthy,
            _ => ServerHealthState.Unknown
        };
    }

    private static long ToUnixMs(DateTimeOffset value)
    {
        if (value == default || value < DateTimeOffset.UnixEpoch)
        {
            return 0;
        }

        return value.ToUnixTimeMilliseconds();
    }

    private static DateTimeOffset FromUnixMs(long value)
    {
        return value <= 0
            ? DateTimeOffset.UnixEpoch
            : DateTimeOffset.FromUnixTimeMilliseconds(value);
    }
}
