using System.Windows;
using System.Windows.Controls;
using MacTune.Services;

namespace MacTune;

/// <summary>Lid / hibernate settings, throttle log, startup apps.</summary>
public partial class MainWindow
{
    readonly ThrottleMonitor _throttle = new();
    int _throttleShown = -1;

    // ================= Lid & hibernate =================
    static readonly (string Label, int Value)[] LidChoices =
        { ("Không làm gì", 0), ("Ngủ", 1), ("Ngủ đông", 2), ("Tắt máy", 3) };

    static readonly (string Label, int Minutes)[] HibChoices =
        { ("15 phút", 15), ("30 phút", 30), ("1 giờ", 60), ("2 giờ", 120), ("3 giờ", 180), ("Không bao giờ", 0) };

    void LoadLid()
    {
        _loading = true;
        try
        {
            var (la, ld) = PowerService.GetLidAction();
            Fill(CbLidAc, LidChoices, la, fallbackValue: 1);
            Fill(CbLidDc, LidChoices, ld, fallbackValue: 1);
            var (ha, hd) = PowerService.GetHibernateAfter();
            Fill(CbHibAc, HibChoices, ha, fallbackValue: 0);
            Fill(CbHibDc, HibChoices, hd, fallbackValue: 0);
            LidStatus.Text = PowerService.HibernationAvailable() ? "" : "Ngủ đông đang tắt – chọn một mốc thời gian để bật.";
        }
        catch (Exception ex) { LidStatus.Text = "Lỗi đọc cài đặt: " + ex.Message; }
        finally { _loading = false; }
    }

    static void Fill(ComboBox cb, (string Label, int Value)[] choices, int current, int fallbackValue)
    {
        cb.Items.Clear();
        foreach (var (label, v) in choices) cb.Items.Add(new ComboBoxItem { Content = label, Tag = v });
        if (current >= 0 && choices.All(c => c.Value != current))
            cb.Items.Insert(0, new ComboBoxItem { Content = $"{current} phút (hiện tại)", Tag = current });
        int target = current >= 0 ? current : fallbackValue;
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == target)
                          ?? cb.Items.Cast<ComboBoxItem>().First();
        cb.IsEnabled = current >= 0;
    }

    static int Val(ComboBox cb) => cb.SelectedItem is ComboBoxItem i ? (int)i.Tag : -1;

    void Lid_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        try
        {
            if ((sender == CbLidAc || sender == CbLidDc) && CbLidAc.IsEnabled && CbLidDc.IsEnabled && Val(CbLidAc) >= 0 && Val(CbLidDc) >= 0)
                PowerService.SetLidAction(Val(CbLidAc), Val(CbLidDc));
            if ((sender == CbHibAc || sender == CbHibDc) && CbHibAc.IsEnabled && CbHibDc.IsEnabled && Val(CbHibAc) >= 0 && Val(CbHibDc) >= 0)
                PowerService.SetHibernateAfter(Val(CbHibAc), Val(CbHibDc));
            LidStatus.Text = $"✓ Đã lưu lúc {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { LidStatus.Text = "Lỗi: " + ex.Message; }
    }

    void LidPreset_Click(object sender, RoutedEventArgs e)
    {
        // plugged: lid → sleep, hibernate after 3h; battery: lid → sleep, hibernate after 30 min
        try
        {
            PowerService.SetLidAction(1, 1);
            PowerService.SetHibernateAfter(180, 30);
            LoadLid();
            LidStatus.Text = "✓ Gập máy = Ngủ; chạy pin sau 30 phút tự Ngủ đông.";
            ShowToast("Đã bật chống tụt pin khi ngủ");
        }
        catch (Exception ex) { LidStatus.Text = "Lỗi: " + ex.Message; }
    }

    // ================= Throttle log =================
    void ThrottleTick()
    {
        var s = _lastSnap;
        if (s is null) return;
        int src = _lastBat?.PluggedIn == false ? 1 : 0;
        bool turboOff = _boost[src] == 0 || (_max[src] >= 0 && _max[src] < 100);
        _throttle.Sample(s.CpuPackageTemp, s.CpuMaxClockMHz, s.CpuLoad, s.CpuPackagePower, src == 1, turboOff);

        if (_throttle.Events.Count != _throttleShown)
        {
            _throttleShown = _throttle.Events.Count;
            ThrottleList.ItemsSource = _throttle.Events.Take(20).ToList();
            ThrottleEmpty.Visibility = _throttleShown == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        ThrottleBadge.Text = _throttle.Active ? $"⚠ Đang hạ xung: {_throttle.CurrentReason}"
                           : _throttle.TodayCount > 0 ? $"{_throttle.TodayCount} lần hôm nay" : "";
        if (s.CpuMaxClockMHz is null)
            ThrottleEmpty.Text = "Cần đọc được xung nhịp CPU (driver PawnIO) để theo dõi hạ xung.";
    }

    // ================= Startup apps =================
    const int StartupShort = 6;
    bool _startupAll;

    void StartupMore_Click(object sender, RoutedEventArgs e) { _startupAll = !_startupAll; RefreshStartup(); }

    void RefreshStartup()
    {
        try
        {
            // enabled apps first; only a handful shown until "Xem tất cả" is pressed
            var all = StartupAppsService.List().OrderByDescending(a => a.Enabled).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
            StartupCount.Text = $"{all.Count(a => a.Enabled)}/{all.Count} đang bật";
            StartupList.ItemsSource = _startupAll ? all : all.Take(StartupShort).ToList();
            BtnStartupMore.Visibility = all.Count > StartupShort ? Visibility.Visible : Visibility.Collapsed;
            BtnStartupMore.Content = _startupAll ? "Thu gọn" : $"Xem tất cả ({all.Count})";
        }
        catch (Exception ex) { ShowToast("Không đọc được danh sách khởi động: " + ex.Message); }
    }

    void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        var cb = (CheckBox)sender;
        var app = (StartupApp)cb.Tag;
        bool on = cb.IsChecked == true;
        try
        {
            StartupAppsService.SetEnabled(app, on);
            ShowToast($"{app.Name}: {(on ? "sẽ chạy" : "không chạy")} khi khởi động");
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
        RefreshStartup();
    }
}
