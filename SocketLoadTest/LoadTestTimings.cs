namespace SocketLoadTest;

// Fixed stage names and fixed buckets keep diagnostics independent of client count.
internal sealed class LoadTestTimings
{
    private readonly Dictionary<string, StageHistogram> stages = new(StringComparer.Ordinal);

    public LoadTestTimings()
    {
        foreach (string stage in new[] { "tcp_connect", "tls_authenticate", "register", "route_lookup", "first_healthcheck", "client_ready" })
        {
            stages.Add(stage, new StageHistogram());
        }
    }

    public void Record(string stage, TimeSpan duration, bool success)
    {
        if (!stages.TryGetValue(stage, out StageHistogram? histogram))
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (duration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration));
        histogram.Record(duration.TotalMilliseconds, success);
    }

    public IReadOnlyDictionary<string, StageTimingReport> Snapshot() =>
        stages.ToDictionary(pair => pair.Key, pair => pair.Value.Snapshot(), StringComparer.Ordinal);

    private sealed class StageHistogram
    {
        private readonly object gate = new();
        private readonly Histogram succeeded = new();
        private readonly Histogram failed = new();

        public void Record(double milliseconds, bool success)
        {
            lock (gate)
                (success ? succeeded : failed).Record(milliseconds);
        }

        public StageTimingReport Snapshot()
        {
            lock (gate)
                return new(succeeded.Snapshot(), failed.Snapshot());
        }
    }

    private sealed class Histogram
    {
        private static readonly double[] Bounds = CreateBounds();
        private readonly long[] buckets = new long[Bounds.Length];
        private long count;
        private double sum;
        private double max;

        public void Record(double milliseconds)
        {
            int index = Array.BinarySearch(Bounds, milliseconds);
            if (index < 0) index = ~index;
            buckets[Math.Min(index, buckets.Length - 1)]++;
            count++;
            sum += milliseconds;
            max = Math.Max(max, milliseconds);
        }

        public LatencyTimingReport Snapshot() => new(count, count == 0 ? 0 : sum / count,
            max, Percentile(0.50), Percentile(0.95), Percentile(0.99));

        private double Percentile(double quantile)
        {
            if (count == 0) return 0;
            long rank = (long)Math.Ceiling(count * quantile);
            long cumulative = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                cumulative += buckets[i];
                if (cumulative >= rank)
                    return i == buckets.Length - 1 ? max : Math.Min(Bounds[i], max);
            }
            return max;
        }

        private static double[] CreateBounds()
        {
            double[] bounds = new double[257];
            for (int i = 1; i < bounds.Length; i++)
                bounds[i] = 0.001 * Math.Pow(1.1, i - 1);
            return bounds;
        }
    }
}

internal sealed record StageTimingReport(LatencyTimingReport Succeeded, LatencyTimingReport Failed);

internal sealed record LatencyTimingReport(long Count, double MeanMilliseconds, double MaxMilliseconds,
    double P50UpperBoundMilliseconds, double P95UpperBoundMilliseconds, double P99UpperBoundMilliseconds);
