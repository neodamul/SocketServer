using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon;
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
    private readonly int maxConnections;
    private readonly int pendingAcceptCount;
    private readonly TimeSpan idleTimeout;
    private readonly TimeSpan idleScanInterval;
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
    }

    public TcpServer(int id, string name, string ipAddress, int port)
        : base(id, name, ipAddress, port)
    {
        this.maxConnections = DefaultMaxConnections;
        this.pendingAcceptCount = DefaultPendingAcceptCount;
        this.idleTimeout = TimeSpan.FromSeconds(DefaultIdleTimeoutSeconds);
        this.idleScanInterval = TimeSpan.FromSeconds(DefaultIdleScanIntervalSeconds);
    }

    public TcpServer(
        int id,
        string name,
        string ipAddress,
        int port,
        int maxConnections,
        int pendingAcceptCount,
        TimeSpan idleTimeout,
        TimeSpan? idleScanInterval = null)
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
        this.acceptLoopCancellation?.Cancel();
        this.CloseConnectedClients();
        this.Disconnect();
        this.isListening = false;
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
            IsSocketInitialized = this.Socket != null,
            IsBound = this.Socket?.IsBound ?? false,
            IsListening = this.isListening,
            IsAcceptLoopRunning = this.acceptLoopTask != null && !this.acceptLoopTask.IsCompleted,
            IpAddress = this.GetIpAddress(),
            Port = this.GetPort(),
            ConnectedClientCount = this.GetConnectedClientCount(),
            MaxConnections = this.maxConnections,
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
            using Socket client = await SocketAsyncEventArgsTransport.AcceptAsync(this.Socket);
            if (client == null)
            {
                return false;
            }

            (bool success, HelloWorldRequest request) = await HelloWorldProtocol.TryReceiveRequestAsync(client);
            if (!success)
            {
                return false;
            }

            return await HelloWorldProtocol.SendAsync(client, HelloWorldProtocol.CreateResponse(request.ClientId));
        }
        catch (SocketException)
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

        tasks[^1] = this.RunIdleTimeoutLoopAsync(cancellationToken);
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

                long connectionId = Interlocked.Increment(ref this.nextConnectionId);
                ConnectionSession session = new(connectionId, client);
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
            catch (ObjectDisposedException)
            {
                CloseClient(client);
                break;
            }
        }
    }

    private async Task RunIdleTimeoutLoopAsync(CancellationToken cancellationToken)
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

            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach (ConnectionSession session in this.connectedClients.Values)
            {
                if (now - session.LastReceivedAt <= this.idleTimeout)
                {
                    continue;
                }

                if (this.RemoveConnectedClient(session))
                {
                    Interlocked.Increment(ref this.totalIdleTimeoutClients);
                    Logger.Debug($"Client closed by idle timeout. connectionId={session.Id}, remote={session.RemoteEndPoint}");
                }
            }
        }
    }

    private async Task HandleClientAsync(ConnectionSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(session.Socket);
                if (!success)
                {
                    break;
                }

                session.MarkReceived();
                Interlocked.Increment(ref this.totalReceivedMessages);
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

            sent = await HealthCheckProtocol.SendAsync(session.Socket, HealthCheckProtocol.CreatePong(healthCheckMessage.ClientId));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        if (HelloWorldProtocol.TryDecodeRequest(frame, out HelloWorldRequest helloWorldRequest))
        {
            sent = await HelloWorldProtocol.SendAsync(session.Socket, HelloWorldProtocol.CreateResponse(helloWorldRequest.ClientId));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        return false;
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

    private bool RemoveConnectedClient(ConnectionSession session)
    {
        if (!this.connectedClients.TryRemove(session.Id, out ConnectionSession removedSession))
        {
            return false;
        }

        if (removedSession.Close())
        {
            Interlocked.Decrement(ref this.activeConnectionSlots);
            Interlocked.Increment(ref this.totalClosedClients);
            Logger.Debug($"Client closed. connectionId={removedSession.Id}, remote={removedSession.RemoteEndPoint}, connectedClients={this.GetConnectedClientCount()}");
        }

        return true;
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
