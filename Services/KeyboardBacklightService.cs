using HidSharp;

namespace MacTune.Services;

/// <summary>
/// Keyboard backlight on T2 MacBook Pros (MacBookPro16,1): HID device 05AC:8102, collection usage 0xFF00000F.
/// Protocol from Linux hid-apple.c (apple_magic_backlight):
///   feature report 3 = power  : [3][u8 on][u32 0x5E | rate&lt;&lt;8][pad×3]
///   feature report 1 = level  : [1][u32 brightness 0..max][u32 0x5E | rate&lt;&lt;8]
/// max brightness = logical maximum of report 1 field 0 (14660 on this machine).
/// </summary>
public sealed class KeyboardBacklightService : IDisposable
{
    const uint BacklightUsage = 0xFF00000F;
    const byte Rate = 1;

    readonly HidDevice _dev;
    readonly int _max;
    readonly object _lock = new();

    KeyboardBacklightService(HidDevice dev, int max) { _dev = dev; _max = max; }

    public static KeyboardBacklightService? TryOpen(out string? error)
    {
        if (!HardwareCompatibilityService.IsAppleSystem())
        {
            error = "không phải máy Mac Apple";
            return null;
        }
        try
        {
            foreach (var d in DeviceList.Local.GetHidDevices(0x05ac, 0x8102))
            {
                var rd = d.GetReportDescriptor();
                if (!rd.DeviceItems.Any(i => i.Usages.GetAllValues().Contains(BacklightUsage))) continue;
                var level = rd.FeatureReports.FirstOrDefault(r => r.ReportID == 1);
                var power = rd.FeatureReports.FirstOrDefault(r => r.ReportID == 3);
                if (level is null || power is null || level.DataItems.Count < 2) continue;
                int max = level.DataItems[0].LogicalMaximum;
                if (max <= 0) continue;
                error = null;
                return new KeyboardBacklightService(d, max);
            }
            error = "không tìm thấy điều khiển đèn bàn phím";
        }
        catch (Exception ex) { error = ex.Message; }
        return null;
    }

    static void PutU32(byte[] b, int at, uint v)
    {
        b[at] = (byte)v; b[at + 1] = (byte)(v >> 8); b[at + 2] = (byte)(v >> 16); b[at + 3] = (byte)(v >> 24);
    }

    /// <summary>Sets brightness as a fraction 0..1 (0 = off).</summary>
    public bool Set(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        uint value = (uint)Math.Round(fraction * _max);
        uint rateField = 0x5Eu | ((uint)Rate << 8);
        lock (_lock)
        {
            if (!_dev.TryOpen(out HidStream s)) return false;
            using (s)
            {
                var power = new byte[9];
                power[0] = 3;
                power[1] = (byte)(value > 0 ? 1 : 0);
                PutU32(power, 2, rateField);
                s.SetFeature(power);

                if (value > 0)
                {
                    var level = new byte[9];
                    level[0] = 1;
                    PutU32(level, 1, value);
                    PutU32(level, 5, rateField);
                    s.SetFeature(level);
                }
            }
        }
        return true;
    }

    /// <summary>Reads the current level (0..1) if the device reports it.</summary>
    public double? Get()
    {
        try
        {
            lock (_lock)
            {
                if (!_dev.TryOpen(out HidStream s)) return null;
                using (s)
                {
                    var power = new byte[9]; power[0] = 3;
                    s.GetFeature(power);
                    if (power[1] == 0) return 0;
                    var level = new byte[9]; level[0] = 1;
                    s.GetFeature(level);
                    uint v = BitConverter.ToUInt32(level, 1);
                    return Math.Clamp(v / (double)_max, 0, 1);
                }
            }
        }
        catch { return null; }
    }

    public void Dispose() { }
}
