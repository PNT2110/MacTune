namespace MacTune.Services;

[Flags]
public enum ThrottleLimitFlags
{
    None = 0,
    Thermal = 1 << 0,       // CPU core/package thermal trip or VRM thermal alert
    PowerLimit = 1 << 1,    // RAPL PL1 / PL2 / Core power limit
    CurrentLimit = 1 << 2,  // EDP / IccMax electrical current limit
    Firmware = 1 << 3,      // External BD PROCHOT, SMC low-watt adapter cap, or firmware PL1 clamp
}

public enum ThrottleCategory
{
    None = 0,
    Thermal = 1,
    PowerLimit = 2,
    CurrentLimit = 3,
    Firmware = 4,
}

public record ThrottleDiagnosis(
    bool IsThrottled,
    ThrottleCategory PrimaryCategory,
    ThrottleLimitFlags ActiveFlags,
    string Reason,
    string SourceTag,
    double? Pl1Watts = null,
    double? Pl2Watts = null);

public record ThrottleEvent(
    DateTime Start,
    DateTime End,
    string Reason,
    double PeakTemp,
    double MinClockGHz,
    bool OnBattery,
    ThrottleCategory Category = ThrottleCategory.Thermal,
    ThrottleLimitFlags Flags = ThrottleLimitFlags.None,
    string SourceTag = "Cảm biến")
{
    public string Time => Start.ToString("HH:mm:ss") + (End > Start ? $" – {End:HH:mm:ss}" : "");
    public string Duration => $"{Math.Max(2, (End - Start).TotalSeconds):0} giây";
    public string Detail => $"Nóng nhất {PeakTemp:0}°C · xung thấp nhất {MinClockGHz:0.00} GHz"
                          + (OnBattery ? " · chạy pin" : "")
                          + (string.IsNullOrEmpty(SourceTag) ? "" : $" · {SourceTag}");
}

/// <summary>
/// Multi-source CPU throttling classifier for Intel Macs:
/// - When hardware MSR flags (0x64F/0x690, 0x19C, 0x1B1, 0x610) are available via PawnIO IntelMSR,
///   cleanly distinguishes Thermal (CPU/VRM), Power Limit (PL1/PL2), Current Limit (EDP/IccMax),
///   and Firmware/Board limits (BD PROCHOT asserted below TjMax or firmware PL1 clamp).
/// - When MSR access is unavailable, falls back honestly to sensor &amp; SMC power heuristics without
///   fabricating unsupported flags (like Current Limit).
/// </summary>
public sealed class ThrottleMonitor
{
    const double DefaultBaseGHz = 2.4;
    readonly List<ThrottleEvent> _events = new();
    DateTime? _start;
    double _peak;
    double _minClock;
    string _reason = "";
    bool _battery;
    ThrottleCategory _category;
    ThrottleLimitFlags _flags;
    string _sourceTag = "";

    public IReadOnlyList<ThrottleEvent> Events => _events;
    public int TodayCount => _events.Count(e => e.Start.Date == DateTime.Today);
    public bool Active => _start is not null;
    public string? CurrentReason => _start is null ? null : _reason;
    public ThrottleDiagnosis LastDiagnosis { get; private set; } =
        new(false, ThrottleCategory.None, ThrottleLimitFlags.None, "", "Chưa có dữ liệu");

