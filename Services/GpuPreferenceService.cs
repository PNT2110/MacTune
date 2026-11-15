using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MacTune.Services;

public record GpuPref(string ExePath, int Mode)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(ExePath);
    public string ModeText => Mode switch { 1 => "GPU Intel (tiết kiệm)", 2 => "GPU AMD (mạnh)", _ => "Windows tự chọn" };
}

/// <summary>
/// Per-app GPU choice – the same registry Windows Settings › Display › Graphics uses,
/// plus live GPU VRAM per-process monitoring via Windows GPU Process Memory counters.
/// </summary>
public static class GpuPreferenceService
{
    const string Key = @"Software\Microsoft\DirectX\UserGpuPreferences";

    static readonly HashSet<string> SystemGpuIgnored = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "csrss", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost",
        "TextInputHost", "Widgets", "WidgetService", "explorer", "SystemSettings",
        "ApplicationFrameHost", "LockApp", "MacTune", "RadeonSoftware", "AMDRSSrcExt",
        "atieclxx", "amdow", "SmartScreen", "RuntimeBroker", "sihost", "ctfmon",
    };

    static readonly Regex PidRegex = new(@"^pid_(\d+)_", RegexOptions.Compiled);

    public static List<GpuPref> List()
    {
        var list = new List<GpuPref>();
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        if (k is null) return list;
        foreach (var name in k.GetValueNames())
        {
            if (k.GetValue(name) is not string data) continue;
            var m = Regex.Match(data, @"GpuPreference=(\d)");
            if (m.Success) list.Add(new GpuPref(name, int.Parse(m.Groups[1].Value)));
        }
        return list.OrderBy(p => p.Name).ToList();
    }

    public static void Set(string exePath, int mode)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key);
        k.SetValue(exePath, $"GpuPreference={Math.Clamp(mode, 0, 2)};", RegistryValueKind.String);
    }

    public static void Remove(string exePath)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue(exePath, throwOnMissingValue: false);
    }

    /// <summary>Returns top user processes holding dedicated GPU VRAM (&gt;= 25 MB).</summary>
    public static List<(string Name, double VramMB)> TopActiveGpuApps(int max = 3)
    {
        var byApp = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var cat = new PerformanceCounterCategory("GPU Process Memory");
            var byPid = new Dictionary<int, long>();
            foreach (var inst in cat.GetInstanceNames())
            {
                var m = PidRegex.Match(inst);
                if (!m.Success || !int.TryParse(m.Groups[1].Value, out int pid) || pid <= 4) continue;
                try
                {
                    using var c = new PerformanceCounter("GPU Process Memory", "Dedicated Usage", inst, readOnly: true);
                    long bytes = c.RawValue;
                    if (bytes > 0)
                        byPid[pid] = Math.Max(byPid.GetValueOrDefault(pid), bytes);
                }
                catch { }
            }

            foreach (var (pid, bytes) in byPid)
            {
                double mb = bytes / 1048576.0;
                if (mb < 25) continue;
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    string name = proc.ProcessName;
                    if (SystemGpuIgnored.Contains(name)) continue;
                    byApp[name] = byApp.GetValueOrDefault(name) + mb;
                }
                catch { }
            }
        }
        catch { }

        return byApp.OrderByDescending(kv => kv.Value)
                    .Take(max)
                    .Select(kv => (kv.Key, kv.Value))
                    .ToList();
    }
}
