using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon;
using SocketCommon.Interface;
using SocketCommon.Model;

namespace SocketServer.Model;
public class TcpServer : SocketClient.Model.TcpClient, IServer, IClient, IDisposable
{
    private readonly object clientLock = new();
    private readonly HashSet<Socket> connectedClients = new();
    private CancellationTokenSource acceptLoopCancellation;
    private Task acceptLoopTask;
    private bool isListening;
    private DateTimeOffset? startedAt;
    private long totalAcceptedClients;
    private long totalClosedClients;
    private long totalReceivedMessages;
    private long totalSentMessages;
    private bool disposedValue;

    public TcpServer() : base(0, null)
    { }

    public TcpServer(int id, string name)
        : base(id, name, null, Constants.LocalHostPort)
    { }

    public TcpServer(int id, string name, string ipAddress, int port)
        : base(id, name, ipAddress, port)
    { }

    public bool Start()
    {
        return this.Bind() && this.Listen();
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

            return true;
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
            return true;
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

    public bool End()
    {
        this.acceptLoopCancellation?.Cancel();
        this.CloseConnectedClients();
        this.Disconnect();
        this.isListening = false;
        this.acceptLoopCancellation?.Dispose();
        this.acceptLoopCancellation = null;
        this.acceptLoopTask = null;
        return true;
    }

    public bool StartClientAcceptLoop()
    {
        if (this.Socket == null || !this.Socket.IsBound)
        {
            return false;
        }

        if (this.acceptLoopTask != null && !this.acceptLoopTask.IsCompleted)
        {
            return true;
        }

        this.acceptLoopCancellation?.Dispose();
        this.acceptLoopCancellation = new CancellationTokenSource();
        this.acceptLoopTask = this.RunClientAcceptLoopAsync(this.acceptLoopCancellation.Token);
        return true;
    }

    public int GetConnectedClientCount()
    {
        lock (this.clientLock)
        {
            return this.connectedClients.Count;
        }
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
            TotalAcceptedClients = Interlocked.Read(ref this.totalAcceptedClients),
            TotalClosedClients = Interlocked.Read(ref this.totalClosedClients),
            TotalReceivedMessages = Interlocked.Read(ref this.totalReceivedMessages),
            TotalSentMessages = Interlocked.Read(ref this.totalSentMessages),
            ListenBacklog = SocketFactory.ListenBacklog,
            NoDelay = SocketFactory.NoDelay,
            MaxPayloadLength = SocketMessageFrame.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = SocketAsyncEventArgsFactory.AvailableCount,
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

                this.AddConnectedClient(client);
                Interlocked.Increment(ref this.totalAcceptedClients);
                _ = this.HandleClientAsync(client, cancellationToken);
            }
            catch (SocketException)
            {
                CloseClient(client);
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
            catch (ObjectDisposedException)
            {
                CloseClient(client);
                break;
            }
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                (bool success, SocketMessageFrame frame) = await SocketMessageFrame.TryReceiveAsync(client);
                if (!success)
                {
                    break;
                }

                Interlocked.Increment(ref this.totalReceivedMessages);
                bool handled = await this.HandleClientMessageAsync(client, frame);
                if (!handled)
                {
                    break;
                }
            }
        }
        finally
        {
            this.RemoveConnectedClient(client);
            Interlocked.Increment(ref this.totalClosedClients);
            CloseClient(client);
        }
    }

    private async Task<bool> HandleClientMessageAsync(Socket client, SocketMessageFrame frame)
    {
        bool sent;
        if (HealthCheckProtocol.TryDecode(frame, out HealthCheckMessage healthCheckMessage))
        {
            if (healthCheckMessage.Type != HealthCheckMessageType.Ping)
            {
                return true;
            }

            sent = await HealthCheckProtocol.SendAsync(client, HealthCheckProtocol.CreatePong(healthCheckMessage.ClientId));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        if (HelloWorldProtocol.TryDecodeRequest(frame, out HelloWorldRequest helloWorldRequest))
        {
            sent = await HelloWorldProtocol.SendAsync(client, HelloWorldProtocol.CreateResponse(helloWorldRequest.ClientId));
            if (sent)
            {
                Interlocked.Increment(ref this.totalSentMessages);
            }

            return sent;
        }

        return false;
    }

    private void AddConnectedClient(Socket client)
    {
        lock (this.clientLock)
        {
            this.connectedClients.Add(client);
        }
    }

    private void RemoveConnectedClient(Socket client)
    {
        lock (this.clientLock)
        {
            this.connectedClients.Remove(client);
        }
    }

    private void CloseConnectedClients()
    {
        Socket[] clients;
        lock (this.clientLock)
        {
            clients = new Socket[this.connectedClients.Count];
            this.connectedClients.CopyTo(clients);
            this.connectedClients.Clear();
        }

        foreach (Socket client in clients)
        {
            CloseClient(client);
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
