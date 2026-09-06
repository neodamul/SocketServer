using System.Diagnostics;

namespace SocketLoadTest;

internal interface IAdmissionClock
{
    long GetTimestamp();

    TimeSpan GetElapsed(long startTimestamp, long endTimestamp);
}

internal sealed class StopwatchAdmissionClock : IAdmissionClock
{
    public static StopwatchAdmissionClock Instance { get; } = new();

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public TimeSpan GetElapsed(long startTimestamp, long endTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp, endTimestamp);
}

internal sealed class ActiveAdmissionStopwatch
{
    private readonly object gate = new();
    private readonly IAdmissionClock clock;
    private long activeStartedAt;
    private TimeSpan accumulated;
    private bool isStarted;
    private bool isRunning;

    public ActiveAdmissionStopwatch(IAdmissionClock clock)
    {
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public void Start()
    {
        lock (this.gate)
        {
            if (this.isStarted)
            {
                throw new InvalidOperationException("Admission stopwatch has already started.");
            }

            this.isStarted = true;
            this.isRunning = true;
            this.activeStartedAt = this.clock.GetTimestamp();
        }
    }

    public void Pause()
    {
        lock (this.gate)
        {
            if (!this.isStarted || !this.isRunning)
            {
                return;
            }

            this.accumulated += this.clock.GetElapsed(this.activeStartedAt, this.clock.GetTimestamp());
            this.isRunning = false;
        }
    }

    public void Resume()
    {
        lock (this.gate)
        {
            if (!this.isStarted)
            {
                throw new InvalidOperationException("Admission stopwatch has not started.");
            }

            if (this.isRunning)
            {
                return;
            }

            this.activeStartedAt = this.clock.GetTimestamp();
            this.isRunning = true;
        }
    }

    public TimeSpan Elapsed
    {
        get
        {
            lock (this.gate)
            {
                if (!this.isStarted)
                {
                    return TimeSpan.Zero;
                }

                return this.isRunning
                    ? this.accumulated + this.clock.GetElapsed(this.activeStartedAt, this.clock.GetTimestamp())
                    : this.accumulated;
            }
        }
    }
}

internal static class AdmissionBatchRunner
{
    public static async Task<TResult> RunAsync<TResult>(
        ActiveAdmissionStopwatch admissionStopwatch,
        IReadOnlyCollection<int> certificateWarmupClientIds,
        Func<IReadOnlyCollection<int>, CancellationToken, Task> warmup,
        Func<Task<TResult>> connect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admissionStopwatch);
        ArgumentNullException.ThrowIfNull(certificateWarmupClientIds);
        ArgumentNullException.ThrowIfNull(warmup);
        ArgumentNullException.ThrowIfNull(connect);

        admissionStopwatch.Pause();
        try
        {
            await warmup(certificateWarmupClientIds, cancellationToken);
        }
        finally
        {
            admissionStopwatch.Resume();
        }

        return await connect();
    }
}
