using Microsoft.Win32;

namespace MacTune.Services;

public enum MacArchitecture
{
    AppleT2,
    ApplePreT2,
    NonApple,
}

public enum CapabilityStatus
{
    Writable,       // Hardware present, verified & safe to write
    ReadOnly,       // Hardware present, telemetry works, write requires optional driver or locked by firmware
    NotApplicable,  // Not present on this Mac model / PC
}

public record CapabilityItem(
    string Key,
    string Title,
    CapabilityStatus Status,
    bool CanWrite,
    string Badge,
    string Detail);

public record HardwareCompatibilityReport(
    bool IsAppleMachine,
    string Manufacturer,
    string ModelId,
    string FriendlyModel,
    MacArchitecture Architecture,
    bool IsLaptop,
    bool HasTouchBarModel,
    IReadOnlyList<CapabilityItem> Capabilities)
{
    public bool CanWrite(string key) =>
        Capabilities.FirstOrDefault(c => c.Key.Equals(key, StringComparison.OrdinalIgnoreCase))?.CanWrite == true;
}

/// <summary>
/// Identifies the exact Intel Mac model &amp; architecture (Apple T2 vs Pre-T2 vs non-Apple PC)
/// and verifies which hardware features exist and are safe to write BEFORE permitting hardware writes.
/// </summary>
public static class HardwareCompatibilityService
{
    static readonly Dictionary<string, string> KnownMacModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MacBookPro16,1"] = "MacBook Pro 16\" 2019",
        ["MacBookPro16,4"] = "MacBook Pro 16\" 2020 (5600M)",
        ["MacBookPro16,2"] = "MacBook Pro 13\" 2020 (4 cổng TB3)",
        ["MacBookPro16,3"] = "MacBook Pro 13\" 2020 (2 cổng TB3)",
        ["MacBookPro15,1"] = "MacBook Pro 15\" 2018–2019",
        ["MacBookPro15,3"] = "MacBook Pro 15\" 2019 (Vega)",
        ["MacBookPro15,2"] = "MacBook Pro 13\" 2018–2019 (4 cổng TB3)",
        ["MacBookPro15,4"] = "MacBook Pro 13\" 2019 (2 cổng TB3)",
        ["MacBookPro14,3"] = "MacBook Pro 15\" 2017 (Touch Bar)",
        ["MacBookPro14,2"] = "MacBook Pro 13\" 2017 (Touch Bar)",
        ["MacBookPro14,1"] = "MacBook Pro 13\" 2017 (Fn)",
        ["MacBookPro13,3"] = "MacBook Pro 15\" 2016 (Touch Bar)",
        ["MacBookPro13,2"] = "MacBook Pro 13\" 2016 (Touch Bar)",
        ["MacBookPro13,1"] = "MacBook Pro 13\" 2016 (Fn)",
        ["MacBookPro12,1"] = "MacBook Pro 13\" Retina 2015",
        ["MacBookPro11,4"] = "MacBook Pro 15\" Retina 2015",
        ["MacBookPro11,5"] = "MacBook Pro 15\" Retina 2015 (dGPU)",
        ["MacBookAir9,1"] = "MacBook Air 13\" 2020 (Intel)",
        ["MacBookAir8,2"] = "MacBook Air 13\" 2019",
        ["MacBookAir8,1"] = "MacBook Air 13\" 2018",
        ["MacBookAir7,2"] = "MacBook Air 13\" 2015–2017",
        ["iMac20,1"] = "iMac 27\" Retina 5K 2020",
        ["iMac20,2"] = "iMac 27\" Retina 5K 2020 (XT)",
        ["iMac19,1"] = "iMac 27\" Retina 5K 2019",
        ["iMac19,2"] = "iMac 21.5\" Retina 4K 2019",
        ["iMacPro1,1"] = "iMac Pro 27\" 2017",
        ["Macmini8,1"] = "Mac mini 2018",
        ["MacPro7,1"] = "Mac Pro 2019",
    };

    static readonly HashSet<string> TouchBarModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "MacBookPro13,2", "MacBookPro13,3",
        "MacBookPro14,2", "MacBookPro14,3",
        "MacBookPro15,1", "MacBookPro15,2", "MacBookPro15,3", "MacBookPro15,4",
        "MacBookPro16,1", "MacBookPro16,2", "MacBookPro16,3", "MacBookPro16,4",
    };

    public static (string Manufacturer, string ModelId) ReadBiosIdentity()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            string mfg = (k?.GetValue("SystemManufacturer") as string ?? "").Trim();
            string prod = (k?.GetValue("SystemProductName") as string ?? "").Trim();
            return (mfg, prod);
        }
        catch { return ("", ""); }
    }

    public static bool IsAppleSystem()
    {
        var (mfg, prod) = ReadBiosIdentity();
        return IsAppleModel(mfg, prod);
    }

    public static bool IsAppleModel(string manufacturer, string modelId)
    {
        if (manufacturer.Contains("Apple", StringComparison.OrdinalIgnoreCase)) return true;
        return modelId.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("iMac", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("Macmini", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("MacPro", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsT2MacModel(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return false;
        return modelId.StartsWith("MacBookPro15,", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("MacBookPro16,", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("MacBookAir8,", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("MacBookAir9,", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("iMac20,", StringComparison.OrdinalIgnoreCase)
            || modelId.Equals("iMacPro1,1", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("Macmini8,", StringComparison.OrdinalIgnoreCase)
            || modelId.StartsWith("MacPro7,", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsLaptopMacModel(string modelId) =>
        modelId.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase);

    public static bool ModelSupportsTouchBar(string modelId) =>
        TouchBarModels.Contains(modelId.Trim());

    public static string ResolveFriendlyModelName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return "Không rõ model";
        if (KnownMacModels.TryGetValue(modelId.Trim(), out var friendly)) return friendly;
        if (modelId.StartsWith("MacBookPro", StringComparison.OrdinalIgnoreCase)) return $"MacBook Pro ({modelId})";
        if (modelId.StartsWith("MacBookAir", StringComparison.OrdinalIgnoreCase)) return $"MacBook Air ({modelId})";
        if (modelId.StartsWith("MacBook", StringComparison.OrdinalIgnoreCase)) return $"MacBook ({modelId})";
        if (modelId.StartsWith("iMac", StringComparison.OrdinalIgnoreCase)) return $"iMac ({modelId})";
        if (modelId.StartsWith("Macmini", StringComparison.OrdinalIgnoreCase)) return $"Mac mini ({modelId})";
        if (modelId.StartsWith("MacPro", StringComparison.OrdinalIgnoreCase)) return $"Mac Pro ({modelId})";
        return modelId;
    }

    /// <summary>Pure, deterministic compatibility evaluator usable both on live hardware and across simulated Mac models in tests.</summary>
    public static HardwareCompatibilityReport Evaluate(
        string manufacturer,
        string modelId,
        string? smcBackend,
        int smcFanCount,
        int smcWritableFans,
        bool smcChargeLimitWritable,
        bool hasTouchBarDevice,
        bool hasKbdBacklight,
        int audioAdapters,
        int wirelessAdapters,
        bool cpuPowerSupported,
        bool msrSupported)
    {
        bool isApple = IsAppleModel(manufacturer, modelId);
        bool isT2 = isApple && IsT2MacModel(modelId);
        var arch = !isApple ? MacArchitecture.NonApple : isT2 ? MacArchitecture.AppleT2 : MacArchitecture.ApplePreT2;
        bool isLaptop = isApple ? (string.IsNullOrEmpty(modelId) || IsLaptopMacModel(modelId)) : hasKbdBacklight;
        bool tbSupported = isApple && (ModelSupportsTouchBar(modelId) || hasTouchBarDevice);

        var items = new List<CapabilityItem>();

        // 1. SMC Fan Control
        if (!isApple)
            items.Add(new("FanControl", "Điều khiển quạt SMC", CapabilityStatus.NotApplicable, false, "Không hỗ trợ", "Chỉ cho phép ghi quạt trên máy Mac Apple"));
        else if (smcWritableFans > 0)
            items.Add(new("FanControl", "Điều khiển quạt SMC", CapabilityStatus.Writable, true, "Hỗ trợ ghi", $"Backend {smcBackend} · {smcWritableFans}/{smcFanCount} quạt cho phép ghi an toàn"));
        else if (!string.IsNullOrEmpty(smcBackend) && smcBackend != "none")
            items.Add(new("FanControl", "Điều khiển quạt SMC", CapabilityStatus.ReadOnly, false, "Chỉ đọc", $"Đọc được {smcFanCount} quạt nhưng khóa điều khiển từ chối ghi"));
        else if (isT2)
            items.Add(new("FanControl", "Điều khiển quạt SMC", CapabilityStatus.ReadOnly, false, "Cần driver", "Máy chip T2 cần PawnIO Unrestricted hoặc AppleSMC để chỉnh quạt"));
        else
            items.Add(new("FanControl", "Điều khiển quạt SMC", CapabilityStatus.ReadOnly, false, "Cần driver", "Chưa kết nối được MacHALDriver / AppleSMC"));

        // 2. CPU Power & Profile Tuning
        if (cpuPowerSupported)
            items.Add(new("CpuPower", "Quản lý xung & điện CPU", CapabilityStatus.Writable, true, "Hỗ trợ ghi", "Hỗ trợ Turbo Boost, xung tối đa/tối thiểu và sao lưu Power Plan"));
        else
            items.Add(new("CpuPower", "Quản lý xung & điện CPU", CapabilityStatus.ReadOnly, false, "Hạn chế", "Windows ẩn cấu hình xung nhịp CPU trên thiết bị này"));

        // 3. Touch Bar
        if (!isApple || !tbSupported)
            items.Add(new("TouchBar", "Thanh Touch Bar", CapabilityStatus.NotApplicable, false, "Không có", "Dòng máy này không trang bị Touch Bar – đã khóa ghi"));
        else if (hasTouchBarDevice)
            items.Add(new("TouchBar", "Thanh Touch Bar", CapabilityStatus.Writable, true, "Hỗ trợ ghi", "Đã nhận driver Touch Bar (đổi tức thì F1–F12 ↔ Media)"));
        else
            items.Add(new("TouchBar", "Thanh Touch Bar", CapabilityStatus.Writable, true, "Qua Registry", "Dòng máy có Touch Bar · lưu chế độ qua Boot Camp Registry"));

        // 4. Keyboard Backlight
        if (!isApple || !isLaptop)
            items.Add(new("KeyboardBacklight", "Đèn nền bàn phím", CapabilityStatus.NotApplicable, false, "Không có", isApple ? "Máy Mac để bàn không có đèn phím tích hợp" : "Chỉ hỗ trợ bàn phím nội bộ MacBook"));
        else if (hasKbdBacklight)
            items.Add(new("KeyboardBacklight", "Đèn nền bàn phím", CapabilityStatus.Writable, true, "Hỗ trợ ghi", "Đã xác minh giao diện HID đèn phím Apple"));
        else
            items.Add(new("KeyboardBacklight", "Đèn nền bàn phím", CapabilityStatus.ReadOnly, false, "Chưa nhận HID", "Chưa tìm thấy HID đèn bàn phím (cần Boot Camp driver)"));

        // 5. Boot Camp Speaker Anti-Pop
        if (audioAdapters > 0)
            items.Add(new("BootCampAudio", "Chống bụp & trễ loa", CapabilityStatus.Writable, true, "Hỗ trợ ghi", $"Tìm thấy {audioAdapters} mạch âm thanh tương thích (có sao lưu gốc)"));
        else
            items.Add(new("BootCampAudio", "Chống bụp & trễ loa", CapabilityStatus.NotApplicable, false, "Không có", "Không tìm thấy mạch Apple/AMD/Intel Audio tương thích"));

        // 6. Boot Camp Bluetooth & Broadcom Wi-Fi
        if (wirelessAdapters > 0)
            items.Add(new("BootCampWireless", "Ổn định BT & Wi-Fi", CapabilityStatus.Writable, true, "Hỗ trợ ghi", $"Tìm thấy {wirelessAdapters} adapter Apple/Broadcom (có sao lưu gốc)"));
        else
            items.Add(new("BootCampWireless", "Ổn định BT & Wi-Fi", CapabilityStatus.NotApplicable, false, "Không có", "Không tìm thấy chip Bluetooth/Wi-Fi Broadcom Apple"));

        // 7. Hardware Battery Charge Limit (80%)
        if (!isApple || !isLaptop)
            items.Add(new("BatteryChargeLimit", "Giới hạn sạc cứng 80%", CapabilityStatus.NotApplicable, false, "Không có pin", "Máy không dùng pin MacBook"));
        else if (smcChargeLimitWritable)
            items.Add(new("BatteryChargeLimit", "Giới hạn sạc cứng 80%", CapabilityStatus.Writable, true, "Hỗ trợ ghi", "SMC cho phép ghi trực tiếp khóa BCLM / CHWA"));
        else if (isT2)
            items.Add(new("BatteryChargeLimit", "Giới hạn sạc cứng 80%", CapabilityStatus.ReadOnly, false, "Khóa bởi T2", "Firmware T2 chặn ghi BCLM từ Windows (dùng nhắc sạc 80%)"));
        else
            items.Add(new("BatteryChargeLimit", "Giới hạn sạc cứng 80%", CapabilityStatus.ReadOnly, false, "Không hỗ trợ", "SMC trên máy này không mở khóa ghi giới hạn sạc"));

        // 8. MSR Throttling Diagnostics
        if (msrSupported)
            items.Add(new("MsrDiagnostics", "Chẩn đoán hạ xung MSR", CapabilityStatus.Writable, false, "Đủ thanh ghi", "Đọc trực tiếp Thermal, PL1/PL2, EDP Current & PROCHOT qua MSR"));
        else
            items.Add(new("MsrDiagnostics", "Chẩn đoán hạ xung MSR", CapabilityStatus.ReadOnly, false, "Cảm biến", "Đang chẩn đoán qua cảm biến (cài PawnIO để đọc đủ thanh ghi MSR)"));

        return new HardwareCompatibilityReport(
            IsAppleMachine: isApple,
            Manufacturer: string.IsNullOrEmpty(manufacturer) ? "Unknown" : manufacturer,
            ModelId: string.IsNullOrEmpty(modelId) ? "Unknown" : modelId,
            FriendlyModel: ResolveFriendlyModelName(modelId),
            Architecture: arch,
            IsLaptop: isLaptop,
            HasTouchBarModel: tbSupported,
            Capabilities: items);
    }

    public static HardwareCompatibilityReport ProbeLive(SmcService? smc, KeyboardBacklightService? kbd, CpuMsrProbe? msr)
    {
        var (mfg, model) = ReadBiosIdentity();
        var fans = smc?.Fans() ?? Array.Empty<FanInfo>();
        bool cpuPowerOk = PowerService.Get(PowerService.Boost).Ac >= 0 || PowerService.Get(PowerService.MaxState).Ac >= 0;

        return Evaluate(
            manufacturer: mfg,
            modelId: model,
            smcBackend: smc?.Backend,
            smcFanCount: fans.Count,
            smcWritableFans: fans.Count(f => f.CanControl),
            smcChargeLimitWritable: smc?.ChargeLimitKey is not null,
            hasTouchBarDevice: AppleKeyboardService.HasTouchBarDriverDevice(),
            hasKbdBacklight: kbd is not null,
            audioAdapters: BootCampFixesService.CountMatchingAudioAdapters(),
            wirelessAdapters: BootCampFixesService.CountMatchingWirelessAdapters(),
            cpuPowerSupported: cpuPowerOk,
            msrSupported: msr?.IsAvailable == true);
    }
}
