using System.Globalization;
using System.IO;
using System.Text;

namespace MacTune.Services;

public record CpuLogEntry(
    DateTime Timestamp,
    string Profile,
    bool OnBattery,
    double? CpuTempC,
    double? CpuClockMHz,
    double? CpuPowerW,
    double? CpuLoadPct,
    double? GpuTempC = null,
    double? GpuPowerW = null,
    double? SystemPowerW = null,
    double? FanLeftRpm = null,
    double? FanRightRpm = null,
    string ThrottleCategory = "",
    string ThrottleReason = "");

public record CpuLogSessionSummary(
    bool IsRecording,
    string? FilePath,
    int SampleCount,
    TimeSpan Elapsed,
    double? AvgTempC,
    double? MaxTempC,
    double? MinClockGHz,
    double? AvgClockGHz,
    double? MaxClockGHz,
    double? AvgPowerW,
    double? MaxPowerW,
    double? AvgLoadPct,
    int ThrottledSamples);

/// <summary>
/// Streams CPU temperature, clock speed, package power, load, fan RPMs, and throttle diagnostics
/// to a UTF-8 CSV file for side-by-side before/after comparison.
/// </summary>
public sealed class CpuLoggerService : IDisposable
{
    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MacTune", "Logs");

    readonly object _lock = new();
    StreamWriter? _writer;
    DateTime _startedAt;
    DateTime _lastSampleAt = DateTime.MinValue;
    string? _currentFilePath;

    int _samples;
    int _tempCount, _clockCount, _powerCount, _loadCount, _throttledSamples;
    double _sumTemp, _maxTemp;
    double _sumClockGHz, _minClockGHz = double.MaxValue, _maxClockGHz;
    double _sumPower, _maxPower;
    double _sumLoad;

    public int IntervalSeconds { get; set; } = 2;
    public bool IsRecording { get { lock (_lock) return _writer is not null; } }
    public string? LastFilePath { get { lock (_lock) return _currentFilePath; } }

    public string Start(string? customDirectory = null, string? sessionTag = null)
    {
        lock (_lock)
        {
            StopInternal();
            string dir = string.IsNullOrWhiteSpace(customDirectory) ? DefaultLogDirectory : customDirectory;
            Directory.CreateDirectory(dir);

            string safeTag = string.IsNullOrWhiteSpace(sessionTag)
                ? ""
                : "-" + string.Concat(sessionTag.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ' ' ? '_' : c));
            string fileName = $"cpu-log-{DateTime.Now:yyyyMMdd-HHmmss}{safeTag}.csv";
            string fullPath = Path.Combine(dir, fileName);

            var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _writer.WriteLine("Timestamp,ElapsedSec,Profile,PowerSource,CpuTempC,CpuClockMHz,CpuPowerW,CpuLoadPct,GpuTempC,GpuPowerW,SystemPowerW,FanLeftRpm,FanRightRpm,ThrottleCategory,ThrottleReason");
            _writer.Flush();

            _startedAt = DateTime.Now;
            _lastSampleAt = DateTime.MinValue;
            _currentFilePath = fullPath;
            ResetCounters();
            return fullPath;
        }
    }

    public bool RecordSample(CpuLogEntry e, bool ignoreInterval = false)
    {
        lock (_lock)
        {
            if (_writer is null) return false;
            if (!ignoreInterval && _lastSampleAt != DateTime.MinValue &&
                (e.Timestamp - _lastSampleAt).TotalSeconds < IntervalSeconds - 0.25)
                return false;

            _lastSampleAt = e.Timestamp;
            double elapsed = Math.Max(0, (e.Timestamp - _startedAt).TotalSeconds);

            string line = string.Join(",",
                e.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                Fmt(elapsed, "0.0"),
                Csv(e.Profile),
                e.OnBattery ? "Battery" : "AC",
                Fmt(e.CpuTempC, "0.1"),
                Fmt(e.CpuClockMHz, "0"),
                Fmt(e.CpuPowerW, "0.2"),
                Fmt(e.CpuLoadPct, "0.1"),
                Fmt(e.GpuTempC, "0.1"),
                Fmt(e.GpuPowerW, "0.2"),
                Fmt(e.SystemPowerW, "0.2"),
                Fmt(e.FanLeftRpm, "0"),
                Fmt(e.FanRightRpm, "0"),
                Csv(e.ThrottleCategory),
                Csv(e.ThrottleReason));

            _writer.WriteLine(line);
            _writer.Flush();

            _samples++;
            if (e.CpuTempC is double t and > 0)
            {
                _tempCount++;
                _sumTemp += t;
                if (t > _maxTemp) _maxTemp = t;
            }
            if (e.CpuClockMHz is double mhz and > 0)
            {
                double ghz = mhz / 1000.0;
                _clockCount++;
                _sumClockGHz += ghz;
                if (ghz < _minClockGHz) _minClockGHz = ghz;
                if (ghz > _maxClockGHz) _maxClockGHz = ghz;
            }
            if (e.CpuPowerW is double w and >= 0)
            {
                _powerCount++;
                _sumPower += w;
                if (w > _maxPower) _maxPower = w;
            }
            if (e.CpuLoadPct is double l and >= 0)
            {
                _loadCount++;
                _sumLoad += l;
            }
            if (!string.IsNullOrEmpty(e.ThrottleCategory) && !e.ThrottleCategory.Equals("None", StringComparison.OrdinalIgnoreCase))
                _throttledSamples++;

            return true;
        }
    }

    public CpuLogSessionSummary GetSummary()
    {
        lock (_lock)
        {
            bool rec = _writer is not null;
            TimeSpan elapsed = _samples > 0 || rec
                ? ((_writer is not null ? DateTime.Now : _lastSampleAt) - _startedAt)
                : TimeSpan.Zero;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;

            return new CpuLogSessionSummary(
                IsRecording: rec,
                FilePath: _currentFilePath,
                SampleCount: _samples,
                Elapsed: elapsed,
                AvgTempC: _tempCount > 0 ? _sumTemp / _tempCount : null,
                MaxTempC: _tempCount > 0 ? _maxTemp : null,
                MinClockGHz: _clockCount > 0 ? _minClockGHz : null,
                AvgClockGHz: _clockCount > 0 ? _sumClockGHz / _clockCount : null,
                MaxClockGHz: _clockCount > 0 ? _maxClockGHz : null,
                AvgPowerW: _powerCount > 0 ? _sumPower / _powerCount : null,
                MaxPowerW: _powerCount > 0 ? _maxPower : null,
                AvgLoadPct: _loadCount > 0 ? _sumLoad / _loadCount : null,
                ThrottledSamples: _throttledSamples);
        }
    }

    public string? Stop()
    {
        lock (_lock)
        {
            string? path = _currentFilePath;
            StopInternal();
            return path;
        }
    }

    void StopInternal()
    {
        try { _writer?.Flush(); _writer?.Dispose(); } catch { }
        _writer = null;
    }

    void ResetCounters()
    {
        _samples = _tempCount = _clockCount = _powerCount = _loadCount = _throttledSamples = 0;
        _sumTemp = _maxTemp = _sumClockGHz = _maxClockGHz = _sumPower = _maxPower = _sumLoad = 0;
        _minClockGHz = double.MaxValue;
    }

    static string Fmt(double? v, string fmt) =>
        v is double d && !double.IsNaN(d) && !double.IsInfinity(d) ? d.ToString(fmt, CultureInfo.InvariantCulture) : "";

    static string Csv(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public void Dispose()
    {
        lock (_lock) StopInternal();
    }
}
