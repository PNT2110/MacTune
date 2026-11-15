using System.IO;
using System.Management;

namespace MacTune.Services;

/// <summary>
/// SSD info when LibreHardwareMonitor finds no storage device. On T2 Macs the internal SSD sits behind
/// Apple's own controller (shown to Windows as a SCSI disk), so standard NVMe S.M.A.R.T. is not available.
/// This reads what Windows itself exposes (Storage reliability counters) plus free space and the SMC SSD sensor.
/// </summary>
public static class StorageFallbackService
{
    static bool _logged;

    public static List<DriveHealth> Read(SmcService? smc)
    {
        var list = new List<DriveHealth>();
        try
        {
            var scope = new ManagementScope(@"root\Microsoft\Windows\Storage");
            using var disks = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));
            foreach (ManagementObject disk in disks.Get())
            {
                string name = $"{disk["FriendlyName"]}".Trim();
                float? temp = null, life = null;
                string note = "";
                try
                {
                    foreach (ManagementObject rc in disk.GetRelated("MSFT_StorageReliabilityCounter"))
                    {
                        if (rc["Temperature"] is not null && Convert.ToInt32(rc["Temperature"]) is > 0 and < 120 and var t) temp = t;
                        if (rc["Wear"] is not null && Convert.ToInt32(rc["Wear"]) is >= 0 and <= 100 and var w) life = 100 - w;
                        note = $"temp={rc["Temperature"]} wear={rc["Wear"]} hours={rc["PowerOnHours"]}";
                    }
                }
                catch (Exception ex) { note = "reliability counter: " + ex.Message; }

                // SMC has its own SSD temperature sensor on these Macs
                if (temp is null && smc?.ReadNumber("TH0a") is double st && st is > 5 and < 120) temp = (float)st;

                float? used = null;
                try
                {
                    var sys = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory)!);
                    used = 100f * (sys.TotalSize - sys.TotalFreeSpace) / sys.TotalSize;
                }
                catch { }

                if (!_logged) DiagLog.Write($"storage fallback: \"{name}\" health={disk["HealthStatus"]} {note}");
                list.Add(new DriveHealth(name, temp, used, life, null, null));
            }
        }
        catch (Exception ex) { if (!_logged) DiagLog.Write("storage fallback failed: " + ex.Message); }
        _logged = true;
        return list;
    }
}
