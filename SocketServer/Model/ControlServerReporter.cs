using System;
using System.Collections.Generic;
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

    private readonly TcpServer server;
    private readonly IReadOnlyCollection<EndpointConfig> controlServers;
    private readonly string clusterId;
    private readonly int portRangeStart;
    private readonly int portRangeEnd;
    private readonly TimeSpan reportTimeout;
    private readonly ResourceUsageProvider resourceUsageProvider = new();
    private readonly IReadOnlyCollection<ControlEndpointConnection> connections;
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

        await BroadcastAsync(0, ControlMessageIds.ServerRegister, request);
        _ = this.server.RefreshRelayServersFromControlServersAsync();
    }

    public void StartHeartbeatLoop(TimeSpan interval)
    {
        if (this.heartbeatTask != null && !this.heartbeatTask.IsCompleted)
        {
            return;
        }

        this.cancellation?.Dispose();
        this.cancellation = new CancellationTokenSource();
        this.heartbeatTask = this.RunHeartbeatLoopAsync(interval, this.cancellation.Token);
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
                await Task.Delay(interval, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
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

        await BroadcastAsync(0, ControlMessageIds.ServerHeartbeat, heartbeat);
        _ = this.server.RefreshRelayServersFromControlServersAsync();
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
                await BroadcastAsync(0, report.MessageId, report.Message);
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

    private async Task BroadcastAsync<T>(uint clientId, uint messageId, T payload)
    {
        List<Task> tasks = new();
        foreach (ControlEndpointConnection connection in this.connections)
        {
            tasks.Add(SendToEndpointAsync(connection, clientId, messageId, payload));
        }

        await Task.WhenAll(tasks);
    }

    private static async Task SendToEndpointAsync<T>(
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
        }
        catch (SocketException exception)
        {
            Logger.Warn($"ControlServer report failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
        }
        catch (IOException exception)
        {
            Logger.Warn($"ControlServer report I/O failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
        }
        catch (AuthenticationException exception)
        {
            Logger.Warn($"ControlServer report authentication failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
        }
        catch (ObjectDisposedException exception)
        {
            Logger.Warn($"ControlServer report failed because socket is disposed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
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
