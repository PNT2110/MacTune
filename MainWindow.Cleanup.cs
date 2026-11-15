using System.Windows;
using System.Windows.Controls;
using MacTune.Services;

namespace MacTune;

/// <summary>Junk cleanup page and screen-off / sleep timers.</summary>
public partial class MainWindow
{
    JunkScan? _scan;

    // ================= Cleanup =================
    async void Scan_Click(object sender, RoutedEventArgs e)
    {
        BtnScan.IsEnabled = false;
        BtnClean.IsEnabled = false;
        CleanStatus.Text = "Đang quét…";
        try
        {
            _scan = await Task.Run(JunkCleanerService.Scan);
            CleanTempSize.Text = $"{JunkCleanerService.Format(_scan.TempBytes)}  ({_scan.TempFiles:N0} file)";
            CleanBinSize.Text = $"{JunkCleanerService.Format(_scan.RecycleBytes)}  ({_scan.RecycleItems:N0} mục)";
            UpdateCleanTotal();
            CleanStatus.Text = "Chọn mục muốn dọn rồi bấm “Dọn ngay”.";
        }
        catch (Exception ex) { CleanStatus.Text = "Lỗi khi quét: " + ex.Message; }
        finally { BtnScan.IsEnabled = true; }
    }

    void CleanSelection_Changed(object sender, RoutedEventArgs e) => UpdateCleanTotal();

    void UpdateCleanTotal()
    {
        if (_scan is null) return;
        long total = (ChkCleanTemp.IsChecked == true ? _scan.TempBytes : 0)
                   + (ChkCleanBin.IsChecked == true ? _scan.RecycleBytes : 0);
        CleanTotal.Text = JunkCleanerService.Format(total);
        BtnClean.IsEnabled = total > 0;
    }

    async void Clean_Click(object sender, RoutedEventArgs e)
    {
        if (_scan is null) return;
        bool temp = ChkCleanTemp.IsChecked == true, bin = ChkCleanBin.IsChecked == true;
        string what = string.Join(" và ", new[] { temp ? "file tạm" : null, bin ? "Thùng rác (xóa vĩnh viễn)" : null }.Where(s => s is not null));
        if (MessageBox.Show(this, Loc.T($"Xóa {what}?"), "MacTune", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        BtnClean.IsEnabled = BtnScan.IsEnabled = false;
        CleanStatus.Text = "Đang dọn…";
        try
        {
            long freed = 0; int skipped = 0;
            if (temp)
            {
                var r = await Task.Run(JunkCleanerService.CleanTemp);
                freed += r.FreedBytes; skipped += r.Skipped;
            }
            if (bin)
            {
                await Task.Run(JunkCleanerService.EmptyRecycleBin);
                freed += _scan.RecycleBytes;
            }
            CleanStatus.Text = $"✓ Đã giải phóng {JunkCleanerService.Format(freed)}." +
                               (skipped > 0 ? $" Bỏ qua {skipped:N0} file đang được dùng." : "");
            ShowToast($"Đã dọn {JunkCleanerService.Format(freed)}");
            Notify("Dọn dẹp xong", $"Đã giải phóng {JunkCleanerService.Format(freed)}.");
        }
        catch (Exception ex) { CleanStatus.Text = "Lỗi khi dọn: " + ex.Message; }
        finally
        {
            BtnScan.IsEnabled = true;
            _scan = await Task.Run(JunkCleanerService.Scan);
            CleanTempSize.Text = $"{JunkCleanerService.Format(_scan.TempBytes)}  ({_scan.TempFiles:N0} file)";
            CleanBinSize.Text = $"{JunkCleanerService.Format(_scan.RecycleBytes)}  ({_scan.RecycleItems:N0} mục)";
            UpdateCleanTotal();
        }
    }

    void DiskCleanup_Click(object sender, RoutedEventArgs e)
    {
        try { JunkCleanerService.OpenWindowsDiskCleanup(); }
        catch (Exception ex) { ShowToast("Không mở được Disk Cleanup: " + ex.Message); }
    }

    // ================= Screen-off / sleep timers =================
    static readonly (string Label, int Minutes)[] TimerChoices =
    {
        ("1 phút", 1), ("2 phút", 2), ("3 phút", 3), ("5 phút", 5), ("10 phút", 10),
        ("15 phút", 15), ("20 phút", 20), ("30 phút", 30), ("45 phút", 45), ("1 giờ", 60),
        ("2 giờ", 120), ("Không bao giờ", 0),
    };

    void LoadTimers()
    {
        _loading = true;
        try
        {
            var (sa, sd) = PowerService.GetScreenOff();
            var (la, ld) = PowerService.GetSleep();
            FillTimer(CbScreenAc, sa);
            FillTimer(CbScreenDc, sd);
            FillTimer(CbSleepAc, la);
            FillTimer(CbSleepDc, ld);
        }
        catch (Exception ex) { TimersStatus.Text = "Lỗi đọc cài đặt: " + ex.Message; }
        finally { _loading = false; }
    }

    static void FillTimer(ComboBox cb, int current)
    {
        cb.Items.Clear();
        foreach (var (label, min) in TimerChoices)
            cb.Items.Add(new ComboBoxItem { Content = label, Tag = min });
        if (current > 0 && TimerChoices.All(c => c.Minutes != current))
            cb.Items.Insert(0, new ComboBoxItem { Content = $"{current} phút (hiện tại)", Tag = current });
        cb.SelectedItem = cb.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == current)
                          ?? cb.Items.Cast<ComboBoxItem>().First();
        cb.IsEnabled = current >= 0;
    }

    static int Minutes(ComboBox cb) => cb.SelectedItem is ComboBoxItem i ? (int)i.Tag : -1;

    void Timers_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        try
        {
            int sa = Minutes(CbScreenAc), sd = Minutes(CbScreenDc), la = Minutes(CbSleepAc), ld = Minutes(CbSleepDc);
            if ((sender == CbScreenAc || sender == CbScreenDc) && sa >= 0 && sd >= 0) PowerService.SetScreenOff(sa, sd);
            if ((sender == CbSleepAc || sender == CbSleepDc) && la >= 0 && ld >= 0) PowerService.SetSleep(la, ld);

            // sleeping before the screen turns off makes no sense – warn
            bool odd = (la > 0 && sa > la) || (ld > 0 && sd > ld) || (la > 0 && sa == 0) || (ld > 0 && sd == 0);
            TimersStatus.Text = odd
                ? "⚠ Máy sẽ ngủ trước khi màn hình kịp tắt – nên đặt thời gian ngủ dài hơn."
                : $"✓ Đã lưu lúc {DateTime.Now:HH:mm:ss}.";
        }
        catch (Exception ex) { TimersStatus.Text = "Lỗi: " + ex.Message; }
    }
}
