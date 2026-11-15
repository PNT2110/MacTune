using System.Windows;
using System.Windows.Controls;
using MacTune.Services;
using Microsoft.Win32;

namespace MacTune;

public record RuleRow(AppRule Rule, string Process, string Summary);

/// <summary>Settings persistence, auto-switching, battery alerts, tray temperature, GPU per app.</summary>
public partial class MainWindow
{
    readonly AppSettings _settings = AppSettings.Load();
    AppRule? _activeRule;
    string? _profileBeforeRule;
    string? _planGuidBeforeRule;
    bool _chargeAlerted, _lowAlerted;
    bool? _lastPlugged;

    // ================= Startup =================
    void InitFeatures()
    {
        _loading = true;
        ChkTray.IsChecked = _settings.TrayOnClose;
        ChkTop.IsChecked = Topmost = _settings.Topmost;
        ChkTrayTemp.IsChecked = _settings.TrayShowsTemp;

        FillProfileCombo(CbAcProfile, _settings.AcProfile);
        FillProfileCombo(CbDcProfile, _settings.DcProfile);
        FillProfileCombo(CbRuleProfile, "performance");
        CbRuleFan.Items.Clear();
        CbRuleFan.Items.Add(new ComboBoxItem { Content = "Quạt: giữ nguyên", Tag = null });
        foreach (var c in FanCurve.All) CbRuleFan.Items.Add(new ComboBoxItem { Content = $"Quạt: {c.Name}", Tag = c.Key });
        CbRuleFan.SelectedIndex = 3; // Mát tối đa

        ChkAutoPower.IsChecked = _settings.AutoSwitchPower;
        ChkAppRules.IsChecked = _settings.AppRulesEnabled;
        ChkChargeAlert.IsChecked = _settings.ChargeAlert;
        SlChargeAlert.Value = _settings.ChargeAlertAt;
        ChkLowAlert.IsChecked = _settings.LowAlert;
        SlLowAlert.Value = _settings.LowAlertAt;
        _loading = false;

        RefreshRules();
        RefreshGpuList();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Desktop shortcut that opens MacTune without the UAC prompt (app is elevated now)
        Task.Run(() =>
        {
            LaunchService.EnsureNoPromptShortcut();
            SystemService.RefreshStartupTaskIfEnabled();
        });
    }

    /// <summary>Re-applies the saved fan mode after the SMC is connected.</summary>
    void RestoreFanMode()
    {
        if (_fans is null) return;
        try
        {
            switch (_settings.FanMode)
            {
                case "Curve":
                    var c = FanCurve.All.FirstOrDefault(x => x.Key == _settings.FanCurve) ?? FanCurve.All[1];
                    _fans.SetCurve(c);
                    SelectFanTile(c);
                    break;
                case "Fixed":
                    SlFan.Value = _settings.FanFixedPct * 100;
                    _fans.SetFixed(_settings.FanFixedPct);
                    SelectFanTile(FanMode.Fixed);
                    FixedPanel.Visibility = Visibility.Visible;
                    break;
            }
        }
        catch { /* keep Apple auto */ }
    }

    void SelectFanTile(object tag)
    {
        foreach (var rb in _fanTiles) rb.IsChecked = Equals(rb.Tag, tag);
    }

    void SaveFanSettings()
    {
        if (_fans is null || _activeRule?.FanCurve is not null) return; // don't persist rule overrides
        _settings.FanMode = _fans.Mode.ToString();
        _settings.FanCurve = _fans.Curve.Key;
        _settings.FanFixedPct = _fans.FixedPercent;
        _settings.Save();
    }

    // ================= Per-tick work =================
    void FeaturesTick(double? cpuTemp)
    {
        // Tray icon temperature & power tooltip
        if (_tray is not null)
        {
            if (_settings.TrayShowsTemp) _tray.Icon = TrayIconRenderer.Render(cpuTemp);
            string tip = cpuTemp is double t
                ? $"MacTune · CPU {t:0}°C · Vào {LastPowerInW:0}W · Dùng {LastPowerOutW:0}W"
                : "MacTune";
            tip = Loc.T(tip);
            _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        }

        var b = _lastBat;
        if (b is not null)
        {
            // Battery alerts (only once per crossing; re-armed after moving 3% away)
            double? pct = b.RemainingMWh is int r && b.FullMWh is int f && f > 0 ? 100.0 * r / f : null;
            if (pct is double p)
            {
                if (_settings.ChargeAlert && b.PluggedIn == true && p >= _settings.ChargeAlertAt && !_chargeAlerted)
                {
                    _chargeAlerted = true;
                    Notify("Pin đã đạt " + $"{p:0}%", "Rút sạc ra để pin bền hơn.");
                }
                if (p < _settings.ChargeAlertAt - 3 || b.PluggedIn == false) _chargeAlerted = false;

                if (_settings.LowAlert && b.PluggedIn == false && p <= _settings.LowAlertAt && !_lowAlerted)
                {
                    _lowAlerted = true;
                    Notify($"Pin yếu: còn {p:0}%", "Cắm sạc để tránh máy tắt đột ngột.");
                }
                if (p > _settings.LowAlertAt + 3 || b.PluggedIn == true) _lowAlerted = false;
            }

            // Plug / unplug detection (backup for the system event)
            if (_lastPlugged is not null && b.PluggedIn != _lastPlugged) ApplyPowerSourceProfile(b.PluggedIn == true);
            _lastPlugged = b.PluggedIn;
        }

        if (_tick % 3 == 0) CheckAppRules();
        ThrottleTick();
        MoreTick();
    }

