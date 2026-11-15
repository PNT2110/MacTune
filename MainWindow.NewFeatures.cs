using System.Windows;
using System.Windows.Media;
using MacTune.Services;

namespace MacTune;

/// <summary>
/// Connects the 4 new features to the UI:
/// 1) Boot Camp 100% Disk Startup Optimizer
/// 2) macOS-style Mini Tray Control Center Flyout
/// 3) Mac Key Swap (Command ⌘ ↔ Ctrl) &amp; Apple Touch Bar mode (F1–F12 vs Media)
/// 4) Discrete AMD Radeon Pro GPU live card &amp; active VRAM app monitor
/// </summary>
public partial class MainWindow
{
    readonly MacKeySwapService _keySwap = new();
    TrayFlyoutWindow? _flyout;
    bool _tbFnDefault;
    bool _syncingNewUi;
    bool _gpuAppsBusy;
    string _gpuAppsText = "";
    string _lastFanShort = "Tự động";
    public double LastPowerInW { get; private set; }
    public double LastPowerOutW { get; private set; }
    public string FlyoutPowerInText => _lastBat?.PluggedIn == true ? $"{LastPowerInW:0.0} W" : "0 W";
    public string FlyoutPowerOutText => LastPowerOutW > 0 ? $"{LastPowerOutW:0.0} W" : "–";

    void InitNewFeatures()
    {
        _tbFnDefault = AppleKeyboardService.IsTouchBarFnDefault();
        if (_settings.SwapCommandCtrl)
            _keySwap.SetEnabled(true);

        SyncInputControls();
        _ = RefreshBootOptUiAsync();
        _ = RefreshBcFixesUiAsync();
    }

    void DisposeNewFeatures()
    {
        _keySwap.Dispose();
        if (_flyout is not null)
        {
            try { _flyout.Close(); } catch { }
            _flyout = null;
        }
    }

    // ================= 1. Boot Camp Disk Optimizer =================
    async Task RefreshBootOptUiAsync()
    {
        bool savedPref = _settings.MuteLogonUpdaters;
        var st = await Task.Run(() => BootOptimizerService.GetState(savedPref));
        ApplyBootOptStateToUi(st);
    }

    void ApplyBootOptStateToUi(BootOptState st)
    {
        _syncingNewUi = true;
        try
        {
            ChkOptSysMain.IsChecked = st.SysMainOff;
            ChkOptSearch.IsChecked = st.SearchTamed;
            ChkOptUpdaters.IsChecked = st.UpdatersMuted;

            bool allGood = st.Score == 3;
            BootOptBadgeText.Text = allGood ? "✓ Đã tối ưu 3/3" : $"Đã bật {st.Score}/3";
            BootOptBadge.Background = (Brush)FindResource(allGood ? "GreenSoft" : "OrangeSoft");
            BootOptBadgeText.Foreground = (Brush)FindResource(allGood ? "Green" : "Orange");
            BtnBootOptAll.IsEnabled = !allGood;
        }
        finally { _syncingNewUi = false; }
    }

