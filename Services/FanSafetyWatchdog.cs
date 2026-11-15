namespace MacTune.Services;

/// <summary>
/// Independent high-priority kernel-thread watchdog that monitors the main telemetry/UI heartbeat.
/// If the WPF Dispatcher or sensor loop hangs while fans are in manual mode, this thread bypasses
/// the frozen UI thread and forces all fans back to Apple's automatic SMC thermal controller.
/// </summary>
public sealed class FanSafetyWatchdog : IDisposable
{
    readonly FanController _fans;
    readonly TimeSpan _stallTimeout;
    readonly TimeSpan _pollInterval;
    readonly ManualResetEventSlim _stopEvent = new(false);
    readonly Thread _thread;

    long _lastHeartbeatTicks;
    int _tripCount;
    bool _disposed;

    public int TripCount => Volatile.Read(ref _tripCount);
    public event Action<string>? Tripped;

    public FanSafetyWatchdog(FanController fans, TimeSpan? stallTimeout = null, TimeSpan? pollInterval = null)
    {
        _fans = fans;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(8);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

        _thread = new Thread(WatchLoop)
        {
            IsBackground = true,
            Name = "MacTune-FanSafetyWatchdog",
            Priority = ThreadPriority.Highest,
        };
        _thread.Start();
    }

    /// <summary>Called on every healthy telemetry tick.</summary>
    public void Heartbeat() =>
        Interlocked.Exchange(ref _lastHeartbeatTicks, DateTime.UtcNow.Ticks);

    void WatchLoop()
    {
        while (!_stopEvent.Wait(_pollInterval))
        {
            try
            {
                if (_fans.Mode == FanMode.Auto || _fans.IsSuspended)
                {
                    // Keep fresh while in Auto/Sleep so engaging manual starts with a clean window
                    Heartbeat();
                    continue;
                }

                long last = Interlocked.Read(ref _lastHeartbeatTicks);
                var elapsed = new TimeSpan(DateTime.UtcNow.Ticks - last);
                if (elapsed > _stallTimeout)
                {
                    const string msg = "Luồng chính không phản hồi quá hạn – Watchdog đã trả quạt về Apple tự động.";
                    if (_fans.EmergencyWatchdogTrip(msg))
                    {
                        Interlocked.Increment(ref _tripCount);
                        try { Tripped?.Invoke(msg); } catch { }
                    }
                    Heartbeat();
                }
            }
            catch { /* watchdog thread must never terminate */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stopEvent.Set();
        try { _thread.Join(500); } catch { }
        _stopEvent.Dispose();
    }
}
