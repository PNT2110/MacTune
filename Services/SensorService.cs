using LibreHardwareMonitor.Hardware;

namespace MacTune.Services;

public record GpuTelemetry(
    string Name,
    float? TempC,
    float? HotSpotC,
    float? LoadPct,
    float? PowerW,
    float? CoreClockMHz,
    float? VramUsedMB,
    float? VramTotalMB);

public record SensorSnapshot(
    float? CpuPackageTemp, float? CpuPackagePower, float? CpuMaxClockMHz, float? CpuLoad,
    IReadOnlyList<(string Name, float Celsius)> Temps,
    IReadOnlyList<(string Name, float Rpm)> Fans)
{
    public IReadOnlyList<DriveHealth> Drives { get; init; } = Array.Empty<DriveHealth>();
    /// <summary>Design capacity reported by the battery gauge chip (mWh).</summary>
    public int? BatteryDesignMWh { get; init; }
    /// <summary>Discrete GPU telemetry (e.g. AMD Radeon Pro 5500M).</summary>
    public GpuTelemetry? Dgpu { get; init; }
}

/// <summary>SSD health from S.M.A.R.T. / NVMe log as exposed by LibreHardwareMonitor.</summary>
public record DriveHealth(string Name, float? TempC, float? UsedSpacePct, float? LifeLeftPct, float? WrittenGB, float? ReadGB);

/// <summary>Read-only hardware telemetry via LibreHardwareMonitor.</summary>
public sealed class SensorService : IDisposable
{
    readonly Computer _pc = new()
    {
        IsCpuEnabled = true,
        IsGpuEnabled = true,
        IsMotherboardEnabled = true,
        IsStorageEnabled = true,
        IsBatteryEnabled = true,
    };

    public SensorService() => _pc.Open();

    static bool _logged;

