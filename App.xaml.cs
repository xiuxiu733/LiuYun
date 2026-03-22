using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel;
using Windows.Graphics;
using Microsoft.UI.Xaml.Hosting;
using WinRT.Interop;
using LiuYun.Services;

namespace LiuYun
{
    public partial class App : Application
    {
        private HotKeyService? _hotKeyService;
        private DoubleTapHotKeyService? _doubleTapHotKeyService;
        private TrayIconService? _trayIconService;
        private ClipboardMonitorService? _clipboardMonitorService;
        private BackgroundTaskQueue? _backgroundTaskQueue;
        public MainWindow? m_window;
        public static LiuYun.Models.AllClipboardItems ClipboardModel { get; private set; } = new LiuYun.Models.AllClipboardItems();
        public event EventHandler? ClipboardDataReloading;
        public event EventHandler? ClipboardDataReplaced;
        private bool _isWindowHidden;
        private Visual? _rootVisual;
        private Compositor? _compositor;
        private bool _hasShownHotKeyFailure;
        private readonly TimeSpan _windowAnimationDuration = TimeSpan.FromMilliseconds(140);
        private readonly TimeSpan _windowAnimationTimeout = TimeSpan.FromMilliseconds(480);
        private readonly TimeSpan _pasteActivationDelay = TimeSpan.FromMilliseconds(90);
        private readonly TimeSpan _activeWindowWaitTimeout = TimeSpan.FromMilliseconds(320);
        private readonly TimeSpan _activeWindowPollInterval = TimeSpan.FromMilliseconds(16);
        private readonly TimeSpan _activeWindowRetryDelay = TimeSpan.FromMilliseconds(36);
        private const int ActiveWindowActivationRetries = 4;
        private const float WindowAnimationOffset = 12f;
        private const int ClipboardPreloadLimit = 300;
        public const int CursorPlacementOffsetPx = 8;
        private const string StartupLaunchArgument = "--startup";
        private const string SingleInstanceMutexName = @"Local\LiuYun.SingleInstance.Mutex";
        private const string SingleInstanceActivationEventName = @"Local\LiuYun.SingleInstance.Activate";
        private static readonly HashSet<string> LikelyTextInputProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "java",
            "javaw",
            "windowsterminal",
            "openconsole",
            "cmd",
            "powershell",
            "pwsh"
        };
        private IntPtr _capturedInvocationWindow = IntPtr.Zero;
        private IntPtr _capturedInvocationFocusWindow = IntPtr.Zero;
        private bool _isStartupLaunch;
        private readonly object _memoryTrimSync = new();
        private CancellationTokenSource? _memoryTrimCts;
        private readonly SemaphoreSlim _windowAnimationGate = new(1, 1);
        private long _lastBackgroundTrimRequestTicks;
        private static readonly TimeSpan BackgroundTrimMinInterval = TimeSpan.FromSeconds(8);
        private int _clipboardHistoryDirtyWhileHidden;
        private int _isExiting;
        private int _autoHideSuppressionCount;
        private static Mutex? _singleInstanceMutex;
        private bool _ownsSingleInstanceMutex;
        private EventWaitHandle? _singleInstanceActivateEvent;
        private CancellationTokenSource? _singleInstanceActivateListenerCts;
        private Task? _singleInstanceActivateListenerTask;
        public AutoPasteFallbackReason LastAutoPasteFallbackReason { get; private set; } = AutoPasteFallbackReason.None;
        public bool AutoHideOnDeactivate { get; private set; } = true;
        public bool IsMainWindowVisible => !_isWindowHidden;

        public enum AutoPasteFallbackReason
        {
            None = 0,
            NoCapturedTarget = 1,
            InvalidTarget = 2,
            UacBlocked = 3,
            ActivationFailed = 4,
            NoInputFocus = 5,
            SendInputFailed = 6
        }

        public App()
        {
            this.InitializeComponent();
            RegisterGlobalExceptionHandlers();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            _isStartupLaunch = HasLaunchArgument(args.Arguments, StartupLaunchArgument);
            if (!TryAcquireSingleInstanceLock())
            {
                if (!_isStartupLaunch)
                {
                    SignalRunningInstanceToActivate();
                }

                Exit();
                return;
            }

            EnsureSingleInstanceActivationEvent();
            StoragePathService.ProcessPendingMigrationIfAny();
            DatabaseService.Initialize();
            UpdateBannerService.EnsureStartupCheckStarted(UpdateBannerService.GetCurrentVersionString());

            m_window = new MainWindow();
            m_window.Closed += (_, __) =>
            {
                DisposeTrayIcon();
                Exit();
            };
            m_window.Activate();
            if (_isStartupLaunch)
            {
                HideWindowForStartupLaunch();
            }
            InitializeTrayIcon();
            StartSingleInstanceActivationListener();

            _backgroundTaskQueue = new BackgroundTaskQueue(capacity: 1024, workerCount: 3);

            ClipboardModel = new LiuYun.Models.AllClipboardItems(_backgroundTaskQueue, m_window.DispatcherQueue);

            FireAndForget(InitializeAfterWindowReadyAsync(), nameof(InitializeAfterWindowReadyAsync));
        }

        private bool TryAcquireSingleInstanceLock()
        {
            if (_ownsSingleInstanceMutex && _singleInstanceMutex != null)
            {
                return true;
            }

            try
            {
                Mutex mutex = new(initiallyOwned: true, name: SingleInstanceMutexName, createdNew: out bool createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    return false;
                }

                _singleInstanceMutex = mutex;
                _ownsSingleInstanceMutex = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Single-instance mutex initialization failed: {ex}");
                return true;
            }
        }

