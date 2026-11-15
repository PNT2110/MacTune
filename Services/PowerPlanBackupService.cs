using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MacTune.Services;

public record PlanSettingEntry(string Subgroup, string Alias, int Ac, int Dc);

public record PlanSnapshotItem(
    string Guid,
    string Name,
    bool IsMacTunePlan,
    string? PowFileName,
    List<PlanSettingEntry> Settings);

public record PowerBackupManifest(
    string Id,
    string Label,
    DateTime CreatedAt,
    string ActivePlanGuid,
    string ActivePlanName,
    bool HibernateEnabled,
    List<PlanSnapshotItem> Plans);

/// <summary>
/// Captures full Windows power-plan state (.pow binary exports + per-plan AC/DC indices + active scheme GUID)
/// automatically before MacTune makes any power-plan change, and supports manual snapshot &amp; 1-click restore.
/// </summary>
public static class PowerPlanBackupService
{
    static readonly (string Sub, string Alias)[] TrackedSettings =
    {
        ("SUB_PROCESSOR", PowerService.Boost),
        ("SUB_PROCESSOR", PowerService.MaxState),
        ("SUB_PROCESSOR", PowerService.MinState),
        ("SUB_PROCESSOR", PowerService.Epp),
        ("SUB_PROCESSOR", PowerService.MinCores),
        ("SUB_VIDEO", "VIDEOIDLE"),
        ("SUB_SLEEP", "STANDBYIDLE"),
        ("SUB_BUTTONS", "LIDACTION"),
        ("SUB_SLEEP", "HIBERNATEIDLE"),
        ("SUB_SLEEP", "RTCWAKE"),
    };

    static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    static readonly object Sync = new();
    static bool _baselineChecked;

    /// <summary>Can be overridden in unit tests to isolate filesystem state.</summary>
    public static string? RootDirectoryOverride { get; set; }

    public static string BackupRoot => RootDirectoryOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacTune", "Backups");

    static string BaselineManifestPath => Path.Combine(BackupRoot, "baseline", "manifest.json");
    static string ManualManifestPath => Path.Combine(BackupRoot, "manual", "manifest.json");

    public static bool HasBaselineBackup => File.Exists(BaselineManifestPath);
    public static bool HasManualBackup => File.Exists(ManualManifestPath);

    /// <summary>Automatically called by PowerService before any mutating powercfg command.</summary>
    public static void EnsureBaselineBeforeChange()
    {
        lock (Sync)
        {
            if (_baselineChecked && File.Exists(BaselineManifestPath)) return;
            _baselineChecked = true;
            if (File.Exists(BaselineManifestPath)) return;

            try
            {
                CaptureToFolder(Path.Combine(BackupRoot, "baseline"), "baseline", "Nguyên trạng trước khi thay đổi");
                DiagLog.Write("PowerPlanBackupService: captured automatic baseline power plan backup.");
            }
            catch (Exception ex)
            {
                DiagLog.Write("PowerPlanBackupService baseline failed: " + ex.Message);
            }
        }
    }

    public static PowerBackupManifest CreateManualBackup()
    {
        lock (Sync)
        {
            return CaptureToFolder(Path.Combine(BackupRoot, "manual"), "manual", "Bản sao lưu thủ công");
        }
    }

    public static PowerBackupManifest? GetBaselineInfo() => ReadManifest(BaselineManifestPath);
    public static PowerBackupManifest? GetManualInfo() => ReadManifest(ManualManifestPath);

