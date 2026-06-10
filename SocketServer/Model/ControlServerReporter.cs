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
    private const int SessionReportChannelCount = 4;
    private const int SessionReportWorkerCount = 4;
    private const int SessionReportQueueCapacity = 10000;

    private readonly TcpServer server;
    private readonly IReadOnlyCollection<EndpointConfig> controlServers;
    private readonly string clusterId;
    private readonly int portRangeStart;
    private readonly int portRangeEnd;
    private readonly TimeSpan reportTimeout;
    private readonly ResourceUsageProvider resourceUsageProvider = new();
    private readonly IReadOnlyCollection<ControlEndpointConnection> connections;
    private readonly IReadOnlyCollection<ControlEndpointConnectionGroup> sessionConnectionGroups;
    private readonly object relayRefreshLock = new();
    private readonly Channel<ControlReportMessage>[] reportChannels;
    private readonly CancellationTokenSource reportCancellation = new();
    private readonly CancellationTokenSource relayRefreshCancellation = new();
    private readonly Task[] reportWorkerTasks;
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
        this.sessionConnectionGroups = CreateConnectionGroups(controlServers, this.reportTimeout, SessionReportChannelCount);
        this.reportChannels = CreateReportChannels(SessionReportWorkerCount);
        this.server.ConfigureControlRouting(this.controlServers, this.clusterId);
        this.server.SessionOpenedAsync += this.SendSessionOpenedAsync;
        this.server.SessionUpdatedAsync += this.SendSessionUpdatedAsync;
        this.server.SessionClosedAsync += this.SendSessionClosedAsync;
        this.reportWorkerTasks = StartReportWorkers(this.reportChannels, this.reportCancellation.Token);
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
            this.QueueRelayRefresh(force: true, delay: TimeSpan.FromSeconds(1));
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
        this.heartbeatTask = DedicatedWorker.Start(
            token => this.RunHeartbeatLoopAsync(normalizedInterval, token),
            this.cancellation.Token);
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

        foreach (ControlEndpointConnectionGroup group in this.sessionConnectionGroups)
        {
            foreach (ControlEndpointConnection connection in group.Connections)
            {
                connection.Close();
            }
        }

        this.relayRefreshCancellation.Cancel();
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
            TotalReceivedMessages = status.TotalReceivedMessages,
            TotalSentMessages = status.TotalSentMessages,
            TotalReceivedMessageBytes = status.TotalReceivedMessageBytes,
            TotalSentMessageBytes = status.TotalSentMessageBytes,
            ListenBacklog = status.ListenBacklog,
            PendingAcceptCount = status.PendingAcceptCount,
            IdleTimeoutSeconds = status.IdleTimeoutSeconds,
            NoDelay = status.NoDelay,
            MaxPayloadLength = status.MaxPayloadLength,
            SocketAsyncEventArgsAvailableCount = status.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsTotalCreatedCount = status.SocketAsyncEventArgsTotalCreatedCount,
            SocketAsyncEventArgsInUseCount = status.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = status.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = status.SocketAsyncEventArgsGrowthCount,
            SentAt = status.ObservedAt == default ? DateTimeOffset.UtcNow : status.ObservedAt
        };

        int successCount = await BroadcastAsync(0, ControlMessageIds.ServerHeartbeat, heartbeat);
        if (successCount > 0)
        {
            this.QueueRelayRefresh(force: false);
        }
    }

    private void QueueRelayRefresh(bool force, TimeSpan? delay = null)
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

        _ = this.RefreshRelayServersInBackgroundAsync(delay ?? TimeSpan.Zero, this.relayRefreshCancellation.Token);
    }

    private async Task RefreshRelayServersInBackgroundAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await this.server.RefreshRelayServersFromControlServersAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            Logger.Warn($"SocketServer relay server refresh failed. instanceId={this.server.InstanceId}", exception);
        }
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

    private Task[] StartReportWorkers(Channel<ControlReportMessage>[] channels, CancellationToken cancellationToken)
    {
        Task[] workers = new Task[channels.Length];
        for (int i = 0; i < workers.Length; i++)
        {
            ChannelReader<ControlReportMessage> reader = channels[i].Reader;
            workers[i] = DedicatedWorker.Start(token => this.RunReportWorkerAsync(reader, token), cancellationToken);
        }

        return workers;
    }

    private Task SendSessionEventAsync(ConnectionSession session, uint messageId, string state)
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

        ChannelWriter<ControlReportMessage> writer = this.GetReportChannelWriter(message);
        if (!writer.TryWrite(new ControlReportMessage(messageId, message)))
        {
            Logger.Warn($"ControlServer session event queue rejected item. instanceId={this.server.InstanceId}, messageId={messageId}, sessionId={session.Id}, clientId={session.ClientId}");
        }
        else
        {
            Logger.Debug($"ControlServer session event queued. instanceId={this.server.InstanceId}, messageId={messageId}, sessionId={session.Id}, clientId={session.ClientId}");
        }

        return Task.CompletedTask;
    }

    private async Task RunReportWorkerAsync(ChannelReader<ControlReportMessage> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (ControlReportMessage report in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await BroadcastSessionEventAsync(0, report.MessageId, report.Message);
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

    private async Task<int> BroadcastAsync<T>(
        uint clientId,
        uint messageId,
        T payload,
        bool requireAllEndpoints = false)
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
        if (requireAllEndpoints)
        {
            await allReportsTask;
            int completedSuccessCount = CountSuccessfulCompletedReports(tasks);
            if (completedSuccessCount == 0)
            {
                Logger.Warn($"ControlServer broadcast completed without successful endpoint. messageId={messageId}, endpointCount={this.connections.Count}");
            }
            else
            {
                Logger.Debug($"ControlServer broadcast completed. messageId={messageId}, successEndpoints={completedSuccessCount}, endpointCount={this.connections.Count}");
            }

            return completedSuccessCount;
        }

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

    private async Task<int> BroadcastSessionEventAsync<T>(
        uint clientId,
        uint messageId,
        T payload)
    {
        List<Task<bool>> tasks = new();
        ulong partitionKey = GetSessionEventPartitionKey(clientId, payload);
        foreach (ControlEndpointConnectionGroup group in this.sessionConnectionGroups)
        {
            ControlEndpointConnection connection = group.GetConnection(partitionKey);
            tasks.Add(SendSessionEventToEndpointWithRetryAsync(connection, clientId, messageId, payload));
        }

        if (tasks.Count == 0)
        {
            Logger.Warn($"ControlServer session event skipped because no endpoints are configured. messageId={messageId}");
            return 0;
        }

        bool[] results = await Task.WhenAll(tasks);
        int successCount = results.Count(result => result);
        if (successCount == 0)
        {
            Logger.Warn($"ControlServer session event completed without successful endpoint. messageId={messageId}, endpointCount={tasks.Count}");
        }
        else if (messageId == ControlMessageIds.SessionUpdated)
        {
            Logger.Debug($"ControlServer session event completed. messageId={messageId}, successEndpoints={successCount}, endpointCount={tasks.Count}");
        }
        else
        {
            Logger.Info($"ControlServer session event completed. messageId={messageId}, successEndpoints={successCount}, endpointCount={tasks.Count}");
        }

        return successCount;
    }

    private static bool RequiresAllEndpointReports(uint messageId)
    {
        return messageId == ControlMessageIds.SessionOpened ||
            messageId == ControlMessageIds.SessionUpdated ||
            messageId == ControlMessageIds.SessionClosed;
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

    private static async Task<bool> SendSessionEventToEndpointWithRetryAsync<T>(
        ControlEndpointConnection connection,
        uint clientId,
        uint messageId,
        T payload)
    {
        const int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await SendToEndpointAsync(connection, clientId, messageId, payload))
            {
                return true;
            }

            if (attempt < maxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.Stop();
            this.server.SessionOpenedAsync -= this.SendSessionOpenedAsync;
            this.server.SessionUpdatedAsync -= this.SendSessionUpdatedAsync;
            this.server.SessionClosedAsync -= this.SendSessionClosedAsync;
            foreach (Channel<ControlReportMessage> channel in this.reportChannels)
            {
                channel.Writer.TryComplete();
            }

            this.reportCancellation.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                Task.WaitAll(this.reportWorkerTasks, TimeSpan.FromSeconds(5));
            }
            catch (AggregateException)
            {
            }

            this.reportCancellation.Dispose();
            this.relayRefreshCancellation.Dispose();
            foreach (ControlEndpointConnection connection in this.connections)
            {
                connection.Dispose();
            }

            foreach (ControlEndpointConnectionGroup group in this.sessionConnectionGroups)
            {
                foreach (ControlEndpointConnection connection in group.Connections)
                {
                    connection.Dispose();
                }
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

    private static Channel<ControlReportMessage>[] CreateReportChannels(int workerCount)
    {
        int normalizedWorkerCount = Math.Max(2, workerCount);
        Channel<ControlReportMessage>[] channels = new Channel<ControlReportMessage>[normalizedWorkerCount];
        for (int i = 0; i < channels.Length; i++)
        {
            channels[i] = Channel.CreateBounded<ControlReportMessage>(
                new BoundedChannelOptions(SessionReportQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });
        }

        return channels;
    }

    private ChannelWriter<ControlReportMessage> GetReportChannelWriter(SessionEventMessage message)
    {
        int index = GetPartitionIndex((ulong)message.SessionId, this.reportChannels.Length);
        return this.reportChannels[index].Writer;
    }

    private static IReadOnlyCollection<ControlEndpointConnectionGroup> CreateConnectionGroups(
        IReadOnlyCollection<EndpointConfig> endpoints,
        TimeSpan reportTimeout,
        int channelCount)
    {
        int normalizedChannelCount = Math.Max(2, channelCount);
        List<ControlEndpointConnectionGroup> groups = new();
        foreach (EndpointConfig endpoint in endpoints)
        {
            List<ControlEndpointConnection> connections = new();
            for (int i = 0; i < normalizedChannelCount; i++)
            {
                connections.Add(new ControlEndpointConnection(endpoint, reportTimeout));
            }

            groups.Add(new ControlEndpointConnectionGroup(endpoint, connections));
        }

        return groups;
    }

    private static ulong GetSessionEventPartitionKey<T>(uint clientId, T payload)
    {
        if (payload is SessionEventMessage session && session.SessionId > 0)
        {
            return (ulong)session.SessionId;
        }

        return clientId;
    }

    private static int GetPartitionIndex(ulong partitionKey, int partitionCount)
    {
        return (int)(partitionKey % (ulong)partitionCount);
    }

    private sealed class ControlEndpointConnectionGroup
    {
        public ControlEndpointConnectionGroup(
            EndpointConfig endpoint,
            IReadOnlyList<ControlEndpointConnection> connections)
        {
            this.Endpoint = endpoint;
            this.Connections = connections;
        }

        public EndpointConfig Endpoint { get; }

        public IReadOnlyList<ControlEndpointConnection> Connections { get; }

        public ControlEndpointConnection GetConnection(ulong partitionKey)
        {
            int index = GetPartitionIndex(partitionKey, this.Connections.Count);
            return this.Connections[index];
        }
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

        public Task ConnectAsync()
        {
            return this.EnsureConnectedAsync();
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
            await SocketFactory.ConnectAsync(this.socket, this.Endpoint.Host, this.Endpoint.Port);
            this.connection = await SecureSocketConnection.AuthenticateClientAsync(this.socket, "SocketServer");
        }
    }
}
