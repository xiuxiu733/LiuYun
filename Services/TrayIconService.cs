using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LiuYun.Services
{
    public sealed partial class TrayIconService : IDisposable
    {
        private const int WM_APP = 0x8000;
        private const int WM_TRAYMESSAGE = WM_APP + 1;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_CONTEXTMENU = 0x007B;
        private const int WM_NULL = 0x0000;
        private const int WM_DESTROY = 0x0002;

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_DELETE = 0x00000002;
        private const uint NIM_SETVERSION = 0x00000004;
        private const uint NOTIFYICON_VERSION_4 = 4;
        private const uint TPM_BOTTOMALIGN = 0x00000020;
        private const uint TPM_RIGHTALIGN = 0x00000008;
        private const uint TPM_RIGHTBUTTON = 0x00000002;
        private const uint TPM_RETURNCMD = 0x00000100;
        private const uint MF_CHECKED = 0x00000008;

        private const int CommandExit = 1002;
        private const int CommandToggleStartup = 1003;
        private const int CommandClearHistory = 1004;
        private const int CommandOpenSettings = 1005;

        private readonly IntPtr _windowHandle;
        private readonly ushort _iconId;
        private readonly SUBCLASSPROC _subclassDelegate;

        private bool _iconAdded;
        private readonly bool _ownsIconHandle;
        private IntPtr _iconHandle;
        private IntPtr _menuHandle;
        private bool _disposed;
        private bool _startupChecked;

        public event EventHandler? ShowRequested;
        public event EventHandler? ExitRequested;
        public event EventHandler? ToggleStartupRequested;
        public event EventHandler? ClearHistoryRequested;
        public event EventHandler? OpenSettingsRequested;

        public TrayIconService(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
            _iconId = 1;
            _subclassDelegate = WindowProc;

            if (!SetWindowSubclass(windowHandle, _subclassDelegate, _iconId, IntPtr.Zero))
            {
                throw new InvalidOperationException("Failed to subclass window for tray icon.");
            }

            (_iconHandle, _ownsIconHandle) = LoadApplicationIcon();
            AddTrayIcon();
        }

        private void AddTrayIcon()
        {
            NOTIFYICONDATA data = CreateNotifyIconData();
            _iconAdded = Shell_NotifyIcon(NIM_ADD, ref data);

            if (_iconAdded)
            {
                data.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIcon(NIM_SETVERSION, ref data);
            }
        }

        private NOTIFYICONDATA CreateNotifyIconData()
        {
            NOTIFYICONDATA data = new()
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = _iconId,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYMESSAGE,
                hIcon = _iconHandle,
                szTip = "LiuYun",
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            return data;
        }

        private IntPtr WindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            switch (uMsg)
            {
                case WM_TRAYMESSAGE:
                    HandleTrayMessage(lParam);
                    break;
                case WM_DESTROY:
                    RemoveTrayIcon();
                    break;
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void HandleTrayMessage(IntPtr lParam)
        {
            int raw = (int)lParam;
            int message = raw & 0xFFFF;
            switch (message)
            {
                case WM_LBUTTONUP:
                case WM_LBUTTONDBLCLK:
                    ShowRequested?.Invoke(this, EventArgs.Empty);
                    break;
                case WM_RBUTTONUP:
                case WM_CONTEXTMENU:
                    ShowContextMenu();
                    break;
            }
        }

        private void ShowContextMenu()
        {
            // Recreate menu every time so dynamic checked state is reflected.
            if (_menuHandle != IntPtr.Zero)
            {
                DestroyMenu(_menuHandle);
                _menuHandle = IntPtr.Zero;
            }

            _menuHandle = CreatePopupMenu();
            uint startupFlags = _startupChecked ? MF_CHECKED : 0u;
            AppendMenu(_menuHandle, startupFlags, CommandToggleStartup, "开机自启动");
            AppendMenu(_menuHandle, 0, CommandClearHistory, "清空历史记录");
            AppendMenu(_menuHandle, 0, CommandOpenSettings, "设置");
            AppendMenu(_menuHandle, 0, CommandExit, "退出");

            GetCursorPos(out POINT cursorPos);

            IntPtr popupOwner = _windowHandle;
            SetForegroundWindow(popupOwner);
            int commandId = TrackPopupMenu(
                _menuHandle,
                TPM_BOTTOMALIGN | TPM_RIGHTALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON,
                cursorPos.X,
                cursorPos.Y,
                0,
                popupOwner,
                IntPtr.Zero);

            _ = PostMessage(popupOwner, (uint)WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (commandId == CommandExit)
            {
                ExitRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (commandId == CommandToggleStartup)
            {
                ToggleStartupRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (commandId == CommandClearHistory)
            {
                ClearHistoryRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (commandId == CommandOpenSettings)
            {
                OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
            }

            // Destroy the menu after use to ensure next open reflects current state
            if (_menuHandle != IntPtr.Zero)
            {
                DestroyMenu(_menuHandle);
                _menuHandle = IntPtr.Zero;
            }
        }

        public void SetStartupChecked(bool isChecked)
        {
            _startupChecked = isChecked;
        }

        public void ShowInfoTip(string title, string message)
        {
            try
            {
                NOTIFYICONDATA data = CreateNotifyIconData();
                // Ensure the info flag is set so balloon is displayed
                data.uFlags |= NIF_INFO;
                data.szInfoTitle = title ?? string.Empty;
                data.szInfo = message ?? string.Empty;
                data.dwInfoFlags = 0; // NIIF_NONE

                // Modify the existing icon entry to display the balloon tip
                Shell_NotifyIcon(NIM_MODIFY, ref data);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to show info tip: {ex}");
            }
        }

        private static (IntPtr Handle, bool OwnsHandle) LoadApplicationIcon()
        {
            try
            {
                if (!string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    IntPtr[] smallIcons = new IntPtr[1];
                    if (ExtractIconEx(Environment.ProcessPath, 0, null, smallIcons, 1) > 0 && smallIcons[0] != IntPtr.Zero)
                    {
                        return (smallIcons[0], true);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractIconEx failed: {ex}");
            }

            return (LoadIcon(IntPtr.Zero, new IntPtr(32512)), false);
        }

        private void RemoveTrayIcon()
        {
            if (_iconAdded)
            {
                NOTIFYICONDATA data = CreateNotifyIconData();
                Shell_NotifyIcon(NIM_DELETE, ref data);
                _iconAdded = false;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            RemoveTrayIcon();

            if (_ownsIconHandle && _iconHandle != IntPtr.Zero)
            {
                DestroyIcon(_iconHandle);
                _iconHandle = IntPtr.Zero;
            }

            if (_menuHandle != IntPtr.Zero)
            {
                DestroyMenu(_menuHandle);
                _menuHandle = IntPtr.Zero;
            }

            RemoveWindowSubclass(_windowHandle, _subclassDelegate, _iconId);

            _disposed = true;
            GC.SuppressFinalize(this);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [LibraryImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        [LibraryImport("comctl32.dll")]
        private static partial IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, int uIDNewItem, string lpNewItem);

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
    }
}
