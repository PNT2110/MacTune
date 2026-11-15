using System.IO;

namespace MacTune.Services;

public record FanSafetyCaseResult(string Name, bool Passed, string Detail);

public record FanSafetyReport(
    bool AllPassed,
    DateTime TestedAt,
    IReadOnlyList<FanSafetyCaseResult> Cases,
    string Summary);

/// <summary>
/// Deterministic safety verification harness for FanController &amp; FanSafetyWatchdog.
/// Tests all four critical failure scenarios without risking real hardware thermal spikes:
///   1) App hang / UI deadlock (Watchdog thread forces Apple Auto)
///   2) Driver disconnect / sensor loss / 0-RPM fault (Immediate fallback to Apple Auto)
///   3) System Sleep / Suspend (Immediate release to Apple Auto before standby)
///   4) System Wake / Resume + 95°C overheat clamp (Revalidation, healthy restore, broken-wake rejection)
/// </summary>
public static class FanSafetySelfTest
{
    sealed class SimulatedSmcBackend : IFanBackend
    {
        public bool Healthy { get; set; } = true;
        public bool AcceptWrites { get; set; } = true;
        public bool ResumeResult { get; set; } = true;
        public double ReportedActualRpm { get; set; } = 2400;
        public double LastTargetRpm { get; private set; }
        public bool IsManual { get; private set; }
        public int AutoCallCount { get; private set; }

        public bool IsHealthy => Healthy;

        public IReadOnlyList<FanInfo> Fans()
        {
            if (!Healthy) return Array.Empty<FanInfo>();
            return new[]
            {
                new FanInfo(0, "Quạt trái", ReportedActualRpm, 1836, 5600, LastTargetRpm, IsManual, true),
                new FanInfo(1, "Quạt phải", ReportedActualRpm, 1700, 5200, LastTargetRpm, IsManual, true),
            };
        }

        public bool SetFanRpm(int index, double rpm)
        {
            if (!Healthy || !AcceptWrites) return false;
            IsManual = true;
            LastTargetRpm = rpm;
            if (ReportedActualRpm > 100) ReportedActualRpm = rpm;
            return true;
        }

        public void AllFansAuto()
        {
            IsManual = false;
            LastTargetRpm = 0;
            AutoCallCount++;
        }

        public bool RevalidateAfterResume()
        {
            Healthy = ResumeResult;
            return ResumeResult;
        }
    }

