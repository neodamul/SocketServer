using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using SocketCommon.Model;

namespace SocketControl.Model;

public class BackendServerRegistry
{
    private readonly object syncRoot = new();
    private readonly ConcurrentDictionary<string, BackendServerSnapshot> servers = new();
    private readonly ConcurrentDictionary<string, RouteReservationMessage> reservations = new();
    private readonly ConcurrentDictionary<string, SessionEventMessage> sessions = new();
    private readonly ConcurrentDictionary<string, SessionEventMessage> sessionTombstones = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> zeroConnectionCutoffs = new();
    private readonly ConcurrentDictionary<uint, ClientLocationMessage> clientLocations = new();
    private readonly TimeSpan heartbeatTimeout;
    private readonly IBackendRegistryStore store;
    private long version;
    private static readonly Func<int, int> DefaultTieBreakerIndexSelector = RandomNumberGenerator.GetInt32;

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
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset registerHeartbeatAt = GetRegisterHeartbeatAt(request.StartedAt, now);
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
                LastHeartbeatAt = registerHeartbeatAt,
                UpdatedAt = now,
                Version = Interlocked.Increment(ref this.version)
            },
            (_, existing) =>
            {
                bool restarted = request.StartedAt > existing.StartedAt;
                if (restarted)
                {
                    this.RemoveInstanceState(request.InstanceId);
                }

                existing.ClusterId = request.ClusterId;
                existing.SourceControlNodeId = controlNodeId;
                existing.ServerId = request.ServerId;
                existing.Name = request.Name;
                existing.Host = request.Host;
                existing.Port = request.Port;
                existing.PortRangeStart = request.PortRangeStart;
                existing.PortRangeEnd = request.PortRangeEnd;
                existing.MaxConnections = request.MaxConnections;
                if (restarted)
                {
                    existing.Health = ServerHealthState.Healthy;
                }

                existing.StartedAt = request.StartedAt;
                if (restarted || existing.LastHeartbeatAt == default)
                {
                    existing.LastHeartbeatAt = registerHeartbeatAt;
                }

                existing.UpdatedAt = now;
                existing.Version = Interlocked.Increment(ref this.version);
                return existing;
            });

            RecalculateReservations();
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
                TotalAcceptedClients = heartbeat.TotalAcceptedClients,
                TotalClosedClients = heartbeat.TotalClosedClients,
                TotalRejectedClients = heartbeat.TotalRejectedClients,
                TotalIdleTimeoutClients = heartbeat.TotalIdleTimeoutClients,
                TotalReceivedMessages = heartbeat.TotalReceivedMessages,
                TotalSentMessages = heartbeat.TotalSentMessages,
                TotalReceivedMessageBytes = heartbeat.TotalReceivedMessageBytes,
                TotalSentMessageBytes = heartbeat.TotalSentMessageBytes,
                ListenBacklog = heartbeat.ListenBacklog,
                PendingAcceptCount = heartbeat.PendingAcceptCount,
                IdleTimeoutSeconds = heartbeat.IdleTimeoutSeconds,
                NoDelay = heartbeat.NoDelay,
                MaxPayloadLength = heartbeat.MaxPayloadLength,
                SocketAsyncEventArgsAvailableCount = heartbeat.SocketAsyncEventArgsAvailableCount,
                SocketAsyncEventArgsTotalCreatedCount = heartbeat.SocketAsyncEventArgsTotalCreatedCount,
                SocketAsyncEventArgsInUseCount = heartbeat.SocketAsyncEventArgsInUseCount,
                SocketAsyncEventArgsHighWatermarkInUseCount = heartbeat.SocketAsyncEventArgsHighWatermarkInUseCount,
                SocketAsyncEventArgsGrowthCount = heartbeat.SocketAsyncEventArgsGrowthCount,
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
                existing.TotalAcceptedClients = heartbeat.TotalAcceptedClients;
                existing.TotalClosedClients = heartbeat.TotalClosedClients;
                existing.TotalRejectedClients = heartbeat.TotalRejectedClients;
                existing.TotalIdleTimeoutClients = heartbeat.TotalIdleTimeoutClients;
                existing.TotalReceivedMessages = heartbeat.TotalReceivedMessages;
                existing.TotalSentMessages = heartbeat.TotalSentMessages;
                existing.TotalReceivedMessageBytes = heartbeat.TotalReceivedMessageBytes;
                existing.TotalSentMessageBytes = heartbeat.TotalSentMessageBytes;
                existing.ListenBacklog = heartbeat.ListenBacklog;
                existing.PendingAcceptCount = heartbeat.PendingAcceptCount;
                existing.IdleTimeoutSeconds = heartbeat.IdleTimeoutSeconds;
                existing.NoDelay = heartbeat.NoDelay;
                existing.MaxPayloadLength = heartbeat.MaxPayloadLength;
                existing.SocketAsyncEventArgsAvailableCount = heartbeat.SocketAsyncEventArgsAvailableCount;
                existing.SocketAsyncEventArgsTotalCreatedCount = heartbeat.SocketAsyncEventArgsTotalCreatedCount;
                existing.SocketAsyncEventArgsInUseCount = heartbeat.SocketAsyncEventArgsInUseCount;
                existing.SocketAsyncEventArgsHighWatermarkInUseCount = heartbeat.SocketAsyncEventArgsHighWatermarkInUseCount;
                existing.SocketAsyncEventArgsGrowthCount = heartbeat.SocketAsyncEventArgsGrowthCount;
                existing.ResourceUsage = heartbeat.ResourceUsage;
                existing.LastHeartbeatAt = heartbeat.SentAt;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                existing.Version = Interlocked.Increment(ref this.version);
                return existing;
            });

            snapshot.Health = EvaluateHealth(snapshot, threshold);
            if (snapshot.CurrentConnections == 0)
            {
                MarkZeroConnectionCutoff(snapshot.InstanceId, snapshot.LastHeartbeatAt);
                PruneInstanceSessions(snapshot.InstanceId);
            }
            else
            {
                ClearZeroConnectionCutoff(snapshot.InstanceId);
            }

            RecalculateReservations();
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
                    if (IsNewerServerSnapshot(snapshot, existing))
                    {
                        if (snapshot.StartedAt > existing.StartedAt)
                        {
                            this.RemoveInstanceState(snapshot.InstanceId);
                        }

                        return snapshot;
                    }

                    return existing;
                });

            this.version = Math.Max(this.version, stored.Version);
            if (stored.CurrentConnections == 0)
            {
                MarkZeroConnectionCutoff(stored.InstanceId, stored.LastHeartbeatAt);
                PruneInstanceSessions(stored.InstanceId);
            }
            else
            {
                ClearZeroConnectionCutoff(stored.InstanceId);
            }

            RecalculateReservations();
            this.SaveState();
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

            List<BackendServerSnapshot> candidates = this.servers.Values
                .Where(server => server.Health == ServerHealthState.Healthy)
                .Where(server => !IsHeartbeatExpired(server))
                .Where(server => !request.PreferredServerId.HasValue || server.ServerId == request.PreferredServerId.Value)
                .Where(server => server.AvailableConnections > 0)
                .ToList();
            BackendServerSnapshot? selected = SelectRouteCandidate(candidates);

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

    internal static BackendServerSnapshot? SelectRouteCandidate(IReadOnlyCollection<BackendServerSnapshot> candidates)
    {
        return SelectRouteCandidate(candidates, DefaultTieBreakerIndexSelector);
    }

    internal static BackendServerSnapshot? SelectRouteCandidate(
        IReadOnlyCollection<BackendServerSnapshot> candidates,
        Func<int, int> tieBreakerIndexSelector)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }

        int maxAvailableConnections = candidates.Max(server => server.AvailableConnections);
        int minCurrentConnections = candidates
            .Where(server => server.AvailableConnections == maxAvailableConnections)
            .Min(server => server.CurrentConnections);
        List<BackendServerSnapshot> tiedCandidates = candidates
            .Where(server => server.AvailableConnections == maxAvailableConnections)
            .Where(server => server.CurrentConnections == minCurrentConnections)
            .OrderBy(server => server.InstanceId, StringComparer.Ordinal)
            .ToList();
        if (tiedCandidates.Count == 0)
        {
            return null;
        }

        if (tiedCandidates.Count == 1)
        {
            return tiedCandidates[0];
        }

        int selectedIndex = tieBreakerIndexSelector?.Invoke(tiedCandidates.Count) ?? 0;
        if (selectedIndex < 0 || selectedIndex >= tiedCandidates.Count)
        {
            selectedIndex = 0;
        }

        return tiedCandidates[selectedIndex];
    }

    public void UpsertReservation(RouteReservationMessage reservation)
    {
        lock (this.syncRoot)
        {
            if (this.reservations.TryGetValue(reservation.ReservationId, out RouteReservationMessage? existing))
            {
                if (existing.InstanceId == reservation.InstanceId)
                {
                    this.reservations[reservation.ReservationId] = reservation;
                    this.SaveState();
                    return;
                }

                this.AdjustReservationCount(existing.InstanceId, -1);
            }

            this.reservations[reservation.ReservationId] = reservation;
            this.AdjustReservationCount(reservation.InstanceId, 1);
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

            this.AdjustReservationCount(reservation.InstanceId, -1);
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
            string sessionKey = CreateSessionKey(session);
            if (IsBeforeZeroConnectionCutoff(session))
            {
                this.SaveState();
                return;
            }

            if (this.sessionTombstones.TryGetValue(sessionKey, out SessionEventMessage? tombstone) &&
                IsTombstoneCurrentForSession(tombstone, session))
            {
                this.SaveState();
                return;
            }

            if (this.sessions.TryGetValue(sessionKey, out SessionEventMessage? existingSession) &&
                IsExistingSessionNewer(existingSession, session))
            {
                this.SaveState();
                return;
            }

            this.sessionTombstones.TryRemove(sessionKey, out _);
            this.sessions[sessionKey] = session;
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
            string sessionKey = CreateSessionKey(session);
            if (this.sessions.TryGetValue(sessionKey, out SessionEventMessage? existingSession) &&
                IsExistingSessionNewer(existingSession, session))
            {
                return;
            }

            this.sessions.TryRemove(sessionKey, out _);
            this.sessionTombstones.AddOrUpdate(
                sessionKey,
                session,
                (_, existing) => session.Version >= existing.Version ? session : existing);
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

            if (IsSessionTombstoneCurrent(location.InstanceId, location.SessionId, location.Version))
            {
                this.SaveState();
                return;
            }

            if (!TryGetMatchingSession(location, out _))
            {
                this.SaveState();
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
                session.Version >= existing.Version &&
                !HasNewerMatchingSession(existing, session))
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
                location.Version >= existing.Version &&
                !TryGetMatchingSession(existing, out _))
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
            if (PruneExpiredSessions(DateTimeOffset.UtcNow))
            {
                this.SaveState();
            }

            if (!this.clientLocations.TryGetValue(request.TargetClientId, out ClientLocationMessage? location) ||
                IsSessionTombstoneCurrent(location.InstanceId, location.SessionId, location.Version) ||
                !TryGetMatchingSession(location, out _) ||
                !this.servers.TryGetValue(location.InstanceId, out BackendServerSnapshot? server) ||
                server.Health != ServerHealthState.Healthy ||
                IsHeartbeatExpired(server))
            {
                if (location != null)
                {
                    this.clientLocations.TryRemove(request.TargetClientId, out _);
                    this.SaveState();
                }

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

    public IReadOnlyCollection<BackendServerSnapshot> MarkExpiredServersUnhealthy()
    {
        lock (this.syncRoot)
        {
            Dictionary<string, long> versions = this.servers.ToDictionary(item => item.Key, item => item.Value.Version);
            bool normalized = NormalizeExpiredServers();
            if (!normalized)
            {
                return Array.Empty<BackendServerSnapshot>();
            }

            this.SaveState();
            return this.servers.Values
                .Where(server => server.Health == ServerHealthState.Unhealthy)
                .Where(server => !versions.TryGetValue(server.InstanceId, out long previousVersion) || server.Version != previousVersion)
                .ToArray();
        }
    }

    public BackendRegistryState SnapshotState()
    {
        lock (this.syncRoot)
        {
            ExpireReservations();
            bool recalculated = RecalculateReservations();
            bool normalized = NormalizeExpiredServers();
            bool pruned = PruneExpiredSessions(DateTimeOffset.UtcNow);
            if (recalculated || normalized || pruned)
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
            if (server.CurrentConnections == 0)
            {
                MarkZeroConnectionCutoff(server.InstanceId, server.LastHeartbeatAt);
            }
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

        foreach (SessionEventMessage session in state.SessionTombstones ?? Enumerable.Empty<SessionEventMessage>())
        {
            this.sessionTombstones[CreateSessionKey(session)] = session;
            this.version = Math.Max(this.version, session.Version);
        }

        foreach (ClientLocationMessage location in state.ClientLocations ?? Enumerable.Empty<ClientLocationMessage>())
        {
            this.clientLocations[location.ClientId] = location;
            this.version = Math.Max(this.version, location.Version);
        }

        foreach (string instanceId in this.zeroConnectionCutoffs.Keys)
        {
            PruneInstanceSessions(instanceId);
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
            bool recalculated = RecalculateReservations();
            bool normalized = NormalizeExpiredServers();
            bool pruned = PruneExpiredSessions(DateTimeOffset.UtcNow);
            if (recalculated || normalized || pruned)
            {
                this.SaveState();
            }

            ApplySessionCounts();
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
                TotalSessionCount = currentServers.Sum(server => server.RegisteredSessionCount),
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

    private bool PruneExpiredSessions(DateTimeOffset now)
    {
        bool changed = false;
        DateTimeOffset expiresBefore = now - this.heartbeatTimeout;
        foreach (SessionEventMessage session in this.sessions.Values)
        {
            if (session.LastReceivedAt == default || session.LastReceivedAt >= expiresBefore)
            {
                continue;
            }

            changed |= RemoveSessionWithTombstone(session, session.Version);
        }

        return changed;
    }

    private bool PruneInstanceSessions(string instanceId)
    {
        bool changed = false;
        foreach (SessionEventMessage session in this.sessions.Values.Where(item => item.InstanceId == instanceId))
        {
            changed |= RemoveSessionWithTombstone(session, session.Version);
        }

        return changed;
    }

    private bool RemoveSessionWithTombstone(SessionEventMessage session, long tombstoneVersion)
    {
        SessionEventMessage tombstone = new()
        {
            ClusterId = session.ClusterId,
            SessionId = session.SessionId,
            ClientId = session.ClientId,
            ServerId = session.ServerId,
            InstanceId = session.InstanceId,
            RemoteEndPoint = session.RemoteEndPoint,
            ConnectedAt = session.ConnectedAt,
            LastReceivedAt = session.LastReceivedAt,
            State = "Closed",
            Version = Math.Max(session.Version, tombstoneVersion)
        };
        int before = this.sessions.Count;
        string sessionKey = CreateSessionKey(tombstone);
        this.sessions.TryRemove(sessionKey, out _);
        this.sessionTombstones.AddOrUpdate(
            sessionKey,
            tombstone,
            (_, existing) => tombstone.Version >= existing.Version ? tombstone : existing);
        if (tombstone.ClientId > 0 &&
            this.clientLocations.TryGetValue(tombstone.ClientId, out ClientLocationMessage? location) &&
            location.InstanceId == tombstone.InstanceId &&
            location.SessionId == tombstone.SessionId &&
            tombstone.Version >= location.Version)
        {
            this.clientLocations.TryRemove(tombstone.ClientId, out _);
        }

        return this.sessions.Count != before;
    }

    private bool RecalculateReservations()
    {
        bool changed = false;
        Dictionary<string, int> countsByInstanceId = new(StringComparer.OrdinalIgnoreCase);
        foreach (RouteReservationMessage reservation in this.reservations.Values)
        {
            if (!this.servers.ContainsKey(reservation.InstanceId))
            {
                continue;
            }

            countsByInstanceId[reservation.InstanceId] =
                countsByInstanceId.TryGetValue(reservation.InstanceId, out int count) ? count + 1 : 1;
        }

        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            int reservedConnections = countsByInstanceId.TryGetValue(server.InstanceId, out int count) ? count : 0;
            if (server.ReservedConnections != reservedConnections)
            {
                changed = true;
                server.ReservedConnections = reservedConnections;
            }
        }

        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            int previousAvailable = server.AvailableConnections;
            UpdateAvailable(server);
            changed |= previousAvailable != server.AvailableConnections;
        }

        return changed;
    }

    private void ApplySessionCounts()
    {
        Dictionary<string, int> activeSessionCountsByInstanceId = this.sessions.Values
            .Where(session => session.ClientId > 0)
            .Where(session => !string.Equals(session.State, "ServerDisconnected", StringComparison.OrdinalIgnoreCase))
            .GroupBy(session => session.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (BackendServerSnapshot server in this.servers.Values)
        {
            server.RegisteredSessionCount = activeSessionCountsByInstanceId.TryGetValue(server.InstanceId, out int count) ? count : 0;
            server.StaleConnectionCount = Math.Max(0, server.CurrentConnections - server.RegisteredSessionCount);
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

            bool serverChanged = false;
            if (server.Health != ServerHealthState.Unhealthy ||
                server.CurrentConnections != 0 ||
                server.ReservedConnections != 0 ||
                server.AvailableConnections != 0)
            {
                serverChanged = true;
            }

            server.Health = ServerHealthState.Unhealthy;
            server.CurrentConnections = 0;
            server.ReservedConnections = 0;
            server.AvailableConnections = 0;
            MarkZeroConnectionCutoff(server.InstanceId, now);

            if (PruneInstanceSessions(server.InstanceId))
            {
                serverChanged = true;
            }

            foreach (ClientLocationMessage location in this.clientLocations.Values.Where(item => item.InstanceId == server.InstanceId))
            {
                serverChanged |= this.clientLocations.TryRemove(location.ClientId, out _);
            }

            foreach (RouteReservationMessage reservation in this.reservations.Values.Where(item => item.InstanceId == server.InstanceId))
            {
                serverChanged |= this.reservations.TryRemove(reservation.ReservationId, out _);
            }

            if (serverChanged)
            {
                server.Version = Interlocked.Increment(ref this.version);
                server.UpdatedAt = now;
                changed = true;
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
            SessionTombstones = this.sessionTombstones.Values.ToList(),
            ClientLocations = this.clientLocations.Values.ToList()
        };
    }

    private bool IsHeartbeatExpired(BackendServerSnapshot snapshot)
    {
        return DateTimeOffset.UtcNow - snapshot.LastHeartbeatAt > this.heartbeatTimeout;
    }

    private static DateTimeOffset GetRegisterHeartbeatAt(DateTimeOffset startedAt, DateTimeOffset fallback)
    {
        return startedAt == default ? fallback : startedAt;
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

    private void AdjustReservationCount(string instanceId, int delta)
    {
        if (!this.servers.TryGetValue(instanceId, out BackendServerSnapshot? server))
        {
            return;
        }

        server.ReservedConnections = Math.Max(0, server.ReservedConnections + delta);
        UpdateAvailable(server);
    }

    private static bool IsNewer(ClientLocationMessage candidate, ClientLocationMessage existing)
    {
        return candidate.Version > existing.Version ||
            (candidate.Version == existing.Version && candidate.UpdatedAt >= existing.UpdatedAt);
    }

    private bool IsSessionTombstoneCurrent(string instanceId, long sessionId, long version)
    {
        return this.sessionTombstones.TryGetValue(CreateSessionKey(instanceId, sessionId), out SessionEventMessage? tombstone) &&
            tombstone.Version >= version;
    }

    private static bool IsTombstoneCurrentForSession(SessionEventMessage tombstone, SessionEventMessage session)
    {
        if (session.ConnectedAt != default && tombstone.ConnectedAt != default)
        {
            return session.ConnectedAt <= tombstone.ConnectedAt;
        }

        return tombstone.Version >= session.Version;
    }

    private static bool IsExistingSessionNewer(SessionEventMessage existing, SessionEventMessage incoming)
    {
        if (existing.ConnectedAt != default && incoming.ConnectedAt != default)
        {
            return existing.ConnectedAt > incoming.ConnectedAt ||
                (existing.ConnectedAt == incoming.ConnectedAt && existing.Version > incoming.Version);
        }

        return existing.Version > incoming.Version;
    }

    private bool TryGetMatchingSession(ClientLocationMessage location, out SessionEventMessage? session)
    {
        if (!this.sessions.TryGetValue(CreateSessionKey(location.InstanceId, location.SessionId), out session))
        {
            return false;
        }

        return session.ClientId == location.ClientId;
    }

    private bool HasNewerMatchingSession(ClientLocationMessage existing, SessionEventMessage incoming)
    {
        if (!TryGetMatchingSession(existing, out SessionEventMessage? session))
        {
            return false;
        }

        return session!.ConnectedAt != default &&
            incoming.ConnectedAt != default &&
            session.ConnectedAt > incoming.ConnectedAt;
    }

    private bool IsBeforeZeroConnectionCutoff(SessionEventMessage session)
    {
        if (!this.zeroConnectionCutoffs.TryGetValue(session.InstanceId, out DateTimeOffset cutoff))
        {
            return false;
        }

        return session.ConnectedAt == default || session.ConnectedAt <= cutoff;
    }

    private bool IsInstanceZeroConnectionCutoffActive(string instanceId)
    {
        return this.zeroConnectionCutoffs.ContainsKey(instanceId);
    }

    private void MarkZeroConnectionCutoff(string instanceId, DateTimeOffset cutoff)
    {
        DateTimeOffset normalizedCutoff = cutoff == default ? DateTimeOffset.UtcNow : cutoff;
        this.zeroConnectionCutoffs.AddOrUpdate(
            instanceId,
            normalizedCutoff,
            (_, existing) => normalizedCutoff > existing ? normalizedCutoff : existing);
    }

    private void ClearZeroConnectionCutoff(string instanceId)
    {
        this.zeroConnectionCutoffs.TryRemove(instanceId, out _);
    }

    private void RemoveInstanceState(string instanceId)
    {
        foreach (SessionEventMessage session in this.sessions.Values.Where(item => item.InstanceId == instanceId))
        {
            this.sessions.TryRemove(CreateSessionKey(session), out _);
        }

        foreach (SessionEventMessage session in this.sessionTombstones.Values.Where(item => item.InstanceId == instanceId))
        {
            this.sessionTombstones.TryRemove(CreateSessionKey(session), out _);
        }

        ClearZeroConnectionCutoff(instanceId);

        foreach (ClientLocationMessage location in this.clientLocations.Values.Where(item => item.InstanceId == instanceId))
        {
            this.clientLocations.TryRemove(location.ClientId, out _);
        }

        foreach (RouteReservationMessage reservation in this.reservations.Values.Where(item => item.InstanceId == instanceId))
        {
            this.reservations.TryRemove(reservation.ReservationId, out _);
        }
    }

    private static bool IsNewerServerSnapshot(BackendServerSnapshot candidate, BackendServerSnapshot existing)
    {
        if (candidate.LastHeartbeatAt != existing.LastHeartbeatAt)
        {
            return candidate.LastHeartbeatAt > existing.LastHeartbeatAt;
        }

        if (candidate.UpdatedAt != existing.UpdatedAt)
        {
            return candidate.UpdatedAt >= existing.UpdatedAt;
        }

        return candidate.Version >= existing.Version;
    }

    private static string CreateSessionKey(SessionEventMessage session)
    {
        return CreateSessionKey(session.InstanceId, session.SessionId);
    }

    private static string CreateSessionKey(string instanceId, long sessionId)
    {
        return $"{instanceId}:{sessionId}";
    }
}

public class ControlHealthThreshold
{
    public double DegradedCpuPercent { get; init; } = 85;

    public double DegradedMemoryPercent { get; init; } = 85;

    public double DegradedStoragePercent { get; init; } = 90;
}
