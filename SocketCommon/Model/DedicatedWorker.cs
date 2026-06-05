using System;
using System.Threading;
using System.Threading.Tasks;

namespace SocketCommon.Model;

public static class DedicatedWorker
{
    public static Task Start(Func<CancellationToken, Task> worker, CancellationToken cancellationToken)
    {
        if (worker == null)
        {
            throw new ArgumentNullException(nameof(worker));
        }

        return Task.Factory
            .StartNew(
                () => worker(cancellationToken),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)
            .Unwrap();
    }

    public static Task[] StartMany(
        int workerCount,
        Func<CancellationToken, Task> worker,
        CancellationToken cancellationToken)
    {
        int normalizedWorkerCount = Math.Max(1, workerCount);
        Task[] workers = new Task[normalizedWorkerCount];
        for (int index = 0; index < normalizedWorkerCount; index++)
        {
            workers[index] = Start(worker, cancellationToken);
        }

        return workers;
    }
}
