using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SocketCommon.Configuration;

public class EndpointConfig
{
    public string Host { get; set; } = Constants.LocalHostIpAddress;

    public int Port { get; set; }
}

public class ControlServerConfigFile
{
    public ControlServerNodeConfig ControlServer { get; set; } = new();

    public SocketSecurityConfig Security { get; set; } = new();

    public List<EndpointConfig> Peers { get; set; } = new();

    public ClusterRegistryConfig Registry { get; set; } = new();
}

public class ControlServerNodeConfig
{
    public string ClusterId { get; set; } = "socket-cluster-1";

    public string NodeId { get; set; } = "control-1";

    public string Host { get; set; } = Constants.LocalHostIpAddress;

    public int Port { get; set; } = Constants.LocalHostPort;

    public int PeerSyncPort { get; set; } = 5020;

    public int HeartbeatTimeoutSeconds { get; set; } = 90;

    public int RouteReservationSeconds { get; set; } = 10;

    public string RoutingPolicy { get; set; } = "MostAvailableConnections";

    public double DegradedCpuPercent { get; set; } = 85;

    public double DegradedMemoryPercent { get; set; } = 85;

    public double DegradedStoragePercent { get; set; } = 90;
}

public class ClusterRegistryConfig
{
    public string Provider { get; set; } = "InMemory";

    public string SyncMode { get; set; } = "ActiveActive";

    public string ConnectionString { get; set; } = "";
}

public class SocketServerConfigFile
{
    public SocketSecurityConfig Security { get; set; } = new();

    public SocketAsyncEventArgsPoolConfig SocketAsyncEventArgsPool { get; set; } = new();

    public List<EndpointConfig> ControlServers { get; set; } = new()
    {
        new EndpointConfig { Host = Constants.LocalHostIpAddress, Port = Constants.LocalHostPort }
    };

    public List<SocketServerInstanceConfig> Servers { get; set; } = new();
}

public class SocketServerInstanceConfig
{
    public int ServerId { get; set; } = 1;

    public string InstanceId { get; set; } = "server-1";

    public string Name { get; set; } = "socket-server-1";

    public string BindHost { get; set; } = Constants.LocalHostIpAddress;

    public int PortRangeStart { get; set; } = 5100;

    public int PortRangeEnd { get; set; } = 5199;

    public int MaxConnections { get; set; } = 10000;

    public int PendingAcceptCount { get; set; } = 100;

    public int IdleTimeoutSeconds { get; set; } = 90;

    public int HeartbeatIntervalSeconds { get; set; } = 30;
}

public class SocketClientConfigFile
{
    public SocketSecurityConfig Security { get; set; } = new();

    public SocketClientConnectionConfig Client { get; set; } = new();
}

public class SocketSecurityConfig
{
    public string TlsProtocol { get; set; } = "Tls13";

    public bool RequireTls13 { get; set; } = true;

    public string CertificateDirectory { get; set; } = "";

    public int AuthenticationTimeoutMilliseconds { get; set; } = 5000;
}

public class SocketAsyncEventArgsPoolConfig
{
    public int InitialSize { get; set; } = 1000;

    public int GrowthSize { get; set; } = 100;

    public int MaxRetained { get; set; } = 20000;
}

public class SocketClientConnectionConfig
{
    public int ClientId { get; set; } = 1;

    public string Name { get; set; } = "socket-client-1";

    public List<EndpointConfig> ControlEndpoints { get; set; } = new()
    {
        new EndpointConfig { Host = Constants.LocalHostIpAddress, Port = Constants.LocalHostPort }
    };

    public int HealthCheckIntervalSeconds { get; set; } = 30;
}

public static class SocketConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static T Load<T>(string path) where T : new()
    {
        if (!File.Exists(path))
        {
            return new T();
        }

        string json = File.ReadAllText(path);
        T value = JsonSerializer.Deserialize<T>(json, JsonOptions);
        return value == null ? new T() : value;
    }

    public static bool IsValidPortRange(int start, int end)
    {
        if (start == 0 && end == 0)
        {
            return true;
        }

        return start > 0 && end >= start && end <= 65535;
    }
}
