using System.Diagnostics;
using System.Threading;
using SocketCommon.Model;

namespace SocketLoadTest;

internal static class LoadTestCertificateWarmup
{
    public static bool IsRequired =>
        SecureSocketConnection.ConfiguredTransportMode == SocketTransportSecurityMode.Tls &&
        SecureSocketConnection.RequireClientCertificate;

    public static string GetClientCertificateModuleName(int clientId)
    {
        return $"SocketClient-{clientId}";
    }

    public static int GetDefaultConcurrency()
    {
        return Math.Clamp(Environment.ProcessorCount, 1, 32);
    }

    public static async Task<LoadTestCertificateWarmupResult> WarmUpAsync(
        IEnumerable<int> clientIds,
        int maxConcurrency,
        Action<int>? onCompleted,
        CancellationToken cancellationToken)
    {
        int[] ids = clientIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0 || !IsRequired)
        {
            return new LoadTestCertificateWarmupResult(ids.Length, 0, TimeSpan.Zero);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        int completed = 0;
        int nextIndex = -1;
        int workerCount = Math.Min(ids.Length, Math.Max(1, maxConcurrency));
        Task[] tasks = new Task[workerCount];

        for (int worker = 0; worker < workerCount; worker++)
        {
            tasks[worker] = Task.Run(
                () =>
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int currentIndex = Interlocked.Increment(ref nextIndex);
                        if (currentIndex >= ids.Length)
                        {
                            break;
                        }

                        int clientId = ids[currentIndex];
                        LocalCertificateStore.GetOrCreateCached(GetClientCertificateModuleName(clientId));
                        int current = Interlocked.Increment(ref completed);
                        onCompleted?.Invoke(current);
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();
        return new LoadTestCertificateWarmupResult(ids.Length, completed, stopwatch.Elapsed);
    }
}

internal sealed record LoadTestCertificateWarmupResult(
    int Total,
    int Completed,
    TimeSpan Elapsed);
