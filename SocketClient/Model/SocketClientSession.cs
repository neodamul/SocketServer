using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketClient.Model;

public enum SocketClientSessionFailure
{
    None = 0,
    Connect = 1,
    RegisterUnavailable = 2,
    RegisterRejected = 3
}

public sealed class SocketClientSession : IDisposable
{
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim sendGate = new(1, 1);
    private TcpClient client;
    private CancellationTokenSource receiveCancellation;
    private Task receiveTask;
    private CancellationTokenSource healthCheckCancellation;
    private Task healthCheckTask;
    private CancellationTokenSource reconnectCancellation;
    private Task reconnectTask;
    private ReconnectSettings reconnectSettings;
    private TimeSpan nextReconnectDelay = TimeSpan.FromSeconds(30);
    private TimeSpan receiveHeaderTimeout = TimeSpan.FromSeconds(90);
    private bool registered;
    private bool reconnectEnabled;
    private bool intentionalDisconnect;
    private SocketClientSessionFailure lastFailure;

    public event Action<ClientMessageDelivery> MessageReceived;

    public event Action<ClientMessageAck> MessageAcknowledged;

    public event Action<ClientMessageError> MessageFailed;

    public event Action<HealthCheckMessage> HealthCheckReceived;

    public bool IsConnected
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.client?.IsConnected() ?? false;
            }
        }
    }

    public bool IsRegistered
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.registered;
            }
        }
    }

    public string ConnectedEndpoint
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.client == null
                    ? ""
                    : $"{this.client.GetIpAddress()}:{this.client.GetPort()}";
            }
        }
    }

    public SocketClientSessionFailure LastFailure
    {
        get
        {
            lock (this.syncRoot)
            {
                return this.lastFailure;
            }
        }
    }

    public async Task<bool> ConnectAndRegisterAsync(
        int clientId,
        string clientName,
        string host,
        int port,
        bool useControlServer,
        int healthCheckIntervalSeconds)
    {
        return await this.ConnectAndRegisterAsync(
            clientId,
            clientName,
            host,
            port,
            useControlServer,
            healthCheckIntervalSeconds,
            30,
            90);
    }

    public async Task<bool> ConnectAndRegisterAsync(
        int clientId,
        string clientName,
        IEnumerable<IPEndPoint> controlEndpoints,
        int healthCheckIntervalSeconds,
        int reconnectRetrySeconds,
        int duplicateRejectBackoffSeconds)
    {
        IPEndPoint[] endpoints = NormalizeControlEndpoints(controlEndpoints);
        IPEndPoint firstEndpoint = endpoints.Length > 0
            ? endpoints[0]
            : new IPEndPoint(IPAddress.Loopback, 0);
        return await this.ConnectAndRegisterAsync(
            clientId,
            clientName,
            firstEndpoint.Address.ToString(),
            firstEndpoint.Port,
            true,
            endpoints,
            healthCheckIntervalSeconds,
            reconnectRetrySeconds,
            duplicateRejectBackoffSeconds);
    }

    public async Task<bool> ConnectAndRegisterAsync(
        int clientId,
        string clientName,
        string host,
        int port,
        bool useControlServer,
        int healthCheckIntervalSeconds,
        int reconnectRetrySeconds,
        int duplicateRejectBackoffSeconds)
    {
        return await this.ConnectAndRegisterAsync(
            clientId,
            clientName,
            host,
            port,
            useControlServer,
            Array.Empty<IPEndPoint>(),
            healthCheckIntervalSeconds,
            reconnectRetrySeconds,
            duplicateRejectBackoffSeconds);
    }

    private async Task<bool> ConnectAndRegisterAsync(
        int clientId,
        string clientName,
        string host,
        int port,
        bool useControlServer,
        IPEndPoint[] controlEndpoints,
        int healthCheckIntervalSeconds,
        int reconnectRetrySeconds,
        int duplicateRejectBackoffSeconds)
    {
        this.reconnectSettings = new ReconnectSettings(
            clientId,
            clientName,
            host,
            port,
            useControlServer,
            controlEndpoints,
            Math.Max(1, healthCheckIntervalSeconds),
            Math.Max(1, reconnectRetrySeconds),
            Math.Max(1, duplicateRejectBackoffSeconds));
        this.receiveHeaderTimeout = CalculateReceiveHeaderTimeout(this.reconnectSettings.HealthCheckIntervalSeconds);
        this.nextReconnectDelay = TimeSpan.FromSeconds(this.reconnectSettings.ReconnectRetrySeconds);
        this.intentionalDisconnect = false;
        this.reconnectEnabled = true;
        this.StopReconnectLoop();

        bool success = await this.TryConnectAndRegisterOnceAsync();
        this.StartReconnectLoop();
        return success;
    }

    private async Task<bool> TryConnectAndRegisterOnceAsync()
    {
        ReconnectSettings settings = this.reconnectSettings;
        TcpClient nextClient = new(settings.ClientId, settings.ClientName, settings.Host, settings.Port);
        bool connected = settings.UseControlServer
            ? settings.ControlEndpoints.Length > 0
                ? await nextClient.ConnectViaControlServersAsync(settings.ControlEndpoints)
                : await nextClient.ConnectViaControlServerAsync(settings.Host, settings.Port)
            : await Task.Run(nextClient.Connect);
        if (!connected)
        {
            nextClient.Dispose();
            this.SetLastFailure(SocketClientSessionFailure.Connect);
            this.nextReconnectDelay = TimeSpan.FromSeconds(settings.ReconnectRetrySeconds);
            return false;
        }

        (bool registerReceived, ClientRegisterAck registerAck) = await nextClient.RegisterClientWithAckAsync();
        if (!registerReceived)
        {
            nextClient.Dispose();
            this.SetLastFailure(SocketClientSessionFailure.RegisterUnavailable);
            this.nextReconnectDelay = TimeSpan.FromSeconds(settings.ReconnectRetrySeconds);
            return false;
        }

        if (!registerAck.Success)
        {
            nextClient.Dispose();
            this.SetLastFailure(SocketClientSessionFailure.RegisterRejected);
            int delaySeconds = registerAck?.RetryAfterSeconds > 0
                ? registerAck.RetryAfterSeconds
                : settings.DuplicateRejectBackoffSeconds;
            this.nextReconnectDelay = TimeSpan.FromSeconds(Math.Max(1, delaySeconds));
            return false;
        }

        this.StopBackgroundLoops();
        lock (this.syncRoot)
        {
            this.client?.Dispose();
            this.client = nextClient;
            this.registered = true;
            this.lastFailure = SocketClientSessionFailure.None;
        }

        this.StartReceiveLoop();
        this.StartHealthCheckLoop(settings.HealthCheckIntervalSeconds);
        this.nextReconnectDelay = TimeSpan.FromSeconds(settings.ReconnectRetrySeconds);
        return true;
    }

    public async Task<bool> SendMessageAsync(uint targetClientId, string content, int ttlSeconds = 10)
    {
        TcpClient activeClient = this.GetRequiredClient();
        await this.sendGate.WaitAsync();
        try
        {
            (bool success, _, ClientMessageError error) =
                await activeClient.SendClientMessageRequestAsync(targetClientId, content ?? "", ttlSeconds);
            if (!success && error != null)
            {
                this.MessageFailed?.Invoke(error);
            }

            return success;
        }
        finally
        {
            this.sendGate.Release();
        }
    }

    public void Disconnect()
    {
        this.intentionalDisconnect = true;
        this.reconnectEnabled = false;
        this.StopReconnectLoop();
        this.StopBackgroundLoops();
        lock (this.syncRoot)
        {
            this.client?.Dispose();
            this.client = null;
            this.registered = false;
        }
    }

    public void Dispose()
    {
        this.Disconnect();
        this.sendGate.Dispose();
    }

    private TcpClient GetRequiredClient()
    {
        lock (this.syncRoot)
        {
            if (this.client == null || !this.client.IsConnected())
            {
                throw new InvalidOperationException("Client is not connected.");
            }

            return this.client;
        }
    }

    private void StartReceiveLoop()
    {
        lock (this.syncRoot)
        {
            if (this.receiveTask != null && !this.receiveTask.IsCompleted)
            {
                return;
            }

            this.receiveCancellation?.Dispose();
            this.receiveCancellation = new CancellationTokenSource();
            this.receiveTask = this.RunReceiveLoopAsync(this.receiveCancellation.Token);
        }
    }

    private void StartHealthCheckLoop(int intervalSeconds)
    {
        lock (this.syncRoot)
        {
            if (this.healthCheckTask != null && !this.healthCheckTask.IsCompleted)
            {
                return;
            }

            this.healthCheckCancellation?.Dispose();
            this.healthCheckCancellation = new CancellationTokenSource();
            this.healthCheckTask = this.RunHealthCheckLoopAsync(
                TimeSpan.FromSeconds(intervalSeconds),
                this.healthCheckCancellation.Token);
        }
    }

    private void StopBackgroundLoops()
    {
        lock (this.syncRoot)
        {
            this.receiveCancellation?.Cancel();
            this.receiveCancellation?.Dispose();
            this.receiveCancellation = null;
            this.receiveTask = null;
            this.healthCheckCancellation?.Cancel();
            this.healthCheckCancellation?.Dispose();
            this.healthCheckCancellation = null;
            this.healthCheckTask = null;
        }
    }

    private void StartReconnectLoop()
    {
        lock (this.syncRoot)
        {
            if (!this.reconnectEnabled ||
                this.reconnectTask != null && !this.reconnectTask.IsCompleted)
            {
                return;
            }

            this.reconnectCancellation?.Dispose();
            this.reconnectCancellation = new CancellationTokenSource();
            this.reconnectTask = this.RunReconnectLoopAsync(this.reconnectCancellation.Token);
        }
    }

    private void StopReconnectLoop()
    {
        lock (this.syncRoot)
        {
            this.reconnectCancellation?.Cancel();
            this.reconnectCancellation?.Dispose();
            this.reconnectCancellation = null;
            this.reconnectTask = null;
        }
    }

    private void SetLastFailure(SocketClientSessionFailure failure)
    {
        lock (this.syncRoot)
        {
            this.lastFailure = failure;
        }
    }

    private async Task RunReconnectLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(this.nextReconnectDelay, cancellationToken);
            if (cancellationToken.IsCancellationRequested || !this.reconnectEnabled || this.intentionalDisconnect)
            {
                return;
            }

            if (this.IsConnected && this.IsRegistered)
            {
                continue;
            }

            await this.TryConnectAndRegisterOnceAsync();
        }
    }

    private void HandleUnexpectedDisconnect()
    {
        if (this.intentionalDisconnect)
        {
            return;
        }

        this.StopBackgroundLoops();
        lock (this.syncRoot)
        {
            this.client?.Dispose();
            this.client = null;
            this.registered = false;
        }

        this.nextReconnectDelay = TimeSpan.FromSeconds(this.reconnectSettings.ReconnectRetrySeconds <= 0 ? 30 : this.reconnectSettings.ReconnectRetrySeconds);
        if (this.reconnectEnabled)
        {
            this.StartReconnectLoop();
        }
    }

    private async Task RunReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient activeClient;
            lock (this.syncRoot)
            {
                activeClient = this.client;
            }

            if (activeClient == null || !activeClient.IsConnected())
            {
                break;
            }

            try
            {
                (bool success, SocketMessageFrame frame) = await activeClient.TryReceiveFrameAsync(
                    (int)this.receiveHeaderTimeout.TotalMilliseconds,
                    SocketFactory.ReadTimeoutMilliseconds);
                if (!success || frame == null)
                {
                    break;
                }

                this.HandleFrame(frame);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (IOException)
            {
                break;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            this.HandleUnexpectedDisconnect();
        }
    }

    private async Task RunHealthCheckLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken);
                TcpClient activeClient;
                lock (this.syncRoot)
                {
                    activeClient = this.client;
                }

                if (activeClient == null || !activeClient.IsConnected())
                {
                    break;
                }

                await this.sendGate.WaitAsync(cancellationToken);
                try
                {
                    if (!await activeClient.SendHealthCheckAsync())
                    {
                        break;
                    }
                }
                finally
                {
                    this.sendGate.Release();
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            this.HandleUnexpectedDisconnect();
        }
    }

    private void HandleFrame(SocketMessageFrame frame)
    {
        if (HealthCheckProtocol.TryDecode(frame, out HealthCheckMessage healthCheck))
        {
            this.HealthCheckReceived?.Invoke(healthCheck);
            return;
        }

        if (ClientMessageProtocol.TryDecodeDelivery(frame, out ClientMessageDelivery delivery))
        {
            this.MessageReceived?.Invoke(delivery);
            return;
        }

        if (ClientMessageProtocol.TryDecodeAck(frame, out ClientMessageAck ack))
        {
            this.MessageAcknowledged?.Invoke(ack);
            return;
        }

        if (ClientMessageProtocol.TryDecodeError(frame, out ClientMessageError error))
        {
            this.MessageFailed?.Invoke(error);
        }
    }

    private sealed record ReconnectSettings(
        int ClientId,
        string ClientName,
        string Host,
        int Port,
        bool UseControlServer,
        IPEndPoint[] ControlEndpoints,
        int HealthCheckIntervalSeconds,
        int ReconnectRetrySeconds,
        int DuplicateRejectBackoffSeconds);

    private static IPEndPoint[] NormalizeControlEndpoints(IEnumerable<IPEndPoint> controlEndpoints)
    {
        List<IPEndPoint> endpoints = new();
        if (controlEndpoints != null)
        {
            foreach (IPEndPoint endpoint in controlEndpoints)
            {
                if (endpoint != null)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        return endpoints.ToArray();
    }

    private static TimeSpan CalculateReceiveHeaderTimeout(int healthCheckIntervalSeconds)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(1, healthCheckIntervalSeconds));
        TimeSpan readTimeout = TimeSpan.FromMilliseconds(SocketFactory.ReadTimeoutMilliseconds);
        return interval + readTimeout + TimeSpan.FromSeconds(5);
    }

}
