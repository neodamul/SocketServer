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
using SocketCommon;
using SocketCommon.Configuration;
using SocketCommon.Interface;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketServer.Model;
public class TcpServer : SocketClient.Model.TcpClient, IServer, IClient, IDisposable
{
    public const int DefaultMaxConnections = 10000;
    public const int DefaultPendingAcceptCount = 100;
    public const int DefaultIdleTimeoutSeconds = 90;
    public const int DefaultIdleScanIntervalSeconds = 10;

    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<TcpServer>();

    private readonly ConcurrentDictionary<long, ConnectionSession> connectedClients = new();
    private readonly ConcurrentDictionary<uint, ConnectionSession> connectedClientsById = new();
    private readonly ConcurrentDictionary<string, BackendServerSnapshot> relayServers = new();
    private readonly int maxConnections;
    private readonly int pendingAcceptCount;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan idleScanInterval;
    private readonly int serverId;
    private readonly string instanceId;
    private IReadOnlyCollection<EndpointConfig> controlServers = Array.Empty<EndpointConfig>();
    private string clusterId = "socket-cluster-1";
    private CancellationTokenSource acceptLoopCancellation;
    private Task acceptLoopTask;
    private bool isListening;
    private DateTimeOffset? startedAt;
    private int activeConnectionSlots;
    private long nextConnectionId;
    private long totalAcceptedClients;
    private long totalClosedClients;
    private long totalRejectedClients;
    private long totalIdleTimeoutClients;
    private long totalReceivedMessages;
    private long totalSentMessages;
    private bool disposedValue;

    public event Func<ConnectionSession, Task> SessionOpenedAsync;

    public event Func<ConnectionSession, Task> SessionUpdatedAsync;

    public event Func<ConnectionSession, Task> SessionClosedAsync;

    public TcpServer()
        : this(0, null)
    { }