    /// <summary>Pure diagnostic classifier usable directly in unit tests and live telemetry.</summary>
    public static ThrottleDiagnosis Classify(
        double? tempC,
        double? clockMHz,
        double? loadPct,
        double? powerW,
        bool onBattery,
        bool turboOff,
        MsrLimitSample? msr = null,
        int? adapterRatedWatts = null,
        double? boardTempC = null,
        double baseGHz = DefaultBaseGHz)
    {
        bool hasMsr = msr?.Available == true;
        string src = hasMsr ? "MSR" : "Cảm biến";

        if (clockMHz is not double mhz || loadPct is not double load)
            return new(false, ThrottleCategory.None, ThrottleLimitFlags.None, "", src, msr?.Pl1Watts, msr?.Pl2Watts);

        double ghz = mhz / 1000.0;

        // Intentional user cap (Silent profile / Turbo disabled) is only overridden by severe BD PROCHOT (< 1.15 GHz) or >= 96°C overheat
        if (turboOff && ghz >= 1.15 && (tempC ?? 0) < 96)
            return new(false, ThrottleCategory.None, ThrottleLimitFlags.None, "", src, msr?.Pl1Watts, msr?.Pl2Watts);

        // ---------- Path A: Hardware MSR registers available ----------
        if (hasMsr && msr is not null)
        {
            var flags = ThrottleLimitFlags.None;
            var details = new List<string>();

            // 1. Thermal (CPU silicon thermal trip or VRM thermal alert)
            if (msr.CoreThermalActive && (tempC is null || tempC >= 88))
            {
                flags |= ThrottleLimitFlags.Thermal;
                details.Add("Quá nhiệt nhân CPU");
            }
            else if (msr.VrThermalActive)
            {
                flags |= ThrottleLimitFlags.Thermal;
                details.Add("Quá nhiệt mạch nguồn VRM");
            }

            // 2. Firmware / External BD PROCHOT / SMC PL1 Clamp
            bool externalProchot = msr.ProchotActive && !msr.CoreThermalActive && (tempC ?? 0) < 90;
            bool smcLowPl1Clamp = !turboOff && msr.Pl1Active && msr.Pl1Watts is double pl1Cap && pl1Cap <= 22.0;
            bool weakAdapterClamp = !onBattery && adapterRatedWatts is int aw && aw > 0 && aw < 60 && (ghz < baseGHz - 0.15 || msr.Pl1Active);

            if (externalProchot)
            {
                flags |= ThrottleLimitFlags.Firmware;
                details.Add("Giới hạn Firmware (tín hiệu BD PROCHOT ngoài)");
            }
            else if (smcLowPl1Clamp)
            {
                flags |= ThrottleLimitFlags.Firmware;
                details.Add($"Giới hạn Firmware SMC (khóa PL1 {msr.Pl1Watts:0}W)");
            }
            else if (weakAdapterClamp)
            {
                flags |= ThrottleLimitFlags.Firmware;
                details.Add($"Giới hạn Firmware (củ sạc {adapterRatedWatts}W thấp)");
            }

            // 3. Power Limit (PL1 / PL2 / Core Power) — when not already attributed to a tiny firmware clamp
            if (!smcLowPl1Clamp && (msr.Pl1Active || msr.Pl2Active || msr.CorePowerLimitActive))
            {
                // Ignore momentary idle power flags unless CPU is actually under meaningful load or below base clock
                if (load >= 35 || ghz < baseGHz - 0.10)
                {
                    flags |= ThrottleLimitFlags.PowerLimit;
                    if (msr.Pl1Active && msr.Pl1Watts is double w1)
                        details.Add($"Giới hạn công suất PL1 ({w1:0}W)");
                    else if (msr.Pl2Active && msr.Pl2Watts is double w2)
                        details.Add($"Giới hạn công suất PL2 ({w2:0}W)");
                    else
                        details.Add("Giới hạn công suất (Power Limit)");
                }
            }

            // 4. Current Limit (EDP / IccMax)
            if (msr.CurrentLimitEdpActive && (load >= 35 || ghz < baseGHz))
            {
                flags |= ThrottleLimitFlags.CurrentLimit;
                details.Add("Giới hạn dòng điện (EDP / IccMax)");
            }

            // Fallback safety: if MSR shows 0 flags on an unusual model yet clock crashed under heavy load
            if (flags == ThrottleLimitFlags.None && load >= 80 && ghz < baseGHz - 0.25)
            {
                if ((tempC ?? 0) >= 95)
                {
                    flags = ThrottleLimitFlags.Thermal;
                    details.Add("Quá nhiệt (gần 100°C)");
                }
                else if (ghz < 1.20 && (tempC ?? 0) < 85)
                {
                    flags = ThrottleLimitFlags.Firmware;
                    details.Add("Giới hạn Firmware (khóa xung thấp)");
                }
            }

            if (flags != ThrottleLimitFlags.None)
            {
                var primary = PickPrimary(flags);
                return new ThrottleDiagnosis(true, primary, flags, string.Join(" + ", details), "MSR", msr.Pl1Watts, msr.Pl2Watts);
            }

            return new ThrottleDiagnosis(false, ThrottleCategory.None, ThrottleLimitFlags.None, "", "MSR", msr.Pl1Watts, msr.Pl2Watts);
        }

        // ---------- Path B: Sensor & SMC heuristic fallback (no MSR driver) ----------
        bool clockDropped = load >= 75 && ghz < baseGHz - 0.15 && !turboOff;
        bool severePin = load >= 45 && ghz < 1.20 && (tempC ?? 0) < 85; // classic 0.8 GHz BD PROCHOT even at moderate load

        if (!clockDropped && !severePin)
            return new(false, ThrottleCategory.None, ThrottleLimitFlags.None, "", "Cảm biến");

        if ((tempC ?? 0) >= 95)
            return new(true, ThrottleCategory.Thermal, ThrottleLimitFlags.Thermal, "Quá nhiệt (gần 100°C)", "Cảm biến");

        if ((boardTempC ?? 0) >= 90)
            return new(true, ThrottleCategory.Thermal, ThrottleLimitFlags.Thermal, "Quá nhiệt mạch nguồn / bo mạch", "Cảm biến SMC");

        if (severePin)
            return new(true, ThrottleCategory.Firmware, ThrottleLimitFlags.Firmware, "Giới hạn Firmware (BD PROCHOT khóa xung thấp)", "Cảm biến");

        if (!onBattery && adapterRatedWatts is int rated && rated > 0 && rated < 60)
            return new(true, ThrottleCategory.Firmware, ThrottleLimitFlags.Firmware, $"Giới hạn Firmware (củ sạc {rated}W không đủ tải)", "Cảm biến SMC");

        if (onBattery)
            return new(true, ThrottleCategory.PowerLimit, ThrottleLimitFlags.PowerLimit, "Giới hạn công suất khi chạy pin", "Cảm biến");

        if (powerW is double w && w < 30)
            return new(true, ThrottleCategory.Firmware, ThrottleLimitFlags.Firmware, "Giới hạn Firmware / công suất thấp (<30W)", "Cảm biến");

        return new(true, ThrottleCategory.PowerLimit, ThrottleLimitFlags.PowerLimit, "Giới hạn công suất (Power Limit)", "Cảm biến");
    }

