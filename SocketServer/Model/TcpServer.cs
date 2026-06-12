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
    private static readonly SocketLogger RelayLogger = SocketLogManager.GetRelayLogger<TcpServer>();
    private const int MinimumClientCommandWorkerCount = 4;
    private const int MinimumClientResponseWorkerCount = 4;
    private const int MaximumClientWorkerCount = 64;
    private const int ConnectionsPerClientCommandWorker = 1000;
    private const int ConnectionsPerClientResponseWorker = 500;
    private const int ServerRelayWorkerCount = 8;
    private const int MinimumServerRelayChannelCount = 2;
    private const int ServerRelayBatchFlushIntervalMilliseconds = 20;
    private const int ServerRelayBatchMaxItems = 256;

    private readonly ConcurrentDictionary<long, ConnectionSession> connectedClients = new();
    private readonly ConcurrentDictionary<uint, ConnectionSession> connectedClientsById = new();
    private readonly ConcurrentDictionary<string, BackendServerSnapshot> relayServers = new();
    private readonly ConcurrentDictionary<string, PersistentSecureChannel> controlCommandChannels = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PersistentSecureChannelPool> serverRelayChannelPools = new(StringComparer.Ordinal);
    private ClientLocationCache clientLocationCache = new();
    private readonly Channel<ClientCommandRequest> clientCommandRequestChannel = Channel.CreateUnbounded<ClientCommandRequest>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly Channel<ClientResponseCommand> clientResponseChannel = Channel.CreateUnbounded<ClientResponseCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly Channel<ServerRelayCommand> serverRelayRequestChannel = Channel.CreateUnbounded<ServerRelayCommand>(
        new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    private readonly Channel<ServerRelayResult> serverRelayResponseChannel = Channel.CreateUnbounded<ServerRelayResult>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
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
    private Task serverRelayResponseTask;
    private Task[] clientCommandWorkerTasks = Array.Empty<Task>();
    private Task[] clientResponseWorkerTasks = Array.Empty<Task>();
    private Task[] serverRelayWorkerTasks = Array.Empty<Task>();
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
    private long totalReceivedMessageBytes;
    private long totalSentMessageBytes;
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

    public void ConfigureClientLocationCache(ClientLocationCacheConfig config)
    {
        this.clientLocationCache = new ClientLocationCache(config);
    }

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
        RelayLogger.Debug(() => $"Relay server snapshot refresh started. instanceId={this.instanceId}, controlServers={this.controlServers.Count}");
        List<Task<(EndpointConfig Endpoint, ClusterStatusSnapshot Snapshot)>> snapshotTasks = this.controlServers
            .Select(FetchRelaySnapshotAsync)
            .ToList();
        while (snapshotTasks.Count > 0)
        {
            Task<(EndpointConfig Endpoint, ClusterStatusSnapshot Snapshot)> completedTask = await Task.WhenAny(snapshotTasks);
            snapshotTasks.Remove(completedTask);
            (EndpointConfig endpoint, ClusterStatusSnapshot snapshot) = await completedTask;
            if (snapshot == null)
            {
                continue;
            }

            receivedSnapshot = true;
            RelayLogger.Info($"Relay server snapshot received. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}, servers={snapshot.ServerCount}");
            foreach (BackendServerSnapshot server in snapshot.Servers)
            {
                if (!IsRelayCandidate(server))
                {
                    RelayLogger.Debug(() => $"Relay server candidate skipped. sourceInstanceId={this.instanceId}, candidateInstanceId={server?.InstanceId}, host={server?.Host}, port={server?.Port}, health={server?.Health}");
                    continue;
                }

                if (!latestServers.TryGetValue(server.InstanceId, out BackendServerSnapshot existing) ||
                    server.Version > existing.Version ||
                    (server.Version == existing.Version && server.UpdatedAt > existing.UpdatedAt))
                {
                    latestServers[server.InstanceId] = server;
                }
            }

            break;
        }

        foreach (Task<(EndpointConfig Endpoint, ClusterStatusSnapshot Snapshot)> task in snapshotTasks.Where(task => !task.IsCompleted))
        {
            _ = task.ContinueWith(_ => { }, TaskContinuationOptions.ExecuteSynchronously);
        }

        async Task<(EndpointConfig Endpoint, ClusterStatusSnapshot Snapshot)> FetchRelaySnapshotAsync(EndpointConfig endpoint)
        {
            try
            {
                RelayLogger.Debug(() => $"Relay server snapshot request started. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}");
                PersistentSecureChannel channel = this.GetControlCommandChannel(endpoint);
                (bool success, SocketMessageFrame frame) = await channel.SendAndReceiveAsync(
                    connection => ControlProtocol.SendAndReceiveAsync(
                        connection,
                        0,
                        ControlMessageIds.RegistrySnapshotRequest,
                        new RegistrySnapshotRequest { RequestedAt = DateTimeOffset.UtcNow }),
                    SocketFactory.ReadTimeoutMilliseconds);

                if (!success ||
                    frame.MessageId != ControlMessageIds.RegistrySnapshotResponse ||
                    !ControlProtocol.TryDecode<ClusterStatusSnapshot>(frame, ControlMessageIds.RegistrySnapshotResponse, out var snapshot) ||
                    snapshot == null)
                {
                    return (endpoint, null);
                }

                return (endpoint, snapshot);
            }
            catch (SocketException exception)
            {
                Logger.Warn($"Relay server snapshot refresh failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
                RelayLogger.Warn($"Relay server snapshot refresh failed. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (IOException exception)
            {
                Logger.Warn($"Relay server snapshot refresh I/O failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
                RelayLogger.Warn($"Relay server snapshot refresh I/O failed. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"Relay server snapshot refresh timed out. endpoint={endpoint.Host}:{endpoint.Port}", exception);
                RelayLogger.Warn($"Relay server snapshot refresh timed out. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }
            catch (AuthenticationException exception)
            {
                Logger.Warn($"Relay server snapshot refresh authentication failed. endpoint={endpoint.Host}:{endpoint.Port}", exception);
                RelayLogger.Warn($"Relay server snapshot refresh authentication failed. instanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}", exception);
            }

            return (endpoint, null);
        }

        if (!receivedSnapshot)
        {
            RelayLogger.Warn($"Relay server snapshot refresh completed without response. instanceId={this.instanceId}, cachedRelayServers={this.relayServers.Count}");
            return this.relayServers.Count;
        }

        foreach (string instanceIdKey in this.relayServers.Keys)
        {
            if (!latestServers.ContainsKey(instanceIdKey))
            {
                this.relayServers.TryRemove(instanceIdKey, out _);
                RelayLogger.Info($"Relay server removed from cache. sourceInstanceId={this.instanceId}, removedInstanceId={instanceIdKey}");
            }
        }

        foreach (KeyValuePair<string, BackendServerSnapshot> item in latestServers)
        {
            this.relayServers[item.Key] = item.Value;
            RelayLogger.Debug(() => $"Relay server cached. sourceInstanceId={this.instanceId}, relayInstanceId={item.Key}, endpoint={item.Value.Host}:{item.Value.Port}, health={item.Value.Health}, current={item.Value.CurrentConnections}, available={item.Value.AvailableConnections}");
        }

        Logger.Debug(() => $"Relay server list refreshed. instanceId={this.instanceId}, relayServers={this.relayServers.Count}");
        RelayLogger.Info($"Relay server list refreshed. instanceId={this.instanceId}, relayServers={this.relayServers.Count}");
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
            LocalCertificateStore.GetOrCreateCertificateContext("SocketServer");
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
        this.ClosePersistentChannels();
        this.CloseConnectedClients();
        await WaitForTaskAsync(this.acceptLoopTask, timeout);
        foreach (Task task in this.clientCommandWorkerTasks)
        {
            await WaitForTaskAsync(task, timeout);
        }

        foreach (Task task in this.serverRelayWorkerTasks)
        {
            await WaitForTaskAsync(task, timeout);
        }

        foreach (Task task in this.clientResponseWorkerTasks)
        {
            await WaitForTaskAsync(task, timeout);
        }

        await WaitForTaskAsync(this.serverRelayResponseTask, timeout);
        this.acceptLoopCancellation?.Dispose();
        this.acceptLoopCancellation = null;
        this.acceptLoopTask = null;
        this.serverRelayResponseTask = null;
        this.clientCommandWorkerTasks = Array.Empty<Task>();
        this.clientResponseWorkerTasks = Array.Empty<Task>();
        this.serverRelayWorkerTasks = Array.Empty<Task>();
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
        int clientCommandWorkerCount = this.GetClientCommandWorkerCount();
        int clientResponseWorkerCount = this.GetClientResponseWorkerCount();
        this.clientCommandWorkerTasks = DedicatedWorker.StartMany(
            clientCommandWorkerCount,
            this.RunClientCommandRequestLoopAsync,
            this.acceptLoopCancellation.Token);
        this.clientResponseWorkerTasks = DedicatedWorker.StartMany(
            clientResponseWorkerCount,
            this.RunClientResponseLoopAsync,
            this.acceptLoopCancellation.Token);
        this.serverRelayWorkerTasks = DedicatedWorker.StartMany(
            ServerRelayWorkerCount,
            this.RunServerRelayRequestLoopAsync,
            this.acceptLoopCancellation.Token);
        this.serverRelayResponseTask = DedicatedWorker.Start(
            this.RunServerRelayResponseLoopAsync,
            this.acceptLoopCancellation.Token);
        this.acceptLoopTask = DedicatedWorker.Start(this.RunClientAcceptLoopAsync, this.acceptLoopCancellation.Token);
        Logger.Info($"Accept loop started. endpoint={this.GetIpAddress()}:{this.GetPort()}, commandWorkers={clientCommandWorkerCount}, responseWorkers={clientResponseWorkerCount}, relayWorkers={ServerRelayWorkerCount}");
        return true;
    }

    private int GetClientCommandWorkerCount()
    {
        return CalculateWorkerCount(
            this.maxConnections,
            ConnectionsPerClientCommandWorker,
            MinimumClientCommandWorkerCount,
            MaximumClientWorkerCount);
    }

    private int GetClientResponseWorkerCount()
    {
        return CalculateWorkerCount(
            this.maxConnections,
            ConnectionsPerClientResponseWorker,
            MinimumClientResponseWorkerCount,
            MaximumClientWorkerCount);
    }

    private static int CalculateWorkerCount(int connectionLimit, int connectionsPerWorker, int minimum, int maximum)
    {
        int normalizedConnections = Math.Max(1, connectionLimit);
        int normalizedConnectionsPerWorker = Math.Max(1, connectionsPerWorker);
        int calculated = (int)Math.Ceiling(normalizedConnections / (double)normalizedConnectionsPerWorker);
        return Math.Min(Math.Max(minimum, calculated), maximum);
    }

    public int GetConnectedClientCount()
    {
        return Volatile.Read(ref this.activeConnectionSlots);
    }

    public TcpServerStatus GetStatus()
    {
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
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
            TotalReceivedMessageBytes = Interlocked.Read(ref this.totalReceivedMessageBytes),
            TotalSentMessageBytes = Interlocked.Read(ref this.totalSentMessageBytes),
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
            ObservedAt = observedAt,
            UpdatedAt = observedAt
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
            bool slotAcquired = false;
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

                slotAcquired = true;
                SecureSocketConnection connection =
                    await SecureSocketConnection.AuthenticateServerAsync(client, "SocketServer");
                long connectionId = Interlocked.Increment(ref this.nextConnectionId);
                ConnectionSession session = new(connectionId, connection);
                this.AddConnectedClient(session);
                slotAcquired = false;
                Interlocked.Increment(ref this.totalAcceptedClients);
                Logger.Debug(() => $"Client accepted. connectionId={session.Id}, remote={session.RemoteEndPoint}, connectedClients={this.GetConnectedClientCount()}");
                session.HandlerTask = this.HandleClientAsync(session, cancellationToken);
            }
            catch (SocketException exception)
            {
                this.ReleaseUnregisteredConnectionSlot(slotAcquired);
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client accept failed.", exception);
            }
            catch (IOException exception)
            {
                this.ReleaseUnregisteredConnectionSlot(slotAcquired);
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client TLS connection failed during accept.", exception);
            }
            catch (AuthenticationException exception)
            {
                this.ReleaseUnregisteredConnectionSlot(slotAcquired);
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn("Client TLS authentication failed during accept.", exception);
            }
            catch (ObjectDisposedException)
            {
                this.ReleaseUnregisteredConnectionSlot(slotAcquired);
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
                Logger.Debug(() => $"Client closed by cleanup scheduler. connectionId={session.Id}, remote={session.RemoteEndPoint}");
            }
        }

        return closedCount;
    }

    private int GetClientFrameHeaderReadTimeoutMilliseconds()
    {
        double timeoutMilliseconds =
            this.idleTimeout.TotalMilliseconds +
            this.idleScanInterval.TotalMilliseconds +
            SocketFactory.ReadTimeoutMilliseconds;

        if (timeoutMilliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }

        return Math.Max(SocketFactory.ReadTimeoutMilliseconds, (int)Math.Ceiling(timeoutMilliseconds));
    }

    private async Task HandleClientAsync(ConnectionSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(
                    session.Connection,
                    this.GetClientFrameHeaderReadTimeoutMilliseconds(),
                    SocketFactory.ReadTimeoutMilliseconds);
                if (!success)
                {
                    break;
                }

                if (ClientMessageProtocol.TryDecodeRelay(frame, out ServerRelayMessage relayMessage))
                {
                    session.MarkReceived(frame.ClientId);
                    Interlocked.Increment(ref this.totalReceivedMessages);
                    this.AddReceivedMessageBytes(frame);
                    await this.EnqueueServerRelayReceiveAsync(session, relayMessage);
                    continue;
                }

                if (ClientMessageProtocol.TryDecodeRelayBatch(frame, out ServerRelayBatchMessage relayBatch))
                {
                    session.MarkReceived(frame.ClientId);
                    Interlocked.Increment(ref this.totalReceivedMessages);
                    this.AddReceivedMessageBytes(frame);
                    await this.EnqueueServerRelayBatchReceiveAsync(session, relayBatch);
                    continue;
                }

                if (!this.IsClientFrameAuthorized(session, frame.ClientId))
                {
                    break;
                }

                session.MarkReceived(frame.ClientId);
                this.UpdateClientIndex(session);
                Interlocked.Increment(ref this.totalReceivedMessages);
                if (!HealthCheckProtocol.TryDecode(frame, out _))
                {
                    this.AddReceivedMessageBytes(frame);
                }

                bool shouldReportOpened = !session.HasReportedOpened;
                if (!session.HasReportedOpened)
                {
                    session.MarkReportedOpened();
                }

                bool handled = await this.EnqueueClientCommandAsync(session, frame);
                if (shouldReportOpened)
                {
                    _ = this.NotifySessionOpenedAsync(session);
                }

                _ = this.NotifySessionUpdatedAsync(session);
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

    private bool IsClientFrameAuthorized(ConnectionSession session, uint clientId)
    {
        if (SecureSocketConnection.SecurityProfile != SocketSecurityProfile.EndToEndTls ||
            !SecureSocketConnection.RequireClientCertificate ||
            !SecureSocketConnection.EnforceClientCertificateId ||
            clientId == 0)
        {
            return true;
        }

        if (!session.AuthenticatedClientId.HasValue)
        {
            Logger.Warn($"Client frame rejected because TLS certificate has no clientId binding. instanceId={this.InstanceId}, connectionId={session.Id}, frameClientId={clientId}");
            return false;
        }

        if (session.AuthenticatedClientId.Value != clientId)
        {
            Logger.Warn($"Client frame rejected because TLS certificate clientId does not match frame clientId. instanceId={this.InstanceId}, connectionId={session.Id}, authenticatedClientId={session.AuthenticatedClientId.Value}, frameClientId={clientId}");
            return false;
        }

        return true;
    }

    private async Task<bool> EnqueueClientCommandAsync(ConnectionSession session, SocketMessageFrame frame)
    {
        ClientCommandRequest request = new(session, frame);
        if (!this.clientCommandRequestChannel.Writer.TryWrite(request))
        {
            Logger.Warn($"Client command queue rejected frame. instanceId={this.instanceId}, connectionId={session.Id}, messageId={frame.MessageId}");
            return await this.HandleClientMessageAsync(session, frame);
        }

        return await request.Completion.Task;
    }

    private async Task RunClientCommandRequestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ClientCommandRequest request in this.clientCommandRequestChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    bool result = await this.HandleClientMessageAsync(request.Session, request.Frame);
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
            Logger.Warn($"Client command worker stopped unexpectedly. instanceId={this.instanceId}", exception);
        }
    }

    private async Task<bool> EnqueueClientResponseAsync(ConnectionSession session, byte[] payload, string operation)
    {
        ClientResponseCommand command = new(session, payload, operation);
        if (!this.clientResponseChannel.Writer.TryWrite(command))
        {
            Logger.Warn($"Client response queue rejected command. instanceId={this.instanceId}, connectionId={session.Id}, operation={operation}");
            return await session.SendAsync(payload);
        }

        return await command.Completion.Task;
    }

    private async Task<bool> EnqueueClientResponseAsync(ConnectionSession session, SocketMessageFrame frame, string operation)
    {
        return await this.EnqueueClientResponseAsync(session, frame.Encode(), operation);
    }

    private async Task RunClientResponseLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ClientResponseCommand command in this.clientResponseChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    bool sent = await command.Session.SendAsync(command.Payload);
                    command.Completion.TrySetResult(sent);
                    Logger.Debug(() => $"Client response command processed. instanceId={this.instanceId}, connectionId={command.Session.Id}, operation={command.Operation}, success={sent}");
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
            Logger.Warn($"Client response worker stopped unexpectedly. instanceId={this.instanceId}", exception);
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

            Logger.Debug(() => $"Healthcheck ping received. instanceId={this.InstanceId}, connectionId={session.Id}, clientId={healthCheckMessage.ClientId}");
            sent = await this.EnqueueClientResponseAsync(
                session,
                HealthCheckProtocol.Encode(HealthCheckProtocol.CreatePong(healthCheckMessage.ClientId)),
                "HealthCheckPong");
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
                Logger.Debug(() => $"Healthcheck pong sent. instanceId={this.InstanceId}, connectionId={session.Id}, clientId={healthCheckMessage.ClientId}");
            }
            else
            {
                Logger.Warn($"Healthcheck pong send failed. instanceId={this.InstanceId}, connectionId={session.Id}, clientId={healthCheckMessage.ClientId}");
            }

            return sent;
        }

        if (HelloWorldProtocol.TryDecodeRequest(frame, out HelloWorldRequest helloWorldRequest))
        {
            SocketMessageFrame.TryDecode(
                HelloWorldProtocol.Encode(HelloWorldProtocol.CreateResponse(helloWorldRequest.ClientId)),
                out SocketMessageFrame responseFrame);
            sent = await this.EnqueueClientResponseAsync(
                session,
                responseFrame,
                "HelloWorldResponse");
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
                this.AddSentMessageBytes(responseFrame);
            }

            return sent;
        }

        if (ClientMessageProtocol.TryDecodeRegister(frame, out ClientRegisterRequest registerRequest))
        {
            session.MarkReceived(registerRequest.ClientId);
            if (registerRequest.ClientId > 0 &&
                this.connectedClientsById.TryGetValue(registerRequest.ClientId, out ConnectionSession existingSession) &&
                existingSession.Id != session.Id &&
                !existingSession.IsClosed)
            {
                SocketMessageFrame duplicateAckFrame = ClientMessageProtocol.CreateFrame(
                    registerRequest.ClientId,
                    ClientMessageIds.ClientRegisterAck,
                    new ClientRegisterAck
                    {
                        ClientId = registerRequest.ClientId,
                        Success = false,
                        ErrorMessage = "Duplicate clientId is already connected.",
                        RetryAfterSeconds = Math.Max(1, (int)Math.Ceiling(this.idleTimeout.TotalSeconds))
                    });
                sent = await this.EnqueueClientResponseAsync(
                    session,
                    duplicateAckFrame,
                    "ClientRegisterDuplicateAck");
                if (sent)
                {
                    Interlocked.Increment(ref this.totalSentMessages);
                    this.AddSentMessageBytes(duplicateAckFrame);
                }

                Logger.Warn($"Duplicate client register rejected. instanceId={this.InstanceId}, clientId={registerRequest.ClientId}, existingConnectionId={existingSession.Id}, rejectedConnectionId={session.Id}");
                return false;
            }

            this.UpdateClientIndex(session);
            SocketMessageFrame ackFrame = ClientMessageProtocol.CreateFrame(
                registerRequest.ClientId,
                ClientMessageIds.ClientRegisterAck,
                new ClientRegisterAck
                {
                    ClientId = registerRequest.ClientId,
                    Success = registerRequest.ClientId > 0,
                    ErrorMessage = registerRequest.ClientId == 0 ? "ClientId must be greater than zero." : ""
                });
            sent = await this.EnqueueClientResponseAsync(
                session,
                ackFrame,
                "ClientRegisterAck");
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
                this.AddSentMessageBytes(ackFrame);
            }

            return sent;
        }

        if (ClientMessageProtocol.TryDecodeSendRequest(frame, out ClientMessageSendRequest clientMessageRequest))
        {
            return await this.HandleClientMessageSendAsync(session, clientMessageRequest);
        }

        if (ClientMessageProtocol.TryDecodeRelay(frame, out ServerRelayMessage relayMessage))
        {
            await this.EnqueueServerRelayReceiveAsync(session, relayMessage);
            return false;
        }

        if (ClientMessageProtocol.TryDecodeRelayBatch(frame, out ServerRelayBatchMessage relayBatch))
        {
            await this.EnqueueServerRelayBatchReceiveAsync(session, relayBatch);
            return false;
        }

        return false;
    }

    private async Task<bool> HandleClientMessageSendAsync(ConnectionSession sourceSession, ClientMessageSendRequest request)
    {
        RelayLogger.Info($"Client message send requested. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, sourceClientId={request.SourceClientId}, targetClientId={request.TargetClientId}, ttlSeconds={request.TtlSeconds}");
        if (request.SourceClientId == 0 || request.TargetClientId == 0)
        {
            RelayLogger.Warn($"Client message send rejected. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, sourceClientId={request.SourceClientId}, targetClientId={request.TargetClientId}, reason=InvalidClientId");
            return await this.SendClientMessageErrorAsync(sourceSession, request, "InvalidClientId", "Source and target client ids must be greater than zero.");
        }

        sourceSession.MarkReceived(request.SourceClientId);
        this.UpdateClientIndex(sourceSession);

        if (await this.DeliverToLocalClientAsync(request))
        {
            RelayLogger.Info($"Client message delivered locally. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            return await this.SendClientMessageAckAsync(sourceSession, request, this.instanceId);
        }

        RelayLogger.Debug(() => $"Client message local delivery missed. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
        string relayErrorMessage = "";
        if (this.clientLocationCache.TryGet(request.TargetClientId, out CachedClientLocation cachedLocation))
        {
            RelayLogger.Debug(() => $"Client location cache hit. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, targetInstanceId={cachedLocation.InstanceId}");
            (bool cacheSuccess, string cacheTargetInstanceId, string cacheErrorMessage) = await this.RelayToRemoteServerAsync(cachedLocation, request);
            if (cacheSuccess)
            {
                RelayLogger.Info($"Client message delivered by cached targeted relay. sourceInstanceId={this.instanceId}, targetInstanceId={cacheTargetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
                return await this.SendClientMessageAckAsync(sourceSession, request, cacheTargetInstanceId);
            }

            this.clientLocationCache.Invalidate(request.TargetClientId);
            relayErrorMessage = cacheErrorMessage;
            RelayLogger.Warn($"Client location cache stale or failed. sourceInstanceId={this.instanceId}, targetInstanceId={cacheTargetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, error={cacheErrorMessage}");
        }

        ClientLocationResponse location = await this.ResolveClientLocationAsync(request.SourceClientId, request.TargetClientId);
        if (location != null && location.Success && location.InstanceId == this.instanceId)
        {
            RelayLogger.Warn($"Client message location resolved to local instance but target is absent. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            return await this.SendClientMessageErrorAsync(sourceSession, request, "TargetNotConnected", "Target client is not connected to this server.");
        }

        if (location != null && location.Success)
        {
            (bool success, string targetInstanceId, string errorMessage) = await this.RelayToRemoteServerAsync(location, request);
            if (success)
            {
                this.clientLocationCache.Set(request.TargetClientId, location);
                RelayLogger.Info($"Client message delivered by targeted relay. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
                return await this.SendClientMessageAckAsync(sourceSession, request, targetInstanceId);
            }

            if (IsTargetNotConnectedError(errorMessage))
            {
                this.clientLocationCache.Invalidate(request.TargetClientId);
            }

            relayErrorMessage = errorMessage;
            RelayLogger.Warn($"Client message targeted relay failed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, error={errorMessage}");
        }

        (bool broadcastSuccess, string broadcastTargetInstanceId, string broadcastErrorMessage) =
            await this.BroadcastRelayToKnownServersAsync(request);
        if (broadcastSuccess)
        {
            this.CacheBroadcastRelayTarget(request.TargetClientId, broadcastTargetInstanceId);
            RelayLogger.Info($"Client message delivered by broadcast relay. sourceInstanceId={this.instanceId}, targetInstanceId={broadcastTargetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            return await this.SendClientMessageAckAsync(sourceSession, request, broadcastTargetInstanceId);
        }

        relayErrorMessage = string.IsNullOrWhiteSpace(broadcastErrorMessage) ? relayErrorMessage : broadcastErrorMessage;
        RelayLogger.Warn($"Client message target relay failed. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, locationSuccess={location?.Success ?? false}, error={relayErrorMessage}");
        return await this.SendClientMessageErrorAsync(
            sourceSession,
            request,
            "RelayFailed",
            string.IsNullOrWhiteSpace(relayErrorMessage)
                ? location?.ErrorMessage ?? "Target client location was not found."
                : relayErrorMessage);
    }

    private async Task HandleServerRelayAsync(ConnectionSession relaySession, ServerRelayMessage relayMessage)
    {
        RelayLogger.Info($"Server relay received. receiverInstanceId={this.instanceId}, sourceInstanceId={relayMessage.SourceInstanceId}, messageToken={relayMessage.MessageToken}, sourceClientId={relayMessage.SourceClientId}, targetClientId={relayMessage.TargetClientId}, ttlSeconds={relayMessage.TtlSeconds}");
        ServerRelayBatchResultItem result = await this.ApplyServerRelayMessageAsync(relayMessage);
        ClientMessageSendRequest request = CreateClientMessageRequest(relayMessage);
        uint messageId = result.Success
            ? ServerRelayMessageIds.ServerRelayAck
            : ServerRelayMessageIds.ServerRelayError;
        object payload = result.Success
            ? CreateClientMessageAck(request, this.instanceId)
            : CreateClientMessageError(request, result.ErrorCode, result.ErrorMessage);
        await this.EnqueueClientResponseAsync(
            relaySession,
            ClientMessageProtocol.CreateFrame(request.SourceClientId, messageId, payload),
            result.Success ? "ServerRelayAck" : "ServerRelayError");
        if (result.Success)
        {
            RelayLogger.Info($"Server relay delivered. receiverInstanceId={this.instanceId}, sourceInstanceId={relayMessage.SourceInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
        }
    }

    private async Task HandleServerRelayBatchAsync(ConnectionSession relaySession, ServerRelayBatchMessage relayBatch)
    {
        ServerRelayBatchResult result = new()
        {
            CreatedAt = DateTimeOffset.UtcNow
        };

        foreach (ServerRelayMessage relayMessage in relayBatch.Items)
        {
            result.Items.Add(await this.ApplyServerRelayMessageAsync(relayMessage));
        }

        await this.EnqueueClientResponseAsync(
            relaySession,
            ClientMessageProtocol.CreateFrame(
                relayBatch.Items.FirstOrDefault()?.SourceClientId ?? 0,
                ServerRelayMessageIds.ServerRelayBatchResult,
                result),
            "ServerRelayBatchResult");
        RelayLogger.Info($"Server relay batch processed. receiverInstanceId={this.instanceId}, count={relayBatch.Items.Count}, successCount={result.Items.Count(item => item.Success)}");
    }

    private async Task<ServerRelayBatchResultItem> ApplyServerRelayMessageAsync(ServerRelayMessage relayMessage)
    {
        ClientMessageSendRequest request = CreateClientMessageRequest(relayMessage);

        if (DateTimeOffset.UtcNow - request.CreatedAt > TimeSpan.FromSeconds(Math.Max(1, request.TtlSeconds)))
        {
            string errorMessage = "Relay message ttl expired.";
            RelayLogger.Warn($"Server relay expired. receiverInstanceId={this.instanceId}, sourceInstanceId={relayMessage.SourceInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            return new ServerRelayBatchResultItem
            {
                MessageToken = request.MessageToken,
                Success = false,
                TargetInstanceId = this.instanceId,
                ErrorCode = "MessageExpired",
                ErrorMessage = errorMessage
            };
        }

        if (!await this.DeliverToLocalClientAsync(request))
        {
            string errorMessage = "Target client is not connected to this server.";
            RelayLogger.Debug(() => $"Server relay target not connected. receiverInstanceId={this.instanceId}, sourceInstanceId={relayMessage.SourceInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            return new ServerRelayBatchResultItem
            {
                MessageToken = request.MessageToken,
                Success = false,
                TargetInstanceId = this.instanceId,
                ErrorCode = "TargetNotConnected",
                ErrorMessage = errorMessage
            };
        }

        return new ServerRelayBatchResultItem
        {
            MessageToken = request.MessageToken,
            Success = true,
            TargetInstanceId = this.instanceId
        };
    }

    private static ClientMessageSendRequest CreateClientMessageRequest(ServerRelayMessage relayMessage)
    {
        return new ClientMessageSendRequest
        {
            MessageToken = relayMessage.MessageToken,
            SourceClientId = relayMessage.SourceClientId,
            TargetClientId = relayMessage.TargetClientId,
            Content = relayMessage.Content,
            TtlSeconds = relayMessage.TtlSeconds,
            CreatedAt = relayMessage.CreatedAt
        };
    }

    private async Task EnqueueServerRelayReceiveAsync(ConnectionSession relaySession, ServerRelayMessage relayMessage)
    {
        ServerRelayCommand command = new(relaySession, relayMessage);
        if (!this.serverRelayRequestChannel.Writer.TryWrite(command))
        {
            RelayLogger.Warn($"Server relay request queue rejected receive command. receiverInstanceId={this.instanceId}, sourceInstanceId={relayMessage.SourceInstanceId}, messageToken={relayMessage.MessageToken}");
            await this.HandleServerRelayAsync(relaySession, relayMessage);
            return;
        }

        await command.Completion.Task;
    }

    private async Task EnqueueServerRelayBatchReceiveAsync(ConnectionSession relaySession, ServerRelayBatchMessage relayBatch)
    {
        ServerRelayCommand command = new(relaySession, relayBatch);
        if (!this.serverRelayRequestChannel.Writer.TryWrite(command))
        {
            RelayLogger.Warn($"Server relay request queue rejected batch receive command. receiverInstanceId={this.instanceId}, count={relayBatch.Items.Count}");
            await this.HandleServerRelayBatchAsync(relaySession, relayBatch);
            return;
        }

        await command.Completion.Task;
    }

    private async Task RunServerRelayRequestLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ServerRelayCommand command in this.serverRelayRequestChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    if (command.IsReceiveCommand)
                    {
                        await this.HandleServerRelayAsync(command.Session, command.RelayMessage);
                        command.Completion.TrySetResult((true, this.instanceId, ""));
                        this.serverRelayResponseChannel.Writer.TryWrite(new ServerRelayResult(
                            true,
                            this.instanceId,
                            command.RelayMessage?.MessageToken ?? "",
                            ""));
                        continue;
                    }

                    if (command.IsReceiveBatchCommand)
                    {
                        await this.HandleServerRelayBatchAsync(command.Session, command.RelayBatchMessage);
                        command.Completion.TrySetResult((true, this.instanceId, ""));
                        this.serverRelayResponseChannel.Writer.TryWrite(new ServerRelayResult(
                            true,
                            this.instanceId,
                            $"batch:{command.RelayBatchMessage?.Items.Count ?? 0}",
                            ""));
                        continue;
                    }

                    List<ServerRelayCommand> batch = await this.CollectServerRelaySendBatchAsync(command, cancellationToken);
                    await this.SendServerRelayCommandBatchAsync(batch);
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
            RelayLogger.Warn($"Server relay worker stopped unexpectedly. instanceId={this.instanceId}", exception);
        }
    }

    private async Task<List<ServerRelayCommand>> CollectServerRelaySendBatchAsync(
        ServerRelayCommand firstCommand,
        CancellationToken cancellationToken)
    {
        List<ServerRelayCommand> batch = new(ServerRelayBatchMaxItems) { firstCommand };
        try
        {
            await Task.Delay(ServerRelayBatchFlushIntervalMilliseconds, cancellationToken);
        }
        catch (TaskCanceledException)
        {
            return batch;
        }

        while (batch.Count < ServerRelayBatchMaxItems &&
            this.serverRelayRequestChannel.Reader.TryRead(out ServerRelayCommand? command))
        {
            if (command.IsReceiveCommand || command.IsReceiveBatchCommand)
            {
                await this.ProcessServerRelayReceiveCommandAsync(command);
                continue;
            }

            batch.Add(command);
        }

        return batch;
    }

    private async Task ProcessServerRelayReceiveCommandAsync(ServerRelayCommand command)
    {
        if (command.IsReceiveCommand)
        {
            await this.HandleServerRelayAsync(command.Session, command.RelayMessage);
            command.Completion.TrySetResult((true, this.instanceId, ""));
            return;
        }

        if (command.IsReceiveBatchCommand)
        {
            await this.HandleServerRelayBatchAsync(command.Session, command.RelayBatchMessage);
            command.Completion.TrySetResult((true, this.instanceId, ""));
        }
    }

    private async Task SendServerRelayCommandBatchAsync(IReadOnlyList<ServerRelayCommand> commands)
    {
        foreach (IGrouping<string, ServerRelayCommand> group in commands.GroupBy(command => $"{command.Host}:{command.Port}"))
        {
            ServerRelayCommand[] groupCommands = group.ToArray();
            if (groupCommands.Length == 1)
            {
                ServerRelayCommand command = groupCommands[0];
                (bool success, string targetInstanceId, string errorMessage) = await this.SendRelayToRemoteServerAsync(
                    command.Host,
                    command.Port,
                    command.TargetInstanceId,
                    command.Request);
                command.Completion.TrySetResult((success, targetInstanceId, errorMessage));
                this.serverRelayResponseChannel.Writer.TryWrite(new ServerRelayResult(
                    success,
                    targetInstanceId,
                    command.Request?.MessageToken ?? "",
                    errorMessage));
                continue;
            }

            await this.SendRelayBatchToRemoteServerAsync(groupCommands);
        }
    }

    private async Task RunServerRelayResponseLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ServerRelayResult result in this.serverRelayResponseChannel.Reader.ReadAllAsync(cancellationToken))
            {
                if (result.Success)
                {
                    RelayLogger.Debug(() => $"Server relay response processed. sourceInstanceId={this.instanceId}, targetInstanceId={result.TargetInstanceId}, messageToken={result.MessageToken}, success=true");
                    continue;
                }

                RelayLogger.Warn($"Server relay response failed. sourceInstanceId={this.instanceId}, targetInstanceId={result.TargetInstanceId}, messageToken={result.MessageToken}, error={result.ErrorMessage}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            RelayLogger.Warn($"Server relay response worker stopped unexpectedly. instanceId={this.instanceId}", exception);
        }
    }

    private async Task<bool> DeliverToLocalClientAsync(ClientMessageSendRequest request)
    {
        if (!this.connectedClientsById.TryGetValue(request.TargetClientId, out ConnectionSession targetSession) ||
            targetSession.IsClosed)
        {
            RelayLogger.Debug(() => $"Local client delivery skipped. instanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, reason=TargetNotConnected");
            return false;
        }

        SocketMessageFrame deliveryFrame = ClientMessageProtocol.CreateFrame(
            request.TargetClientId,
            ClientMessageIds.ClientMessageDeliver,
            ClientMessageProtocol.CreateDelivery(request));
        bool sent = await this.EnqueueClientResponseAsync(
            targetSession,
            deliveryFrame,
            "ClientMessageDeliver");
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
            this.AddSentMessageBytes(deliveryFrame);
            RelayLogger.Debug(() => $"Local client delivery sent. instanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, connectionId={targetSession.Id}");
            return true;
        }

        RelayLogger.Warn($"Local client delivery failed. instanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, connectionId={targetSession.Id}");
        this.RemoveConnectedClient(targetSession);
        return false;
    }

    private async Task<ClientLocationResponse> ResolveClientLocationAsync(uint sourceClientId, uint targetClientId)
    {
        foreach (EndpointConfig endpoint in this.controlServers)
        {
            try
            {
                RelayLogger.Debug(() => $"Client location request started. sourceInstanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}, sourceClientId={sourceClientId}, targetClientId={targetClientId}");
                PersistentSecureChannel channel = this.GetControlCommandChannel(endpoint);
                (bool success, SocketMessageFrame frame) = await channel.SendAndReceiveAsync(
                    connection => ControlProtocol.SendAndReceiveAsync(
                        connection,
                        sourceClientId,
                        ControlMessageIds.ClientLocationRequest,
                        new ClientLocationRequest
                        {
                            SourceClientId = sourceClientId,
                            TargetClientId = targetClientId
                        }),
                    SocketFactory.ReadTimeoutMilliseconds);

                if (success &&
                    (frame.MessageId == ControlMessageIds.ClientLocationResponse ||
                        frame.MessageId == ControlMessageIds.ClientLocationNotFound) &&
                    ControlProtocol.TryDecode(frame, frame.MessageId, out ClientLocationResponse response))
                {
                    RelayLogger.Info($"Client location response received. sourceInstanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}, success={response.Success}, targetInstanceId={response.InstanceId}");
                    return response;
                }
            }
            catch (SocketException exception)
            {
                Logger.Warn($"Client location request failed. endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
                RelayLogger.Warn($"Client location request failed. sourceInstanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
            }
            catch (TimeoutException exception)
            {
                Logger.Warn($"Client location request timed out. endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
                RelayLogger.Warn($"Client location request timed out. sourceInstanceId={this.instanceId}, endpoint={endpoint.Host}:{endpoint.Port}, targetClientId={targetClientId}", exception);
            }
        }

        RelayLogger.Warn($"Client location request exhausted. sourceInstanceId={this.instanceId}, sourceClientId={sourceClientId}, targetClientId={targetClientId}");
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
        CachedClientLocation location,
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
        ServerRelayCommand command = new(host, port, targetInstanceId, request);
        if (!this.serverRelayRequestChannel.Writer.TryWrite(command))
        {
            RelayLogger.Warn($"Server relay request queue rejected command. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}");
            return await this.SendRelayToRemoteServerAsync(host, port, targetInstanceId, request);
        }

        return await command.Completion.Task;
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> SendRelayToRemoteServerAsync(
        string host,
        int port,
        string targetInstanceId,
        ClientMessageSendRequest request)
    {
        try
        {
            RelayLogger.Info($"Server relay send started. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
            PersistentSecureChannelPool channelPool = this.GetServerRelayChannelPool(host, port);
            (bool success, SocketMessageFrame frame) = await channelPool.SendAndReceiveAsync(
                connection => ClientMessageProtocol.SendRelayAndReceiveAsync(
                    connection,
                    ClientMessageProtocol.CreateRelay(this.clusterId, this.instanceId, request)),
                SocketFactory.ReadTimeoutMilliseconds);
            if (!success)
            {
                RelayLogger.Warn($"Server relay send failed without response. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}");
                return (false, targetInstanceId, "Target server did not respond to relay.");
            }

            if (frame.MessageId == ServerRelayMessageIds.ServerRelayAck &&
                ClientMessageProtocol.TryDecode(frame, ServerRelayMessageIds.ServerRelayAck, out ClientMessageAck _))
            {
                RelayLogger.Info($"Server relay send acknowledged. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}");
                return (true, targetInstanceId, "");
            }

            if (frame.MessageId == ServerRelayMessageIds.ServerRelayError &&
                ClientMessageProtocol.TryDecode(frame, ServerRelayMessageIds.ServerRelayError, out ClientMessageError error))
            {
                RelayLogger.Warn($"Server relay send returned error. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, errorCode={error.ErrorCode}, error={error.ErrorMessage}");
                return (false, targetInstanceId, error.ErrorMessage);
            }

            RelayLogger.Warn($"Server relay send returned invalid response. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, messageId={frame.MessageId}");
            return (false, targetInstanceId, "Target server returned an invalid relay response.");
        }
        catch (SocketException exception)
        {
            Logger.Warn($"Server relay failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            RelayLogger.Warn($"Server relay socket failed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay socket failed.");
        }
        catch (IOException exception)
        {
            Logger.Warn($"Server relay I/O failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            RelayLogger.Warn($"Server relay I/O failed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay I/O failed.");
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"Server relay authentication failed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            RelayLogger.Warn($"Server relay authentication failed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay authentication failed.");
        }
        catch (TimeoutException exception)
        {
            Logger.Warn($"Server relay timed out. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            RelayLogger.Warn($"Server relay timed out. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay timed out.");
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"Server relay channel was closed. target={host}:{port}, targetClientId={request.TargetClientId}", exception);
            RelayLogger.Warn($"Server relay channel was closed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}", exception);
            return (false, targetInstanceId, "Target server relay channel was closed.");
        }
    }

    private void CacheBroadcastRelayTarget(uint targetClientId, string targetInstanceId)
    {
        if (targetClientId == 0 || string.IsNullOrWhiteSpace(targetInstanceId))
        {
            return;
        }

        BackendServerSnapshot? server = this.GetRelayCandidates()
            .FirstOrDefault(item => string.Equals(item.InstanceId, targetInstanceId, StringComparison.Ordinal));
        if (server != null)
        {
            this.clientLocationCache.Set(targetClientId, server);
        }
    }

    private static bool IsTargetNotConnectedError(string errorMessage)
    {
        return !string.IsNullOrWhiteSpace(errorMessage) &&
            errorMessage.IndexOf("not connected", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private async Task SendRelayBatchToRemoteServerAsync(IReadOnlyList<ServerRelayCommand> commands)
    {
        ServerRelayCommand first = commands[0];
        string host = first.Host;
        int port = first.Port;
        string targetInstanceId = first.TargetInstanceId;
        ServerRelayBatchMessage batch = new()
        {
            CreatedAt = DateTimeOffset.UtcNow,
            Items = commands
                .Select(command => ClientMessageProtocol.CreateRelay(this.clusterId, this.instanceId, command.Request))
                .ToList()
        };

        try
        {
            RelayLogger.Info($"Server relay batch send started. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, count={batch.Items.Count}");
            PersistentSecureChannelPool channelPool = this.GetServerRelayChannelPool(host, port);
            (bool success, SocketMessageFrame frame) = await channelPool.SendAndReceiveAsync(
                connection => ClientMessageProtocol.SendRelayBatchAndReceiveAsync(
                    connection,
                    batch),
                SocketFactory.ReadTimeoutMilliseconds);
            if (!success ||
                frame.MessageId != ServerRelayMessageIds.ServerRelayBatchResult ||
                !ClientMessageProtocol.TryDecodeRelayBatchResult(frame, out ServerRelayBatchResult result))
            {
                this.CompleteRelayBatch(commands, false, targetInstanceId, "Target server did not return a valid relay batch response.");
                return;
            }

            Dictionary<string, ServerRelayBatchResultItem> resultsByToken = result.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.MessageToken))
                .GroupBy(item => item.MessageToken, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            foreach (ServerRelayCommand command in commands)
            {
                if (command.Request == null ||
                    !resultsByToken.TryGetValue(command.Request.MessageToken, out ServerRelayBatchResultItem? item))
                {
                    this.CompleteRelayCommand(command, false, targetInstanceId, "Target server did not return a result for the relay message.");
                    continue;
                }

                this.CompleteRelayCommand(
                    command,
                    item.Success,
                    string.IsNullOrWhiteSpace(item.TargetInstanceId) ? targetInstanceId : item.TargetInstanceId,
                    item.Success ? "" : item.ErrorMessage);
            }

            RelayLogger.Info($"Server relay batch send completed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, count={commands.Count}, successCount={result.Items.Count(item => item.Success)}");
        }
        catch (Exception exception) when (exception is SocketException ||
            exception is IOException ||
            exception is AuthenticationException ||
            exception is TimeoutException ||
            exception is ObjectDisposedException)
        {
            Logger.Warn($"Server relay batch failed. target={host}:{port}, count={commands.Count}", exception);
            RelayLogger.Warn($"Server relay batch failed. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, endpoint={host}:{port}, count={commands.Count}", exception);
            this.CompleteRelayBatch(commands, false, targetInstanceId, "Target server relay batch failed.");
        }
    }

    private void CompleteRelayBatch(
        IReadOnlyList<ServerRelayCommand> commands,
        bool success,
        string targetInstanceId,
        string errorMessage)
    {
        foreach (ServerRelayCommand command in commands)
        {
            this.CompleteRelayCommand(command, success, targetInstanceId, errorMessage);
        }
    }

    private void CompleteRelayCommand(
        ServerRelayCommand command,
        bool success,
        string targetInstanceId,
        string errorMessage)
    {
        command.Completion.TrySetResult((success, targetInstanceId, errorMessage));
        this.serverRelayResponseChannel.Writer.TryWrite(new ServerRelayResult(
            success,
            targetInstanceId,
            command.Request?.MessageToken ?? "",
            errorMessage));
    }

    private async Task<(bool Success, string TargetInstanceId, string ErrorMessage)> BroadcastRelayToKnownServersAsync(
        ClientMessageSendRequest request)
    {
        BackendServerSnapshot[] servers = this.GetRelayCandidates();
        if (servers.Length == 0)
        {
            await this.RefreshRelayServersFromControlServersAsync();
            servers = this.GetRelayCandidates();
        }

        RelayLogger.Info($"Broadcast relay started. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, candidateCount={servers.Length}");
        if (servers.Length == 0)
        {
            RelayLogger.Warn($"Broadcast relay skipped. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, reason=NoRelayCandidates");
            return (false, "", "No known relay SocketServer instances.");
        }

        List<Task<(bool Success, string TargetInstanceId, string ErrorMessage)>> pendingTasks = servers
            .Select(server => this.RelayToRemoteServerAsync(server, request))
            .ToList();
        string errorMessage = "";
        while (pendingTasks.Count > 0)
        {
            Task<(bool Success, string TargetInstanceId, string ErrorMessage)> completedTask =
                await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);

            (bool success, string targetInstanceId, string relayErrorMessage) = await completedTask;
            if (success)
            {
                if (pendingTasks.Count > 0)
                {
                    RelayLogger.Debug(() => $"Broadcast relay returning after first delivery. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, pendingRelayCount={pendingTasks.Count}");
                }

                RelayLogger.Info($"Broadcast relay delivered. sourceInstanceId={this.instanceId}, targetInstanceId={targetInstanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}");
                return (true, targetInstanceId, "");
            }

            if (string.IsNullOrWhiteSpace(errorMessage) && !string.IsNullOrWhiteSpace(relayErrorMessage))
            {
                errorMessage = relayErrorMessage;
            }
        }

        RelayLogger.Warn($"Broadcast relay failed. sourceInstanceId={this.instanceId}, messageToken={request.MessageToken}, targetClientId={request.TargetClientId}, candidateCount={servers.Length}, error={errorMessage}");
        return (false, "", string.IsNullOrWhiteSpace(errorMessage)
            ? "No relay SocketServer delivered the message."
            : errorMessage);
    }

    private BackendServerSnapshot[] GetRelayCandidates()
    {
        return this.relayServers.Values
            .Where(IsRelayCandidate)
            .ToArray();
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

    private PersistentSecureChannel GetControlCommandChannel(EndpointConfig endpoint)
    {
        string endpointKey = PersistentSecureChannel.CreateEndpointKey(endpoint.Host, endpoint.Port);
        return this.controlCommandChannels.GetOrAdd(
            endpointKey,
            _ => new PersistentSecureChannel(endpoint, "SocketServer"));
    }

    private PersistentSecureChannelPool GetServerRelayChannelPool(string host, int port)
    {
        string endpointKey = PersistentSecureChannel.CreateEndpointKey(host, port);
        return this.serverRelayChannelPools.GetOrAdd(
            endpointKey,
            _ =>
            {
                RelayLogger.Info($"Server relay channel pool created. sourceInstanceId={this.instanceId}, endpoint={endpointKey}, channelCount={MinimumServerRelayChannelCount}");
                return new PersistentSecureChannelPool(host, port, "SocketServer", MinimumServerRelayChannelCount);
            });
    }

    private void ClosePersistentChannels()
    {
        foreach (PersistentSecureChannel channel in this.controlCommandChannels.Values)
        {
            channel.Dispose();
        }

        foreach (PersistentSecureChannelPool channelPool in this.serverRelayChannelPools.Values)
        {
            channelPool.Dispose();
        }

        this.controlCommandChannels.Clear();
        this.serverRelayChannelPools.Clear();
    }

    private async Task<bool> SendClientMessageAckAsync(ConnectionSession sourceSession, ClientMessageSendRequest request, string targetInstanceId)
    {
        SocketMessageFrame ackFrame = ClientMessageProtocol.CreateFrame(
            request.SourceClientId,
            ClientMessageIds.ClientMessageAck,
            CreateClientMessageAck(request, targetInstanceId));
        bool sent = await this.EnqueueClientResponseAsync(
            sourceSession,
            ackFrame,
            "ClientMessageAck");
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
            this.AddSentMessageBytes(ackFrame);
        }

        return sent;
    }

    private async Task<bool> SendClientMessageErrorAsync(
        ConnectionSession sourceSession,
        ClientMessageSendRequest request,
        string errorCode,
        string errorMessage)
    {
        SocketMessageFrame errorFrame = ClientMessageProtocol.CreateFrame(
            request.SourceClientId,
            ClientMessageIds.ClientMessageError,
            CreateClientMessageError(request, errorCode, errorMessage));
        bool sent = await this.EnqueueClientResponseAsync(
            sourceSession,
            errorFrame,
            "ClientMessageError");
        if (sent)
        {
            Interlocked.Increment(ref this.totalSentMessages);
            this.AddSentMessageBytes(errorFrame);
        }

        return sent;
    }

    private void AddReceivedMessageBytes(SocketMessageFrame frame)
    {
        Interlocked.Add(ref this.totalReceivedMessageBytes, GetFrameWireBytes(frame));
    }

    private void AddSentMessageBytes(SocketMessageFrame frame)
    {
        Interlocked.Add(ref this.totalSentMessageBytes, GetFrameWireBytes(frame));
    }

    private static int GetFrameWireBytes(SocketMessageFrame frame)
    {
        return SocketMessageFrame.HeaderLength + (frame?.Payload?.Length ?? 0);
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

    private void ReleaseUnregisteredConnectionSlot(bool slotAcquired)
    {
        if (slotAcquired)
        {
            Interlocked.Decrement(ref this.activeConnectionSlots);
        }
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

        this.connectedClientsById.AddOrUpdate(
            session.ClientId,
            session,
            (_, existingSession) => existingSession.Id == session.Id || existingSession.IsClosed ? session : existingSession);
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
            Logger.Debug(() => $"Client closed. connectionId={removedSession.Id}, remote={removedSession.RemoteEndPoint}, connectedClients={this.GetConnectedClientCount()}");
            if (removedSession.HasReportedOpened)
            {
                _ = this.NotifySessionClosedAsync(removedSession);
            }
        }

        return true;
    }

    private Task NotifySessionOpenedAsync(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionOpenedAsync;
        if (handler != null)
        {
            return this.RunSessionEventHandlerAsync(handler, session, "opened");
        }

        return Task.CompletedTask;
    }

    private Task NotifySessionClosedAsync(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionClosedAsync;
        if (handler != null)
        {
            return this.RunSessionEventHandlerAsync(handler, session, "closed");
        }

        return Task.CompletedTask;
    }

    private Task NotifySessionUpdatedAsync(ConnectionSession session)
    {
        Func<ConnectionSession, Task> handler = this.SessionUpdatedAsync;
        if (handler != null)
        {
            return this.RunSessionEventHandlerAsync(handler, session, "updated");
        }

        return Task.CompletedTask;
    }

    private async Task RunSessionEventHandlerAsync(Func<ConnectionSession, Task> handler, ConnectionSession session, string eventName)
    {
        foreach (Func<ConnectionSession, Task> eventHandler in handler.GetInvocationList())
        {
            try
            {
                await eventHandler(session);
            }
            catch (Exception exception)
            {
                Logger.Warn($"Session {eventName} event handler failed. connectionId={session.Id}", exception);
            }
        }
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

internal sealed class ClientCommandRequest
{
    public ClientCommandRequest(ConnectionSession session, SocketMessageFrame frame)
    {
        this.Session = session;
        this.Frame = frame;
    }

    public ConnectionSession Session { get; }

    public SocketMessageFrame Frame { get; }

    public TaskCompletionSource<bool> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ClientResponseCommand
{
    public ClientResponseCommand(ConnectionSession session, byte[] payload, string operation)
    {
        this.Session = session;
        this.Payload = payload;
        this.Operation = operation;
    }

    public ConnectionSession Session { get; }

    public byte[] Payload { get; }

    public string Operation { get; }

    public TaskCompletionSource<bool> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ServerRelayCommand
{
    public ServerRelayCommand(string host, int port, string targetInstanceId, ClientMessageSendRequest request)
    {
        this.Host = host;
        this.Port = port;
        this.TargetInstanceId = targetInstanceId;
        this.Request = request;
    }

    public ServerRelayCommand(ConnectionSession session, ServerRelayMessage relayMessage)
    {
        this.Session = session;
        this.RelayMessage = relayMessage;
    }

    public ServerRelayCommand(ConnectionSession session, ServerRelayBatchMessage relayBatchMessage)
    {
        this.Session = session;
        this.RelayBatchMessage = relayBatchMessage;
    }

    public bool IsReceiveCommand => this.RelayMessage != null;

    public bool IsReceiveBatchCommand => this.RelayBatchMessage != null;

    public string Host { get; }

    public int Port { get; }

    public string TargetInstanceId { get; }

    public ClientMessageSendRequest Request { get; }

    public ConnectionSession Session { get; }

    public ServerRelayMessage RelayMessage { get; }

    public ServerRelayBatchMessage RelayBatchMessage { get; }

    public TaskCompletionSource<(bool Success, string TargetInstanceId, string ErrorMessage)> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal sealed class ServerRelayResult
{
    public ServerRelayResult(bool success, string targetInstanceId, string messageToken, string errorMessage)
    {
        this.Success = success;
        this.TargetInstanceId = targetInstanceId;
        this.MessageToken = messageToken;
        this.ErrorMessage = errorMessage;
    }

    public bool Success { get; }

    public string TargetInstanceId { get; }

    public string MessageToken { get; }

    public string ErrorMessage { get; }
}
