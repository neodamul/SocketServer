using System;
using System.Diagnostics;
using System.IO;
using SocketCommon.Model;

namespace SocketCommon.Diagnostics;

public class ResourceUsageProvider
{
    private readonly Process process;
    private TimeSpan previousProcessorTime;
    private DateTimeOffset previousSampleAt;

    public ResourceUsageProvider()
    {
        this.process = Process.GetCurrentProcess();
        this.previousProcessorTime = this.process.TotalProcessorTime;
        this.previousSampleAt = DateTimeOffset.UtcNow;
    }

    public ResourceUsageSnapshot Capture(string storagePath = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        this.process.Refresh();

        TimeSpan processorTime = this.process.TotalProcessorTime;
        double elapsedMilliseconds = Math.Max(1, (now - this.previousSampleAt).TotalMilliseconds);
        double cpuMilliseconds = Math.Max(0, (processorTime - this.previousProcessorTime).TotalMilliseconds);
        double cpuPercent = cpuMilliseconds / (elapsedMilliseconds * Math.Max(1, Environment.ProcessorCount)) * 100;

        this.previousProcessorTime = processorTime;
        this.previousSampleAt = now;

        return new ResourceUsageSnapshot
        {
            CpuUsagePercent = ClampPercent(cpuPercent),
            MemoryUsagePercent = ClampPercent(GetMemoryUsagePercent()),
            StorageUsagePercent = ClampPercent(GetStorageUsagePercent(storagePath)),
            CapturedAt = now
        };
    }

    private double GetMemoryUsagePercent()
    {
        GCMemoryInfo memoryInfo = GC.GetGCMemoryInfo();
        long totalAvailableMemoryBytes = memoryInfo.TotalAvailableMemoryBytes;
        if (totalAvailableMemoryBytes <= 0)
        {
            return 0;
        }

        return (double)this.process.WorkingSet64 / totalAvailableMemoryBytes * 100;
    }

    private static double GetStorageUsagePercent(string storagePath)
    {
        string path = string.IsNullOrWhiteSpace(storagePath)
            ? AppContext.BaseDirectory
            : storagePath;
        DriveInfo drive = new(Path.GetPathRoot(Path.GetFullPath(path)));
        if (drive.TotalSize <= 0)
        {
            return 0;
        }

        return (double)(drive.TotalSize - drive.AvailableFreeSpace) / drive.TotalSize * 100;
    }

    private static double ClampPercent(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            return 0;
        }

        return value > 100 ? 100 : value;
    }
}
