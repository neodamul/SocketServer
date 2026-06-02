using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SocketCommon.Model;

namespace SocketControl.Model;

public class BackendServerRegistry
{
    private readonly ConcurrentDictionary<string, BackendServerSnapshot> servers = new();
    private readonly ConcurrentDictionary<string, RouteReservationMessage> reservations = new();
    private readonly ConcurrentDictionary<long, SessionEventMessage> sessions = new();
    private readonly TimeSpan heartbeatTimeout;
    private long version;

    public BackendServerRegistry()
        : this(TimeSpan.FromSeconds(90))
    {
    }

    public BackendServerRegistry(TimeSpan heartbeatTimeout)
    {
        this.heartbeatTimeout = heartbeatTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(90)
            : heartbeatTimeout;
    }

    public IReadOnlyCollection<BackendServerSnapshot> Servers => this.servers.Values.ToArray();

    public IReadOnlyCollection<RouteReservationMessage> Reservations => this.reservations.Values.ToArray();

    public IReadOnlyCollection<SessionEventMessage> Sessions => this.sessions.Values.ToArray();

    public BackendServerSnapshot Upsert(ServerRegisterRequest request, string controlNodeId)
    {
        BackendServerSnapshot snapshot = this.servers.AddOrUpdate(
            request.InstanceId,
            _ => new BackendServerSnapshot
            {
                ClusterId = request.ClusterId,
                SourceControlNodeId = controlNodeId,
                ServerId = request.ServerId,
                InstanceId = request.InstanceId,
                Name = request.Name,
                Host = request.Host,
                Port = request.Port,
                PortRangeStart = request.PortRangeStart,
                PortRangeEnd = request.PortRangeEnd,
                MaxConnections = request.MaxConnections,
                Health = ServerHealthState.Healthy,
                StartedAt = request.StartedAt,
                LastHeartbeatAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = Interlocked.Increment(ref this.version)
            },
            (_, existing) =>
            {
                existing.ClusterId = request.ClusterId;
                existing.SourceControlNodeId = controlNodeId;
                existing.ServerId = request.ServerId;
                existing.Name = request.Name;
                existing.Host = request.Host;
                existing.Port = request.Port;
                existing.PortRangeStart = request.PortRangeStart;
                existing.PortRangeEnd = request.PortRangeEnd;
                existing.MaxConnections = request.MaxConnections;
                existing.Health = ServerHealthState.Healthy;
                existing.StartedAt = request.StartedAt;
                existing.LastHeartbeatAt = DateTimeOffset.UtcNow;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.Version = Interlocked.Increment(ref this.version);
                return existing;
            });

        UpdateAvailable(snapshot);
        return snapshot;
    }

    public BackendServerSnapshot Upsert(ServerHeartbeatRequest heartbeat, string controlNodeId, ControlHealthThreshold threshold)
    {
        BackendServerSnapshot snapshot = this.servers.AddOrUpdate(
            heartbeat.InstanceId,
            _ => new BackendServerSnapshot
            {
                ClusterId = heartbeat.ClusterId,
                SourceControlNodeId = controlNodeId,
                ServerId = heartbeat.ServerId,
                InstanceId = heartbeat.InstanceId,
                Host = heartbeat.Host,
                Port = heartbeat.Port,
                MaxConnections = heartbeat.MaxConnections,
                CurrentConnections = heartbeat.CurrentConnections,
                ResourceUsage = heartbeat.ResourceUsage,
                LastHeartbeatAt = heartbeat.SentAt,
                UpdatedAt = DateTimeOffset.UtcNow,
                Version = Interlocked.Increment(ref this.version)
            },
            (_, existing) =>
            {
                existing.ClusterId = heartbeat.ClusterId;
                existing.SourceControlNodeId = controlNodeId;
                existing.ServerId = heartbeat.ServerId;
                existing.Host = heartbeat.Host;
                existing.Port = heartbeat.Port;
                existing.MaxConnections = heartbeat.MaxConnections;
                existing.CurrentConnections = heartbeat.CurrentConnections;
                existing.ResourceUsage = heartbeat.ResourceUsage;
                existing.LastHeartbeatAt = heartbeat.SentAt;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.Version = Interlocked.Increment(ref this.version);
                return existing;
            });

        snapshot.Health = EvaluateHealth(snapshot, threshold);
        UpdateAvailable(snapshot);
        return snapshot;
    }

    public void UpsertPeerSnapshot(BackendServerSnapshot snapshot)
    {
        this.servers.AddOrUpdate(
            snapshot.InstanceId,
            snapshot,
            (_, existing) =>
            {
                if (snapshot.Version > existing.Version ||
                    (snapshot.Version == existing.Version && snapshot.UpdatedAt > existing.UpdatedAt))
                {
                    return snapshot;
                }

                return existing;
            });
    }

