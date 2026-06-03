using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using SocketCommon.Model;

namespace SocketControl.Model;

public class BackendServerRegistry
{
    private readonly object syncRoot = new();
    private readonly ConcurrentDictionary<string, BackendServerSnapshot> servers = new();
    private readonly ConcurrentDictionary<string, RouteReservationMessage> reservations = new();
    private readonly ConcurrentDictionary<string, SessionEventMessage> sessions = new();
    private readonly ConcurrentDictionary<uint, ClientLocationMessage> clientLocations = new();
    private readonly TimeSpan heartbeatTimeout;
    private readonly IBackendRegistryStore store;
    private long version;

    public BackendServerRegistry()
        : this(TimeSpan.FromSeconds(90))
    {
    }

    public BackendServerRegistry(TimeSpan heartbeatTimeout)
        : this(heartbeatTimeout, new InMemoryBackendRegistryStore())
    {
    }

    public BackendServerRegistry(TimeSpan heartbeatTimeout, IBackendRegistryStore store)
    {
        this.heartbeatTimeout = heartbeatTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(90)
            : heartbeatTimeout;
        this.store = store ?? new InMemoryBackendRegistryStore();
        this.LoadState(this.store.Load());
    }

    public IReadOnlyCollection<BackendServerSnapshot> Servers => this.servers.Values.ToArray();

    public IReadOnlyCollection<RouteReservationMessage> Reservations => this.reservations.Values.ToArray();

    public IReadOnlyCollection<SessionEventMessage> Sessions => this.sessions.Values.ToArray();

    public IReadOnlyCollection<ClientLocationMessage> ClientLocations => this.clientLocations.Values.ToArray();

