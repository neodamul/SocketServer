using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SocketCommon.Configuration;
using SocketCommon.Diagnostics;
using SocketCommon.Logging;
using SocketCommon.Model;

namespace SocketServer.Model;

public class ControlServerReporter : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<ControlServerReporter>();
    private static readonly TimeSpan MetadataRegisterInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RelayRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BroadcastCompletionGraceInterval = TimeSpan.FromSeconds(1);

    private readonly TcpServer server;
    private readonly IReadOnlyCollection<EndpointConfig> controlServers;
    private readonly string clusterId;
    private readonly int portRangeStart;
    private readonly int portRangeEnd;
    private readonly TimeSpan reportTimeout;
    private readonly ResourceUsageProvider resourceUsageProvider = new();
    private readonly IReadOnlyCollection<ControlEndpointConnection> connections;
    private readonly object relayRefreshLock = new();
    private readonly Channel<ControlReportMessage> reportChannel = Channel.CreateBounded<ControlReportMessage>(
        new BoundedChannelOptions(10000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource reportCancellation = new();
    private readonly Task reportWorkerTask;
    private CancellationTokenSource cancellation;
    private Task heartbeatTask;
    private DateTimeOffset lastRegisterSentAt = DateTimeOffset.MinValue;
    private DateTimeOffset lastRelayRefreshStartedAt = DateTimeOffset.MinValue;
    private bool disposedValue;

    public ControlServerReporter(
        TcpServer server,
        IReadOnlyCollection<EndpointConfig> controlServers,
        string clusterId,
        int portRangeStart,
        int portRangeEnd,
        TimeSpan? reportTimeout = null)
    {
        this.server = server;
        this.controlServers = controlServers;
        this.clusterId = string.IsNullOrWhiteSpace(clusterId) ? "socket-cluster-1" : clusterId;
        this.portRangeStart = portRangeStart;
        this.portRangeEnd = portRangeEnd;
        this.reportTimeout = NormalizeReportTimeout(reportTimeout);
        this.connections = CreateConnections(controlServers, this.reportTimeout);
        this.server.ConfigureControlRouting(this.controlServers, this.clusterId);
        this.server.SessionOpenedAsync += this.SendSessionOpenedAsync;
        this.server.SessionUpdatedAsync += this.SendSessionUpdatedAsync;
        this.server.SessionClosedAsync += this.SendSessionClosedAsync;
        this.reportWorkerTask = this.RunReportWorkerAsync(this.reportCancellation.Token);
    }

    public async Task RegisterAsync()
    {
        TcpServerStatus status = this.server.GetStatus();
        ServerRegisterRequest request = new()
        {
            ClusterId = this.clusterId,
            ServerId = status.ServerId,
            InstanceId = status.InstanceId,
            Name = status.InstanceId,
            Host = status.IpAddress,
            Port = status.Port,
            PortRangeStart = this.portRangeStart,
            PortRangeEnd = this.portRangeEnd,
            MaxConnections = status.MaxConnections,
            PendingAcceptCount = status.PendingAcceptCount,
            IdleTimeoutSeconds = status.IdleTimeoutSeconds,
            StartedAt = status.StartedAt ?? DateTimeOffset.UtcNow
        };

        int successCount = await BroadcastAsync(0, ControlMessageIds.ServerRegister, request);
        if (successCount > 0)
        {
            this.lastRegisterSentAt = DateTimeOffset.UtcNow;
            Logger.Info($"SocketServer metadata report completed. instanceId={request.InstanceId}, endpoint={request.Host}:{request.Port}, successEndpoints={successCount}, controlEndpoints={this.connections.Count}");
            if (successCount == this.connections.Count)
            {
                await this.RefreshRelayServersAsync(force: true);
            }
            else
            {
                this.QueueRelayRefresh(force: true);
            }

            return;
        }

        Logger.Warn($"SocketServer metadata report completed without successful ControlServer endpoint. instanceId={request.InstanceId}, endpoint={request.Host}:{request.Port}, controlEndpoints={this.connections.Count}");
    }

    public void StartHeartbeatLoop(TimeSpan interval)
    {
        if (this.heartbeatTask != null && !this.heartbeatTask.IsCompleted)
        {
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        TimeSpan normalizedInterval = NormalizeHeartbeatInterval(interval);
        this.heartbeatTask = this.RunHeartbeatLoopAsync(normalizedInterval, this.cancellation.Token);
        Logger.Info($"SocketServer heartbeat loop started. instanceId={this.server.InstanceId}, intervalMs={normalizedInterval.TotalMilliseconds}, controlEndpoints={this.connections.Count}");
    }

    public void Stop()
    {
        this.cancellation?.Cancel();
        this.cancellation?.Dispose();
        this.cancellation = null;
        this.heartbeatTask = null;
        foreach (ControlEndpointConnection connection in this.connections)
        {
            connection.Close();
        }
    }

    private async Task RunHeartbeatLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SendHeartbeatAsync();
                await SendRegisterIfDueAsync();
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Logger.Warn($"SocketServer heartbeat loop operation was canceled by a nested operation. instanceId={this.server.InstanceId}");
            }
            catch (Exception exception)
            {
                Logger.Warn($"SocketServer heartbeat loop iteration failed. instanceId={this.server.InstanceId}", exception);
            }

            try
            {
                await Task.Delay(interval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendRegisterIfDueAsync()
    {
        if (DateTimeOffset.UtcNow - this.lastRegisterSentAt < MetadataRegisterInterval)
        {
            return;
        }

        await RegisterAsync();
    }

    private async Task SendHeartbeatAsync()
    {
        TcpServerStatus status = this.server.GetStatus();
        ServerHeartbeatRequest heartbeat = new()
        {
            ClusterId = this.clusterId,
            ServerId = status.ServerId,
            InstanceId = status.InstanceId,
            Host = status.IpAddress,
            Port = status.Port,
            Health = ServerHealthState.Healthy,
            MaxConnections = status.MaxConnections,
            CurrentConnections = status.ConnectedClientCount,
            ReservedConnections = 0,
            AvailableConnections = status.AvailableConnections,
            ResourceUsage = this.resourceUsageProvider.Capture(),
            TotalAcceptedClients = status.TotalAcceptedClients,
            TotalClosedClients = status.TotalClosedClients,
            TotalRejectedClients = status.TotalRejectedClients,
            TotalIdleTimeoutClients = status.TotalIdleTimeoutClients,
            SentAt = DateTimeOffset.UtcNow
        };

        int successCount = await BroadcastAsync(0, ControlMessageIds.ServerHeartbeat, heartbeat);
        if (successCount > 0)
        {
            this.QueueRelayRefresh(force: false);
        }
    }

    private void QueueRelayRefresh(bool force)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (this.relayRefreshLock)
        {
            if (!force && now - this.lastRelayRefreshStartedAt < RelayRefreshInterval)
            {
                return;
            }

            this.lastRelayRefreshStartedAt = now;
        }

        _ = this.server.RefreshRelayServersFromControlServersAsync();
    }

    private async Task RefreshRelayServersAsync(bool force)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (this.relayRefreshLock)
        {
            if (!force && now - this.lastRelayRefreshStartedAt < RelayRefreshInterval)
            {
                return;
            }

            this.lastRelayRefreshStartedAt = now;
        }

        await this.server.RefreshRelayServersFromControlServersAsync();
    }

    private Task SendSessionOpenedAsync(ConnectionSession session)
    {
        return SendSessionEventAsync(session, ControlMessageIds.SessionOpened, "Opened");
    }

    private Task SendSessionClosedAsync(ConnectionSession session)
    {
        return SendSessionEventAsync(session, ControlMessageIds.SessionClosed, "Closed");
    }

    private Task SendSessionUpdatedAsync(ConnectionSession session)
    {
        return SendSessionEventAsync(session, ControlMessageIds.SessionUpdated, "Updated");
    }

    private async Task SendSessionEventAsync(ConnectionSession session, uint messageId, string state)
    {
        SessionEventMessage message = new()
        {
            ClusterId = this.clusterId,
            SessionId = session.Id,
            ClientId = session.ClientId,
            ServerId = this.server.ServerId,
            InstanceId = this.server.InstanceId,
            RemoteEndPoint = session.RemoteEndPoint,
            ConnectedAt = session.ConnectedAt,
            LastReceivedAt = session.LastReceivedAt,
            State = state,
            Version = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        await this.reportChannel.Writer.WriteAsync(new ControlReportMessage(messageId, message), this.reportCancellation.Token);
    }

    private async Task RunReportWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ControlReportMessage report in this.reportChannel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await BroadcastAsync(0, report.MessageId, report.Message);
                }
                catch (Exception exception)
                {
                    Logger.Warn($"ControlServer report item failed. instanceId={this.server.InstanceId}, messageId={report.MessageId}", exception);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn("ControlServer report worker stopped unexpectedly.", exception);
        }
    }

    private async Task<int> BroadcastAsync<T>(uint clientId, uint messageId, T payload)
    {
        List<(ControlEndpointConnection Connection, Task<bool> Task)> tasks = new();
        foreach (ControlEndpointConnection connection in this.connections)
        {
            tasks.Add((connection, SendToEndpointAsync(connection, clientId, messageId, payload)));
        }

        if (tasks.Count == 0)
        {
            Logger.Warn($"ControlServer broadcast skipped because no endpoints are configured. messageId={messageId}");
            return 0;
        }

        Task<bool[]> allReportsTask = Task.WhenAll(tasks.Select(item => item.Task));
        Task<bool> firstSuccessTask = WaitForFirstSuccessAsync(tasks.Select(item => item.Task));
        Task completedTask = await Task.WhenAny(allReportsTask, firstSuccessTask);
        if (completedTask != allReportsTask && await firstSuccessTask)
        {
            Task finalTask = await Task.WhenAny(allReportsTask, Task.Delay(BroadcastCompletionGraceInterval));
            if (finalTask != allReportsTask)
            {
                int pendingCount = tasks.Count(item => !item.Task.IsCompleted);
                Logger.Warn($"ControlServer broadcast continuing with pending endpoint reports. messageId={messageId}, pendingEndpoints={pendingCount}, graceMs={BroadcastCompletionGraceInterval.TotalMilliseconds}");
            }
        }

        int successCount = CountSuccessfulCompletedReports(tasks);
        if (successCount == 0)
        {
            Logger.Warn($"ControlServer broadcast completed without successful endpoint. messageId={messageId}, endpointCount={this.connections.Count}");
        }
        else
        {
            Logger.Debug($"ControlServer broadcast completed. messageId={messageId}, successEndpoints={successCount}, endpointCount={this.connections.Count}");
        }

        return successCount;
    }

    private static async Task<bool> WaitForFirstSuccessAsync(IEnumerable<Task<bool>> reportTasks)
    {
        List<Task<bool>> pendingTasks = reportTasks.ToList();
        while (pendingTasks.Count > 0)
        {
            Task<bool> completedTask = await Task.WhenAny(pendingTasks);
            pendingTasks.Remove(completedTask);
            if (await completedTask)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountSuccessfulCompletedReports(IEnumerable<(ControlEndpointConnection Connection, Task<bool> Task)> tasks)
    {
        int successCount = 0;
        foreach ((_, Task<bool> task) in tasks)
        {
            if (task.IsCompletedSuccessfully && task.Result)
            {
                successCount++;
            }
        }

        return successCount;
    }

    private static async Task<bool> SendToEndpointAsync<T>(
        ControlEndpointConnection connection,
        uint clientId,
        uint messageId,
        T payload)
    {
        try
        {
            (bool success, _) = await connection.SendAndReceiveAsync(clientId, messageId, payload);
            if (!success)
            {
                Logger.Warn($"ControlServer report failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}");
            }

            return success;
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer report failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            return false;
        }
        catch (IOException exception)
        {
            Logger.Warn($"ControlServer report I/O failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            return false;
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"ControlServer report authentication failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            return false;
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"ControlServer report failed because socket is disposed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            return false;
        }
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.Stop();
            this.server.SessionOpenedAsync -= this.SendSessionOpenedAsync;
            this.server.SessionUpdatedAsync -= this.SendSessionUpdatedAsync;
            this.server.SessionClosedAsync -= this.SendSessionClosedAsync;
            this.reportChannel.Writer.TryComplete();
            this.reportCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                this.reportWorkerTask.Wait(TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            this.reportCancellation.Dispose();
            foreach (ControlEndpointConnection connection in this.connections)
            {
                connection.Dispose();
            }

            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }

    private sealed record ControlReportMessage(uint MessageId, SessionEventMessage Message);

    private static TimeSpan NormalizeReportTimeout(TimeSpan? reportTimeout)
    {
        if (reportTimeout.HasValue && reportTimeout.Value > TimeSpan.Zero)
        {
            return reportTimeout.Value;
        }

        int timeoutMilliseconds = Math.Max(
            SocketFactory.ReadTimeoutMilliseconds,
            SocketFactory.WriteTimeoutMilliseconds);
        return TimeSpan.FromMilliseconds(timeoutMilliseconds);
    }

    private static TimeSpan NormalizeHeartbeatInterval(TimeSpan interval)
    {
        return interval <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : interval;
    }

    private static IReadOnlyCollection<ControlEndpointConnection> CreateConnections(
        IReadOnlyCollection<EndpointConfig> endpoints,
        TimeSpan reportTimeout)
    {
        List<ControlEndpointConnection> connections = new();
        foreach (EndpointConfig endpoint in endpoints)
        {
            connections.Add(new ControlEndpointConnection(endpoint, reportTimeout));
        }

        return connections;
    }

    private sealed class ControlEndpointConnection : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private readonly TimeSpan reportTimeout;
        private Socket socket;
        private SecureSocketConnection connection;

        public ControlEndpointConnection(EndpointConfig endpoint, TimeSpan reportTimeout)
        {
            this.Endpoint = endpoint;
            this.reportTimeout = reportTimeout;
        }

        public EndpointConfig Endpoint { get; }

        public async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveAsync<T>(
            uint clientId,
            uint messageId,
            T payload)
        {
            await this.sendLock.WaitAsync();
            try
            {
                Task<(bool Success, SocketMessageFrame Frame)> reportTask =
                    this.SendAndReceiveCoreAsync(clientId, messageId, payload);
                Task completedTask = await Task.WhenAny(reportTask, Task.Delay(this.reportTimeout));
                if (completedTask != reportTask)
                {
                    this.Close();
                    Logger.Warn($"ControlServer report timed out. endpoint={this.Endpoint.Host}:{this.Endpoint.Port}, messageId={messageId}, timeoutMs={this.reportTimeout.TotalMilliseconds}");
                    return (false, default);
                }

                (bool success, SocketMessageFrame frame) = await reportTask;
                if (!success)
                {
                    this.Close();
                }

                return (success, frame);
            }
            finally
            {
                this.sendLock.Release();
            }
        }

        private async Task<(bool Success, SocketMessageFrame Frame)> SendAndReceiveCoreAsync<T>(
            uint clientId,
            uint messageId,
            T payload)
        {
            await this.EnsureConnectedAsync();
            return await ControlProtocol.SendAndReceiveAsync(
                this.connection,
                clientId,
                messageId,
                payload);
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
            this.Close();
        }

        private async Task EnsureConnectedAsync()
        {
            if (this.connection != null && this.connection.IsConnected)
            {
                return;
            }

            this.Close();
            this.socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
            await SocketFactory.ConnectAsync(this.socket, IPAddress.Parse(this.Endpoint.Host), this.Endpoint.Port);
            this.connection = await SecureSocketConnection.AuthenticateClientAsync(this.socket, "SocketServer");
        }
    }
}
