using System.IO;

namespace MacTune.Services;

/// <summary>Small diagnostic log at %ProgramData%\MacTune\log.txt (kept under ~200 KB).</summary>
public static class DiagLog
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MacTune", "log.txt");
    static readonly object Lock = new();

    public static void Write(string line)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 200_000) File.Delete(FilePath);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {line}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
