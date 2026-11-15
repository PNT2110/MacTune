using System.Windows;

namespace MacTune;

public partial class App : Application
{
    static Mutex? _single;
    public static EventWaitHandle? ShowSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        bool trayMode = e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        // Single instance: a second launch brings the running window forward.
        _single = new Mutex(true, @"Global\MacTune_single", out bool first);
        ShowSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\MacTune_show");
        if (!first)
        {
            if (!trayMode) ShowSignal.Set();
            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Never leave fans pinned in manual mode if something goes wrong.
        DispatcherUnhandledException += (_, args) =>
        {
            MacTune.MainWindow.Current?.RestoreFansToApple();
            MessageBox.Show(MacTune.Services.Loc.T("MacTune gặp lỗi và đã trả quạt về chế độ tự động của Apple.") + "\n\n" + args.Exception.Message,
                "MacTune", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => MacTune.MainWindow.Current?.RestoreFansToApple();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => MacTune.MainWindow.Current?.RestoreFansToApple();
        SessionEnding += (_, _) => MacTune.MainWindow.Current?.RestoreFansToApple();
        base.OnStartup(e);

        var win = new MainWindow();
        MainWindow = win;

        new Thread(() =>
        {
            while (ShowSignal.WaitOne())
                Dispatcher.BeginInvoke(() => MacTune.MainWindow.Current?.ShowFromTray());
        }) { IsBackground = true }.Start();

        if (trayMode)
            _ = win.InitAsync();
        else
            win.Show();
    }
}
