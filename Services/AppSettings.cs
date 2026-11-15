using System.IO;
using System.Text.Json;

namespace MacTune.Services;

public class AppRule
{
    public string Process { get; set; } = "";      // e.g. "eldenring" (without .exe)
    public string Profile { get; set; } = "performance";
    public string? FanCurve { get; set; } = "cool"; // null = keep current fan mode
}

/// <summary>Persistent settings in %ProgramData%\MacTune\settings.json.</summary>
public class AppSettings
{
    // Fans
    public string FanMode { get; set; } = "Auto";   // Auto | Curve | Fixed
    public string FanCurve { get; set; } = "balanced";
    public double FanFixedPct { get; set; } = 0.5;

    // Window & Input
    public string Language { get; set; } = "vi";   // vi | en
    public bool TrayOnClose { get; set; } = true;
    public bool Topmost { get; set; }
    public bool TrayShowsTemp { get; set; } = true;
    public double KeyboardBacklight { get; set; } = 0.5;
    public bool SwapCommandCtrl { get; set; }
    public bool MuteLogonUpdaters { get; set; }

    // Battery alerts
    public bool ChargeAlert { get; set; } = true;
    public int ChargeAlertAt { get; set; } = 80;
    public bool LowAlert { get; set; } = true;
    public int LowAlertAt { get; set; } = 20;

    // Auto switching
    public bool AutoSwitchPower { get; set; }
    public string AcProfile { get; set; } = "performance";
    public string DcProfile { get; set; } = "balanced";
    public bool AppRulesEnabled { get; set; } = true;
    public List<AppRule> AppRules { get; set; } = new();

    // Learned average battery drain (W) per profile key, for runtime estimates
    public Dictionary<string, double> DrainW { get; set; } = new();
    public Dictionary<string, int> DrainSamples { get; set; } = new();

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacTune");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* corrupt file → defaults */ }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* non-fatal */ }
    }
}