    public static FanSafetyReport RunAll(SmcService? liveSmc = null)
    {
        var results = new List<FanSafetyCaseResult>();
        string tmpMarker = Path.Combine(Path.GetTempPath(), $"mactune-safety-test-{Guid.NewGuid():N}.flag");

        try
        {
            // ---------- Test 1: App Hang / Frozen Main Thread Watchdog ----------
            {
                var mock = new SimulatedSmcBackend();
                var fc = new FanController(mock, tmpMarker);
                using var wd = new FanSafetyWatchdog(fc,
                    stallTimeout: TimeSpan.FromMilliseconds(90),
                    pollInterval: TimeSpan.FromMilliseconds(25));

                fc.SetFixed(0.45);
                fc.Tick(68);
                wd.Heartbeat();
                bool manualBeforeHang = fc.Mode == FanMode.Fixed && mock.IsManual;

                // Simulate UI thread deadlock: stop calling wd.Heartbeat() for 220 ms
                Thread.Sleep(220);

                bool trippedToAuto = fc.Mode == FanMode.Auto && !mock.IsManual && wd.TripCount >= 1 && !File.Exists(tmpMarker);
                results.Add(new FanSafetyCaseResult(
                    "Tình huống 1 · App treo (Watchdog độc lập)",
                    manualBeforeHang && trippedToAuto,
                    manualBeforeHang && trippedToAuto
                        ? "Luồng chính ngừng phản hồi → Watchdog tự động trả quạt về Apple Auto"
                        : $"Thất bại (mode={fc.Mode}, manual={mock.IsManual}, trips={wd.TripCount})"));
            }

            // ---------- Test 2: Lost Driver / Sensor Drop / 0 RPM ----------
            {
                var mock = new SimulatedSmcBackend();
                var fc = new FanController(mock, tmpMarker);

                // 2a. Driver disconnects mid-run
                fc.SetCurve(FanCurve.All[1]);
                fc.Tick(72);
                mock.Healthy = false;
                fc.Tick(72);
                bool driverLossSafe = fc.Mode == FanMode.Auto && mock.AutoCallCount >= 1;

                // 2b. Sensor returns null temperature
                mock.Healthy = true;
                fc.SetFixed(0.50);
                fc.Tick(70);
                fc.Tick(null);
                bool sensorLossSafe = fc.Mode == FanMode.Auto;

                // 2c. Tachometer stuck at 0 RPM twice
                fc.SetFixed(0.60);
                mock.ReportedActualRpm = 0;
                fc.Tick(70);
                fc.Tick(70);
                bool zeroRpmSafe = fc.Mode == FanMode.Auto;

                bool ok = driverLossSafe && sensorLossSafe && zeroRpmSafe;
                results.Add(new FanSafetyCaseResult(
                    "Tình huống 2 · Mất driver / đứt cảm biến / 0 RPM",
                    ok,
                    ok ? "Ngắt SMC, mất nhiệt độ CPU & quạt báo 0 RPM đều tự nhả về Apple Auto"
                       : $"Thất bại (driver={driverLossSafe}, sensor={sensorLossSafe}, zeroRpm={zeroRpmSafe})"));
            }

            // ---------- Test 3: System Sleep (Suspend) Handoff ----------
            {
                var mock = new SimulatedSmcBackend();
                var fc = new FanController(mock, tmpMarker);
                fc.SetCurve(FanCurve.All[2]); // Mát tối đa
                fc.Tick(74);
                bool wasManual = mock.IsManual;

                fc.OnSystemSuspend();
                bool releasedOnSleep = fc.Mode == FanMode.Auto && !mock.IsManual && fc.IsSuspended;

                // Ensure stray background tick during sleep cannot re-enable manual fan writes
                fc.Tick(80);
                bool stayedAutoDuringSleep = fc.Mode == FanMode.Auto && !mock.IsManual;

                bool ok = wasManual && releasedOnSleep && stayedAutoDuringSleep;
                results.Add(new FanSafetyCaseResult(
                    "Tình huống 3 · Chuyển sang Sleep (Suspend)",
                    ok,
                    ok ? "Nhả quạt về firmware Apple ngay trước khi ngủ & khóa ghi trong lúc ngủ"
                       : $"Thất bại (released={releasedOnSleep}, stayedAuto={stayedAutoDuringSleep})"));

                // ---------- Test 4: System Resume + 95°C Emergency Override ----------
                // 4a. Healthy driver after wake -> restores user curve & enforces 95°C -> 100% clamp
                mock.ResumeResult = true;
                bool resumedOk = fc.OnSystemResume();
                bool restoredCurve = resumedOk && fc.Mode == FanMode.Curve && fc.Curve.Key == "cool";

                fc.SetFixed(0.25); // User had low 25% fixed speed
                fc.Tick(96.5);     // Sudden post-wake thermal spike >= 95°C!
                bool emergencyMax = Math.Abs(mock.LastTargetRpm - 5600) < 5 || Math.Abs(mock.LastTargetRpm - 5200) < 5;

                // 4b. Unhealthy driver after wake -> refuses manual restore and stays safely in Auto
                fc.OnSystemSuspend();
                mock.ResumeResult = false;
                bool brokenWake = !fc.OnSystemResume() && fc.Mode == FanMode.Auto && !mock.IsManual;

                bool okResume = restoredCurve && emergencyMax && brokenWake;
                results.Add(new FanSafetyCaseResult(
                    "Tình huống 4 · Thức dậy Resume & Bảo vệ 95°C",
                    okResume,
                    okResume ? "Tự kiểm tra lại driver sau khi thức, khôi phục đường cong & ép 100% ở 95°C"
                             : $"Thất bại (restore={restoredCurve}, max95={emergencyMax}, brokenWake={brokenWake})"));
            }

            // Optional Live SMC Read-Only Sanity Check (never spins up physical fans during self-test)
            if (liveSmc is not null)
            {
                bool healthy = liveSmc.IsHealthy;
                var liveFans = liveSmc.Fans();
                bool saneBounds = liveFans.Count > 0 && liveFans.All(f => f.Min >= 0 && f.Max is >= 2000 and <= 10000);
                results.Add(new FanSafetyCaseResult(
                    "Phần cứng thực tế · Kết nối SMC hiện tại",
                    healthy && saneBounds,
                    healthy && saneBounds
                        ? $"Backend {liveSmc.Backend} phản hồi tốt ({liveFans.Count} quạt, ngưỡng RPM hợp lệ)"
                        : $"SMC thực tế báo không ổn định (healthy={healthy}, fans={liveFans.Count})"));
            }
        }
        finally
        {
            try { if (File.Exists(tmpMarker)) File.Delete(tmpMarker); } catch { }
        }

        bool allPass = results.All(r => r.Passed);
        string summary = allPass
            ? $"✓ Đạt {results.Count}/{results.Count} bài kiểm thử an toàn quạt (Treo app, Mất driver, Sleep, Resume)."
            : $"⚠ Không đạt {results.Count(r => !r.Passed)}/{results.Count} bài kiểm thử an toàn quạt.";
        return new FanSafetyReport(allPass, DateTime.Now, results, summary);
    }
}
