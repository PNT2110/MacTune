using System.Diagnostics;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace MacTune.Services;

/// <summary>
/// Opening MacTune without a UAC prompt: an on-demand scheduled task with highest privileges,
/// and a desktop shortcut that just triggers that task.
/// </summary>
public static class LaunchService
{
    const string TaskName = "MacTune-Launch";

    internal static int Schtasks(string args)
    {
        var psi = new ProcessStartInfo("schtasks.exe", args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        string err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 && args.StartsWith("/Create", StringComparison.OrdinalIgnoreCase))
            DiagLog.Write($"schtasks {args} failed ({p.ExitCode}): {err.Trim()}");
        return p.ExitCode;
    }

    /// <summary>
    /// Registers an elevated scheduled task via XML so laptop battery constraints are disabled
    /// (DisallowStartIfOnBatteries=false, StopIfGoingOnBatteries=false, ExecutionTimeLimit=PT0S).
    /// </summary>
    public static bool CreateElevatedTask(string taskName, string exe, string? arguments, bool onLogon, bool allowParallel)
    {
        string userId = WindowsIdentity.GetCurrent().User?.Value
                        ?? $"{Environment.UserDomainName}\\{Environment.UserName}";
        string escapedUser = SecurityElement.Escape(userId)!;
        string escapedExe = SecurityElement.Escape(exe)!;
        string escapedDir = SecurityElement.Escape(Path.GetDirectoryName(exe) ?? "")!;
        string argsXml = string.IsNullOrWhiteSpace(arguments)
            ? ""
            : $"<Arguments>{SecurityElement.Escape(arguments)}</Arguments>";
        string triggersXml = onLogon
            ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{escapedUser}</UserId></LogonTrigger></Triggers>"
            : "<Triggers />";
        string multiPolicy = allowParallel ? "Parallel" : "IgnoreNew";

        string xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Principals>
                <Principal id="Author">
                  <UserId>{escapedUser}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>{multiPolicy}</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              {triggersXml}
              <Actions Context="Author">
                <Exec>
                  <Command>"{escapedExe}"</Command>
                  {argsXml}
                  <WorkingDirectory>{escapedDir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        string tmpXml = Path.Combine(Path.GetTempPath(), $"{taskName}-{Guid.NewGuid():N}.xml");
        try
        {
            File.WriteAllText(tmpXml, xml, Encoding.Unicode);
            return Schtasks($"/Create /F /TN \"{taskName}\" /XML \"{tmpXml}\"") == 0;
        }
        finally
        {
            try { File.Delete(tmpXml); } catch { }
        }
    }

    /// <summary>Creates/refreshes the task and points the desktop shortcut at it. Requires admin (app runs elevated).</summary>
    public static bool EnsureNoPromptShortcut()
    {
        string exe = Environment.ProcessPath!;
        if (!CreateElevatedTask(TaskName, exe, arguments: null, onLogon: false, allowParallel: true))
            return false;

        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic lnk = shell.CreateShortcut(Path.Combine(desktop, "MacTune.lnk"));
            lnk.TargetPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
            lnk.Arguments = $"/Run /I /TN {TaskName}";
            lnk.WorkingDirectory = Path.GetDirectoryName(exe);
            lnk.IconLocation = exe + ",0";
            lnk.WindowStyle = 7; // minimized – hides the brief console flash
            lnk.Description = "MacTune";
            lnk.Save();
            return true;
        }
        catch { return false; }
    }
}
