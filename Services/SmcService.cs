using System.Buffers.Binary;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MacTune.Services;

public record SmcKeyInfo(int Size, string Type, byte Attributes)
{
    public bool Readable => (Attributes & 0x80) != 0;
    public bool Writable => (Attributes & 0x40) != 0;
}

public record FanInfo(int Index, string Name, double Actual, double Min, double Max, double Target, bool Manual, bool CanControl);
public record SmcPowerInfo(double? DcInWatts, double? SystemWatts, int? AdapterRatedWatts);

/// <summary>
/// Apple SMC access through Apple's Boot Camp MacHALDriver (\\.\MacHALDriver).
/// Protocol per SimpleMacFan; T2 fan key family (flt + per-fan F{i}Md) per BootCampPerformanceControl docs
/// (validated on MacBookPro16,1). Every write is gated by the key's reported type, size and writable attribute.
/// </summary>
public sealed class SmcService : IDisposable
{
    // Backend 1: AppleSMC driver (installed by Macs Fan Control). Validated on T2 MacBookPro16,1.
    const uint AsRead = 0x220000, AsWrite = 0x220004, AsInfo = 0x22000C;
    // Backend 2: Apple MacHALDriver (pre-T2 Macs; does not answer SMC calls on T2 machines).
    const uint MhRead = 0x9C402454, MhWrite = 0x9C402458, MhInfo = 0x9C402460;

    // Backend 3: PawnIO driver + RPMac's AppleT2Smc module (T2 MMIO SMC). Module only permits fan-key writes.
    const uint PioLoad = 0xA1B22084, PioExec = 0xA1B22104;
    public const string PawnIoSetupUrl = "https://github.com/namazso/PawnIO.Setup/releases/download/2.0.1/PawnIO_setup.exe";

    enum Kind { AppleSmc, MacHal, PawnIo }

    readonly SafeFileHandle _h;
    readonly Kind _kind;
    bool _appleSmc => _kind == Kind.AppleSmc;
    public string Backend => _kind.ToString();
    /// <summary>The T2 PawnIO module refuses every write except fan keys.</summary>
    public bool CanWriteBattery => _kind != Kind.PawnIo;
    readonly object _lock = new();
    readonly Dictionary<string, SmcKeyInfo?> _infoCache = new();

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize,
        byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

    SmcService(SafeFileHandle h, Kind kind) { _h = h; _kind = kind; }

