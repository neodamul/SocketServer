using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using SocketCommon.Logging;

namespace SocketCommon.Model;

public static class SocketAsyncEventArgsTransport
{
    public const int BufferSize = 8192;
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger(typeof(SocketAsyncEventArgsTransport));

    public static async Task<bool> SendAsync(Socket socket, byte[] bytes)
    {
        int offset = 0;

        try
        {
            while (offset < bytes.Length)
            {
                int count = Math.Min(BufferSize, bytes.Length - offset);
                int sent = await SendChunkAsync(socket, bytes, offset, count);
                if (sent <= 0)
                {
                    return false;
                }

                offset += sent;
            }

            return true;
        }
        catch (SocketException exception)
        {
            Logger.Warn("Socket send failed.", exception);
            return false;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn("Socket send failed because socket is disposed.", exception);
            return false;
        }
    }

    public static async Task<string> ReceiveLineAsync(Socket socket, int maxMessageLength)
    {
        using MemoryStream stream = new();

        try
        {
            while (stream.Length < maxMessageLength)
            {
                int remaining = maxMessageLength - (int)stream.Length;
                int count = Math.Min(BufferSize, remaining);
                using SocketMappedReceiveBuffer receiveBuffer = await ReceiveMappedAsync(socket, count);
                if (receiveBuffer == null || receiveBuffer.Count <= 0)
                {
                    return null;
                }

                ArraySegment<byte> segment = receiveBuffer.Segment;
                int newlineIndex = Array.IndexOf(segment.Array, (byte)'\n', segment.Offset, receiveBuffer.Count);
                if (newlineIndex >= 0)
                {
                    stream.Write(segment.Array, segment.Offset, newlineIndex - segment.Offset + 1);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }

                stream.Write(segment.Array, segment.Offset, receiveBuffer.Count);
            }
        }
        catch (SocketException exception)
        {
            Logger.Warn("Socket line receive failed.", exception);
            return null;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn("Socket line receive failed because socket is disposed.", exception);
            return null;
        }

        return null;
    }

    public static async Task<byte[]> ReceiveExactAsync(Socket socket, int length)
    {
        if (length == 0)
        {
            return Array.Empty<byte>();
        }

        byte[] result = new byte[length];
        int offset = 0;

        try
        {
            while (offset < length)
            {
                int count = Math.Min(BufferSize, length - offset);
                using SocketMappedReceiveBuffer receiveBuffer = await ReceiveMappedAsync(socket, count);
                if (receiveBuffer == null || receiveBuffer.Count <= 0)
                {
                    return null;
                }

                ArraySegment<byte> segment = receiveBuffer.Segment;
                Buffer.BlockCopy(segment.Array, segment.Offset, result, offset, receiveBuffer.Count);
                offset += receiveBuffer.Count;
            }

            return result;
        }
        catch (SocketException exception)
        {
            Logger.Warn("Socket receive failed.", exception);
            return null;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn("Socket receive failed because socket is disposed.", exception);
            return null;
        }
    }

