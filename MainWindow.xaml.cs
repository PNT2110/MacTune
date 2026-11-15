using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MacTune.Services;

namespace MacTune;

public record TempRow(string Name, double Value, string Display, Brush Brush);
public record FanRow(string Name, string Rpm, double Pct, string Range, string Badge, Brush BadgeBg, Brush BadgeFg);

public partial class MainWindow : Window
{
    SensorService? _sensors;
    SmcService? _smc;
    FanController? _fans;
    string? _smcError;
    SensorSnapshot? _lastSnap;
    BatteryInfo? _lastBat;
    readonly DispatcherTimer _fanSliderTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    public static MainWindow? Current { get; private set; }
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _brightTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    System.Windows.Forms.NotifyIcon? _tray;
    bool _busy, _loading, _exiting;
    int _tick;

    static readonly (string Name, int Value)[] BoostModes =
    {
        ("Tắt", 0), ("Bật", 1), ("Mạnh", 2), ("Tiết kiệm", 3), ("Mạnh · tiết kiệm", 4),
    };

    // cached per-source advanced values: [0]=AC, [1]=DC
    readonly int[] _boost = new int[2], _max = new int[2], _min = new int[2], _epp = new int[2], _cores = new int[2];

    public MainWindow()
    {
        InitializeComponent();
        if (typeof(MainWindow).Assembly.GetName().Version is { } ver)
        {
            SideVersion.Text = ver.Build > 0 ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : $"v{ver.Major}.{ver.Minor}";
            AboutVersion.Text = $"v{ver.Major}.{ver.Minor}.{Math.Max(0, ver.Build)}";
        }
        Loc.SetLanguage(_settings.Language);
        Loc.Attach(this);
        RbLangEn.IsChecked = Loc.IsEnglish;
        RbLangVi.IsChecked = !Loc.IsEnglish;
        BuildProfileTiles(OvProfiles);
        BuildProfileTiles(PerfProfiles);
        foreach (var (name, value) in BoostModes)
            BoostSeg.Children.Add(new RadioButton { Content = name, Tag = value, GroupName = "boost", Style = (Style)FindResource("Seg") });

        _toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _brightTimer.Tick += (_, _) =>
        {
            _brightTimer.Stop();
            try { SystemService.SetBrightness((int)SlBright.Value); } catch (Exception ex) { ShowToast("Lỗi độ sáng: " + ex.Message); }
        };

        _fanSliderTimer.Tick += (_, _) =>
        {
            _fanSliderTimer.Stop();
            _fans?.SetFixed(SlFan.Value / 100); SaveFanSettings();
        };

        Current = this;
        SourceInitialized += (_, _) => EnableDarkTitleBar();
        Loaded += async (_, _) => await InitAsync();
        Closing += OnClosing;
        SetupTray();
    }

    bool _initStarted;
    string _profileName = "";

    public async Task InitAsync()
    {
        if (_initStarted) return;
        _initStarted = true;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string step) => DiagLog.Write($"startup {step}: {sw.ElapsedMilliseconds} ms");
        DiagLog.Write($"MacTune {AboutVersion.Text} — startup begin: {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds:0} ms after process start");

        // let the window paint before doing any slow work
        await Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        MachineInfo.Text = await Task.Run(SystemService.MachineSummary);
        Mark("machine info");
        InitFeatures();
        Mark("features");
        InitNewFeatures();
        InitDiagnostics();
        Mark("new features");
        LoadTimers();
        LoadLid();
        Mark("timers+lid");
        InitKeyboard();
        Mark("keyboard");
        LoadPowerState();
        Mark("power state");
        RefreshBattery();
        Mark("battery");
        _loading = true;
        if (SystemService.GetBrightness() is int br) SlBright.Value = br; else SlBright.IsEnabled = false;
        ChkStartup.IsChecked = SystemService.IsStartupEnabled();
        _loading = false;
        Mark("brightness+startup flag");

        try { _sensors = await Task.Run(() => new SensorService()); }
        catch (Exception ex) { SensorWarning.Text = "Không mở được cảm biến phần cứng: " + ex.Message; }

