using System.IO;

namespace MacTune.Services;

public enum FanMode { Auto, Curve, Fixed }

/// <summary>A temperature→fan-speed curve: below Low°C fans idle at min, at High°C they reach max.</summary>
public record FanCurve(string Key, string Name, string Description, double LowC, double HighC, double FloorPct)
{
    public static readonly FanCurve[] All =
    {
        new("quiet", "Êm", "Từ 70°C", 70, 95, 0),
        new("balanced", "Cân bằng", "Từ 60°C", 60, 90, 0.10),
        new("cool", "Mát tối đa", "Từ 45°C", 45, 80, 0.30),
    };

    public double Percent(double tempC)
    {
        double t = Math.Clamp((tempC - LowC) / (HighC - LowC), 0, 1);
        return Math.Max(FloorPct, t * t * (3 - 2 * t)); // smoothstep
    }
}

/// <summary>
/// Drives fans from the selected mode on each tick, with safety rules:
///  - always clamps to each fan's own min/max (done in SmcService)
///  - CPU ≥ 95°C in Fixed mode → fans forced to max until it drops below 85°C
///  - a marker file records manual control; next app start restores Apple Auto if the app crashed
///  - exit / crash / shutdown → Apple Auto
/// </summary>
public sealed class FanController
{
    static readonly string Marker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacTune", "fans-manual.flag");

    readonly SmcService _smc;
    bool _overheat;

    public FanMode Mode { get; private set; } = FanMode.Auto;
    public FanCurve Curve { get; private set; } = FanCurve.All[1];
    public double FixedPercent { get; private set; } = 0.5;
    public string Status { get; private set; } = "";

    public FanController(SmcService smc)
    {
        _smc = smc;
        if (File.Exists(Marker))
        {
            // previous session did not exit cleanly while fans were manual
            _smc.AllFansAuto();
            TryDelete();
            Status = "Đã trả quạt về chế độ tự động của Apple sau lần thoát không đúng cách trước.";
        }
    }

    public void SetAuto()
    {
        Mode = FanMode.Auto;
        _smc.AllFansAuto();
        TryDelete();
        Status = "Tự động (Apple)";
    }

    public void SetCurve(FanCurve c) { Curve = c; Engage(FanMode.Curve); }
    public void SetFixed(double pct) { FixedPercent = Math.Clamp(pct, 0, 1); Engage(FanMode.Fixed); }

    DateTime _engagedAt;
    bool _verified;
    double? _smooth;

    void Engage(FanMode m)
    {
        if (Mode == FanMode.Auto) { _engagedAt = DateTime.Now; _verified = false; _smooth = null; }
        Mode = m;
        Directory.CreateDirectory(Path.GetDirectoryName(Marker)!);
        File.WriteAllText(Marker, DateTime.Now.ToString("O"));
    }

    /// <summary>Call every ~2 s with the current CPU temperature.</summary>
    public void Tick(double? cpuTemp)
    {
        if (Mode == FanMode.Auto) return;

        if (cpuTemp is null)
        {
            SetAuto();
            Status = "Mất tín hiệu nhiệt độ CPU – đã trả quạt về tự động để an toàn.";
            return;
        }

        if (cpuTemp >= 95) _overheat = true;
        else if (cpuTemp < 85) _overheat = false;

        double want = _overheat ? 1.0 : Mode == FanMode.Curve ? Curve.Percent(cpuTemp.Value) : FixedPercent;

        // Smooth curve changes so CPU temperature spikes don't make fans surge up and down:
        // rise quickly (50% of the gap per tick), fall slowly (10%), ignore tiny changes.
        if (Mode == FanMode.Curve && !_overheat && _smooth is double prev)
        {
            double k = want > prev ? 0.5 : 0.10;
            want = Math.Abs(want - prev) < 0.02 ? prev : prev + (want - prev) * k;
        }
        _smooth = want;
        double pct = want;

        int ok = 0;
        var fans = _smc.Fans();
        foreach (var f in fans.Where(f => f.CanControl))
        {
            double lo = Math.Max(f.Min, 1000);
            if (_smc.SetFanRpm(f.Index, lo + (f.Max - lo) * pct)) ok++;
        }

        if (ok == 0)
        {
            SetAuto();
            Status = "SMC từ chối lệnh ghi quạt – đã trả về tự động.";
            return;
        }

        // Safety: 20 s after engaging, the fans must actually be near their targets
        if (!_verified && DateTime.Now - _engagedAt > TimeSpan.FromSeconds(20))
        {
            var now = _smc.Fans().Where(f => f.CanControl).ToList();
            bool responding = now.Any(f => f.Target > 0 && Math.Abs(f.Actual - f.Target) < 700);
            DiagLog.Write("fan check: " + string.Join("; ", now.Select(f => $"fan{f.Index} actual={f.Actual:0} target={f.Target:0} md={(f.Manual ? 1 : 0)}")));
            if (!responding)
            {
                SetAuto();
                Status = "Quạt không đáp ứng lệnh – đã trả về Apple tự động.";
                return;
            }
            _verified = true;
        }
        Status = _overheat
            ? $"⚠ {cpuTemp:0}°C · Quạt tối đa"
            : $"Quạt {pct * 100:0}% · CPU {cpuTemp:0}°C";
    }

    static void TryDelete() { try { File.Delete(Marker); } catch { } }
}
