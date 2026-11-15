using System.Management;

namespace MacTune.Services;

public record BatteryInfo(
    int? DesignMWh, int? FullMWh, int? RemainingMWh, int? CycleCount,
    double? VoltageV, double? RateW, bool? Charging, bool? PluggedIn)
{
    /// <summary>MacBookPro16,1 ships with a 100 Wh battery; used when Windows does not report design capacity.</summary>
    public const int FallbackDesignMWh = 99800;

    public double? HealthPercent =>
        FullMWh is int full ? 100.0 * full / (DesignMWh ?? FallbackDesignMWh) : null;
}

public static class BatteryService
{
    static ManagementObject? First(string cls)
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\wmi", $"SELECT * FROM {cls}");
            return s.Get().Cast<ManagementObject>().FirstOrDefault();
        }
        catch { return null; }
    }

    static int? Int(ManagementObject? o, string prop)
    {
        if (o?[prop] is null) return null;
        int v = Convert.ToInt32(o[prop]);
        return v > 0 ? v : null; // 0 means "not reported" on Boot Camp
    }

    public static BatteryInfo Read()
    {
        var stat = First("BatteryStaticData");
        var full = First("BatteryFullChargedCapacity");
        var status = First("BatteryStatus");
        var cycles = First("BatteryCycleCount");

        double? rateW = null;
        bool? charging = null, plugged = null;
        if (status is not null)
        {
            charging = (bool)status["Charging"];
            plugged = (bool)status["PowerOnline"];
            int charge = Convert.ToInt32(status["ChargeRate"]);
            int discharge = Convert.ToInt32(status["DischargeRate"]);
            rateW = (charging == true ? charge : -discharge) / 1000.0;
        }

        int? mv = Int(status, "Voltage");
        return new BatteryInfo(
            Int(stat, "DesignedCapacity"), Int(full, "FullChargedCapacity"),
            Int(status, "RemainingCapacity"), Int(cycles, "CycleCount"),
            mv / 1000.0, rateW, charging, plugged);
    }
}