    public RouteResponse Resolve(RouteRequest request, string controlNodeId, TimeSpan reservationTtl)
    {
        ExpireReservations();

        BackendServerSnapshot? selected = this.servers.Values
            .Where(server => server.Health == ServerHealthState.Healthy)
            .Where(server => !IsHeartbeatExpired(server))
            .Where(server => !request.PreferredServerId.HasValue || server.ServerId == request.PreferredServerId.Value)
            .Where(server => server.AvailableConnections > 0)
            .OrderByDescending(server => server.AvailableConnections)
            .ThenBy(server => server.CurrentConnections)
            .FirstOrDefault();

        if (selected == null)
        {
            return new RouteResponse
            {
                Success = false,
                ErrorMessage = "No available SocketServer instance."
            };
        }

        RouteReservationMessage reservation = new()
        {
            ReservationId = $"{controlNodeId}-{Interlocked.Increment(ref this.version)}",
            ClientId = request.ClientId,
            ServerId = selected.ServerId,
            InstanceId = selected.InstanceId,
            SourceControlNodeId = controlNodeId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(reservationTtl)
        };
        this.reservations[reservation.ReservationId] = reservation;
        selected.ReservedConnections++;
        UpdateAvailable(selected);

        return new RouteResponse
        {
            Success = true,
            ReservationId = reservation.ReservationId,
            ServerId = selected.ServerId,
            InstanceId = selected.InstanceId,
            Host = selected.Host,
            Port = selected.Port,
            ExpiresAt = reservation.ExpiresAt
        };
    }

    public void UpsertReservation(RouteReservationMessage reservation)
    {
        this.reservations[reservation.ReservationId] = reservation;
        if (this.servers.TryGetValue(reservation.InstanceId, out BackendServerSnapshot? server))
        {
            server.ReservedConnections++;
            UpdateAvailable(server);
        }
    }

    public void ReleaseReservation(string reservationId)
    {
        if (!this.reservations.TryRemove(reservationId, out RouteReservationMessage? reservation))
        {
            return;
        }

        if (this.servers.TryGetValue(reservation.InstanceId, out BackendServerSnapshot? server) &&
            server.ReservedConnections > 0)
        {
            server.ReservedConnections--;
            UpdateAvailable(server);
        }
    }

    public RouteReservationMessage? ReleaseReservationFor(uint clientId, string instanceId)
    {
        RouteReservationMessage? reservation = this.reservations.Values
            .Where(item => item.ClientId == clientId && item.InstanceId == instanceId)
            .OrderBy(item => item.ExpiresAt)
            .FirstOrDefault();
        if (reservation != null)
        {
            ReleaseReservation(reservation.ReservationId);
        }

        return reservation;
    }

    public void UpsertSession(SessionEventMessage session)
    {
        this.sessions[session.SessionId] = session;
    }

    public void RemoveSession(SessionEventMessage session)
    {
        this.sessions.TryRemove(session.SessionId, out _);
    }

    public ClusterStatusSnapshot GetStatus()
    {
        ExpireReservations();
        BackendServerSnapshot[] currentServers = this.servers.Values.ToArray();
        return new ClusterStatusSnapshot
        {
            Servers = currentServers,
            ServerCount = currentServers.Length,
            HealthyServerCount = currentServers.Count(server => server.Health == ServerHealthState.Healthy),
            TotalMaxConnections = currentServers.Sum(server => server.MaxConnections),
            TotalCurrentConnections = currentServers.Sum(server => server.CurrentConnections),
            TotalReservedConnections = currentServers.Sum(server => server.ReservedConnections),
            TotalAvailableConnections = currentServers.Sum(server => server.AvailableConnections),
            TotalSessionCount = this.sessions.Count,
            AverageCpuUsagePercent = currentServers.Length == 0 ? 0 : currentServers.Average(server => server.ResourceUsage.CpuUsagePercent),
            AverageMemoryUsagePercent = currentServers.Length == 0 ? 0 : currentServers.Average(server => server.ResourceUsage.MemoryUsagePercent),
            AverageStorageUsagePercent = currentServers.Length == 0 ? 0 : currentServers.Average(server => server.ResourceUsage.StorageUsagePercent),
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    private void ExpireReservations()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (RouteReservationMessage reservation in this.reservations.Values)
        {
            if (reservation.ExpiresAt > now)
            {
                continue;
            }

            ReleaseReservation(reservation.ReservationId);
        }
    }

    private bool IsHeartbeatExpired(BackendServerSnapshot snapshot)
    {
        return DateTimeOffset.UtcNow - snapshot.LastHeartbeatAt > this.heartbeatTimeout;
    }

    private static ServerHealthState EvaluateHealth(BackendServerSnapshot snapshot, ControlHealthThreshold threshold)
    {
        if (snapshot.ResourceUsage.CpuUsagePercent >= threshold.DegradedCpuPercent ||
            snapshot.ResourceUsage.MemoryUsagePercent >= threshold.DegradedMemoryPercent ||
            snapshot.ResourceUsage.StorageUsagePercent >= threshold.DegradedStoragePercent)
        {
            return ServerHealthState.Degraded;
        }

        return ServerHealthState.Healthy;
    }

    private static void UpdateAvailable(BackendServerSnapshot snapshot)
    {
        snapshot.AvailableConnections = Math.Max(
            0,
            snapshot.MaxConnections - snapshot.CurrentConnections - snapshot.ReservedConnections);
    }
}

public class ControlHealthThreshold
{
    public double DegradedCpuPercent { get; init; } = 85;

    public double DegradedMemoryPercent { get; init; } = 85;

    public double DegradedStoragePercent { get; init; } = 90;
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