    async void BootOptToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingNewUi) return;
        bool wantSysMain = ChkOptSysMain.IsChecked == true;
        bool wantSearch = ChkOptSearch.IsChecked == true;
        bool wantUpd = ChkOptUpdaters.IsChecked == true;

        BootOptStatus.Text = "Đang áp dụng…";
        try
        {
            string msg = "";
            if (sender == ChkOptSysMain)
            {
                await Task.Run(() => BootOptimizerService.SetSysMainDisabled(wantSysMain));
                msg = wantSysMain ? "✓ Đã tắt nạp trước SysMain." : "Đã bật lại SysMain.";
            }
            else if (sender == ChkOptSearch)
            {
                int dirs = await Task.Run(() => BootOptimizerService.SetSearchOptimized(wantSearch));
                msg = wantSearch ? $"✓ Đã hoãn Windows Search & loại trừ {dirs} thư mục cache/code." : "Đã trả Windows Search về mặc định.";
            }
            else if (sender == ChkOptUpdaters)
            {
                _settings.MuteLogonUpdaters = wantUpd;
                _settings.Save();
                int cnt = await Task.Run(() => BootOptimizerService.SetLogonUpdatersMuted(wantUpd));
                msg = wantUpd ? $"✓ Đã hoãn {cnt} tác vụ cập nhật chạy lúc mở máy." : $"Đã bật lại {cnt} tác vụ cập nhật.";
            }
            BootOptStatus.Text = msg;
            if (msg.Length > 0) ShowToast(msg.TrimStart('✓', ' '));
        }
        catch (Exception ex) { BootOptStatus.Text = "Lỗi: " + ex.Message; }

        await RefreshBootOptUiAsync();
    }

    async void BootOptAll_Click(object sender, RoutedEventArgs e)
    {
        BtnBootOptAll.IsEnabled = false;
        BootOptStatus.Text = "Đang tối ưu toàn bộ khởi động…";
        try
        {
            _settings.MuteLogonUpdaters = true;
            _settings.Save();
            var (dirs, tasks) = await Task.Run(() =>
            {
                BootOptimizerService.SetSysMainDisabled(true);
                int d = BootOptimizerService.SetSearchOptimized(true);
                int t = BootOptimizerService.SetLogonUpdatersMuted(true);
                return (d, t);
            });
            BootOptStatus.Text = $"✓ Hoàn tất: tắt SysMain, loại trừ {dirs} thư mục cache, hoãn {tasks} tác vụ đầu giờ.";
            ShowToast("Đã tối ưu chống 100% Disk lúc mở máy");
        }
        catch (Exception ex) { BootOptStatus.Text = "Lỗi: " + ex.Message; }

        await RefreshBootOptUiAsync();
    }

    // ================= 2. Mini Tray Flyout =================
    public void ToggleTrayFlyout()
    {
        _flyout ??= new TrayFlyoutWindow(this);
        _flyout.ToggleAtTray();
    }

    public string CurrentProfileKey => Profile.All.FirstOrDefault(p => p.Name == _profileName)?.Key ?? "";

    public string CurrentFanQuickKey => _fans switch
    {
        null => "auto",
        { Mode: FanMode.Auto } => "auto",
        { Mode: FanMode.Curve } => _fans.Curve.Key,
        _ => "",
    };

    public bool IsFanControlAvailable => _fans is not null;
    public bool IsMeetingOn => _meeting;
    public bool IsMacKeysOn => _keySwap.Enabled;
    public bool IsTouchBarFnOn => _tbFnDefault;
    public double CurrentBrightness => SlBright.Value;
    public bool IsBrightnessEnabled => SlBright.IsEnabled;
    public double CurrentKbdBacklight => SlKbd.Value;
    public bool IsKbdEnabled => SlKbd.IsEnabled;

    public string FlyoutTelemetrySummary
    {
        get
        {
            string cpu = _lastSnap?.CpuPackageTemp is float ct ? $"CPU {ct:0}°C" : "CPU –";
            string gpu = _lastSnap?.Dgpu?.TempC is float gt ? $" · GPU {gt:0}°C" : "";
            string bat = OvBattery.Text != "–" ? $" · Pin {OvBattery.Text}" : "";
            return cpu + gpu + bat;
        }
    }

    public string FlyoutFanSummary => _lastFanShort;

    public void ApplyProfileByKey(string key)
    {
        var p = Profile.All.FirstOrDefault(x => x.Key == key);
        if (p is null) return;
        try
        {
            p.Apply();
            LoadPowerState();
            ShowToast($"Đã chuyển sang {p.Name}");
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
    }

    public void ApplyQuickFanByKey(string key)
    {
        if (_fans is null) return;
        try
        {
            if (key == "auto")
            {
                _fans.SetAuto();
                SelectFanTile(FanMode.Auto);
                FixedPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                var c = FanCurve.All.FirstOrDefault(x => x.Key == key);
                if (c is null) return;
                _fans.SetCurve(c);
                SelectFanTile(c);
                FixedPanel.Visibility = Visibility.Collapsed;
            }
            SaveFanSettings();
            FanStatus.Text = _fans.Status;
        }
        catch (Exception ex) { ShowToast("Lỗi quạt: " + ex.Message); }
    }

    public void SetBrightnessExternal(double pct)
    {
        if (!SlBright.IsEnabled) return;
        SlBright.Value = Math.Clamp(pct, 0, 100);
    }

    public void SetKbdExternal(double pct)
    {
        if (!SlKbd.IsEnabled) return;
        SlKbd.Value = Math.Clamp(pct, 0, 100);
    }

    // ================= 3. Mac Keys (⌘ ↔ Ctrl) & Touch Bar Mode =================
    void SyncInputControls()
    {
        _syncingNewUi = true;
        try
        {
            bool mac = _keySwap.Enabled;
            ChkOvMacKeys.IsChecked = mac;
            ChkSysMacKeys.IsChecked = mac;
            OvMacKeysDesc.Text = mac ? "Đang bật ⌘ ↔ Ctrl" : "Đảo Command ↔ Ctrl";

            ChkOvTbFn.IsChecked = _tbFnDefault;
            RbTbFn.IsChecked = _tbFnDefault;
            RbTbMedia.IsChecked = !_tbFnDefault;
            OvTbFnDesc.Text = _tbFnDefault ? "Đang hiện F1–F12" : "Đang hiện Media";
        }
        finally { _syncingNewUi = false; }
    }

    public void SetMacKeys(bool on)
    {
        _keySwap.SetEnabled(on);
        _settings.SwapCommandCtrl = on;
        _settings.Save();
        SyncInputControls();
        _flyout?.SyncFromMain();
        ShowToast(on ? "Đã bật phím kiểu Mac (⌘ ↔ Ctrl)" : "Đã trả về phím Windows mặc định");
    }

    public void SetTouchBarFn(bool fnDefault)
    {
        if (_compatReport is not null && !_compatReport.CanWrite("TouchBar"))
        {
            SyncInputControls();
            ShowToast("Máy này không có Touch Bar");
            return;
        }
        _tbFnDefault = fnDefault;
        bool live = false;
        try { live = AppleKeyboardService.SetTouchBarFnDefault(fnDefault); } catch { }
        SyncInputControls();
        _flyout?.SyncFromMain();
        string modeName = fnDefault ? "Hàng phím F1–F12" : "Thanh Media & Độ sáng";
        ShowToast(live ? $"Touch Bar: {modeName}" : $"Đã lưu Touch Bar ({modeName})");
    }

    void OvMacKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingNewUi) SetMacKeys(ChkOvMacKeys.IsChecked == true);
    }

    void SysMacKeys_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingNewUi) SetMacKeys(ChkSysMacKeys.IsChecked == true);
    }

    void OvTbFn_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingNewUi) SetTouchBarFn(ChkOvTbFn.IsChecked == true);
    }

    void TbMode_Click(object sender, RoutedEventArgs e)
    {
        if (!_syncingNewUi) SetTouchBarFn(RbTbFn.IsChecked == true);
    }

    // ================= 3b. Boot Camp Hardware Fixes =================
    async Task RefreshBcFixesUiAsync()
    {
        var st = await Task.Run(BootCampFixesService.GetState);
        _syncingNewUi = true;
        try
        {
            ChkFixAudioPop.IsChecked = st.AudioPopFixed;
            ChkFixWireless.IsChecked = st.WirelessOptimized;
            ChkNaturalMouse.IsChecked = st.NaturalMouseWheel;
            BtnBootMacOs.Visibility = st.CanBootMacOs ? Visibility.Visible : Visibility.Collapsed;
            BtnBcFixAll.IsEnabled = !(st.AudioPopFixed && st.WirelessOptimized);
        }
        finally { _syncingNewUi = false; }
    }

    async void BcFixToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingNewUi) return;
        bool wantAudio = ChkFixAudioPop.IsChecked == true;
        bool wantWireless = ChkFixWireless.IsChecked == true;
        bool wantMouse = ChkNaturalMouse.IsChecked == true;

        BcFixStatus.Text = "Đang áp dụng…";
        try
        {
            string msg = "";
            if (sender == ChkFixAudioPop)
            {
                int n = await Task.Run(() => BootCampFixesService.SetAudioPopFixed(wantAudio));
                msg = wantAudio
                    ? $"✓ Đã khóa ngủ mạch loa ({n} thiết bị) – có hiệu lực trọn vẹn sau khi khởi động lại."
                    : "Đã trả chế độ tiết kiệm điện loa về mặc định.";
            }
            else if (sender == ChkFixWireless)
            {
                int n = await Task.Run(() => BootCampFixesService.SetWirelessOptimized(wantWireless));
                msg = wantWireless
                    ? $"✓ Đã khóa tắt ngầm Bluetooth & tắt gom gói Wi-Fi ({n} adapter)."
                    : "Đã trả cài đặt Bluetooth & Wi-Fi về mặc định.";
            }
            else if (sender == ChkNaturalMouse)
            {
                int n = await Task.Run(() => BootCampFixesService.SetNaturalMouseWheel(wantMouse));
                msg = wantMouse
                    ? $"✓ Đã bật cuộn tự nhiên cho {n} chuột rời (rút cắm lại chuột hoặc khởi động lại để nhận)."
                    : $"Đã trả chiều cuộn mặc định Windows cho {n} chuột rời.";
            }
            BcFixStatus.Text = msg;
            if (msg.Length > 0) ShowToast(msg.TrimStart('✓', ' '));
        }
        catch (Exception ex) { BcFixStatus.Text = "Lỗi: " + ex.Message; }

        await RefreshBcFixesUiAsync();
    }

    async void BcFixAll_Click(object sender, RoutedEventArgs e)
    {
        BtnBcFixAll.IsEnabled = false;
        BcFixStatus.Text = "Đang áp dụng…";
        try
        {
            var (aud, net) = await Task.Run(() =>
            {
                int a = BootCampFixesService.SetAudioPopFixed(true);
                int w = BootCampFixesService.SetWirelessOptimized(true);
                return (a, w);
            });
            BcFixStatus.Text = $"✓ Đã chống bụp loa ({aud} mạch) & ổn định Bluetooth/Wi-Fi ({net} adapter). Khởi động lại máy 1 lần để có hiệu lực trọn vẹn.";
            ShowToast("Đã bật chống bụp loa & ổn định Bluetooth/Wi-Fi");
        }
        catch (Exception ex) { BcFixStatus.Text = "Lỗi: " + ex.Message; }

        await RefreshBcFixesUiAsync();
    }

    void BootMacOs_Click(object sender, RoutedEventArgs e)
    {
        try { BootCampFixesService.OpenStartupDisk(); }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
    }

    // ================= 4. Discrete AMD GPU Telemetry =================
    void UpdateGpuOverview(SensorSnapshot s)
    {
        var g = s.Dgpu;
        if (g is null)
        {
            GpuOverviewCard.Visibility = Visibility.Collapsed;
            return;
        }

        GpuOverviewCard.Visibility = Visibility.Visible;
        OvGpuName.Text = g.Name;
        OvGpuTemp.Text = g.TempC is float t ? $"{t:0}°C" : "–";
        OvGpuTemp.Foreground = (g.TempC ?? 0) >= 88 ? (Brush)FindResource("Red")
                             : (g.TempC ?? 0) >= 75 ? (Brush)FindResource("Orange") : (Brush)FindResource("Text");
        OvGpuLoad.Text = g.LoadPct is float l ? $"{l:0}%" : "–";
        OvGpuPower.Text = g.PowerW is float w ? $"{w:0.0} W" : "–";

        if (g.VramUsedMB is float used)
        {
            float total = g.VramTotalMB is > 0 ? g.VramTotalMB.Value : 8192f;
            OvGpuVram.Text = $"{used / 1024.0:0.0} / {total / 1024.0:0} GB";
            OvGpuVramBar.Value = Math.Clamp(used / total * 100.0, 0, 100);
        }
        else
        {
            OvGpuVram.Text = "–";
            OvGpuVramBar.Value = 0;
        }

        string clk = g.CoreClockMHz is float mhz ? $"{mhz:0} MHz" : "";
        OvGpuApps.Text = _gpuAppsText.Length > 0
            ? $"Đang dùng VRAM: {_gpuAppsText}"
            : (clk.Length > 0 ? $"Xung nhân {clk} · tiết kiệm điện" : "Hoạt động ổn định");

        if (_tick % 3 == 1 && !_gpuAppsBusy)
        {
            _gpuAppsBusy = true;
            _ = Task.Run(() =>
            {
                var apps = GpuPreferenceService.TopActiveGpuApps(3);
                string summary = apps.Count == 0 ? "" : string.Join(" · ", apps.Select(a =>
                    a.VramMB >= 1024 ? $"{a.Name} ({a.VramMB / 1024.0:0.0} GB)" : $"{a.Name} ({a.VramMB:0} MB)"));
                Dispatcher.BeginInvoke(() =>
                {
                    _gpuAppsText = summary;
                    _gpuAppsBusy = false;
                });
            });
        }
    }

    // ================= 5. Charger Input W & System Output W =================
    void UpdatePowerFlow(SensorSnapshot s, SmcPowerInfo? smcPower, BatteryInfo? b)
    {
        bool plugged = b?.PluggedIn == true;
        double batW = b?.RateW ?? 0; // >0 charging into battery, <0 discharging from battery

        double inW;
        if (!plugged)
            inW = 0;
        else if (smcPower?.DcInWatts is double dc && dc >= 0.5)
            inW = dc;
        else if (smcPower?.SystemWatts is double sw && sw >= 1.0)
            inW = sw + Math.Max(0, batW);
        else
            inW = (s.CpuPackagePower ?? 0) + (s.Dgpu?.PowerW ?? 0) + 7.5 + Math.Max(0, batW);

        double outW;
        if (smcPower?.SystemWatts is double sys && sys >= 1.0)
            outW = sys;
        else if (!plugged && batW < -0.5)
            outW = -batW;
        else if (plugged && smcPower?.DcInWatts is double dc2 && dc2 > Math.Max(0, batW) + 1.0)
            outW = dc2 - Math.Max(0, batW);
        else
            outW = (s.CpuPackagePower ?? 0) + (s.Dgpu?.PowerW ?? 0) + 7.5;

        LastPowerInW = inW;
        LastPowerOutW = outW;

        int maxAdapter = smcPower?.AdapterRatedWatts ?? 96;

        // Overview: Sạc vào card
        OvChargeIn.Text = plugged ? $"{inW:0.0} W" : "0 W";
        OvChargeIn.Foreground = (Brush)FindResource(plugged ? "Green" : "MutedBrush");
        OvChargeInBar.Value = plugged ? Math.Clamp(inW / maxAdapter * 100.0, 0, 100) : 0;
        if (!plugged)
        {
            OvChargeInSub.Text = "Đang chạy pin (rút sạc)";
        }
        else
        {
            string adapter = smcPower?.AdapterRatedWatts is int rw ? $"Củ {rw}W" : "Đã cắm sạc";
            string intoBat = batW > 0.5 ? $" · nạp pin +{batW:0.0}W"
                           : batW < -0.5 ? $" · bù pin {batW:0.0}W"
                           : " · pin đầy";
            OvChargeInSub.Text = adapter + intoBat;
        }

        // Overview: Đang sử dụng card
        OvPower.Text = $"{outW:0.0} W";
        OvPowerBar.Value = Math.Clamp(outW, 0, 100);
        string cpuStr = s.CpuPackagePower is float cp ? $"CPU {cp:0.0}W" : "CPU –";
        string gpuStr = s.Dgpu?.PowerW is float gp ? $" · GPU {gp:0.0}W" : "";
        OvPowerSub.Text = cpuStr + gpuStr;

        ChartPower.Push((float)outW);
        ChartPowerNow.Text = OvPower.Text;

        // Battery page: Sạc vào / Đang sử dụng / Vào-Ra pin
        BatPowerIn.Text = plugged ? $"{inW:0.0} W" : "0 W";
        BatPowerIn.Foreground = (Brush)FindResource(plugged ? "Green" : "MutedBrush");
        BatPowerInSub.Text = !plugged ? "Chưa cắm sạc"
                           : smcPower?.AdapterRatedWatts is int rw2 ? $"Củ sạc {rw2} W" : "Nguồn Adapter";

        BatPowerOut.Text = $"{outW:0.0} W";
        BatRateSub.Text = batW > 0.5 ? "Đang nạp vào pin" : batW < -0.5 ? "Đang xả từ pin" : "Giữ mức pin";
        BatRate.Foreground = batW > 0.5 ? (Brush)FindResource("Green")
                           : batW < -0.5 ? (Brush)FindResource("Orange") : (Brush)FindResource("Text");

        ChartBattery.Push((float)outW);

        // Sidebar compact summary
        SideTemp.Text = $"CPU {OvTemp.Text} · Dùng {outW:0.0}W";
        SideBattery.Text = plugged
            ? $"Pin {OvBattery.Text} · Vào {inW:0.0}W"
            : $"Pin {OvBattery.Text} · Xả {outW:0.0}W";
    }
}
