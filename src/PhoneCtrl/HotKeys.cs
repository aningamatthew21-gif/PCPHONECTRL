using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PhoneCtrl
{
    /// <summary>System-wide keyboard shortcuts (RegisterHotKey). They work even when another app has focus.</summary>
    internal sealed class HotKeys : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly HwndSource _source;
        private readonly Dictionary<int, PhoneAction> _registered = new Dictionary<int, PhoneAction>();
        private readonly Action<PhoneAction> _onHotKey;
        private int _nextId = 0xB000;

        public List<string> Failures { get; } = new List<string>();

        public HotKeys(HwndSource source, Action<PhoneAction> onHotKey)
        {
            _source = source;
            _onHotKey = onHotKey;
            _source.AddHook(WndProc);
        }

        public void Register(IDictionary<string, string> map)
        {
            UnregisterAll();
            Failures.Clear();
            foreach (var pair in map)
            {
                if (string.IsNullOrWhiteSpace(pair.Value)) continue;
                if (!Enum.TryParse(pair.Key, true, out PhoneAction action))
                {
                    Failures.Add($"Unknown action '{pair.Key}'");
                    continue;
                }
                if (!TryParse(pair.Value, out uint mods, out uint vk))
                {
                    Failures.Add($"Can't understand shortcut '{pair.Value}'");
                    continue;
                }
                int id = _nextId++;
                if (RegisterHotKey(_source.Handle, id, mods | MOD_NOREPEAT, vk))
                {
                    _registered[id] = action;
                    Log.Info($"Hotkey {pair.Value} → {action}");
                }
                else
                {
                    Failures.Add($"{pair.Value} is already used by another app");
                }
            }
            foreach (var f in Failures) Log.Info("Hotkey problem: " + f);
        }

        internal static bool TryParse(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return false;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].ToLowerInvariant())
                {
                    case "ctrl": case "control": mods |= MOD_CONTROL; break;
                    case "alt": mods |= MOD_ALT; break;
                    case "shift": mods |= MOD_SHIFT; break;
                    case "win": case "windows": mods |= MOD_WIN; break;
                    default: return false;
                }
            }
            if (!Enum.TryParse(parts[^1], true, out Key key)) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && _registered.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                _onHotKey(action);
            }
            return IntPtr.Zero;
        }

        private void UnregisterAll()
        {
            foreach (var id in _registered.Keys) UnregisterHotKey(_source.Handle, id);
            _registered.Clear();
        }

        public void Dispose()
        {
            UnregisterAll();
            _source.RemoveHook(WndProc);
        }
    }
}
