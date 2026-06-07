using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public sealed class PersistentSecureChannelPool : IDisposable
{
    private readonly PersistentSecureChannel[] channels;
    private int nextIndex;
    private bool disposed;

    public PersistentSecureChannelPool(string host, int port, string moduleName, int channelCount)
    {
        int normalizedChannelCount = Math.Max(1, channelCount);
        this.channels = new PersistentSecureChannel[normalizedChannelCount];
        for (int index = 0; index < this.channels.Length; index++)
        {
            this.channels[index] = new PersistentSecureChannel(host, port, moduleName);
        }
    }

    public int ChannelCount => this.channels.Length;

    public Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveAsync(
        Func<SecureSocketConnection, Task<(bool Success, SocketMessageFrame Frame)>> operation,
        int timeoutMilliseconds = 0)
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(PersistentSecureChannelPool));
        }

        PersistentSecureChannel channel = this.GetNextChannel();
        return channel.SendAndReceiveAsync(operation, timeoutMilliseconds);
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        foreach (PersistentSecureChannel channel in this.channels)
        {
            channel.Dispose();
        }
    }

    private PersistentSecureChannel GetNextChannel()
    {
        int index = Interlocked.Increment(ref this.nextIndex);
        return this.channels[(index & int.MaxValue) % this.channels.Length];
    }
}
