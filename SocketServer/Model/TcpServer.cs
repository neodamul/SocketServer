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
            CloseClient(client);
        }
    }

    private Task<bool> HandleClientMessageAsync(Socket client, SocketMessageFrame frame)
    {
        if (HealthCheckProtocol.TryDecode(frame, out HealthCheckMessage healthCheckMessage))
        {
            return healthCheckMessage.Type == HealthCheckMessageType.Ping
                ? HealthCheckProtocol.SendAsync(client, HealthCheckProtocol.CreatePong(healthCheckMessage.ClientId))
                : Task.FromResult(true);
        }

        if (HelloWorldProtocol.TryDecodeRequest(frame, out HelloWorldRequest helloWorldRequest))
        {
            return HelloWorldProtocol.SendAsync(client, HelloWorldProtocol.CreateResponse(helloWorldRequest.ClientId));
        }

        return Task.FromResult(false);
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
