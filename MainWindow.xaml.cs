using System;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Threading.Tasks;
using Windows.Graphics;
using WinRT;
using LiuYun.Services;

namespace LiuYun
{
    public sealed partial class MainWindow : Window
    {
        private DesktopAcrylicController? m_acrylicController;
        private SystemBackdropConfiguration? m_configurationSource;

        private double _currentScale = 1.0;
        private SUBCLASSPROC? _subclassProc;
        private IntPtr _hWnd;

        private const double IdealWindowSizeDip = 520;
        private const double WindowWidthRatio = 0.58;
        private const double SidebarWidthPercent = 0.28;

        private const double MinWindowSizeDip = 450;
        private const double MaxWindowSizeDip = 620;

        private const double LargeScreenHeightThreshold = 850;
        private const double LargeScreenWidthThreshold = 1400;
        private const double MediumScreenDipThreshold = 800;

        private const double MediumScreenPercent = 0.65;
        private const double SmallScreenPercent = 0.68;

        private const double MaxScreenOccupancyPercent = 0.75;
        private const double MinSidebarWidthDip = 150;
        private const double LogoHorizontalPaddingDip = 56;
        private const double MinLogoWidthDip = 56;
        private const double MaxLogoWidthDip = 96;
        private const double RootGridVerticalPaddingDip = 16;
        private const double FixedContentHeightDip = 432;
        private const double MinWindowHeightDip = 430;
        private const double MaxWindowHeightDip = 620;
        private const int WindowHeightSafetyMarginPx = 8;
        private bool _isFreeDragMoving;
        private uint _activeDragPointerId;
        private POINT _dragStartCursor;
        private PointInt32 _dragStartWindowPosition;
        private bool _dragMoved;

        public MainWindow()
        {
            this.InitializeComponent();
            EnsureOverlayDynamicBrush();

            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            SetupWindowSizeAndLock();

            SetupAcrylicBackdrop();
            SyncOverlayBrushFromActiveLayer();

            SetupDpiChangeHandler();

            rootFrame.Navigated += RootFrame_Navigated;

            this.Activated += MainWindow_Activated;

            rootFrame.Loaded += RootFrame_Loaded;
            SidebarGrid.SizeChanged += SidebarGrid_SizeChanged;

            this.Closed += MainWindow_Closed;
        }

        private void EnsureOverlayDynamicBrush()
        {
            if (Application.Current?.Resources is not ResourceDictionary resources)
            {
                return;
            }

            if (resources.TryGetValue("OverlayDynamicAcrylicBrush", out object? existing) &&
                existing is Microsoft.UI.Xaml.Media.AcrylicBrush)
            {
                return;
            }

            var seedColor = Windows.UI.Color.FromArgb(0xCC, 0x1F, 0x3A, 0x63);
            var acrylic = new Microsoft.UI.Xaml.Media.AcrylicBrush
            {
                TintColor = seedColor,
                TintOpacity = 0.68,
                FallbackColor = seedColor
            };
            resources["OverlayDynamicAcrylicBrush"] = acrylic;
        }

        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            rootFrame.Navigated -= RootFrame_Navigated;
            this.Activated -= MainWindow_Activated;
            rootFrame.Loaded -= RootFrame_Loaded;
            SidebarGrid.SizeChanged -= SidebarGrid_SizeChanged;
            this.Closed -= MainWindow_Closed;
            if (rootFrame.XamlRoot != null)
            {
                rootFrame.XamlRoot.Changed -= XamlRoot_Changed;
            }

            if (_subclassProc != null && _hWnd != IntPtr.Zero)
            {
                RemoveWindowSubclass(_hWnd, _subclassProc, (IntPtr)1);
                _subclassProc = null;
                _hWnd = IntPtr.Zero;
            }

            if (m_acrylicController != null)
            {
                m_acrylicController.Dispose();
                m_acrylicController = null;
            }

            m_configurationSource = null;
        }

        private void SetupDpiChangeHandler()
        {
            _hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _subclassProc = new SUBCLASSPROC(WindowSubclassProc);
            SetWindowSubclass(_hWnd, _subclassProc, (IntPtr)1, IntPtr.Zero);
        }

        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (uMsg == WM_NCLBUTTONDBLCLK)
            {
                return IntPtr.Zero;
            }

