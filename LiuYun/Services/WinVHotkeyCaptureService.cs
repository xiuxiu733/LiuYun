using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiuYun.Services
{
    public sealed class WinVHotkeyCaptureService : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYUP = 0x0105;

        private const uint VK_V = 0x56;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;

        private readonly HookProc _hookProc;
        private IntPtr _hookHandle = IntPtr.Zero;
        private bool _disposed;
        private bool _winDown;
        private bool _winVTriggeredWhileWinDown;

        public event EventHandler? WinVPressed;

        public bool IsActive => _hookHandle != IntPtr.Zero;
        public int LastErrorCode { get; private set; }
        public string? LastErrorMessage { get; private set; }

        public WinVHotkeyCaptureService()
        {
            _hookProc = HookCallback;

            IntPtr moduleHandle = GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName);
            _hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, moduleHandle, 0);
            if (_hookHandle == IntPtr.Zero)
            {
                CaptureLastError();
            }
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
                if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                {
                    _winDown = true;
                    _winVTriggeredWhileWinDown = false;
                }
                else if (vkCode == VK_V)
                {
                    bool winPressed = _winDown || IsWinKeyDown();
                    if (winPressed && !_winVTriggeredWhileWinDown)
                    {
                        _winVTriggeredWhileWinDown = true;
                        WinVPressed?.Invoke(this, EventArgs.Empty);
                    }
                }
            }
            else if (message == WM_KEYUP || message == WM_SYSKEYUP)
            {
                if (vkCode == VK_LWIN || vkCode == VK_RWIN)
                {
                    _winDown = false;
                    _winVTriggeredWhileWinDown = false;
                }
            }

            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static bool IsWinKeyDown()
        {
            return IsVirtualKeyDown(VK_LWIN) || IsVirtualKeyDown(VK_RWIN);
        }

        private static bool IsVirtualKeyDown(uint virtualKey)
        {
            short state = GetAsyncKeyState((int)virtualKey);
            return (state & 0x8000) != 0;
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

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);
    }
}
