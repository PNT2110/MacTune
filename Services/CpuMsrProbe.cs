using System.Text.RegularExpressions;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32;

namespace MacTune.Services;

/// <summary>
/// Snapshot of Intel architectural thermal / power / current / firmware MSR flags.
/// Read-only; populated when PawnIO's signed IntelMSR module is available.
/// </summary>
public record MsrLimitSample(
    bool Available,
    uint CorePerfLimitReasons,
    ulong CoreThermStatus,
    ulong PackageThermStatus,
    bool BdProchotEnabled,
    double? Pl1Watts,
    double? Pl2Watts)
{
    // MSR_CORE_PERF_LIMIT_REASONS (0x64F Skylake+ / 0x690 Haswell-Broadwell) live status bits [15:0]:
    //   Bit 0 : PROCHOT Status (external PROCHOT# or BD PROCHOT assertion)
    //   Bit 1 : Thermal Status (internal silicon thermal trip)
    //   Bit 6 : VR Therm Alert Status (Voltage Regulator thermal limit)
    //   Bit 8 : Electrical Design Point / IccMax (Current Limit)
    //   Bit 9 : Core Power Limiting
    //   Bit 10: Package Power Limiting PL1
    //   Bit 11: Package Power Limiting PL2
    //   Bits 12..15: Normal multi-core turbo / P1 ratio ceilings (NOT throttling)
    public bool ProchotActive => (CorePerfLimitReasons & (1u << 0)) != 0 || (CoreThermStatus & (1UL << 0)) != 0;
    public bool ExternalProchotLog => (CoreThermStatus & (1UL << 2)) != 0; // PROCHOT# or FORCEPR# pin assertion
    public bool CoreThermalActive => (CorePerfLimitReasons & (1u << 1)) != 0
                                  || ((CoreThermStatus & (1UL << 0)) != 0 && (CoreThermStatus & (1UL << 2)) == 0)
                                  || (PackageThermStatus & (1UL << 0)) != 0;
    public bool VrThermalActive => (CorePerfLimitReasons & (1u << 6)) != 0;
    public bool CurrentLimitEdpActive => (CorePerfLimitReasons & (1u << 8)) != 0;
    public bool CorePowerLimitActive => (CorePerfLimitReasons & (1u << 9)) != 0;
    public bool Pl1Active => (CorePerfLimitReasons & (1u << 10)) != 0 || (PackageThermStatus & (1UL << 10)) != 0;
    public bool Pl2Active => (CorePerfLimitReasons & (1u << 11)) != 0;
}

/// <summary>
/// Safe, read-only Intel MSR probe backed by LibreHardwareMonitor's signed PawnIO IntelMSR module.
/// Never writes to any MSR. Gracefully degrades to Unavailable when PawnIO is absent.
/// </summary>
public sealed class CpuMsrProbe : IDisposable
{
    const uint MsrIa32ThermStatus = 0x19C;
    const uint MsrIa32PackageThermStatus = 0x1B1;
    const uint MsrIa32PowerCtl = 0x1FC;
    const uint MsrRaplPowerUnit = 0x606;
    const uint MsrPkgPowerLimit = 0x610;

    readonly object _lock = new();
    IntelMsr? _msr;
    double _powerUnitWatts = 0.125; // Intel default 1/8 W
    readonly uint _perfLimitMsr;

    public bool IsAvailable { get; private set; }
    public int CpuFamily { get; }
    public int CpuModel { get; }

    public CpuMsrProbe()
    {
        (CpuFamily, CpuModel) = DetectCpuFamilyModel();
        _perfLimitMsr = ResolvePerfLimitReasonsMsr(CpuFamily, CpuModel);
        TryConnect();
    }

