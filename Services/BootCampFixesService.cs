using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MacTune.Services;

public record BootCampFixState(bool AudioPopFixed, bool WirelessOptimized, bool NaturalMouseWheel, bool CanBootMacOs);

/// <summary>
/// Reversible fixes for classic Boot Camp hardware annoyances on Intel Macs:
/// 1) Apple/AMD PortCls audio 3-second D3 idle sleep (speaker pop / clipped notification start)
/// 2) Apple Broadcom Bluetooth radio power-off lag &amp; Wi-Fi packet-coalescing ping spikes
/// 3) macOS-style Natural Scrolling (FlipFlopWheel) for external USB/Bluetooth mice
/// 4) Quick launch into Apple's Startup Disk selector (macOS reboot)
/// </summary>
public static class BootCampFixesService
{
    const string AudioClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e96c-e325-11ce-bfc1-08002be10318}";
    const string BluetoothClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}";
    const string NetworkClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";
    const string HidEnumKey = @"SYSTEM\CurrentControlSet\Enum\HID";

    static readonly byte[] NeverIdle = { 0xFF, 0xFF, 0xFF, 0xFF };
    static readonly byte[] StateD0 = { 0x00, 0x00, 0x00, 0x00 };
    static readonly byte[] DefaultAppleIdle = { 0x03, 0x00, 0x00, 0x00 };

    public static BootCampFixState GetState() => new(
        IsAudioPopFixed(),
        IsWirelessOptimized(),
        IsNaturalMouseWheel(),
        CanOpenStartupDisk());

    // ---------- 1. Speaker Anti-Pop & Zero Wake Delay ----------
    static bool IsTargetAudioAdapter(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        return desc.Contains("Apple Audio", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Cirrus", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("AMD High Definition Audio", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Intel(R) Display Audio", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsAudioPopFixed()
    {
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(AudioClassKey);
            if (cls is null) return false;

            int matched = 0, fixedCount = 0;
            foreach (var sub in cls.GetSubKeyNames())
            {
                if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                using var drv = cls.OpenSubKey(sub);
                if (!IsTargetAudioAdapter(drv?.GetValue("DriverDesc") as string)) continue;
                using var ps = drv?.OpenSubKey("PowerSettings");
                if (ps?.GetValue("ConservationIdleTime") is not byte[] cons || cons.Length != 4) continue;

                matched++;
                if (cons.SequenceEqual(NeverIdle) &&
                    ps.GetValue("PerformanceIdleTime") is byte[] perf && perf.SequenceEqual(NeverIdle))
                    fixedCount++;
            }
            return matched > 0 && fixedCount == matched;
        }
        catch { return false; }
    }

    public static int SetAudioPopFixed(bool enable)
    {
        int touched = 0;
        using var cls = Registry.LocalMachine.OpenSubKey(AudioClassKey);
        if (cls is null) return 0;

        foreach (var sub in cls.GetSubKeyNames())
        {
            if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
            using var drv = cls.OpenSubKey(sub);
            if (!IsTargetAudioAdapter(drv?.GetValue("DriverDesc") as string)) continue;
            using var ps = cls.OpenSubKey($@"{sub}\PowerSettings", writable: true);
            if (ps?.GetValue("ConservationIdleTime") is not byte[] cons || cons.Length != 4) continue;

            if (enable)
            {
                if (!cons.SequenceEqual(NeverIdle) && ps.GetValue("MacTuneBackup_Conservation") is null)
                    ps.SetValue("MacTuneBackup_Conservation", cons, RegistryValueKind.Binary);
                if (ps.GetValue("PerformanceIdleTime") is byte[] perf && !perf.SequenceEqual(NeverIdle) && ps.GetValue("MacTuneBackup_Performance") is null)
                    ps.SetValue("MacTuneBackup_Performance", perf, RegistryValueKind.Binary);
                if (ps.GetValue("IdlePowerState") is byte[] idle && !idle.SequenceEqual(StateD0) && ps.GetValue("MacTuneBackup_IdleState") is null)
                    ps.SetValue("MacTuneBackup_IdleState", idle, RegistryValueKind.Binary);

                ps.SetValue("ConservationIdleTime", NeverIdle, RegistryValueKind.Binary);
                ps.SetValue("PerformanceIdleTime", NeverIdle, RegistryValueKind.Binary);
                if (ps.GetValue("IdlePowerState") is byte[] { Length: 4 })
                    ps.SetValue("IdlePowerState", StateD0, RegistryValueKind.Binary);
            }
            else
            {
                var bakCons = ps.GetValue("MacTuneBackup_Conservation") as byte[] ?? DefaultAppleIdle;
                var bakPerf = ps.GetValue("MacTuneBackup_Performance") as byte[] ?? DefaultAppleIdle;
                ps.SetValue("ConservationIdleTime", bakCons, RegistryValueKind.Binary);
                ps.SetValue("PerformanceIdleTime", bakPerf, RegistryValueKind.Binary);
                if (ps.GetValue("MacTuneBackup_IdleState") is byte[] bakIdle)
                    ps.SetValue("IdlePowerState", bakIdle, RegistryValueKind.Binary);
                else if (ps.GetValue("IdlePowerState") is byte[] { Length: 4 })
                    ps.SetValue("IdlePowerState", DefaultAppleIdle, RegistryValueKind.Binary);

                ps.DeleteValue("MacTuneBackup_Conservation", throwOnMissingValue: false);
                ps.DeleteValue("MacTuneBackup_Performance", throwOnMissingValue: false);
                ps.DeleteValue("MacTuneBackup_IdleState", throwOnMissingValue: false);
            }
            touched++;
        }
        return touched;
    }

    // ---------- 2. Bluetooth Sleep/Lag Fix & Broadcom Wi-Fi Anti-Jitter ----------
    static bool IsTargetBluetoothRadio(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        if (desc.Contains("RFCOMM", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Avrcp", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Enumerator", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("TDI", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Personal Area", StringComparison.OrdinalIgnoreCase))
            return false;

        return desc.Equals("Bluetooth Radio", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Broadcom", StringComparison.OrdinalIgnoreCase)
            || desc.Contains("Apple", StringComparison.OrdinalIgnoreCase);
    }

    static bool IsTargetWifiAdapter(string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return false;
        bool vendor = desc.Contains("Broadcom", StringComparison.OrdinalIgnoreCase)
                   || desc.Contains("Apple", StringComparison.OrdinalIgnoreCase);
        bool wireless = desc.Contains("802.11", StringComparison.OrdinalIgnoreCase)
                     || desc.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                     || desc.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);
        return vendor && wireless;
    }

    public static bool IsWirelessOptimized()
    {
        try
        {
            int btMatched = 0, btFixed = 0;
            using (var btCls = Registry.LocalMachine.OpenSubKey(BluetoothClassKey))
            {
                if (btCls is not null)
                {
                    foreach (var sub in btCls.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                        using var k = btCls.OpenSubKey(sub);
                        if (!IsTargetBluetoothRadio(k?.GetValue("DriverDesc") as string)) continue;
                        btMatched++;
                        if (k?.GetValue("PnPCapabilities") is int pnp && (pnp & 0x18) == 0x18) btFixed++;
                    }
                }
            }

            int wifiMatched = 0, wifiFixed = 0;
            using (var netCls = Registry.LocalMachine.OpenSubKey(NetworkClassKey))
            {
                if (netCls is not null)
                {
                    foreach (var sub in netCls.GetSubKeyNames())
                    {
                        if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                        using var k = netCls.OpenSubKey(sub);
                        if (!IsTargetWifiAdapter(k?.GetValue("DriverDesc") as string)) continue;
                        wifiMatched++;
                        bool pnpOk = k?.GetValue("PnPCapabilities") is int pnp && (pnp & 0x18) == 0x18;
                        var pc = k?.GetValue("*PacketCoalescing");
                        bool pcOk = pc is null || pc.ToString() == "0";
                        if (pnpOk && pcOk) wifiFixed++;
                    }
                }
            }

            if (btMatched + wifiMatched == 0) return false;
            return btFixed == btMatched && wifiFixed == wifiMatched;
        }
        catch { return false; }
    }

    public static int SetWirelessOptimized(bool enable)
    {
        int touched = 0;

        using (var btCls = Registry.LocalMachine.OpenSubKey(BluetoothClassKey))
        {
            if (btCls is not null)
            {
                foreach (var sub in btCls.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    using var k = btCls.OpenSubKey(sub, writable: true);
                    if (!IsTargetBluetoothRadio(k?.GetValue("DriverDesc") as string) || k is null) continue;
                    SetPnpNoSleep(k, enable);
                    touched++;
                }
            }
        }

        using (var netCls = Registry.LocalMachine.OpenSubKey(NetworkClassKey))
        {
            if (netCls is not null)
            {
                foreach (var sub in netCls.GetSubKeyNames())
                {
                    if (sub.Length != 4 || !sub.All(char.IsDigit)) continue;
                    using var k = netCls.OpenSubKey(sub, writable: true);
                    if (!IsTargetWifiAdapter(k?.GetValue("DriverDesc") as string) || k is null) continue;
                    SetPnpNoSleep(k, enable);
                    SetNdisZeroFlag(k, "*PacketCoalescing", "MacTuneBackup_PacketCoalescing", enable);
                    SetNdisZeroFlag(k, "*WakeOnPattern", "MacTuneBackup_WakeOnPattern", enable);
                    touched++;
                }
            }
        }

        return touched;
    }

    static void SetPnpNoSleep(RegistryKey k, bool enable)
    {
        if (enable)
        {
            if (k.GetValue("MacTuneBackup_PnP") is null)
            {
                int prev = k.GetValue("PnPCapabilities") is int v ? v : -1;
                if ((prev & 0x18) != 0x18)
                    k.SetValue("MacTuneBackup_PnP", prev, RegistryValueKind.DWord);
            }
            k.SetValue("PnPCapabilities", 24, RegistryValueKind.DWord);
        }
        else if (k.GetValue("MacTuneBackup_PnP") is int bak)
        {
            if (bak < 0) k.DeleteValue("PnPCapabilities", throwOnMissingValue: false);
            else k.SetValue("PnPCapabilities", bak, RegistryValueKind.DWord);
            k.DeleteValue("MacTuneBackup_PnP", throwOnMissingValue: false);
        }
    }

    static void SetNdisZeroFlag(RegistryKey k, string valueName, string backupName, bool disableFeature)
    {
        object? cur = k.GetValue(valueName);
        if (cur is null) return;
        var kind = k.GetValueKind(valueName);

        if (disableFeature)
        {
            if (cur.ToString() != "0" && k.GetValue(backupName) is null)
                k.SetValue(backupName, cur, kind);
            k.SetValue(valueName, kind == RegistryValueKind.String ? "0" : 0, kind);
        }
        else if (k.GetValue(backupName) is { } bak)
        {
            k.SetValue(valueName, bak, kind);
            k.DeleteValue(backupName, throwOnMissingValue: false);
        }
    }

    // ---------- 3. Natural Scrolling for External Mice (FlipFlopWheel) ----------
    public static bool IsNaturalMouseWheel()
    {
        try
        {
            using var hid = Registry.LocalMachine.OpenSubKey(HidEnumKey);
            if (hid is null) return false;
            int total = 0, flipped = 0;
            foreach (var dev in hid.GetSubKeyNames())
            {
                using var devKey = hid.OpenSubKey(dev);
                if (devKey is null) continue;
                foreach (var inst in devKey.GetSubKeyNames())
                {
                    using var dp = devKey.OpenSubKey($@"{inst}\Device Parameters");
                    if (dp?.GetValue("FlipFlopWheel") is not int v) continue;
                    total++;
                    if (v != 0) flipped++;
                }
            }
            return total > 0 && flipped * 2 >= total;
        }
        catch { return false; }
    }

    public static int SetNaturalMouseWheel(bool enable)
    {
        int count = 0;
        int val = enable ? 1 : 0;
        using var hid = Registry.LocalMachine.OpenSubKey(HidEnumKey);
        if (hid is null) return 0;

        foreach (var dev in hid.GetSubKeyNames())
        {
            using var devKey = hid.OpenSubKey(dev);
            if (devKey is null) continue;
            foreach (var inst in devKey.GetSubKeyNames())
            {
                try
                {
                    using var dp = devKey.OpenSubKey($@"{inst}\Device Parameters", writable: true);
                    if (dp?.GetValue("FlipFlopWheel") is not int) continue;
                    dp.SetValue("FlipFlopWheel", val, RegistryValueKind.DWord);
                    if (dp.GetValue("FlipFlopHScroll") is int)
                        dp.SetValue("FlipFlopHScroll", val, RegistryValueKind.DWord);
                    count++;
                }
                catch { /* system-owned HID branch */ }
            }
        }
        return count;
    }

    // ---------- 4. Apple Startup Disk Control Panel (Reboot to macOS) ----------
    static string StartupDiskExe => Path.Combine(Environment.SystemDirectory, "AppleControlPanel.exe");

    public static bool CanOpenStartupDisk() => File.Exists(StartupDiskExe);

    public static void OpenStartupDisk()
    {
        Process.Start(new ProcessStartInfo(StartupDiskExe) { UseShellExecute = true });
    }
}