    public BackendServerSnapshot Upsert(ServerRegisterRequest request, string controlNodeId)
    {
        lock (this.syncRoot)
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
            this.SaveState();
            return snapshot;
        }
    }

    public BackendServerSnapshot Upsert(ServerHeartbeatRequest heartbeat, string controlNodeId, ControlHealthThreshold threshold)
    {
        lock (this.syncRoot)
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
            this.SaveState();
            return snapshot;
        }
    }

    public void UpsertPeerSnapshot(BackendServerSnapshot snapshot)
    {
        lock (this.syncRoot)
        {
            BackendServerSnapshot stored = this.servers.AddOrUpdate(
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

            this.version = Math.Max(this.version, stored.Version);
            UpdateAvailable(stored);
            if (ReferenceEquals(stored, snapshot))
            {
                this.SaveState();
            }
        }
    }

    public void ImportSnapshot(ClusterStatusSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return;
        }

        lock (this.syncRoot)
        {
            foreach (BackendServerSnapshot server in snapshot.Servers)
            {
                this.UpsertPeerSnapshot(server);
            }

            this.SaveState();
        }
    }

    public RouteResponse Resolve(RouteRequest request, string controlNodeId, TimeSpan reservationTtl)
    {
        lock (this.syncRoot)
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
            this.SaveState();

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
    }

    public void UpsertReservation(RouteReservationMessage reservation)
    {
        lock (this.syncRoot)
        {
            this.reservations[reservation.ReservationId] = reservation;
            if (this.servers.TryGetValue(reservation.InstanceId, out BackendServerSnapshot? server))
            {
                server.ReservedConnections++;
                UpdateAvailable(server);
            }

            this.SaveState();
        }
    }

    public void ReleaseReservation(string reservationId)
    {
        lock (this.syncRoot)
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

            this.SaveState();
        }
    }

    public RouteReservationMessage? ReleaseReservationFor(uint clientId, string instanceId)
    {
        lock (this.syncRoot)
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
    }

    public void UpsertSession(SessionEventMessage session)
    {
        lock (this.syncRoot)
        {
            this.sessions[CreateSessionKey(session)] = session;
            if (session.ClientId == 0 || !this.servers.TryGetValue(session.InstanceId, out BackendServerSnapshot? server))
            {
                this.SaveState();
                return;
            }

            UpsertClientLocation(new ClientLocationMessage
            {
                ClusterId = session.ClusterId,
                ClientId = session.ClientId,
                ServerId = session.ServerId,
                InstanceId = session.InstanceId,
                Host = server.Host,
                Port = server.Port,
                SessionId = session.SessionId,
                State = session.State,
                Version = session.Version,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            this.SaveState();
        }
    }

    public void RemoveSession(SessionEventMessage session)
    {
        lock (this.syncRoot)
        {
            this.sessions.TryRemove(CreateSessionKey(session), out _);
            if (session.ClientId > 0)
            {
                RemoveClientLocation(session);
            }

            this.SaveState();
        }
    }

    public void UpsertClientLocation(ClientLocationMessage location)
    {
        lock (this.syncRoot)
        {
            if (location.ClientId == 0)
            {
                return;
            }

            this.clientLocations.AddOrUpdate(
                location.ClientId,
                location,
                (_, existing) => IsNewer(location, existing) ? location : existing);
            this.SaveState();
        }
    }

    public void RemoveClientLocation(SessionEventMessage session)
    {
        lock (this.syncRoot)
        {
            if (session.ClientId == 0)
            {
                return;
            }

            if (!this.clientLocations.TryGetValue(session.ClientId, out ClientLocationMessage? existing))
            {
                return;
            }

            if (existing.InstanceId == session.InstanceId &&
                existing.SessionId == session.SessionId &&
                session.Version >= existing.Version)
            {
                this.clientLocations.TryRemove(session.ClientId, out _);
                this.SaveState();
            }
        }
    }

    public void RemoveClientLocation(ClientLocationMessage location)
    {
        lock (this.syncRoot)
        {
            if (location.ClientId == 0)
            {
                return;
            }

            if (!this.clientLocations.TryGetValue(location.ClientId, out ClientLocationMessage? existing))
            {
                return;
            }

            if (existing.InstanceId == location.InstanceId &&
                existing.SessionId == location.SessionId &&
                location.Version >= existing.Version)
            {
                this.clientLocations.TryRemove(location.ClientId, out _);
                this.SaveState();
            }
        }
    }

    public ClientLocationResponse ResolveClientLocation(ClientLocationRequest request)
    {
        lock (this.syncRoot)
        {
            if (!this.clientLocations.TryGetValue(request.TargetClientId, out ClientLocationMessage? location) ||
                !this.servers.TryGetValue(location.InstanceId, out BackendServerSnapshot? server) ||
                server.Health != ServerHealthState.Healthy ||
                IsHeartbeatExpired(server))
            {
                return new ClientLocationResponse
                {
                    Success = false,
                    TargetClientId = request.TargetClientId,
                    ErrorMessage = "Target client is not connected to an available SocketServer."
                };
            }

            return new ClientLocationResponse
            {
                Success = true,
                TargetClientId = request.TargetClientId,
                ServerId = location.ServerId,
                InstanceId = location.InstanceId,
                Host = location.Host,
                Port = location.Port,
                SessionId = location.SessionId,
                State = location.State,
                UpdatedAt = location.UpdatedAt
            };
        }
    }

    public BackendServerSnapshot? MarkServerDisconnected(string instanceId)
    {
        lock (this.syncRoot)
        {
            if (!this.servers.TryGetValue(instanceId, out BackendServerSnapshot? server))
            {
                return null;
            }

            server.Health = ServerHealthState.Unhealthy;
            server.CurrentConnections = 0;
            server.ReservedConnections = 0;
            server.AvailableConnections = 0;
            server.UpdatedAt = DateTimeOffset.UtcNow;
            server.LastHeartbeatAt = DateTimeOffset.UtcNow - this.heartbeatTimeout - TimeSpan.FromSeconds(1);
            server.Version = Interlocked.Increment(ref this.version);

            foreach (SessionEventMessage session in this.sessions.Values.Where(item => item.InstanceId == instanceId))
            {
                session.State = "ServerDisconnected";
                session.Version = Math.Max(session.Version, server.Version);
            }

            foreach (ClientLocationMessage location in this.clientLocations.Values.Where(item => item.InstanceId == instanceId))
            {
                location.State = "ServerDisconnected";
                location.Version = Math.Max(location.Version, server.Version);
                location.UpdatedAt = DateTimeOffset.UtcNow;
            }

            this.SaveState();
            return server;
        }
    }

    public BackendRegistryState SnapshotState()
    {
        lock (this.syncRoot)
        {
            ExpireReservations();
            bool normalized = NormalizeExpiredServers();
            if (normalized)
            {
                this.SaveState();
            }

            return CreateState();
        }
    }

    private void LoadState(BackendRegistryState state)
    {
        if (state == null)
        {
            return;
        }

        foreach (BackendServerSnapshot server in state.Servers ?? Enumerable.Empty<BackendServerSnapshot>())
        {
            this.servers[server.InstanceId] = server;
            this.version = Math.Max(this.version, server.Version);
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (RouteReservationMessage reservation in (state.Reservations ?? Enumerable.Empty<RouteReservationMessage>()).Where(item => item.ExpiresAt > now))
        {
            this.reservations[reservation.ReservationId] = reservation;
        }

        foreach (SessionEventMessage session in state.Sessions ?? Enumerable.Empty<SessionEventMessage>())
        {
            this.sessions[CreateSessionKey(session)] = session;
            this.version = Math.Max(this.version, session.Version);
        }

        foreach (ClientLocationMessage location in state.ClientLocations ?? Enumerable.Empty<ClientLocationMessage>())
        {
            this.clientLocations[location.ClientId] = location;
            this.version = Math.Max(this.version, location.Version);
        }

        this.version = Math.Max(this.version, state.Version);
        RecalculateReservations();
        NormalizeExpiredServers();
    }

    private void SaveState()
    {
        this.store.Save(CreateState());
    }

    public ClusterStatusSnapshot GetStatus()
    {
        lock (this.syncRoot)
        {
            ExpireReservations();
            bool normalized = NormalizeExpiredServers();
            if (normalized)
            {
                this.SaveState();
            }

            BackendServerSnapshot[] currentServers = this.servers.Values.ToArray();
            return new ClusterStatusSnapshot
            {
                Servers = currentServers,
                ServerCount = currentServers.Length,
                HealthyServerCount = currentServers.Count(server =>
                    server.Health == ServerHealthState.Healthy && !IsHeartbeatExpired(server)),
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

    private void RecalculateReservations()
    {
        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            server.ReservedConnections = 0;
        }

        foreach (RouteReservationMessage reservation in this.reservations.Values)
        {
            if (this.servers.TryGetValue(reservation.InstanceId, out BackendServerSnapshot? server))
            {
                server.ReservedConnections++;
            }
        }

        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            UpdateAvailable(server);
        }
    }

    private bool NormalizeExpiredServers()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        bool changed = false;
        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            if (now - server.LastHeartbeatAt <= this.heartbeatTimeout)
            {
                UpdateAvailable(server);
                continue;
            }

            if (server.Health != ServerHealthState.Unhealthy ||
                server.CurrentConnections != 0 ||
                server.ReservedConnections != 0 ||
                server.AvailableConnections != 0)
            {
                changed = true;
            }

            server.Health = ServerHealthState.Unhealthy;
            server.CurrentConnections = 0;
            server.ReservedConnections = 0;
            server.AvailableConnections = 0;

            foreach (SessionEventMessage session in this.sessions.Values.Where(item => item.InstanceId == server.InstanceId))
            {
                if (session.State != "ServerDisconnected")
                {
                    changed = true;
                }

                session.State = "ServerDisconnected";
                session.Version = Math.Max(session.Version, server.Version);
            }

            foreach (ClientLocationMessage location in this.clientLocations.Values.Where(item => item.InstanceId == server.InstanceId))
            {
                if (location.State != "ServerDisconnected")
                {
                    changed = true;
                }

                location.State = "ServerDisconnected";
                location.Version = Math.Max(location.Version, server.Version);
                location.UpdatedAt = now;
            }

            foreach (RouteReservationMessage reservation in this.reservations.Values.Where(item => item.InstanceId == server.InstanceId))
            {
                changed |= this.reservations.TryRemove(reservation.ReservationId, out _);
            }
        }

        return changed;
    }

    private BackendRegistryState CreateState()
    {
        return new BackendRegistryState
        {
            Version = this.version,
            Servers = this.servers.Values.ToList(),
            Reservations = this.reservations.Values.ToList(),
            Sessions = this.sessions.Values.ToList(),
            ClientLocations = this.clientLocations.Values.ToList()
        };
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

    private static bool IsNewer(ClientLocationMessage candidate, ClientLocationMessage existing)
    {
        return candidate.Version > existing.Version ||
            (candidate.Version == existing.Version && candidate.UpdatedAt >= existing.UpdatedAt);
    }

    private static string CreateSessionKey(SessionEventMessage session)
    {
        return $"{session.InstanceId}:{session.SessionId}";
    }
}

public class ControlHealthThreshold
{
    public double DegradedCpuPercent { get; init; } = 85;

    public double DegradedMemoryPercent { get; init; } = 85;

    public double DegradedStoragePercent { get; init; } = 90;
}