    void Notify(string title, string text)
    {
        _tray?.ShowBalloonTip(5000, Loc.T(title), Loc.T(text), System.Windows.Forms.ToolTipIcon.Info);
    }

    // ================= Auto switch on AC / DC =================
    void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;
        Dispatcher.BeginInvoke(() =>
        {
            bool plugged = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Online;
            if (plugged != _lastPlugged) { _lastPlugged = plugged; ApplyPowerSourceProfile(plugged); }
        });
    }

    void ApplyPowerSourceProfile(bool plugged)
    {
        if (!_settings.AutoSwitchPower || _activeRule is not null || _meeting) return;
        var p = Profile.All.FirstOrDefault(x => x.Key == (plugged ? _settings.AcProfile : _settings.DcProfile));
        if (p is null) return;
        try
        {
            p.Apply();
            LoadPowerState();
            Notify(plugged ? "Đã cắm sạc" : "Đang chạy pin", $"Chuyển sang chế độ {p.Name}.");
        }
        catch { }
    }

    // ================= Per-app rules =================
    void CheckAppRules()
    {
        if (_meeting) return;
        if (!_settings.AppRulesEnabled || _settings.AppRules.Count == 0)
        {
            if (_activeRule is not null) EndRule();
            return;
        }

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var proc in System.Diagnostics.Process.GetProcesses())
        {
            try { running.Add(proc.ProcessName); } catch { }
            proc.Dispose();
        }
        var match = _settings.AppRules.FirstOrDefault(r => running.Contains(r.Process));

        if (match is null) { if (_activeRule is not null) EndRule(); return; }
        if (ReferenceEquals(match, _activeRule)) return;

        _profileBeforeRule ??= Profile.All.FirstOrDefault(x => x.Name == _profileName)?.Key;
        _planGuidBeforeRule ??= PowerService.ActivePlanGuid();
        _activeRule = match;
        var p = Profile.All.FirstOrDefault(x => x.Key == match.Profile);
        try
        {
            p?.Apply();
            if (match.FanCurve is not null && _fans is not null)
            {
                var c = FanCurve.All.FirstOrDefault(x => x.Key == match.FanCurve);
                if (c is not null) { _fans.SetCurve(c); SelectFanTile(c); }
            }
            LoadPowerState();
            Notify($"Phát hiện {match.Process}", $"Chuyển sang {p?.Name ?? match.Profile}" +
                   (match.FanCurve is not null ? ", quạt " + FanCurve.All.First(x => x.Key == match.FanCurve).Name : "") + ".");
        }
        catch { }
        RuleStatus.Text = $"Đang áp dụng quy tắc cho {match.Process}.";
    }

    void EndRule(bool quiet = false)
    {
        var ended = _activeRule;
        _activeRule = null;
        try
        {
            // restore previous profile (or AC/DC choice) and saved fan mode
            bool plugged = _lastBat?.PluggedIn == true;
            string? key = _settings.AutoSwitchPower ? (plugged ? _settings.AcProfile : _settings.DcProfile) : _profileBeforeRule;
            if (Profile.All.FirstOrDefault(x => x.Key == key) is { } prevProf) prevProf.Apply();
            else if (!string.IsNullOrEmpty(_planGuidBeforeRule)) PowerService.Run($"/setactive {_planGuidBeforeRule}");
            if (!quiet && ended?.FanCurve is not null && _fans is not null)
            {
                if (_settings.FanMode == "Auto") { _fans.SetAuto(); SelectFanTile(FanMode.Auto); }
                else RestoreFanMode();
            }
            if (!quiet)
            {
                LoadPowerState();
                if (ended is not null) Notify($"{ended.Process} đã đóng", "Đã trả về chế độ trước đó.");
            }
        }
        catch { }
        _profileBeforeRule = null;
        _planGuidBeforeRule = null;
        if (!quiet) RuleStatus.Text = "";
    }

    void RefreshRules()
    {
        RuleList.ItemsSource = _settings.AppRules.Select(r => new RuleRow(r, r.Process,
            $"{Profile.All.FirstOrDefault(p => p.Key == r.Profile)?.Name ?? r.Profile}" +
            (r.FanCurve is null ? "" : $" · quạt {FanCurve.All.FirstOrDefault(c => c.Key == r.FanCurve)?.Name}"))).ToList();
        RuleEmpty.Visibility = _settings.AppRules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // suggestions: running apps that have a window
        var names = System.Diagnostics.Process.GetProcesses()
            .Where(p => { try { return p.MainWindowHandle != IntPtr.Zero; } catch { return false; } })
            .Select(p => p.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        CbRuleApp.ItemsSource = names;
    }

    void AddRule_Click(object sender, RoutedEventArgs e)
    {
        string name = (CbRuleApp.Text ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (name.Length == 0) { RuleStatus.Text = "Nhập tên ứng dụng (ví dụ: chrome, Photoshop, eldenring)."; return; }
        _settings.AppRules.RemoveAll(r => r.Process.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.AppRules.Add(new AppRule
        {
            Process = name,
            Profile = (string)((ComboBoxItem)CbRuleProfile.SelectedItem).Tag,
            FanCurve = (string?)((ComboBoxItem)CbRuleFan.SelectedItem).Tag,
        });
        _settings.Save();
        CbRuleApp.Text = "";
        RefreshRules();
        ShowToast($"Đã thêm quy tắc cho {name}");
    }

    void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        var row = (RuleRow)((Button)sender).Tag;
        _settings.AppRules.Remove(row.Rule);
        if (ReferenceEquals(row.Rule, _activeRule)) EndRule();
        _settings.Save();
        RefreshRules();
    }

    // ================= Settings controls =================
    static void FillProfileCombo(ComboBox cb, string selectedKey)
    {
        cb.Items.Clear();
        foreach (var p in Profile.All)
        {
            var item = new ComboBoxItem { Content = p.Name, Tag = p.Key };
            cb.Items.Add(item);
            if (p.Key == selectedKey) cb.SelectedItem = item;
        }
        if (cb.SelectedItem is null) cb.SelectedIndex = 1;
    }

    void AutoSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        bool turnedOnPower = sender == ChkAutoPower && ChkAutoPower.IsChecked == true && !_settings.AutoSwitchPower;
        _settings.AutoSwitchPower = ChkAutoPower.IsChecked == true;
        _settings.AcProfile = (string?)(CbAcProfile.SelectedItem as ComboBoxItem)?.Tag ?? "performance";
        _settings.DcProfile = (string?)(CbDcProfile.SelectedItem as ComboBoxItem)?.Tag ?? "balanced";
        _settings.AppRulesEnabled = ChkAppRules.IsChecked == true;
        _settings.ChargeAlert = ChkChargeAlert.IsChecked == true;
        _settings.LowAlert = ChkLowAlert.IsChecked == true;
        _settings.Save();
        if (turnedOnPower) ApplyPowerSourceProfile(_lastBat?.PluggedIn == true);
        if (!_settings.AppRulesEnabled && _activeRule is not null) EndRule();
    }

    void AutoSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        _settings.ChargeAlertAt = (int)SlChargeAlert.Value;
        _settings.LowAlertAt = (int)SlLowAlert.Value;
        _chargeAlerted = _lowAlerted = false;
        _settings.Save();
    }

    void SettingsToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.TrayOnClose = ChkTray.IsChecked == true;
        _settings.TrayShowsTemp = ChkTrayTemp.IsChecked == true;
        _settings.Topmost = ChkTop.IsChecked == true;
        _settings.Save();
        if (!_settings.TrayShowsTemp && _tray is not null)
            _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
    }

    // ================= GPU per app =================
    void RefreshGpuList()
    {
        var list = GpuPreferenceService.List();
        GpuList.ItemsSource = list;
        GpuEmpty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void AddGpu_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = Loc.T("Ứng dụng (*.exe)|*.exe"), Title = Loc.T("Chọn ứng dụng") };
        if (dlg.ShowDialog(this) != true) return;
        GpuPreferenceService.Set(dlg.FileName, 1);
        RefreshGpuList();
        ShowToast("Đã đặt GPU Intel. Mở lại ứng dụng đó để áp dụng.");
    }

    void GpuIntel_Click(object sender, RoutedEventArgs e) => SetGpu(sender, 1);
    void GpuAmd_Click(object sender, RoutedEventArgs e) => SetGpu(sender, 2);

    void SetGpu(object sender, int mode)
    {
        var p = (GpuPref)((Button)sender).Tag;
        GpuPreferenceService.Set(p.ExePath, mode);
        RefreshGpuList();
        ShowToast($"{p.Name}: {(mode == 1 ? "GPU Intel" : "GPU AMD")} – mở lại ứng dụng để áp dụng.");
    }

    void GpuRemove_Click(object sender, RoutedEventArgs e)
    {
        var p = (GpuPref)((Button)sender).Tag;
        GpuPreferenceService.Remove(p.ExePath);
        RefreshGpuList();
    }
}
