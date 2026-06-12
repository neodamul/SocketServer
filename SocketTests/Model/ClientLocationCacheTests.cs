using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketCommon.Configuration;
using SocketCommon.Model;
using SocketServer.Model;

namespace SocketTests.Model;

[TestClass]
public class ClientLocationCacheTests
{
    [TestMethod]
    public void DefaultConfigEnablesClientLocationCacheTest()
    {
        SocketServerConfigFile config = new();

        Assert.IsTrue(config.ClientLocationCache.Enabled);
        Assert.AreEqual(60, config.ClientLocationCache.TtlSeconds);
        Assert.AreEqual(200000, config.ClientLocationCache.MaxEntries);
    }

    [TestMethod]
    public void TryGetReturnsCachedLocationTest()
    {
        ClientLocationCache cache = new();

        cache.Set(100, "server-1", "127.0.0.1", 10100);

        Assert.IsTrue(cache.TryGet(100, out CachedClientLocation location));
        Assert.AreEqual("server-1", location.InstanceId);
        Assert.AreEqual("127.0.0.1", location.Host);
        Assert.AreEqual(10100, location.Port);
    }

    [TestMethod]
    public void TryGetExpiresOldLocationTest()
    {
        ClientLocationCache cache = new(new ClientLocationCacheConfig
        {
            Enabled = true,
            TtlSeconds = 1,
            MaxEntries = 10
        });

        cache.Set(100, "server-1", "127.0.0.1", 10100);
        Assert.IsTrue(cache.TryGet(100, out _));
        System.Threading.Thread.Sleep(TimeSpan.FromMilliseconds(1200));

        Assert.IsFalse(cache.TryGet(100, out _));
    }

    [TestMethod]
    public void InvalidateRemovesLocationTest()
    {
        ClientLocationCache cache = new();

        cache.Set(100, "server-1", "127.0.0.1", 10100);
        cache.Invalidate(100);

        Assert.IsFalse(cache.TryGet(100, out _));
    }

    [TestMethod]
    public void MaxEntriesPrunesOldestLocationTest()
    {
        ClientLocationCache cache = new(new ClientLocationCacheConfig
        {
            Enabled = true,
            TtlSeconds = 60,
            MaxEntries = 2
        });

        cache.Set(100, "server-1", "127.0.0.1", 10100);
        System.Threading.Thread.Sleep(5);
        cache.Set(101, "server-2", "127.0.0.1", 10101);
        System.Threading.Thread.Sleep(5);
        cache.Set(102, "server-3", "127.0.0.1", 10102);

        Assert.IsFalse(cache.TryGet(100, out _));
        Assert.IsTrue(cache.TryGet(101, out _));
        Assert.IsTrue(cache.TryGet(102, out _));
    }

    [TestMethod]
    public void DisabledCacheDoesNotStoreLocationTest()
    {
        ClientLocationCache cache = new(new ClientLocationCacheConfig
        {
            Enabled = false
        });

        cache.Set(100, new ClientLocationResponse
        {
            Success = true,
            TargetClientId = 100,
            InstanceId = "server-1",
            Host = "127.0.0.1",
            Port = 10100
        });

        Assert.IsFalse(cache.TryGet(100, out _));
    }
}
