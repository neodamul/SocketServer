using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using SocketCommon.Model;

namespace SocketCommon.Diagnostics;

public class ResourceUsageProvider
{
    private CpuSample previousCpuSample;

    public ResourceUsageProvider()
    {
        this.previousCpuSample = CaptureCpuSample();
    }

    public ResourceUsageSnapshot Capture(string storagePath = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CpuSample cpuSample = CaptureCpuSample();
        double cpuPercent = CalculateCpuUsagePercent(this.previousCpuSample, cpuSample);
        this.previousCpuSample = cpuSample;

        return new ResourceUsageSnapshot
        {
            CpuUsagePercent = ClampPercent(cpuPercent),
            MemoryUsagePercent = ClampPercent(GetSystemMemoryUsagePercent()),
            StorageUsagePercent = ClampPercent(GetStorageUsagePercent(storagePath)),
            CapturedAt = now
        };
    }

    private static CpuSample CaptureCpuSample()
    {
        if (OperatingSystem.IsLinux())
        {
            return CaptureLinuxCpuSample();
        }

        if (OperatingSystem.IsMacOS())
        {
            return CaptureMacCpuSample();
        }

        if (OperatingSystem.IsWindows())
        {
            return CaptureWindowsCpuSample();
        }

        return new CpuSample(0, 0);
    }

    private static CpuSample CaptureLinuxCpuSample()
    {
        string firstLine = File.ReadLines("/proc/stat").FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine) || !firstLine.StartsWith("cpu ", StringComparison.Ordinal))
        {
            return new CpuSample(0, 0);
        }

        ulong[] values = firstLine
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Select(value => ulong.TryParse(value, out ulong parsed) ? parsed : 0)
            .ToArray();
        if (values.Length < 4)
        {
            return new CpuSample(0, 0);
        }

        ulong idle = values[3] + (values.Length > 4 ? values[4] : 0);
        ulong total = 0;
        foreach (ulong value in values)
        {
            total += value;
        }

        return new CpuSample((long)idle, (long)total);
    }

    private static CpuSample CaptureMacCpuSample()
    {
        int[] cpuInfo = new int[CpuStateCount];
        uint count = CpuStateCount;
        int result = host_statistics64(mach_host_self(), HostCpuLoadInfo, cpuInfo, ref count);
        if (result != 0 || count < CpuStateCount)
        {
            return new CpuSample(0, 0);
        }

        long user = (uint)cpuInfo[CpuStateUser];
        long system = (uint)cpuInfo[CpuStateSystem];
        long idle = (uint)cpuInfo[CpuStateIdle];
        long nice = (uint)cpuInfo[CpuStateNice];
        return new CpuSample(idle, user + system + idle + nice);
    }

    private static CpuSample CaptureWindowsCpuSample()
    {
        if (!GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime))
        {
            return new CpuSample(0, 0);
        }

        ulong idle = ToUInt64(idleTime);
        ulong kernel = ToUInt64(kernelTime);
        ulong user = ToUInt64(userTime);
        return new CpuSample((long)idle, (long)(kernel + user));
    }

    private static double CalculateCpuUsagePercent(CpuSample previous, CpuSample current)
    {
        long totalDelta = current.Total - previous.Total;
        if (totalDelta <= 0)
        {
            return 0;
        }

        long idleDelta = Math.Max(0, current.Idle - previous.Idle);
        return (1 - ((double)idleDelta / totalDelta)) * 100;
    }

    private static double GetSystemMemoryUsagePercent()
    {
        if (OperatingSystem.IsLinux())
        {
            return GetLinuxMemoryUsagePercent();
        }

        if (OperatingSystem.IsMacOS())
        {
            return GetMacMemoryUsagePercent();
        }

        if (OperatingSystem.IsWindows())
        {
            return GetWindowsMemoryUsagePercent();
        }

        return 0;
    }

    private static double GetLinuxMemoryUsagePercent()
    {
        long total = 0;
        long available = 0;
        foreach (string line in File.ReadLines("/proc/meminfo"))
        {
            if (line.StartsWith("MemTotal:", StringComparison.Ordinal))
            {
                total = ParseMemInfoKilobytes(line);
            }
            else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal))
            {
                available = ParseMemInfoKilobytes(line);
            }
        }

        if (total <= 0)
        {
            return 0;
        }

        return (double)(total - available) / total * 100;
    }

    private static double GetMacMemoryUsagePercent()
    {
        long totalMemoryBytes = GetMacTotalMemoryBytes();
        if (totalMemoryBytes <= 0)
        {
            return 0;
        }

        VmStatistics64 stats = new();
        uint count = (uint)(Marshal.SizeOf<VmStatistics64>() / sizeof(int));
        int result = host_statistics64(mach_host_self(), HostVmInfo64, ref stats, ref count);
        if (result != 0)
        {
            return 0;
        }

        long pageSize = Environment.SystemPageSize;
        long availableBytes = (long)(stats.FreeCount + stats.InactiveCount + stats.SpeculativeCount) * pageSize;
        return (double)(totalMemoryBytes - availableBytes) / totalMemoryBytes * 100;
    }

    private static double GetWindowsMemoryUsagePercent()
    {
        MemoryStatusEx status = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>()
        };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0)
        {
            return 0;
        }

        return (double)(status.TotalPhys - status.AvailPhys) / status.TotalPhys * 100;
    }

    private static long GetMacTotalMemoryBytes()
    {
        long totalMemoryBytes = 0;
        nuint size = (nuint)sizeof(long);
        int result = sysctlbyname("hw.memsize", ref totalMemoryBytes, ref size, IntPtr.Zero, 0);
        return result == 0 ? totalMemoryBytes : 0;
    }

    private static long ParseMemInfoKilobytes(string line)
    {
        string value = new(line.Where(char.IsDigit).ToArray());
        return long.TryParse(value, out long parsed) ? parsed : 0;
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

    private readonly record struct CpuSample(long Idle, long Total);

    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const int CpuStateUser = 0;
    private const int CpuStateSystem = 1;
    private const int CpuStateIdle = 2;
    private const int CpuStateNice = 3;
    private const uint CpuStateCount = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint FreeCount;
        public uint ActiveCount;
        public uint InactiveCount;
        public uint WireCount;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong Pageins;
        public ulong Pageouts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeableCount;
        public uint SpeculativeCount;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong Swapins;
        public ulong Swapouts;
        public uint CompressorPageCount;
        public uint ThrottledCount;
        public uint ExternalPageCount;
        public uint InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    private static ulong ToUInt64(FileTime fileTime)
    {
        return ((ulong)fileTime.HighDateTime << 32) | fileTime.LowDateTime;
    }

    [DllImport("libSystem.dylib")]
    private static extern int mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(int host, int flavor, int[] hostInfoOut, ref uint hostInfoCount);

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(int host, int flavor, ref VmStatistics64 hostInfoOut, ref uint hostInfoCount);

    [DllImport("libSystem.dylib")]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ref long oldp,
        ref nuint oldlenp,
        IntPtr newp,
        nuint newlen);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
