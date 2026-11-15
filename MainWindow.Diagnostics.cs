using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MacTune.Services;

namespace MacTune;

public record CompatRow(string Title, string Detail, string Badge, Brush BadgeBg, Brush BadgeFg);
public record SafetyRow(string Name, string Detail, string Badge, Brush BadgeBg, Brush BadgeFg);

/// <summary>
/// Connects the 5 engineering upgrades to the UI:
/// 1) Multi-domain Throttling Diagnostics (Thermal, Power Limit, Current Limit, Firmware)
/// 2) CPU Performance CSV Logger
/// 3) Power Plan Baseline &amp; Manual Backup / Restore
/// 4) Fan Safety Watchdog &amp; 4-scenario Self-Test
/// 5) Hardware Compatibility Probing &amp; Pre-Write Gating
/// </summary>
public partial class MainWindow
{
    CpuMsrProbe? _msrProbe;
    FanSafetyWatchdog? _fanWatchdog;
    readonly CpuLoggerService _cpuLogger = new();
    HardwareCompatibilityReport? _compatReport;
    IReadOnlyList<FanInfo> _lastFans = Array.Empty<FanInfo>();
    SmcPowerInfo? _lastSmcPower;

    void InitDiagnostics()
    {
        CbLogInterval.Items.Clear();
        CbLogInterval.Items.Add(new ComboBoxItem { Content = "1 giây", Tag = 1 });
        CbLogInterval.Items.Add(new ComboBoxItem { Content = "2 giây", Tag = 2 });
        CbLogInterval.Items.Add(new ComboBoxItem { Content = "5 giây", Tag = 5 });
        CbLogInterval.SelectedIndex = 1; // 2s default

        _ = Task.Run(() =>
        {
            PowerPlanBackupService.EnsureBaselineBeforeChange();
            Dispatcher.BeginInvoke(RefreshBackupUi);
        });
    }

    async Task InitHardwareProbesAsync()
    {
        _msrProbe = await Task.Run(() => new CpuMsrProbe());
        AttachFanWatchdog();
        await RefreshCompatibilityUiAsync();
        RefreshThrottleHeaderUi();
    }

