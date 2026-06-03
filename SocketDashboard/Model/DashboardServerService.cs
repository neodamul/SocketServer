using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading.Tasks;
using SocketCommon;
using SocketCommon.Configuration;
using SocketCommon.Logging;
using SocketCommon.Model;
using SocketControl.Model;
using SocketServer.Model;

namespace SocketDashboard.Model;

public class DashboardServerService : IDisposable
{
    private static readonly SocketLogger Logger = SocketLogManager.GetLogger<DashboardServerService>();

    private readonly TcpServer server;
    private readonly EndpointConfig controlEndpoint;
    private bool disposedValue;

    public DashboardServerService()
        : this(0)
    {
    }

    public DashboardServerService(int port)
        : this(port, new EndpointConfig { Host = Constants.LocalHostIpAddress, Port = Constants.LocalHostPort })
    {
    }

    public DashboardServerService(int port, EndpointConfig controlEndpoint)
    {
        this.StartedAt = DateTimeOffset.UtcNow;
        this.controlEndpoint = controlEndpoint;
        this.server = new TcpServer(1, "dashboardServer", Constants.LocalHostIpAddress, port);
        this.StartSucceeded = this.server.Start() && this.server.StartClientAcceptLoop();
        Logger.Info($"Dashboard server service started. port={port}, success={this.StartSucceeded}");
    }

    public DateTimeOffset StartedAt { get; }

    public bool StartSucceeded { get; }

    public DashboardServerStatus GetStatus()
    {
        return new DashboardServerStatus
        {
            DashboardStartedAt = this.StartedAt,
            StartSucceeded = this.StartSucceeded,
            Server = this.server.GetStatus(),
            Cluster = this.GetControlClusterStatus() ?? BuildClusterStatus(this.server.GetStatus())
        };
    }

