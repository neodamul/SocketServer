using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using SocketCommon.Model;

namespace SocketCommon.Diagnostics;

public class ResourceUsageProvider
{
    private CpuSample previousCpuSample;
    private double? previousMemoryUsagePercent;

    public ResourceUsageProvider()
    {
        this.previousCpuSample = CaptureCpuSample();
    }

    public ResourceUsageSnapshot Capture(string storagePath = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CpuSample cpuSample = CaptureCpuSample();
        double cpuPercent = 0;
        if (cpuSample.IsValid)
        {
            cpuPercent = this.previousCpuSample.IsValid
                ? CalculateCpuUsagePercent(this.previousCpuSample, cpuSample)
                : 0;
            this.previousCpuSample = cpuSample;
        }

        return new ResourceUsageSnapshot
        {
            CpuUsagePercent = ClampPercent(cpuPercent),
            MemoryUsagePercent = this.NormalizeMemoryUsagePercent(GetSystemMemoryUsagePercent()),
            StorageUsagePercent = ClampPercent(GetStorageUsagePercent(storagePath)),
            CapturedAt = now
        };
    }

    internal double NormalizeMemoryUsagePercent(double memoryUsagePercent)
    {
        double capturedMemoryUsagePercent = ClampPercent(memoryUsagePercent);
        if (capturedMemoryUsagePercent > 0)
        {
            this.previousMemoryUsagePercent = capturedMemoryUsagePercent;
            return capturedMemoryUsagePercent;
        }

        return this.previousMemoryUsagePercent ?? capturedMemoryUsagePercent;
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
        int result = host_statistics64(MachHostPort.Value, HostCpuLoadInfo, cpuInfo, ref count);
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

    internal static double CalculateCpuUsagePercent(CpuSample previous, CpuSample current)
    {
        if (!previous.IsValid || !current.IsValid)
        {
            return 0;
        }

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

        double hostStatisticsMemoryUsagePercent = GetMacHostStatisticsMemoryUsagePercent(totalMemoryBytes);
        if (hostStatisticsMemoryUsagePercent > 0)
        {
            return hostStatisticsMemoryUsagePercent;
        }

        double sysctlMemoryUsagePercent = GetMacSysctlMemoryUsagePercent(totalMemoryBytes);
        if (sysctlMemoryUsagePercent > 0)
        {
            return sysctlMemoryUsagePercent;
        }

        return GetMacVmStatMemoryUsagePercent(totalMemoryBytes);
    }

    private static double GetMacHostStatisticsMemoryUsagePercent(long totalMemoryBytes)
    {
        int[] vmInfo = new int[HostVmInfo64Count];
        uint count = HostVmInfo64Count;
        int result = host_statistics64(MachHostPort.Value, HostVmInfo64, vmInfo, ref count);
        if (result != 0 || count <= VmSpeculativeCount)
        {
            return 0;
        }

        long pageSize = Environment.SystemPageSize;
        long availablePages =
            (uint)vmInfo[VmFreeCount] +
            (uint)vmInfo[VmInactiveCount] +
            (uint)vmInfo[VmSpeculativeCount];
        long availableBytes = availablePages * pageSize;
        return (double)(totalMemoryBytes - availableBytes) / totalMemoryBytes * 100;
    }

    private static double GetMacSysctlMemoryUsagePercent(long totalMemoryBytes)
    {
        if (!TryReadSysctlUInt("vm.page_free_count", out uint freePages) ||
            !TryReadSysctlUInt("vm.page_inactive_count", out uint inactivePages) ||
            !TryReadSysctlUInt("vm.page_speculative_count", out uint speculativePages))
        {
            return 0;
        }

        ulong availablePages = (ulong)freePages + inactivePages + speculativePages;
        ulong availableBytes = availablePages * (ulong)Environment.SystemPageSize;
        return (totalMemoryBytes - (double)availableBytes) / totalMemoryBytes * 100;
    }

    private static double GetMacVmStatMemoryUsagePercent(long totalMemoryBytes)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/vm_stat",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
            {
                return 0;
            }

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2000) || process.ExitCode != 0)
            {
                KillProcess(process);
                return 0;
            }

            string output = outputTask.GetAwaiter().GetResult();
            _ = errorTask.GetAwaiter().GetResult();
            long pageSize = ParseVmStatPageSize(output);
            long freePages = ParseVmStatPages(output, "Pages free");
            long inactivePages = ParseVmStatPages(output, "Pages inactive");
            long speculativePages = ParseVmStatPages(output, "Pages speculative");
            if (pageSize <= 0 || freePages < 0 || inactivePages < 0 || speculativePages < 0)
            {
                return 0;
            }

            long availablePages = freePages + inactivePages + speculativePages;
            double availableBytes = availablePages * (double)pageSize;
            return (totalMemoryBytes - availableBytes) / totalMemoryBytes * 100;
        }
        catch
        {
            return 0;
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    internal static long ParseVmStatPageSize(string output)
    {
        Match match = Regex.Match(output ?? string.Empty, @"page size of (?<value>\d+) bytes", RegexOptions.CultureInvariant);
        return match.Success && long.TryParse(match.Groups["value"].Value, out long parsed)
            ? parsed
            : 0;
    }

    internal static long ParseVmStatPages(string output, string label)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(label))
        {
            return -1;
        }

        foreach (string line in output.Split('\n'))
        {
            if (!line.TrimStart().StartsWith(label, StringComparison.Ordinal))
            {
                continue;
            }

            string digits = new(line.Where(char.IsDigit).ToArray());
            return long.TryParse(digits, out long parsed) ? parsed : -1;
        }

        return -1;
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

    private static bool TryReadSysctlUInt(string name, out uint value)
    {
        value = 0;
        nuint size = (nuint)sizeof(uint);
        int result = sysctlbyname(name, ref value, ref size, IntPtr.Zero, 0);
        return result == 0;
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

    internal readonly record struct CpuSample(long Idle, long Total)
    {
        internal bool IsValid => this.Total > 0 && this.Idle >= 0 && this.Idle <= this.Total;
    }

    private static readonly Lazy<int> MachHostPort = new(mach_host_self, isThreadSafe: true);

    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private const uint HostVmInfo64Count = 62;
    private const int CpuStateUser = 0;
    private const int CpuStateSystem = 1;
    private const int CpuStateIdle = 2;
    private const int CpuStateNice = 3;
    private const uint CpuStateCount = 4;
    private const int VmFreeCount = 0;
    private const int VmInactiveCount = 2;
    private const int VmSpeculativeCount = 23;

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
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ref long oldp,
        ref nuint oldlenp,
        IntPtr newp,
        nuint newlen);

    [DllImport("libSystem.dylib")]
    private static extern int sysctlbyname(
        [MarshalAs(UnmanagedType.LPStr)] string name,
        ref uint oldp,
        ref nuint oldlenp,
        IntPtr newp,
        nuint newlen);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
