using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Configuration;

namespace SocketCommon.Model;

public sealed class PersistentSecureChannel : IDisposable
{
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly string host;
    private readonly int port;
    private readonly string moduleName;
    private Socket socket;
    private SecureSocketConnection connection;
    private bool disposed;

    public PersistentSecureChannel(EndpointConfig endpoint, string moduleName)
        : this(endpoint?.Host, endpoint?.Port ?? 0, moduleName)
    {
    }

    public PersistentSecureChannel(string host, int port, string moduleName)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Host is required.", nameof(host));
        }

        if (port <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (string.IsNullOrWhiteSpace(moduleName))
        {
            throw new ArgumentException("Module name is required.", nameof(moduleName));
        }

        this.host = host;
        this.port = port;
        this.moduleName = moduleName;
    }

    public string Host => this.host;

    public int Port => this.port;

    public string EndpointKey => CreateEndpointKey(this.host, this.port);

    public bool IsConnected => this.connection != null && this.connection.IsConnected;

    public static string CreateEndpointKey(string host, int port)
    {
        return $"{host}:{port}";
    }

    public async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveAsync(
        Func<SecureSocketConnection, Task<(bool Success, SocketMessageFrame Frame)>> operation,
        int timeoutMilliseconds = 0)
    {
        if (operation == null)
        {
            throw new ArgumentNullException(nameof(operation));
        }

        int normalizedTimeoutMilliseconds = NormalizeTimeout(timeoutMilliseconds);
        await this.sendLock.WaitAsync();
        try
        {
            Task<(bool Success, SocketMessageFrame Frame)> operationTask = this.SendAndReceiveCoreAsync(operation);
            Task completedTask = await Task.WhenAny(
                operationTask,
                Task.Delay(normalizedTimeoutMilliseconds));
            if (completedTask != operationTask)
            {
                this.Close();
                return (false, null);
            }

            (bool success, SocketMessageFrame frame) = await operationTask;
            if (!success)
            {
                this.Close();
            }

            return (success, frame);
        }
        catch
        {
            this.Close();
            throw;
        }
        finally
        {
            this.sendLock.Release();
        }
    }

    public void Close()
    {
        try
        {
            this.connection?.Dispose();
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            this.connection = null;
            this.socket?.Dispose();
            this.socket = null;
        }
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.Close();
        this.sendLock.Dispose();
    }

    private async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveCoreAsync(
        Func<SecureSocketConnection, Task<(bool Success, SocketMessageFrame Frame)>> operation)
    {
        await this.EnsureConnectedAsync();
        return await operation(this.connection);
    }

    private async Task EnsureConnectedAsync()
    {
        if (this.disposed)
        {
            throw new ObjectDisposedException(nameof(PersistentSecureChannel));
        }

        if (this.connection != null && this.connection.IsConnected)
        {
            return;
        }

        this.Close();
        this.socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        await SocketFactory.ConnectAsync(this.socket, IPAddress.Parse(this.host), this.port);
        this.connection = await SecureSocketConnection.AuthenticateClientAsync(this.socket, this.moduleName);
    }

    private static int NormalizeTimeout(int timeoutMilliseconds)
    {
        return timeoutMilliseconds <= 0
            ? SocketFactory.ReadTimeoutMilliseconds
            : timeoutMilliseconds;
    }
}