    /// <summary>Opens the first working SMC backend; returns null with a reason when none works.</summary>
    public static SmcService? TryOpen(out string? error)
    {
        var reasons = new List<string>();
        var candidates = new[]
        {
            (@"\\?\GLOBALROOT\Device\PawnIO", Kind.PawnIo),
            (@"\\.\APPLESMC", Kind.AppleSmc),
            (@"\\.\MacHALDriver", Kind.MacHal),
        };
        foreach (var (path, kind) in candidates)
        {
            var h = CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                reasons.Add(kind switch
                {
                    Kind.PawnIo => err == 2 ? "chưa cài PawnIO" : $"PawnIO: {new Win32Exception(err).Message}",
                    Kind.AppleSmc => err == 2 ? "không có AppleSMC" : $"AppleSMC bận ({new Win32Exception(err).Message})",
                    _ => $"MacHAL: {new Win32Exception(err).Message}",
                });
                continue;
            }
            var smc = new SmcService(h, kind);
            if (kind == Kind.PawnIo && !smc.LoadT2Module(out string why))
            {
                smc.Dispose();
                reasons.Add(why);
                continue;
            }
            if (smc.Info("FNum") is not null) { error = null; return smc; }
            smc.Dispose();
            reasons.Add(kind switch
            {
                Kind.PawnIo => "module T2 không đọc được SMC",
                Kind.AppleSmc => "AppleSMC không trả lời",
                _ => "MacHAL không đọc được SMC trên máy T2",
            });
        }
        error = string.Join("; ", reasons) + ".";
        return null;
    }

    // ---------- PawnIO ----------
    bool LoadT2Module(out string why)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "drivers", "AppleT2Smc.bin");
        if (!File.Exists(path)) { why = "thiếu file drivers\\AppleT2Smc.bin"; return false; }
        byte[] blob = File.ReadAllBytes(path);
        if (Ioctl(PioLoad, blob, Array.Empty<byte>(), 0, out _)) { why = ""; return true; }
        int err = Marshal.GetLastWin32Error();
        DiagLog.Write($"PawnIO load failed: win32={err}");
        why = err == 87
            ? "PawnIO đang ở chế độ bắt buộc chữ ký – cần PawnIO 2.0.1 bản Unrestricted + bật Test Signing"
            : $"PawnIO từ chối module ({new Win32Exception(err).Message})";
        return false;
    }

    /// <summary>Calls a module function: 32-byte name + int64 cells in, int64 cells out.</summary>
    long[]? PawnExec(string fn, long[] input, int outCells)
    {
        var inBuf = new byte[32 + input.Length * 8];
        Encoding.ASCII.GetBytes(fn).CopyTo(inBuf, 0);
        for (int i = 0; i < input.Length; i++) BinaryPrimitives.WriteInt64LittleEndian(inBuf.AsSpan(32 + i * 8), input[i]);
        var outBuf = new byte[Math.Max(1, outCells) * 8];
        if (!Ioctl(PioExec, inBuf, outBuf, outCells * 8, out _)) return null;
        var r = new long[outCells];
        for (int i = 0; i < outCells; i++) r[i] = BinaryPrimitives.ReadInt64LittleEndian(outBuf.AsSpan(i * 8));
        return r;
    }

    static long KeyLE(string key)
    {
        var b = KeyBytes(key);
        return (long)BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    byte[]? PawnRead(byte cmd, string key, int len)
    {
        var r = PawnExec("ioctl_smc_read", new long[] { cmd, KeyLE(key), len }, 32);
        return r?.Take(len).Select(v => (byte)v).ToArray();
    }

    static byte[] KeyBytes(string key) => Encoding.ASCII.GetBytes(key.PadRight(4)[..4]);

    bool Ioctl(uint code, byte[] input, byte[] output, int outSize, out int returned)
    {
        lock (_lock) return DeviceIoControl(_h, code, input, input.Length, output, outSize, out returned, IntPtr.Zero);
    }

    public SmcKeyInfo? Info(string key)
    {
        lock (_lock)
        {
            if (_infoCache.TryGetValue(key, out var cached)) return cached;
            SmcKeyInfo? info = null;
            if (_kind == Kind.PawnIo)
            {
                // [len, type0..3, flags]
                var o = PawnRead(0x13, key, 6);
                if (o is not null && o[0] is > 0 and <= 32)
                {
                    string type = Encoding.ASCII.GetString(o, 1, 4);
                    if (!KnownTypes.Contains(type.TrimEnd()))
                    {
                        string rev = new string(type.Reverse().ToArray());
                        if (KnownTypes.Contains(rev.TrimEnd())) type = rev;
                    }
                    info = new SmcKeyInfo(o[0], type, o[5]);
                }
            }
            else if (_appleSmc)
            {
                // in: u32 key (chars in memory order); out: {u8 size; u32 type; u8 attr} packed
                var o = new byte[6];
                if (Ioctl(AsInfo, KeyBytes(key), o, 6, out int r) && r >= 6 && o[0] is > 0 and <= 32)
                    info = new SmcKeyInfo(o[0], Encoding.ASCII.GetString(o, 1, 4), o[5]);
            }
            else
            {
                var input = new byte[5];
                KeyBytes(key).CopyTo(input, 0);
                var o = new byte[12];
                if (Ioctl(MhInfo, input, o, 12, out _) && o[0] is > 0 and <= 32)
                    info = new SmcKeyInfo(o[0], Encoding.ASCII.GetString(o, 4, 4), o[8]);
            }
            // only trust types we know how to decode
            if (info is not null && !KnownTypes.Contains(info.Type.TrimEnd())) info = info with { Attributes = (byte)(info.Attributes & ~0x40) };
            _infoCache[key] = info;
            return info;
        }
    }

    static readonly HashSet<string> KnownTypes = new()
    {
        "flt", "fpe2", "fp88", "fp6a", "fp4c", "fp2e",
        "sp78", "sp87", "sp96", "spa5", "spb4", "spf0",
        "ui8", "ui16", "ui32", "si8", "si16", "si32", "flag",
    };

    public byte[]? ReadRaw(string key)
    {
        var info = Info(key);
        if (info is null || !info.Readable) return null;
        if (_kind == Kind.PawnIo) return PawnRead(0x10, key, info.Size);
        var output = new byte[32];
        byte[] input;
        uint code;
        if (_appleSmc) { input = new byte[5]; KeyBytes(key).CopyTo(input, 0); input[4] = (byte)info.Size; code = AsRead; }
        else { input = KeyBytes(key); code = MhRead; }
        if (!Ioctl(code, input, output, _appleSmc ? 32 : info.Size, out _)) return null;
        return output[..info.Size];
    }

    bool WriteRaw(string key, byte[] data)
    {
        if (_kind == Kind.PawnIo)
        {
            var cells = new long[34];
            cells[0] = KeyLE(key);
            cells[1] = data.Length;
            for (int i = 0; i < data.Length && i < 32; i++) cells[2 + i] = data[i];
            return PawnExec("ioctl_smc_write", cells, 0) is not null;
        }
        var input = new byte[_appleSmc ? 5 + 32 : 5 + data.Length];
        KeyBytes(key).CopyTo(input, 0);
        input[4] = (byte)data.Length;
        data.CopyTo(input, 5);
        return Ioctl(_appleSmc ? AsWrite : MhWrite, input, new byte[1], _appleSmc ? 1 : 0, out _);
    }

    public double? ReadNumber(string key)
    {
        var info = Info(key);
        var r = ReadRaw(key);
        if (info is null || r is null) return null;
        return info.Type.TrimEnd() switch
        {
            "flt" when r.Length == 4 => BinaryPrimitives.ReadSingleLittleEndian(r),
            "fpe2" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r) / 4.0,
            "fp88" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r) / 256.0,
            "fp6a" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r) / 1024.0,
            "fp4c" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r) / 4096.0,
            "fp2e" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r) / 16384.0,
            "sp78" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r) / 256.0,
            "sp87" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r) / 128.0,
            "sp96" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r) / 64.0,
            "spa5" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r) / 32.0,
            "spb4" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r) / 16.0,
            "spf0" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r),
            "ui8" or "flag" when r.Length >= 1 => r[0],
            "ui16" when r.Length == 2 => BinaryPrimitives.ReadUInt16BigEndian(r),
            "ui32" when r.Length == 4 => BinaryPrimitives.ReadUInt32BigEndian(r),
            "si8" when r.Length == 1 => (sbyte)r[0],
            "si16" when r.Length == 2 => BinaryPrimitives.ReadInt16BigEndian(r),
            "si32" when r.Length == 4 => BinaryPrimitives.ReadInt32BigEndian(r),
            _ => null,
        };
    }

    /// <summary>Encodes by the key's declared type; refuses non-writable keys or size mismatch.</summary>
    public bool WriteNumber(string key, double value)
    {
        var info = Info(key);
        if (info is null || !info.Writable) return false;
        byte[]? data = info.Type.TrimEnd() switch
        {
            "flt" => BitConverter.GetBytes((float)value),                    // little-endian on x86
            "fpe2" => BE16((ushort)Math.Round(value * 4)),
            "ui8" or "flag" => new[] { (byte)value },
            "ui16" => BE16((ushort)value),
            _ => null,
        };
        if (data is null || data.Length != info.Size) return false;
        return WriteRaw(key, data);
    }

    static byte[] BE16(ushort v) => new[] { (byte)(v >> 8), (byte)v };

    // ================= Fans =================
    public int FanCount => (int)(ReadNumber("FNum") ?? 0);

    /// <summary>T2 family: per-fan F{i}Md (ui8, writable) and flt F{i}Tg. Legacy: FS! mask + fpe2.</summary>
    bool PerFanMode(int i) => Info($"F{i}Md") is { Writable: true, Size: 1 };
    bool HasMask => Info("FS! ") is { Writable: true, Type: "ui16" };

    public IReadOnlyList<FanInfo> Fans()
    {
        var list = new List<FanInfo>();
        int n = Math.Clamp(FanCount, 0, 10);
        int mask = HasMask ? (int)(ReadNumber("FS! ") ?? 0) : 0;
        for (int i = 0; i < n; i++)
        {
            double ac = ReadNumber($"F{i}Ac") ?? 0;
            double mn = ReadNumber($"F{i}Mn") ?? 0;
            double mx = ReadNumber($"F{i}Mx") ?? 0;
            double tg = ReadNumber($"F{i}Tg") ?? 0;
            bool manual = PerFanMode(i) ? (ReadNumber($"F{i}Md") ?? 0) == 1 : (mask & (1 << i)) != 0;
            bool canControl = (PerFanMode(i) || HasMask) && Info($"F{i}Tg") is { Writable: true }
                              && mx is > 0 and <= 10000;
            string name = n == 2 ? (i == 0 ? "Quạt trái" : "Quạt phải") : $"Quạt {i + 1}";
            list.Add(new FanInfo(i, name, ac, mn, mx, tg, manual, canControl));
        }
        return list;
    }

    /// <summary>Sets a fan to manual at <paramref name="rpm"/>, clamped to the fan's own [Min, Max].</summary>
    public bool SetFanRpm(int i, double rpm)
    {
        var fan = Fans().FirstOrDefault(f => f.Index == i);
        if (fan is null || !fan.CanControl) return false;
        double target = Math.Clamp(rpm, Math.Max(fan.Min, 1000), fan.Max);

        if (PerFanMode(i))
        {
            // T2: F{i}Md may still read 0 right after the write (seen on MacBookPro16,1),
            // so – like RPMac – write mode then target without a read-back gate.
            // FanController verifies the fan actually responds via its RPM instead.
            if (!fan.Manual && !WriteNumber($"F{i}Md", 1)) { Log(i, $"write F{i}Md=1 FAILED (win32={Marshal.GetLastWin32Error()})"); return false; }
        }
        else
        {
            int mask = (int)(ReadNumber("FS! ") ?? 0) | (1 << i);
            if (!WriteNumber("FS! ", mask)) { Log(i, "write FS! FAILED"); return false; }
        }
        bool ok = WriteNumber($"F{i}Tg", target);
        if (!ok) Log(i, $"write F{i}Tg={target:0} FAILED (win32={Marshal.GetLastWin32Error()})");
        else if (Math.Abs(target - _lastLogged.GetValueOrDefault(i)) > 150)
        {
            _lastLogged[i] = target;
            Log(i, $"target {target:0} RPM ok (actual {fan.Actual:0})");
        }
        return ok;
    }

    readonly Dictionary<int, double> _lastLogged = new();
    static void Log(int fan, string msg) => DiagLog.Write($"fan{fan}: {msg}");

    /// <summary>Returns a fan to Apple's automatic control.</summary>
    public bool SetFanAuto(int i)
    {
        _lastLogged.Remove(i);
        if (PerFanMode(i)) { bool r = WriteNumber($"F{i}Md", 0); Log(i, $"auto -> {(r ? "ok" : "FAILED")}"); return r; }
        if (HasMask)
        {
            int mask = (int)(ReadNumber("FS! ") ?? 0) & ~(1 << i);
            return WriteNumber("FS! ", mask);
        }
        return false;
    }

    public void AllFansAuto()
    {
        for (int i = 0; i < Math.Clamp(FanCount, 0, 10); i++)
            try { SetFanAuto(i); } catch { /* best effort */ }
    }

    // ================= Temperatures =================
    static readonly (string Key, string Name)[] TempKeys =
    {
        ("TCXc", "CPU · nhân nóng nhất"), ("TC0P", "CPU · cạnh chip"), ("TC0D", "CPU · die"),
        ("TG0P", "GPU · cạnh chip"), ("TG0D", "GPU · die"), ("TPCD", "Chipset (PCH)"),
        ("Tm0P", "Bo mạch / RAM"), ("TB0T", "Pin"), ("TB1T", "Pin · cell 1"), ("TB2T", "Pin · cell 2"),
        ("Ts0P", "Chiếu nghỉ tay"), ("TW0P", "Wi-Fi"), ("TA0P", "Không khí trong máy"),
        ("TH0a", "SSD"), ("TaLC", "Khe gió trái"), ("TaRC", "Khe gió phải"),
    };

    public List<(string Name, float Celsius)> Temperatures()
    {
        var list = new List<(string, float)>();
        foreach (var (key, name) in TempKeys)
            if (ReadNumber(key) is double v && v is > 5 and < 130) list.Add(($"SMC · {name}", (float)v));
        return list;
    }

    public double? HottestCpu() => ReadNumber("TCXc") ?? ReadNumber("TC0P");

    // ================= Battery charge limit =================
    /// <summary>Which charge-limit mechanism this SMC exposes, if any.</summary>
    public string? ChargeLimitKey => !CanWriteBattery ? null :
        Info("BCLM") is { Writable: true, Size: 1 } ? "BCLM" :
        Info("CHWA") is { Writable: true, Size: 1 } ? "CHWA" : null;

    /// <summary>Current limit in % (100 = no limit), or null when unsupported.</summary>
    public int? ReadChargeLimit()
    {
        switch (ChargeLimitKey)
        {
            case "BCLM": return ReadNumber("BCLM") is double b and > 0 ? (int)b : null;
            case "CHWA": return (ReadNumber("CHWA") ?? 0) == 1 ? 80 : 100;
            default: return null;
        }
    }

    /// <summary>Only 80% or 100% are allowed: the values Apple firmware itself uses on T2 Macs.</summary>
    public bool SetChargeLimit(int percent)
    {
        if (percent is not (80 or 100)) return false;
        bool ok = ChargeLimitKey switch
        {
            "BCLM" => WriteNumber("BCLM", percent),
            "CHWA" => WriteNumber("CHWA", percent == 80 ? 1 : 0),
            _ => false,
        };
        return ok && ReadChargeLimit() == percent; // verify read-back
    }

    public int? CycleCount() => ReadNumber("B0CT") is double c and > 0 ? (int)c : null;

    /// <summary>Reads live Charger Input Power (PDTR / VD0R*ID0R), Total System Power (PSTR), and Adapter Rating (AC-W).</summary>
    public SmcPowerInfo ReadPower()
    {
        double? dcIn = ReadNumber("PDTR");
        if (dcIn is null && ReadNumber("VD0R") is double v and > 0 && ReadNumber("ID0R") is double a and >= 0)
            dcIn = v * a;
        if (dcIn is < 0 or > 260) dcIn = null;

        double? sys = ReadNumber("PSTR");
        if (sys is <= 0 or > 260) sys = null;

        int? rated = ReadNumber("AC-W") is double rw and >= 10 and <= 240 ? (int)Math.Round(rw) : null;
        return new SmcPowerInfo(dcIn, sys, rated);
    }

    public void Dispose() => _h.Dispose();
}
