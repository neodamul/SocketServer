using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public static class SocketAsyncEventArgsTransport
{
    public const int BufferSize = 8192;

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
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
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
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
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
        catch (SocketException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    public static Task<Socket> AcceptAsync(Socket socket)
    {
        SocketAsyncEventArgs args = new();
        TaskCompletionSource<Socket> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Complete(SocketAsyncEventArgs completedArgs)
        {
            Socket acceptedSocket = completedArgs.SocketError == SocketError.Success
                ? completedArgs.AcceptSocket
                : null;

            completedArgs.Dispose();
            completion.TrySetResult(acceptedSocket);
        }

        args.Completed += (_, completedArgs) => Complete(completedArgs);

        bool pending;
        try
        {
            pending = socket.AcceptAsync(args);
        }
        catch
        {
            args.Dispose();
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
        SocketAsyncEventArgs args = new();
        args.SetBuffer(buffer, offset, count);
        return args;
    }

    private static Task<int> RunAsync(Socket socket, SocketAsyncEventArgs args, Func<Socket, SocketAsyncEventArgs, bool> operation)
    {
        TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        void Complete(SocketAsyncEventArgs completedArgs)
        {
            int bytesTransferred = completedArgs.SocketError == SocketError.Success
                ? completedArgs.BytesTransferred
                : -1;

            completedArgs.Dispose();
            completion.TrySetResult(bytesTransferred);
        }

        args.Completed += (_, completedArgs) => Complete(completedArgs);

        bool pending;
        try
        {
            pending = operation(socket, args);
        }
        catch
        {
            args.Dispose();
            throw;
        }

        if (!pending)
        {
            Complete(args);
        }

        return completion.Task;
    }
}