    public static (int Family, int Model) DetectCpuFamilyModel()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            string id = k?.GetValue("Identifier") as string ?? "";
            var m = Regex.Match(id, @"Family\s+(\d+)\s+Model\s+(\d+)", RegexOptions.IgnoreCase);
            if (m.Success)
                return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
        }
        catch { }
        return (6, 158);
    }

    public static uint ResolvePerfLimitReasonsMsr(int family, int model)
    {
        if (family != 6) return 0;
        // Haswell (60, 63, 69, 70) & Broadwell (61, 71, 79, 86) expose MSR_CORE_PERF_LIMIT_REASONS at 0x690
        if (model is 60 or 63 or 69 or 70 or 61 or 71 or 79 or 86) return 0x690;
        // Skylake (78, 94), Kaby/Coffee Lake (142, 158), Comet Lake (165, 166), Ice Lake+ expose 0x64F
        if (model >= 78) return 0x64F;
        return 0;
    }

    public bool TryConnect()
    {
        lock (_lock)
        {
            if (IsAvailable && _msr is not null) return true;
            try
            {
                if (!PawnIo.IsInstalled) { IsAvailable = false; return false; }
                var candidate = new IntelMsr();
                if (!candidate.ReadMsr(MsrIa32ThermStatus, out ulong _))
                {
                    candidate.Close();
                    IsAvailable = false;
                    return false;
                }
                if (candidate.ReadMsr(MsrRaplPowerUnit, out ulong unitRaw))
                {
                    int pwrBits = (int)(unitRaw & 0xF);
                    if (pwrBits is >= 0 and <= 16)
                        _powerUnitWatts = 1.0 / (1 << pwrBits);
                }
                _msr = candidate;
                IsAvailable = true;
                DiagLog.Write($"CpuMsrProbe connected (family={CpuFamily}, model={CpuModel}, limitMsr=0x{_perfLimitMsr:X}, pwrUnit={_powerUnitWatts}W)");
                return true;
            }
            catch (Exception ex)
            {
                DiagLog.Write($"CpuMsrProbe unavailable: {ex.Message}");
                IsAvailable = false;
                return false;
            }
        }
    }

    public MsrLimitSample Read()
    {
        lock (_lock)
        {
            if (!IsAvailable || _msr is null)
                return new MsrLimitSample(false, 0, 0, 0, false, null, null);

            try
            {
                if (!_msr.ReadMsr(MsrIa32ThermStatus, out ulong coreTherm))
                {
                    CloseInternal();
                    return new MsrLimitSample(false, 0, 0, 0, false, null, null);
                }

                _msr.ReadMsr(MsrIa32PackageThermStatus, out ulong pkgTherm);
                _msr.ReadMsr(MsrIa32PowerCtl, out ulong pwrCtl);

                ulong perfReasons = 0;
                if (_perfLimitMsr != 0)
                    _msr.ReadMsr(_perfLimitMsr, out perfReasons);

                double? pl1 = null, pl2 = null;
                if (_msr.ReadMsr(MsrPkgPowerLimit, out ulong pkgLim))
                {
                    bool pl1En = (pkgLim & (1UL << 15)) != 0;
                    uint pl1Raw = (uint)(pkgLim & 0x7FFFUL);
                    if (pl1En && pl1Raw > 0) pl1 = pl1Raw * _powerUnitWatts;

                    bool pl2En = (pkgLim & (1UL << 47)) != 0;
                    uint pl2Raw = (uint)((pkgLim >> 32) & 0x7FFFUL);
                    if (pl2En && pl2Raw > 0) pl2 = pl2Raw * _powerUnitWatts;
                }

                return new MsrLimitSample(
                    Available: true,
                    CorePerfLimitReasons: (uint)(perfReasons & 0xFFFFFFFFUL),
                    CoreThermStatus: coreTherm,
                    PackageThermStatus: pkgTherm,
                    BdProchotEnabled: (pwrCtl & 0x1UL) != 0,
                    Pl1Watts: pl1,
                    Pl2Watts: pl2);
            }
            catch
            {
                CloseInternal();
                return new MsrLimitSample(false, 0, 0, 0, false, null, null);
            }
        }
    }

    void CloseInternal()
    {
        IsAvailable = false;
        try { _msr?.Close(); } catch { }
        _msr = null;
    }

    public void Dispose()
    {
        lock (_lock) CloseInternal();
    }
}