            if (uMsg == WM_DPICHANGED || uMsg == WM_DISPLAYCHANGE)
            {
                DispatcherQueue.TryEnqueue(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(100);
                    double newScale = GetDpiScale(hWnd);
                    _currentScale = newScale;
                    ResizeWindowForCurrentDpi();
                });
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void RootFrame_Loaded(object sender, RoutedEventArgs e)
        {
            if (rootFrame.XamlRoot != null)
            {
                rootFrame.XamlRoot.Changed += XamlRoot_Changed;
                _currentScale = rootFrame.XamlRoot.RasterizationScale;
            }

            RefreshOverlayBrushForTransientSurfaces();
            ResizeWindowForCurrentDpi();
        }

        private void SidebarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 0.5)
            {
                ResizeWindowForCurrentDpi();
            }
        }

        private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
        {
            double newScale = sender.RasterizationScale;
            if (Math.Abs(newScale - _currentScale) > 0.01)
            {
                _currentScale = newScale;
                ResizeWindowForCurrentDpi();
            }
        }

        private void CheckAndUpdateDpiScale()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            double newScale = GetDpiScale(hWnd);

            if (Math.Abs(newScale - _currentScale) > 0.01)
            {
                _currentScale = newScale;
                ResizeWindowForCurrentDpi();
            }
        }

        private void ResizeWindowForCurrentDpi()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                int scaledSize;

                if (displayArea != null)
                {
                    int workAreaWidth = displayArea.WorkArea.Width;
                    int workAreaHeight = displayArea.WorkArea.Height;
                    double workAreaHeightDip = workAreaHeight / _currentScale;
                    double workAreaWidthDip = workAreaWidth / _currentScale;
                    double targetSizeDip;
                    if (workAreaHeightDip >= LargeScreenHeightThreshold || workAreaWidthDip >= LargeScreenWidthThreshold)
                    {
                        targetSizeDip = IdealWindowSizeDip;
                    }
                    else if (workAreaHeightDip >= MediumScreenDipThreshold)
                    {
                        targetSizeDip = workAreaHeightDip * MediumScreenPercent;
                    }
                    else
                    {
                        targetSizeDip = workAreaHeightDip * SmallScreenPercent;
                    }

                    targetSizeDip = Math.Clamp(targetSizeDip, MinWindowSizeDip, MaxWindowSizeDip);
                    targetSizeDip = Math.Min(targetSizeDip, workAreaWidthDip);
                    int targetSize = (int)Math.Round(targetSizeDip * _currentScale);
                    int maxAllowedSize = (int)Math.Round(Math.Min(workAreaWidth, workAreaHeight) * MaxScreenOccupancyPercent);
                    scaledSize = Math.Min(targetSize, maxAllowedSize);
                }
                else
                {
                    scaledSize = (int)Math.Round(520 * _currentScale);
                }

                int scaledWidth = Math.Max((int)Math.Round(scaledSize * WindowWidthRatio), 1);
                double windowWidthDip = scaledWidth / _currentScale;
                double sidebarWidthDip = Math.Max(windowWidthDip * SidebarWidthPercent, MinSidebarWidthDip);
                SidebarGrid.Width = sidebarWidthDip;
                UpdateSidebarLogoSize(sidebarWidthDip);
                int scaledHeight = CalculateWindowHeightPx(displayArea);
                appWindow.Resize(new SizeInt32(scaledWidth, scaledHeight));

            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (m_configurationSource != null)
            {
                m_configurationSource.IsInputActive = true;
            }

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            RemoveSystemButtons(hWnd);

            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {
                CheckAndUpdateDpiScale();
                RefreshOverlayBrushForTransientSurfaces();
                NotifyWindowVisibilityChanged(true);
            }

            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                if (App.Current is App app && app.m_window == this)
                {
                    if (app.IsClipboardPinned)
                    {
                        // Keep clipboard pinned but allow automatic target switch to the newly activated window.
                        app.CaptureInvocationWindowFromCurrentForeground();
                    }

                    if (app.AutoHideOnDeactivate)
                    {
                        _ = app.TriggerWindowHideAsync();
                    }
                }
            }
        }

        private static void MoveAppWindowNearCursor(AppWindow appWindow, POINT cursorPoint)
        {
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

            int rightX = cursorPoint.X + App.CursorPlacementOffsetPx;
            int leftX = cursorPoint.X - App.CursorPlacementOffsetPx - windowWidth;
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

            int yAbove = cursorPoint.Y - App.CursorPlacementOffsetPx - windowHeight;
            int yBelow = cursorPoint.Y + App.CursorPlacementOffsetPx;
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

        private void RootFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            UpdateNavigationButtons(e.SourcePageType);
        }

        internal void NotifyWindowVisibilityChanged(bool isVisible)
        {
            if (rootFrame.Content is LiuYun.Views.IBackgroundAwarePage backgroundAwarePage)
            {
                backgroundAwarePage.OnHostWindowVisibilityChanged(isVisible);
            }
        }

        private void WindowDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not UIElement sourceElement ||
                !e.GetCurrentPoint(sourceElement).Properties.IsLeftButtonPressed)
            {
                return;
            }

            PopupPlacementMode mode = PopupPlacementConfigService.GetMode();
            if (mode != PopupPlacementMode.FreeDrag && mode != PopupPlacementMode.FollowMouse)
            {
                return;
            }

            if (!TryGetCursorPos(out POINT cursorPoint) || !TryGetCurrentAppWindow(out AppWindow appWindow))
            {
                return;
            }

            _isFreeDragMoving = true;
            _activeDragPointerId = e.Pointer.PointerId;
            _dragStartCursor = cursorPoint;
            _dragStartWindowPosition = appWindow.Position;
            _dragMoved = false;

            sourceElement.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void WindowDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isFreeDragMoving || e.Pointer.PointerId != _activeDragPointerId || sender is not UIElement sourceElement)
            {
                return;
            }

            if (!e.GetCurrentPoint(sourceElement).Properties.IsLeftButtonPressed)
            {
                EndFreeDrag(sourceElement, shouldPersist: true);
                e.Handled = true;
                return;
            }

            if (!TryGetCursorPos(out POINT cursorPoint) || !TryGetCurrentAppWindow(out AppWindow appWindow))
            {
                return;
            }

            int deltaX = cursorPoint.X - _dragStartCursor.X;
            int deltaY = cursorPoint.Y - _dragStartCursor.Y;
            int targetX = _dragStartWindowPosition.X + deltaX;
            int targetY = _dragStartWindowPosition.Y + deltaY;

            appWindow.Move(new PointInt32(targetX, targetY));
            _dragMoved = _dragMoved || deltaX != 0 || deltaY != 0;
            e.Handled = true;
        }

        private void WindowDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isFreeDragMoving || e.Pointer.PointerId != _activeDragPointerId || sender is not UIElement sourceElement)
            {
                return;
            }

            EndFreeDrag(sourceElement, shouldPersist: true);
            e.Handled = true;
        }

        private void WindowDragHandle_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (!_isFreeDragMoving || e.Pointer.PointerId != _activeDragPointerId || sender is not UIElement sourceElement)
            {
                return;
            }

            EndFreeDrag(sourceElement, shouldPersist: true);
            e.Handled = true;
        }

        private void WindowDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (!_isFreeDragMoving || e.Pointer.PointerId != _activeDragPointerId || sender is not UIElement sourceElement)
            {
                return;
            }

            EndFreeDrag(sourceElement, shouldPersist: true);
            e.Handled = true;
        }

        private void EndFreeDrag(UIElement sourceElement, bool shouldPersist)
        {
            sourceElement.ReleasePointerCaptures();

            if (shouldPersist && _dragMoved)
            {
                SaveFreeDragAnchorPositionIfNeeded();
            }

            _isFreeDragMoving = false;
            _activeDragPointerId = 0;
            _dragMoved = false;
        }

        private bool TryGetCurrentAppWindow(out AppWindow appWindow)
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            appWindow = AppWindow.GetFromWindowId(windowId);
            return appWindow != null;
        }

        private static bool TryGetCursorPos(out POINT cursorPoint)
        {
            return GetCursorPos(out cursorPoint);
        }

        private void SaveFreeDragAnchorPositionIfNeeded()
        {
            if (PopupPlacementConfigService.GetMode() != PopupPlacementMode.FreeDrag)
            {
                return;
            }

            try
            {
                if (!TryGetCurrentAppWindow(out AppWindow appWindow))
                {
                    return;
                }

                PointInt32 position = appWindow.Position;
                PopupPlacementConfigService.SetFreeDragPosition(position.X, position.Y);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to persist free-drag window position: {ex.Message}");
            }
        }

        private void UpdateNavigationButtons(Type pageType)
        {
            var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
            Microsoft.UI.Xaml.Media.Brush highlightBrush;

            if (Application.Current.Resources.TryGetValue("SubtleFillColorSecondaryBrush", out object? resource) && resource is Microsoft.UI.Xaml.Media.Brush brush)
            {
                highlightBrush = brush;
            }
            else
            {
                highlightBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x19, 0xFF, 0xFF, 0xFF));
            }

            ClipboardButton.Background = transparentBrush;
            SettingsButton.Background = transparentBrush;

            if (pageType == typeof(LiuYun.Views.ClipboardPage))
            {
                ClipboardButton.Background = highlightBrush;
            }
            else if (pageType == typeof(LiuYun.Views.SettingsPage))
            {
                SettingsButton.Background = highlightBrush;
            }
        }

        internal void NavigateRoot(Type pageType)
        {
            if (rootFrame.Content?.GetType() == pageType)
            {
                return;
            }

            rootFrame.Navigate(pageType);
            if (rootFrame.BackStackDepth > 0)
            {
                rootFrame.BackStack.Clear();
            }
        }

        internal void PrepareClipboardHistoryForHotkeyShow()
        {
            ClipboardFilterState.Current = ClipboardCategoryFilter.All;
            ClipboardTimeFilterState.Clear();
            UpdateBannerService.CollapseReleaseNotes();
            NavigateRoot(typeof(LiuYun.Views.ClipboardPage));
        }

        private void ClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateRoot(typeof(LiuYun.Views.ClipboardPage));
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateRoot(typeof(LiuYun.Views.SettingsPage));
        }

        private void SetupAcrylicBackdrop()
        {
            if (DesktopAcrylicController.IsSupported())
            {
                m_configurationSource = new SystemBackdropConfiguration();
                m_configurationSource.IsInputActive = true;

                m_acrylicController = new DesktopAcrylicController();
                m_acrylicController.SetSystemBackdropConfiguration(m_configurationSource);
                m_acrylicController.AddSystemBackdropTarget(this.As<Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop>());
            }
        }

        internal void RefreshOverlayBrushForTransientSurfaces()
        {
            SyncOverlayBrushFromActiveLayer();
            SyncOverlayBrushAfterDelay(90);
            SyncOverlayBrushAfterDelay(220);
        }

        private void SyncOverlayBrushFromActiveLayer()
        {
            EnsureOverlayDynamicBrush();

            if (Application.Current?.Resources is not ResourceDictionary resources)
            {
                return;
            }

            if (!resources.TryGetValue("LayerOnAcrylicFillColorDefaultBrush", out object? activeObj) ||
                activeObj is not Microsoft.UI.Xaml.Media.SolidColorBrush activeBrush)
            {
                return;
            }

            if (resources.TryGetValue("OverlayDynamicAcrylicBrush", out object? overlayObj) &&
                overlayObj is Microsoft.UI.Xaml.Media.AcrylicBrush overlayBrush)
            {
                if (IsValidOverlayColor(activeBrush.Color))
                {
                    overlayBrush.TintColor = activeBrush.Color;
                    overlayBrush.FallbackColor = activeBrush.Color;
                }
            }
        }

        private static bool IsValidOverlayColor(Windows.UI.Color color)
        {
            return color.A >= 0x40 && !(color.R == 0x00 && color.G == 0x00 && color.B == 0x00);
        }

        private async void SyncOverlayBrushAfterDelay(int delayMs)
        {
            await Task.Delay(delayMs);
            DispatcherQueue.TryEnqueue(SyncOverlayBrushFromActiveLayer);
        }

        private int CalculateWindowHeightPx(DisplayArea? displayArea)
        {
            double requiredHeightDip = GetRequiredWindowHeightDip();
            int requiredHeightPx = (int)Math.Ceiling(requiredHeightDip * _currentScale);

            if (displayArea != null)
            {
                int maxHeightPx = Math.Max(200, displayArea.WorkArea.Height - WindowHeightSafetyMarginPx);
                requiredHeightPx = Math.Min(requiredHeightPx, maxHeightPx);
            }

            return requiredHeightPx;
        }

        private void UpdateSidebarLogoSize(double sidebarWidthDip)
        {
            if (FlowLogoImage == null)
            {
                return;
            }

            double targetLogoWidthDip = Math.Clamp(sidebarWidthDip - LogoHorizontalPaddingDip, MinLogoWidthDip, MaxLogoWidthDip);
            FlowLogoImage.Width = targetLogoWidthDip;
        }

        private double GetRequiredWindowHeightDip()
        {
            double targetHeightDip = RootGridVerticalPaddingDip + FixedContentHeightDip;
            return Math.Clamp(targetHeightDip, MinWindowHeightDip, MaxWindowHeightDip);
        }

        private void SetupWindowSizeAndLock()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                _currentScale = GetDpiScale(hWnd);
                DisplayArea displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                int scaledSize;

                if (displayArea != null)
                {
                    int workAreaWidth = displayArea.WorkArea.Width;
                    int workAreaHeight = displayArea.WorkArea.Height;
                    double workAreaHeightDip = workAreaHeight / _currentScale;
                    double workAreaWidthDip = workAreaWidth / _currentScale;
                    double targetSizeDip;
                    if (workAreaHeightDip >= LargeScreenHeightThreshold || workAreaWidthDip >= LargeScreenWidthThreshold)
                    {
                        targetSizeDip = IdealWindowSizeDip;
                    }
                    else if (workAreaHeightDip >= MediumScreenDipThreshold)
                    {
                        targetSizeDip = workAreaHeightDip * MediumScreenPercent;
                    }
                    else
                    {
                        targetSizeDip = workAreaHeightDip * SmallScreenPercent;
                    }

                    targetSizeDip = Math.Clamp(targetSizeDip, MinWindowSizeDip, MaxWindowSizeDip);
                    targetSizeDip = Math.Min(targetSizeDip, workAreaWidthDip);
                    int targetSize = (int)Math.Round(targetSizeDip * _currentScale);
                    int maxAllowedSize = (int)Math.Round(Math.Min(workAreaWidth, workAreaHeight) * MaxScreenOccupancyPercent);
                    scaledSize = Math.Min(targetSize, maxAllowedSize);
                }
                else
                {
                    scaledSize = (int)Math.Round(520 * _currentScale);
                }

                int scaledWidth = Math.Max((int)Math.Round(scaledSize * WindowWidthRatio), 1);
                double windowWidthDip = scaledWidth / _currentScale;
                double sidebarWidthDip = Math.Max(windowWidthDip * SidebarWidthPercent, MinSidebarWidthDip);
                SidebarGrid.Width = sidebarWidthDip;
                UpdateSidebarLogoSize(sidebarWidthDip);
                int scaledHeight = CalculateWindowHeightPx(displayArea);
                appWindow.Resize(new SizeInt32(scaledWidth, scaledHeight));

                if (appWindow.Presenter is OverlappedPresenter presenter)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                    presenter.IsMinimizable = false;
                    presenter.SetBorderAndTitleBar(true, false);
                }

                if (appWindow.TitleBar != null)
                {
                    appWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
                    appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonPressedBackgroundColor = Microsoft.UI.Colors.Transparent;
                    appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Transparent;
                }
            }

            RemoveSystemButtons(hWnd);
            HideFromTaskbar(hWnd);
        }

        #region Win32 API Constants and Imports

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000;
        private const int WS_MAXIMIZEBOX = 0x00010000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int SWP_NOSIZE = 0x0001;
        private const int SWP_NOMOVE = 0x0002;
        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_FRAMECHANGED = 0x0020;

        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_DISPLAYCHANGE = 0x007E;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, IntPtr uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        #endregion

        #region Window Style Methods

        private void RemoveSystemButtons(IntPtr hWnd)
        {
            int style = GetWindowLong(hWnd, GWL_STYLE);
            style &= ~(WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            SetWindowLong(hWnd, GWL_STYLE, style);

            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        private void HideFromTaskbar(IntPtr hWnd)
        {
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            exStyle &= ~WS_EX_APPWINDOW;
            exStyle |= WS_EX_TOOLWINDOW;
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle);

            SetWindowPos(hWnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }

        // Make the window top-most or restore normal z-order
        public void SetTopMost(bool topMost)
        {
            try
            {
                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                IntPtr insertAfter = topMost ? new IntPtr(-1) : new IntPtr(-2);
                uint flags = (uint)(SWP_NOSIZE | SWP_NOMOVE);
                SetWindowPos(hWnd, insertAfter, 0, 0, 0, 0, flags);
            }
            catch
            {
                // ignore failures
            }
        }

        private static double GetDpiScale(IntPtr hWnd)
        {
            uint dpi = GetDpiForWindow(hWnd);
            return dpi > 0 ? dpi / 96.0 : 1.0;
        }

        #endregion
    }
}
