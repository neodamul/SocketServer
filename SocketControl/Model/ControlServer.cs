using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketControl.Model;

public class ControlServer : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<ControlServer>();

    private readonly ControlServerNodeConfig config;
    private readonly IReadOnlyCollection<EndpointConfig> peers;
    private readonly BackendServerRegistry registry;
    private readonly ControlHealthThreshold healthThreshold;
    private Socket? listener;
    private Socket? peerListener;
    private CancellationTokenSource? cancellation;
    private Task? acceptTask;
    private Task? peerAcceptTask;
    private bool disposedValue;

    public ControlServer(ControlServerConfigFile config)
    {
        this.config = config.ControlServer;
        this.peers = config.Peers;
        this.registry = new BackendServerRegistry(TimeSpan.FromSeconds(config.ControlServer.HeartbeatTimeoutSeconds));
        this.healthThreshold = new ControlHealthThreshold
        {
            DegradedCpuPercent = this.config.DegradedCpuPercent,
            DegradedMemoryPercent = this.config.DegradedMemoryPercent,
            DegradedStoragePercent = this.config.DegradedStoragePercent
        };
    }

    public BackendServerRegistry Registry => this.registry;

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
            if (this.config.PeerSyncPort > 0 && this.config.PeerSyncPort != this.config.Port)
            {
                this.peerListener = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                this.peerListener.Bind(new IPEndPoint(IPAddress.Parse(this.config.Host), this.config.PeerSyncPort));
                this.peerListener.Listen(SocketFactory.ListenBacklog);
                this.peerAcceptTask = this.RunAcceptLoopAsync(this.cancellation.Token, this.peerListener);
            }

            _ = Task.Run(this.SyncRegistryFromPeersAsync);
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
        this.cancellation?.Cancel();
        this.listener?.Dispose();
        this.peerListener?.Dispose();
        this.listener = null;
        this.peerListener = null;
        this.acceptTask = null;
        this.peerAcceptTask = null;
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
        using SecureSocketConnection acceptedConnection =
            await SecureSocketConnection.AuthenticateServerAsync(socket, "SocketControl");
        string serverInstanceId = "";
        try
        {
            while (true)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(acceptedConnection);
                if (!success)
                {
                    break;
                }

                switch (frame.MessageId)
                {
                    case ControlMessageIds.ServerRegister:
                        serverInstanceId = GetServerRegisterInstanceId(frame);
                        await HandleServerRegisterAsync(acceptedConnection, frame);
                        break;
                    case ControlMessageIds.ServerHeartbeat:
                        serverInstanceId = GetServerHeartbeatInstanceId(frame, serverInstanceId);
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
        finally
        {
            if (!string.IsNullOrWhiteSpace(serverInstanceId))
            {
                BackendServerSnapshot? snapshot = this.registry.MarkServerDisconnected(serverInstanceId);
                if (snapshot != null)
                {
                    Logger.Warn($"SocketServer control channel disconnected. instanceId={serverInstanceId}");
                    await PublishServerSnapshotAsync(snapshot);
                }
            }
        }
    }

    private async Task HandleServerRegisterAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (!ControlProtocol.TryDecode(frame, ControlMessageIds.ServerRegister, out ServerRegisterRequest request))
        {
            return;
        }

        BackendServerSnapshot snapshot = this.registry.Upsert(request, this.config.NodeId);
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
            this.registry.UpsertSession(sessionEvent);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryUpsert);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationUpsert);
        }
        else if (frame.MessageId == ControlMessageIds.SessionUpdated)
        {
            this.registry.UpsertSession(sessionEvent);
            releasedReservation = this.registry.ReleaseReservationFor(sessionEvent.ClientId, sessionEvent.InstanceId);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryUpsert);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationUpsert);
            if (releasedReservation != null)
            {
                await PublishReservationReleaseAsync(releasedReservation);
            }
        }
        else if (frame.MessageId == ControlMessageIds.SessionClosed)
        {
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
        await ControlProtocol.SendAsync(connection, request.ClientId, ControlMessageIds.RouteResponse, response);
        if (response.Success)
        {
            RouteReservationMessage? reservation = this.registry.Reservations
                .FirstOrDefault(item => item.ReservationId == response.ReservationId);
            if (reservation != null)
            {
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
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerSessionRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.SessionSummaryRemove, out SessionEventMessage session))
        {
            this.registry.RemoveSession(session);
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationUpsert, out ClientLocationMessage location))
        {
            this.registry.UpsertClientLocation(location);
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationRemove, out ClientLocationMessage location))
        {
            this.registry.RemoveClientLocation(location);
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationUpsert, out RouteReservationMessage reservation))
        {
            this.registry.UpsertReservation(reservation);
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationReleaseAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationRelease, out RouteReservationMessage reservation))
        {
            this.registry.ReleaseReservation(reservation.ReservationId);
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandleRegistrySnapshotRequestAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        await ControlProtocol.SendAsync(
            connection,
            frame.ClientId,
            ControlMessageIds.RegistrySnapshotResponse,
            this.registry.GetStatus());
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
        foreach (EndpointConfig peer in this.peers)
        {
            try
            {
                Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                await socket.ConnectAsync(IPAddress.Parse(peer.Host), peer.Port);
                using SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketControl");
                await ControlProtocol.SendAndReceiveAsync(
                    connection,
                    0,
                    messageId,
                    payload);
            }
            catch (SocketException exception)
            {
                Logger.Warn($"ControlServer peer sync failed. peer={peer.Host}:{peer.Port}", exception);
            }
        }
    }

    private async Task SyncRegistryFromPeersAsync()
    {
        foreach (EndpointConfig peer in this.peers)
        {
            try
            {
                Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                await socket.ConnectAsync(IPAddress.Parse(peer.Host), peer.Port);
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
                }
            }
            catch (SocketException exception)
            {
                Logger.Warn($"ControlServer initial peer sync failed. peer={peer.Host}:{peer.Port}", exception);
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

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.Stop();
            this.cancellation?.Dispose();
            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }
}
