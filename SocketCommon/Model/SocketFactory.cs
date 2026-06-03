using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Logging;

namespace SocketCommon.Model;

public static class SocketFactory
{
    public const int ListenBacklog = 100;
    public const bool NoDelay = true;
    public const int DefaultOperationTimeoutSeconds = 30;
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger(typeof(SocketFactory));
    private static readonly object OptionsLock = new();
    private static int connectTimeoutMilliseconds = DefaultOperationTimeoutSeconds * 1000;
    private static int readTimeoutMilliseconds = DefaultOperationTimeoutSeconds * 1000;
    private static int writeTimeoutMilliseconds = DefaultOperationTimeoutSeconds * 1000;

    public static int ConnectTimeoutMilliseconds
    {
        get
        {
            lock (OptionsLock)
            {
                return connectTimeoutMilliseconds;
            }
        }
    }

    public static int ReadTimeoutMilliseconds
    {
        get
        {
            lock (OptionsLock)
            {
                return readTimeoutMilliseconds;
            }
        }
    }

    public static int WriteTimeoutMilliseconds
    {
        get
        {
            lock (OptionsLock)
            {
                return writeTimeoutMilliseconds;
            }
        }
    }

    public static void Configure(SocketOperationConfig config)
    {
        config ??= new SocketOperationConfig();
        lock (OptionsLock)
        {
            connectTimeoutMilliseconds = NormalizeSeconds(config.ConnectTimeoutSeconds) * 1000;
            readTimeoutMilliseconds = NormalizeSeconds(config.ReadTimeoutSeconds) * 1000;
            writeTimeoutMilliseconds = NormalizeSeconds(config.WriteTimeoutSeconds) * 1000;
        }
    }

    public static Socket CreateTcpSocket(AddressFamily family = AddressFamily.InterNetwork)
    {
        Socket socket = new(family, SocketType.Stream, ProtocolType.Tcp);
        ConfigureTcpSocket(socket);
        return socket;
    }

    public static void ConfigureTcpSocket(Socket socket)
    {
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        socket.NoDelay = NoDelay;
    }

    public static Task ConnectAsync(Socket socket, IPAddress address, int port)
    {
        return ConnectAsync(socket, new IPEndPoint(address, port));
    }

    public static async Task ConnectAsync(Socket socket, EndPoint endpoint)
    {
        Task connectTask = socket.ConnectAsync(endpoint);
        Task completedTask = await Task.WhenAny(connectTask, Task.Delay(ConnectTimeoutMilliseconds));
        if (completedTask == connectTask)
        {
            await connectTask;
            return;
        }

        Logger.Warn($"Socket connect timed out. endpoint={endpoint}, timeoutMs={ConnectTimeoutMilliseconds}");
        socket.Dispose();
        throw new TimeoutException($"Socket connect timed out after {ConnectTimeoutMilliseconds}ms.");
    }

    public static async Task<bool> WaitForReadWriteAsync(Task operationTask, Socket socket, int timeoutMilliseconds, string operationName)
    {
        Task completedTask = await Task.WhenAny(operationTask, Task.Delay(timeoutMilliseconds));
        if (completedTask == operationTask)
        {
            await operationTask;
            return true;
        }

        Logger.Warn($"Socket {operationName} timed out. timeoutMs={timeoutMilliseconds}");
        socket?.Dispose();
        return false;
    }

    public static async Task<int> WaitForReadWriteAsync(Task<int> operationTask, Socket socket, int timeoutMilliseconds, string operationName)
    {
        Task completedTask = await Task.WhenAny(operationTask, Task.Delay(timeoutMilliseconds));
        if (completedTask == operationTask)
        {
            return await operationTask;
        }

        Logger.Warn($"Socket {operationName} timed out. timeoutMs={timeoutMilliseconds}");
        socket?.Dispose();
        try
        {
            return await operationTask;
        }
        catch
        {
            return -1;
        }
    }

    private static int NormalizeSeconds(int seconds)
    {
        return seconds <= 0 ? DefaultOperationTimeoutSeconds : seconds;
    }
}
