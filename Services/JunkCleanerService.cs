using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace MacTune.Services;

public record JunkScan(long TempBytes, int TempFiles, long RecycleBytes, long RecycleItems);
public record CleanResult(long FreedBytes, int Deleted, int Skipped);

/// <summary>
/// Conservative cleaner: user/Windows temp files older than 24h and the Recycle Bin.
/// Deeper cleanup is delegated to Windows' own Disk Cleanup (cleanmgr).
/// Never follows junctions/symlinks; files in use are skipped.
/// </summary>
public static class JunkCleanerService
{
    static readonly TimeSpan MinAge = TimeSpan.FromHours(24);

    static IEnumerable<string> TempRoots() => new[]
    {
        Path.GetTempPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
    }.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);

    static IEnumerable<FileInfo> OldTempFiles()
    {
        var cutoff = DateTime.Now - MinAge;
        foreach (var root in TempRoots())
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                FileSystemInfo[] items;
                try { items = dir.GetFileSystemInfos(); } catch { continue; }
                foreach (var it in items)
                {
                    if ((it.Attributes & FileAttributes.ReparsePoint) != 0) continue; // never follow links
                    if (it is DirectoryInfo d) stack.Push(d);
                    else if (it is FileInfo f && f.LastWriteTime < cutoff) yield return f;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHQueryRecycleBin(string? root, ref SHQUERYRBINFO info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);

    public static JunkScan Scan()
    {
        long bytes = 0; int files = 0;
        foreach (var f in OldTempFiles()) { try { bytes += f.Length; files++; } catch { } }
        var rb = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        SHQueryRecycleBin(null, ref rb);
        return new JunkScan(bytes, files, rb.i64Size, rb.i64NumItems);
    }

    public static CleanResult CleanTemp()
    {
        long freed = 0; int deleted = 0, skipped = 0;
        foreach (var f in OldTempFiles().ToList())
        {
            try { long len = f.Length; f.Delete(); freed += len; deleted++; }
            catch { skipped++; } // in use or protected
        }
        // remove now-empty subfolders (not the roots)
        foreach (var root in TempRoots())
            try
            {
                foreach (var d in new DirectoryInfo(root).GetDirectories()
                             .Where(d => (d.Attributes & FileAttributes.ReparsePoint) == 0))
                    TryRemoveEmpty(d);
            }
            catch { }
        return new CleanResult(freed, deleted, skipped);
    }

    static void TryRemoveEmpty(DirectoryInfo d)
    {
        try
        {
            foreach (var sub in d.GetDirectories().Where(s => (s.Attributes & FileAttributes.ReparsePoint) == 0))
                TryRemoveEmpty(sub);
            if (!d.EnumerateFileSystemInfos().Any()) d.Delete();
        }
        catch { }
    }

    public static void EmptyRecycleBin()
    {
        const uint NoConfirm = 0x1, NoProgress = 0x2, NoSound = 0x4;
        SHEmptyRecycleBin(IntPtr.Zero, null, NoConfirm | NoProgress | NoSound);
    }

    /// <summary>Opens Windows' own Disk Cleanup including system files (update cache, logs…).</summary>
    public static void OpenWindowsDiskCleanup()
        => Process.Start(new ProcessStartInfo("cleanmgr.exe", "/d C:") { UseShellExecute = true });

    public static string Format(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}
