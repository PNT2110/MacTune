using System.IO;

namespace MacTune.Services;

public enum FanMode { Auto, Curve, Fixed }

public interface IFanBackend
{
    bool IsHealthy { get; }
    IReadOnlyList<FanInfo> Fans();
    bool SetFanRpm(int index, double rpm);
    void AllFansAuto();
    bool RevalidateAfterResume();
}

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
/// Drives fans from the selected mode on each tick, with multi-layer safety rules:
///  - always clamps to each fan's own min/max (done in SmcService)
///  - CPU/GPU ≥ 95°C → fans forced to 100% max until it drops below 85°C
///  - UI/telemetry hang → FanSafetyWatchdog forces Apple Auto on an independent OS thread
///  - lost SMC driver / missing temp / 0 RPM → immediate fallback to Apple Auto
///  - system Sleep (Suspend) → immediate handoff to Apple Auto; Wake (Resume) → driver revalidation &amp; safe restore
///  - crash marker file → next app start restores Apple Auto if previous process died abruptly
/// </summary>
public sealed class FanController
{
    static readonly string DefaultMarker = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacTune", "fans-manual.flag");

    readonly IFanBackend _smc;
    readonly string _markerPath;
    readonly Func<DateTime> _clock;
    readonly object _sync = new();

    bool _overheat;
    bool _isSuspended;
    FanMode? _modeBeforeSuspend;
    FanCurve? _curveBeforeSuspend;
    double _fixedBeforeSuspend;

    DateTime _engagedAt;
    bool _verified;
    double? _smooth;
    double? _filteredTemp;
    int _zeroRpmStrikes;

    public FanMode Mode { get; private set; } = FanMode.Auto;
    public FanCurve Curve { get; private set; } = FanCurve.All[1];
    public double FixedPercent { get; private set; } = 0.5;
    public string Status { get; private set; } = "";
    public bool IsSuspended { get { lock (_sync) return _isSuspended; } }

    public FanController(IFanBackend smc, string? customMarkerPath = null, Func<DateTime>? clock = null)
    {
        _smc = smc;
        _markerPath = customMarkerPath ?? DefaultMarker;
        _clock = clock ?? (() => DateTime.Now);

        if (File.Exists(_markerPath))
        {
            // previous session did not exit cleanly while fans were manual
            try { _smc.AllFansAuto(); } catch { }
            TryDelete();
            Status = "Đã trả quạt về chế độ tự động của Apple sau lần thoát không đúng cách trước.";
        }
    }

    public void SetAuto()
    {
        lock (_sync)
        {
            SetAutoInternal("Tự động (Apple)");
        }
    }

    void SetAutoInternal(string reason)
    {
        Mode = FanMode.Auto;
        _overheat = false;
        _zeroRpmStrikes = 0;
        try { _smc.AllFansAuto(); } catch { }
        TryDelete();
        Status = reason;
    }

    public void SetCurve(FanCurve c)
    {
        lock (_sync)
        {
            Curve = c;
            EngageInternal(FanMode.Curve);
        }
    }

    public void SetFixed(double pct)
    {
        lock (_sync)
        {
            FixedPercent = Math.Clamp(pct, 0, 1);
            EngageInternal(FanMode.Fixed);
        }
    }

    void EngageInternal(FanMode m)
    {
        if (_isSuspended)
        {
            _modeBeforeSuspend = m;
            return;
        }
        if (Mode == FanMode.Auto)
        {
            _engagedAt = _clock();
            _verified = false;
            _smooth = null;
            _filteredTemp = null;
            _zeroRpmStrikes = 0;
        }
        Mode = m;
        try
        {
            string? dir = Path.GetDirectoryName(_markerPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_markerPath, _clock().ToString("O"));
        }
        catch { }
    }

    /// <summary>Called by FanSafetyWatchdog from an independent thread if the UI/telemetry loop hangs.</summary>
    public bool EmergencyWatchdogTrip(string reason)
    {
        lock (_sync)
        {
            if (Mode == FanMode.Auto) return false;
            DiagLog.Write("FAN WATCHDOG TRIP: " + reason);
            SetAutoInternal(reason);
            return true;
        }
    }

    /// <summary>Called immediately when Windows enters Sleep / Modern Standby.</summary>
    public void OnSystemSuspend()
    {
        lock (_sync)
        {
            if (_isSuspended) return;
            _isSuspended = true;
            _modeBeforeSuspend = Mode;
            _curveBeforeSuspend = Curve;
            _fixedBeforeSuspend = FixedPercent;
            if (Mode != FanMode.Auto)
            {
                DiagLog.Write($"FanController suspend: handing fans back to Apple Auto (was {Mode})");
                SetAutoInternal("Máy chuyển sang Ngủ – đã trả quạt về Apple tự động.");
            }
        }
    }

