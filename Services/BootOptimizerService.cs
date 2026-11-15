using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MacTune.Services;

public record BootOptState(bool SysMainOff, bool SearchTamed, bool UpdatersMuted)
{
    public int Score => (SysMainOff ? 1 : 0) + (SearchTamed ? 1 : 0) + (UpdatersMuted ? 1 : 0);
}

/// <summary>
/// Fixes 100% disk active time at Windows startup on Apple Boot Camp SSDs:
/// 1) Disables SysMain (Superfetch) 4-5 GB cold-boot preload burst
/// 2) Delays Windows Search startup + pauses indexing on battery + marks dev/cache folders NotContentIndexed
/// 3) Disables third-party updater / bloat logon scheduled tasks (OneDrive, Office, Apple, AMD, Edge/Google)
/// </summary>
public static class BootOptimizerService
{
    const string SysMainKey = @"SYSTEM\CurrentControlSet\Services\SysMain";
    const string WSearchKey = @"SYSTEM\CurrentControlSet\Services\WSearch";
    const string SearchPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\Windows Search";

    static readonly string[] UserCacheDirs =
    {
        ".gemini", ".nuget", ".vscode", ".cursor", ".cache", ".cargo", ".gradle", ".m2", ".npm", ".pnpm-store",
        @"AppData\Local\Temp", @"AppData\Local\npm-cache", @"AppData\Local\pip", @"AppData\Local\Packages",
    };

