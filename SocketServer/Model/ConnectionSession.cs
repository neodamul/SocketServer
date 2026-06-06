using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketServer.Model;

public class ConnectionSession
{
    private int closed;
    private readonly object sendQueueLock = new();
    private Task sendQueueTail = Task.CompletedTask;

    public ConnectionSession(long id, SecureSocketConnection connection)
    {
        this.Id = id;
        this.Connection = connection;
        this.Socket = connection.Socket;
        this.RemoteEndPoint = connection.Socket.RemoteEndPoint?.ToString() ?? "";
        this.ConnectedAt = DateTimeOffset.UtcNow;
        this.LastReceivedAt = this.ConnectedAt;
    }

    public long Id { get; }

    public Socket Socket { get; }

    public SecureSocketConnection Connection { get; }

    public string RemoteEndPoint { get; }

    public DateTimeOffset ConnectedAt { get; }

    public DateTimeOffset LastReceivedAt { get; private set; }

    public uint ClientId { get; private set; }

    public Task HandlerTask { get; set; }

    public bool HasReportedOpened { get; private set; }

    public bool IsClosed => this.closed != 0;

    public async Task<bool> SendAsync(SocketMessageFrame frame)
    {
        return await this.SendAsync(frame.Encode());
    }

    public async Task<bool> SendAsync(byte[] bytes)
    {
        if (bytes == null)
        {
            throw new ArgumentNullException(nameof(bytes));
        }

        Task<bool> sendTask;
        lock (this.sendQueueLock)
        {
            sendTask = this.SendAfterAsync(this.sendQueueTail, bytes);
            this.sendQueueTail = sendTask;
        }

        return await sendTask.ConfigureAwait(false);
    }

    private async Task<bool> SendAfterAsync(Task previousSendTask, byte[] bytes)
    {
        try
        {
            await previousSendTask.ConfigureAwait(false);
        }
        catch
        {
        }

        if (this.IsClosed)
        {
            return false;
        }

        try
        {
            return await this.Connection.SendAsync(bytes).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
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

    public void MarkReportedOpened()
    {
        this.HasReportedOpened = true;
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
            this.Connection.Dispose();
        }

        return true;
    }
}
