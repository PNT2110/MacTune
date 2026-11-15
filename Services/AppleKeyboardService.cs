using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace MacTune.Services;

/// <summary>
/// Apple Boot Camp Touch Bar mode (F1-F12 vs Media Control Strip via AppleDFR/KeyMagic IOCTL)
/// and instant macOS-style Command (⌘) ↔ Ctrl modifier swap.
/// </summary>
public static class AppleKeyboardService
{
    const string HkcuAppleKbd = @"Software\Apple Inc.\Apple Keyboard Support";
    const string HklmKeyMagic = @"SYSTEM\CurrentControlSet\Services\KeyMagic";
    const uint IoctlSetOsxFnBehavior = 0xB403201C;

    static readonly string[] AppleDevices =
    {
        @"\\.\AppleKeyboard",
        @"\\.\AppleKeyMagic2Keyboard",
        @"\\.\AppleKeyboardInternalUSB",
        @"\\.\AppleSPIKeyboard",
        @"\\.\AppleDFR",
    };

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, ref int lpInBuffer, uint nInBufferSize,
        IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr hObject);

    /// <summary>True when at least one Apple keyboard / Touch Bar kernel device responds or KeyMagic service exists.</summary>
    public static bool HasTouchBarDriverDevice()
    {
        foreach (var dev in AppleDevices)
        {
            IntPtr h = CreateFileW(dev, 0x80000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            if (h != IntPtr.Zero && h != new IntPtr(-1))
            {
                CloseHandle(h);
                return true;
            }
        }
        try
        {
            using var km = Registry.LocalMachine.OpenSubKey(HklmKeyMagic);
            if (km is not null) return true;
        }
        catch { }
        return false;
    }

    public static bool IsTouchBarSupportedOnThisMac()
    {
        var (mfg, model) = HardwareCompatibilityService.ReadBiosIdentity();
        if (!HardwareCompatibilityService.IsAppleModel(mfg, model)) return false;
        return HardwareCompatibilityService.ModelSupportsTouchBar(model) || HasTouchBarDriverDevice();
    }

    /// <summary>
    /// Returns true if Touch Bar defaults to F1–F12 keys (OSXFnBehavior == 0),
    /// false if Touch Bar defaults to Media / Brightness Control Strip (OSXFnBehavior == 1).
    /// </summary>
    public static bool IsTouchBarFnDefault()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(HkcuAppleKbd);
            if (k?.GetValue("OSXFnBehavior") is int v) return v == 0;
        }
        catch { }

        try
        {
            using var km = Registry.LocalMachine.OpenSubKey(HklmKeyMagic);
            if (km?.GetValue("OSXFnBehavior") is byte[] b && b.Length > 0) return b[0] == 0;
            if (km?.GetValue("OSXFnBehavior") is int vi) return vi == 0;
        }
        catch { }

        return false;
    }

    /// <summary>
    /// Switches Touch Bar immediately between F1–F12 default (fnDefault=true, OSXFnBehavior=0)
    /// and Media/Brightness Control Strip default (fnDefault=false, OSXFnBehavior=1).
    /// Fail-closed: refuses to write on non-Apple or non-Touch-Bar Macs.
    /// </summary>
    public static bool SetTouchBarFnDefault(bool fnDefault)
    {
        if (!IsTouchBarSupportedOnThisMac()) return false;
        int osxVal = fnDefault ? 0 : 1;

        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(HkcuAppleKbd);
            k.SetValue("OSXFnBehavior", osxVal, RegistryValueKind.DWord);
        }
        catch { }

        try
        {
            using var users = Registry.Users;
            foreach (var sid in users.GetSubKeyNames())
            {
                if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || sid.EndsWith("_Classes", StringComparison.Ordinal))
                    continue;
                try
                {
                    using var uk = users.CreateSubKey($@"{sid}\{HkcuAppleKbd}");
                    uk.SetValue("OSXFnBehavior", osxVal, RegistryValueKind.DWord);
                }
                catch { }
            }
        }
        catch { }

        try
        {
            using var km = Registry.LocalMachine.OpenSubKey(HklmKeyMagic, writable: true);
            km?.SetValue("OSXFnBehavior", new byte[] { (byte)osxVal }, RegistryValueKind.Binary);
        }
        catch { }

        bool anyDriverOk = false;
        foreach (var dev in AppleDevices)
        {
            IntPtr h = CreateFileW(dev, 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) continue;
            try
            {
                int buf = osxVal;
                if (DeviceIoControl(h, IoctlSetOsxFnBehavior, ref buf, 4u, IntPtr.Zero, 0u, out _, IntPtr.Zero))
                    anyDriverOk = true;
            }
            finally { CloseHandle(h); }
        }
        return anyDriverOk;
    }
}

