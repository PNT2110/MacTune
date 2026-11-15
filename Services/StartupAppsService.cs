using System.IO;
using Microsoft.Win32;

namespace MacTune.Services;

public record StartupApp(string Name, string Command, string Source, RegistryHive Hive, string ApprovedKey, bool Enabled)
{
    public string Where => Source;
}

/// <summary>
/// Lists apps that start with Windows and enables/disables them exactly like Task Manager does,
/// via the Explorer\StartupApproved keys (first byte 0x02 = enabled, 0x03 = disabled).
/// Nothing is deleted, so every change is reversible.
/// </summary>
public static class StartupAppsService
{
    const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    static readonly (RegistryHive Hive, RegistryView View, string Key, string Approved, string Label)[] RunKeys =
    {
        (RegistryHive.CurrentUser, RegistryView.Default, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Người dùng"),
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run", "Toàn máy"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"Software\Microsoft\Windows\CurrentVersion\Run", "Run32", "Toàn máy (32-bit)"),
    };

    public static List<StartupApp> List()
    {
        var list = new List<StartupApp>();
        foreach (var (hive, view, key, approved, label) in RunKeys)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var k = root.OpenSubKey(key);
                if (k is null) continue;
                foreach (var name in k.GetValueNames())
                {
                    if (string.IsNullOrEmpty(name)) continue;
                    string cmd = k.GetValue(name)?.ToString() ?? "";
                    list.Add(new StartupApp(name, cmd, label, hive, approved, IsEnabled(hive, approved, name)));
                }
            }
            catch { }
        }

        // Startup folders
        foreach (var (dir, hive, label) in new[]
                 {
                     (Environment.GetFolderPath(Environment.SpecialFolder.Startup), RegistryHive.CurrentUser, "Thư mục Startup"),
                     (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), RegistryHive.LocalMachine, "Thư mục Startup (toàn máy)"),
                 })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir).Where(f => !f.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase)))
            {
                string file = Path.GetFileName(f);
                list.Add(new StartupApp(Path.GetFileNameWithoutExtension(f), f, label, hive, "StartupFolder",
                    IsEnabled(hive, "StartupFolder", file)));
            }
        }
        return list.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static string ValueName(StartupApp a) => a.ApprovedKey == "StartupFolder" ? Path.GetFileName(a.Command) : a.Name;

    static bool IsEnabled(RegistryHive hive, string approved, string name)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var k = root.OpenSubKey(Approved + approved);
            if (k?.GetValue(name) is byte[] b && b.Length > 0) return (b[0] & 0x01) == 0; // 02/06 = on, 03/07 = off
        }
        catch { }
        return true; // no entry = enabled
    }

    public static void SetEnabled(StartupApp app, bool enabled)
    {
        using var root = RegistryKey.OpenBaseKey(app.Hive, RegistryView.Default);
        using var k = root.CreateSubKey(Approved + app.ApprovedKey);
        var data = new byte[12];
        data[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(DateTime.Now.ToFileTimeUtc()).CopyTo(data, 4); // disabled timestamp
        k.SetValue(ValueName(app), data, RegistryValueKind.Binary);
    }
}
