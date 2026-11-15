using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MacTune;

public partial class TrayFlyoutWindow : Window
{
    readonly MainWindow _main;
    bool _syncing;
    DateTime _lastHiddenUtc;

    public TrayFlyoutWindow(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        MacTune.Services.Loc.Attach(this);
        Deactivated += (_, _) =>
        {
            _lastHiddenUtc = DateTime.UtcNow;
            Hide();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };
    }

    public void ToggleAtTray()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        // Prevent immediate reopen when clicking tray icon while flyout had focus
        if ((DateTime.UtcNow - _lastHiddenUtc).TotalMilliseconds < 240)
            return;

        SyncFromMain();
        Opacity = 0;
        Show();
        UpdateLayout();

        var dpi = VisualTreeHelper.GetDpi(this);
        var cursor = System.Windows.Forms.Control.MousePosition;
        var wa = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

        double waLeft = wa.Left / dpi.DpiScaleX;
        double waRight = wa.Right / dpi.DpiScaleX;
        double waBottom = wa.Bottom / dpi.DpiScaleY;
        double cursorX = cursor.X / dpi.DpiScaleX;

        Left = Math.Clamp(cursorX - ActualWidth / 2, waLeft + 6, waRight - ActualWidth - 6);
        Top = waBottom - ActualHeight - 4;

        Opacity = 1;
        Activate();
        Focus();
    }

    public void SyncFromMain()
    {
        _syncing = true;
        try
        {
            FlySummary.Text = _main.FlyoutTelemetrySummary;
            FlyPowerIn.Text = _main.FlyoutPowerInText;
            FlyPowerOut.Text = _main.FlyoutPowerOutText;
            FlyFanRpm.Text = _main.FlyoutFanSummary;

            string pk = _main.CurrentProfileKey;
            FlyProfSilent.IsChecked = pk == "silent";
            FlyProfBalanced.IsChecked = pk == "balanced";
            FlyProfPerf.IsChecked = pk == "performance";

            FlyFanBox.IsEnabled = _main.IsFanControlAvailable;
            FlyFanBox.Opacity = _main.IsFanControlAvailable ? 1.0 : 0.45;
            string fk = _main.CurrentFanQuickKey;
            FlyFanAuto.IsChecked = fk == "auto";
            FlyFanQuiet.IsChecked = fk == "quiet";
            FlyFanBal.IsChecked = fk == "balanced";
            FlyFanCool.IsChecked = fk == "cool";

            FlySlBright.IsEnabled = _main.IsBrightnessEnabled;
            FlySlBright.Value = _main.CurrentBrightness;
            FlyBrightVal.Text = $"{FlySlBright.Value:0}%";

            FlySlKbd.IsEnabled = _main.IsKbdEnabled;
            FlySlKbd.Value = _main.CurrentKbdBacklight;
            FlyKbdVal.Text = FlySlKbd.Value <= 0 ? "Tắt" : $"{FlySlKbd.Value:0}%";

            BtnFlyMeeting.IsChecked = _main.IsMeetingOn;
            BtnFlyMacKeys.IsChecked = _main.IsMacKeysOn;
            BtnFlyTbFn.IsChecked = _main.IsTouchBarFnOn;
            FlyTbSub.Text = _main.IsTouchBarFnOn ? "Đang bật" : "Hiện Media";
        }
        finally { _syncing = false; }
    }

    void OpenMain_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _main.ShowFromTray();
    }

    void FlyProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton rb || rb.Tag is not string key) return;
        _main.ApplyProfileByKey(key);
        SyncFromMain();
    }

    void FlyFan_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton rb || rb.Tag is not string key) return;
        _main.ApplyQuickFanByKey(key);
        SyncFromMain();
    }

    void FlySlBright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FlyBrightVal is not null) FlyBrightVal.Text = $"{e.NewValue:0}%";
        if (_syncing) return;
        _main.SetBrightnessExternal(e.NewValue);
    }

    void FlySlKbd_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FlyKbdVal is not null) FlyKbdVal.Text = e.NewValue <= 0 ? "Tắt" : $"{e.NewValue:0}%";
        if (_syncing) return;
        _main.SetKbdExternal(e.NewValue);
    }

    void BtnFlyMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _main.SetMeeting(BtnFlyMeeting.IsChecked == true);
        SyncFromMain();
    }

    void BtnFlyMacKeys_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _main.SetMacKeys(BtnFlyMacKeys.IsChecked == true);
        SyncFromMain();
    }

    void BtnFlyTbFn_Click(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        _main.SetTouchBarFn(BtnFlyTbFn.IsChecked == true);
        SyncFromMain();
    }
}
