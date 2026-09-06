using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SocketLoadTest;

namespace SocketTests.Model;

[TestClass]
public class LoadTestTimingTests
{
    [TestMethod]
    public void TimingSeparatesFailuresAndUsesNearestRankBounds()
    {
        LoadTestTimings timings = new();
        for (int i = 1; i <= 100; i++)
        {
            timings.Record("tcp_connect", TimeSpan.FromMilliseconds(i), true);
        }
        timings.Record("tcp_connect", TimeSpan.FromSeconds(30), false);

        var stage = timings.Snapshot()["tcp_connect"];
        Assert.AreEqual(100L, stage.Succeeded.Count);
        Assert.AreEqual(50.5, stage.Succeeded.MeanMilliseconds, 0.001);
        AssertBound(stage.Succeeded.P50UpperBoundMilliseconds, 50);
        AssertBound(stage.Succeeded.P95UpperBoundMilliseconds, 95);
        AssertBound(stage.Succeeded.P99UpperBoundMilliseconds, 99);
        Assert.AreEqual(1L, stage.Failed.Count);
        Assert.AreEqual(30000, stage.Failed.MaxMilliseconds);
    }

    [TestMethod]
    public void TimingConcurrentWritersDoNotLoseSamples()
    {
        LoadTestTimings timings = new();
        Parallel.For(0, 10000, _ => timings.Record("client_ready", TimeSpan.FromMilliseconds(2), true));
        Assert.AreEqual(10000L, timings.Snapshot()["client_ready"].Succeeded.Count);
    }

    [TestMethod]
    public void TimingSnapshotsAreIndependentAndInvalidStagesAreRejected()
    {
        LoadTestTimings timings = new();
        timings.Record("first_healthcheck", TimeSpan.Zero, true);
        var before = timings.Snapshot()["first_healthcheck"];
        timings.Record("first_healthcheck", TimeSpan.FromSeconds(1), false);
        Assert.AreEqual(0L, before.Failed.Count);
        Assert.AreEqual(0d, before.Succeeded.P99UpperBoundMilliseconds);
        Assert.AreEqual(1L, timings.Snapshot()["first_healthcheck"].Failed.Count);
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            timings.Record("client-123", TimeSpan.Zero, true));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() =>
            timings.Record("tcp_connect", TimeSpan.FromMilliseconds(-1), true));
    }

    [TestMethod]
    public void TimingSnapshotAlwaysUsesTheStableAdmissionStageSchema()
    {
        LoadTestTimings timings = new();

        CollectionAssert.AreEquivalent(
            new[] { "route_lookup", "tcp_connect", "tls_authenticate", "register", "first_healthcheck", "client_ready" },
            timings.Snapshot().Keys.ToArray());

        foreach (var stage in timings.Snapshot().Values)
        {
            Assert.AreEqual(0L, stage.Succeeded.Count);
            Assert.AreEqual(0L, stage.Failed.Count);
        }
    }

    private static void AssertBound(double actual, double value)
    {
        Assert.IsTrue(actual >= value && actual <= value * 1.1,
            $"Expected upper bound in [{value}, {value * 1.1}], got {actual}.");
    }
}
