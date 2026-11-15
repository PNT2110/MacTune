using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MacTune.Services;

/// <summary>Reads and changes Windows power settings through powercfg.exe.</summary>
public static class PowerService
{
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerf = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";

    // Processor setting aliases understood by powercfg
    public const string Boost = "PERFBOOSTMODE";     // 0 off,1 on,2 aggressive,3 eff. on,4 eff. aggressive
    public const string MaxState = "PROCTHROTTLEMAX"; // %
    public const string MinState = "PROCTHROTTLEMIN"; // %
    public const string Epp = "PERFEPP";              // 0 = performance … 100 = power saving
    public const string MinCores = "CPMINCORES";      // % of cores kept unparked

    public static string Run(string args)
    {
        var psi = new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"powercfg {args} thất bại: {output.Trim()}");
        return output;
    }

    public static string ActivePlanGuid()
    {
        var m = Regex.Match(Run("/getactivescheme"), @"[0-9a-fA-F-]{36}");
        return m.Success ? m.Value.ToLowerInvariant() : "";
    }

    // MacTune-owned plans (created from Balanced on first use; this machine has no Power saver plan)
    public const string MtSilent = "6d61630a-0001-4d54-9000-000000000001";
    public const string MtBalanced = "6d61630a-0002-4d54-9000-000000000002";
    public const string MtPerformance = "6d61630a-0003-4d54-9000-000000000003";

    static readonly HashSet<string> _unhidden = new();

    static bool PlanExists(string guid) => Run("/l").Contains(guid, StringComparison.OrdinalIgnoreCase);

    public static void EnsureAndActivate(string guid, string name)
    {
        PowerPlanBackupService.EnsureBaselineBeforeChange();
        if (!PlanExists(guid))
        {
            string basePlan = ActivePlanGuid();
            if (string.IsNullOrEmpty(basePlan)) basePlan = Balanced;
            Run($"/duplicatescheme {basePlan} {guid}");
            Run($"/changename {guid} \"MacTune - {name}\" \"Tạo bởi MacTune\"");
        }
        Run($"/setactive {guid}");
    }

    static void SetAcrossPlans(string sub, string alias, int ac, int dc)
    {
        PowerPlanBackupService.EnsureBaselineBeforeChange();
        string list = Run("/l");
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string active = ActivePlanGuid();
        if (!string.IsNullOrEmpty(active)) targets.Add(active);
        foreach (var mt in new[] { MtSilent, MtBalanced, MtPerformance })
            if (list.Contains(mt, StringComparison.OrdinalIgnoreCase))
                targets.Add(mt);
        if (targets.Count == 0) targets.Add("SCHEME_CURRENT");

        foreach (var scheme in targets)
        {
            Run($"/setacvalueindex {scheme} {sub} {alias} {ac}");
            Run($"/setdcvalueindex {scheme} {sub} {alias} {dc}");
        }
        Run("/setactive SCHEME_CURRENT");
    }

    /// <summary>Reads (AC, DC) setting index on the specified scheme (including hidden settings, independent of Windows display language).</summary>
    public static (int Ac, int Dc) QuerySchemeAcDc(string scheme, string sub, string alias)
    {
        try
        {
            string text = Run($"/qh {scheme} {sub} {alias}");
            int Parse(string label)
            {
                var m = Regex.Match(text, label + @"[^:]*:\s*0x([0-9a-fA-F]+)");
                return m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;
            }
            var r = (Parse("Current AC Power Setting Index"), Parse("Current DC Power Setting Index"));
            if (r.Item1 >= 0 && r.Item2 >= 0) return r;

            // Localized Windows fallback: AC and DC current indices are always the last two 8-digit hex values
            var hex = Regex.Matches(text, @":\s*0x([0-9a-fA-F]{8})\b");
            if (hex.Count >= 2)
                return (Convert.ToInt32(hex[^2].Groups[1].Value, 16), Convert.ToInt32(hex[^1].Groups[1].Value, 16));
            return (-1, -1);
        }
        catch (InvalidOperationException) { return (-1, -1); }
    }

    static (int Ac, int Dc) QueryAcDc(string sub, string alias) => QuerySchemeAcDc("SCHEME_CURRENT", sub, alias);

    static void Unhide(string alias)
    {
        if (!_unhidden.Add(alias)) return;
        try { Run($"/attributes SUB_PROCESSOR {alias} -ATTRIB_HIDE"); } catch (InvalidOperationException) { }
    }

    /// <summary>Returns (AC, DC) value of a processor setting on the active plan, or (-1,-1) if unavailable.</summary>
    public static (int Ac, int Dc) Get(string alias) => QueryAcDc("SUB_PROCESSOR", alias);

    public static void Set(string alias, int ac, int dc, bool apply = true)
    {
        PowerPlanBackupService.EnsureBaselineBeforeChange();
        Unhide(alias);
        Run($"/setacvalueindex SCHEME_CURRENT SUB_PROCESSOR {alias} {ac}");
        Run($"/setdcvalueindex SCHEME_CURRENT SUB_PROCESSOR {alias} {dc}");
        if (apply) Run("/setactive SCHEME_CURRENT");
    }

    // ---------- Screen-off / sleep timers (minutes, 0 = never) ----------
    static (int Ac, int Dc) GetSeconds(string sub, string alias) => QueryAcDc(sub, alias);

    public static (int AcMin, int DcMin) GetScreenOff()
    {
        var (a, d) = GetSeconds("SUB_VIDEO", "VIDEOIDLE");
        return (a < 0 ? -1 : a / 60, d < 0 ? -1 : d / 60);
    }

    public static (int AcMin, int DcMin) GetSleep()
    {
        var (a, d) = GetSeconds("SUB_SLEEP", "STANDBYIDLE");
        return (a < 0 ? -1 : a / 60, d < 0 ? -1 : d / 60);
    }

    public static void SetScreenOff(int acMin, int dcMin) =>
        SetAcrossPlans("SUB_VIDEO", "VIDEOIDLE", acMin * 60, dcMin * 60);

    public static void SetSleep(int acMin, int dcMin) =>
        SetAcrossPlans("SUB_SLEEP", "STANDBYIDLE", acMin * 60, dcMin * 60);

    // ---------- Lid action & hibernate-after-sleep ----------
    /// <summary>0 = do nothing, 1 = sleep, 2 = hibernate, 3 = shut down.</summary>
    public static (int Ac, int Dc) GetLidAction() => GetSeconds("SUB_BUTTONS", "LIDACTION");

    public static void SetLidAction(int ac, int dc) =>
        SetAcrossPlans("SUB_BUTTONS", "LIDACTION", ac, dc);

    /// <summary>Minutes of sleep before switching to hibernate (0 = never).</summary>
    public static (int AcMin, int DcMin) GetHibernateAfter()
    {
        var (a, d) = GetSeconds("SUB_SLEEP", "HIBERNATEIDLE");
        return (a < 0 ? -1 : a / 60, d < 0 ? -1 : d / 60);
    }

    public static void SetHibernateAfter(int acMin, int dcMin)
    {
        if (acMin > 0 || dcMin > 0) EnsureHibernation();
        SetAcrossPlans("SUB_SLEEP", "HIBERNATEIDLE", acMin * 60, dcMin * 60);
    }

    /// <summary>0 = disable wake timers (prevents midnight wake in closed lid/bag), 1 = enable.</summary>
    public static void SetWakeTimers(int ac, int dc) =>
        SetAcrossPlans("SUB_SLEEP", "RTCWAKE", ac, dc);

    public static bool HibernationAvailable()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power");
            if (k?.GetValue("HibernateEnabled") is int he) return he != 0;
            return !Run("/a").Contains("Hibernation has not been enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    static void EnsureHibernation()
    {
        if (!HibernationAvailable()) Run("/hibernate on");
    }

    /// <summary>Generates Windows' detailed battery report and returns its path.</summary>
    public static string BatteryReport()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MacTune-battery-report.html");
        Run($"/batteryreport /output \"{path}\"");
        return path;
    }
}
