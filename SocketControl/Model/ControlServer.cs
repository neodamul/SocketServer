using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Diagnostics;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketControl.Model;

public class ControlServer : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<ControlServer>();
    private static readonly SocketLogger RelayLogger = SocketLogManager.GetRelayLogger<ControlServer>();
    private const int MinCommandWorkerCount = 4;
    private const int MaxCommandWorkerCount = 64;

    private readonly ControlServerNodeConfig config;
    private readonly IReadOnlyCollection<EndpointConfig> peers;
    private readonly BackendServerRegistry registry;
    private readonly ControlHealthThreshold healthThreshold;
    private readonly ResourceUsageProvider resourceUsageProvider = new();
    private readonly ConcurrentDictionary<long, ActiveControlConnection> activeConnections = new();
    private readonly ConcurrentDictionary<SecureSocketConnection, ActiveControlConnection> activeConnectionsBySecureConnection = new();
    private readonly ConcurrentDictionary<string, PersistentSecureChannel> peerChannels = new(StringComparer.Ordinal);
    private readonly Channel<ControlCommandRequest> commandRequestChannel = Channel.CreateUnbounded<ControlCommandRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly Channel<ControlResponseCommand> commandResponseChannel = Channel.CreateUnbounded<ControlResponseCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly Channel<PeerRelayCommand> peerRelayRequestChannel = Channel.CreateUnbounded<PeerRelayCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private readonly Channel<PeerRelayResult> peerRelayResponseChannel = Channel.CreateUnbounded<PeerRelayResult>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private Socket? listener;
    private Socket? peerListener;
    private CancellationTokenSource? cancellation;
    private Task? acceptTask;
    private Task? peerAcceptTask;
    private Task? connectionCleanupTask;
    private Task? peerSnapshotSyncTask;
    private Task? peerRelayRequestTask;
    private Task? peerRelayResponseTask;
    private Task[] commandWorkerTasks = Array.Empty<Task>();
    private Task[] commandResponseWorkerTasks = Array.Empty<Task>();
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
            this.listener.Bind(new IPEndPoint(SocketFactory.ResolveAddress(this.config.Host), this.config.Port));
            this.listener.Listen(SocketFactory.ListenBacklog);
            LocalCertificateStore.GetOrCreate("SocketControl");
            this.cancellation = new CancellationTokenSource();
            this.acceptTask = DedicatedWorker.Start(this.RunAcceptLoopAsync, this.cancellation.Token);
            this.connectionCleanupTask = DedicatedWorker.Start(this.RunConnectionCleanupLoopAsync, this.cancellation.Token);
            this.peerSnapshotSyncTask = DedicatedWorker.Start(this.RunPeerSnapshotSyncLoopAsync, this.cancellation.Token);
            this.commandWorkerTasks = DedicatedWorker.StartMany(
                GetCommandWorkerCount(),
                this.RunCommandRequestLoopAsync,
                this.cancellation.Token);
            this.commandResponseWorkerTasks = DedicatedWorker.StartMany(
                GetCommandResponseWorkerCount(),
                this.RunCommandResponseLoopAsync,
                this.cancellation.Token);
            this.peerRelayRequestTask = DedicatedWorker.Start(
                this.RunPeerRelayRequestLoopAsync,
                this.cancellation.Token);
            this.peerRelayResponseTask = DedicatedWorker.Start(
                this.RunPeerRelayResponseLoopAsync,
                this.cancellation.Token);
            if (this.config.PeerSyncPort > 0 && this.config.PeerSyncPort != this.config.Port)
            {
                this.peerListener = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                this.peerListener.Bind(new IPEndPoint(SocketFactory.ResolveAddress(this.config.Host), this.config.PeerSyncPort));
                this.peerListener.Listen(SocketFactory.ListenBacklog);
                this.peerAcceptTask = DedicatedWorker.Start(
                    token => this.RunAcceptLoopAsync(token, this.peerListener),
                    this.cancellation.Token);
            }

            Logger.Info($"ControlServer started. nodeId={this.config.NodeId}, endpoint={this.config.Host}:{this.Port}, commandWorkers={this.commandWorkerTasks.Length}, responseWorkers={this.commandResponseWorkerTasks.Length}");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer start failed. endpoint={this.config.Host}:{this.config.Port}", exception);
            return false;
        }
    }

    private static int GetCommandWorkerCount()
    {
        return CalculateWorkerCount(Environment.ProcessorCount, 2, MinCommandWorkerCount, MaxCommandWorkerCount);
    }

    private static int GetCommandResponseWorkerCount()
    {
        return CalculateWorkerCount(Environment.ProcessorCount, 2, MinCommandWorkerCount, MaxCommandWorkerCount);
    }

    private static int CalculateWorkerCount(int processorCount, int multiplier, int minimum, int maximum)
    {
        int normalizedProcessorCount = Math.Max(1, processorCount);
        int calculated = normalizedProcessorCount * Math.Max(1, multiplier);
        return Math.Min(Math.Max(calculated, minimum), maximum);
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

        this.ClosePeerChannels();

        await WaitForTaskAsync(this.acceptTask, timeout);
        await WaitForTaskAsync(this.peerAcceptTask, timeout);
        await WaitForTaskAsync(this.connectionCleanupTask, timeout);
        await WaitForTaskAsync(this.peerSnapshotSyncTask, timeout);
        foreach (Task commandWorkerTask in this.commandWorkerTasks)
        {
            await WaitForTaskAsync(commandWorkerTask, timeout);
        }

        foreach (Task commandResponseWorkerTask in this.commandResponseWorkerTasks)
        {
            await WaitForTaskAsync(commandResponseWorkerTask, timeout);
        }

        await WaitForTaskAsync(this.peerRelayRequestTask, timeout);
        await WaitForTaskAsync(this.peerRelayResponseTask, timeout);

        this.listener = null;
        this.peerListener = null;
        this.acceptTask = null;
        this.peerAcceptTask = null;
        this.connectionCleanupTask = null;
        this.peerSnapshotSyncTask = null;
        this.commandWorkerTasks = Array.Empty<Task>();
        this.commandResponseWorkerTasks = Array.Empty<Task>();
        this.peerRelayRequestTask = null;
        this.peerRelayResponseTask = null;
        this.cancellation?.Dispose();
        this.cancellation = null;
        Logger.Info($"ControlServer stopped. nodeId={this.config.NodeId}");
    }

    public ClusterStatusSnapshot GetClusterStatus()
    {
        return this.CreateClusterStatusSnapshot();
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
            this.activeConnectionsBySecureConnection[acceptedConnection] = activeConnection;
            while (true)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(
                    acceptedConnection,
                    this.GetControlFrameHeaderReadTimeoutMilliseconds(),
                    SocketFactory.ReadTimeoutMilliseconds);
                if (!success)
                {
                    break;
                }

                activeConnection.MarkReceived();
                ControlCommandResult result = await EnqueueControlCommandAsync(
                    acceptedConnection,
                    frame,
                    activeConnection,
                    serverInstanceId);
                serverInstanceId = result.ServerInstanceId;
                if (ShouldCloseAfterResponse(frame.MessageId))
                {
                    Logger.Debug(() => $"ControlServer closing one-shot request connection. messageId={frame.MessageId}");
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
            if (acceptedConnection != null)
            {
                this.activeConnectionsBySecureConnection.TryRemove(acceptedConnection, out _);
            }

            string disconnectedInstanceId = string.IsNullOrWhiteSpace(serverInstanceId)
                ? activeConnection.ServerInstanceId
                : serverInstanceId;
            if (!string.IsNullOrWhiteSpace(disconnectedInstanceId))
            {
                Logger.Debug(() => $"ControlServer request channel closed. instanceId={disconnectedInstanceId}");
            }
        }
    }

    private static bool ShouldCloseAfterResponse(uint messageId)
    {
        return messageId == ControlMessageIds.RouteRequest;
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

    private async Task<ControlCommandResult> EnqueueControlCommandAsync(
        SecureSocketConnection connection,
        SocketMessageFrame frame,
        ActiveControlConnection activeConnection,
        string currentServerInstanceId)
    {
        ControlCommandRequest request = new(connection, frame, activeConnection, currentServerInstanceId);
        if (!this.commandRequestChannel.Writer.TryWrite(request))
        {
            Logger.Warn($"ControlServer command request queue rejected frame. messageId={frame.MessageId}");
            return await ProcessControlCommandAsync(request);
        }

        return await request.Completion.Task;
    }

    private async Task RunCommandRequestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ControlCommandRequest request in this.commandRequestChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    ControlCommandResult result = await ProcessControlCommandAsync(request);
                    request.Completion.TrySetResult(result);
                }
                catch (Exception exception)
                {
                    request.Completion.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn($"ControlServer command worker stopped unexpectedly. controlNodeId={this.config.NodeId}", exception);
        }
    }

    private async Task<ControlCommandResult> ProcessControlCommandAsync(ControlCommandRequest request)
    {
        string serverInstanceId = request.ServerInstanceId;
        switch (request.Frame.MessageId)
        {
            case ControlMessageIds.ServerRegister:
                serverInstanceId = GetServerRegisterInstanceId(request.Frame);
                request.ActiveConnection.ServerInstanceId = serverInstanceId;
                await HandleServerRegisterAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.ServerHeartbeat:
                serverInstanceId = GetServerHeartbeatInstanceId(request.Frame, serverInstanceId);
                request.ActiveConnection.ServerInstanceId = serverInstanceId;
                await HandleServerHeartbeatAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.SessionOpened:
            case ControlMessageIds.SessionUpdated:
            case ControlMessageIds.SessionClosed:
                await HandleSessionEventAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.RouteRequest:
                await HandleRouteRequestAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.ClientLocationRequest:
                await HandleClientLocationRequestAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.ServerRegistryUpsert:
                await HandlePeerServerUpsertAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.SessionSummaryUpsert:
                await HandlePeerSessionUpsertAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.SessionSummaryRemove:
                await HandlePeerSessionRemoveAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.ClientLocationUpsert:
                await HandlePeerClientLocationUpsertAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.ClientLocationRemove:
                await HandlePeerClientLocationRemoveAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.RouteReservationUpsert:
                await HandlePeerReservationUpsertAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.RouteReservationRelease:
                await HandlePeerReservationReleaseAsync(request.Connection, request.Frame);
                break;
            case ControlMessageIds.RegistrySnapshotRequest:
                await HandleRegistrySnapshotRequestAsync(request.Connection, request.Frame);
                break;
            default:
                Logger.Warn($"ControlServer received unknown message. messageId={request.Frame.MessageId}");
                break;
        }

        return new ControlCommandResult(serverInstanceId);
    }

    private async Task<bool> SendControlResponseAsync<T>(
        SecureSocketConnection connection,
        uint clientId,
        uint messageId,
        T payload)
    {
        this.activeConnectionsBySecureConnection.TryGetValue(connection, out ActiveControlConnection? activeConnection);
        ControlResponseCommand command = new(
            activeConnection,
            connection,
            clientId,
            messageId,
            payload!,
            payload?.GetType().Name ?? typeof(T).Name);
        if (!this.commandResponseChannel.Writer.TryWrite(command))
        {
            Logger.Warn($"ControlServer response queue rejected item. controlNodeId={this.config.NodeId}, messageId={messageId}, payloadType={command.PayloadType}");
            return activeConnection == null
                ? await ControlProtocol.SendAsync(connection, clientId, messageId, payload)
                : await activeConnection.SendAsync(connection, clientId, messageId, payload!);
        }

        return await command.Completion.Task;
    }

    private async Task RunCommandResponseLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ControlResponseCommand command in this.commandResponseChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    bool success = command.ActiveConnection == null
                        ? await ControlProtocol.SendAsync(
                            command.Connection,
                            command.ClientId,
                            command.MessageId,
                            command.Payload)
                        : await command.ActiveConnection.SendAsync(
                            command.Connection,
                            command.ClientId,
                            command.MessageId,
                            command.Payload);
                    command.Completion.TrySetResult(success);
                }
                catch (Exception exception)
                {
                    command.Completion.TrySetException(exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn($"ControlServer response worker stopped unexpectedly. controlNodeId={this.config.NodeId}", exception);
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
        await SendControlResponseAsync(connection, frame.ClientId, ControlMessageIds.ServerRegisterAck, new ServerRegisterAck
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
        Logger.Debug(() => $"SocketServer heartbeat received. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, current={request.CurrentConnections}, available={request.AvailableConnections}, cpu={request.ResourceUsage.CpuUsagePercent}, memory={request.ResourceUsage.MemoryUsagePercent}, storage={request.ResourceUsage.StorageUsagePercent}, health={snapshot.Health}");
        RelayLogger.Debug(() => $"Server heartbeat registry upsert received. controlNodeId={this.config.NodeId}, instanceId={request.InstanceId}, version={snapshot.Version}, health={snapshot.Health}, current={snapshot.CurrentConnections}, available={snapshot.AvailableConnections}");
        await SendControlResponseAsync(connection, frame.ClientId, ControlMessageIds.ServerHeartbeatAck, new ServerHeartbeatAck
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
            Logger.Debug(() => $"Session opened. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Debug(() => $"Session summary upsert started. event=opened, controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.UpsertSession(sessionEvent);
            releasedReservation = this.registry.ReleaseReservationFor(sessionEvent.ClientId, sessionEvent.InstanceId);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryUpsert);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationUpsert);
            if (releasedReservation != null)
            {
                RelayLogger.Debug(() => $"Route reservation released by session open. controlNodeId={this.config.NodeId}, reservationId={releasedReservation.ReservationId}, clientId={releasedReservation.ClientId}, instanceId={releasedReservation.InstanceId}");
                await PublishReservationReleaseAsync(releasedReservation);
            }
        }
        else if (frame.MessageId == ControlMessageIds.SessionUpdated)
        {
            Logger.Debug(() => $"Session updated. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Debug(() => $"Session summary upsert started. event=updated, controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.UpsertSession(sessionEvent);
            releasedReservation = this.registry.ReleaseReservationFor(sessionEvent.ClientId, sessionEvent.InstanceId);
            if (releasedReservation != null)
            {
                RelayLogger.Debug(() => $"Route reservation released by session update. controlNodeId={this.config.NodeId}, reservationId={releasedReservation.ReservationId}, clientId={releasedReservation.ClientId}, instanceId={releasedReservation.InstanceId}");
                await PublishReservationReleaseAsync(releasedReservation);
            }
        }
        else if (frame.MessageId == ControlMessageIds.SessionClosed)
        {
            Logger.Debug(() => $"Session closed. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}");
            RelayLogger.Debug(() => $"Session summary remove started. controlNodeId={this.config.NodeId}, instanceId={sessionEvent.InstanceId}, sessionId={sessionEvent.SessionId}, clientId={sessionEvent.ClientId}, version={sessionEvent.Version}");
            this.registry.RemoveSession(sessionEvent);
            await PublishSessionAsync(sessionEvent, ControlMessageIds.SessionSummaryRemove);
            await PublishClientLocationAsync(sessionEvent, ControlMessageIds.ClientLocationRemove);
        }

        await SendControlResponseAsync(connection, frame.ClientId, ControlMessageIds.ServerHeartbeatAck, new ServerHeartbeatAck
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
        await SendControlResponseAsync(
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
        Logger.Debug(() => $"Route request resolved. controlNodeId={this.config.NodeId}, clientId={request.ClientId}, success={response.Success}, instanceId={response.InstanceId}, endpoint={response.Host}:{response.Port}, reason={response.ErrorMessage}");
        RelayLogger.Debug(() => $"Route reservation evaluated. controlNodeId={this.config.NodeId}, clientId={request.ClientId}, success={response.Success}, instanceId={response.InstanceId}, reservationId={response.ReservationId}");
        await SendControlResponseAsync(connection, request.ClientId, ControlMessageIds.RouteResponse, response);
        if (response.Success)
        {
            RouteReservationMessage? reservation = this.registry.Reservations
                .FirstOrDefault(item => item.ReservationId == response.ReservationId);
            if (reservation != null)
            {
                RelayLogger.Debug(() => $"Route reservation upsert started. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}, expiresAt={reservation.ExpiresAt}");
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
        await SendControlResponseAsync(connection, frame.ClientId, ControlMessageIds.ControlRegisterAck, new ControlAckMessage
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
            RelayLogger.Debug(() => $"Peer session summary upsert applied. controlNodeId={this.config.NodeId}, instanceId={session.InstanceId}, sessionId={session.SessionId}, clientId={session.ClientId}, version={session.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerSessionRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.SessionSummaryRemove, out SessionEventMessage session))
        {
            this.registry.RemoveSession(session);
            RelayLogger.Debug(() => $"Peer session summary remove applied. controlNodeId={this.config.NodeId}, instanceId={session.InstanceId}, sessionId={session.SessionId}, clientId={session.ClientId}, version={session.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationUpsert, out ClientLocationMessage location))
        {
            this.registry.UpsertClientLocation(location);
            RelayLogger.Debug(() => $"Peer client location upsert applied. controlNodeId={this.config.NodeId}, clientId={location.ClientId}, instanceId={location.InstanceId}, endpoint={location.Host}:{location.Port}, version={location.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerClientLocationRemoveAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.ClientLocationRemove, out ClientLocationMessage location))
        {
            this.registry.RemoveClientLocation(location);
            RelayLogger.Debug(() => $"Peer client location remove applied. controlNodeId={this.config.NodeId}, clientId={location.ClientId}, instanceId={location.InstanceId}, version={location.Version}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationUpsertAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationUpsert, out RouteReservationMessage reservation))
        {
            this.registry.UpsertReservation(reservation);
            RelayLogger.Debug(() => $"Peer route reservation upsert applied. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandlePeerReservationReleaseAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        if (ControlProtocol.TryDecode(frame, ControlMessageIds.RouteReservationRelease, out RouteReservationMessage reservation))
        {
            this.registry.ReleaseReservation(reservation.ReservationId);
            RelayLogger.Debug(() => $"Peer route reservation release applied. controlNodeId={this.config.NodeId}, reservationId={reservation.ReservationId}, clientId={reservation.ClientId}, instanceId={reservation.InstanceId}");
        }

        await SendPeerAckAsync(connection, frame.ClientId);
    }

    private async Task HandleRegistrySnapshotRequestAsync(SecureSocketConnection connection, SocketMessageFrame frame)
    {
        ClusterStatusSnapshot status = this.CreateClusterStatusSnapshot();
        RelayLogger.Info($"Registry snapshot response started. controlNodeId={this.config.NodeId}, servers={status.ServerCount}, sessions={status.TotalSessionCount}, current={status.TotalCurrentConnections}, available={status.TotalAvailableConnections}");
        await SendControlResponseAsync(
            connection,
            frame.ClientId,
            ControlMessageIds.RegistrySnapshotResponse,
            status);
    }

    private ClusterStatusSnapshot CreateClusterStatusSnapshot()
    {
        ClusterStatusSnapshot status = this.registry.GetStatus();
        return new ClusterStatusSnapshot
        {
            ServerCount = status.ServerCount,
            HealthyServerCount = status.HealthyServerCount,
            TotalMaxConnections = status.TotalMaxConnections,
            TotalCurrentConnections = status.TotalCurrentConnections,
            TotalReservedConnections = status.TotalReservedConnections,
            TotalAvailableConnections = status.TotalAvailableConnections,
            TotalSessionCount = status.TotalSessionCount,
            AverageCpuUsagePercent = status.AverageCpuUsagePercent,
            AverageMemoryUsagePercent = status.AverageMemoryUsagePercent,
            AverageStorageUsagePercent = status.AverageStorageUsagePercent,
            ControlServerResourceUsage = this.resourceUsageProvider.Capture(),
            Servers = status.Servers,
            UpdatedAt = status.UpdatedAt
        };
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

    private Task PublishAsync<T>(uint messageId, T payload)
    {
        if (this.peers.Count == 0)
        {
            return Task.CompletedTask;
        }

        PeerRelayCommand command = new(messageId, payload!, payload?.GetType().Name ?? typeof(T).Name);
        if (!this.peerRelayRequestChannel.Writer.TryWrite(command))
        {
            RelayLogger.Warn($"Control peer relay queue rejected command. controlNodeId={this.config.NodeId}, messageId={messageId}, payloadType={command.PayloadType}");
        }
        else
        {
            RelayLogger.Debug(() => $"Control peer relay queued. controlNodeId={this.config.NodeId}, messageId={messageId}, peerCount={this.peers.Count}, payloadType={command.PayloadType}");
        }

        return Task.CompletedTask;
    }

    private async Task RunPeerRelayRequestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (PeerRelayCommand command in this.peerRelayRequestChannel.Reader.ReadAllAsync(cancellationToken))
            {
                await PublishQueuedCommandAsync(command);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn($"ControlServer peer relay request worker stopped unexpectedly. controlNodeId={this.config.NodeId}", exception);
        }
    }

    private async Task RunPeerRelayResponseLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (PeerRelayResult result in this.peerRelayResponseChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (result.Success)
                {
                    RelayLogger.Info($"Control peer relay send completed. controlNodeId={this.config.NodeId}, peer={result.Peer.Host}:{result.Peer.Port}, messageId={result.MessageId}, payloadType={result.PayloadType}");
                }
                else
                {
                    RelayLogger.Warn($"Control peer relay send failed. controlNodeId={this.config.NodeId}, peer={result.Peer.Host}:{result.Peer.Port}, messageId={result.MessageId}, payloadType={result.PayloadType}, error={result.ErrorMessage}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn($"ControlServer peer relay response worker stopped unexpectedly. controlNodeId={this.config.NodeId}", exception);
        }
    }

    private async Task PublishQueuedCommandAsync(PeerRelayCommand command)
    {
        List<Task> tasks = new();
        RelayLogger.Debug(() => $"Control peer relay fanout started. controlNodeId={this.config.NodeId}, messageId={command.MessageId}, peerCount={this.peers.Count}, payloadType={command.PayloadType}");
        foreach (EndpointConfig peer in this.peers)
        {
            tasks.Add(PublishToPeerAsync(peer, command.MessageId, command.Payload, command.PayloadType));
        }

        await Task.WhenAll(tasks);
        RelayLogger.Debug(() => $"Control peer relay fanout completed. controlNodeId={this.config.NodeId}, messageId={command.MessageId}, peerCount={this.peers.Count}, payloadType={command.PayloadType}");
    }

    private async Task PublishToPeerAsync(EndpointConfig peer, uint messageId, object payload, string payloadType)
    {
        try
        {
            RelayLogger.Debug(() => $"Control peer relay send started. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={payloadType}");
            PersistentSecureChannel channel = this.GetPeerChannel(peer);
            (bool success, _) = await channel.SendAndReceiveAsync(
                connection => ControlProtocol.SendAndReceiveAsync(
                    connection,
                    0,
                    messageId,
                    payload,
                    GetPeerOperationTimeoutMilliseconds()),
                GetPeerOperationTimeoutMilliseconds());
            this.peerRelayResponseChannel.Writer.TryWrite(new PeerRelayResult(peer, messageId, payloadType, success, success ? "" : "Peer relay response timed out or failed."));
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer peer event relay failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay socket failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={payloadType}", exception);
            this.peerRelayResponseChannel.Writer.TryWrite(new PeerRelayResult(peer, messageId, payloadType, false, exception.Message));
        }
        catch (IOException exception)
        {
            Logger.Warn($"ControlServer peer event relay I/O failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay I/O failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={payloadType}", exception);
            this.peerRelayResponseChannel.Writer.TryWrite(new PeerRelayResult(peer, messageId, payloadType, false, exception.Message));
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"ControlServer peer event relay authentication failed. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay authentication failed. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={payloadType}", exception);
            this.peerRelayResponseChannel.Writer.TryWrite(new PeerRelayResult(peer, messageId, payloadType, false, exception.Message));
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"ControlServer peer event relay timed out. peer={peer.Host}:{peer.Port}, messageId={messageId}", exception);
            RelayLogger.Warn($"Control peer relay timed out. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}, messageId={messageId}, payloadType={payloadType}", exception);
            this.peerRelayResponseChannel.Writer.TryWrite(new PeerRelayResult(peer, messageId, payloadType, false, exception.Message));
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
                RelayLogger.Debug(() => $"Control peer snapshot sync started. controlNodeId={this.config.NodeId}, peer={peer.Host}:{peer.Port}");
                PersistentSecureChannel channel = this.GetPeerChannel(peer);
                (bool success, SocketMessageFrame frame) = await channel.SendAndReceiveAsync(
                    connection => ControlProtocol.SendAndReceiveAsync(
                        connection,
                        0,
                        ControlMessageIds.RegistrySnapshotRequest,
                        new RegistrySnapshotRequest { RequestedAt = DateTimeOffset.UtcNow },
                        GetPeerOperationTimeoutMilliseconds()),
                    GetPeerOperationTimeoutMilliseconds());
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
        return SendControlResponseAsync(connection, clientId, ControlMessageIds.ControlRegisterAck, new ControlAckMessage
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

    private int GetControlFrameHeaderReadTimeoutMilliseconds()
    {
        double timeoutMilliseconds = GetHeartbeatTimeout().TotalMilliseconds + TimeSpan.FromSeconds(5).TotalMilliseconds;
        return Math.Max(SocketFactory.ReadTimeoutMilliseconds, (int)Math.Ceiling(timeoutMilliseconds));
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

    private static int GetPeerOperationTimeoutMilliseconds()
    {
        return Math.Min(
            5000,
            Math.Max(1000, SocketFactory.ReadTimeoutMilliseconds));
    }

    private PersistentSecureChannel GetPeerChannel(EndpointConfig peer)
    {
        string endpointKey = PersistentSecureChannel.CreateEndpointKey(peer.Host, peer.Port);
        return this.peerChannels.GetOrAdd(
            endpointKey,
            _ => new PersistentSecureChannel(peer, "SocketControl"));
    }

    private void ClosePeerChannels()
    {
        foreach (PersistentSecureChannel channel in this.peerChannels.Values)
        {
            channel.Dispose();
        }

        this.peerChannels.Clear();
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
        catch (OperationCanceledException)
        {
        }
    }

    private bool IsStopping()
    {
        return this.disposedValue || this.cancellation?.IsCancellationRequested == true;
    }

    private sealed class ActiveControlConnection
    {
        private readonly object sendQueueLock = new();
        private long lastReceivedAtTicks;
        private Task sendQueueTail = Task.CompletedTask;

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

        public Task<bool> SendAsync(
            SecureSocketConnection connection,
            uint clientId,
            uint messageId,
            object payload)
        {
            lock (this.sendQueueLock)
            {
                Task<bool> sendTask = SendAfterAsync(this.sendQueueTail, connection, clientId, messageId, payload);
                this.sendQueueTail = sendTask;
                return sendTask;
            }
        }

        private static async Task<bool> SendAfterAsync(
            Task previousSendTask,
            SecureSocketConnection connection,
            uint clientId,
            uint messageId,
            object payload)
        {
            try
            {
                await previousSendTask.ConfigureAwait(false);
            }
            catch
            {
            }

            try
            {
                return await ControlProtocol.SendAsync(connection, clientId, messageId, payload).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }
    }

    private sealed class ControlCommandRequest
    {
        public ControlCommandRequest(
            SecureSocketConnection connection,
            SocketMessageFrame frame,
            ActiveControlConnection activeConnection,
            string serverInstanceId)
        {
            this.Connection = connection;
            this.Frame = frame;
            this.ActiveConnection = activeConnection;
            this.ServerInstanceId = serverInstanceId;
        }

        public SecureSocketConnection Connection { get; }

        public SocketMessageFrame Frame { get; }

        public ActiveControlConnection ActiveConnection { get; }

        public string ServerInstanceId { get; }

        public TaskCompletionSource<ControlCommandResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record ControlCommandResult(string ServerInstanceId);

    private sealed class ControlResponseCommand
    {
        public ControlResponseCommand(
            ActiveControlConnection? activeConnection,
            SecureSocketConnection connection,
            uint clientId,
            uint messageId,
            object payload,
            string payloadType)
        {
            this.ActiveConnection = activeConnection;
            this.Connection = connection;
            this.ClientId = clientId;
            this.MessageId = messageId;
            this.Payload = payload;
            this.PayloadType = payloadType;
        }

        public ActiveControlConnection? ActiveConnection { get; }

        public SecureSocketConnection Connection { get; }

        public uint ClientId { get; }

        public uint MessageId { get; }

        public object Payload { get; }

        public string PayloadType { get; }

        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record PeerRelayCommand(uint MessageId, object Payload, string PayloadType);

    private sealed record PeerRelayResult(
        EndpointConfig Peer,
        uint MessageId,
        string PayloadType,
        bool Success,
        string ErrorMessage);

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
