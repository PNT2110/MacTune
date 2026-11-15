using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using MacTune.Services;

namespace MacTune;

public record EstimateRow(string Name, string Hours, string Detail);
public record DriveRow(string Name, string Life, Brush LifeBrush, string Temp, string Written, string Used);

/// <summary>Meeting mode, per-profile battery runtime, RAM and SSD cards.</summary>
public partial class MainWindow
{
    // ================= Meeting / presentation mode =================
    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);
    const uint EsContinuous = 0x80000000, EsSystem = 0x1, EsDisplay = 0x2;

    bool _meeting;
    string? _profileBeforeMeeting;
    string? _planGuidBeforeMeeting;

    void Meeting_Click(object sender, RoutedEventArgs e) => SetMeeting(ChkMeeting.IsChecked == true);

    public void SetMeeting(bool on)
    {
        if (on == _meeting) return;
        _meeting = on;
        ChkMeeting.IsChecked = on;
        try
        {
            if (on)
            {
                _profileBeforeMeeting = Profile.All.FirstOrDefault(p => p.Name == _profileName)?.Key;
                _planGuidBeforeMeeting = PowerService.ActivePlanGuid();
                SetThreadExecutionState(EsContinuous | EsSystem | EsDisplay); // keep screen on, no sleep
                Profile.All.First(p => p.Key == "silent").Apply();
                if (_fans is not null) { var quiet = FanCurve.All.First(c => c.Key == "quiet"); _fans.SetCurve(quiet); SelectFanTile(quiet); }
                MeetingDesc.Text = $"Đang bật ({DateTime.Now:HH:mm})";
                ShowToast("Đã bật chế độ Họp");
            }
            else
            {
                SetThreadExecutionState(EsContinuous);
                if (Profile.All.FirstOrDefault(p => p.Key == _profileBeforeMeeting) is { } prevProf) prevProf.Apply();
                else if (!string.IsNullOrEmpty(_planGuidBeforeMeeting)) PowerService.Run($"/setactive {_planGuidBeforeMeeting}");
                if (_fans is not null)
                {
                    if (_settings.FanMode == "Auto") { _fans.SetAuto(); SelectFanTile(FanMode.Auto); }
                    else RestoreFanMode();
                }
                MeetingDesc.Text = "Giữ sáng · quạt êm";
                ShowToast("Đã tắt chế độ Họp");
            }
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
        LoadPowerState();
    }

    void RestoreMeetingOnExit()
    {
        if (!_meeting) return;
        _meeting = false;
        try
        {
            SetThreadExecutionState(EsContinuous);
            if (Profile.All.FirstOrDefault(p => p.Key == _profileBeforeMeeting) is { } prevProf) prevProf.Apply();
            else if (!string.IsNullOrEmpty(_planGuidBeforeMeeting)) PowerService.Run($"/setactive {_planGuidBeforeMeeting}");
        }
        catch { }
    }

    // ================= Battery runtime per profile =================
    int _drainTicks;

    void LearnDrain()
    {
        var b = _lastBat;
        if (b?.PluggedIn != false || b.RateW is not double w || w > -1) return; // only while discharging
        var key = Profile.All.FirstOrDefault(p => p.Name == _profileName)?.Key;
        if (key is null) return;

        double drain = -w;
        int n = _settings.DrainSamples.GetValueOrDefault(key);
        double avg = _settings.DrainW.GetValueOrDefault(key, drain);
        n = Math.Min(n + 1, 900);                 // ~30 min memory at 2 s ticks
        _settings.DrainW[key] = avg + (drain - avg) / n;
        _settings.DrainSamples[key] = n;
        if (++_drainTicks % 30 == 0) _settings.Save();
    }

    void ShowEstimates()
    {
        double? fullWh = _lastBat?.FullMWh / 1000.0;
        double? nowWh = _lastBat?.RemainingMWh / 1000.0;
        BatEstimates.ItemsSource = Profile.All.Select(p =>
        {
            if (!_settings.DrainW.TryGetValue(p.Key, out double w) || _settings.DrainSamples.GetValueOrDefault(p.Key) < 15 || fullWh is null)
                return new EstimateRow(p.Name, "—", "Đang đo mức pin…");
            double full = fullWh.Value / w, now = (nowWh ?? 0) / w;
            return new EstimateRow(p.Name, $"{(int)full}h {(int)(full * 60 % 60):00}m",
                $"~{w:0.0} W · còn {(int)now}h {(int)(now * 60 % 60):00}m");
        }).ToList();
    }

    // ================= RAM & SSD =================
    void ShowMemory()
    {
        var m = MemoryService.Read();
        RamBar.Value = m.UsedPct;
        RamBar.Foreground = m.UsedPct >= 90 ? (Brush)FindResource("Red") : m.UsedPct >= 75 ? (Brush)FindResource("Orange") : (Brush)FindResource("Accent");
        RamText.Text = $"{m.UsedGB:0.0} / {m.TotalGB:0} GB ({m.UsedPct:0}%) · trống {m.AvailableGB:0.0} GB";
    }

    async void FreeRam_Click(object sender, RoutedEventArgs e)
    {
        BtnFreeRam.IsEnabled = false;
        RamStatus.Text = "Đang dọn…";
        try
        {
            double gb = await Task.Run(MemoryService.Free);
            RamStatus.Text = gb >= 0.05 ? $"✓ Đã giải phóng {gb:0.0} GB" : "✓ RAM đã gọn";
            ShowMemory();
        }
        catch (Exception ex) { RamStatus.Text = "Lỗi: " + ex.Message; }
        finally { BtnFreeRam.IsEnabled = true; }
    }

    IReadOnlyList<DriveHealth> _fallbackDrives = Array.Empty<DriveHealth>();
    DateTime _fallbackAt;
    bool _fallbackBusy;

    void ShowDrives()
    {
        var drives = _lastSnap?.Drives ?? Array.Empty<DriveHealth>();
        if (drives.Count == 0)
        {
            // T2 Macs: no S.M.A.R.T. through LibreHardwareMonitor – use what Windows + SMC expose
            drives = _fallbackDrives;
            if (!_fallbackBusy && DateTime.Now - _fallbackAt > TimeSpan.FromSeconds(30))
            {
                _fallbackBusy = true;
                var smc = _smc;
                Task.Run(() => StorageFallbackService.Read(smc)).ContinueWith(t =>
                {
                    _fallbackBusy = false;
                    _fallbackAt = DateTime.Now;
                    if (t.IsCompletedSuccessfully) { _fallbackDrives = t.Result; ShowDrives(); }
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }
        DriveList.ItemsSource = drives.Select(d => new DriveRow(
            d.Name,
            d.LifeLeftPct is float l ? $"{l:0}%" : "—",
            (d.LifeLeftPct ?? 100) < 50 ? (Brush)FindResource("Red") : (d.LifeLeftPct ?? 100) < 80 ? (Brush)FindResource("Orange") : (Brush)FindResource("Green"),
            d.TempC is float t ? $"{t:0}°C" : "—",
            d.WrittenGB is float w ? (w >= 1024 ? $"{w / 1024:0.0} TB" : $"{w:0} GB") : "—",
            d.UsedSpacePct is float u ? $"{u:0}%" : "—")).ToList();
        DriveEmpty.Visibility = drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void MoreTick()
    {
        LearnDrain();
        if (PageBattery.Visibility == Visibility.Visible && _tick % 3 == 0) ShowEstimates();
        if (PageClean.Visibility == Visibility.Visible) { ShowMemory(); if (_tick % 5 == 0) ShowDrives(); }
    }
}
