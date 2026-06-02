using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
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
    private readonly ResourceUsageProvider resourceUsageProvider = new();
    private readonly IReadOnlyCollection<ControlEndpointConnection> connections;
    private CancellationTokenSource cancellation;
    private Task heartbeatTask;
    private bool disposedValue;

    public ControlServerReporter(
        TcpServer server,
        IReadOnlyCollection<EndpointConfig> controlServers,
        string clusterId,
        int portRangeStart,
        int portRangeEnd)
    {
        this.server = server;
        this.controlServers = controlServers;
        this.clusterId = string.IsNullOrWhiteSpace(clusterId) ? "socket-cluster-1" : clusterId;
        this.portRangeStart = portRangeStart;
        this.portRangeEnd = portRangeEnd;
        this.connections = CreateConnections(controlServers);
        this.server.ConfigureControlRouting(this.controlServers, this.clusterId);
        this.server.SessionOpenedAsync += this.SendSessionOpenedAsync;
        this.server.SessionUpdatedAsync += this.SendSessionUpdatedAsync;
        this.server.SessionClosedAsync += this.SendSessionClosedAsync;
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

        await BroadcastAsync(0, messageId, message);
    }

    private async Task BroadcastAsync<T>(uint clientId, uint messageId, T payload)
    {
        foreach (ControlEndpointConnection connection in this.connections)
        {
            try
            {
                await connection.SendAndReceiveAsync(clientId, messageId, payload);
            }
            catch (SocketException exception)
            {
                Logger.Warn($"ControlServer report failed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            }
            catch (ObjectDisposedException exception)
            {
                Logger.Warn($"ControlServer report failed because socket is disposed. endpoint={connection.Endpoint.Host}:{connection.Endpoint.Port}, messageId={messageId}", exception);
            }
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
            foreach (ControlEndpointConnection connection in this.connections)
            {
                connection.Dispose();
            }

            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }

    private static IReadOnlyCollection<ControlEndpointConnection> CreateConnections(IReadOnlyCollection<EndpointConfig> endpoints)
    {
        List<ControlEndpointConnection> connections = new();
        foreach (EndpointConfig endpoint in endpoints)
        {
            connections.Add(new ControlEndpointConnection(endpoint));
        }

        return connections;
    }

    private sealed class ControlEndpointConnection : IDisposable
    {
        private readonly SemaphoreSlim sendLock = new(1, 1);
        private Socket socket;
        private SecureSocketConnection connection;

        public ControlEndpointConnection(EndpointConfig endpoint)
        {
            this.Endpoint = endpoint;
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
                await this.EnsureConnectedAsync();
                (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
                    this.connection,
                    clientId,
                    messageId,
                    payload);
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
            await this.socket.ConnectAsync(IPAddress.Parse(this.Endpoint.Host), this.Endpoint.Port);
            this.connection = await SecureSocketConnection.AuthenticateClientAsync(this.socket, "SocketServer");
        }
    }
}