        private void EnsureSingleInstanceActivationEvent()
        {
            if (_singleInstanceActivateEvent != null)
            {
                return;
            }

            try
            {
                _singleInstanceActivateEvent = new EventWaitHandle(
                    initialState: false,
                    mode: EventResetMode.AutoReset,
                    name: SingleInstanceActivationEventName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Single-instance activation event initialization failed: {ex}");
            }
        }

        private void StartSingleInstanceActivationListener()
        {
            if (!_ownsSingleInstanceMutex || _singleInstanceActivateEvent is null || _singleInstanceActivateListenerTask != null)
            {
                return;
            }

            _singleInstanceActivateListenerCts = new CancellationTokenSource();
            CancellationToken cancellationToken = _singleInstanceActivateListenerCts.Token;
            EventWaitHandle activationEvent = _singleInstanceActivateEvent;
            _singleInstanceActivateListenerTask = Task.Run(
                () => SingleInstanceActivationListenerLoop(activationEvent, cancellationToken),
                cancellationToken);
        }

        private void SingleInstanceActivationListenerLoop(EventWaitHandle activationEvent, CancellationToken cancellationToken)
        {
            WaitHandle[] waitHandles = { activationEvent, cancellationToken.WaitHandle };

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int signaledIndex = WaitHandle.WaitAny(waitHandles);
                    if (signaledIndex != 0 || cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    DispatcherQueue? dispatcherQueue = m_window?.DispatcherQueue;
                    if (dispatcherQueue == null)
                    {
                        continue;
                    }

                    dispatcherQueue.TryEnqueue(() =>
                    {
                        ShowMainWindow();
                        FireAndForget(ForceActivateMainWindowAsync(), nameof(ForceActivateMainWindowAsync));
                    });
                }
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Single-instance activation listener failed: {ex}");
            }
        }

        private void StopSingleInstanceActivationListener()
        {
            try
            {
                _singleInstanceActivateListenerCts?.Cancel();
            }
            catch
            {
            }

            try
            {
                _singleInstanceActivateEvent?.Set();
            }
            catch
            {
            }

            try
            {
                _singleInstanceActivateListenerTask?.Wait(300);
            }
            catch
            {
            }

            _singleInstanceActivateListenerTask = null;
            _singleInstanceActivateListenerCts?.Dispose();
            _singleInstanceActivateListenerCts = null;

            _singleInstanceActivateEvent?.Dispose();
            _singleInstanceActivateEvent = null;
        }

