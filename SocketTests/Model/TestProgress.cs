using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace SocketTests.Model;

internal sealed class TestProgress : IDisposable
{
    private readonly string testName;
    private readonly TextWriter writer;
    private readonly Stopwatch totalElapsed = Stopwatch.StartNew();
    private readonly Stopwatch stepElapsed = Stopwatch.StartNew();
    private int step;
    private bool disposed;

    private TestProgress(string testName, TextWriter writer)
    {
        this.testName = testName;
        this.writer = writer;
        this.Write("START", "test started");
    }

    public static TestProgress Start(string testName, TextWriter? writer = null)
    {
        return new TestProgress(testName, writer ?? Console.Out);
    }

    public void Step(string message)
    {
        this.Write($"STEP {Interlocked.Increment(ref this.step)}", message);
        this.stepElapsed.Restart();
    }

    public async Task<T> WaitAsync<T>(string operation, Task<T> task, TimeSpan timeout)
    {
        this.Step($"{operation} waiting timeout={timeout}");
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            this.Write("TIMEOUT", $"{operation} exceeded {timeout}");
            Assert.Fail($"{operation} timed out after {timeout}.");
        }

        T result = await task;
        this.Write("DONE", $"{operation} completed");
        return result;
    }

    public async Task WaitAsync(string operation, Task task, TimeSpan timeout)
    {
        this.Step($"{operation} waiting timeout={timeout}");
        Task completed = await Task.WhenAny(task, Task.Delay(timeout));
        if (completed != task)
        {
            this.Write("TIMEOUT", $"{operation} exceeded {timeout}");
            Assert.Fail($"{operation} timed out after {timeout}.");
        }

        await task;
        this.Write("DONE", $"{operation} completed");
    }

    public void Dispose()
    {
        if (this.disposed)
        {
            return;
        }

        this.disposed = true;
        this.totalElapsed.Stop();
        this.stepElapsed.Stop();
        this.Write("END", "test completed");
    }

    private void Write(string marker, string message)
    {
        this.writer.WriteLine(
            $"[test-progress] {DateTimeOffset.UtcNow:O} {this.testName} {marker} " +
            $"total={this.totalElapsed.Elapsed} since-last={this.stepElapsed.Elapsed} " +
            $"thread={Environment.CurrentManagedThreadId} {message}");
    }
}