    public TcpServer(int id, string name)
        : base(id, name, null, Constants.LocalHostPort)
    {
        this.maxConnections = DefaultMaxConnections;
        this.pendingAcceptCount = DefaultPendingAcceptCount;
        this.idleTimeout = TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds);
        this.idleScanInterval = TimeSpan.FromSeconds(DefaultIdleScanIntervalSeconds);
        this.serverId = id;
        this.instanceId = CreateInstanceId(id, name);
    }

    public TcpServer(int id, string name, string ipAddress, int port)
        : base(id, name, ipAddress, port)
    {
        this.maxConnections = DefaultMaxConnections;
        this.pendingAcceptCount = DefaultPendingAcceptCount;
        this.idleTimeout = TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds);
        this.idleScanInterval = TimeSpan.FromSeconds(DefaultIdleScanIntervalSeconds);
        this.serverId = id;
        this.instanceId = CreateInstanceId(id, name);
    }

    public TcpServer(
        int id,
        string name,
        string ipAddress,
        int port,
        int maxConnections,
        int pendingAcceptCount,
        TimeSpan idleTimeout,
        TimeSpan? idleScanInterval = null,
        string instanceId = null)
        : base(id, name, ipAddress, port)
    {
        this.maxConnections = Math.Max(1, maxConnections);
        this.pendingAcceptCount = Math.Max(1, pendingAcceptCount);
        this.idleTimeout = idleTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds)
            : idleTimeout;
        this.idleScanInterval = idleScanInterval.HasValue && idleScanInterval.Value > TimeSpan.Zero
            ? idleScanInterval.Value
            : TimeSpan.FromSeconds(DefaultIdleScanIntervalSeconds);
        this.serverId = id;
        this.instanceId = string.IsNullOrWhiteSpace(instanceId)
            ? CreateInstanceId(id, name)
            : instanceId;
    }

    public int ServerId => this.serverId;

    public string InstanceId => this.instanceId;

    public int RelayServerCount => this.relayServers.Count;

    public void ConfigureControlRouting(IReadOnlyCollection<EndpointConfig> controlServers, string clusterId)
    {
        this.controlServers = controlServers ?? Array.Empty<EndpointConfig>();
        this.clusterId = string.IsNullOrWhiteSpace(clusterId) ? "socket-cluster-1" : clusterId;
    }

    public async Task<int> RefreshRelayServersFromControlServersAsync()
    {
        Dictionary<string, BackendServerSnapshot> latestServers = new(StringComparer.Ordinal);
        bool receivedSnapshot = false;
        foreach (EndpointConfig endpoint in this.controlServers)
        {
            try
            {
                Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                await SocketFactory.ConnectAsync(socket, IPAddress.Parse(endpoint.Host), endpoint.Port);
                using SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketServer");
                (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
                    connection,
                    0,
                    ControlMessageIds.RegistrySnapshotRequest,
                    new RegistrySnapshotRequest { RequestedAt = DateTimeOffset.UtcNow });

                if (!success ||
                    frame.MessageId != ControlMessageIds.RegistrySnapshotResponse ||
                    !ControlProtocol.TryDecode(frame, ControlMessageIds.RegistrySnapshotResponse, out ClusterStatusSnapshot snapshot))
                {
                    continue;
                }

                receivedSnapshot = true;
                foreach (BackendServerSnapshot server in snapshot.Servers)
                {
                    if (!IsRelayCandidate(server))
                    {
                        continue;
                    }

                    if (!latestServers.TryGetValue(server.InstanceId, out BackendServerSnapshot existing) ||
                        server.Version > existing.Version ||
                        (server.Version == existing.Version && server.UpdatedAt > existing.UpdatedAt))
                    {
                        latestServers[server.InstanceId] = server;
                    }
                }
            }
            catch (SocketException exception)
            {
                Logger.Warn($"Relay server snapshot refresh failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (IOException exception)
            {
                Logger.Warn($"Relay server snapshot refresh I/O failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"Relay server snapshot refresh timed out. endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (AuthenticationException exception)
            {
                Logger.Warn($"Relay server snapshot refresh authentication failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
        }

        if (!receivedSnapshot)
        {
            return this.relayServers.Count;
        }

        foreach (string instanceIdKey in this.relayServers.Keys)
        {
            if (!latestServers.ContainsKey(instanceIdKey))
            {
                this.relayServers.TryRemove(instanceIdKey, out _);
            }
        }

        foreach (KeyValuePair<string, BackendServerSnapshot> item in latestServers)
        {
            this.relayServers[item.Key] = item.Value;
        }

        Logger.Debug($"Relay server list refreshed. instanceId={this.instanceId}, relayServers={this.relayServers.Count}");
        return this.relayServers.Count;
    }

    public bool Start()
    {
        bool started = this.Bind() && this.Listen();
        Logger.Info($"Server start requested. endpoint={this.GetIpAddress()}:{this.GetPort()}, success={started}");
        return started;
    }

    public bool Bind()
    {
        try
        {
            if (this.Socket == null)
            {
                this.Initialize();
            }

            this.Socket.Bind(new IPEndPoint(this.IpAddress, this.Port));
            if (this.Socket.LocalEndPoint is IPEndPoint localEndPoint)
            {
                this.SetPort(localEndPoint.Port);
            }

            Logger.Info($"Server socket bound. endpoint={this.IpAddress}:{this.Port}");
            LocalCertificateStore.GetOrCreate("SocketServer");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Server bind failed. endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn("Server bind failed because socket is disposed.", exception);
            return false;
        }
    }

    public bool BindInPortRange(int portRangeStart, int portRangeEnd)
    {
        if (portRangeStart == 0 && portRangeEnd == 0)
        {
            this.SetPort(0);
            return this.Bind();
        }

        if (portRangeStart <= 0 || portRangeEnd < portRangeStart)
        {
            return false;
        }

        for (int port = portRangeStart; port <= portRangeEnd; port++)
        {
            this.SetPort(port);
            if (this.Bind())
            {
                return true;
            }

            this.Socket?.Dispose();
            this.Socket = null;
        }

        return false;
    }

    public bool Listen()
    {
        try
        {
            if (this.Socket == null)
            {
                return false;
            }

            this.Socket.Listen(SocketFactory.ListenBacklog);
            this.isListening = true;
            this.startedAt ??= DateTimeOffset.UtcNow;
            Logger.Info($"Server listening. endpoint={this.IpAddress}:{this.Port}, backlog={SocketFactory.ListenBacklog}");
            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Server listen failed. endpoint={this.IpAddress}:{this.Port}", exception);
            return false;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn("Server listen failed because socket is disposed.", exception);
            return false;
        }
    }

    public bool End()
    {
        return this.EndAsync().GetAwaiter().GetResult();
    }

    public Task<bool> EndAsync()
    {
        return this.EndAsync(TimeSpan.FromSeconds(5));
    }

    public async Task<bool> EndAsync(TimeSpan timeout)
    {
        this.acceptLoopCancellation?.Cancel();
        this.Disconnect();
        this.isListening = false;
        this.CloseConnectedClients();
        await WaitForTaskAsync(this.acceptLoopTask, timeout);
        this.acceptLoopCancellation?.Dispose();
        this.acceptLoopCancellation = null;
        this.acceptLoopTask = null;
        Logger.Info($"Server ended. endpoint={this.GetIpAddress()}:{this.GetPort()}");
        return true;
    }

    public bool StartClientAcceptLoop()
    {
        if (this.Socket == null || !this.Socket.IsBound)
        {
            Logger.Warn("Accept loop start skipped because server socket is not bound.");
            return false;
        }

        if (this.acceptLoopTask != null && !this.acceptLoopTask.IsCompleted)
        {
            return true;
        }

        this.acceptLoopCancellation?.Dispose();
        this.acceptLoopCancellation = new CancellationTokenSource();
        this.acceptLoopTask = this.RunClientAcceptLoopAsync(this.acceptLoopCancellation.Token);
        Logger.Info($"Accept loop started. endpoint={this.GetIpAddress()}:{this.GetPort()}");
        return true;
    }

    public int GetConnectedClientCount()
    {
        return this.connectedClients.Count;
    }

    public TcpServerStatus GetStatus()
    {
        return new TcpServerStatus
        {
            ServerId = this.serverId,
            InstanceId = this.instanceId,
            IsSocketInitialized = this.Socket != null,
            IsBound = this.Socket?.IsBound ?? false,
            IsListening = this.isListening,
            IsAcceptLoopRunning = this.acceptLoopTask != null && !this.acceptLoopTask.IsCompleted,
            IpAddress = this.GetIpAddress(),
            Port = this.GetPort(),
            ConnectedClientCount = this.GetConnectedClientCount(),
            MaxConnections = this.maxConnections,
            AvailableConnections = Math.Max(0, this.maxConnections - this.GetConnectedClientCount()),
            PendingAcceptCount = this.pendingAcceptCount,
            IdleTimeoutSeconds = (int)this.idleTimeout.TotalSeconds,
            TotalAcceptedClients = Interlocked.Read(ref this.totalAcceptedClients),
            TotalClosedClients = Interlocked.Read(ref this.totalClosedClients),
            TotalRejectedClients = Interlocked.Read(ref this.totalRejectedClients),
            TotalIdleTimeoutClients = Interlocked.Read(ref this.totalIdleTimeoutClients),
            TotalReceivedMessages = Interlocked.Read(ref this.totalReceivedMessages),
            TotalSentMessages = Interlocked.Read(ref this.totalSentMessages),
            ListenBacklog = SocketFactory.ListenBacklog,
            NoDelay = SocketFactory.NoDelay,
            MaxPayloadLength = SocketMessageFrame.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = SocketAsyncEventArgsFactory.AvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = SocketAsyncEventArgsFactory.TotalCreatedCount,
            SocketAsyncEventArgsInUseCount = SocketAsyncEventArgsFactory.InUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = SocketAsyncEventArgsFactory.HighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = SocketAsyncEventArgsFactory.GrowthCount,
            SocketAsyncEventArgsBufferSize = SocketAsyncEventArgsFactory.BufferSize,
            SocketAsyncEventArgsBufferSlabCount = SocketAsyncEventArgsFactory.BufferSlabCount,
            SocketAsyncEventArgsBufferBytesAllocated = SocketAsyncEventArgsFactory.BufferBytesAllocated,
            StartedAt = this.startedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }

    public bool AcceptHelloWorldRequestAndRespond()
    {
        return AcceptHelloWorldRequestAndRespondAsync().GetAwaiter().GetResult();
    }

    public async Task<bool> AcceptHelloWorldRequestAndRespondAsync()
    {
        if (this.Socket == null)
        {
            return false;
        }

        try
        {
            Socket client = await SocketAsyncEventArgsTransport.AcceptAsync(this.Socket);
            if (client == null)
            {
                return false;
            }

            using SecureSocketConnection connection =
                await SecureSocketConnection.AuthenticateServerAsync(client, "SocketServer");
            (bool success, HelloWorldRequest request) = await HelloWorldProtocol.TryReceiveRequestAsync(connection);
            if (!success)
            {
                return false;
            }

            return await HelloWorldProtocol.SendAsync(connection, HelloWorldProtocol.CreateResponse(request.ClientId));
        }
        catch (SocketException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (AuthenticationException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private async Task RunClientAcceptLoopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks = new Task[this.pendingAcceptCount + 1];
        for (int i = 0; i < this.pendingAcceptCount; i++)
        {
            tasks[i] = this.RunAcceptWorkerAsync(cancellationToken);
        }

        tasks[^1] = this.RunUnhealthyConnectionCleanupLoopAsync(cancellationToken);
        await Task.WhenAll(tasks);
    }

    private async Task RunAcceptWorkerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client = null;
            try
            {
                client = await SocketAsyncEventArgsTransport.AcceptAsync(this.Socket);
                if (client == null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    continue;
                }

                if (!this.TryAcquireConnectionSlot())
                {
                    Interlocked.Increment(ref this.totalRejectedClients);
                    Logger.Warn($"Client rejected because max connections reached. remote={client.RemoteEndPoint}, maxConnections={this.maxConnections}");
                    CloseClient(client);
                    continue;
                }

                SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateServerAsync(client, "SocketServer");
                long connectionId = Interlocked.Increment(ref this.nextConnectionId);
                ConnectionSession session = new(connectionId, connection);
                this.AddConnectedClient(session);
                Interlocked.Increment(ref this.totalAcceptedClients);
                Logger.Debug($"Client accepted. connectionId={session.Id}, remote={session.RemoteEndPoint}, connectedClients={this.GetConnectedClientCount()}");
                session.HandlerTask = this.HandleClientAsync(session, cancellationToken);
            }
            catch (SocketException exception)
            {
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client accept failed.", exception);
            }
            catch (IOException exception)
            {
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client TLS connection failed during accept.", exception);
            }
            catch (AuthenticationException exception)
            {
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client TLS authentication failed during accept.", exception);
            }
            catch (ObjectDisposedException)
            {
                CloseClient(client);
                break;
            }
        }
    }

    private async Task RunUnhealthyConnectionCleanupLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(this.idleScanInterval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            this.CleanupUnhealthyConnections(DateTimeOffset.UtcNow);
        }
    }

    private int CleanupUnhealthyConnections(DateTimeOffset now)
    {
        int closedCount = 0;
        foreach (ConnectionSession session in this.connectedClients.Values)
        {
            if (now - session.LastReceivedAt <= this.idleTimeout)
            {
                continue;
            }

            if (this.RemoveConnectedClient(session))
            {
                closedCount++;
                Interlocked.Increment(ref this.totalIdleTimeoutClients);
                Logger.Debug($"Client closed by cleanup scheduler. connectionId={session.Id}, remote={session.RemoteEndPoint}");
            }
        }

        return closedCount;
    }

    private async Task HandleClientAsync(ConnectionSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(session.Connection);
                if (!success)
                {
                    break;
                }

                if (ClientMessageProtocol.TryDecodeRelay(frame, out ServerRelayMessage relayMessage))
                {
                    session.MarkReceived(frame.ClientId);
                    Interlocked.Increment(ref this.totalReceivedMessages);
                    await this.HandleServerRelayAsync(session, relayMessage);
                    break;
                }

                session.MarkReceived(frame.ClientId);
                this.UpdateClientIndex(session);
                Interlocked.Increment(ref this.totalReceivedMessages);
                if (!session.HasReportedOpened)
                {
                    session.MarkReportedOpened();
                    this.NotifySessionOpened(session);
                }

                this.NotifySessionUpdated(session);
                bool handled = await this.HandleClientMessageAsync(session, frame);
                if (!handled)
                {
                    break;
                }
            }
        }
        finally
        {
            this.RemoveConnectedClient(session);
        }
    }

    private async Task<bool> HandleClientMessageAsync(ConnectionSession session, SocketMessageFrame frame)
    {
        bool sent;
        if (HealthCheckProtocol.TryDecode(frame, out HealthCheckMessage healthCheckMessage))
        {
            if (healthCheckMessage.Type != HealthCheckMessageType.Ping)
            {
                return true;
            }

            sent = await session.SendAsync(HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePong(healthCheckMessage.ClientId)));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        if (HelloWorldProtocol.TryDecodeRequest(frame, out HelloWorldRequest helloWorldRequest))
        {
            sent = await session.SendAsync(HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse(helloWorldRequest.ClientId)));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        if (ClientMessageProtocol.TryDecodeRegister(frame, out ClientRegisterRequest registerRequest))
        {
            session.MarkReceived(registerRequest.ClientId);
            this.UpdateClientIndex(session);
            sent = await session.SendAsync(ClientMessageProtocol.CreateFrame(
                registerRequest.ClientId,
                ClientMessageIds.ClientRegisterAck,
                new ClientRegisterAck
                {
                    ClientId = registerRequest.ClientId,
                    Success = registerRequest.ClientId > 0,
                    ErrorMessage = registerRequest.ClientId == 0 ? "ClientId must be greater than zero." : ""
                }));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        if (ClientMessageProtocol.TryDecodeSendRequest(frame, out ClientMessageSendRequest clientMessageRequest))
        {
            return await this.HandleClientMessageSendAsync(session, clientMessageRequest);
        }

        if (ClientMessageProtocol.TryDecodeRelay(frame, out ServerRelayMessage relayMessage))
        {
            await this.HandleServerRelayAsync(session, relayMessage);
            return false;
        }

        return false;
    }

    private async Task<bool> HandleClientMessageSendAsync(ConnectionSession sourceSession, ClientMessageSendRequest request)
    {
        if (request.SourceClientId == 0 || request.TargetClientId == 0)
        {
            return await this.SendClientMessageErrorAsync(sourceSession, request, "InvalidClientId", "Source and target client ids must be greater than zero.");
        }

        sourceSession.MarkReceived(request.SourceClientId);
        this.UpdateClientIndex(sourceSession);

        if (await this.DeliverToLocalClientAsync(request))
        {
            return await this.SendClientMessageAckAsync(sourceSession, request, this.instanceId);
        }

        (bool broadcastSuccess, string broadcastTargetInstanceId, string broadcastErrorMessage) =
            await this.BroadcastRelayToKnownServersAsync(request);
        if (broadcastSuccess)
        {
            return await this.SendClientMessageAckAsync(sourceSession, request, broadcastTargetInstanceId);
        }

        ClientLocationResponse location = await this.ResolveClientLocationAsync(request.SourceClientId, request.TargetClientId);
        if (location == null || !location.Success)
        {
            return await this.SendClientMessageErrorAsync(
                sourceSession,
                request,
                "RelayFailed",
                string.IsNullOrWhiteSpace(broadcastErrorMessage)
                    ? location?.ErrorMessage ?? "Target client location was not found."
                    : broadcastErrorMessage);
        }

        if (location != null && location.Success && location.InstanceId == this.instanceId)
        {
            return await this.SendClientMessageErrorAsync(sourceSession, request, "TargetNotConnected", "Target client is not connected to this server.");
        }

        (bool success, string targetInstanceId, string errorMessage) = await this.RelayToRemoteServerAsync(location, request);
        if (!success)
        {
            return await this.SendClientMessageErrorAsync(sourceSession, request, "RelayFailed", errorMessage);
        }

        return await this.SendClientMessageAckAsync(sourceSession, request, targetInstanceId);
    }

    private async Task HandleServerRelayAsync(ConnectionSession relaySession, ServerRelayMessage relayMessage)
    {
        ClientMessageSendRequest request = new()
        {
            MessageToken = relayMessage.MessageToken,
            SourceClientId = relayMessage.SourceClientId,
            TargetClientId = relayMessage.TargetClientId,
            Content = relayMessage.Content,
            TtlSeconds = relayMessage.TtlSeconds,
            CreatedAt = relayMessage.CreatedAt
        };

        if (DateTimeOffset.UtcNow - request.CreatedAt > TimeSpan.FromSeconds(Math.Max(1, request.TtlSeconds)))
        {
            await relaySession.SendAsync(ClientMessageProtocol.CreateFrame(
                request.SourceClientId,
                ServerRelayMessageIds.ServerRelayError,
                CreateClientMessageError(request, "MessageExpired", "Relay message ttl expired.")));
            return;
        }

        if (!await this.DeliverToLocalClientAsync(request))
        {
            await relaySession.SendAsync(ClientMessageProtocol.CreateFrame(
                request.SourceClientId,
                ServerRelayMessageIds.ServerRelayError,
                CreateClientMessageError(request, "TargetNotConnected", "Target client is not connected to this server.")));
            return;
        }

        await relaySession.SendAsync(ClientMessageProtocol.CreateFrame(
            request.SourceClientId,
            ServerRelayMessageIds.ServerRelayAck,
            CreateClientMessageAck(request, this.instanceId)));
    }

    private async Task<bool> DeliverToLocalClientAsync(ClientMessageSendRequest request)
    {
        if (!this.connectedClientsById.TryGetValue(request.TargetClientId, out ConnectionSession targetSession) ||
            targetSession.IsClosed)
        {
            return false;
        }

        bool sent = await targetSession.SendAsync(ClientMessageProtocol.CreateFrame(
            request.TargetClientId,
            ClientMessageIds.ClientMessageDeliver,
            ClientMessageProtocol.CreateDelivery(request)));
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
            return true;
        }

        this.RemoveConnectedClient(targetSession);
        return false;
    }

    private async Task<ClientLocationResponse> ResolveClientLocationAsync(uint sourceClientId, uint targetClientId)
    {
        foreach (EndpointConfig endpoint in this.controlServers)
        {
            try
            {
                Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
                await SocketFactory.ConnectAsync(socket, IPAddress.Parse(endpoint.Host), endpoint.Port);
                using SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketServer");
                (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
                    connection,
                    sourceClientId,
                    ControlMessageIds.ClientLocationRequest,
                    new ClientLocationRequest
                    {
                        SourceClientId = sourceClientId,
                        TargetClientId = targetClientId
                    });

                if (success &&
                    (frame.MessageId == ControlMessageIds.ClientLocationResponse ||
                        frame.MessageId == ControlMessageIds.ClientLocationNotFound) &&
                    ControlProtocol.TryDecode(frame, frame.MessageId, out ClientLocationResponse response))
                {
                    return response;
                }
            }
            catch (SocketException exception)
            {
                Logger.Warn($"Client location request failed. endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"Client location request timed out. endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
            }
        }

        return new ClientLocationResponse
        {
            Success = false,
            TargetClientId = targetClientId,
            ErrorMessage = "No ControlServer responded to the client location request."
        };
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> RelayToRemoteServerAsync(
        ClientLocationResponse location,
        ClientMessageSendRequest request)
    {
        return await RelayToRemoteServerAsync(
            location.Host,
            location.Port,
            location.InstanceId,
            request);
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> RelayToRemoteServerAsync(
        BackendServerSnapshot server,
        ClientMessageSendRequest request)
    {
        return await RelayToRemoteServerAsync(
            server.Host,
            server.Port,
            server.InstanceId,
            request);
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> RelayToRemoteServerAsync(
        string host,
        int port,
        string targetInstanceId,
        ClientMessageSendRequest request)
    {
        try
        {
            Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
            await SocketFactory.ConnectAsync(socket, IPAddress.Parse(host), port);
            using SecureSocketConnection connection =
                await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketServer");
            (bool success, SocketMessageFrame frame) = await ClientMessageProtocol.SendRelayAndReceiveAsync(
                connection,
                ClientMessageProtocol.CreateRelay(this.clusterId, this.instanceId, request));
            if (!success)
            {
                return (false, targetInstanceId, "Target server did not respond to relay.");
            }

            if (frame.MessageId == ServerRelayMessageIds.ServerRelayAck &&
                ClientMessageProtocol.TryDecode(frame, ServerRelayMessageIds.ServerRelayAck, out ClientMessageAck _))
            {
                return (true, targetInstanceId, "");
            }

            if (frame.MessageId == ServerRelayMessageIds.ServerRelayError &&
                ClientMessageProtocol.TryDecode(frame, ServerRelayMessageIds.ServerRelayError, out ClientMessageError error))
            {
                return (false, targetInstanceId, error.ErrorMessage);
            }

            return (false, targetInstanceId, "Target server returned an invalid relay response.");
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Server relay failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay socket failed.");
        }
        catch (IOException exception)
        {
            Logger.Warn($"Server relay I/O failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay I/O failed.");
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"Server relay authentication failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay authentication failed.");
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"Server relay timed out. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay timed out.");
        }
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> BroadcastRelayToKnownServersAsync(
        ClientMessageSendRequest request)
    {
        await this.RefreshRelayServersFromControlServersAsync();
        BackendServerSnapshot[] servers = this.relayServers.Values
            .Where(IsRelayCandidate)
            .ToArray();
        if (servers.Length == 0)
        {
            return (false, "", "No known relay SocketServer instances.");
        }

        Task<(bool Success, string TargetInstanceId, string ErrorMessage)>[] tasks = servers
            .Select(server => this.RelayToRemoteServerAsync(server, request))
            .ToArray();
        (bool Success, string TargetInstanceId, string ErrorMessage)[] results = await Task.WhenAll(tasks);
        (bool Success, string TargetInstanceId, string ErrorMessage) delivered =
            results.FirstOrDefault(result => result.Success);
        if (delivered.Success)
        {
            return delivered;
        }

        string errorMessage = results.FirstOrDefault(result => !string.IsNullOrWhiteSpace(result.ErrorMessage)).ErrorMessage;
        return (false, "", string.IsNullOrWhiteSpace(errorMessage)
            ? "No relay SocketServer delivered the message."
            : errorMessage);
    }

    private bool IsRelayCandidate(BackendServerSnapshot server)
    {
        return server != null &&
            !string.IsNullOrWhiteSpace(server.InstanceId) &&
            !string.Equals(server.InstanceId, this.instanceId, StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(server.Host) &&
            server.Port > 0 &&
            server.Health == ServerHealthState.Healthy;
    }

    private async Task<bool> SendClientMessageAckAsync(ConnectionSession sourceSession, ClientMessageSendRequest request, string targetInstanceId)
    {
        bool sent = await sourceSession.SendAsync(ClientMessageProtocol.CreateFrame(
            request.SourceClientId,
            ClientMessageIds.ClientMessageAck,
            CreateClientMessageAck(request, targetInstanceId)));
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
        }

        return sent;
    }

    private async Task<bool> SendClientMessageErrorAsync(
        ConnectionSession sourceSession,
        ClientMessageSendRequest request,
        string errorCode,
        string errorMessage)
    {
        bool sent = await sourceSession.SendAsync(ClientMessageProtocol.CreateFrame(
            request.SourceClientId,
            ClientMessageIds.ClientMessageError,
            CreateClientMessageError(request, errorCode, errorMessage)));
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
        }

        return sent;
    }

    private bool TryAcquireConnectionSlot()
    {
        int current = Interlocked.Increment(ref this.activeConnectionSlots);
        if (current <= this.maxConnections)
        {
            return true;
        }

        Interlocked.Decrement(ref this.activeConnectionSlots);
        return false;
    }

    private void AddConnectedClient(ConnectionSession session)
    {
        this.connectedClients[session.Id] = session;
    }

    private void UpdateClientIndex(ConnectionSession session)
    {
        if (session.ClientId == 0)
        {
            return;
        }

        this.connectedClientsById[session.ClientId] = session;
    }

    private bool RemoveConnectedClient(ConnectionSession session)
    {
        if (!this.connectedClients.TryRemove(session.Id, out ConnectionSession removedSession))
        {
            return false;
        }

        if (removedSession.Close())
        {
            if (removedSession.ClientId > 0 &&
                this.connectedClientsById.TryGetValue(removedSession.ClientId, out ConnectionSession indexedSession) &&
                indexedSession.Id == removedSession.Id)
            {
                this.connectedClientsById.TryRemove(removedSession.ClientId, out _);
            }

            Interlocked.Decrement(ref this.activeConnectionSlots);
            Interlocked.Increment(ref this.totalClosedClients);
            Logger.Debug($"Client closed. connectionId={removedSession.Id}, remote={removedSession.RemoteEndPoint}, connectedClients={this.GetConnectedClientCount()}");
            if (removedSession.HasReportedOpened)
            {
                this.NotifySessionClosed(removedSession);
            }
        }

        return true;
    }

    private void NotifySessionOpened(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionOpenedAsync;
        if (handler != null)
        {
            this.RunSessionEventHandler(handler, session, "opened");
        }
    }

    private void NotifySessionClosed(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionClosedAsync;
        if (handler != null)
        {
            this.RunSessionEventHandler(handler, session, "closed");
        }
    }

    private void NotifySessionUpdated(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionUpdatedAsync;
        if (handler != null)
        {
            this.RunSessionEventHandler(handler, session, "updated");
        }
    }

    private void RunSessionEventHandler(Func<ConnectionSession, Task> handler, ConnectionSession session, string eventName)
    {
        _ = Task.Run(() => handler(session)).ContinueWith(
            task => Logger.Warn($"Session {eventName} event handler failed. connectionId={session.Id}", task.Exception),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    private static string CreateInstanceId(int id, string name)
    {
        return string.IsNullOrWhiteSpace(name)
            ? $"server-{id}"
            : name;
    }

    private static ClientMessageAck CreateClientMessageAck(ClientMessageSendRequest request, string targetInstanceId)
    {
        return new ClientMessageAck
        {
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            Delivered = true,
            TargetInstanceId = targetInstanceId ?? "",
            DeliveredAt = DateTimeOffset.UtcNow
        };
    }

    private static ClientMessageError CreateClientMessageError(
        ClientMessageSendRequest request,
        string errorCode,
        string errorMessage)
    {
        return new ClientMessageError
        {
            MessageToken = request.MessageToken,
            SourceClientId = request.SourceClientId,
            TargetClientId = request.TargetClientId,
            ErrorCode = errorCode ?? "",
            ErrorMessage = errorMessage ?? "",
            FailedAt = DateTimeOffset.UtcNow
        };
    }

    private void CloseConnectedClients()
    {
        foreach (ConnectionSession session in this.connectedClients.Values)
        {
            this.RemoveConnectedClient(session);
        }
    }

    private static void CloseClient(Socket client)
    {
        if (client == null)
        {
            return;
        }

        try
        {
            if (client.Connected)
            {
                client.Shutdown(SocketShutdown.Both);
            }
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            client.Dispose();
        }
    }

    private static async Task WaitForTaskAsync(Task task, TimeSpan timeout)
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

    protected override void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                this.End();
            }

            base.Dispose(disposing);
            disposedValue = true;
        }
    }

    // // TODO: 비관리형 리소스를 해제하는 코드가 'Dispose(bool disposing)'에 포함된 경우에만 종료자를 재정의합니다.
    // ~TcpServer()
    // {
    //     // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
    //     Dispose(disposing: false);
    // }

    void IDisposable.Dispose()
    {
        // 이 코드를 변경하지 마세요. 'Dispose(bool disposing)' 메서드에 정리 코드를 입력합니다.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
