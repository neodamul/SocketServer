using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
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
    private readonly IReadOnlyCollection<EndpointConfig> controlEndpoints;
    private readonly object statusCacheLock = new();
    private readonly Dictionary<string, DashboardControlServerStatus> lastHealthyControlStatusByEndpoint = new(StringComparer.OrdinalIgnoreCase);
    private ClusterStatusSnapshot? lastSuccessfulCluster;
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
        : this(port, new[] { controlEndpoint })
    {
    }

    public DashboardServerService(int port, IEnumerable<EndpointConfig> controlEndpoints)
    {
        this.StartedAt = DateTimeOffset.UtcNow;
        this.controlEndpoints = NormalizeControlEndpoints(controlEndpoints).ToArray();
        this.server = new TcpServer(1, "dashboardServer", Constants.LocalHostIpAddress, port);
        this.StartSucceeded = this.server.Start() && this.server.StartClientAcceptLoop();
        Logger.Info($"Dashboard server service started. port={port}, success={this.StartSucceeded}");
    }

    public DateTimeOffset StartedAt { get; }

    public bool StartSucceeded { get; }

    public DashboardServerStatus GetStatus()
    {
        return this.GetStatusAsync().GetAwaiter().GetResult();
    }

    public async Task<DashboardServerStatus> GetStatusAsync()
    {
        TcpServerStatus serverStatus = this.server.GetStatus();
        ControlServerQueryResult controlStatus = await this.GetControlServerStatusesAsync();
        return new DashboardServerStatus
        {
            DashboardStartedAt = this.StartedAt,
            StartSucceeded = this.StartSucceeded,
            Server = serverStatus,
            Cluster = controlStatus.Cluster ?? BuildClusterStatus(serverStatus),
            ControlServers = controlStatus.ControlServers
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
        return this.GetMetricsAsync().GetAwaiter().GetResult();
    }

    public async Task<DashboardMetrics> GetMetricsAsync()
    {
        DashboardServerStatus status = await this.GetStatusAsync();
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
            SocketAsyncEventArgsBufferSize = status.Server.SocketAsyncEventArgsBufferSize,
            SocketAsyncEventArgsBufferSlabCount = status.Server.SocketAsyncEventArgsBufferSlabCount,
            SocketAsyncEventArgsBufferBytesAllocated = status.Server.SocketAsyncEventArgsBufferBytesAllocated,
            RequireTls13 = SecureSocketConnection.RequireTls13,
            RequireClientCertificate = SecureSocketConnection.RequireClientCertificate
        };
    }

    private async Task<ControlServerQueryResult> GetControlServerStatusesAsync()
    {
        ControlServerEndpointQuery[] queries = this.controlEndpoints
            .Select(endpoint => QueryControlServerStatusAsync(endpoint))
            .ToArray();
        if (queries.Length == 0)
        {
            return new ControlServerQueryResult();
        }

        await Task.WhenAll(queries.Select(query => query.Task));
        DashboardControlServerStatus[] controlServers = queries
            .Select(query => query.Task.Result)
            .ToArray();
        ClusterStatusSnapshot? mergedCluster = MergeClusterSnapshots(
            queries
                .Select(query => query.Snapshot)
                .Where(snapshot => snapshot != null)
                .Cast<ClusterStatusSnapshot>());

        return this.ApplyStatusCache(controlServers, mergedCluster);
    }

    private ControlServerQueryResult ApplyStatusCache(
        IReadOnlyCollection<DashboardControlServerStatus> controlServers,
        ClusterStatusSnapshot? mergedCluster)
    {
        lock (this.statusCacheLock)
        {
            if (mergedCluster != null)
            {
                this.lastSuccessfulCluster = mergedCluster;
            }

            DashboardControlServerStatus[] statuses = controlServers
                .Select(status =>
                {
                    string key = CreateEndpointKey(status.Host, status.Port);
                    if (status.IsHealthy)
                    {
                        this.lastHealthyControlStatusByEndpoint[key] = status;
                        return status;
                    }

                    if (!this.lastHealthyControlStatusByEndpoint.TryGetValue(key, out DashboardControlServerStatus cached))
                    {
                        return status;
                    }

                    return new DashboardControlServerStatus
                    {
                        Host = status.Host,
                        Port = status.Port,
                        IsHealthy = status.IsHealthy,
                        Status = status.Status,
                        ServerCount = cached.ServerCount,
                        HealthyServerCount = cached.HealthyServerCount,
                        TotalCurrentConnections = cached.TotalCurrentConnections,
                        TotalAvailableConnections = cached.TotalAvailableConnections,
                        TotalSessionCount = cached.TotalSessionCount,
                        CheckedAt = status.CheckedAt,
                        ErrorMessage = string.IsNullOrWhiteSpace(status.ErrorMessage)
                            ? "Using last known counters because the current query did not return a snapshot."
                            : $"{status.ErrorMessage} Last known counters are retained."
                    };
                })
                .ToArray();

            return new ControlServerQueryResult
            {
                Cluster = mergedCluster ?? this.lastSuccessfulCluster,
                ControlServers = statuses
            };
        }
    }

    private static ClusterStatusSnapshot? MergeClusterSnapshots(IEnumerable<ClusterStatusSnapshot> snapshots)
    {
        ClusterStatusSnapshot[] healthySnapshots = snapshots.ToArray();
        if (healthySnapshots.Length == 0)
        {
            return null;
        }

        BackendServerSnapshot[] servers = healthySnapshots
            .SelectMany(snapshot => snapshot.Servers ?? Array.Empty<BackendServerSnapshot>())
            .GroupBy(server => server.InstanceId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(server => server.LastHeartbeatAt)
                .ThenByDescending(server => server.UpdatedAt)
                .ThenByDescending(server => server.Version)
                .First())
            .OrderBy(server => server.ServerId)
            .ThenBy(server => server.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ClusterStatusSnapshot
        {
            Servers = servers,
            ServerCount = servers.Length,
            HealthyServerCount = servers.Count(server => server.Health == ServerHealthState.Healthy),
            TotalMaxConnections = servers.Sum(server => server.MaxConnections),
            TotalCurrentConnections = servers.Sum(server => server.CurrentConnections),
            TotalReservedConnections = servers.Sum(server => server.ReservedConnections),
            TotalAvailableConnections = servers.Sum(server => server.AvailableConnections),
            TotalSessionCount = healthySnapshots.Max(snapshot => snapshot.TotalSessionCount),
            AverageCpuUsagePercent = servers.Length == 0 ? 0 : servers.Average(server => server.ResourceUsage.CpuUsagePercent),
            AverageMemoryUsagePercent = servers.Length == 0 ? 0 : servers.Average(server => server.ResourceUsage.MemoryUsagePercent),
            AverageStorageUsagePercent = servers.Length == 0 ? 0 : servers.Average(server => server.ResourceUsage.StorageUsagePercent),
            UpdatedAt = healthySnapshots.Max(snapshot => snapshot.UpdatedAt)
        };
    }

    private static ControlServerEndpointQuery QueryControlServerStatusAsync(EndpointConfig endpoint)
    {
        DateTimeOffset checkedAt = DateTimeOffset.UtcNow;
        ControlServerEndpointQuery query = new();
        query.Task = Task.Run(async () =>
        {
            try
            {
                ClusterStatusSnapshot? snapshot = await QueryControlClusterStatusAsync(new EndpointConfig
                {
                    Host = endpoint.Host,
                    Port = endpoint.Port
                });
                query.Snapshot = snapshot;
                if (snapshot == null)
                {
                    return CreateControlServerStatus(endpoint, false, "NoResponse", checkedAt, null, "Registry snapshot was not returned.");
                }

                return CreateControlServerStatus(endpoint, true, "Healthy", checkedAt, snapshot, "");
            }
            catch (SocketException exception)
            {
                return CreateControlServerStatus(endpoint, false, "Unavailable", checkedAt, null, exception.Message);
            }
            catch (IOException exception)
            {
                return CreateControlServerStatus(endpoint, false, "Unavailable", checkedAt, null, exception.Message);
            }
            catch (AuthenticationException exception)
            {
                return CreateControlServerStatus(endpoint, false, "AuthenticationFailed", checkedAt, null, exception.Message);
            }
            catch (TimeoutException exception)
            {
                return CreateControlServerStatus(endpoint, false, "Timeout", checkedAt, null, exception.Message);
            }
            catch (FormatException exception)
            {
                return CreateControlServerStatus(endpoint, false, "InvalidEndpoint", checkedAt, null, exception.Message);
            }
            catch (Exception exception)
            {
                return CreateControlServerStatus(endpoint, false, "Unavailable", checkedAt, null, exception.Message);
            }
        });
        return query;
    }

    private static async Task<ClusterStatusSnapshot?> QueryControlClusterStatusAsync(EndpointConfig endpoint)
    {
        LocalCertificateStore.GetOrCreate("SocketDashboard");
        using Socket socket = SocketFactory.CreateTcpSocket(AddressFamily.InterNetwork);
        Task<ClusterStatusSnapshot?> queryTask = QueryControlClusterStatusAsync(endpoint, socket);
        Task completedTask = await Task.WhenAny(
            queryTask,
            Task.Delay(GetControlQueryTimeoutMilliseconds()));
        if (completedTask != queryTask)
        {
            socket.Dispose();
            throw new TimeoutException($"ControlServer dashboard query timed out. endpoint={endpoint.Host}:{endpoint.Port}");
        }

        return await queryTask;
    }

    private static async Task<ClusterStatusSnapshot?> QueryControlClusterStatusAsync(EndpointConfig endpoint, Socket socket)
    {
        await SocketFactory.ConnectAsync(socket, endpoint.Host, endpoint.Port);
        using SecureSocketConnection connection = await SecureSocketConnection.AuthenticateClientAsync(socket, "SocketDashboard");
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

    private static int GetControlQueryTimeoutMilliseconds()
    {
        int timeout = Math.Min(SocketFactory.ConnectTimeoutMilliseconds, SocketFactory.ReadTimeoutMilliseconds);
        return Math.Max(1000, timeout);
    }

    private static DashboardControlServerStatus CreateControlServerStatus(
        EndpointConfig endpoint,
        bool isHealthy,
        string status,
        DateTimeOffset checkedAt,
        ClusterStatusSnapshot? snapshot,
        string errorMessage)
    {
        return new DashboardControlServerStatus
        {
            Host = endpoint.Host,
            Port = endpoint.Port,
            IsHealthy = isHealthy,
            Status = status,
            ServerCount = snapshot?.ServerCount ?? 0,
            HealthyServerCount = snapshot?.HealthyServerCount ?? 0,
            TotalCurrentConnections = snapshot?.TotalCurrentConnections ?? 0,
            TotalAvailableConnections = snapshot?.TotalAvailableConnections ?? 0,
            TotalSessionCount = snapshot?.TotalSessionCount ?? 0,
            CheckedAt = checkedAt,
            ErrorMessage = errorMessage
        };
    }

    private static string CreateEndpointKey(string host, int port)
    {
        return $"{host}:{port}";
    }

    private static IEnumerable<EndpointConfig> NormalizeControlEndpoints(IEnumerable<EndpointConfig> endpoints)
    {
        EndpointConfig[] normalized = (endpoints ?? Array.Empty<EndpointConfig>())
            .Where(endpoint => endpoint != null)
            .Select(endpoint => new EndpointConfig
            {
                Host = string.IsNullOrWhiteSpace(endpoint.Host) ? Constants.LocalHostIpAddress : endpoint.Host,
                Port = endpoint.Port <= 0 ? Constants.LocalHostPort : endpoint.Port
            })
            .GroupBy(endpoint => $"{endpoint.Host}:{endpoint.Port}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        return normalized.Length == 0
            ? new[] { new EndpointConfig { Host = Constants.LocalHostIpAddress, Port = Constants.LocalHostPort } }
            : normalized;
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

    public IReadOnlyCollection<DashboardControlServerStatus> ControlServers { get; init; } = Array.Empty<DashboardControlServerStatus>();
}

public class DashboardControlServerStatus
{
    public string Host { get; init; } = "";

    public int Port { get; init; }

    public bool IsHealthy { get; init; }

    public string Status { get; init; } = "";

    public int ServerCount { get; init; }

    public int HealthyServerCount { get; init; }

    public int TotalCurrentConnections { get; init; }

    public int TotalAvailableConnections { get; init; }

    public int TotalSessionCount { get; init; }

    public DateTimeOffset CheckedAt { get; init; }

    public string ErrorMessage { get; init; } = "";
}

internal sealed class ControlServerQueryResult
{
    public ClusterStatusSnapshot? Cluster { get; init; }

    public IReadOnlyCollection<DashboardControlServerStatus> ControlServers { get; init; } = Array.Empty<DashboardControlServerStatus>();
}

internal sealed class ControlServerEndpointQuery
{
    public ClusterStatusSnapshot? Snapshot { get; set; }

    public Task<DashboardControlServerStatus> Task { get; set; } = System.Threading.Tasks.Task.FromResult(new DashboardControlServerStatus());
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

    public int SocketAsyncEventArgsBufferSize { get; init; }

    public int SocketAsyncEventArgsBufferSlabCount { get; init; }

    public long SocketAsyncEventArgsBufferBytesAllocated { get; init; }

    public bool RequireTls13 { get; init; }

    public bool RequireClientCertificate { get; init; }
}
