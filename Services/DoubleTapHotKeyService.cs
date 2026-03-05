using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiuYun.Services
{
    public sealed class DoubleTapHotKeyService : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_SHIFT = 0x10;
        private const uint VK_CONTROL = 0x11;
        private const uint VK_MENU = 0x12;
        private const uint VK_LSHIFT = 0xA0;
        private const uint VK_RSHIFT = 0xA1;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;
        private const uint VK_LMENU = 0xA4;
        private const uint VK_RMENU = 0xA5;

        private enum DoubleTapTargetKey
        {
            None = 0,
            Ctrl = 1,
            Alt = 2,
            Shift = 3
        }

        private readonly HookProc _hookProc;
        private readonly DoubleTapTargetKey _targetKey;
        private readonly int _tapIntervalMs;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _disposed;

        private DateTime _lastTargetTapUtc = DateTime.MinValue;
        private bool _sawOtherKeySinceLastTap;

        public event EventHandler? HotKeyPressed;

        public bool IsActive => _hookHandle != IntPtr.Zero;
        public int LastErrorCode { get; private set; }
        public string? LastErrorMessage { get; private set; }

        public DoubleTapHotKeyService(string tapKey, int tapIntervalMs)
        {
            _hookProc = HookCallback;
            _targetKey = ParseTapKey(tapKey);
            _tapIntervalMs = Math.Clamp(tapIntervalMs, 200, 500);

            if (_targetKey == DoubleTapTargetKey.None)
            {
                LastErrorCode = 87;
                LastErrorMessage = "Invalid double tap key.";
                return;
            }

            IntPtr moduleHandle = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                CaptureLastError();
            }
        }

        private static DoubleTapTargetKey ParseTapKey(string tapKey)
        {
            return tapKey.Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => DoubleTapTargetKey.Ctrl,
                "alt" => DoubleTapTargetKey.Alt,
                "shift" => DoubleTapTargetKey.Shift,
                _ => DoubleTapTargetKey.None
            };
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
            {
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }

            uint vkCode = unchecked((uint)Marshal.ReadInt32(lParam));
            int message = unchecked((int)wParam.ToInt64());

            if (message == WM_KEYDOWN || message == WM_SYSKEYDOWN)
            {
                if (!IsTargetKey(vkCode))
                {
                    _sawOtherKeySinceLastTap = true;
                }
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (IsTargetKey(vkCode))
                {
                    DateTime now = DateTime.UtcNow;
                    bool isDoubleTap =
                        _lastTargetTapUtc != DateTime.MinValue &&
                        !_sawOtherKeySinceLastTap &&
                        (now - _lastTargetTapUtc).TotalMilliseconds <= _tapIntervalMs;

                    if (isDoubleTap)
                    {
                        _lastTargetTapUtc = DateTime.MinValue;
                        _sawOtherKeySinceLastTap = false;
                        HotKeyPressed?.Invoke(this, EventArgs.Empty);
                    }
                    else
                    {
                        _lastTargetTapUtc = now;
                        _sawOtherKeySinceLastTap = false;
                    }
                }
                else
                {
                    _sawOtherKeySinceLastTap = true;
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private bool IsTargetKey(uint vkCode)
        {
            return _targetKey switch
            {
                DoubleTapTargetKey.Ctrl => vkCode == VK_CONTROL || vkCode == VK_LCONTROL || vkCode == VK_RCONTROL,
                DoubleTapTargetKey.Alt => vkCode == VK_MENU || vkCode == VK_LMENU || vkCode == VK_RMENU,
                DoubleTapTargetKey.Shift => vkCode == VK_SHIFT || vkCode == VK_LSHIFT || vkCode == VK_RSHIFT,
                _ => false
            };
        }

        private void CaptureLastError()
        {
            LastErrorCode = Marshal.GetLastWin32Error();
            if (LastErrorCode == 0)
            {
                LastErrorMessage = "Unknown error.";
                return;
            }

            try
            {
                LastErrorMessage = new Win32Exception(LastErrorCode).Message;
            }
            catch
            {
                LastErrorMessage = "Unknown error.";
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_hookHandle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }

            _disposed = true;
        }

        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
