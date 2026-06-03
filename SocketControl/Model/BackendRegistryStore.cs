using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SocketCommon.Configuration;
using SocketCommon.Model;

namespace SocketControl.Model;

public interface IBackendRegistryStore
{
    BackendRegistryState Load();

    void Save(BackendRegistryState state);
}

public sealed class InMemoryBackendRegistryStore : IBackendRegistryStore
{
    public BackendRegistryState Load()
    {
        return new BackendRegistryState();
    }

    public void Save(BackendRegistryState state)
    {
    }
}

public sealed class FileBackendRegistryStore : IBackendRegistryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string path;

    public FileBackendRegistryStore(string path)
    {
        this.path = string.IsNullOrWhiteSpace(path)
            ? Path.Combine(AppContext.BaseDirectory, "control-registry.json")
            : path;
    }

    public BackendRegistryState Load()
    {
        if (!File.Exists(this.path))
        {
            return new BackendRegistryState();
        }

        string json = File.ReadAllText(this.path);
        return JsonSerializer.Deserialize<BackendRegistryState>(json, JsonOptions) ?? new BackendRegistryState();
    }

    public void Save(BackendRegistryState state)
    {
        string? directory = Path.GetDirectoryName(this.path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporaryPath = $"{this.path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        if (File.Exists(this.path))
        {
            File.Replace(temporaryPath, this.path, null);
            return;
        }

        File.Move(temporaryPath, this.path);
    }
}

public static class BackendRegistryStoreFactory
{
    public static IBackendRegistryStore Create(ClusterRegistryConfig config, string nodeId)
    {
        if (config != null &&
            string.Equals(config.Provider, "File", StringComparison.OrdinalIgnoreCase))
        {
            string path = string.IsNullOrWhiteSpace(config.ConnectionString)
                ? Path.Combine(AppContext.BaseDirectory, $"{nodeId}-registry.json")
                : config.ConnectionString;
            return new FileBackendRegistryStore(path);
        }

        return new InMemoryBackendRegistryStore();
    }
}

public sealed class BackendRegistryState
{
    public long Version { get; set; }

    public List<BackendServerSnapshot> Servers { get; set; } = new();

    public List<RouteReservationMessage> Reservations { get; set; } = new();

    public List<SessionEventMessage> Sessions { get; set; } = new();

    public List<ClientLocationMessage> ClientLocations { get; set; } = new();
}
