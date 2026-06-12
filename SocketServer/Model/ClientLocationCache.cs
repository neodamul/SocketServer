using System;
using System.Collections.Concurrent;
using System.Linq;
using SocketCommon.Configuration;
using SocketCommon.Model;

namespace SocketServer.Model;

internal sealed class ClientLocationCache
{
    public const int DefaultTtlSeconds = 60;
    public const int DefaultMaxEntries = 200000;

    private readonly ConcurrentDictionary<uint, CachedClientLocation> locations = new();
    private readonly TimeSpan ttl;
    private readonly int maxEntries;

    public ClientLocationCache(ClientLocationCacheConfig? config = null)
    {
        config ??= new ClientLocationCacheConfig();
        this.Enabled = config.Enabled;
        this.ttl = TimeSpan.FromSeconds(config.TtlSeconds <= 0 ? DefaultTtlSeconds : config.TtlSeconds);
        this.maxEntries = Math.Max(1, config.MaxEntries <= 0 ? DefaultMaxEntries : config.MaxEntries);
    }

    public bool Enabled { get; }

    public int Count => this.locations.Count;

    public bool TryGet(uint clientId, out CachedClientLocation location)
    {
        location = default;
        if (!this.Enabled || clientId == 0)
        {
            return false;
        }

        if (!this.locations.TryGetValue(clientId, out CachedClientLocation cached))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - cached.CachedAt > this.ttl)
        {
            this.locations.TryRemove(clientId, out _);
            return false;
        }

        location = cached;
        return true;
    }

    public void Set(uint clientId, string instanceId, string host, int port)
    {
        if (!this.Enabled ||
            clientId == 0 ||
            string.IsNullOrWhiteSpace(instanceId) ||
            string.IsNullOrWhiteSpace(host) ||
            port <= 0)
        {
            return;
        }

        this.locations[clientId] = new CachedClientLocation(instanceId, host, port, DateTimeOffset.UtcNow);
        this.PruneIfNeeded();
    }

    public void Set(uint clientId, ClientLocationResponse location)
    {
        if (location == null || !location.Success)
        {
            return;
        }

        this.Set(clientId, location.InstanceId, location.Host, location.Port);
    }

    public void Set(uint clientId, BackendServerSnapshot server)
    {
        if (server == null)
        {
            return;
        }

        this.Set(clientId, server.InstanceId, server.Host, server.Port);
    }

    public void Invalidate(uint clientId)
    {
        if (clientId != 0)
        {
            this.locations.TryRemove(clientId, out _);
        }
    }

    private void PruneIfNeeded()
    {
        if (this.locations.Count <= this.maxEntries)
        {
            return;
        }

        foreach (uint key in this.locations
            .OrderBy(item => item.Value.CachedAt)
            .Take(Math.Max(1, this.locations.Count - this.maxEntries))
            .Select(item => item.Key))
        {
            this.locations.TryRemove(key, out _);
        }
    }
}

internal readonly record struct CachedClientLocation(
    string InstanceId,
    string Host,
    int Port,
    DateTimeOffset CachedAt);
