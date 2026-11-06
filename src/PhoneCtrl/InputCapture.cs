using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using PhoneCtrl.Ble;

namespace PhoneCtrl
{
    /// <summary>
    /// "Mouse on iPhone" mode. While active, the PC's mouse and keyboard are swallowed by low-level hooks
    /// and forwarded to the phone over Bluetooth. The exit shortcut (default Ctrl+Alt+Q) gives them back.
    /// </summary>
    internal sealed class InputCapture : IDisposable
    {
        // ---- Win32 ----
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr extra; }

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT { public uint vkCode; public uint scanCode; public uint flags; public uint time; public IntPtr extra; }

        private const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
        private const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
        private const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
            WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208,
            WM_MOUSEWHEEL = 0x20A, WM_XBUTTONDOWN = 0x20B, WM_XBUTTONUP = 0x20C;
        private const uint LLMHF_INJECTED = 0x01, LLKHF_INJECTED = 0x10;

        // HID modifier bits
        private const byte LCtrl = 0x01, LShift = 0x02, LAlt = 0x04, LGui = 0x08,
            RCtrl = 0x10, RShift = 0x20, RAlt = 0x40, RGui = 0x80;

        // ---- state ----
        private readonly HidPeripheral _phone;
        private readonly Dispatcher _dispatcher;
        private readonly HookProc _mouseProc;
        private readonly HookProc _keyboardProc;
        private readonly object _gate = new object();
        private readonly List<byte> _keys = new List<byte>();

        private IntPtr _mouseHook, _keyboardHook;
        private POINT _anchor;
        private POINT _originalCursor;
        private int _dx, _dy, _wheelAccum, _wheelNotches;
        private byte _buttons, _sentButtons;
        private byte _modifiers;
        private bool _keyboardDirty;
        private CancellationTokenSource? _loopCts;

        private uint _exitMods;  // MOD_* bits from HotKeys.TryParse
        private uint _exitVk;

        public bool IsActive { get; private set; }

        /// <summary>Raised on the UI thread when capture starts (true) or stops (false).</summary>
        public event Action<bool>? ActiveChanged;

        public InputCapture(HidPeripheral phone, Dispatcher dispatcher)
        {
            _phone = phone;
            _dispatcher = dispatcher;
            _mouseProc = MouseHook;       // keep delegates alive (the GC must not collect them)
            _keyboardProc = KeyboardHook;
        }

        public void SetExitShortcut(string text)
        {
            if (!HotKeys.TryParse(text, out _exitMods, out _exitVk))
                HotKeys.TryParse("Ctrl+Alt+Q", out _exitMods, out _exitVk);
        }

        // ------------------------------------------------------------------ start / stop

        public bool Start()
        {
            if (IsActive) return true;
            if (!_phone.IsConnected) return false;

            // Park the frozen PC cursor in the middle of the main screen; at a screen edge Windows
            // would clip movement and the phone pointer could not move in that direction.
            GetCursorPos(out _originalCursor);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            SetCursorPos(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
            GetCursorPos(out _anchor);
            lock (_gate)
            {
                _dx = _dy = _wheelAccum = _wheelNotches = 0;
                _buttons = _sentButtons = 0;
                _modifiers = 0;
                _keys.Clear();
                _keyboardDirty = false;
            }

            var module = GetModuleHandle(null);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);
            if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
            {
                Log.Error($"Could not install input hooks (error {Marshal.GetLastWin32Error()})");
                RemoveHooks();
                return false;
            }

            IsActive = true;
            _loopCts = new CancellationTokenSource();
            var token = _loopCts.Token;
            _ = Task.Run(() => SendLoopAsync(token));
            Log.Info("Capture started");
            ActiveChanged?.Invoke(true);
            return true;
        }

        public void Stop()
        {
            if (!IsActive) return;
            IsActive = false;
            RemoveHooks();
            SetCursorPos(_originalCursor.X, _originalCursor.Y);
            _loopCts?.Cancel();
            _loopCts = null;

            // Release everything on the phone so no key or button stays "held down".
            _ = ReleaseAllAsync();
            Log.Info("Capture stopped");
            ActiveChanged?.Invoke(false);
        }

        private async Task ReleaseAllAsync()
        {
            await _phone.SendKeyboardAsync(0, Array.Empty<byte>());
            await _phone.SendMouseAsync(0, 0, 0, 0);
        }

        private void RemoveHooks()
        {
            if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
            if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
            _mouseHook = _keyboardHook = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Coalesces input and sends it to the phone at the Bluetooth report rate.</summary>
        private async Task SendLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                bool sentSomething = false;
                try
                {
                    byte mods = 0;
                    byte[]? keys = null;
                    int dx, dy, wheel;
                    byte buttons;
                    bool mouseDirty;
                    lock (_gate)
                    {
                        if (_keyboardDirty)
                        {
                            mods = _modifiers;
                            keys = _keys.ToArray();
                            _keyboardDirty = false;
                        }
                        dx = _dx; dy = _dy; wheel = _wheelNotches; buttons = _buttons;
                        _dx = _dy = _wheelNotches = 0;
                        mouseDirty = dx != 0 || dy != 0 || wheel != 0 || buttons != _sentButtons;
                        _sentButtons = buttons;
                    }

                    if (keys != null)
                    {
                        await _phone.SendKeyboardAsync(mods, keys);
                        sentSomething = true;
                    }
                    if (mouseDirty)
                    {
                        await _phone.SendMouseAsync(buttons, dx, dy, wheel);
                        sentSomething = true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Capture send failed", ex);
                }

                if (!sentSomething)
                {
                    try { await Task.Delay(8, token); } catch (TaskCanceledException) { }
                }
            }
        }

        // ------------------------------------------------------------------ hooks

