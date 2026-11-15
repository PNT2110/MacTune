# MacTune

**A control center for Intel Macs running Windows through Boot Camp.**
Fans, CPU power, battery, keyboard light, Mac-style keys and the everyday Boot Camp annoyances — in one small app.

[Tiếng Việt bên dưới](#tiếng-việt) · [Install](#install) · [Fan control setup](docs/FAN-SETUP.md) · [Build](#build-from-source)

> **Status:** personal project, developed and tested on one machine: **MacBook Pro 16" 2019 (MacBookPro16,1, T2, i9-9980HK, Radeon Pro 5500M) on Windows 11**.
> Other Intel Macs may work partly or not at all. It changes power settings and can drive your fans — read [Safety](#safety) first.

## What it does

| Area | Features |
|---|---|
| **Overview** | Live CPU temperature, clock and power, charger input, system draw, battery level, AMD GPU load / VRAM / top apps, 3-minute charts |
| **Performance** | One-click **Silent / Balanced / Performance** profiles (own power plans, nothing existing is modified). Per power source: Turbo Boost mode, min/max CPU state, EPP, core parking |
| **Fans & Heat** | Fan RPM, **Auto / Quiet / Balanced / Max cooling** curves or a fixed speed, all SMC temperature sensors, CPU throttling log with the likely reason |
| **Battery** | Health, cycles, charge / discharge power, time left, learned runtime per profile, Windows battery report |
| **Automation** | Switch profile when plugging / unplugging, per-app rules (e.g. game → Performance + Max cooling), charge and low-battery alerts |
| **Disk & Cleanup** | Fix for "100% disk after boot" (SysMain, Search indexing, updater tasks — each reversible), temp files and Recycle Bin cleanup, free RAM, startup apps |
| **System** | Display brightness, **keyboard backlight**, **⌘ ↔ Ctrl** (`⌘+Tab` app switch, `⌘+Q`, `⌘+Shift+4` screenshot), Touch Bar mode, **speaker anti-pop**, **Bluetooth/Wi-Fi lag fix**, **natural mouse scroll**, sleep / lid / hibernate sync, GPU per app |
| **Everyday** | Tray icon showing CPU temperature, quick-controls flyout, Meeting mode (screen stays on, quiet fans), start with Windows, opens without a UAC prompt, **English / Tiếng Việt** |

### What it cannot do

- **Charge limit (80%)** — not available on T2 Macs: the fan module only allows fan keys to be written. MacTune offers a "battery reached 80%" alert instead.
- **Undervolting, PL1/PL2 power limits** — locked or not safely reachable on these machines.
- **SSD S.M.A.R.T.** — Apple's SSD controller does not expose it to Windows; only usage, temperature and Windows' own health status are shown.
- **Turning the AMD GPU fully off** — Boot Camp has no graphics switching. You can only choose the GPU per app.

## Requirements

- An Intel Mac with Windows 10 or 11 (64-bit) installed through Boot Camp, with Apple's Boot Camp drivers
- Administrator rights
- For fan control, CPU power and clock readings: [PawnIO](https://github.com/namazso/PawnIO.Setup) — see [Fan control setup](docs/FAN-SETUP.md)

## Install

1. Download `MacTune-x.y.z-win-x64.zip` from the **Releases** page and extract it anywhere.
2. Right-click **`install.ps1`** → **Run with PowerShell**, and accept the administrator prompt.

That's it. MacTune is copied to `C:\Program Files\MacTune`, started, and puts a shortcut on your desktop that opens it without a UAC prompt.
No .NET installation is needed — the release is self-contained.

If Windows blocks the script ("running scripts is disabled"), open PowerShell in the extracted folder and run:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

**Portable use:** you can also just run `MacTune.exe` from the extracted folder.

**Uninstall:** `powershell -ExecutionPolicy Bypass -File "C:\Program Files\MacTune\install.ps1" -Uninstall` (or run the same script from the zip with `-Uninstall`).

### Optional: fan control

Fan control on T2 Macs needs an unsigned driver module, which means turning on Windows Test Signing.
It is a real security trade-off, so it is opt-in: follow **[docs/FAN-SETUP.md](docs/FAN-SETUP.md)**. Everything else works without it.

## Safety

- **Fans.** Targets are clamped to each fan's limits; at 95 °C the fans go to maximum; MacTune hands the fans back to Apple on exit, crash, shutdown, or if they stop responding. Still, you are overriding Apple's thermal management — use sensible modes.
- **Power settings.** Profiles live in three power plans named "MacTune - …". Your existing plans are not edited. Advanced CPU sliders change the *active* plan; "Default" restores Windows defaults.
- **Reversible tweaks.** Startup apps are disabled the same way Task Manager does it (nothing is deleted). The boot optimizations can each be switched back off.
- **Cleanup** only deletes temp files older than 24 hours and, if you tick it, the Recycle Bin.
- **Admin rights.** MacTune runs elevated. The installer puts it in Program Files so that only an administrator can replace the program.
- No telemetry, no network access.

This software is provided **without any warranty** (see [LICENSE](LICENSE)). You use it at your own risk.

## Build from source

```powershell
winget install Microsoft.DotNet.SDK.8
git clone https://github.com/PNT2110/MacTune.git
cd MacTune
dotnet build -c Release            # output: bin\Release\net8.0-windows\MacTune.exe
.\build-release.ps1 -Version 1.0.0 # self-contained zip in .\dist
```

Project layout:

```
App.xaml, MainWindow*.xaml(.cs)   UI (WPF). MainWindow is split into partial classes by feature
TrayFlyoutWindow.xaml(.cs)        quick-controls flyout
Controls/                         small custom controls (line chart)
Services/                         hardware and Windows access, no UI
  SmcService, FanController       SMC (PawnIO / AppleSMC / MacHAL back ends) and fan logic
  PowerService, Profiles          powercfg wrapper and the three profiles
  SensorService                   LibreHardwareMonitor telemetry
  Loc, Loc.Dictionary             runtime Vietnamese → English translation
drivers/                          AppleT2Smc PawnIO module (LGPL, from RPMac) + its source
docs/                             guides
```

The UI is written in Vietnamese; English is produced at runtime from `Services/Loc.Dictionary.cs`. To add or fix a translation, edit that file.
Diagnostic log: `C:\ProgramData\MacTune\log.txt`. Settings: `C:\ProgramData\MacTune\settings.json`.

## Contributing

Reports from other Mac models are especially welcome — please include the model identifier (e.g. `MacBookPro15,1`), Windows version, and the log file.
Hardware-writing changes (fans, SMC, HID) should fail closed: verify the key or report exists and is writable before sending anything.

## Credits

MacTune stands on the work of people who figured out how this hardware talks:
[RPMac](https://github.com/golirt1/RPMAC) (T2 SMC module, used here under LGPL),
[PawnIO](https://github.com/namazso/PawnIO.Setup),
[LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor),
[SimpleMacFan](https://github.com/ozaretskyi/SimpleMacFan),
[mac-fan-control-windows](https://github.com/charlie754/mac-fan-control-windows),
[BootCampPerformanceControl](https://github.com/mksolutionsuniversal/BootCampPerformanceControl),
and the Linux `hid-apple` driver. Details in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Not affiliated with Apple. Mac, MacBook Pro, Boot Camp and Touch Bar are trademarks of Apple Inc.

## License

[GPL-3.0](LICENSE). The bundled `drivers/AppleT2Smc.*` files are LGPL-2.1-or-later.

---

## Tiếng Việt

**MacTune là bảng điều khiển cho máy Mac Intel chạy Windows qua Boot Camp**: quạt, hiệu năng CPU, pin, đèn bàn phím, phím kiểu Mac (`⌘+Tab` đổi app, `⌘+Q`, `⌘+Shift+4` chụp ảnh), chống bụp loa, ổn định Bluetooth/Wi-Fi, cuộn chuột tự nhiên và chống 100% Disk lúc mở máy — gom gọn trong một app.

> App mới được thử trên một máy: **MacBook Pro 16" 2019 (MacBookPro16,1) chạy Windows 11**. Máy khác có thể chỉ chạy được một phần.

### Cài đặt

1. Tải file `MacTune-x.y.z-win-x64.zip` ở trang **Releases** rồi giải nén.
2. Chuột phải **`install.ps1`** → **Run with PowerShell**, bấm **Yes** khi Windows hỏi quyền admin.

App được cài vào `C:\Program Files\MacTune`, tự mở, và tạo icon trên Desktop (mở không bị hỏi quyền). Không cần cài .NET.

- Đổi ngôn ngữ: **Cài đặt → Ngôn ngữ**.
- Gỡ cài đặt: chạy `install.ps1 -Uninstall`.

### Điều khiển quạt (tùy chọn)

Trên máy có chip T2, chỉnh quạt cần driver PawnIO bản Unrestricted và bật Test Signing của Windows. Việc này **làm giảm bảo mật** và một số game có chống gian lận sẽ không chạy, nên bạn tự quyết định có bật hay không.
Hướng dẫn từng bước: [docs/FAN-SETUP.md](docs/FAN-SETUP.md) (tiếng Anh) hoặc file `HUONG-DAN-QUAT.txt` trong thư mục app (tiếng Việt). Các tính năng khác không cần bước này.

### Những thứ app không làm được

- Giới hạn sạc 80% trên máy T2 (thay bằng thông báo khi pin đạt 80%)
- Undervolt, chỉnh PL1/PL2
- Đọc S.M.A.R.T. của ổ SSD Apple
- Tắt hẳn card AMD (chỉ chọn được GPU cho từng app)

### An toàn

Quạt luôn nằm trong giới hạn của từng quạt, tự chạy tối đa khi CPU từ 95 °C, và được trả về cho Apple khi thoát app, app lỗi hoặc tắt máy. App không thu thập dữ liệu và không kết nối mạng. Phần mềm **không kèm bảo hành**, bạn tự chịu rủi ro khi dùng.