        Mark("sensors (LHM)");
        _smc = await Task.Run(() => SmcService.TryOpen(out _smcError));
        Mark("smc");
        string backend = _smc?.Backend ?? "none";
        DiagLog.Write($"SMC backend={backend} fans={(_smc is null ? 0 : _smc.FanCount)} err={_smcError}");
        if (_smc is not null)
            foreach (var k in new[] { "FNum", "F0Ac", "F0Md", "F0Tg", "F0Mx", "TC0P", "PDTR", "PSTR", "VD0R", "ID0R", "AC-W", "PPBR" })
                DiagLog.Write($"  key {k}: {_smc.Info(k)} = {_smc.ReadNumber(k)}");
        if (_smc is not null) _fans = new FanController(_smc);
        BuildFanTiles();
        RestoreFanMode();
        LoadChargeLimit();
        await InitHardwareProbesAsync();

        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        await TickAsync();
        Mark("first tick (done)");
    }

    // ================= Language =================
    void Lang_Click(object sender, RoutedEventArgs e)
    {
        _settings.Language = RbLangEn.IsChecked == true ? "en" : "vi";
        _settings.Save();
        Loc.SetLanguage(_settings.Language);
        LocalizeTrayMenu();
    }

    void LocalizeTrayMenu(System.Windows.Forms.ContextMenuStrip? menu = null)
    {
        menu ??= _tray?.ContextMenuStrip;
        if (menu is null) return;
        foreach (System.Windows.Forms.ToolStripItem item in menu.Items)
        {
            if (item is System.Windows.Forms.ToolStripSeparator) continue;
            item.Tag ??= item.Text;            // keep the Vietnamese source
            item.Text = Loc.T((string)item.Tag!);
        }
    }

    // ================= Navigation =================
    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageOverview is null) return;
        var pages = new UIElement[] { PageOverview, PagePerf, PageBattery, PageFans, PageAuto, PageClean, PageSystem, PageSettings };
        var navs = new[] { NavOverview, NavPerf, NavBattery, NavFans, NavAuto, NavClean, NavSystem, NavSettings };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = navs[i] == sender ? Visibility.Visible : Visibility.Collapsed;
        if (sender == NavPerf) RefreshBackupUi();
        if (sender == NavSystem) { _ = RefreshProcessesAsync(); RefreshGpuList(); LoadTimers(); LoadLid(); SyncInputControls(); _ = RefreshBcFixesUiAsync(); _ = RefreshCompatibilityUiAsync(); }
        if (sender == NavClean) { RefreshStartup(); ShowMemory(); ShowDrives(); _ = RefreshBootOptUiAsync(); }
        if (sender == NavBattery) ShowEstimates();
        if (sender == NavAuto) RefreshRules();
    }

    // ================= Telemetry loop =================
    async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            _tick++;
            var lhm = _sensors;
            var smc = _smc;
            var (s, fans, smcTemps, smcCpu, smcPwr) = await Task.Run(() =>
            {
                var snap = lhm?.Read();
                IReadOnlyList<FanInfo> f = Array.Empty<FanInfo>();
                List<(string, float)> t = new();
                double? cpu = null;
                SmcPowerInfo? pwr = null;
                if (smc is not null)
                {
                    try { f = smc.Fans(); t = smc.Temperatures(); cpu = smc.HottestCpu(); pwr = smc.ReadPower(); } catch { }
                }
                return (snap, f, t, cpu, pwr);
            });

            // Merge: prefer LHM package temp; fall back to SMC (works without extra drivers)
            var temps = new List<(string Name, float Celsius)>(s?.Temps ?? Array.Empty<(string, float)>());
            temps.AddRange(smcTemps);
            float? cpuTemp = s?.CpuPackageTemp ?? (float?)smcCpu;
            var merged = new SensorSnapshot(cpuTemp, s?.CpuPackagePower, s?.CpuMaxClockMHz, s?.CpuLoad, temps, s?.Fans ?? Array.Empty<(string, float)>())
            {
                Drives = s?.Drives ?? Array.Empty<DriveHealth>(),
                BatteryDesignMWh = s?.BatteryDesignMWh,
                Dgpu = s?.Dgpu,
            };
            _lastSnap = merged;
            RefreshBattery();
            ShowSensors(merged);
            UpdatePowerFlow(merged, smcPwr, _lastBat);
            ShowFans(fans);
            DiagnosticsTick(merged, fans, smcPwr);

            if (_fans is not null)
            {
                var fc = _fans;
                float? thermalTemp = (cpuTemp, merged.Dgpu?.TempC) switch
                {
                    (float c, float g) => Math.Max(c, g),
                    (float c, null) => c,
                    (null, float g) => g,
                    _ => null,
                };
                await Task.Run(() => fc.Tick(thermalTemp));
                FanStatus.Text = fc.Status;
            }
            UpdateTips();
            FeaturesTick(cpuTemp);
            if (_flyout?.IsVisible == true) _flyout.SyncFromMain();
            if (PageSystem.Visibility == Visibility.Visible && _tick % 2 == 0) await RefreshProcessesAsync();
        }
        catch (Exception ex) { SensorWarning.Text = "Lỗi đọc cảm biến: " + ex.Message; }
        finally { _busy = false; }
    }

    void ShowSensors(SensorSnapshot s)
    {
        OvTemp.Text = s.CpuPackageTemp is float t ? $"{t:0}°C" : "–";
        OvTempBar.Value = s.CpuPackageTemp ?? 0;
        OvTemp.Foreground = (s.CpuPackageTemp ?? 0) >= 90 ? (Brush)FindResource("Red")
                          : (s.CpuPackageTemp ?? 0) >= 75 ? (Brush)FindResource("Orange") : (Brush)FindResource("Text");
        OvClock.Text = s.CpuMaxClockMHz is float c ? $"Xung nhịp {c / 1000:0.00} GHz" : "Xung nhịp –";

        ChartTemp.Push(s.CpuPackageTemp);
        ChartTempNow.Text = OvTemp.Text;

        SensorWarning.Text = s.CpuPackageTemp is null
            ? "Chưa đọc được nhiệt độ CPU." :
            s.CpuPackagePower is null
            ? "Cần driver PawnIO để đọc công suất & xung nhịp CPU." : "";

        TempList.ItemsSource = s.Temps.OrderByDescending(x => x.Celsius).Select(x => new TempRow(
            x.Name.Replace("SMC · ", ""), x.Celsius, $"{x.Celsius:0}°C",
            x.Celsius >= 90 ? (Brush)FindResource("Red") : x.Celsius >= 70 ? (Brush)FindResource("Orange") : (Brush)FindResource("Green")))
            .ToList();

        UpdateGpuOverview(s);
    }

    // ================= Fans =================
    readonly List<RadioButton> _fanTiles = new();

    void BuildFanTiles()
    {
        FanModeTiles.Children.Clear();
        _fanTiles.Clear();
        void Add(string icon, string name, string desc, Action onClick, object tag)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = icon, FontSize = 18, Margin = new Thickness(0, 0, 0, 4) });
            sp.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 13.5 });
            sp.Children.Add(new TextBlock { Text = desc, Style = (Style)FindResource("Muted"), FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            var rb = new RadioButton { Content = sp, Tag = tag, Style = (Style)FindResource("ProfileTile"), GroupName = "fanmode" };
            rb.Click += (_, _) =>
            {
                try { onClick(); SaveFanSettings(); FixedPanel.Visibility = tag is FanMode.Fixed ? Visibility.Visible : Visibility.Collapsed; FanStatus.Text = _fans?.Status; }
                catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
            };
            _fanTiles.Add(rb);
            FanModeTiles.Children.Add(rb);
        }

        if (_fans is null)
        {
            FanModeTiles.IsEnabled = false;
            FanStatus.Text = "Chưa kết nối SMC";
            var btn = new Button { Content = "Hướng dẫn bật quạt", Style = (Style)FindResource("Primary"),
                                   HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(5, 10, 0, 0) };
            btn.Click += (_, _) =>
            {
                string enGuide = System.IO.Path.Combine(AppContext.BaseDirectory, "docs", "FAN-SETUP.md");
                string viGuide = System.IO.Path.Combine(AppContext.BaseDirectory, "HUONG-DAN-QUAT.txt");
                string guide = Loc.IsEnglish && System.IO.File.Exists(enGuide) ? enGuide : viGuide;
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(guide) { UseShellExecute = true }); }
                catch { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{guide}\"")); }
            };
            FanControls.Children.Add(btn);
        }

        Add("🍏", "Tự động", "Mặc định", () => _fans!.SetAuto(), FanMode.Auto);
        foreach (var c in FanCurve.All)
            Add(c.Key == "quiet" ? "🤫" : c.Key == "cool" ? "❄" : "⚖", c.Name, c.Description, () => _fans!.SetCurve(c), c);
        Add("🎚", "Cố định", "Tự chọn", () => _fans!.SetFixed(SlFan.Value / 100), FanMode.Fixed);
        _fanTiles[0].IsChecked = true;
        SlFan.Value = 50;
    }

    void SlFan_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FixedLabel is null) return;
        FixedLabel.Text = $"{e.NewValue:0}%";
        if (_fans?.Mode == FanMode.Fixed) { _fanSliderTimer.Stop(); _fanSliderTimer.Start(); }
    }

    void ShowFans(IReadOnlyList<FanInfo> fans)
    {
        if (fans.Count == 0) { FanGauges.ItemsSource = null; _lastFanShort = _fans?.Status ?? "Tự động"; return; }
        var accentSoft = (Brush)FindResource("AccentSoft");
        var subtle = (Brush)FindResource("SubtleBg");
        FanGauges.ItemsSource = fans.Select(f => new FanRow(
            f.Name, $"{f.Actual:0} RPM",
            f.Max > f.Min ? Math.Clamp((f.Actual - f.Min) / (f.Max - f.Min) * 100, 0, 100) : 0,
            $"{f.Min:0} – {f.Max:0} RPM",
            f.Manual ? "Thủ công" : "Tự động",
            f.Manual ? accentSoft : subtle,
            f.Manual ? (Brush)FindResource("Accent") : (Brush)FindResource("MutedBrush"))).ToList();
        _lastFanShort = string.Join(" · ", fans.Select(f => $"{f.Actual:0}")) + " RPM";
        SideFan.Text = "Quạt " + string.Join(" · ", fans.Select(f => $"{f.Actual:0}"));
    }

    public void RestoreFansToApple()
    {
        try { if (_fans is not null && _fans.Mode != FanMode.Auto) _fans.SetAuto(); else _smc?.AllFansAuto(); }
        catch { /* best effort during shutdown */ }
    }

    // ================= Charge limit =================
    void LoadChargeLimit()
    {
        if (_smc?.ChargeLimitKey is null)
        {
            ChkChargeLimit.IsEnabled = false;
            ChargeLimitStatus.Text = _smc is null
                ? "Chưa kết nối SMC."
                : "Không hỗ trợ từ Windows trên máy này.";
            return;
        }
        int? limit = _smc.ReadChargeLimit();
        ChkChargeLimit.IsChecked = limit == 80;
        ChargeLimitStatus.Text = limit == 80 ? "Đang bật · dừng sạc ở 80%" : "Đang tắt · sạc tối đa 100%";
    }

    void ChkChargeLimit_Click(object sender, RoutedEventArgs e)
    {
        int want = ChkChargeLimit.IsChecked == true ? 80 : 100;
        bool ok = false;
        try { ok = _smc?.SetChargeLimit(want) ?? false; } catch { }
        ShowToast(ok ? (want == 80 ? "Giới hạn sạc 80%" : "Sạc tối đa 100%") : "SMC từ chối lệnh");
        LoadChargeLimit();
    }

    // ================= Friendly tips =================
    void UpdateTips()
    {
        var tips = new List<string>();
        int level = 0; // 0 good, 1 note, 2 warning
        var s = _lastSnap; var b = _lastBat;

        if (s?.CpuPackageTemp is float t)
        {
            if (t >= 90) { tips.Add($"🔥 CPU rất nóng ({t:0}°C) · Nên bật quạt “Mát tối đa”"); level = 2; }
            else if (t >= 82) { tips.Add($"🌡 CPU khá nóng ({t:0}°C) · Kê thoáng máy hoặc giảm Turbo"); level = Math.Max(level, 1); }
        }
        if (b?.HealthPercent is double h && h < 78 && ChkChargeLimit.IsChecked != true && ChkChargeLimit.IsEnabled)
        {
            tips.Add($"🔋 Pin còn {h:0}% · Nên bật giới hạn sạc 80%");
            level = Math.Max(level, 1);
        }
        if (b?.PluggedIn == false && _profileName == "Hiệu năng")
        {
            tips.Add("⚡ Đang dùng pin ở chế độ Hiệu năng · Chuyển “Cân bằng” để tiết kiệm hơn");
            level = Math.Max(level, 1);
        }
        if (b?.PluggedIn == false && s?.Dgpu?.PowerW is float gw && gw >= 18)
        {
            tips.Add($"🎮 Card rời AMD đang ăn {gw:0} W trên pin" + (_gpuAppsText.Length > 0 ? $" ({_gpuAppsText})" : "") + " · Đóng bớt ứng dụng đồ họa nặng");
            level = Math.Max(level, 1);
        }

        HealthEmoji.Text = level switch { 2 => "🥵", 1 => "⚠", _ => "😊" };
        HealthTitle.Text = level switch { 2 => "Đang nóng", 1 => "Chú ý", _ => "Ổn định" };
        HealthBanner.Background = (Brush)FindResource(level switch { 2 => "RedSoft", 1 => "OrangeSoft", _ => "GreenSoft" });
        HealthBanner.BorderBrush = new SolidColorBrush(level switch
        {
            2 => Color.FromRgb(0xFC, 0xA5, 0xA5),
            1 => Color.FromRgb(0xFC, 0xD9, 0xA4),
            _ => Color.FromRgb(0xBC, 0xEB, 0xD7),
        });
        Tips.ItemsSource = tips;
        TipsCard.Visibility = tips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================= Battery =================
    void RefreshBattery()
    {
        try
        {
            var b = BatteryService.Read();
            if (b.CycleCount is null && _smc?.CycleCount() is int smcCycles) b = b with { CycleCount = smcCycles };
            if (b.DesignMWh is null && _lastSnap?.BatteryDesignMWh is int chipDesign) b = b with { DesignMWh = chipDesign };
            _lastBat = b;
            int design = b.DesignMWh ?? BatteryInfo.FallbackDesignMWh;
            double? pct = b.RemainingMWh is int r && b.FullMWh is int f ? 100.0 * r / f : null;

            string state = b.PluggedIn == true ? (b.Charging == true ? "Đang sạc" : "Đã cắm sạc") : "Chạy pin";
            OvBattery.Text = pct is double p0 ? $"{p0:0}%" : "–";
            OvBatteryBar.Value = pct ?? 0;
            SideBattery.Text = $"Pin {OvBattery.Text} · {state}";

            BatLevel.Text = OvBattery.Text;
            BatHealth.Text = b.HealthPercent is double h ? $"{h:0}%" : "–";
            BatHealth.Foreground = (b.HealthPercent ?? 100) < 70 ? (Brush)FindResource("Orange") : (Brush)FindResource("Text");
            BatRate.Text = b.RateW is double rw ? $"{rw:+0.0;-0.0;0} W" : "–";
            BatState.Text = state;

            string time = "–";
            if (b.RateW is double w && Math.Abs(w) > 0.5 && b.RemainingMWh is int rem && b.FullMWh is int full)
            {
                double hours = w < 0 ? rem / 1000.0 / -w : (full - rem) / 1000.0 / w;
                time = $"{(int)hours}h {(int)(hours * 60 % 60):00}m";
                BatState.Text += w < 0 ? $" · còn {time}" : $" · đầy sau {time}";
                OvBatterySub.Text = w < 0 ? $"Xả {w:0.0}W · còn {time}" : $"Đang sạc · đầy sau {time}";
            }
            else
            {
                OvBatterySub.Text = state;
            }
            BatTime.Text = time;

            if (_tick % 5 == 1)
                FillDetails(new (string, string)[]
                {
                    ("Sạc đầy", b.FullMWh is int fm ? $"{fm / 1000.0:0.0} Wh" : "–"),
                    ("Thiết kế", $"{design / 1000.0:0.0} Wh" + (b.DesignMWh is null ? " *" : "")),
                    ("Hiện còn", b.RemainingMWh is int rm ? $"{rm / 1000.0:0.0} Wh" : "–"),
                    ("Điện áp", b.VoltageV is double v ? $"{v:0.00} V" : "–"),
                    ("Chu kỳ sạc", b.CycleCount is int cc ? $"{cc}" : "–"),
                });
        }
        catch (Exception ex) { BatState.Text = "Lỗi đọc pin: " + ex.Message; }
    }

    void FillDetails((string Label, string Value)[] rows)
    {
        BatDetails.Children.Clear();
        BatDetails.RowDefinitions.Clear();
        for (int i = 0; i < rows.Length; i++)
        {
            BatDetails.RowDefinitions.Add(new RowDefinition());
            var l = new TextBlock { Text = rows[i].Label, Style = (Style)FindResource("Muted"), FontSize = 12.5, Margin = new Thickness(0, 3.5, 0, 3.5) };
            var v = new TextBlock { Text = rows[i].Value, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(0, 3.5, 0, 3.5) };
            Grid.SetRow(l, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            BatDetails.Children.Add(l); BatDetails.Children.Add(v);
        }
    }

    void BatteryReport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = PowerService.BatteryReport();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
    }

    // ================= Profiles & performance =================
    void BuildProfileTiles(Panel host)
    {
        foreach (var p in Profile.All)
        {
            var row = new DockPanel();
            var iconBadge = new Border
            {
                Width = 36, Height = 36, CornerRadius = new CornerRadius(10),
                Background = (Brush)FindResource("AccentSoft"),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = p.Icon, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 17,
                    Foreground = (Brush)FindResource("Accent"),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                }
            };
            DockPanel.SetDock(iconBadge, Dock.Left);
            row.Children.Add(iconBadge);

            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(new TextBlock { Text = p.Name, FontSize = 14.5, FontWeight = FontWeights.SemiBold });
            txt.Children.Add(new TextBlock { Text = p.Description, Style = (Style)FindResource("Muted"), FontSize = 11.5, Margin = new Thickness(0, 1.5, 0, 0) });
            row.Children.Add(txt);

            var tile = new RadioButton { Content = row, Tag = p, Style = (Style)FindResource("ProfileTile"), GroupName = "profile_" + host.Name };
            tile.Click += Profile_Click;
            host.Children.Add(tile);
        }
    }

    void Profile_Click(object sender, RoutedEventArgs e)
    {
        var p = (Profile)((RadioButton)sender).Tag;
        try { p.Apply(); ShowToast($"Đã chuyển sang chế độ {p.Name}"); }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
        LoadPowerState();
    }

    void LoadPowerState()
    {
        try
        {
            string plan = PowerService.ActivePlanGuid();
            (_boost[0], _boost[1]) = PowerService.Get(PowerService.Boost);
            (_max[0], _max[1]) = PowerService.Get(PowerService.MaxState);
            (_min[0], _min[1]) = PowerService.Get(PowerService.MinState);
            (_epp[0], _epp[1]) = PowerService.Get(PowerService.Epp);
            (_cores[0], _cores[1]) = PowerService.Get(PowerService.MinCores);

            var active = Profile.Detect(plan, _boost[0]);
            foreach (var host in new[] { OvProfiles, PerfProfiles })
                foreach (RadioButton rb in host.Children) rb.IsChecked = ReferenceEquals(rb.Tag, active);
            SideProfile.Text = _profileName = active?.Name ?? "Tùy chỉnh";
            ShowAdvanced();
        }
        catch (Exception ex) { ShowToast("Lỗi đọc cấu hình điện: " + ex.Message); }
    }

    int Src => PowerDc.IsChecked == true ? 1 : 0;

    void ShowAdvanced()
    {
        _loading = true;
        int i = Src;
        int boostShown = _boost[i] >= 0 ? _boost[i] : 2; // Windows default when hidden/unknown
        foreach (RadioButton rb in BoostSeg.Children) rb.IsChecked = (int)rb.Tag == boostShown;
        SlMax.Value = Math.Max(_max[i], 20);
        SlMin.Value = Math.Max(_min[i], 0);
        SlEpp.IsEnabled = _epp[i] >= 0;
        SlEpp.Value = Math.Max(_epp[i], 0);
        SlCores.IsEnabled = _cores[i] >= 0;
        SlCores.Value = Math.Max(_cores[i], 0);
        _loading = false;
    }

    void CaptureAdvanced()
    {
        int i = Src;
        var sel = BoostSeg.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (sel is not null) _boost[i] = (int)sel.Tag;
        _max[i] = (int)SlMax.Value;
        _min[i] = (int)Math.Min(SlMin.Value, SlMax.Value);
        if (_epp[i] >= 0) _epp[i] = (int)SlEpp.Value;
        if (_cores[i] >= 0) _cores[i] = (int)SlCores.Value;
    }

    void PowerSource_Checked(object sender, RoutedEventArgs e)
    {
        if (SlMax is null || _loading) return;
        // keep the edits made on the other tab before switching
        int other = Src == 0 ? 1 : 0;
        var sel = BoostSeg.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (sel is not null) _boost[other] = (int)sel.Tag;
        _max[other] = (int)SlMax.Value; _min[other] = (int)Math.Min(SlMin.Value, SlMax.Value);
        if (_epp[other] >= 0) _epp[other] = (int)SlEpp.Value;
        if (_cores[other] >= 0) _cores[other] = (int)SlCores.Value;
        ShowAdvanced();
    }

    void SlEpp_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EppLabel is null) return;
        int v = (int)e.NewValue;
        EppLabel.Text = $"{v} · " + (v <= 20 ? "Hiệu năng" : v <= 60 ? "Cân bằng" : "Tiết kiệm");
    }

    void ApplyAdvanced_Click(object sender, RoutedEventArgs e)
    {
        CaptureAdvanced();
        var failed = new List<string>();
        void TrySet(string label, string alias, int[] v)
        {
            // unknown value (-1) on one source → reuse the other; both unknown → skip
            int ac = v[0] >= 0 ? v[0] : v[1], dc = v[1] >= 0 ? v[1] : v[0];
            if (ac < 0) return;
            try { PowerService.Set(alias, ac, dc, apply: false); }
            catch (Exception) { failed.Add(label); }
        }
        TrySet("Turbo", PowerService.Boost, _boost);
        TrySet("Xung tối đa", PowerService.MaxState, _max);
        TrySet("Xung tối thiểu", PowerService.MinState, _min);
        TrySet("EPP", PowerService.Epp, _epp);
        TrySet("Số nhân", PowerService.MinCores, _cores);
        try { PowerService.Run("/setactive SCHEME_CURRENT"); } catch (Exception ex) { failed.Add(ex.Message); }

        PerfStatus.Text = failed.Count == 0
            ? $"✓ Đã áp dụng lúc {DateTime.Now:HH:mm:ss}"
            : $"Đã áp dụng, trừ: {string.Join(", ", failed)} (máy không hỗ trợ)";
        ShowToast(failed.Count == 0 ? "Đã áp dụng cấu hình CPU" : "Áp dụng xong một phần");
        LoadPowerState();
    }

    void ResetAdvanced_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            PowerService.Set(PowerService.Boost, 2, 2, apply: false);
            PowerService.Set(PowerService.MaxState, 100, 100, apply: false);
            PowerService.Set(PowerService.MinState, 5, 5, apply: false);
            if (_epp[0] >= 0) PowerService.Set(PowerService.Epp, 33, 50, apply: false);
            if (_cores[0] >= 0) PowerService.Set(PowerService.MinCores, 100, 10, apply: false);
            PowerService.Run("/setactive SCHEME_CURRENT");
            ShowToast("Đã khôi phục mặc định");
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); }
        LoadPowerState();
    }

    // ================= System page =================
    void SlBright_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        _brightTimer.Stop(); _brightTimer.Start(); // debounce
    }

    async Task RefreshProcessesAsync()
    {
        var list = await Task.Run(() => SystemService.TopProcesses());
        ProcList.ItemsSource = list;
    }

    async void Kill_Click(object sender, RoutedEventArgs e)
    {
        int pid = (int)((Button)sender).Tag;
        var name = (ProcList.ItemsSource as List<ProcInfo>)?.FirstOrDefault(p => p.Pid == pid)?.Name ?? pid.ToString();
        if (MessageBox.Show(this, Loc.T($"Tắt \"{name}\"? Dữ liệu chưa lưu trong ứng dụng đó sẽ mất."), "MacTune",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try { SystemService.Kill(pid); ShowToast($"Đã tắt {name}"); }
        catch (Exception ex) { ShowToast("Không tắt được: " + ex.Message); }
        await RefreshProcessesAsync();
    }

    // ================= Settings =================
    void ChkStartup_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SystemService.SetStartup(ChkStartup.IsChecked == true);
            ShowToast(ChkStartup.IsChecked == true ? "Sẽ khởi động cùng Windows" : "Đã tắt khởi động cùng Windows");
        }
        catch (Exception ex) { ShowToast("Lỗi: " + ex.Message); ChkStartup.IsChecked = SystemService.IsStartupEnabled(); }
    }

    void ChkTop_Click(object sender, RoutedEventArgs e) { Topmost = ChkTop.IsChecked == true; SettingsToggle_Click(sender, e); }

    // ================= Tray =================
    void SetupTray()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Text = "MacTune",
            Visible = true,
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
        };
        _tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) ToggleTrayFlyout(); };
        _tray.DoubleClick += (_, _) => { _flyout?.Hide(); ShowFromTray(); };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mở cửa sổ chính", null, (_, _) => { _flyout?.Hide(); ShowFromTray(); });
        menu.Items.Add("Bảng điều khiển nhanh", null, (_, _) => ToggleTrayFlyout());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        foreach (var p in Profile.All)
            menu.Items.Add($"Chế độ {p.Name}", null, (_, _) =>
            {
                try { p.Apply(); LoadPowerState(); _tray!.ShowBalloonTip(1500, "MacTune", Loc.T($"Đã chuyển sang {p.Name}"), System.Windows.Forms.ToolTipIcon.None); }
                catch (Exception ex) { _tray!.ShowBalloonTip(2000, "MacTune", ex.Message, System.Windows.Forms.ToolTipIcon.Error); }
            });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Bật/tắt chế độ Họp", null, (_, _) => SetMeeting(!_meeting));
        menu.Items.Add("Đảo phím Mac (⌘ ↔ Ctrl)", null, (_, _) => SetMacKeys(!_keySwap.Enabled));
        if (BootCampFixesService.CanOpenStartupDisk())
            menu.Items.Add("Đĩa khởi động macOS…", null, (_, _) => { try { BootCampFixesService.OpenStartupDisk(); } catch { } });
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Thoát", null, (_, _) => ExitApplication());
        LocalizeTrayMenu(menu);
        _tray.ContextMenuStrip = menu;
    }

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        bool keepTop = ChkTop.IsChecked == true;
        Topmost = true;
        SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Activate();
        Focus();
        Topmost = keepTop;
    }

    bool _cleanedUp;

    void CleanupBeforeExit()
    {
        if (_cleanedUp) return;
        _cleanedUp = true;
        _timer.Stop();
        DisposeDiagnostics();
        DisposeNewFeatures();
        RestoreMeetingOnExit();
        if (_activeRule is not null) EndRule(quiet: true);
        RestoreFansToApple();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _smc?.Dispose();
        _sensors?.Dispose();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
    }

    void ExitApplication()
    {
        _exiting = true;
        CleanupBeforeExit();
        Close();
        Application.Current.Shutdown();
    }

    void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_exiting && ChkTray.IsChecked == true)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        CleanupBeforeExit();
        Application.Current.Shutdown();
    }

    // ================= Helpers =================
    void ShowToast(string text)
    {
        ToastText.Text = text;
        Toast.Visibility = Visibility.Visible;
        _toastTimer.Stop(); _toastTimer.Start();
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    void EnableDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int off = 0;
        DwmSetWindowAttribute(hwnd, 20, ref off, sizeof(int));           // light title bar
        int captionColor = 0x00FFFFFF;                                   // white caption (Win11)
        DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));  // caption color (Win11)
    }
}