        private void ReleaseSingleInstanceLock()
        {
            if (_singleInstanceMutex is null)
            {
                return;
            }

            if (_ownsSingleInstanceMutex)
            {
                try
                {
                    _singleInstanceMutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }

                _ownsSingleInstanceMutex = false;
            }

            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        private void SignalRunningInstanceToActivate()
        {
            if (TrySignalRunningInstanceByEvent())
            {
                return;
            }

            TryBringExistingInstanceWindowToForeground();
        }

        private bool TrySignalRunningInstanceByEvent()
        {
            try
            {
                using EventWaitHandle activationEvent = EventWaitHandle.OpenExisting(SingleInstanceActivationEventName);
                return activationEvent.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to signal running instance: {ex}");
                return false;
            }
        }

        private static void TryBringExistingInstanceWindowToForeground()
        {
            try
            {
                using Process current = Process.GetCurrentProcess();
                Process[] processes = Process.GetProcessesByName(current.ProcessName);
                foreach (Process process in processes)
                {
                    using (process)
                    {
                        if (process.Id == current.Id)
                        {
                            continue;
                        }

                        process.Refresh();
                        IntPtr targetWindow = process.MainWindowHandle;
                        if (targetWindow == IntPtr.Zero || !IsWindow(targetWindow))
                        {
                            continue;
                        }

                        ShowWindow(targetWindow, IsIconic(targetWindow) ? SW_RESTORE : SW_SHOW);
                        SetForegroundWindow(targetWindow);
                        SetWindowPos(targetWindow, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                        SetWindowPos(targetWindow, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to bring existing instance to foreground: {ex}");
            }
        }

        private async Task InitializeAfterWindowReadyAsync()
        {
            await Task.Delay(300);

            HotkeyConfigService.EnsureInitialized();
            ApplyConfiguredGlobalHotKey();

            try
            {
                var items = await Task.Run(() => DatabaseService.GetRecentClipboardItems(ClipboardPreloadLimit));

                var existingIds = new System.Collections.Generic.HashSet<int>();
                foreach (var existingItem in ClipboardModel.Items)
                {
                    existingIds.Add(existingItem.Id);
                }

                foreach (var item in items)
                {
                    if (!existingIds.Contains(item.Id))
                    {
                        ClipboardModel.Items.Add(item);
                    }
                }

                ClipboardModel.IsLoaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error preloading clipboard: {ex.Message}");
                ClipboardModel.IsLoaded = true;
            }

            InitializeClipboardMonitor();

            CacheRootVisual();

            if (_isStartupLaunch)
            {
                return;
            }

            ShowMainWindow();
            await ForceActivateMainWindowAsync();
        }

        private void HideWindowForStartupLaunch()
        {
            if (m_window is null)
            {
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            ShowWindow(hwnd, SW_HIDE);
            _isWindowHidden = true;
            NotifyMainWindowVisibilityChanged(false);
        }

        private static bool HasLaunchArgument(string? rawArguments, string expectedArgument)
        {
            if (string.IsNullOrWhiteSpace(rawArguments) || string.IsNullOrWhiteSpace(expectedArgument))
            {
                return false;
            }

            string[] tokens = rawArguments.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string token in tokens)
            {
                if (string.Equals(token.Trim().Trim('"'), expectedArgument, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private async Task ForceActivateMainWindowAsync()
        {
            if (m_window is null)
            {
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);

            for (int i = 0; i < 2; i++)
            {
                await Task.Delay(i == 0 ? 60 : 140);
                ShowWindow(hwnd, SW_SHOW);
                SetForegroundWindow(hwnd);
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                m_window.DispatcherQueue.TryEnqueue(() => m_window.Activate());
            }
        }

        public static Window? MainWindow => ((App)Current).m_window;

        private void InitializeTrayIcon()
        {
            if (m_window is null)
            {
                return;
            }

            DisposeTrayIcon();

            try
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
                TrayIconService service = new(hwnd);
                service.ShowRequested += (_, __) =>
                {
                    ShowMainWindow();
                    FireAndForget(ForceActivateMainWindowAsync(), nameof(ForceActivateMainWindowAsync));
                };
                service.ExitRequested += (_, __) =>
                {
                    ExitApplication();
                };

                _trayIconService = service;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tray icon initialization failed: {ex}");
            }
        }

        private void DisposeTrayIcon()
        {
            _trayIconService?.Dispose();
            _trayIconService = null;
        }


        private void ApplyConfiguredGlobalHotKey()
        {
            HotkeyTrigger trigger = HotkeyConfigService.GetCurrentTrigger();
            switch (trigger.Type)
            {
                case HotkeyTriggerType.SystemWinV:
                    if (ClipboardRegistryService.GetPreferSystemWinV() || !ClipboardRegistryService.IsWinVDisabled())
                    {
                        ReleaseGlobalHotKey();
                        return;
                    }

                    InitializeHotKey("Win", "V");
                    return;
                case HotkeyTriggerType.KeyChord:
                    InitializeHotKey(trigger.Modifier, trigger.Key);
                    return;
                case HotkeyTriggerType.DoubleTap:
                    InitializeDoubleTapHotKey(trigger.DoubleTapKey, trigger.DoubleTapIntervalMs);
                    return;
                default:
                    ReleaseGlobalHotKey();
                    return;
            }
        }

        private void InitializeHotKey(string modifier, string key)
        {
            if (m_window is null)
            {
                return;
            }

            ReleaseGlobalHotKey();

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            try
            {
                HotKeyService service = new(hwnd, modifier, key);
                if (service.IsActive)
                {
                    _hotKeyService = service;
                    _hotKeyService.HotKeyPressed += (_, __) => ToggleWindowVisibility();
                }
                else
                {
                    string hotkeyLabel = HotkeyConfigService.FormatTriggerForDisplay(new HotkeyTrigger
                    {
                        Type = HotkeyTriggerType.KeyChord,
                        Modifier = modifier,
                        Key = key
                    });
                    string message = BuildHotKeyErrorMessage(hotkeyLabel, service.LastErrorMessage, service.LastErrorCode);
                    service.Dispose();
                    FireAndForget(ShowHotKeyErrorAsync(message), nameof(ShowHotKeyErrorAsync));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotKey initialization failed: {ex}");
                string hotkeyLabel = HotkeyConfigService.FormatTriggerForDisplay(new HotkeyTrigger
                {
                    Type = HotkeyTriggerType.KeyChord,
                    Modifier = modifier,
                    Key = key
                });
                FireAndForget(ShowHotKeyErrorAsync(BuildHotKeyErrorMessage(hotkeyLabel, ex.Message, 0)), nameof(ShowHotKeyErrorAsync));
            }
        }

        private void InitializeDoubleTapHotKey(string tapKey, int tapIntervalMs)
        {
            ReleaseGlobalHotKey();

            try
            {
                DoubleTapHotKeyService service = new(tapKey, tapIntervalMs);
                if (service.IsActive)
                {
                    _doubleTapHotKeyService = service;
                    _doubleTapHotKeyService.HotKeyPressed += (_, __) => ToggleWindowVisibility();
                }
                else
                {
                    string label = HotkeyConfigService.FormatTriggerForDisplay(new HotkeyTrigger
                    {
                        Type = HotkeyTriggerType.DoubleTap,
                        DoubleTapKey = tapKey,
                        DoubleTapIntervalMs = tapIntervalMs
                    });
                    string message = BuildHotKeyErrorMessage(label, service.LastErrorMessage, service.LastErrorCode);
                    service.Dispose();
                    FireAndForget(ShowHotKeyErrorAsync(message), nameof(ShowHotKeyErrorAsync));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DoubleTap hotkey initialization failed: {ex}");
                string label = HotkeyConfigService.FormatTriggerForDisplay(new HotkeyTrigger
                {
                    Type = HotkeyTriggerType.DoubleTap,
                    DoubleTapKey = tapKey,
                    DoubleTapIntervalMs = tapIntervalMs
                });
                FireAndForget(ShowHotKeyErrorAsync(BuildHotKeyErrorMessage(label, ex.Message, 0)), nameof(ShowHotKeyErrorAsync));
            }
        }

        private void ShowMainWindow()
        {
            if (m_window is null)
            {
                return;
            }

            if (_isWindowHidden)
            {
                FireAndForget(ShowFromHiddenAsync(), nameof(ShowFromHiddenAsync));
                return;
            }

            EnsureWindowOnTop();
            m_window.DispatcherQueue.TryEnqueue(() => m_window.Activate());
        }

        private void NotifyMainWindowVisibilityChanged(bool isVisible)
        {
            if (m_window is MainWindow mainWindow)
            {
                mainWindow.DispatcherQueue.TryEnqueue(() =>
                    mainWindow.NotifyWindowVisibilityChanged(isVisible));
            }
        }

        private void ToggleWindowVisibility()
        {
            if (m_window is null)
            {
                return;
            }

            if (Volatile.Read(ref _autoHideSuppressionCount) == 0)
            {
                AutoHideOnDeactivate = true;
            }

            if (_isWindowHidden)
            {
                FireAndForget(ShowFromHiddenAsync(), nameof(ShowFromHiddenAsync));
            }
            else
            {
                FireAndForget(AnimateWindowAsync(false), nameof(AnimateWindowAsync));
            }
        }

        public void BeginAutoHideSuppression()
        {
            if (Interlocked.Increment(ref _autoHideSuppressionCount) == 1)
            {
                AutoHideOnDeactivate = false;
            }
        }

        public void EndAutoHideSuppression()
        {
            int remaining = Interlocked.Decrement(ref _autoHideSuppressionCount);
            if (remaining <= 0)
            {
                Interlocked.Exchange(ref _autoHideSuppressionCount, 0);
                AutoHideOnDeactivate = true;
            }
        }

        private async Task ShowFromHiddenAsync()
        {
            if (m_window is null)
            {
                return;
            }

            PositionWindowForPopupShow();
            await PrepareClipboardHistoryLandingAsync();
            CaptureInvocationWindow();
            await AnimateWindowAsync(true);
            await EnsureVisibleWindowVisualStateAsync();

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            if (GetForegroundWindow() != hwnd)
            {
                await ForceActivateMainWindowAsync();
                await EnsureVisibleWindowVisualStateAsync();
            }
        }

        private async Task EnsureVisibleWindowVisualStateAsync()
        {
            if (m_window is null)
            {
                return;
            }

            await RunOnWindowDispatcherAsync(async () =>
            {
                CacheRootVisual();
                for (int i = 0; i < 3 && (_rootVisual is null || _compositor is null); i++)
                {
                    await Task.Delay(16);
                    CacheRootVisual();
                }

                if (_rootVisual is null)
                {
                    return;
                }

                _rootVisual.StopAnimation(nameof(_rootVisual.Opacity));
                _rootVisual.StopAnimation("Offset.Y");
                var offset = _rootVisual.Offset;
                _rootVisual.Opacity = 1f;
                _rootVisual.Offset = new System.Numerics.Vector3(offset.X, 0f, offset.Z);
            });
        }

        private async Task PrepareClipboardHistoryLandingAsync()
        {
            if (m_window is not MainWindow mainWindow)
            {
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = mainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    mainWindow.PrepareClipboardHistoryForHotkeyShow();
                }
                finally
                {
                    completion.TrySetResult(true);
                }
            });

            if (!enqueued)
            {
                return;
            }

            await Task.WhenAny(completion.Task, Task.Delay(1000));
        }

        private void PositionWindowForPopupShow()
        {
            PopupPlacementMode mode = PopupPlacementConfigService.GetMode();
            if (mode == PopupPlacementMode.FreeDrag && PositionWindowFromSavedLocation())
            {
                return;
            }

            PositionWindowNearCursor();
        }

        private bool PositionWindowFromSavedLocation()
        {
            if (m_window is null || !PopupPlacementConfigService.TryGetFreeDragPosition(out int savedX, out int savedY))
            {
                return false;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow == null)
            {
                return false;
            }

            PointInt32 clamped = ClampWindowPositionToWorkArea(appWindow, savedX, savedY);
            appWindow.Move(clamped);

            if (clamped.X != savedX || clamped.Y != savedY)
            {
                PopupPlacementConfigService.SetFreeDragPosition(clamped.X, clamped.Y);
            }

            return true;
        }

        private static PointInt32 ClampWindowPositionToWorkArea(AppWindow appWindow, int desiredX, int desiredY)
        {
            DisplayArea? displayArea = DisplayArea.GetFromPoint(
                new PointInt32(desiredX, desiredY),
                DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);

            int windowWidth = appWindow.Size.Width > 0 ? appWindow.Size.Width : 520;
            int windowHeight = appWindow.Size.Height > 0 ? appWindow.Size.Height : 520;

            int minX = workArea.X;
            int maxX = workArea.X + workArea.Width - windowWidth;
            if (maxX < minX)
            {
                maxX = minX;
            }

            int minY = workArea.Y;
            int maxY = workArea.Y + workArea.Height - windowHeight;
            if (maxY < minY)
            {
                maxY = minY;
            }

            int clampedX = Math.Clamp(desiredX, minX, maxX);
            int clampedY = Math.Clamp(desiredY, minY, maxY);
            return new PointInt32(clampedX, clampedY);
        }

        private void PositionWindowNearCursor()
        {
            if (m_window is null)
            {
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow == null)
            {
                return;
            }

            if (!GetCursorPos(out POINT cursorPoint))
            {
                return;
            }

            DisplayArea? displayArea = DisplayArea.GetFromPoint(
                new PointInt32(cursorPoint.X, cursorPoint.Y),
                DisplayAreaFallback.Primary);
            RectInt32 workArea = displayArea?.WorkArea ?? new RectInt32(0, 0, 1920, 1080);

            int windowWidth = appWindow.Size.Width > 0 ? appWindow.Size.Width : 520;
            int windowHeight = appWindow.Size.Height > 0 ? appWindow.Size.Height : 520;

            int minX = workArea.X;
            int maxX = workArea.X + workArea.Width - windowWidth;
            if (maxX < minX)
            {
                maxX = minX;
            }

            int rightX = cursorPoint.X + CursorPlacementOffsetPx;
            int leftX = cursorPoint.X - CursorPlacementOffsetPx - windowWidth;
            bool canPlaceRight = rightX <= maxX;
            bool canPlaceLeft = leftX >= minX;

            int targetX;
            if (canPlaceRight)
            {
                targetX = rightX;
            }
            else if (canPlaceLeft)
            {
                targetX = leftX;
            }
            else
            {
                targetX = rightX;
            }

            int yAbove = cursorPoint.Y - CursorPlacementOffsetPx - windowHeight;
            int yBelow = cursorPoint.Y + CursorPlacementOffsetPx;
            int targetY = yAbove >= workArea.Y ? yAbove : yBelow;

            int minY = workArea.Y;
            int maxY = workArea.Y + workArea.Height - windowHeight;
            if (maxY < minY)
            {
                maxY = minY;
            }

            targetX = Math.Clamp(targetX, minX, maxX);
            targetY = Math.Clamp(targetY, minY, maxY);

            appWindow.Move(new PointInt32(targetX, targetY));
        }

        private void CaptureInvocationWindow()
        {
            _capturedInvocationWindow = IntPtr.Zero;
            _capturedInvocationFocusWindow = IntPtr.Zero;

            if (m_window is null)
            {
                return;
            }

            IntPtr foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || !IsWindow(foreground))
            {
                return;
            }

            IntPtr self = WindowNative.GetWindowHandle(m_window);
            if (foreground == self || IsCurrentProcessWindow(foreground) || IsShellOrDesktopWindow(foreground))
            {
                return;
            }

            IntPtr focus = GetFocusedWindowForActive(foreground);
            if (focus != IntPtr.Zero && IsWindow(focus))
            {
                _capturedInvocationFocusWindow = focus;
            }

            _capturedInvocationWindow = foreground;
        }

        public async Task<bool> TryAutoPasteToCapturedTargetAsync()
        {
            LastAutoPasteFallbackReason = AutoPasteFallbackReason.None;

            IntPtr target = _capturedInvocationWindow;
            IntPtr focus = _capturedInvocationFocusWindow;

            if (target == IntPtr.Zero)
            {
                LastAutoPasteFallbackReason = AutoPasteFallbackReason.NoCapturedTarget;
                return false;
            }

            if (!IsWindow(target) || IsShellOrDesktopWindow(target) || IsCurrentProcessWindow(target))
            {
                LastAutoPasteFallbackReason = AutoPasteFallbackReason.InvalidTarget;
                _capturedInvocationWindow = IntPtr.Zero;
                _capturedInvocationFocusWindow = IntPtr.Zero;
                return false;
            }

            if (IsPasteBlockedByElevation(target))
            {
                LastAutoPasteFallbackReason = AutoPasteFallbackReason.UacBlocked;
                return false;
            }

            if (!await TryActivateTargetWindowAsync(target))
            {
                LastAutoPasteFallbackReason = AutoPasteFallbackReason.ActivationFailed;
                return false;
            }

            await Task.Delay(_pasteActivationDelay);

            bool hasLikelyInputFocus = HasLikelyTextInputFocus(target, focus);
            if (!hasLikelyInputFocus && GetForegroundWindow() != target)
            {
                if (!await TryActivateTargetWindowAsync(target))
                {
                    LastAutoPasteFallbackReason = AutoPasteFallbackReason.NoInputFocus;
                    return false;
                }

                await Task.Delay(_pasteActivationDelay);
                hasLikelyInputFocus = HasLikelyTextInputFocus(target, focus);
                if (!hasLikelyInputFocus && GetForegroundWindow() != target)
                {
                    LastAutoPasteFallbackReason = AutoPasteFallbackReason.NoInputFocus;
                    return false;
                }
            }

            ReleaseModifierKeys();
            bool sendInputSuccess = SendCtrlV();
            if (!sendInputSuccess)
            {
                LastAutoPasteFallbackReason = AutoPasteFallbackReason.SendInputFailed;
                return false;
            }

            _capturedInvocationWindow = IntPtr.Zero;
            _capturedInvocationFocusWindow = IntPtr.Zero;
            return true;
        }

        private async Task<bool> TryActivateTargetWindowAsync(IntPtr targetWindow)
        {
            for (int attempt = 0; attempt < ActiveWindowActivationRetries; attempt++)
            {
                if (TryBringWindowToForeground(targetWindow))
                {
                    bool activated = await WaitForActiveWindowAsync(targetWindow, _activeWindowWaitTimeout);
                    if (activated)
                    {
                        return true;
                    }
                }

                await Task.Delay(_activeWindowRetryDelay);
            }

            return false;
        }

        private async Task<bool> WaitForActiveWindowAsync(IntPtr targetWindow, TimeSpan timeout)
        {
            long timeoutMs = Math.Max(1, (long)timeout.TotalMilliseconds);
            long startTick = Environment.TickCount64;
            while (Environment.TickCount64 - startTick <= timeoutMs)
            {
                if (GetForegroundWindow() == targetWindow)
                {
                    return true;
                }

                await Task.Delay(_activeWindowPollInterval);
            }

            return false;
        }

        private bool TryBringWindowToForeground(IntPtr targetWindow)
        {
            if (!IsWindow(targetWindow))
            {
                return false;
            }

            if (IsIconic(targetWindow))
            {
                ShowWindow(targetWindow, SW_RESTORE);
            }

            _ = AllowSetForegroundWindow(ASFW_ANY);

            IntPtr foreground = GetForegroundWindow();
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = foreground != IntPtr.Zero
                ? GetWindowThreadProcessId(foreground, out _)
                : 0;

            bool attached = false;
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attached = AttachThreadInput(foregroundThread, currentThread, true);
            }

            try
            {
                BringWindowToTop(targetWindow);
                bool activated = SetForegroundWindow(targetWindow);
                SetWindowPos(targetWindow, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                SetWindowPos(targetWindow, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
                bool foregroundMatches = GetForegroundWindow() == targetWindow;
                return activated || foregroundMatches;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(foregroundThread, currentThread, false);
                }
            }
        }

        private static IntPtr GetFocusedWindowForActive(IntPtr activeWindow)
        {
            if (activeWindow == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            uint threadId = GetWindowThreadProcessId(activeWindow, out _);
            if (threadId == 0)
            {
                return IntPtr.Zero;
            }

            GUITHREADINFO guiThreadInfo = new()
            {
                cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>()
            };
            if (!GetGUIThreadInfo(threadId, ref guiThreadInfo))
            {
                return IntPtr.Zero;
            }

            if (guiThreadInfo.hwndFocus != IntPtr.Zero)
            {
                return guiThreadInfo.hwndFocus;
            }

            if (guiThreadInfo.hwndActive != IntPtr.Zero)
            {
                return guiThreadInfo.hwndActive;
            }

            return IntPtr.Zero;
        }

        private static bool HasLikelyTextInputFocus(IntPtr targetWindow, IntPtr capturedFocusWindow)
        {
            uint threadId = GetWindowThreadProcessId(targetWindow, out _);
            if (threadId != 0)
            {
                GUITHREADINFO guiThreadInfo = new()
                {
                    cbSize = (uint)Marshal.SizeOf<GUITHREADINFO>()
                };

                if (GetGUIThreadInfo(threadId, ref guiThreadInfo))
                {
                    if (guiThreadInfo.hwndCaret != IntPtr.Zero)
                    {
                        return true;
                    }

                    IntPtr focusedWindow = guiThreadInfo.hwndFocus != IntPtr.Zero
                        ? guiThreadInfo.hwndFocus
                        : guiThreadInfo.hwndActive;

                    if (IsTextInputCandidateWindow(focusedWindow, targetWindow))
                    {
                        return true;
                    }
                }
            }

            if (IsTextInputCandidateWindow(capturedFocusWindow, targetWindow))
            {
                return true;
            }

            if (TryGetFocusedWindowByAttachThreadInput(targetWindow, out IntPtr attachedFocus, out bool hasCaret))
            {
                if (hasCaret)
                {
                    return true;
                }

                if (IsTextInputCandidateWindow(attachedFocus, targetWindow))
                {
                    return true;
                }
            }

            if (IsLikelyTextInputProcess(targetWindow))
            {
                return true;
            }

            return false;
        }

        private static bool IsLikelyTextInputProcess(IntPtr targetWindow)
        {
            GetWindowThreadProcessId(targetWindow, out uint processId);
            if (processId == 0)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName;
                return LikelyTextInputProcessNames.Contains(processName);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetFocusedWindowByAttachThreadInput(IntPtr targetWindow, out IntPtr focusWindow, out bool hasCaret)
        {
            focusWindow = IntPtr.Zero;
            hasCaret = false;

            uint targetThread = GetWindowThreadProcessId(targetWindow, out _);
            uint currentThread = GetCurrentThreadId();
            if (targetThread == 0)
            {
                return false;
            }

            if (!AttachThreadInput(targetThread, currentThread, true))
            {
                return false;
            }

            try
            {
                focusWindow = GetFocus();
                if (GetCaretPos(out POINT _))
                {
                    hasCaret = true;
                }

                return true;
            }
            finally
            {
                AttachThreadInput(targetThread, currentThread, false);
            }
        }

        private static bool IsTextInputCandidateWindow(IntPtr hWnd, IntPtr targetWindow)
        {
            if (hWnd == IntPtr.Zero || !IsWindow(hWnd) || IsShellOrDesktopWindow(hWnd) || IsCurrentProcessWindow(hWnd))
            {
                return false;
            }

            string className = GetWindowClassName(hWnd);
            if (string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            if (className.Equals("SysListView32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("SysTreeView32", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ToolbarWindow32", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (className.Contains("Edit", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("Scintilla", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("Chrome", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("Qt", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("TextBox", StringComparison.OrdinalIgnoreCase) ||
                className.Contains("WindowsForms10.EDIT", StringComparison.OrdinalIgnoreCase) ||
                className.StartsWith("SunAwt", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("GlassWndClass", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("GLFW30", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("CefBrowserWindow", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) ||
                className.Equals("FLUTTER_RUNNER_WIN32_WINDOW", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            IntPtr root = GetAncestor(hWnd, GA_ROOT);
            if (root != IntPtr.Zero && root == targetWindow)
            {
                return true;
            }

            return false;
        }

        private static bool IsShellOrDesktopWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return true;
            }

            string className = GetWindowClassName(hWnd);
            return string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(className, "TrayNotifyWnd", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCurrentProcessWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            uint windowProcessId;
            GetWindowThreadProcessId(hWnd, out windowProcessId);
            if (windowProcessId == 0)
            {
                return false;
            }

            return windowProcessId == (uint)Environment.ProcessId;
        }

        private static bool IsPasteBlockedByElevation(IntPtr targetWindow)
        {
            if (IsCurrentProcessElevated())
            {
                return false;
            }

            return IsWindowProcessElevated(targetWindow);
        }

        private static bool IsCurrentProcessElevated()
        {
            using Process current = Process.GetCurrentProcess();
            return IsProcessHandleElevated(current.Handle);
        }

        private static bool IsWindowProcessElevated(IntPtr targetWindow)
        {
            GetWindowThreadProcessId(targetWindow, out uint processId);
            if (processId == 0)
            {
                return false;
            }

            IntPtr processHandle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return IsProcessHandleElevated(processHandle);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }

        private static bool IsProcessHandleElevated(IntPtr processHandle)
        {
            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            if (!OpenProcessToken(processHandle, TOKEN_QUERY, out IntPtr tokenHandle))
            {
                return false;
            }

            try
            {
                TOKEN_ELEVATION elevation = new();
                int size = Marshal.SizeOf<TOKEN_ELEVATION>();
                if (!GetTokenInformation(tokenHandle, TOKEN_INFORMATION_CLASS.TokenElevation, out elevation, (uint)size, out _))
                {
                    return false;
                }

                return elevation.TokenIsElevated != 0;
            }
            finally
            {
                CloseHandle(tokenHandle);
            }
        }

        private static void ReleaseModifierKeys()
        {
            int inputSize = Marshal.SizeOf<INPUT>();
            INPUT[] inputs =
            {
                new INPUT { type = InputTypeKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } },
                new INPUT { type = InputTypeKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_SHIFT, dwFlags = KEYEVENTF_KEYUP } } },
                new INPUT { type = InputTypeKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_MENU, dwFlags = KEYEVENTF_KEYUP } } },
                new INPUT { type = InputTypeKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP } } },
                new INPUT { type = InputTypeKeyboard, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_RWIN, dwFlags = KEYEVENTF_KEYUP } } }
            };

            _ = SendInput((uint)inputs.Length, inputs, inputSize);
        }

        private static string GetWindowClassName(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            StringBuilder buffer = new StringBuilder(256);
            int len = GetClassName(hWnd, buffer, buffer.Capacity);
            if (len <= 0)
            {
                return string.Empty;
            }

            return buffer.ToString(0, len);
        }

        private static bool SendCtrlV()
        {
            int inputSize = Marshal.SizeOf<INPUT>();
            INPUT[] inputs =
            {
                new INPUT
                {
                    type = InputTypeKeyboard,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = VK_CONTROL }
                    }
                },
                new INPUT
                {
                    type = InputTypeKeyboard,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = VK_V }
                    }
                },
                new INPUT
                {
                    type = InputTypeKeyboard,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP }
                    }
                },
                new INPUT
                {
                    type = InputTypeKeyboard,
                    U = new InputUnion
                    {
                        ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP }
                    }
                }
            };

            uint sentCount = SendInput((uint)inputs.Length, inputs, inputSize);
            return sentCount == inputs.Length;
        }

        public async Task TriggerWindowHideAsync()
        {
            if (m_window is null || _isWindowHidden)
            {
                return;
            }

            await AnimateWindowAsync(false);
        }

        private async Task RunOnWindowDispatcherAsync(Func<Task> action)
        {
            if (m_window?.DispatcherQueue == null)
            {
                return;
            }

            if (m_window.DispatcherQueue.HasThreadAccess)
            {
                await action();
                return;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = m_window.DispatcherQueue.TryEnqueue(() =>
            {
                _ = action().ContinueWith(
                    t =>
                    {
                        if (t.IsFaulted)
                        {
                            completion.TrySetException(t.Exception?.GetBaseException() ?? new InvalidOperationException("Dispatcher action failed."));
                        }
                        else if (t.IsCanceled)
                        {
                            completion.TrySetCanceled();
                        }
                        else
                        {
                            completion.TrySetResult(true);
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            });

            if (!enqueued)
            {
                return;
            }

            await completion.Task;
        }

        private void CacheRootVisual()
        {
            if (m_window is null)
            {
                return;
            }

            _rootVisual = ElementCompositionPreview.GetElementVisual(m_window.Content as UIElement);
            _compositor = _rootVisual?.Compositor;
        }

        private Task AnimateWindowAsync(bool isShowing)
        {
            return RunOnWindowDispatcherAsync(() => AnimateWindowCoreAsync(isShowing));
        }

        private async Task AnimateWindowCoreAsync(bool isShowing)
        {
            if (m_window is null)
            {
                return;
            }

            await _windowAnimationGate.WaitAsync();
            try
            {
                if (m_window is null)
                {
                    return;
                }

                CacheRootVisual();
                IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
                if (isShowing)
                {
                    ShowWindow(hwnd, SW_SHOW);
                }

                if (_rootVisual is null || _compositor is null)
                {
                    if (!isShowing)
                    {
                        ShowWindow(hwnd, SW_HIDE);
                    }

                    _isWindowHidden = !isShowing;
                    NotifyMainWindowVisibilityChanged(isShowing);
                    return;
                }

                float targetOpacity = isShowing ? 1f : 0f;
                float targetOffset = isShowing ? 0f : WindowAnimationOffset;

                _rootVisual.StopAnimation(nameof(_rootVisual.Opacity));
                _rootVisual.StopAnimation("Offset.Y");

                if (isShowing)
                {
                    _rootVisual.Opacity = 0f;
                    var currentOffset = _rootVisual.Offset;
                    _rootVisual.Offset = new System.Numerics.Vector3(currentOffset.X, WindowAnimationOffset, currentOffset.Z);
                }

                var easing = _compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.4f, 0f), new System.Numerics.Vector2(0.2f, 1f));
                var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                batch.Completed += (_, _) => completionSource.TrySetResult(null);

                var opacityAnimation = _compositor.CreateScalarKeyFrameAnimation();
                opacityAnimation.Duration = _windowAnimationDuration;
                opacityAnimation.InsertKeyFrame(1f, targetOpacity, easing);
                _rootVisual.StartAnimation(nameof(_rootVisual.Opacity), opacityAnimation);

                var offsetAnimation = _compositor.CreateScalarKeyFrameAnimation();
                offsetAnimation.Duration = _windowAnimationDuration;
                offsetAnimation.InsertKeyFrame(1f, targetOffset, easing);
                _rootVisual.StartAnimation("Offset.Y", offsetAnimation);

                batch.End();

                Task completedTask = await Task.WhenAny(completionSource.Task, Task.Delay(_windowAnimationTimeout));
                if (!ReferenceEquals(completedTask, completionSource.Task))
                {
                    Debug.WriteLine("Window animation timeout; force applying final visual state.");
                }

                _rootVisual.StopAnimation(nameof(_rootVisual.Opacity));
                _rootVisual.StopAnimation("Offset.Y");
                var finalOffset = _rootVisual.Offset;
                _rootVisual.Opacity = targetOpacity;
                _rootVisual.Offset = new System.Numerics.Vector3(finalOffset.X, targetOffset, finalOffset.Z);

                if (!isShowing)
                {
                    ShowWindow(hwnd, SW_HIDE);
                }

                _isWindowHidden = !isShowing;
                NotifyMainWindowVisibilityChanged(isShowing);
                if (!isShowing)
                {
                    ScheduleBackgroundMemoryTrim();
                }

                if (isShowing)
                {
                    EnsureWindowOnTop();
                    m_window.DispatcherQueue.TryEnqueue(() => m_window.Activate());
                }
            }
            finally
            {
                _windowAnimationGate.Release();
            }
        }

        private void ScheduleBackgroundMemoryTrim()
        {
            Interlocked.Exchange(ref _lastBackgroundTrimRequestTicks, DateTime.UtcNow.Ticks);

            CancellationTokenSource trimCts;
            lock (_memoryTrimSync)
            {
                _memoryTrimCts?.Cancel();
                _memoryTrimCts?.Dispose();
                _memoryTrimCts = new CancellationTokenSource();
                trimCts = _memoryTrimCts;
            }

            FireAndForget(RunBackgroundMemoryTrimAsync(trimCts.Token), nameof(RunBackgroundMemoryTrimAsync));
        }

        public void RequestBackgroundMemoryTrimIfHidden()
        {
            if (!_isWindowHidden)
            {
                return;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            long lastTicks = Interlocked.Read(ref _lastBackgroundTrimRequestTicks);
            if (lastTicks > 0 && (nowTicks - lastTicks) < BackgroundTrimMinInterval.Ticks)
            {
                return;
            }

            ScheduleBackgroundMemoryTrim();
        }

        public void NotifyClipboardHistoryChangedInBackground()
        {
            if (_isWindowHidden)
            {
                Interlocked.Exchange(ref _clipboardHistoryDirtyWhileHidden, 1);
            }
        }

        public bool ConsumeClipboardHistoryDirtyFlag()
        {
            return Interlocked.Exchange(ref _clipboardHistoryDirtyWhileHidden, 0) == 1;
        }

        private async Task RunBackgroundMemoryTrimAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(180, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                await TrimGlobalVisualCachesAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                ForceTrimProcessWorkingSet();

                cancellationToken.ThrowIfCancellationRequested();

                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: false, compacting: false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Background memory trim failed: {ex.Message}");
            }
        }

        private async Task TrimGlobalVisualCachesAsync(CancellationToken cancellationToken)
        {
            if (m_window?.DispatcherQueue == null)
            {
                return;
            }

            var completionSource = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = m_window.DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    completionSource.TrySetResult(null);
                }
                catch (Exception ex)
                {
                    completionSource.TrySetException(ex);
                }
            });

            if (!enqueued)
            {
                return;
            }

            using var cancellationRegistration = cancellationToken.Register(() =>
                completionSource.TrySetCanceled(cancellationToken));
            await completionSource.Task.ConfigureAwait(false);
        }

        private static void ForceTrimProcessWorkingSet()
        {
            try
            {
                using Process process = Process.GetCurrentProcess();
                IntPtr handle = process.Handle;
                _ = EmptyWorkingSet(handle);
                _ = SetProcessWorkingSetSize(handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch
            {
            }
        }

        private void EnsureWindowOnTop()
        {
            if (m_window is null)
            {
                return;
            }

            IntPtr hwnd = WindowNative.GetWindowHandle(m_window);
            SetForegroundWindow(hwnd);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
            SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
        }

        private void RestartApplication()
        {
            bool restartScheduled = false;

            if (IsPackaged())
            {
                try
                {
                    Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
                    restartScheduled = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to request packaged restart: {ex}");
                }
            }
            else
            {
                try
                {
                    if (!string.IsNullOrEmpty(Environment.ProcessPath))
                    {
                        Process.Start(new ProcessStartInfo(Environment.ProcessPath)
                        {
                            UseShellExecute = true
                        });
                        restartScheduled = true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to launch new process for restart: {ex}");
                }
            }

            if (restartScheduled)
            {
                ExitApplication();
            }
        }

        public void RestartApplicationFromSettings()
        {
            RestartApplication();
        }

        public void ExitApplication()
        {
            if (Interlocked.Exchange(ref _isExiting, 1) == 1)
            {
                return;
            }

            if (m_window is not null && m_window.DispatcherQueue.TryEnqueue(ExitApplicationCore))
            {
                return;
            }

            ExitApplicationCore();
        }

        private void ExitApplicationCore()
        {
            ReleaseGlobalHotKey();
            DisposeTrayIcon();
            DisposeClipboardMonitor();
            DisposeClipboardModel();
            DisposeBackgroundTaskQueue();
            StopSingleInstanceActivationListener();
            ReleaseSingleInstanceLock();

            try
            {
                m_window?.Close();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to close main window: {ex.Message}");
            }

            Exit();
        }

        private static void FireAndForget(Task task, string operationName)
        {
            _ = task.ContinueWith(
                t => Debug.WriteLine($"{operationName} failed: {t.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        private void DisposeBackgroundTaskQueue()
        {
            _backgroundTaskQueue?.Dispose();
            _backgroundTaskQueue = null;
        }

        private void DisposeHotKey()
        {
            _hotKeyService?.Dispose();
            _hotKeyService = null;
        }

        private void DisposeDoubleTapHotKey()
        {
            _doubleTapHotKeyService?.Dispose();
            _doubleTapHotKeyService = null;
        }

        public void ReleaseGlobalHotKey()
        {
            DisposeHotKey();
            DisposeDoubleTapHotKey();
        }

        public void RefreshConfiguredGlobalHotKey()
        {
            ApplyConfiguredGlobalHotKey();
        }

        public void NotifyClipboardDataImported()
        {
            void ReloadData()
            {
                try
                {
                    ClipboardDataReloading?.Invoke(this, EventArgs.Empty);
                    ClipboardModel.LoadItems();
                    ClipboardModel.IsLoaded = true;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"NotifyClipboardDataImported failed: {ex}");
                }
                finally
                {
                    ClipboardDataReplaced?.Invoke(this, EventArgs.Empty);
                }
            }

            DispatcherQueue? dispatcherQueue = m_window?.DispatcherQueue;
            if (dispatcherQueue != null)
            {
                if (dispatcherQueue.HasThreadAccess)
                {
                    ReloadData();
                    return;
                }

                if (dispatcherQueue.TryEnqueue(ReloadData))
                {
                    return;
                }
            }

            ReloadData();
        }

        private void InitializeClipboardMonitor()
        {
            if (m_window is null)
            {
                return;
            }

            try
            {
                _clipboardMonitorService = new ClipboardMonitorService(m_window.DispatcherQueue);
                _clipboardMonitorService.Initialize(ClipboardModel);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clipboard monitor initialization failed: {ex}");
            }
        }

        private void DisposeClipboardMonitor()
        {
            _clipboardMonitorService?.Dispose();
            _clipboardMonitorService = null;
        }

        private void DisposeClipboardModel()
        {
            LiuYun.Models.ClipboardItem.ClearGlobalImageCache();
            ClipboardModel?.Dispose();
        }

        private Task ShowHotKeyErrorAsync(string message)
        {
            if (_hasShownHotKeyFailure)
            {
                Debug.WriteLine($"Skipping duplicate hotkey warning: {message}");
                return Task.CompletedTask;
            }

            _hasShownHotKeyFailure = true;

            Debug.WriteLine($"Hotkey warning (silent): {message}");
            return Task.CompletedTask;
        }

        private static string BuildHotKeyErrorMessage(string hotkeyLabel, string? detail, int errorCode)
        {
            string normalizedLabel = string.IsNullOrWhiteSpace(hotkeyLabel) ? "快捷键" : hotkeyLabel;
            bool isWinV = string.Equals(normalizedLabel, "win + v", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(normalizedLabel, "Win+V", StringComparison.OrdinalIgnoreCase);
            string reason = errorCode switch
            {
                1409 => $"快捷键 {normalizedLabel} 已被其他应用或系统占用。",
                1402 => "注册快捷键时窗口句柄尚未就绪。",
                _ => detail ?? "注册快捷键时发生未知错误。"
            };

            string codeSuffix = errorCode != 0 ? $" (0x{errorCode:X8})" : string.Empty;

            string message = $"LiuYun 无法注册全局快捷键 {normalizedLabel}。{Environment.NewLine}{Environment.NewLine}{reason}{codeSuffix}{Environment.NewLine}{Environment.NewLine}";

            if (isWinV)
            {
                var diagnostics = HotkeyDiagnosticsService.RunDiagnostics();
                message += $"=== 诊断信息 ==={Environment.NewLine}";
                message += $"管理员权限: {(diagnostics.IsRunningAsAdmin ? "是" : "否")}{Environment.NewLine}";
                message += $"系统剪贴板历史: {(diagnostics.ClipboardHistoryEnabled ? "已启用(占用Win+V)" : "已禁用")}{Environment.NewLine}{Environment.NewLine}";
            }

            message += "应用将继续运行，但快捷键在本次会话中不可用。";

            return message;
        }

        private static bool IsPackaged()
        {
            try
            {
                _ = Package.Current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void RegisterGlobalExceptionHandlers()
        {
            UnhandledException += App_UnhandledException;

            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            e.Handled = true;
            LogException(e.Exception, "WinUI Unhandled Exception");
        }

        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                LogException(ex, "AppDomain Unhandled Exception");
            }
        }

        private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            e.SetObserved();
            LogException(e.Exception, "Task Unobserved Exception");
        }

        private void LogException(Exception ex, string context)
        {
            try
            {
                string logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {context}\n" +
                                  $"Type: {ex.GetType().FullName}\n" +
                                  $"Message: {ex.Message}\n" +
                                  $"Stack: {ex.StackTrace}\n";

                if (ex is COMException comEx)
                {
                    logMessage += $"HRESULT: 0x{comEx.HResult:X8}\n";
                }

                logMessage += "\n";

                Debug.WriteLine(logMessage);

                string logPath = System.IO.Path.Combine(DatabaseService.AppDataFolder, "error.log");
                System.IO.File.AppendAllText(logPath, logMessage);
            }
            catch
            {
            }
        }

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AllowSetForegroundWindow(uint dwProcessId);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")]
        private static extern IntPtr GetFocus();
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCaretPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(
            IntPtr TokenHandle,
            TOKEN_INFORMATION_CLASS TokenInformationClass,
            out TOKEN_ELEVATION TokenInformation,
            uint TokenInformationLength,
            out uint ReturnLength);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EmptyWorkingSet(IntPtr hProcess);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

        private const uint InputTypeKeyboard = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const ushort VK_CONTROL = 0x11;
        private const ushort VK_SHIFT = 0x10;
        private const ushort VK_MENU = 0x12;
        private const ushort VK_LWIN = 0x5B;
        private const ushort VK_RWIN = 0x5C;
        private const ushort VK_V = 0x56;
        private const uint ASFW_ANY = 0xFFFFFFFF;
        private const uint GA_ROOT = 2;
        private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        private const uint TOKEN_QUERY = 0x0008;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public uint cbSize;
            public uint flags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)]
            public KEYBDINPUT ki;
            [FieldOffset(0)]
            public MOUSEINPUT mi;
            [FieldOffset(0)]
            public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        private enum TOKEN_INFORMATION_CLASS
        {
            TokenElevation = 20
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_ELEVATION
        {
            public int TokenIsElevated;
        }

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const int SW_RESTORE = 9;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);
    }
}