    static PowerBackupManifest? ReadManifest(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<PowerBackupManifest>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    public static List<(string Guid, string Name, bool Active)> ListSystemPlans()
    {
        var result = new List<(string, string, bool)>();
        string output = PowerService.Run("/l");
        foreach (var rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            var m = Regex.Match(line, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\s*\(([^)]*)\)(\s*\*)?");
            if (m.Success)
            {
                string guid = m.Groups[1].Value.ToLowerInvariant();
                string name = m.Groups[2].Value.Trim();
                bool active = m.Groups[3].Success;
                result.Add((guid, name, active));
            }
        }
        return result;
    }

    public static bool IsMacTuneGuid(string guid) =>
        guid.Equals(PowerService.MtSilent, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(PowerService.MtBalanced, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(PowerService.MtPerformance, StringComparison.OrdinalIgnoreCase);

    static bool IsInboxWindowsGuid(string guid) =>
        guid.Equals(PowerService.Balanced, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(PowerService.HighPerf, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(PowerService.PowerSaver, StringComparison.OrdinalIgnoreCase);

    static PowerBackupManifest CaptureToFolder(string folder, string id, string label)
    {
        Directory.CreateDirectory(folder);
        var plans = ListSystemPlans();
        string activeGuid = plans.FirstOrDefault(p => p.Active).Guid ?? PowerService.ActivePlanGuid();
        string activeName = plans.FirstOrDefault(p => p.Guid == activeGuid).Name ?? "Balanced";
        bool hib = PowerService.HibernationAvailable();

        var items = new List<PlanSnapshotItem>();
        foreach (var (guid, name, _) in plans)
        {
            string powFile = $"{guid}.pow";
            string powFull = Path.Combine(folder, powFile);
            try { File.Delete(powFull); } catch { }

            string? savedPow = null;
            try
            {
                PowerService.Run($"/export \"{powFull}\" {guid}");
                if (File.Exists(powFull)) savedPow = powFile;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"PowerPlanBackup export {guid} warning: {ex.Message}");
            }

            var settings = new List<PlanSettingEntry>();
            foreach (var (sub, alias) in TrackedSettings)
            {
                var (ac, dc) = PowerService.QuerySchemeAcDc(guid, sub, alias);
                if (ac >= 0 || dc >= 0)
                    settings.Add(new PlanSettingEntry(sub, alias, ac, dc));
            }

            items.Add(new PlanSnapshotItem(guid, name, IsMacTuneGuid(guid), savedPow, settings));
        }

        var manifest = new PowerBackupManifest(id, label, DateTime.Now, activeGuid, activeName, hib, items);
        string manifestFile = Path.Combine(folder, "manifest.json");
        string tmp = manifestFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, JsonOpts));
        File.Move(tmp, manifestFile, overwrite: true);
        return manifest;
    }

    public static string RestoreBaseline(bool removeMacTunePlansIfAbsent = true) =>
        RestoreFromFolder(Path.Combine(BackupRoot, "baseline"), removeMacTunePlansIfAbsent);

    public static string RestoreManual() =>
        RestoreFromFolder(Path.Combine(BackupRoot, "manual"), removeMacTunePlansIfAbsent: false);

    static string RestoreFromFolder(string folder, bool removeMacTunePlansIfAbsent)
    {
        lock (Sync)
        {
            string manifestPath = Path.Combine(folder, "manifest.json");
            var manifest = ReadManifest(manifestPath)
                ?? throw new InvalidOperationException("Chưa có bản sao lưu power plan để khôi phục.");

            var existing = ListSystemPlans().Select(p => p.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Restore each backed-up plan (.pow import when missing or replaceable, plus exact AC/DC setting indices)
            foreach (var plan in manifest.Plans)
            {
                string? powPath = plan.PowFileName is not null ? Path.Combine(folder, plan.PowFileName) : null;
                bool hasPow = powPath is not null && File.Exists(powPath);

                if (!existing.Contains(plan.Guid) && hasPow)
                {
                    try
                    {
                        PowerService.Run($"/import \"{powPath}\" {plan.Guid}");
                        existing.Add(plan.Guid);
                    }
                    catch { }
                }
                else if (existing.Contains(plan.Guid) && hasPow && !IsInboxWindowsGuid(plan.Guid) &&
                         !string.Equals(PowerService.ActivePlanGuid(), plan.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    // Non-inbox inactive scheme: replace cleanly from .pow binary snapshot
                    try
                    {
                        PowerService.Run($"/delete {plan.Guid}");
                        PowerService.Run($"/import \"{powPath}\" {plan.Guid}");
                    }
                    catch { }
                }

                if (existing.Contains(plan.Guid))
                {
                    foreach (var s in plan.Settings)
                    {
                        try
                        {
                            if (s.Ac >= 0) PowerService.Run($"/setacvalueindex {plan.Guid} {s.Subgroup} {s.Alias} {s.Ac}");
                            if (s.Dc >= 0) PowerService.Run($"/setdcvalueindex {plan.Guid} {s.Subgroup} {s.Alias} {s.Dc}");
                        }
                        catch { }
                    }
                }
            }

            // Pick target active scheme (prefer backed-up active scheme; if user wants clean non-MacTune baseline, fall back to Balanced)
            string targetGuid = manifest.ActivePlanGuid;
            if (removeMacTunePlansIfAbsent && IsMacTuneGuid(targetGuid) && existing.Contains(PowerService.Balanced))
                targetGuid = PowerService.Balanced;

            if (!string.IsNullOrEmpty(targetGuid) && existing.Contains(targetGuid))
                PowerService.Run($"/setactive {targetGuid}");
            else if (existing.Contains(PowerService.Balanced))
            {
                targetGuid = PowerService.Balanced;
                PowerService.Run($"/setactive {targetGuid}");
            }

            // Remove MacTune-created plans that were not in the baseline if requested
            if (removeMacTunePlansIfAbsent)
            {
                var backedGuids = manifest.Plans.Select(p => p.Guid).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var mt in new[] { PowerService.MtSilent, PowerService.MtBalanced, PowerService.MtPerformance })
                {
                    if (!string.Equals(mt, targetGuid, StringComparison.OrdinalIgnoreCase) &&
                        (!backedGuids.Contains(mt) || !IsMacTuneGuid(manifest.ActivePlanGuid)))
                    {
                        try { PowerService.Run($"/delete {mt}"); } catch { }
                    }
                }
            }

            try
            {
                if (manifest.HibernateEnabled != PowerService.HibernationAvailable())
                    PowerService.Run(manifest.HibernateEnabled ? "/hibernate on" : "/hibernate off");
            }
            catch { }

            var restoredName = ListSystemPlans().FirstOrDefault(p => p.Guid.Equals(targetGuid, StringComparison.OrdinalIgnoreCase)).Name
                               ?? manifest.ActivePlanName;
            return restoredName;
        }
    }
}
