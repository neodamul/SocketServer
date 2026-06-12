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
        return SecureSocketConnection.EnforceClientCertificateId && clientId > 0
            ? $"SocketClient-{clientId}"
            : "SocketClient";
    }

    public static int GetRequiredCertificateCount(IEnumerable<int> clientIds)
    {
        return clientIds
            .Select(GetClientCertificateModuleName)
            .Distinct(StringComparer.Ordinal)
            .Count();
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
        string[] moduleNames = clientIds
            .Select(GetClientCertificateModuleName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(moduleName => moduleName, StringComparer.Ordinal)
            .ToArray();
        if (moduleNames.Length == 0 || !IsRequired)
        {
            return new LoadTestCertificateWarmupResult(moduleNames.Length, 0, TimeSpan.Zero);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        int completed = 0;
        int nextIndex = -1;
        int workerCount = Math.Min(moduleNames.Length, Math.Max(1, maxConcurrency));
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
                        if (currentIndex >= moduleNames.Length)
                        {
                            break;
                        }

                        LocalCertificateStore.GetOrCreateCached(moduleNames[currentIndex]);
                        int current = Interlocked.Increment(ref completed);
                        onCompleted?.Invoke(current);
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(tasks);
        stopwatch.Stop();
        return new LoadTestCertificateWarmupResult(moduleNames.Length, completed, stopwatch.Elapsed);
    }
}

internal sealed record LoadTestCertificateWarmupResult(
    int Total,
    int Completed,
    TimeSpan Elapsed);
