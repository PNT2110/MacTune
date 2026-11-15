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
    /// </summary>
    public static bool SetTouchBarFnDefault(bool fnDefault)
    {
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
/// so shortcuts like ⌘+C / ⌘+V / ⌘+Z / ⌘+T work instantly like macOS with zero reboot or UI lag.
/// </summary>
public sealed class MacKeySwapService : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const uint WM_QUIT = 0x0012;
    const int VK_LWIN = 0x5B;
    const int VK_RWIN = 0x5C;
    const int VK_LCONTROL = 0xA2;
    const ushort SC_LCTRL = 0x1D;
    const ushort SC_LWIN = 0x5B;
    const uint LLKHF_EXTENDED = 0x01;
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
    volatile bool _synthCtrlDown, _synthWinDown;

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

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _enabled)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            // Ignore synthetic events so our own SendInput passes straight through
            if ((kb.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) == 0)
            {
                bool isUp = (kb.flags & LLKHF_UP) != 0;
                if (kb.vkCode is VK_LWIN or VK_RWIN)
                {
                    _synthCtrlDown = !isUp;
                    SendKey(VK_LCONTROL, SC_LCTRL, isUp ? KEYEVENTF_KEYUP : 0);
                    return (IntPtr)1;
                }
                if (kb.vkCode == VK_LCONTROL)
                {
                    _synthWinDown = !isUp;
                    SendKey(VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY | (isUp ? KEYEVENTF_KEYUP : 0));
                    return (IntPtr)1;
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
        if (_synthCtrlDown) { _synthCtrlDown = false; SendKey(VK_LCONTROL, SC_LCTRL, KEYEVENTF_KEYUP); }
        if (_synthWinDown) { _synthWinDown = false; SendKey(VK_LWIN, SC_LWIN, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP); }
    }

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SetEnabled(false);
    }
}