    static ThrottleCategory PickPrimary(ThrottleLimitFlags flags)
    {
        if (flags.HasFlag(ThrottleLimitFlags.Thermal)) return ThrottleCategory.Thermal;
        if (flags.HasFlag(ThrottleLimitFlags.Firmware)) return ThrottleCategory.Firmware;
        if (flags.HasFlag(ThrottleLimitFlags.PowerLimit)) return ThrottleCategory.PowerLimit;
        if (flags.HasFlag(ThrottleLimitFlags.CurrentLimit)) return ThrottleCategory.CurrentLimit;
        return ThrottleCategory.None;
    }

    public void Sample(
        double? tempC,
        double? clockMHz,
        double? loadPct,
        double? powerW,
        bool onBattery,
        bool turboOff,
        MsrLimitSample? msr = null,
        int? adapterRatedWatts = null,
        double? boardTempC = null)
    {
        var diag = Classify(tempC, clockMHz, loadPct, powerW, onBattery, turboOff, msr, adapterRatedWatts, boardTempC);
        LastDiagnosis = diag;

        if (diag.IsThrottled && clockMHz is double mhz)
        {
            double ghz = mhz / 1000.0;
            if (_start is null)
            {
                _start = DateTime.Now;
                _peak = 0;
                _minClock = 99;
                _reason = diag.Reason;
                _battery = onBattery;
                _category = diag.PrimaryCategory;
                _flags = diag.ActiveFlags;
                _sourceTag = diag.SourceTag;
            }
            _peak = Math.Max(_peak, tempC ?? 0);
            _minClock = Math.Min(_minClock, ghz);
            _flags |= diag.ActiveFlags;
            // Thermal or Firmware escalations supersede milder initial Power/Current labels
            if (diag.PrimaryCategory is ThrottleCategory.Thermal or ThrottleCategory.Firmware &&
                _category is not ThrottleCategory.Thermal)
            {
                _category = diag.PrimaryCategory;
                _reason = diag.Reason;
            }
        }
        else if (_start is DateTime s)
        {
            var end = DateTime.Now;
            if ((end - s).TotalSeconds >= 4) // ignore 1-sample transient blips
            {
                _events.Insert(0, new ThrottleEvent(s, end, _reason, _peak, _minClock, _battery, _category, _flags, _sourceTag));
                if (_events.Count > 100) _events.RemoveAt(_events.Count - 1);
            }
            _start = null;
        }
    }
}