    void AttachFanWatchdog()
    {
        _fanWatchdog?.Dispose();
        _fanWatchdog = null;
        if (_fans is null) return;

        _fanWatchdog = new FanSafetyWatchdog(_fans);
        _fanWatchdog.Tripped += msg =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                SelectFanTile(FanMode.Auto);
                FixedPanel.Visibility = Visibility.Collapsed;
                FanStatus.Text = msg;
                ShowToast("Watchdog: Đã trả quạt về Apple tự động để an toàn");
            });
        };
    }

    void DisposeDiagnostics()
    {
        _fanWatchdog?.Dispose();
        _fanWatchdog = null;
        _cpuLogger.Dispose();
        _msrProbe?.Dispose();
        _msrProbe = null;
    }

    // ================= 1. Throttling Diagnostics UI =================
    void RefreshThrottleHeaderUi(MsrLimitSample? msr = null)
    {
        bool hasMsr = msr?.Available ?? (_msrProbe?.IsAvailable == true);
        ThrottleSourceBadgeText.Text = hasMsr ? "MSR + SMC" : "Cảm biến";
        ThrottleSourceBadge.Background = (Brush)FindResource(hasMsr ? "AccentSoft" : "SubtleBg");
        ThrottleSourceBadgeText.Foreground = (Brush)FindResource(hasMsr ? "Accent" : "MutedBrush");

        if (msr?.Pl1Watts is double pl1)
        {
            string pl2Str = msr.Pl2Watts is double pl2 ? $" · PL2 {pl2:0}W" : "";
            ThrottlePlInfo.Text = $"Ngưỡng cấu hình: PL1 {pl1:0}W{pl2Str}";
            ThrottlePlInfo.Visibility = Visibility.Visible;
        }
        else
        {
            ThrottlePlInfo.Visibility = Visibility.Collapsed;
        }
    }

    void UpdateThrottleDomainPills(ThrottleDiagnosis diag, bool hasMsr)
    {
        void StylePill(Border box, TextBlock txt, bool active, bool observable = true)
        {
            if (active)
            {
                box.Background = (Brush)FindResource("OrangeSoft");
                box.BorderBrush = (Brush)FindResource("Orange");
                txt.Foreground = (Brush)FindResource("Orange");
                txt.FontWeight = FontWeights.Bold;
                box.Opacity = 1.0;
            }
            else
            {
                box.Background = (Brush)FindResource("SubtleBg");
                box.BorderBrush = (Brush)FindResource("Line");
                txt.Foreground = (Brush)FindResource("MutedBrush");
                txt.FontWeight = FontWeights.SemiBold;
                box.Opacity = observable ? 1.0 : 0.55;
            }
        }

        StylePill(PillThermBox, PillThermText, diag.ActiveFlags.HasFlag(ThrottleLimitFlags.Thermal));
        StylePill(PillPowerBox, PillPowerText, diag.ActiveFlags.HasFlag(ThrottleLimitFlags.PowerLimit));
        StylePill(PillCurrentBox, PillCurrentText, diag.ActiveFlags.HasFlag(ThrottleLimitFlags.CurrentLimit), observable: hasMsr);
        StylePill(PillFwBox, PillFwText, diag.ActiveFlags.HasFlag(ThrottleLimitFlags.Firmware));
    }

    // ================= 2. CPU Performance Logger (CSV) =================
    void DiagnosticsTick(SensorSnapshot merged, IReadOnlyList<FanInfo> fans, SmcPowerInfo? smcPwr)
    {
        _lastFans = fans;
        _lastSmcPower = smcPwr;
        _fanWatchdog?.Heartbeat();

        if (!_cpuLogger.IsRecording) return;

        bool onBat = _lastBat?.PluggedIn == false;
        double? fanL = fans.Count > 0 ? fans[0].Actual : null;
        double? fanR = fans.Count > 1 ? fans[1].Actual : null;
        var diag = _throttle.LastDiagnosis;

        bool wrote = _cpuLogger.RecordSample(new CpuLogEntry(
            Timestamp: DateTime.Now,
            Profile: _profileName,
            OnBattery: onBat,
            CpuTempC: merged.CpuPackageTemp,
            CpuClockMHz: merged.CpuMaxClockMHz,
            CpuPowerW: merged.CpuPackagePower,
            CpuLoadPct: merged.CpuLoad,
            GpuTempC: merged.Dgpu?.TempC,
            GpuPowerW: merged.Dgpu?.PowerW,
            SystemPowerW: LastPowerOutW > 0 ? LastPowerOutW : null,
            FanLeftRpm: fanL,
            FanRightRpm: fanR,
            ThrottleCategory: diag.IsThrottled ? diag.PrimaryCategory.ToString() : "None",
            ThrottleReason: diag.Reason));

        if (wrote) RefreshCsvLoggerUi();
    }

    void LogInterval_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (CbLogInterval.SelectedItem is not ComboBoxItem item || item.Tag is not int sec) return;
        _cpuLogger.IntervalSeconds = sec;
        if (_cpuLogger.IsRecording)
            _timer.Interval = TimeSpan.FromSeconds(sec == 1 ? 1 : 2);
    }

    void ToggleCsvLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_cpuLogger.IsRecording)
            {
                int sec = (CbLogInterval.SelectedItem as ComboBoxItem)?.Tag is int s ? s : 2;
                _cpuLogger.IntervalSeconds = sec;
                string path = _cpuLogger.Start(sessionTag: Profile.All.FirstOrDefault(p => p.Name == _profileName)?.Key ?? "custom");
                _timer.Interval = TimeSpan.FromSeconds(sec == 1 ? 1 : 2);

                // Record immediate T=0 sample if telemetry is ready
                if (_lastSnap is not null)
                    DiagnosticsTick(_lastSnap, _lastFans, _lastSmcPower);

                BtnToggleCsv.Content = "⏹ Dừng ghi CSV";
                BtnOpenCsvFile.IsEnabled = true;
                CsvLogPathText.Text = $"Đang lưu: {Path.GetFileName(path)}";
                RefreshCsvLoggerUi();
                ShowToast("Đã bắt đầu ghi nhật ký CPU ra CSV");
            }
            else
            {
                string? saved = _cpuLogger.Stop();
                _timer.Interval = TimeSpan.FromSeconds(2);
                BtnToggleCsv.Content = "⏺ Bắt đầu ghi CSV";
                RefreshCsvLoggerUi();
                if (saved is not null)
                {
                    CsvLogPathText.Text = $"✓ Đã lưu: {Path.GetFileName(saved)}";
                    ShowToast("Đã dừng và lưu file CSV");
                }
            }
        }
        catch (Exception ex) { ShowToast("Lỗi ghi CSV: " + ex.Message); }
    }

    void RefreshCsvLoggerUi()
    {
        var sum = _cpuLogger.GetSummary();
        if (sum.SampleCount == 0 && !sum.IsRecording)
        {
            CsvLogStatsText.Text = "Chưa có phiên ghi. Bấm “Bắt đầu ghi CSV” để lưu nhiệt độ, xung, công suất và tải CPU.";
            return;
        }

        string temp = sum.AvgTempC is double at ? $"Nhiệt TB {at:0.0}°C (đỉnh {sum.MaxTempC:0}°C)" : "Nhiệt –";
        string clk = sum.AvgClockGHz is double ac ? $" · Xung TB {ac:0.00} GHz ({sum.MinClockGHz:0.00}–{sum.MaxClockGHz:0.00})" : "";
        string pwr = sum.AvgPowerW is double ap ? $" · Điện TB {ap:0.0}W (đỉnh {sum.MaxPowerW:0.0}W)" : "";
        string load = sum.AvgLoadPct is double al ? $" · Tải TB {al:0}%" : "";
        CsvLogStatsText.Text = $"{sum.SampleCount} mẫu ({(int)sum.Elapsed.TotalMinutes:00}:{sum.Elapsed.Seconds:00}) · {temp}{clk}{pwr}{load}";
    }

    void OpenCsvFile_Click(object sender, RoutedEventArgs e)
    {
        string? file = _cpuLogger.LastFilePath;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
        {
            ShowToast("Chưa có file CSV nào được tạo.");
            return;
        }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { ShowToast("Không mở được CSV: " + ex.Message); }
    }

    void OpenCsvFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CpuLoggerService.DefaultLogDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CpuLoggerService.DefaultLogDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { ShowToast("Lỗi mở thư mục: " + ex.Message); }
    }

    // ================= 3. Profile / Power Plan Backup & Restore =================
    void RefreshBackupUi()
    {
        var baseInfo = PowerPlanBackupService.GetBaselineInfo();
        var manualInfo = PowerPlanBackupService.GetManualInfo();

        BtnRestoreBaseline.IsEnabled = baseInfo is not null;
        BtnRestoreManual.Visibility = manualInfo is not null ? Visibility.Visible : Visibility.Collapsed;

        if (baseInfo is null)
        {
            BackupStatusText.Text = "Sẽ tự động sao lưu nguyên trạng Power Plan ngay trước khi thay đổi.";
        }
        else
        {
            string manualNote = manualInfo is not null
                ? $" · Bản thủ công: {manualInfo.ActivePlanName} ({manualInfo.CreatedAt:dd/MM HH:mm})"
                : "";
            BackupStatusText.Text = $"✓ Đã sao lưu nguyên trạng ban đầu: {baseInfo.ActivePlanName} ({baseInfo.Plans.Count} gói · {baseInfo.CreatedAt:dd/MM HH:mm}){manualNote}";
        }
    }

    async void BackupNow_Click(object sender, RoutedEventArgs e)
    {
        BtnBackupNow.IsEnabled = false;
        try
        {
            var m = await Task.Run(PowerPlanBackupService.CreateManualBackup);
            RefreshBackupUi();
            ShowToast($"Đã sao lưu cấu hình điện ({m.ActivePlanName})");
        }
        catch (Exception ex) { BackupStatusText.Text = "Lỗi sao lưu: " + ex.Message; }
        finally { BtnBackupNow.IsEnabled = true; }
    }

    async void RestoreBaseline_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                Loc.T("Khôi phục Power Plan về nguyên trạng ban đầu trước khi MacTune thay đổi?"),
                "MacTune", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnRestoreBaseline.IsEnabled = false;
        try
        {
            string name = await Task.Run(() => PowerPlanBackupService.RestoreBaseline(removeMacTunePlansIfAbsent: true));
            LoadPowerState();
            LoadTimers();
            LoadLid();
            RefreshBackupUi();
            ShowToast($"Đã khôi phục nguyên trạng ({name})");
        }
        catch (Exception ex) { ShowToast("Lỗi khôi phục: " + ex.Message); }
        finally { BtnRestoreBaseline.IsEnabled = PowerPlanBackupService.HasBaselineBackup; }
    }

    async void RestoreManual_Click(object sender, RoutedEventArgs e)
    {
        BtnRestoreManual.IsEnabled = false;
        try
        {
            string name = await Task.Run(PowerPlanBackupService.RestoreManual);
            LoadPowerState();
            LoadTimers();
            LoadLid();
            RefreshBackupUi();
            ShowToast($"Đã khôi phục bản sao lưu thủ công ({name})");
        }
        catch (Exception ex) { ShowToast("Lỗi khôi phục: " + ex.Message); }
        finally { BtnRestoreManual.IsEnabled = true; }
    }

    // ================= 4. Fan Safety Self-Test =================
    async void RunFanSafetyTest_Click(object sender, RoutedEventArgs e)
    {
        BtnFanSafetyTest.IsEnabled = false;
        FanSafetySummaryText.Text = "Đang kiểm thử 4 tình huống an toàn quạt (Watchdog treo app, mất driver, Sleep, Resume)…";
        try
        {
            var smc = _smc;
            var report = await Task.Run(() => FanSafetySelfTest.RunAll(smc));
            var greenBg = (Brush)FindResource("GreenSoft");
            var greenFg = (Brush)FindResource("Green");
            var redBg = (Brush)FindResource("RedSoft");
            var redFg = (Brush)FindResource("Red");

            FanSafetyList.ItemsSource = report.Cases.Select(c => new SafetyRow(
                c.Name,
                c.Detail,
                c.Passed ? "✓ Đạt" : "✕ Lỗi",
                c.Passed ? greenBg : redBg,
                c.Passed ? greenFg : redFg)).ToList();
            FanSafetyList.Visibility = Visibility.Visible;
            FanSafetySummaryText.Text = report.Summary;
            ShowToast(report.AllPassed ? "Kiểm thử an toàn quạt: Đạt 100%" : "Kiểm thử an toàn quạt: Có cảnh báo");
        }
        catch (Exception ex)
        {
            FanSafetySummaryText.Text = "Lỗi kiểm thử: " + ex.Message;
        }
        finally { BtnFanSafetyTest.IsEnabled = true; }
    }

    // ================= 5. Hardware Compatibility Matrix & Write Gating =================
    async Task RefreshCompatibilityUiAsync()
    {
        var smc = _smc;
        var kbd = _kbd;
        var msr = _msrProbe;
        var report = await Task.Run(() => HardwareCompatibilityService.ProbeLive(smc, kbd, msr));
        ApplyCompatibilityReport(report);
    }

    public void ApplyCompatibilityReport(HardwareCompatibilityReport report)
    {
        _compatReport = report;

        string archLabel = report.Architecture switch
        {
            MacArchitecture.AppleT2 => $"Chip Apple T2 · {report.ModelId}",
            MacArchitecture.ApplePreT2 => $"Mac Intel Pre-T2 · {report.ModelId}",
            _ => $"PC ngoài Apple · {report.ModelId}",
        };
        CompatArchBadgeText.Text = archLabel;
        CompatArchBadge.Background = (Brush)FindResource(report.IsAppleMachine ? "AccentSoft" : "OrangeSoft");
        CompatArchBadgeText.Foreground = (Brush)FindResource(report.IsAppleMachine ? "Accent" : "Orange");
        CompatSubtitle.Text = $"{report.FriendlyModel} · Đã kiểm tra phần cứng trước khi mở quyền ghi.";

        var greenBg = (Brush)FindResource("GreenSoft");
        var greenFg = (Brush)FindResource("Green");
        var orangeBg = (Brush)FindResource("OrangeSoft");
        var orangeFg = (Brush)FindResource("Orange");
        var subtleBg = (Brush)FindResource("SubtleBg");
        var mutedFg = (Brush)FindResource("MutedBrush");

        CompatList.ItemsSource = report.Capabilities.Select(c =>
        {
            var (bg, fg) = c.Status switch
            {
                CapabilityStatus.Writable => (greenBg, greenFg),
                CapabilityStatus.ReadOnly => (orangeBg, orangeFg),
                _ => (subtleBg, mutedFg),
            };
            return new CompatRow(c.Title, c.Detail, c.Badge, bg, fg);
        }).ToList();

        // Enforce pre-write hardware gates on UI controls
        bool tbCanWrite = report.CanWrite("TouchBar");
        ChkOvTbFn.IsEnabled = tbCanWrite;
        RbTbMedia.IsEnabled = tbCanWrite;
        RbTbFn.IsEnabled = tbCanWrite;
        if (!tbCanWrite)
            OvTbFnDesc.Text = "Máy không có Touch Bar";

        bool audioCanWrite = report.CanWrite("BootCampAudio");
        ChkFixAudioPop.IsEnabled = audioCanWrite;

        bool wirelessCanWrite = report.CanWrite("BootCampWireless");
        ChkFixWireless.IsEnabled = wirelessCanWrite;
        if (!audioCanWrite && !wirelessCanWrite)
            BtnBcFixAll.IsEnabled = false;

        if (!report.CanWrite("FanControl"))
            FanModeTiles.IsEnabled = false;

        if (!report.CanWrite("BatteryChargeLimit"))
            ChkChargeLimit.IsEnabled = false;
    }
}
