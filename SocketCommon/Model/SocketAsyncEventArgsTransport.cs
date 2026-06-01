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
        byte[] buffer = new byte[BufferSize];

        try
        {
            while (stream.Length < maxMessageLength)
            {
                int remaining = maxMessageLength - (int)stream.Length;
                int count = Math.Min(buffer.Length, remaining);
                int received = await ReceiveChunkAsync(socket, buffer, 0, count);
                if (received <= 0)
                {
                    return null;
                }

                int newlineIndex = Array.IndexOf(buffer, (byte)'\n', 0, received);
                if (newlineIndex >= 0)
                {
                    stream.Write(buffer, 0, newlineIndex + 1);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }

                stream.Write(buffer, 0, received);
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
        byte[] buffer = new byte[length];
        int offset = 0;

        try
        {
            while (offset < length)
            {
                int count = Math.Min(BufferSize, length - offset);
                int received = await ReceiveChunkAsync(socket, buffer, offset, count);
                if (received <= 0)
                {
                    return null;
                }

                offset += received;
            }

            return buffer;
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

    public static Task<Socket> AcceptAsync(Socket socket)
    {
        SocketAsyncEventArgs args = SocketAsyncEventArgsFactory.Rent();
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

    private static Task<int> ReceiveChunkAsync(Socket socket, byte[] buffer, int offset, int count)
    {
        SocketAsyncEventArgs args = CreateArgs(buffer, offset, count);
        return RunAsync(socket, args, static (s, a) => s.ReceiveAsync(a));
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
}
