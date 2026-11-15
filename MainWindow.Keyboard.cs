using System.Windows;
using System.Windows.Threading;
using MacTune.Services;

namespace MacTune;

/// <summary>Keyboard backlight slider.</summary>
public partial class MainWindow
{
    KeyboardBacklightService? _kbd;
    readonly DispatcherTimer _kbdTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    bool _kbdInit;

    void InitKeyboard()
    {
        if (_kbdInit) return;
        _kbdInit = true;
        _kbdTimer.Tick += (_, _) =>
        {
            _kbdTimer.Stop();
            double v = SlKbd.Value / 100;
            bool ok = false;
            try { ok = _kbd?.Set(v) ?? false; } catch (Exception ex) { DiagLog.Write("kbd backlight: " + ex.Message); }
            KbdStatus.Text = ok ? "" : "Không gửi được lệnh tới đèn bàn phím.";
            KbdStatus.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
            _settings.KeyboardBacklight = v;
            _settings.Save();
        };

        _kbd = KeyboardBacklightService.TryOpen(out string? err);
        if (_kbd is null)
        {
            SlKbd.IsEnabled = false;
            KbdStatus.Text = "Không tìm thấy đèn bàn phím (" + err + ").";
            KbdStatus.Visibility = Visibility.Visible;
            DiagLog.Write("kbd backlight unavailable: " + err);
            return;
        }

        _loading = true;
        double? cur = _kbd.Get();
        SlKbd.Value = (cur ?? _settings.KeyboardBacklight) * 100;
        KbdLabel.Text = $"{SlKbd.Value:0}%";
        _loading = false;
        DiagLog.Write($"kbd backlight ok, current={cur}");
    }

    void SlKbd_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (KbdLabel is null) return;
        KbdLabel.Text = e.NewValue <= 0 ? "Tắt" : $"{e.NewValue:0}%";
        if (_loading || !IsLoaded || _kbd is null) return;
        _kbdTimer.Stop(); _kbdTimer.Start(); // debounce
    }
}
