using System.Diagnostics;
using System.Management;

namespace MacTune.Services;

public record ProcInfo(int Pid, string Name, double CpuPercent, double MemoryMB);

public static class SystemService
{
    // ---------- Display brightness (WMI) ----------
    public static int? GetBrightness()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (ManagementObject o in s.Get()) return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return null;
    }

    public static void SetBrightness(int percent)
    {
        using var s = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
        foreach (ManagementObject o in s.Get())
            o.InvokeMethod("WmiSetBrightness", new object[] { (uint)1, (byte)Math.Clamp(percent, 0, 100) });
    }

    // ---------- Top processes by CPU ----------
    static Dictionary<int, TimeSpan> _lastCpu = new();
    static DateTime _lastSample = DateTime.UtcNow;

    public static List<ProcInfo> TopProcesses(int count = 8)
    {
        var now = DateTime.UtcNow;
        double elapsedMs = Math.Max(1, (now - _lastSample).TotalMilliseconds) * Environment.ProcessorCount;
        var current = new Dictionary<int, TimeSpan>();
        var list = new List<ProcInfo>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == 0) continue;
                var t = p.TotalProcessorTime;
                current[p.Id] = t;
                double cpu = _lastCpu.TryGetValue(p.Id, out var prev) ? (t - prev).TotalMilliseconds / elapsedMs * 100 : 0;
                list.Add(new ProcInfo(p.Id, p.ProcessName, cpu, p.WorkingSet64 / 1048576.0));
            }
            catch { /* access denied or exited */ }
            finally { p.Dispose(); }
        }
        _lastCpu = current;
        _lastSample = now;
        return list.OrderByDescending(x => x.CpuPercent).ThenByDescending(x => x.MemoryMB).Take(count).ToList();
    }

    public static void Kill(int pid)
    {
        using var p = Process.GetProcessById(pid);
        p.Kill(entireProcessTree: true);
    }

    // ---------- Start with Windows (scheduled task, needed because app requires admin) ----------
    const string TaskName = "MacTune";

    public static bool IsStartupEnabled() => LaunchService.Schtasks($"/Query /TN {TaskName}") == 0;

    public static void SetStartup(bool enable)
    {
        if (enable)
        {
            string exe = Environment.ProcessPath!;
            if (!LaunchService.CreateElevatedTask(TaskName, exe, "--tray", onLogon: true, allowParallel: false))
                throw new InvalidOperationException("Không tạo được tác vụ khởi động.");
        }
        else LaunchService.Schtasks($"/Delete /F /TN {TaskName}");
    }

    public static void RefreshStartupTaskIfEnabled()
    {
        if (IsStartupEnabled())
        {
            try { SetStartup(true); } catch { }
        }
    }

    // ---------- Machine info ----------
    public static string MachineSummary()
    {
        try
        {
            string model = "", cpu = "", ram = "";
            using (var s = new ManagementObjectSearcher("SELECT Model,TotalPhysicalMemory FROM Win32_ComputerSystem"))
                foreach (ManagementObject o in s.Get())
                { model = $"{o["Model"]}"; ram = $"{Convert.ToDouble(o["TotalPhysicalMemory"]) / 1073741824:0} GB RAM"; }
            using (var s = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor"))
                foreach (ManagementObject o in s.Get()) cpu = $"{o["Name"]}".Replace("(R)", "").Replace("(TM)", "").Replace("CPU ", "");
            string friendly = model == "MacBookPro16,1" ? "MacBook Pro 16\" 2019" : model;
            return $"{friendly}  ·  {cpu.Trim()}  ·  {ram}";
        }
        catch { return ""; }
    }
}
