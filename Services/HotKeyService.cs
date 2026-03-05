using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LiuYun.Services
{
    public sealed partial class HotKeyService : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const int WM_DESTROY = 0x0002;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;
        private const uint HOTKEY_ID = 0x3000;
        private const uint SUBCLASS_ID = 0x3001;

        private static readonly Dictionary<string, uint> KeyCodes = new()
        {
            { "A", 0x41 }, { "B", 0x42 }, { "C", 0x43 }, { "D", 0x44 },
            { "E", 0x45 }, { "F", 0x46 }, { "G", 0x47 }, { "H", 0x48 },
            { "I", 0x49 }, { "J", 0x4A }, { "K", 0x4B }, { "L", 0x4C },
            { "M", 0x4D }, { "N", 0x4E }, { "O", 0x4F }, { "P", 0x50 },
            { "Q", 0x51 }, { "R", 0x52 }, { "S", 0x53 }, { "T", 0x54 },
            { "U", 0x55 }, { "V", 0x56 }, { "W", 0x57 }, { "X", 0x58 },
            { "Y", 0x59 }, { "Z", 0x5A }
        };

        private readonly IntPtr _windowHandle;
        private readonly SUBCLASSPROC _windowProc;
        private bool _isRegistered;
        private bool _isSubclassed;
        private bool _disposed;
        private int _lastErrorCode;
        private string? _lastErrorMessage;

        public event EventHandler? HotKeyPressed;
        public bool IsActive => _isRegistered;
        public int LastErrorCode => _lastErrorCode;
        public string? LastErrorMessage => _lastErrorMessage;

        public HotKeyService(IntPtr windowHandle)
            : this(windowHandle, HotkeyConfigService.GetModifier(), HotkeyConfigService.GetKey())
        {
        }

        public HotKeyService(IntPtr windowHandle, string modifier, string key)
        {
            _windowHandle = windowHandle;
            _windowProc = SubclassWndProc;

            if (!SetWindowSubclass(windowHandle, _windowProc, SUBCLASS_ID, IntPtr.Zero))
            {
                return;
            }
            _isSubclassed = true;

            uint modifiers = ParseModifier(modifier) | MOD_NOREPEAT;
            uint vkCode = GetKeyCode(key);
            if (modifiers == MOD_NOREPEAT || vkCode == 0)
            {
                _lastErrorCode = 87;
                _lastErrorMessage = "快捷键配置无效。";
                RemoveWindowSubclass(_windowHandle, _windowProc, SUBCLASS_ID);
                _isSubclassed = false;
                return;
            }

            _isRegistered = RegisterHotKey(_windowHandle, (int)HOTKEY_ID, modifiers, vkCode);
            if (!_isRegistered)
            {
                CaptureLastError();
                RemoveWindowSubclass(_windowHandle, _windowProc, SUBCLASS_ID);
                _isSubclassed = false;
            }
        }

        private static uint ParseModifier(string modifier)
        {
            if (string.IsNullOrWhiteSpace(modifier))
            {
                return 0;
            }

            uint parsed = 0;
            string[] tokens = modifier
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (string token in tokens)
            {
                uint tokenMask = token switch
                {
                    "Ctrl" or "Control" => MOD_CONTROL,
                    "Alt" => MOD_ALT,
                    "Shift" => MOD_SHIFT,
                    "Win" or "Windows" => MOD_WIN,
                    _ => 0xFFFF_FFFF
                };
                if (tokenMask == 0xFFFF_FFFF)
                {
                    return 0;
                }

                parsed |= tokenMask;
            }

            return parsed;
        }

        private static uint GetKeyCode(string key)
        {
            if (KeyCodes.TryGetValue(key.ToUpper(), out uint code))
            {
                return code;
            }
            return 0;
        }

        private IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (msg == WM_HOTKEY && wParam == (IntPtr)HOTKEY_ID)
            {
                HotKeyPressed?.Invoke(this, EventArgs.Empty);
            }
            else if (msg == WM_DESTROY)
            {
                Unregister();
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        private void Unregister()
        {
            if (_isRegistered)
            {
                UnregisterHotKey(_windowHandle, (int)HOTKEY_ID);
                _isRegistered = false;
            }
        }

        public static (bool CanRegister, int ErrorCode, string ErrorMessage) ProbeWinVRegistration()
        {
            return ProbeWinVRegistration(MOD_WIN | MOD_NOREPEAT);
        }

        public static (bool CanRegister, int ErrorCode, string ErrorMessage) ProbeWinVRegistrationNoRepeatOff()
        {
            return ProbeWinVRegistration(MOD_WIN);
        }

        private static (bool CanRegister, int ErrorCode, string ErrorMessage) ProbeWinVRegistration(uint modifiers)
        {
            const int probeId = 0x7FFE;
            const uint vkV = 0x56;

            bool registered = RegisterHotKey(IntPtr.Zero, probeId, modifiers, vkV);
            if (registered)
            {
                UnregisterHotKey(IntPtr.Zero, probeId);
                return (true, 0, "OK");
            }

            int error = Marshal.GetLastWin32Error();
            string message;
            try
            {
                message = new Win32Exception(error).Message;
            }
            catch
            {
                message = "Unknown error";
            }

            return (false, error, message);
        }

        public static (bool CanRegister, int ErrorCode, string ErrorMessage) ProbeRegistration(string modifier, string key)
        {
            uint parsedModifiers = ParseModifier(modifier) | MOD_NOREPEAT;
            uint vkCode = GetKeyCode(key);
            if (parsedModifiers == MOD_NOREPEAT || vkCode == 0)
            {
                return (false, 87, "Invalid hotkey.");
            }

            const int probeId = 0x7FFD;
            bool registered = RegisterHotKey(IntPtr.Zero, probeId, parsedModifiers, vkCode);
            if (registered)
            {
                UnregisterHotKey(IntPtr.Zero, probeId);
                return (true, 0, "OK");
            }

            int error = Marshal.GetLastWin32Error();
            string message;
            try
            {
                message = new Win32Exception(error).Message;
            }
            catch
            {
                message = "Unknown error";
            }

            return (false, error, message);
        }

        private void CaptureLastError()
        {
            _lastErrorCode = Marshal.GetLastWin32Error();
            if (_lastErrorCode != 0)
            {
                try
                {
                    _lastErrorMessage = new Win32Exception(_lastErrorCode).Message;
                }
                catch
                {
                    _lastErrorMessage = "Unknown error.";
                }
            }
            else
            {
                _lastErrorMessage = "Unknown error.";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Unregister();
            if (_isSubclassed)
            {
                RemoveWindowSubclass(_windowHandle, _windowProc, SUBCLASS_ID);
                _isSubclassed = false;
            }
            _disposed = true;
        }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        [LibraryImport("comctl32.dll")]
        private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
