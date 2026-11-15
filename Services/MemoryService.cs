using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacTune.Services;

public record MemInfo(double TotalGB, double UsedGB, double AvailableGB, double CachedGB)
{
    public double UsedPct => TotalGB > 0 ? UsedGB / TotalGB * 100 : 0;
}

/// <summary>
/// RAM status and a gentle "free memory" action:
///  1) trims every process' working set (pages go to the standby list, nothing is lost)
///  2) purges the standby list (file cache) – same as Sysinternals RAMMap "Empty Standby List".
/// Apps may feel slightly slower right after as caches warm up again.
/// </summary>
public static class MemoryService
{
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                       KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION p, uint size);
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);
    [DllImport("ntdll.dll")] static extern int NtSetSystemInformation(int infoClass, ref int info, int length);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool LookupPrivilegeValue(string? sys, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES p, int len, IntPtr prev, IntPtr ret);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TOKEN_PRIVILEGES { public int Count; public long Luid; public int Attr; }

    public static MemInfo Read()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        double cached = 0;
        if (GetPerformanceInfo(out var p, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
            cached = (double)p.SystemCache.ToUInt64() * p.PageSize.ToUInt64() / (1L << 30);
        double total = m.ullTotalPhys / (double)(1L << 30), avail = m.ullAvailPhys / (double)(1L << 30);
        return new MemInfo(total, total - avail, avail, cached);
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x0020 | 0x0008, out var token)) return;
        try
        {
            if (LookupPrivilegeValue(null, name, out long luid))
            {
                var tp = new TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attr = 0x2 };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>Returns GB of RAM that became available.</summary>
    public static double Free()
    {
        double before = Read().AvailableGB;

        foreach (var p in Process.GetProcesses())
        {
            try { if (p.Id > 4) EmptyWorkingSet(p.Handle); } catch { /* protected process */ }
            finally { p.Dispose(); }
        }

        // SystemMemoryListInformation = 80, MemoryPurgeStandbyList = 4
        EnablePrivilege("SeProfileSingleProcessPrivilege");
        int cmd = 4;
        NtSetSystemInformation(80, ref cmd, sizeof(int));

        Thread.Sleep(500);
        return Math.Max(0, Read().AvailableGB - before);
    }
}
