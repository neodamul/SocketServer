using System;

namespace SocketServer.Model;

public class TcpServerStatus
{
    public int ServerId { get; init; }

    public string InstanceId { get; init; }

    public bool IsSocketInitialized { get; init; }

    public bool IsBound { get; init; }

    public bool IsListening { get; init; }

    public bool IsAcceptLoopRunning { get; init; }

    public string IpAddress { get; init; }

    public int Port { get; init; }

    public int ConnectedClientCount { get; init; }

    public int MaxConnections { get; init; }

    public int AvailableConnections { get; init; }

    public int PendingAcceptCount { get; init; }

    public int IdleTimeoutSeconds { get; init; }

    public long TotalAcceptedClients { get; init; }

    public long TotalClosedClients { get; init; }

    public long TotalRejectedClients { get; init; }

    public long TotalIdleTimeoutClients { get; init; }

    public long TotalReceivedMessages { get; init; }

    public long TotalSentMessages { get; init; }

    public int ListenBacklog { get; init; }

    public bool NoDelay { get; init; }

    public int MaxPayloadLength { get; init; }

    public int SocketAsyncEventArgsAvailableCount { get; init; }

    public int SocketAsyncEventArgsTotalCreatedCount { get; init; }

    public int SocketAsyncEventArgsInUseCount { get; init; }

    public int SocketAsyncEventArgsHighWatermarkInUseCount { get; init; }

    public int SocketAsyncEventArgsGrowthCount { get; init; }

    public DateTimeOffset? StartedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}