    public SensorSnapshot Read()
    {
        float? cpuTemp = null, cpuPower = null, cpuClock = null, cpuLoad = null;
        var temps = new List<(string, float)>();
        var fans = new List<(string, float)>();

        void Visit(IHardware hw)
        {
            hw.Update();
            foreach (var sub in hw.SubHardware) Visit(sub);
            foreach (var s in hw.Sensors)
            {
                if (s.Value is not float v) continue;
                if (s.SensorType == SensorType.Temperature && v > 0 && v < 130
                    && !s.Name.Contains("Distance") && !s.Name.Contains("Limit"))
                {
                    string hwName = hw.HardwareType switch
                    {
                        HardwareType.Cpu => "CPU",
                        HardwareType.GpuAmd => "GPU AMD",
                        HardwareType.GpuIntel => "GPU Intel",
                        HardwareType.Storage => "SSD",
                        _ => hw.Name,
                    };
                    temps.Add(($"{hwName} · {s.Name}", v));
                }

                switch (hw.HardwareType, s.SensorType)
                {
                    case (HardwareType.Cpu, SensorType.Temperature) when s.Name == "CPU Package" && v > 0:
                        cpuTemp = v; break;
                    case (HardwareType.Cpu, SensorType.Power) when s.Name.Contains("Package") && v > 0:
                        cpuPower = v; break;
                    // LHM names these "CPU Core #1" (newer) or "Core #1" (older); skip "Bus Speed"
                    case (HardwareType.Cpu, SensorType.Clock) when (s.Name.Contains("Core #") || s.Name.Contains("P-Core")) && v > 0:
                        cpuClock = Math.Max(cpuClock ?? 0, v); break;
                    case (HardwareType.Cpu, SensorType.Load) when s.Name == "CPU Total":
                        cpuLoad = v; break;
                    case (_, SensorType.Fan):
                        fans.Add((s.Name, v)); break;
                }
            }
        }

        foreach (var hw in _pc.Hardware) Visit(hw);

        if (!_logged)
        {
            _logged = true;
            foreach (var hw in _pc.Hardware)
                DiagLog.Write($"HW {hw.HardwareType} \"{hw.Name}\": " + string.Join(", ", hw.Sensors.Select(x => $"{x.SensorType}:{x.Name}={x.Value}")));
        }

        // Discrete GPU (AMD Radeon Pro / Nvidia)
        GpuTelemetry? dgpu = null;
        var gpuHw = _pc.Hardware.FirstOrDefault(h => h.HardwareType is HardwareType.GpuAmd or HardwareType.GpuNvidia);
        if (gpuHw is not null)
        {
            float? GpuSensor(SensorType t, params string[] names)
            {
                foreach (var n in names)
                {
                    var match = gpuHw.Sensors.FirstOrDefault(s => s.SensorType == t && s.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
                    if (match?.Value is float v) return v;
                }
                foreach (var n in names)
                {
                    var match = gpuHw.Sensors.FirstOrDefault(s => s.SensorType == t && s.Name.Contains(n, StringComparison.OrdinalIgnoreCase));
                    if (match?.Value is float v) return v;
                }
                return null;
            }

            float? gTemp = GpuSensor(SensorType.Temperature, "GPU Core", "Core");
            if (gTemp is <= 0) gTemp = null;
            float? gHot = GpuSensor(SensorType.Temperature, "GPU Hot Spot", "Hot Spot");
            if (gHot is <= 0) gHot = null;
            float? loadCore = GpuSensor(SensorType.Load, "GPU Core");
            float? load3d = GpuSensor(SensorType.Load, "D3D 3D");
            float? gLoad = (loadCore, load3d) switch
            {
                (float a, float b) => Math.Max(a, b),
                (float a, null) => a,
                (null, float b) => b,
                _ => null,
            };
            float? gPower = GpuSensor(SensorType.Power, "GPU Package", "GPU Core", "Package");
            if (gPower is <= 0) gPower = null;
            float? gClock = GpuSensor(SensorType.Clock, "GPU Core");
            if (gClock is <= 0) gClock = null;
            float? vramUsed = GpuSensor(SensorType.SmallData, "D3D Dedicated Memory Used", "GPU Memory Used");
            float? vramTotal = GpuSensor(SensorType.SmallData, "D3D Dedicated Memory Total", "GPU Memory Total");

            dgpu = new GpuTelemetry(gpuHw.Name, gTemp, gHot, gLoad, gPower, gClock, vramUsed, vramTotal);
        }

        var drives = new List<DriveHealth>();
        foreach (var hw in _pc.Hardware.Where(h => h.HardwareType == HardwareType.Storage))
        {
            float? Find(SensorType t, params string[] names) =>
                hw.Sensors.FirstOrDefault(s => s.SensorType == t && names.Any(n => s.Name.Contains(n, StringComparison.OrdinalIgnoreCase)))?.Value;
            float? life = Find(SensorType.Level, "Remaining Life", "Life");
            float? used = Find(SensorType.Level, "Percentage Used");
            if (life is null && used is float u) life = Math.Max(0, 100 - u);
            drives.Add(new DriveHealth(hw.Name,
                hw.Sensors.Where(s => s.SensorType == SensorType.Temperature).Select(s => s.Value).FirstOrDefault(v => v is > 0),
                Find(SensorType.Load, "Used Space"),
                life,
                Find(SensorType.Data, "Data Written", "Host Writes", "Total Bytes Written"),
                Find(SensorType.Data, "Data Read", "Host Reads")));
        }

        // Fallback if "CPU Package" is absent: hottest CPU sensor
        cpuTemp ??= temps.Where(t => t.Item1.StartsWith("CPU")).Select(t => (float?)t.Item2).Max();
        var bat = _pc.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Battery);
        float? design = bat?.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Energy && x.Name.Contains("Designed"))?.Value;
        return new SensorSnapshot(cpuTemp, cpuPower, cpuClock, cpuLoad, temps, fans)
        {
            Drives = drives,
            BatteryDesignMWh = design is > 1000 ? (int)design : null,
            Dgpu = dgpu,
        };
    }

    public void Dispose() => _pc.Close();
}
