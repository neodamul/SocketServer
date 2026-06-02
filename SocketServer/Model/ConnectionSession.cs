using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketServer.Model;

public class ConnectionSession
{
    private int closed;
    private readonly SemaphoreSlim sendLock = new(1, 1);

    public ConnectionSession(long id, Socket socket)
    {
        this.Id = id;
        this.Socket = socket;
        this.RemoteEndPoint = socket.RemoteEndPoint?.ToString() ?? "";
        this.ConnectedAt = DateTimeOffset.UtcNow;
        this.LastReceivedAt = this.ConnectedAt;
    }

    public long Id { get; }

    public Socket Socket { get; }

    public string RemoteEndPoint { get; }

    public DateTimeOffset ConnectedAt { get; }

    public DateTimeOffset LastReceivedAt { get; private set; }

    public uint ClientId { get; private set; }

    public Task HandlerTask { get; set; }

    public bool IsClosed => this.closed != 0;

    public async Task<bool> SendAsync(SocketMessageFrame frame)
    {
        return await this.SendAsync(frame.Encode());
    }

    public async Task<bool> SendAsync(byte[] bytes)
    {
        await this.sendLock.WaitAsync();
        try
        {
            if (this.IsClosed)
            {
                return false;
            }

            return await SocketCommon.Model.SocketAsyncEventArgsTransport.SendAsync(this.Socket, bytes);
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public void MarkReceived(uint clientId = 0)
    {
        if (clientId > 0)
        {
            this.ClientId = clientId;
        }

        this.LastReceivedAt = DateTimeOffset.UtcNow;
    }

    public bool Close()
    {
        if (Interlocked.Exchange(ref this.closed, 1) != 0)
        {
            return false;
        }

        try
        {
            if (this.Socket.Connected)
            {
                this.Socket.Shutdown(SocketShutdown.Both);
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
            this.Socket.Dispose();
        }

        return true;
    }
}