    public static async Task<SocketMappedReceiveBuffer> ReceiveMappedAsync(Socket socket, int maxLength)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Receive length must be greater than zero.");
        }

        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
        SocketBufferLease lease = SocketAsyncEventArgsFactory.GetBufferLease(args);
        int count = Math.Min(maxLength, lease.Length);
        args.SetBuffer(lease.Buffer, lease.Offset, count);

        try
        {
            int received = await RunAsyncWithoutReturn(socket, args, static (s, a) => s.ReceiveAsync(a));
            if (received <= 0)
            {
                SocketAsyncEventArgsFactory.Return(args);
                return null;
            }

            return new SocketMappedReceiveBuffer(args, new ArraySegment<byte>(lease.Buffer, lease.Offset, received));
        }
        catch
        {
            SocketAsyncEventArgsFactory.Return(args);
            throw;
        }
    }

    public static Task<Socket> AcceptAsync(Socket socket)
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
        args.SetBuffer(null, 0, 0);
        TaskCompletionSource<Socket> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SocketAsyncEventArgs> handler = null;

        void Complete(SocketAsyncEventArgs completedArgs)
        {
            Socket acceptedSocket = completedArgs.SocketError == SocketError.Success
                ? completedArgs.AcceptSocket
                : null;

            if (acceptedSocket != null)
            {
                SocketFactory.ConfigureTcpSocket(acceptedSocket);
            }

            completedArgs.Completed -= handler;
            SocketAsyncEventArgsFactory.Return(completedArgs);
            completion.TrySetResult(acceptedSocket);
        }

        handler = (_, completedArgs) => Complete(completedArgs);
        args.Completed += handler;

        bool pending;
        try
        {
            pending = socket.AcceptAsync(args);
        }
        catch
        {
            args.Completed -= handler;
            SocketAsyncEventArgsFactory.Return(args);
            throw;
        }

        if (!pending)
        {
            Complete(args);
        }

        return completion.Task;
    }

    private static Task<int> SendChunkAsync(Socket socket, byte[] buffer, int offset, int count)
    {
        SocketAsyncEventArgs args = CreateArgs(buffer, offset, count);
        return RunAsync(socket, args, static (s, a) => s.SendAsync(a));
    }

    private static SocketAsyncEventArgs CreateArgs(byte[] buffer, int offset, int count)
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
        args.SetBuffer(buffer, offset, count);
        return args;
    }

    private static Task<int> RunAsync(Socket socket, SocketAsyncEventArgs args, Func<Socket, SocketAsyncEventArgs, bool> operation)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SocketAsyncEventArgs> handler = null;

        void Complete(SocketAsyncEventArgs completedArgs)
        {
            int bytesTransferred = completedArgs.SocketError == SocketError.Success
                ? completedArgs.BytesTransferred
                : -1;

            completedArgs.Completed -= handler;
            SocketAsyncEventArgsFactory.Return(completedArgs);
            completion.TrySetResult(bytesTransferred);
        }

        handler = (_, completedArgs) => Complete(completedArgs);
        args.Completed += handler;

        bool pending;
        try
        {
            pending = operation(socket, args);
        }
        catch
        {
            args.Completed -= handler;
            SocketAsyncEventArgsFactory.Return(args);
            throw;
        }

        if (!pending)
        {
            Complete(args);
        }

        return completion.Task;
    }

    private static Task<int> RunAsyncWithoutReturn(Socket socket, SocketAsyncEventArgs args, Func<Socket, SocketAsyncEventArgs, bool> operation)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<SocketAsyncEventArgs> handler = null;

        void Complete(SocketAsyncEventArgs completedArgs)
        {
            int bytesTransferred = completedArgs.SocketError == SocketError.Success
                ? completedArgs.BytesTransferred
                : -1;

            completedArgs.Completed -= handler;
            completion.TrySetResult(bytesTransferred);
        }

        handler = (_, completedArgs) => Complete(completedArgs);
        args.Completed += handler;

        bool pending;
        try
        {
            pending = operation(socket, args);
        }
        catch
        {
            args.Completed -= handler;
            throw;
        }

        if (!pending)
        {
            Complete(args);
        }

        return completion.Task;
    }
}

public sealed class SocketMappedReceiveBuffer : IDisposable
{
    private SocketAsyncEventArgs args;

    internal SocketMappedReceiveBuffer(SocketAsyncEventArgs args, ArraySegment<byte> segment)
    {
        this.args = args;
        Segment = segment;
        Count = segment.Count;
    }

    public ArraySegment<byte> Segment { get; }

    public int Count { get; }

    public Memory<byte> Memory => Segment.Array.AsMemory(Segment.Offset, Count);

    public ReadOnlyMemory<byte> ReadOnlyMemory => Segment.Array.AsMemory(Segment.Offset, Count);

    public void Dispose()
    {
        if (this.args == null)
        {
            return;
        }

        SocketAsyncEventArgsFactory.Return(this.args);
        this.args = null;
    }
}