/// <summary>
/// Swaps Left/Right Command (Win) ↔ Left Control on a dedicated high-priority message loop thread
/// so shortcuts like ⌘+C / ⌘+V / ⌘+Z / ⌘+T work instantly like macOS, plus:
/// - ⌘+Tab → Alt+Tab (app switcher), ⌃+Tab → Ctrl+Tab (browser/editor tab switcher)
/// - ⌘+Q → Alt+F4 (close window)
/// - ⌘+Shift+3 / ⌘+Shift+4 → Win+Shift+S (macOS screenshot)
/// - Bare Left Ctrl tap suppressed so Start Menu never pops up accidentally.
/// </summary>
public sealed class MacKeySwapService : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const uint WM_QUIT = 0x0012;
    const uint VK_TAB = 0x09;
    const uint VK_3 = 0x33;
    const uint VK_4 = 0x34;
    const uint VK_Q = 0x51;
    const uint VK_S = 0x53;
    const uint VK_LWIN = 0x5B;
    const uint VK_RWIN = 0x5C;
    const uint VK_F4 = 0x73;
    const uint VK_LSHIFT = 0xA0;
    const uint VK_RSHIFT = 0xA1;
    const uint VK_LCONTROL = 0xA2;
    const uint VK_RCONTROL = 0xA3;
    const uint VK_LMENU = 0xA4;
    const uint VK_RMENU = 0xA5;

    const ushort SC_LCTRL = 0x1D;
    const ushort SC_S = 0x1F;
    const ushort SC_LALT = 0x38;
    const ushort SC_F4 = 0x3E;
    const ushort SC_LWIN = 0x5B;

    const uint LLKHF_LOWER_IL_INJECTED = 0x02;
    const uint LLKHF_INJECTED = 0x10;
    const uint LLKHF_UP = 0x80;
    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    const uint KEYEVENTF_KEYUP = 0x0002;

    delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint nInputs, ref INPUT pInputs, int cbSize);

    [DllImport("user32.dll")]
    static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    static extern bool PostThreadMessageW(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? lpModuleName);

    readonly LowLevelKeyboardProc _proc;
    readonly object _gate = new();
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    volatile bool _enabled;
    volatile bool _physLWin, _physRWin, _physLCtrl, _physLShift, _physRShift, _physLAlt, _physRAlt;
    volatile bool _cmdTabActive;
    volatile uint _swallowedVk;
    volatile bool _synthCtrlDown, _synthWinDown, _synthAltDown, _synthCtrlForTab;

    bool PhysCmd => _physLWin || _physRWin;
    bool PhysShift => _physLShift || _physRShift;
    bool PhysAlt => _physLAlt || _physRAlt;

    public bool Enabled => _enabled;

    public MacKeySwapService()
    {
        _proc = HookCallback;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    void OnSessionSwitch(object sender, SessionSwitchEventArgs e) => ReleaseSyntheticModifiers();

    public void SetEnabled(bool enable)
    {
        lock (_gate)
        {
            if (enable == _enabled) return;
            _enabled = enable;
            if (enable)
            {
                if (_thread is null || !_thread.IsAlive)
                {
                    var ready = new ManualResetEventSlim(false);
                    _thread = new Thread(() => Pump(ready))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                        Name = "MacTune-KeySwap",
                    };
                    _thread.Start();
                    ready.Wait(1000);
                }
            }
            else
            {
                StopHookLocked();
                ReleaseSyntheticModifiers();
            }
        }
    }

    void Pump(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_QUIT) break;
        }

        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    void StopHookLocked()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _thread?.Join(400);
            _thread = null;
            _threadId = 0;
        }
    }

    static bool IsModifier(uint vk) => vk is VK_LSHIFT or VK_RSHIFT or VK_LMENU or VK_RMENU
        or VK_LCONTROL or VK_RCONTROL or VK_LWIN or VK_RWIN;

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore synthetic events so our own SendInput passes straight through
            if ((kb.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) == 0)
            {
                bool isUp = (kb.flags & LLKHF_UP) != 0;

                // Track physical Shift & Option (Alt) passively
                if (kb.vkCode == VK_LSHIFT) _physLShift = !isUp;
                else if (kb.vkCode == VK_RSHIFT) _physRShift = !isUp;
                else if (kb.vkCode == VK_LMENU) _physLAlt = !isUp;
                else if (kb.vkCode == VK_RMENU) _physRAlt = !isUp;

                // 1) Physical Command (⌘ = LWin / RWin) → Ctrl + macOS combos
                if (kb.vkCode is VK_LWIN or VK_RWIN)
                {
                    if (kb.vkCode == VK_LWIN) _physLWin = !isUp;
                    else _physRWin = !isUp;

                    if (!isUp)
                    {
                        if (!_cmdTabActive && !_synthCtrlDown)
                        {
                            _synthCtrlDown = true;
                            SendKey((ushort)VK_LCONTROL, SC_LCTRL, 0);
                        }
                    }
                    else if (!PhysCmd)
                    {
                        if (_cmdTabActive)
                        {
                            _cmdTabActive = false;
                            if (_synthAltDown) { _synthAltDown = false; SendKey((ushort)VK_LMENU, SC_LALT, KEYEVENTF_KEYUP); }
                        }
                        if (_synthCtrlDown) { _synthCtrlDown = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                    }
                    return (IntPtr)1;
                }

                // 2) Physical Left Control (⌃) → deferred Win (prevents accidental Start Menu tap) + ⌃+Tab → Ctrl+Tab
                if (kb.vkCode == VK_LCONTROL)
                {
                    _physLCtrl = !isUp;
                    if (isUp)
                    {
                        if (_synthWinDown) { _synthWinDown = false; SendKey((ushort)VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP); }
                        if (_synthCtrlForTab) { _synthCtrlForTab = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                    }
                    return (IntPtr)1;
                }

                // 3) Swallow matching key-up of a remapped shortcut key (Q / 3 / 4)
                if (isUp && _swallowedVk != 0 && kb.vkCode == _swallowedVk)
                {
                    _swallowedVk = 0;
                    if (PhysCmd && !_cmdTabActive && !_synthCtrlDown)
                    {
                        _synthCtrlDown = true;
                        SendKey((ushort)VK_LCONTROL, SC_LCTRL, 0);
                    }
                    return (IntPtr)1;
                }

                // 4) Special macOS shortcuts while holding Command (⌘)
                if (!isUp && PhysCmd)
                {
                    // ⌘ + Tab (or ⌘ + Shift + Tab) → Alt + Tab app switcher
                    if (kb.vkCode == VK_TAB && !PhysAlt)
                    {
                        if (!_cmdTabActive)
                        {
                            _cmdTabActive = true;
                            if (_synthCtrlDown) { _synthCtrlDown = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                            if (!_synthAltDown) { _synthAltDown = true; SendKey((ushort)VK_LMENU, SC_LALT, 0); }
                        }
                        return CallNextHookEx(_hook, nCode, wParam, lParam);
                    }

                    // ⌘ + Q → Alt + F4 (close window)
                    if (kb.vkCode == VK_Q && !PhysShift && !PhysAlt && !_cmdTabActive)
                    {
                        if (_synthCtrlDown) { _synthCtrlDown = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                        SendKey((ushort)VK_LMENU, SC_LALT, 0);
                        SendKey((ushort)VK_F4, SC_F4, 0);
                        SendKey((ushort)VK_F4, SC_F4, KEYEVENTF_KEYUP);
                        SendKey((ushort)VK_LMENU, SC_LALT, KEYEVENTF_KEYUP);
                        _swallowedVk = VK_Q;
                        return (IntPtr)1;
                    }

                    // ⌘ + Shift + 3 / 4 → Win + Shift + S (region screenshot)
                    if (kb.vkCode is VK_3 or VK_4 && PhysShift && !PhysAlt && !_cmdTabActive)
                    {
                        if (_synthCtrlDown) { _synthCtrlDown = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                        SendKey((ushort)VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY);
                        SendKey((ushort)VK_S, SC_S, 0);
                        SendKey((ushort)VK_S, SC_S, KEYEVENTF_KEYUP);
                        SendKey((ushort)VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP);
                        _swallowedVk = kb.vkCode;
                        return (IntPtr)1;
                    }
                }

                // 5) Shortcuts while holding Physical Left Control (⌃)
                if (!isUp && _physLCtrl && !IsModifier(kb.vkCode))
                {
                    if (kb.vkCode == VK_TAB && !_synthWinDown)
                    {
                        if (!_synthCtrlForTab) { _synthCtrlForTab = true; SendKey((ushort)VK_LCONTROL, SC_LCTRL, 0); }
                    }
                    else
                    {
                        if (_synthCtrlForTab) { _synthCtrlForTab = false; SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
                        if (!_synthWinDown) { _synthWinDown = true; SendKey((ushort)VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY); }
                    }
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    static void SendKey(ushort vk, ushort scan, uint flags)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } }
        };
        SendInput(1, ref inp, Marshal.SizeOf<INPUT>());
    }

    public void ReleaseSyntheticModifiers()
    {
        _physLWin = _physRWin = _physLCtrl = _physLShift = _physRShift = _physLAlt = _physRAlt = false;
        _cmdTabActive = false;
        _swallowedVk = 0;
        if (_synthCtrlDown || _synthCtrlForTab)
        {
            _synthCtrlDown = _synthCtrlForTab = false;
            SendKey((ushort)VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP);
        }
        if (_synthAltDown) { _synthAltDown = false; SendKey((ushort)VK_LMENU, SC_LALT, KEYEVENTF_KEYUP); }
        if (_synthWinDown) { _synthWinDown = false; SendKey((ushort)VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP); }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SetEnabled(false);
    }
}