    static readonly HashSet<string> ProjectHeavyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "bin", "obj", ".vs", ".next", "dist", "target", "venv", ".venv", "__pycache__",
    };

    public static BootOptState GetState(bool savedUpdaterPref)
    {
        return new BootOptState(IsSysMainDisabled(), IsSearchOptimized(), AreLogonUpdatersMuted(savedUpdaterPref));
    }

    // ---------- 1. SysMain ----------
    public static bool IsSysMainDisabled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(SysMainKey);
            return k?.GetValue("Start") is int start && start == 4;
        }
        catch { return false; }
    }

    public static void SetSysMainDisabled(bool disable)
    {
        if (disable)
        {
            RunTool("sc.exe", "config SysMain start= disabled");
            RunTool("sc.exe", "stop SysMain");
        }
        else
        {
            RunTool("sc.exe", "config SysMain start= auto");
            RunTool("sc.exe", "start SysMain");
        }
    }

    // ---------- 2. Windows Search & Dev/Cache Exclusions ----------
    public static bool IsSearchOptimized()
    {
        try
        {
            using var ws = Registry.LocalMachine.OpenSubKey(WSearchKey);
            bool delayed = ws?.GetValue("DelayedAutostart") is int d && d == 1;
            using var pol = Registry.LocalMachine.OpenSubKey(SearchPolicyKey);
            bool battery = pol?.GetValue("PreventIndexingOnBattery") is int b && b == 1;
            return delayed && battery;
        }
        catch { return false; }
    }

    public static int SetSearchOptimized(bool optimize)
    {
        using (var ws = Registry.LocalMachine.OpenSubKey(WSearchKey, writable: true))
            ws?.SetValue("DelayedAutostart", optimize ? 1 : 0, RegistryValueKind.DWord);

        using (var pol = Registry.LocalMachine.CreateSubKey(SearchPolicyKey))
        {
            if (optimize)
            {
                pol.SetValue("PreventIndexingOnBattery", 1, RegistryValueKind.DWord);
                pol.SetValue("RespectPowerModes", 1, RegistryValueKind.DWord);
            }
            else
            {
                pol.DeleteValue("PreventIndexingOnBattery", throwOnMissingValue: false);
            }
        }

        if (!optimize) return 0;

        int marked = 0;
        try
        {
            var usersRoot = Path.GetPathRoot(Environment.SystemDirectory) is string root ? Path.Combine(root, "Users") : @"C:\Users";
            if (!Directory.Exists(usersRoot)) return 0;

            foreach (var profile in Directory.GetDirectories(usersRoot))
            {
                string name = Path.GetFileName(profile);
                if (name.Equals("Public", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Default ", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (var rel in UserCacheDirs)
                    if (MarkNotIndexed(Path.Combine(profile, rel))) marked++;

                // Scan up to depth 2 under user profile for project build/cache folders
                foreach (var topDir in SafeGetDirs(profile))
                {
                    string topName = Path.GetFileName(topDir);
                    if (topName.Equals("AppData", StringComparison.OrdinalIgnoreCase)) continue;
                    if (ProjectHeavyNames.Contains(topName)) { if (MarkNotIndexed(topDir)) marked++; continue; }

                    foreach (var subDir in SafeGetDirs(topDir))
                    {
                        string subName = Path.GetFileName(subDir);
                        if (ProjectHeavyNames.Contains(subName))
                        {
                            if (MarkNotIndexed(subDir)) marked++;
                        }
                        else
                        {
                            foreach (var d3 in SafeGetDirs(subDir))
                                if (ProjectHeavyNames.Contains(Path.GetFileName(d3)) && MarkNotIndexed(d3))
                                    marked++;
                        }
                    }
                }
            }
        }
        catch { }
        return marked;
    }

    static string[] SafeGetDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return Array.Empty<string>(); }
    }

    static bool MarkNotIndexed(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var attr = File.GetAttributes(dir);
            if ((attr & FileAttributes.ReparsePoint) != 0) return false;
            if ((attr & FileAttributes.NotContentIndexed) == 0)
                File.SetAttributes(dir, attr | FileAttributes.NotContentIndexed);
            return true;
        }
        catch { return false; }
    }

    // ---------- 3. Logon Updater & Bloat Tasks ----------
    static bool IsTargetLogonTask(string fullTaskName)
    {
        return fullTaskName.Equals(@"\Apple\AppleSoftwareUpdate", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.Equals(@"\AMDLinkUpdate", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.Equals(@"\StartDVR", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.Equals(@"\Microsoft\Office\Office Feature Updates Logon", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.Equals(@"\Microsoft\Office\Office Startup Maintenance", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.StartsWith(@"\OneDrive Startup Task", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.StartsWith(@"\OneDrive Per-Machine Standalone Update", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.StartsWith(@"\OneDrive Reporting Task", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.StartsWith(@"\GoogleUpdateTaskMachineCore", StringComparison.OrdinalIgnoreCase)
            || fullTaskName.StartsWith(@"\MicrosoftEdgeUpdateTaskMachineCore", StringComparison.OrdinalIgnoreCase);
    }

    static List<(string Name, bool Disabled)> FindTargetTasks()
    {
        var result = new List<(string, bool)>();
        try
        {
            string csv = RunToolCapture("schtasks.exe", "/Query /FO CSV /NH");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in csv.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith('"')) continue;
                var parts = SplitCsv(line);
                if (parts.Count < 3) continue;
                string taskName = parts[0];
                if (!IsTargetLogonTask(taskName) || !seen.Add(taskName)) continue;
                bool disabled = parts[2].Contains("Disabled", StringComparison.OrdinalIgnoreCase);
                result.Add((taskName, disabled));
            }
        }
        catch { }
        return result;
    }

    public static bool AreLogonUpdatersMuted(bool fallback)
    {
        var tasks = FindTargetTasks();
        return tasks.Count == 0 ? fallback : tasks.All(t => t.Disabled);
    }

    public static int SetLogonUpdatersMuted(bool mute)
    {
        var tasks = FindTargetTasks();
        int count = 0;
        string flag = mute ? "/Disable" : "/Enable";
        foreach (var (name, _) in tasks)
        {
            if (RunTool("schtasks.exe", $"/Change /TN \"{name}\" {flag}") == 0)
                count++;
        }
        return count;
    }

    // ---------- Helpers ----------
    static List<string> SplitCsv(string line)
    {
        var cols = new List<string>();
        bool inQuotes = false;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
            {
                cols.Add(line[start..i].Trim('"'));
                start = i + 1;
            }
        }
        cols.Add(line[start..].Trim('"'));
        return cols;
    }

    static int RunTool(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            p.WaitForExit(6000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static string RunToolCapture(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(6000);
        return output;
    }
}