    /// <summary>Called when Windows resumes from Sleep; revalidates SMC before restoring manual mode.</summary>
    public bool OnSystemResume()
    {
        lock (_sync)
        {
            _isSuspended = false;
            _overheat = false;
            _smooth = null;
            _filteredTemp = null;
            _zeroRpmStrikes = 0;

            var wantMode = _modeBeforeSuspend;
            _modeBeforeSuspend = null;

            bool driverOk = false;
            try { driverOk = _smc.RevalidateAfterResume(); } catch { }

            if (!driverOk)
            {
                SetAutoInternal("Thức dậy sau Ngủ: mất kết nối SMC – giữ quạt Apple tự động.");
                DiagLog.Write("FanController resume: SMC driver unhealthy after wake; keeping Auto.");
                return false;
            }

            if (wantMode is FanMode.Curve && _curveBeforeSuspend is not null)
            {
                Curve = _curveBeforeSuspend;
                EngageInternal(FanMode.Curve);
                Status = $"Đã khôi phục chế độ quạt {Curve.Name} sau khi thức dậy.";
            }
            else if (wantMode is FanMode.Fixed)
            {
                FixedPercent = _fixedBeforeSuspend;
                EngageInternal(FanMode.Fixed);
                Status = "Đã khôi phục tốc độ quạt sau khi thức dậy.";
            }
            return true;
        }
    }

    /// <summary>Call every ~2 s with the hottest CPU/GPU temperature on the shared heatpipe.</summary>
    public void Tick(double? cpuTemp)
    {
        lock (_sync)
        {
            if (_isSuspended || Mode == FanMode.Auto) return;

            if (cpuTemp is not double rawTemp || double.IsNaN(rawTemp) || double.IsInfinity(rawTemp) || rawTemp <= 0)
            {
                SetAutoInternal("Mất tín hiệu nhiệt độ CPU – đã trả quạt về tự động để an toàn.");
                return;
            }

            if (!_smc.IsHealthy)
            {
                SetAutoInternal("Mất kết nối driver SMC – đã trả quạt về tự động.");
                return;
            }

            IReadOnlyList<FanInfo> fans;
            try { fans = _smc.Fans(); }
            catch
            {
                SetAutoInternal("Lỗi đọc trạng thái quạt SMC – đã trả về tự động.");
                return;
            }

            var controllable = fans.Where(f => f.CanControl).ToList();
            if (controllable.Count == 0)
            {
                SetAutoInternal("Không còn quạt cho phép điều khiển – đã trả về tự động.");
                return;
            }

            if (rawTemp >= 95) _overheat = true;
            else if (rawTemp < 85) _overheat = false;

            // Thermal inertia EMA so 1–2s Turbo spikes don't make the fans surge audibly (bypassed near overheat)
            double curveTemp = rawTemp >= 92 || _filteredTemp is null
                ? rawTemp
                : _filteredTemp.Value + (rawTemp - _filteredTemp.Value) * (rawTemp > _filteredTemp.Value ? 0.35 : 0.20);
            _filteredTemp = curveTemp;

            double want = _overheat ? 1.0 : Mode == FanMode.Curve ? Curve.Percent(curveTemp) : FixedPercent;

            // Smooth RPM changes: gentle ramp up (30%/tick), slow ramp down (12%/tick), ignore <2% jitter
            if (Mode == FanMode.Curve && !_overheat && _smooth is double prev)
            {
                double k = want > prev ? 0.30 : 0.12;
                want = Math.Abs(want - prev) < 0.02 ? prev : prev + (want - prev) * k;
            }
            _smooth = want;
            double pct = want;

            int ok = 0;
            foreach (var f in controllable)
            {
                double lo = Math.Max(f.Min, 1000);
                if (_smc.SetFanRpm(f.Index, lo + (f.Max - lo) * pct)) ok++;
            }

            if (ok == 0)
            {
                SetAutoInternal("SMC từ chối lệnh ghi quạt – đã trả về tự động.");
                return;
            }

            // Detect stalled / dead tachometer (0 RPM reported consecutively while manual target >= 1000 RPM)
            if (controllable.Any(f => f.Actual <= 50))
            {
                if (++_zeroRpmStrikes >= 2)
                {
                    SetAutoInternal("Cảnh báo: cảm biến quạt báo 0 RPM – đã trả về Apple tự động.");
                    return;
                }
            }
            else _zeroRpmStrikes = 0;

            // Safety: 20 s after engaging, the fans must actually be near their targets
            if (!_verified && _clock() - _engagedAt > TimeSpan.FromSeconds(20))
            {
                var now = _smc.Fans().Where(f => f.CanControl).ToList();
                bool responding = now.Any(f => f.Target > 0 && Math.Abs(f.Actual - f.Target) < 700);
                DiagLog.Write("fan check: " + string.Join("; ", now.Select(f => $"fan{f.Index} actual={f.Actual:0} target={f.Target:0} md={(f.Manual ? 1 : 0)}")));
                if (!responding)
                {
                    SetAutoInternal("Quạt không đáp ứng lệnh – đã trả về Apple tự động.");
                    return;
                }
                _verified = true;
            }

            Status = _overheat
                ? $"⚠ {rawTemp:0}°C · Quạt tối đa"
                : $"Quạt {pct * 100:0}% · CPU {rawTemp:0}°C";
        }
    }

    void TryDelete() { try { if (File.Exists(_markerPath)) File.Delete(_markerPath); } catch { } }
}
