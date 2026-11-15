namespace MacTune.Services;

/// <summary>One-click presets: a MacTune-owned power plan + CPU settings (same for AC and battery).</summary>
public record Profile(string Key, string Name, string Icon, string Description,
    string Plan, int Boost, int Max, int Min, int Epp)
{
    public static readonly Profile[] All =
    {
        new("silent", "Yên lặng", "", "Mát · pin lâu",
            PowerService.MtSilent, Boost: 0, Max: 100, Min: 5, Epp: 80),
        new("balanced", "Cân bằng", "", "Hằng ngày",
            PowerService.MtBalanced, Boost: 3, Max: 100, Min: 5, Epp: 50),
        new("performance", "Hiệu năng", "", "Game · render",
            PowerService.MtPerformance, Boost: 2, Max: 100, Min: 50, Epp: 0),
    };

    public void Apply()
    {
        PowerService.EnsureAndActivate(Plan, Name);
        PowerService.Set(PowerService.Boost, Boost, Boost, apply: false);
        PowerService.Set(PowerService.MaxState, Max, Max, apply: false);
        PowerService.Set(PowerService.MinState, Min, Min, apply: false);
        // EPP / core parking may not exist on every CPU – skip quietly
        try { PowerService.Set(PowerService.Epp, Epp, Epp, apply: false); } catch (InvalidOperationException) { }
        PowerService.Run("/setactive SCHEME_CURRENT");
    }

    /// <summary>Active MacTune profile, or null when a Windows/custom plan is in use.</summary>
    public static Profile? Detect(string plan, int boostAc) => All.FirstOrDefault(p => p.Plan == plan);
}
