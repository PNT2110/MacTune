namespace MacTune.Services;

public record ThrottleEvent(DateTime Start, DateTime End, string Reason, double PeakTemp, double MinClockGHz, bool OnBattery)
{
    public string Time => Start.ToString("HH:mm:ss") + (End > Start ? $" – {End:HH:mm:ss}" : "");
    public string Duration => $"{Math.Max(2, (End - Start).TotalSeconds):0} giây";
    public string Detail => $"Nóng nhất {PeakTemp:0}°C · xung thấp nhất {MinClockGHz:0.00} GHz" + (OnBattery ? " · chạy pin" : "");
}

/// <summary>
/// Heuristic throttle detector for the i9-9980HK (base 2.4 GHz, TjMax 100°C):
/// while CPU load is high, the clock dropping below base means the CPU is being held back.
/// Reason is inferred from temperature/power/state.
/// </summary>
public sealed class ThrottleMonitor
{
    const double BaseGHz = 2.4;
    readonly List<ThrottleEvent> _events = new();
    DateTime? _start; double _peak, _minClock; string _reason = ""; bool _battery;

    public IReadOnlyList<ThrottleEvent> Events => _events;
    public int TodayCount => _events.Count(e => e.Start.Date == DateTime.Today);
    public bool Active => _start is not null;
    public string? CurrentReason => _start is null ? null : _reason;

    /// <param name="turboOff">Turbo disabled / max state below 100 – low clocks are then intentional.</param>
    public void Sample(double? tempC, double? clockMHz, double? loadPct, double? powerW, bool onBattery, bool turboOff)
    {
        if (clockMHz is not double mhz || loadPct is not double load) return;
        double ghz = mhz / 1000;
        bool throttled = load >= 75 && ghz < BaseGHz - 0.15 && !turboOff;

        if (throttled)
        {
            string reason =
                tempC >= 95 ? "Quá nhiệt (gần 100°C)" :
                onBattery ? "Giới hạn khi chạy pin" :
                powerW is double w && w < 30 ? "Bị giới hạn công suất (sạc yếu hoặc nhiệt bo mạch)" :
                "Giới hạn công suất / nhiệt hệ thống";
            if (_start is null) { _start = DateTime.Now; _peak = 0; _minClock = 99; _reason = reason; _battery = onBattery; }
            _peak = Math.Max(_peak, tempC ?? 0);
            _minClock = Math.Min(_minClock, ghz);
            if (tempC >= 95) _reason = reason; // overheating dominates
        }
        else if (_start is DateTime s)
        {
            var end = DateTime.Now;
            if ((end - s).TotalSeconds >= 4) // ignore 1-sample blips
            {
                _events.Insert(0, new ThrottleEvent(s, end, _reason, _peak, _minClock, _battery));
                if (_events.Count > 100) _events.RemoveAt(_events.Count - 1);
            }
            _start = null;
        }
    }
}