        private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || !IsActive) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLMHF_INJECTED) != 0) return CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            lock (_gate)
            {
                switch (msg)
                {
                    case WM_MOUSEMOVE:
                        // The PC cursor is frozen at _anchor, so each event's offset from it is the movement.
                        _dx += info.pt.X - _anchor.X;
                        _dy += info.pt.Y - _anchor.Y;
                        break;
                    case WM_LBUTTONDOWN: _buttons |= 0x01; break;
                    case WM_LBUTTONUP: _buttons &= unchecked((byte)~0x01); break;
                    case WM_RBUTTONDOWN: _buttons |= 0x02; break;
                    case WM_RBUTTONUP: _buttons &= unchecked((byte)~0x02); break;
                    case WM_MBUTTONDOWN: _buttons |= 0x04; break;
                    case WM_MBUTTONUP: _buttons &= unchecked((byte)~0x04); break;
                    case WM_XBUTTONDOWN:
                    case WM_XBUTTONUP:
                        byte bit = (info.mouseData >> 16) == 1 ? (byte)0x08 : (byte)0x10;
                        if (msg == WM_XBUTTONDOWN) _buttons |= bit; else _buttons &= unchecked((byte)~bit);
                        break;
                    case WM_MOUSEWHEEL:
                        _wheelAccum += (short)(info.mouseData >> 16);
                        int notches = _wheelAccum / 120;
                        _wheelNotches += notches;
                        _wheelAccum -= notches * 120;
                        break;
                }
            }
            return (IntPtr)1; // swallow: the PC doesn't see it
        }

        private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || !IsActive) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & LLKHF_INJECTED) != 0) return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

            int msg = wParam.ToInt32();
            bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
            bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;
            uint vk = info.vkCode;

            lock (_gate)
            {
                // Exit shortcut: give the mouse and keyboard back to the PC.
                if (down && vk == _exitVk && ModsMatch(_modifiers, _exitMods))
                {
                    _dispatcher.BeginInvoke(new Action(Stop));
                    return (IntPtr)1;
                }

                byte modBit = ModifierBit(vk);
                if (modBit != 0)
                {
                    if (down) _modifiers |= modBit;
                    if (up) _modifiers &= unchecked((byte)~modBit);
                    _keyboardDirty = true;
                }
                else
                {
                    byte usage = KeyMap.ToHidUsage(vk);
                    if (usage != 0)
                    {
                        if (down && !_keys.Contains(usage) && _keys.Count < 6) { _keys.Add(usage); _keyboardDirty = true; }
                        if (up && _keys.Remove(usage)) _keyboardDirty = true;
                    }
                }
            }
            return (IntPtr)1;
        }

        private static bool ModsMatch(byte hid, uint required)
        {
            bool ctrl = (hid & (LCtrl | RCtrl)) != 0, alt = (hid & (LAlt | RAlt)) != 0,
                shift = (hid & (LShift | RShift)) != 0, win = (hid & (LGui | RGui)) != 0;
            return ctrl == ((required & 0x2) != 0) && alt == ((required & 0x1) != 0)
                && shift == ((required & 0x4) != 0) && win == ((required & 0x8) != 0);
        }

        private static byte ModifierBit(uint vk) => vk switch
        {
            0xA2 => LCtrl, 0xA3 => RCtrl,
            0xA0 => LShift, 0xA1 => RShift,
            0xA4 => LAlt, 0xA5 => RAlt,
            0x5B => LGui, 0x5C => RGui, // Windows key → ⌘ Command on the iPhone
            _ => 0,
        };

        public void Dispose() => Stop();
    }

    /// <summary>Windows virtual-key code → USB HID keyboard usage.</summary>
    internal static class KeyMap
    {
        public static byte ToHidUsage(uint vk)
        {
            if (vk >= 0x41 && vk <= 0x5A) return (byte)(0x04 + (vk - 0x41));   // A-Z
            if (vk >= 0x31 && vk <= 0x39) return (byte)(0x1E + (vk - 0x31));   // 1-9
            if (vk >= 0x70 && vk <= 0x7B) return (byte)(0x3A + (vk - 0x70));   // F1-F12
            if (vk >= 0x61 && vk <= 0x69) return (byte)(0x59 + (vk - 0x61));   // Numpad 1-9
            return vk switch
            {
                0x30 => 0x27, // 0
                0x0D => 0x28, // Enter
                0x1B => 0x29, // Esc
                0x08 => 0x2A, // Backspace
                0x09 => 0x2B, // Tab
                0x20 => 0x2C, // Space
                0xBD => 0x2D, // - _
                0xBB => 0x2E, // = +
                0xDB => 0x2F, // [ {
                0xDD => 0x30, // ] }
                0xDC => 0x31, // \ |
                0xBA => 0x33, // ; :
                0xDE => 0x34, // ' "
                0xC0 => 0x35, // ` ~
                0xBC => 0x36, // , <
                0xBE => 0x37, // . >
                0xBF => 0x38, // / ?
                0x14 => 0x39, // Caps Lock
                0x2D => 0x49, // Insert
                0x24 => 0x4A, // Home
                0x21 => 0x4B, // Page Up
                0x2E => 0x4C, // Delete
                0x23 => 0x4D, // End
                0x22 => 0x4E, // Page Down
                0x27 => 0x4F, // Right
                0x25 => 0x50, // Left
                0x28 => 0x51, // Down
                0x26 => 0x52, // Up
                0x60 => 0x62, // Numpad 0
                0x6E => 0x63, // Numpad .
                0x6A => 0x55, // Numpad *
                0x6B => 0x57, // Numpad +
                0x6D => 0x56, // Numpad -
                0x6F => 0x54, // Numpad /
                _ => 0,
            };
        }
    }
}
