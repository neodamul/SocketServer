using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketControl.Model;

public class ControlServer : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<ControlServer>();
    private static readonly SocketLogger RelayLogger = SocketLogManager.GetRelayLogger<ControlServer>();

    private readonly ControlServerNodeConfig config;
    private readonly IReadOnlyCollection<EndpointConfig> peers;
    private readonly BackendServerRegistry registry;
    private readonly ControlHealthThreshold healthThreshold;
    private readonly ConcurrentDictionary<long, ActiveControlConnection> activeConnections = new();
    private Socket? listener;
    private Socket? peerListener;
    private CancellationTokenSource? cancellation;
    private Task? acceptTask;
    private Task? peerAcceptTask;
    private Task? connectionCleanupTask;
    private Task? peerSnapshotSyncTask;
    private long nextActiveSocketId;
    private bool disposedValue;

    public ControlServer(ControlServerConfigFile config)
    {
        this.config = config.ControlServer;
        this.peers = config.Peers;
        this.registry = new BackendServerRegistry(
            TimeSpan.FromSeconds(config.ControlServer.HeartbeatTimeoutSeconds),
            BackendRegistryStoreFactory.Create(config.Registry, this.config.NodeId));
        this.healthThreshold = new ControlHealthThreshold
        {
            DegradedCpuPercent = this.config.DegradedCpuPercent,
            DegradedMemoryPercent = this.config.DegradedMemoryPercent,
            DegradedStoragePercent = this.config.DegradedStoragePercent
        };
    }

    public BackendServerRegistry Registry => this.registry;

    public int ActiveConnectionCount => this.activeConnections.Count;

    public int Port => this.listener?.LocalEndPoint is IPEndPoint endPoint ? endPoint.Port : this.config.Port;

    public bool Start()
    {
        if (this.listener != null)
        {
            return true;
        }

        try
        {
            this.listener = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
            this.listener.Bind(new IPEndPoint(IPAddress.Parse(this.config.Host), this.config.Port));
            this.listener.Listen(SocketFactory.ListenBacklog);
            LocalCertificateStore.GetOrCreate("SocketControl");
            this.cancellation = new CancellationTokenSource();
            this.acceptTask = this.RunAcceptLoopAsync(this.cancellation.Token);
            this.connectionCleanupTask = this.RunConnectionCleanupLoopAsync(this.cancellation.Token);
            this.peerSnapshotSyncTask = this.RunPeerSnapshotSyncLoopAsync(this.cancellation.Token);
            if (this.config.PeerSyncPort > 0 && this.config.PeerSyncPort != this.config.Port)
            {
                this.peerListener = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                this.peerListener.Bind(new IPEndPoint(IPAddress.Parse(this.config.Host), this.config.PeerSyncPort));
                this.peerListener.Listen(SocketFactory.ListenBacklog);
                this.peerAcceptTask = this.RunAcceptLoopAsync(this.cancellation.Token, this.peerListener);
            }

            Logger.Info($"ControlServer started. nodeId={this.config.NodeId}, endpoint={this.config.Host}:{this.Port}");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer start failed. endpoint={this.config.Host}:{this.config.Port}", exception);
            return false;
        }
    }

    public void Stop()
    {
        this.StopAsync().GetAwaiter().GetResult();
    }

    public Task StopAsync()
    {
        return this.StopAsync(TimeSpan.FromSeconds(5));
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        this.cancellation?.Cancel();
        this.listener?.Dispose();
        this.peerListener?.Dispose();
        foreach (ActiveControlConnection connection in this.activeConnections.Values)
        {
            connection.Socket.Dispose();
        }

        await WaitForTaskAsync(this.acceptTask, timeout);
        await WaitForTaskAsync(this.peerAcceptTask, timeout);
        await WaitForTaskAsync(this.connectionCleanupTask, timeout);
        await WaitForTaskAsync(this.peerSnapshotSyncTask, timeout);

        this.listener = null;
        this.peerListener = null;
        this.acceptTask = null;
        this.peerAcceptTask = null;
        this.connectionCleanupTask = null;
        this.peerSnapshotSyncTask = null;
        this.cancellation?.Dispose();
        this.cancellation = null;
        Logger.Info($"ControlServer stopped. nodeId={this.config.NodeId}");
    }

    public ClusterStatusSnapshot GetClusterStatus()
    {
        return this.registry.GetStatus();
    }

    private Task RunAcceptLoopAsync(CancellationToken cancellationToken)
    {
        return this.RunAcceptLoopAsync(cancellationToken, this.listener);
    }

    private async Task RunAcceptLoopAsync(CancellationToken cancellationToken, Socket? activeListener)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket? client = null;
            try
            {
                if (activeListener == null)
                {
                    break;
                }

                client = await SocketAsyncEventArgsTransport.AcceptAsync(activeListener);
                if (client == null)
                {
                    continue;
                }

                _ = Task.Run(() => this.HandleConnectionAsync(client), cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    Logger.Warn("ControlServer accept failed.", exception);
                }

                client?.Dispose();
            }
        }
    }

    private async Task HandleConnectionAsync(Socket socket)
    {
        long activeSocketId = Interlocked.Increment(ref this.nextActiveSocketId);
        ActiveControlConnection activeConnection = new(socket);
        this.activeConnections[activeSocketId] = activeConnection;
        SecureSocketConnection? acceptedConnection = null;
        string serverInstanceId = "";
        try
        {
            acceptedConnection = await SecureSocketConnection.AuthenticateServerAsync(socket, "SocketControl");
            while (true)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(acceptedConnection);
                if (!success)
                {
                    break;
                }

                activeConnection.MarkReceived();
                switch (frame.MessageId)
                {
                    case ControlMessageIds.ServerRegister:
                        serverInstanceId = GetServerRegisterInstanceId(frame);
                        activeConnection.ServerInstanceId = serverInstanceId;
                        await HandleServerRegisterAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ServerHeartbeat:
                        serverInstanceId = GetServerHeartbeatInstanceId(frame, serverInstanceId);
                        activeConnection.ServerInstanceId = serverInstanceId;
                        await HandleServerHeartbeatAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.SessionOpened:
                    case ControlMessageIds.SessionUpdated:
                    case ControlMessageIds.SessionClosed:
                        await HandleSessionEventAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.RouteRequest:
                        await HandleRouteRequestAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ClientLocationRequest:
                        await HandleClientLocationRequestAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ServerRegistryUpsert:
                        await HandlePeerServerUpsertAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.SessionSummaryUpsert:
                        await HandlePeerSessionUpsertAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.SessionSummaryRemove:
                        await HandlePeerSessionRemoveAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ClientLocationUpsert:
                        await HandlePeerClientLocationUpsertAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ClientLocationRemove:
                        await HandlePeerClientLocationRemoveAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.RouteReservationUpsert:
                        await HandlePeerReservationUpsertAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.RouteReservationRelease:
                        await HandlePeerReservationReleaseAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.RegistrySnapshotRequest:
                        await HandleRegistrySnapshotRequestAsync(acceptedConnection, frame);
                        break;
                    default:
                        Logger.Warn($"ControlServer received unknown message. messageId={frame.MessageId}");
                        break;
                }
            }
        }
        catch (SocketException exception)
        {
            if (!this.IsStopping())
            {
                Logger.Warn("ControlServer connection failed.", exception);
            }
        }
        catch (IOException exception)
        {
            if (!this.IsStopping())
            {
                Logger.Warn("ControlServer secure connection failed.", exception);
            }
        }
        catch (AuthenticationException exception)
        {
            if (!this.IsStopping())
            {
                Logger.Warn("ControlServer authentication failed.", exception);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            acceptedConnection?.Dispose();
            this.activeConnections.TryRemove(activeSocketId, out _);
            string disconnectedInstanceId = string.IsNullOrWhiteSpace(serverInstanceId)
                ? activeConnection.ServerInstanceId
                : serverInstanceId;
            if (!string.IsNullOrWhiteSpace(disconnectedInstanceId))
            {
                Logger.Debug($"ControlServer request channel closed. instanceId={disconnectedInstanceId}");
            }
        }
    }

    private async Task RunConnectionCleanupLoopAsync(CancellationToken cancellationToken)
    {
        TimeSpan cleanupInterval = GetConnectionCleanupInterval();
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(cleanupInterval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await CleanupUnhealthyConnectionsAsync();
        }
    }

    private async Task CleanupUnhealthyConnectionsAsync()
    {
        TimeSpan heartbeatTimeout = GetHeartbeatTimeout();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<long, ActiveControlConnection> item in this.activeConnections)
        {
            ActiveControlConnection connection = item.Value;
            if (now - connection.LastReceivedAt <= heartbeatTimeout)
            {
                continue;
            }

            Logger.Warn($"ControlServer closing stale control connection. activeSocketId={item.Key}, instanceId={connection.ServerInstanceId}");
            connection.Socket.Dispose();
        }

        foreach (BackendServerSnapshot snapshot in this.registry.MarkExpiredServersUnhealthy())
        {
            Logger.Warn($"SocketServer heartbeat expired. instanceId={snapshot.InstanceId}");
            await PublishServerSnapshotAsync(snapshot);
        }
    }

    private async Task HandleServerRegisterAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.ServerRegister, out ServerRegisterRequest request))
        {
            return;
        }

        BackendServerSnapshot snapshot = this.registry.Upsert(request, this.config.NodeId);
        Logger.Info($"SocketServer registered. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, endpoint={request.Host}:{request.Port}, maxConnections={request.MaxConnections}, pendingAcceptCount={request.PendingAcceptCount}");
        RelayLogger.Info($"Server registry upsert received. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, endpoint={request.Host}:{request.Port}, version={snapshot.Version}, health={snapshot.Health}");
        await ControlProtocol.SendAsync(connection, frame.ClientId, ControlMessageIds.ServerRegisterAck, new ServerRegisterAck
        {
            ClusterId = this.config.ClusterId,
            ControlNodeId = this.config.NodeId,
            ServerId = request.ServerId,
            InstanceId = request.InstanceId,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await PublishServerSnapshotAsync(snapshot);
    }

    private async Task HandleServerHeartbeatAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.ServerHeartbeat, out ServerHeartbeatRequest request))
        {
            return;
        }

        BackendServerSnapshot snapshot = this.registry.Upsert(request, this.config.NodeId, this.healthThreshold);
        Logger.Debug($"SocketServer heartbeat received. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, current={request.CurrentConnections}, available={request.AvailableConnections}, cpu={request.ResourceUsage.CpuUsagePercent}, memory={request.ResourceUsage.MemoryUsagePercent}, storage={request.ResourceUsage.StorageUsagePercent}, health={snapshot.Health}");
        RelayLogger.Debug($"Server heartbeat registry upsert received. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, version={snapshot.Version}, health={snapshot.Health}, current={snapshot.CurrentConnections}, available={snapshot.AvailableConnections}");
        await ControlProtocol.SendAsync(connection, frame.ClientId, ControlMessageIds.ServerHeartbeatAck, new ServerHeartbeatAck
        {
            ClusterId = this.config.ClusterId,
            ControlNodeId = this.config.NodeId,
            ServerId = request.ServerId,
            InstanceId = request.InstanceId,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await PublishServerSnapshotAsync(snapshot);
    }

    private async Task HandleSessionEventAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, frame.MessageId, out SessionEventMessage sessionEvent))
        {
            return;
        }

        RouteReservationMessage? releasedReservation = null;
        if (frame.MessageId == ControlMessageIds.SessionOpened)
        {
            Logger.Info($"Session opened. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Info($"Session summary upsert started. event=opened, controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.UpsertSession(sessionEvent);
            releasedReservation = this.registry.ReleaseReservationFor(sessionEvent.ClientId, sessionEvent.InstanceId);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryUpsert);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationUpsert);
            if (releasedReservation != null)
            {
                RelayLogger.Info($"Route reservation released by session open. controlNodeId={this.config.NodeId}, reservationId={releasedReservation.ReservationId}, clientId={releasedReservation.ClientId}, instanceId={releasedReservation.InstanceId}");
                await PublishReservationReleaseAsync(releasedReservation);
            }
        }
        else if (frame.MessageId == ControlMessageIds.SessionUpdated)
        {
            Logger.Debug($"Session updated. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Debug($"Session summary upsert started. event=updated, controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.UpsertSession(sessionEvent);
            releasedReservation = this.registry.ReleaseReservationFor(sessionEvent.ClientId, sessionEvent.InstanceId);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryUpsert);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationUpsert);
            if (releasedReservation != null)
            {
                RelayLogger.Info($"Route reservation released by session update. controlNodeId={this.config.NodeId}, reservationId={releasedReservation.ReservationId}, clientId={releasedReservation.ClientId}, instanceId={releasedReservation.InstanceId}");
                await PublishReservationReleaseAsync(releasedReservation);
            }
        }
        else if (frame.MessageId == ControlMessageIds.SessionClosed)
        {
            Logger.Info($"Session closed. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Info($"Session summary remove started. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.RemoveSession(sessionEvent);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryRemove);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationRemove);
        }

        await ControlProtocol.SendAsync(connection, frame.ClientId, ControlMessageIds.ServerHeartbeatAck, new ServerHeartbeatAck
        {
            ClusterId = this.config.ClusterId,
            ControlNodeId = this.config.NodeId,
            ServerId = sessionEvent.ServerId,
            InstanceId = sessionEvent.InstanceId,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task HandleClientLocationRequestAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationRequest, out ClientLocationRequest request))
        {
            return;
        }

        ClientLocationResponse response = this.registry.ResolveClientLocation(request);
        RelayLogger.Info($"Client location resolved. controlNodeId={this.config.NodeId}, sourceClientId={request.SourceClientId}, targetClientId={request.TargetClientId}, success={response.Success}, targetInstanceId={response.InstanceId}");
        await ControlProtocol.SendAsync(
            connection,
            request.SourceClientId,
            response.Success ? ControlMessageIds.ClientLocationResponse : ControlMessageIds.ClientLocationNotFound,
            response);
    }

    private async Task HandleRouteRequestAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.RouteRequest, out RouteRequest request))
        {
            return;
        }

        RouteResponse response = this.registry.Resolve(
            request,
            this.config.NodeId,
            TimeSpan.FromSeconds(Math.Max(1, this.config.RouteReservationSeconds)));
        Logger.Info($"Route request resolved. controlNodeId={this.config.NodeId}, clientId={request.ClientId}, success={response.Success}, instanceId={response.InstanceId}, endpoint={response.Host}:{response.Port}, reason={response.ErrorMessage}");
        RelayLogger.Info($"Route reservation evaluated. controlNodeId={this.config.NodeId}, clientId={request.ClientId}, success={response.Success}, instanceId={response.InstanceId}, reservationId={response.ReservationId}");
        await ControlProtocol.SendAsync(connection, request.ClientId, ControlMessageIds.RouteResponse, response);
        if (response.Success)
        {
            RouteReservationMessage? reservation = this.registry.Reservations
                .FirstOrDefault(item => item.ReservationId == response.ReservationId);
            if (reservation != null)
            {
                RelayLogger.Info($"Route reservation upsert started. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}, expiresAt={reservation.ExpiresAt}");
                await PublishReservationAsync(reservation);
            }
        }
    }

    private async Task HandlePeerServerUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.ServerRegistryUpsert, out BackendServerSnapshot snapshot))
        {
            return;
        }

        this.registry.UpsertPeerSnapshot(snapshot);
        RelayLogger.Info($"Peer server registry upsert applied. controlNodeId={this.config.NodeId}, instanceId={snapshot.InstanceId}, sourceNodeId={snapshot.SourceControlNodeId}, version={snapshot.Version}, health={snapshot.Health}");
        await ControlProtocol.SendAsync(connection, frame.ClientId, ControlMessageIds.ControlRegisterAck, new ControlAckMessage
        {
            ClusterId = this.config.ClusterId,
            ControlNodeId = this.config.NodeId,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private async Task HandlePeerSessionUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.SessionSummaryUpsert, out SessionEventMessage session))
        {
            this.registry.UpsertSession(session);
            RelayLogger.Debug($"Peer session summary upsert applied. controlNodeId={this.config.NodeId}, instanceId={session.InstanceId}, sessionId={session.SessionId}, clientId={session.ClientId}, version={session.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerSessionRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.SessionSummaryRemove, out SessionEventMessage session))
        {
            this.registry.RemoveSession(session);
            RelayLogger.Debug($"Peer session summary remove applied. controlNodeId={this.config.NodeId}, instanceId={session.InstanceId}, sessionId={session.SessionId}, clientId={session.ClientId}, version={session.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationUpsert, out ClientLocationMessage location))
        {
            this.registry.UpsertClientLocation(location);
            RelayLogger.Debug($"Peer client location upsert applied. controlNodeId={this.config.NodeId}, clientId={location.ClientId}, instanceId={location.InstanceId}, endpoint={location.Host}:{location.Port}, version={location.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationRemove, out ClientLocationMessage location))
        {
            this.registry.RemoveClientLocation(location);
            RelayLogger.Debug($"Peer client location remove applied. controlNodeId={this.config.NodeId}, clientId={location.ClientId}, instanceId={location.InstanceId}, version={location.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationUpsert, out RouteReservationMessage reservation))
        {
            this.registry.UpsertReservation(reservation);
            RelayLogger.Debug($"Peer route reservation upsert applied. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationReleaseAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationRelease, out RouteReservationMessage reservation))
        {
            this.registry.ReleaseReservation(reservation.ReservationId);
            RelayLogger.Debug($"Peer route reservation release applied. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandleRegistrySnapshotRequestAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        ClusterStatusSnapshot status = this.registry.GetStatus();
        RelayLogger.Info($"Registry snapshot response started. controlNodeId={this.config.NodeId}, servers={status.ServerCount}, sessions={status.TotalSessionCount}, current={status.TotalCurrentConnections}, available={status.TotalAvailableConnections}");
        await ControlProtocol.SendAsync(
            connection,
            frame.ClientId,
            ControlMessageIds.RegistrySnapshotResponse,
            status);
    }

    private async Task PublishServerSnapshotAsync(BackendServerSnapshot snapshot)
    {
        await PublishAsync(ControlMessageIds.ServerRegistryUpsert, snapshot);
    }

    private async Task PublishSessionAsync(SessionEventMessage session, uint messageId)
    {
        await PublishAsync(messageId, session);
    }

    private async Task PublishClientLocationAsync(SessionEventMessage session, uint messageId)
    {
        if (session.ClientId == 0)
        {
            return;
        }

        BackendServerSnapshot? server = this.registry.Servers.FirstOrDefault(item => item.InstanceId == session.InstanceId);
        if (server == null)
        {
            return;
        }

        await PublishAsync(messageId, new ClientLocationMessage
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
    }

    private async Task PublishReservationAsync(RouteReservationMessage reservation)
    {
        await PublishAsync(ControlMessageIds.RouteReservationUpsert, reservation);
    }

    private async Task PublishReservationReleaseAsync(RouteReservationMessage reservation)
    {
        await PublishAsync(ControlMessageIds.RouteReservationRelease, reservation);
    }

    private async Task PublishAsync<T>(uint messageId, T payload)
    {
        List<Task> tasks = new();
        RelayLogger.Debug($"Control peer relay fanout started. controlNodeId={this.config.NodeId}, messageId={messageId}, peerCount={this.peers.Count}, payloadType={typeof(T).Name}");
        foreach (EndpointConfig peer in this.peers)
        {
            tasks.Add(PublishToPeerAsync(peer, messageId, payload));
        }

        await Task.WhenAll(tasks);
        RelayLogger.Debug($"Control peer relay fanout completed. controlNodeId={this.config.NodeId}, messageId={messageId}, peerCount={this.peers.Count}, payloadType={typeof(T).Name}");
    }

    private async Task PublishToPeerAsync<T>(EndpointConfig peer, uint messageId, T payload)
    {
        try
        {
            RelayLogger.Debug($"Control peer relay send started. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}");
            Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
            await SocketFactory.ConnectAsync(socket, IPAddress.Parse(peer.Host), peer.Port);
            using SecureSocketConnection connection =
                await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketControl");
            await ControlProtocol.SendAndReceiveAsync(
                connection,
                0,
                messageId,
                payload);
            RelayLogger.Info($"Control peer relay send completed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}");
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer peer event relay failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay socket failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}", exception);
        }
        catch (IOException exception)
        {
            Logger.Warn($"ControlServer peer event relay I/O failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay I/O failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}", exception);
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"ControlServer peer event relay authentication failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay authentication failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}", exception);
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"ControlServer peer event relay timed out. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay timed out. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={typeof(T).Name}", exception);
        }
    }

    private async Task RunPeerSnapshotSyncLoopAsync(CancellationToken cancellationToken)
    {
        if (this.peers.Count == 0)
        {
            return;
        }

        TimeSpan syncInterval = GetPeerSnapshotSyncInterval();
        while (!cancellationToken.IsCancellationRequested)
        {
            await SyncRegistryFromPeersAsync();
            try
            {
                await Task.Delay(syncInterval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SyncRegistryFromPeersAsync()
    {
        foreach (EndpointConfig peer in this.peers)
        {
            try
            {
                RelayLogger.Debug($"Control peer snapshot sync started. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}");
                Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                await SocketFactory.ConnectAsync(socket, IPAddress.Parse(peer.Host), peer.Port);
                using SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketControl");
                (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
                    connection,
                    0,
                    ControlMessageIds.RegistrySnapshotRequest,
                    new RegistrySnapshotRequest { RequestedAt = DateTimeOffset.UtcNow });
                if (success &&
                    frame.MessageId == ControlMessageIds.RegistrySnapshotResponse &&
                    ControlProtocol.TryDecode(frame, ControlMessageIds.RegistrySnapshotResponse, out ClusterStatusSnapshot snapshot))
                {
                    this.registry.ImportSnapshot(snapshot);
                    Logger.Info($"ControlServer registry snapshot imported. peer={peer.Host}:{peer.Port}, servers={snapshot.ServerCount}");
                    RelayLogger.Info($"Control peer snapshot imported. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, servers={snapshot.ServerCount}, sessions={snapshot.TotalSessionCount}, current={snapshot.TotalCurrentConnections}, available={snapshot.TotalAvailableConnections}");
                }
            }
            catch (SocketException exception)
            {
                Logger.Warn($"ControlServer peer snapshot sync failed. peer={peer.Host}:{peer.Port}", exception);
                RelayLogger.Warn($"Control peer snapshot sync socket failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}", exception);
            }
            catch (IOException exception)
            {
                Logger.Warn($"ControlServer peer snapshot sync I/O failed. peer={peer.Host}:{peer.Port}", exception);
                RelayLogger.Warn($"Control peer snapshot sync I/O failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}", exception);
            }
            catch (AuthenticationException exception)
            {
                Logger.Warn($"ControlServer peer snapshot sync authentication failed. peer={peer.Host}:{peer.Port}", exception);
                RelayLogger.Warn($"Control peer snapshot sync authentication failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"ControlServer peer snapshot sync timed out. peer={peer.Host}:{peer.Port}", exception);
                RelayLogger.Warn($"Control peer snapshot sync timed out. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}", exception);
            }
        }
    }

    private Task SendPeerAckAsync(SecureSocketConnection connection, uint clientId)
    {
        return ControlProtocol.SendAsync(connection, clientId, ControlMessageIds.ControlRegisterAck, new ControlAckMessage
        {
            ClusterId = this.config.ClusterId,
            ControlNodeId = this.config.NodeId,
            ReceivedAt = DateTimeOffset.UtcNow
        });
    }

    private static string GetServerRegisterInstanceId(SocketMessageFrame frame)
    {
        return ControlProtocol.TryDecode(frame, ControlMessageIds.ServerRegister, out ServerRegisterRequest request)
            ? request.InstanceId
            : "";
    }

    private static string GetServerHeartbeatInstanceId(SocketMessageFrame frame, string fallback)
    {
        return ControlProtocol.TryDecode(frame, ControlMessageIds.ServerHeartbeat, out ServerHeartbeatRequest request)
            ? request.InstanceId
            : fallback;
    }

    private TimeSpan GetHeartbeatTimeout()
    {
        return TimeSpan.FromSeconds(this.config.HeartbeatTimeoutSeconds <= 0 ? 90 : this.config.HeartbeatTimeoutSeconds);
    }

    private TimeSpan GetConnectionCleanupInterval()
    {
        double heartbeatTimeoutMilliseconds = GetHeartbeatTimeout().TotalMilliseconds;
        double intervalMilliseconds = Math.Min(5000, Math.Max(250, heartbeatTimeoutMilliseconds / 2));
        return TimeSpan.FromMilliseconds(intervalMilliseconds);
    }

    private TimeSpan GetPeerSnapshotSyncInterval()
    {
        return TimeSpan.FromSeconds(this.config.PeerSnapshotSyncIntervalSeconds <= 0
            ? 30
            : this.config.PeerSnapshotSyncIntervalSeconds);
    }

    private static async Task WaitForTaskAsync(Task? task, TimeSpan timeout)
    {
        if (task == null || task.IsCompleted)
        {
            return;
        }

        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            return;
        }

        try
        {
            await task;
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private bool IsStopping()
    {
        return this.disposedValue || this.cancellation?.IsCancellationRequested == true;
    }

    private sealed class ActiveControlConnection
    {
        private long lastReceivedAtTicks;

        public ActiveControlConnection(Socket socket)
        {
            this.Socket = socket;
            this.MarkReceived();
        }

        public Socket Socket { get; }

        public string ServerInstanceId { get; set; } = "";

        public DateTimeOffset LastReceivedAt => new(Interlocked.Read(ref this.lastReceivedAtTicks), TimeSpan.Zero);

        public void MarkReceived()
        {
            Interlocked.Exchange(ref this.lastReceivedAtTicks, DateTimeOffset.UtcNow.Ticks);
        }
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.Stop();
            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }
}