    public DashboardHealthStatus GetLiveness()
    {
        return new DashboardHealthStatus
        {
            IsHealthy = !this.disposedValue,
            Status = this.disposedValue ? "Stopped" : "Alive",
            DashboardStartedAt = this.StartedAt,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    public DashboardHealthStatus GetReadiness()
    {
        TcpServerStatus status = this.server.GetStatus();
        bool ready = this.StartSucceeded &&
            status.IsListening &&
            status.IsAcceptLoopRunning;
        return new DashboardHealthStatus
        {
            IsHealthy = ready,
            Status = ready ? "Ready" : "NotReady",
            DashboardStartedAt = this.StartedAt,
            CheckedAt = DateTimeOffset.UtcNow
        };
    }

    public DashboardMetrics GetMetrics()
    {
        DashboardServerStatus status = this.GetStatus();
        return new DashboardMetrics
        {
            DashboardStartedAt = this.StartedAt,
            CollectedAt = DateTimeOffset.UtcNow,
            ServerCount = status.Cluster.ServerCount,
            HealthyServerCount = status.Cluster.HealthyServerCount,
            TotalMaxConnections = status.Cluster.TotalMaxConnections,
            TotalCurrentConnections = status.Cluster.TotalCurrentConnections,
            TotalReservedConnections = status.Cluster.TotalReservedConnections,
            TotalAvailableConnections = status.Cluster.TotalAvailableConnections,
            TotalSessionCount = status.Cluster.TotalSessionCount,
            AverageCpuUsagePercent = status.Cluster.AverageCpuUsagePercent,
            AverageMemoryUsagePercent = status.Cluster.AverageMemoryUsagePercent,
            AverageStorageUsagePercent = status.Cluster.AverageStorageUsagePercent,
            LocalAcceptedClients = status.Server.TotalAcceptedClients,
            LocalClosedClients = status.Server.TotalClosedClients,
            LocalRejectedClients = status.Server.TotalRejectedClients,
            LocalIdleTimeoutClients = status.Server.TotalIdleTimeoutClients,
            LocalReceivedMessages = status.Server.TotalReceivedMessages,
            LocalSentMessages = status.Server.TotalSentMessages,
            SocketAsyncEventArgsAvailableCount = status.Server.SocketAsyncEventArgsAvailableCount,
            SocketAsyncEventArgsInUseCount = status.Server.SocketAsyncEventArgsInUseCount,
            SocketAsyncEventArgsHighWatermarkInUseCount = status.Server.SocketAsyncEventArgsHighWatermarkInUseCount,
            SocketAsyncEventArgsGrowthCount = status.Server.SocketAsyncEventArgsGrowthCount,
            RequireTls13 = SecureSocketConnection.RequireTls13,
            RequireClientCertificate = SecureSocketConnection.RequireClientCertificate
        };
    }

    private ClusterStatusSnapshot? GetControlClusterStatus()
    {
        try
        {
            return QueryControlClusterStatusAsync(new EndpointConfig
            {
                Host = this.controlEndpoint.Host,
                Port = this.controlEndpoint.Port
            }).GetAwaiter().GetResult();
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (AuthenticationException)
        {
            return null;
        }
    }

    private static async Task<ClusterStatusSnapshot?> QueryControlClusterStatusAsync(EndpointConfig endpoint)
    {
        LocalCertificateStore.GetOrCreate("SocketDashboard");
        using Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        await socket.ConnectAsync(IPAddress.Parse(endpoint.Host), endpoint.Port);
        using SecureSocketConnection connection =
            await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketDashboard");
        (bool success, SocketMessageFrame frame) = await ControlProtocol.SendAndReceiveAsync(
            connection,
            0,
            ControlMessageIds.RegistrySnapshotRequest,
            new RegistrySnapshotRequest { RequestedAt = DateTimeOffset.UtcNow });
        if (!success ||
            !ControlProtocol.TryDecode(frame, ControlMessageIds.RegistrySnapshotResponse, out ClusterStatusSnapshot snapshot))
        {
            return null;
        }

        return snapshot;
    }

    private static ClusterStatusSnapshot BuildClusterStatus(TcpServerStatus status)
    {
        BackendServerSnapshot server = new()
        {
            ClusterId = "socket-cluster-1",
            ServerId = status.ServerId,
            InstanceId = status.InstanceId,
            Name = status.InstanceId,
            Host = status.IpAddress,
            Port = status.Port,
            MaxConnections = status.MaxConnections,
            CurrentConnections = status.ConnectedClientCount,
            AvailableConnections = status.AvailableConnections,
            Health = status.IsListening ? ServerHealthState.Healthy : ServerHealthState.Unhealthy,
            LastHeartbeatAt = status.UpdatedAt,
            UpdatedAt = status.UpdatedAt,
            StartedAt = status.StartedAt ?? status.UpdatedAt
        };

        return new ClusterStatusSnapshot
        {
            ServerCount = 1,
            HealthyServerCount = server.Health == ServerHealthState.Healthy ? 1 : 0,
            TotalMaxConnections = server.MaxConnections,
            TotalCurrentConnections = server.CurrentConnections,
            TotalReservedConnections = server.ReservedConnections,
            TotalAvailableConnections = server.AvailableConnections,
            Servers = new[] { server },
            UpdatedAt = status.UpdatedAt
        };
    }

    public void Dispose()
    {
        if (!this.disposedValue)
        {
            this.server.Dispose();
            Logger.Info("Dashboard server service disposed.");
            this.disposedValue = true;
            GC.SuppressFinalize(this);
        }
    }
}

public class DashboardServerStatus
{
    public DateTimeOffset DashboardStartedAt { get; init; }

    public bool StartSucceeded { get; init; }

    public TcpServerStatus Server { get; init; } = new();

    public ClusterStatusSnapshot Cluster { get; init; } = new();
}

public class DashboardHealthStatus
{
    public bool IsHealthy { get; init; }

    public string Status { get; init; } = "";

    public DateTimeOffset DashboardStartedAt { get; init; }

    public DateTimeOffset CheckedAt { get; init; }
}

public class DashboardMetrics
{
    public DateTimeOffset DashboardStartedAt { get; init; }

    public DateTimeOffset CollectedAt { get; init; }

    public int ServerCount { get; init; }

    public int HealthyServerCount { get; init; }

    public int TotalMaxConnections { get; init; }

    public int TotalCurrentConnections { get; init; }

    public int TotalReservedConnections { get; init; }

    public int TotalAvailableConnections { get; init; }

    public int TotalSessionCount { get; init; }

    public double AverageCpuUsagePercent { get; init; }

    public double AverageMemoryUsagePercent { get; init; }

    public double AverageStorageUsagePercent { get; init; }

    public long LocalAcceptedClients { get; init; }

    public long LocalClosedClients { get; init; }

    public long LocalRejectedClients { get; init; }

    public long LocalIdleTimeoutClients { get; init; }

    public long LocalReceivedMessages { get; init; }

    public long LocalSentMessages { get; init; }

    public int SocketAsyncEventArgsAvailableCount { get; init; }

    public int SocketAsyncEventArgsInUseCount { get; init; }

    public int SocketAsyncEventArgsHighWatermarkInUseCount { get; init; }

    public int SocketAsyncEventArgsGrowthCount { get; init; }

    public bool RequireTls13 { get; init; }

    public bool RequireClientCertificate { get; init; }
}
