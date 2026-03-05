using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LiuYun.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace LiuYun.Views
{
    public sealed partial class SettingsPage : Page
    {
        private readonly StartupService _startupService = new();
        private bool _startupToggleReady;
        private bool _isApplyingStartupChange;
        private bool _isLoadingClipboardImageCleanupSetting;
        private bool _isCheckingUpdate;
        private bool _updateBannerSubscribed;
        private bool _isRecordingHotkey;
        private bool _isApplyingHotkeyChange;
        private bool _isDataOperationRunning;
        private bool _isLoadingPopupPlacement;
        private bool _isSectionTransitionRunning;
        private string? _captureLastModifierKey;
        private DateTime _captureLastModifierReleaseUtc = DateTime.MinValue;
        private WinVHotkeyCaptureService? _winVCaptureService;
        private static readonly TimeSpan SectionExpandDuration = TimeSpan.FromMilliseconds(230);
        private static readonly TimeSpan SectionCollapseDuration = TimeSpan.FromMilliseconds(120);

        private static void LogUpdate(string message)
        {
            UpdateDiagnostics.Log("SettingsPage.Update", message);
        }

        public SettingsPage()
        {
            InitializeComponent();
            AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(SettingsPage_PreviewKeyDown), true);
            AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler(SettingsPage_PreviewKeyUp), true);
        }

        private void BackToClipboardButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(ClipboardPage));
        }

        private async void SectionToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSectionTransitionRunning)
            {
                return;
            }

            if (sender is not FrameworkElement element || element.Tag is not string tag)
            {
                return;
            }

            string[] segments = tag.Split('|', 2, StringSplitOptions.TrimEntries);
            if (segments.Length == 0 || string.IsNullOrWhiteSpace(segments[0]))
            {
                return;
            }

            if (FindName(segments[0]) is not FrameworkElement content)
            {
                return;
            }

            _isSectionTransitionRunning = true;
            try
            {
                bool shouldExpand = content.Visibility != Visibility.Visible;
                if (shouldExpand && string.Equals(segments[0], nameof(StartupSectionContent), StringComparison.Ordinal))
                {
                    await CollapseSectionIfVisibleAsync(nameof(ExitSectionContent), nameof(ExitSectionChevron));
                }
                else if (shouldExpand && string.Equals(segments[0], nameof(ExitSectionContent), StringComparison.Ordinal))
                {
                    await CollapseSectionIfVisibleAsync(nameof(StartupSectionContent), nameof(StartupSectionChevron));
                }

                await ToggleSectionVisibilityAsync(content, shouldExpand);

                if (segments.Length > 1 &&
                    !string.IsNullOrWhiteSpace(segments[1]) &&
                    FindName(segments[1]) is IconElement chevron)
                {
                    SetChevronExpandedState(chevron, shouldExpand);
                }
            }
            finally
            {
                _isSectionTransitionRunning = false;
            }
        }

        private async Task CollapseSectionIfVisibleAsync(string contentName, string chevronName)
        {
            if (FindName(contentName) is FrameworkElement content && content.Visibility == Visibility.Visible)
            {
                await ToggleSectionVisibilityAsync(content, shouldExpand: false);
            }

            if (FindName(chevronName) is IconElement chevron)
            {
                SetChevronExpandedState(chevron, isExpanded: false);
            }
        }

        private static void SetChevronExpandedState(IconElement chevron, bool isExpanded)
        {
            if (chevron is FontIcon fontIcon)
            {
                fontIcon.Glyph = isExpanded ? "\uE70E" : "\uE70D";
            }
        }

        private static Task RunStoryboardAsync(Storyboard storyboard)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void CompleteHandler(object? s, object e)
            {
                storyboard.Completed -= CompleteHandler;
                tcs.TrySetResult(true);
            }

            storyboard.Completed += CompleteHandler;
            storyboard.Begin();
            return tcs.Task;
        }

        private static async Task ToggleSectionVisibilityAsync(FrameworkElement content, bool shouldExpand)
        {
            if (shouldExpand)
            {
                content.Visibility = Visibility.Visible;
                content.Height = double.NaN;
                content.Opacity = 1;
                content.UpdateLayout();
                double targetHeight = Math.Max(content.ActualHeight, 1d);
                content.Height = 0;
                content.IsHitTestVisible = false;

                var expandStoryboard = new Storyboard();

                var expandHeightAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = targetHeight,
                    Duration = SectionExpandDuration,
                    EnableDependentAnimation = true,
                    EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 }
                };
                Storyboard.SetTarget(expandHeightAnimation, content);
                Storyboard.SetTargetProperty(expandHeightAnimation, nameof(FrameworkElement.Height));

                expandStoryboard.Children.Add(expandHeightAnimation);
                await RunStoryboardAsync(expandStoryboard);

                content.Height = double.NaN;
                content.Opacity = 1;
                content.IsHitTestVisible = true;
                return;
            }

            if (content.Visibility != Visibility.Visible)
            {
                content.Visibility = Visibility.Collapsed;
                content.Height = double.NaN;
                content.Opacity = 1;
                content.IsHitTestVisible = true;
                return;
            }

            double currentHeight = Math.Max(content.ActualHeight, 1d);
            content.Height = currentHeight;
            content.IsHitTestVisible = false;
            var collapseStoryboard = new Storyboard();

            var collapseHeightAnimation = new DoubleAnimation
            {
                From = currentHeight,
                To = 0,
                Duration = SectionCollapseDuration,
                EnableDependentAnimation = true,
                EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseIn, Exponent = 4 }
            };
            Storyboard.SetTarget(collapseHeightAnimation, content);
            Storyboard.SetTargetProperty(collapseHeightAnimation, nameof(FrameworkElement.Height));

            collapseStoryboard.Children.Add(collapseHeightAnimation);
            await RunStoryboardAsync(collapseStoryboard);

            content.Visibility = Visibility.Collapsed;
            content.Height = double.NaN;
            content.Opacity = 1;
            content.IsHitTestVisible = true;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            string currentVersion = UpdateBannerService.GetCurrentVersionString();
            string displayVersion = GetDisplayVersionFromInformationalVersion();
            SetCurrentVersionText(displayVersion);
            _ = RefreshStartupStateAsync();
            HotkeyConfigService.EnsureInitialized();
            UpdateClipboardStatus();
            LoadClipboardImageCleanupSetting();
            LoadPopupPlacementMode();
            RefreshDataStorageCardState();
            AttachUpdateBannerSubscription();
            UpdateBannerService.EnsureStartupCheckStarted(currentVersion);
            ApplyUpdateBannerState(UpdateBannerService.GetSnapshot());
            LogUpdate($"Navigated to settings. version={currentVersion}, displayVersion={displayVersion}");
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            EndHotkeyRecording();
            DetachUpdateBannerSubscription();
        }

        private void SetCurrentVersionText(string version)
        {
            if (CurrentVersionText == null)
            {
                return;
            }

            CurrentVersionText.Text = $"当前版本：{version}";
        }

        private static string GetDisplayVersionFromInformationalVersion()
        {
            string? informationalVersion = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataSeparator = informationalVersion.IndexOf('+');
                string normalizedVersion = metadataSeparator > 0
                    ? informationalVersion.Substring(0, metadataSeparator)
                    : informationalVersion;
                return $"v{normalizedVersion}";
            }

            return $"v{UpdateBannerService.GetCurrentVersionString()}";
        }

        private void AttachUpdateBannerSubscription()
        {
            if (_updateBannerSubscribed)
            {
                return;
            }

            UpdateBannerService.StateChanged += UpdateBannerService_StateChanged;
            _updateBannerSubscribed = true;
        }

        private void DetachUpdateBannerSubscription()
        {
            if (!_updateBannerSubscribed)
            {
                return;
            }

            UpdateBannerService.StateChanged -= UpdateBannerService_StateChanged;
            _updateBannerSubscribed = false;
        }

        private void UpdateBannerService_StateChanged(object? sender, EventArgs e)
        {
            if (DispatcherQueue == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() => ApplyUpdateBannerState(UpdateBannerService.GetSnapshot()));
        }

        private void ApplyUpdateBannerState(UpdateBannerSnapshot state)
        {
            bool hasUpdate = state.HasUpdate;
            UpdateBannerTitleText.Text = "版本检查";
            if (hasUpdate)
            {
                UpdateStatusText.Visibility = Visibility.Collapsed;

                bool hasNotes = !string.IsNullOrWhiteSpace(state.Notes);
                UpdateBannerToggleNotesButton.Visibility = Visibility.Collapsed;
                UpdateBannerDetailsCard.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
                UpdateBannerToggleGlyph.Glyph = state.NotesExpanded ? "\uE70E" : "\uE70D";
                UpdateBannerDetailsVersionText.Text = $"LiuYun {state.RemoteVersion}";
                UpdateBannerDetailsNotesText.Text = FormatReleaseNotes(state.Notes);

                UpdateBannerActionPanel.Visibility = Visibility.Visible;
                UpdateBannerInstallButton.IsEnabled = true;
                UpdateBannerInstallText.Text = "前往GitHub下载";
                CheckUpdateButton.Visibility = Visibility.Collapsed;
                UpdateBannerProgressBarInline.IsIndeterminate = false;
                UpdateBannerProgressBarInline.Value = 0;
                UpdateBannerProgressBarInline.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateStatusText.Visibility = Visibility.Visible;
                UpdateBannerActionPanel.Visibility = Visibility.Collapsed;
                UpdateBannerDetailsCard.Visibility = Visibility.Collapsed;
                UpdateBannerToggleNotesButton.Visibility = Visibility.Collapsed;
                CheckUpdateButton.Visibility = Visibility.Visible;
                UpdateBannerProgressBarInline.IsIndeterminate = false;
                UpdateBannerProgressBarInline.Value = 0;
                UpdateBannerProgressBarInline.Visibility = Visibility.Collapsed;
            }

            if (CheckUpdateButton != null)
            {
                CheckUpdateButton.IsEnabled = !_isCheckingUpdate && !hasUpdate;
            }

            ApplyStartupSelfCheckHint(state);
        }

        private static string FormatReleaseNotes(string rawNotes)
        {
            if (string.IsNullOrWhiteSpace(rawNotes))
            {
                return "暂无更新说明";
            }

            string normalized = rawNotes
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\n', ' ')
                .Trim();

            MatchCollection matches = Regex.Matches(normalized, @"-\s*[^-]+");
            if (matches.Count == 0)
            {
                return normalized;
            }

            return string.Join(Environment.NewLine, matches.Select(m => m.Value.Trim()));
        }

        private void ApplyStartupSelfCheckHint(UpdateBannerSnapshot state)
        {
            if (!state.StartupCheckFailed)
            {
                return;
            }

            string currentText = UpdateStatusText.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(currentText) ||
                currentText.StartsWith("点击“检查更新”", StringComparison.Ordinal) ||
                currentText.StartsWith("当前应用启动自检查失败", StringComparison.Ordinal))
            {
                UpdateStatusText.Text = "当前应用启动自检查失败";
            }
        }

        private async void CheckUpdateAndInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdate)
            {
                LogUpdate("Ignored check click because another update check is running.");
                return;
            }

            LogUpdate("Check update button clicked.");
            _isCheckingUpdate = true;
            CheckUpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "正在检查更新...";

            try
            {
                string currentVersion = UpdateBannerService.GetCurrentVersionString();
                LogUpdate($"Checking update. currentVersion={currentVersion}");
                UpdateCheckResult result = await UpdateBannerService.CheckForUpdatesByUserAsync(currentVersion);
                LogUpdate($"Check result. success={result.Success}, hasUpdate={result.HasUpdate}, remoteVersion={result.RemoteVersion}, error={result.ErrorMessage}");

                if (!result.Success)
                {
                    UpdateStatusText.Text = $"检查失败：{result.ErrorMessage}";
                    return;
                }

                if (!result.HasUpdate)
                {
                    UpdateStatusText.Visibility = Visibility.Visible;
                    UpdateStatusText.Text = $"当前已是最新版本（{currentVersion}）。";
                    return;
                }

                UpdateStatusText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                LogUpdate($"Check/update flow exception: {ex}");
                UpdateStatusText.Text = $"更新流程异常：{ex.Message}";
            }
            finally
            {
                _isCheckingUpdate = false;
                CheckUpdateButton.IsEnabled = true;
                LogUpdate("Check update flow finished.");
            }
        }

        private void UpdateBannerInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateBannerService.TryOpenDownloadPage(out string errorMessage))
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                UpdateStatusText.Text = errorMessage;
            }
        }

        private void UpdateBannerToggleNotesButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBannerService.ToggleReleaseNotesExpanded();
        }

        private async Task RefreshStartupStateAsync()
        {
            _startupToggleReady = false;
            StartupToggleButton.IsEnabled = false;
            StartupStatusText.Text = "正在检查启动状态...";

            StartupStateInfo state = await _startupService.GetStateAsync();

            if (state.IsEnabled)
            {
                StartupToggleButton.Content = "关闭自启动";
                StartupStatusText.Text = "已启用开机自启动";
            }
            else
            {
                StartupToggleButton.Content = "开启自启动";
                StartupStatusText.Text = state.Description;
            }

            StartupToggleButton.IsEnabled = state.CanToggle;
            _startupToggleReady = true;
        }

        private async void StartupToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_startupToggleReady || _isApplyingStartupChange)
            {
                return;
            }

            _isApplyingStartupChange = true;
            StartupToggleButton.IsEnabled = false;

            try
            {
                StartupStateInfo currentState = await _startupService.GetStateAsync();
                bool shouldEnable = !currentState.IsEnabled;

                StartupStatusText.Text = shouldEnable
                    ? "正在启用开机自启动..."
                    : "正在禁用开机自启动...";

                StartupStateInfo newState = shouldEnable
                    ? await _startupService.EnableAsync()
                    : await _startupService.DisableAsync();

                if (newState.IsEnabled)
                {
                    StartupToggleButton.Content = "关闭自启动";
                    StartupStatusText.Text = "已启用开机自启动";
                }
                else
                {
                    StartupToggleButton.Content = "开启自启动";
                    StartupStatusText.Text = newState.Description;
                }

                StartupToggleButton.IsEnabled = newState.CanToggle;
            }
            finally
            {
                _isApplyingStartupChange = false;
            }
        }

        private void UpdateClipboardStatus()
        {
            HotkeyTrigger trigger = HotkeyConfigService.GetCurrentTrigger();
            if (!_isRecordingHotkey)
            {
                HotkeyCaptureText.Text = HotkeyConfigService.FormatTriggerForDisplay(trigger);
            }

            bool controlsEnabled = !_isApplyingHotkeyChange;
            HotkeyCaptureButton.IsEnabled = controlsEnabled;
            ClearHotkeyButton.IsEnabled = controlsEnabled;

            if (_isApplyingHotkeyChange)
            {
                return;
            }

            HotkeyGuideText.Text = trigger.Type switch
            {
                HotkeyTriggerType.SystemWinV => "可点清除按钮取消。",
                HotkeyTriggerType.KeyChord => "普通快捷键已生效，可点清除按钮取消。",
                HotkeyTriggerType.DoubleTap => "双击触发已生效，可点清除按钮取消。",
                _ => "未设置快捷键，点击右侧按钮开始录入。"
            };
        }

        private void HotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isApplyingHotkeyChange)
            {
                return;
            }

            _isRecordingHotkey = true;
            _captureLastModifierKey = null;
            _captureLastModifierReleaseUtc = DateTime.MinValue;
            HotkeyCaptureText.Text = "按下快捷键...";
            ClipboardStatusText.Text = "请按快捷键（Esc 取消）";
            StartWinVCapture();
            HotkeyCaptureButton.Focus(FocusState.Programmatic);
        }

        private async void ClearHotkeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isApplyingHotkeyChange)
            {
                return;
            }

            HotkeyTrigger current = HotkeyConfigService.GetCurrentTrigger();
            if (current.Type == HotkeyTriggerType.None)
            {
                ClipboardStatusText.Text = "当前未设置快捷键";
                return;
            }

            if (current.Type == HotkeyTriggerType.SystemWinV)
            {
                await RestoreSystemWinVAndClearAsync();
                return;
            }

            await ClearRegularHotkeyAsync();
        }

        private async void SettingsPage_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_isRecordingHotkey || _isApplyingHotkeyChange)
            {
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                EndHotkeyRecording();
                UpdateClipboardStatus();
                return;
            }

            if (IsModifierKey(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (!TryNormalizeLetterKey(e.Key, out string keyToken))
            {
                ClipboardStatusText.Text = "仅支持字母键（A-Z）";
                e.Handled = true;
                return;
            }

            string modifierToken = GetPressedModifierToken();
            if (string.IsNullOrWhiteSpace(modifierToken))
            {
                ClipboardStatusText.Text = "至少需要一个修饰键";
                e.Handled = true;
                return;
            }

            bool containsWin = modifierToken.Contains("Win", StringComparison.OrdinalIgnoreCase);
            if (containsWin)
            {
                if (string.Equals(modifierToken, "Win", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(keyToken, "V", StringComparison.OrdinalIgnoreCase))
                {
                    await ApplySystemWinVFromCaptureAsync();
                }
                else
                {
                    ClipboardStatusText.Text = "Win 组合仅支持 win + v";
                }

                e.Handled = true;
                return;
            }

            await ApplyRegularChordFromCaptureAsync(modifierToken, keyToken);
            e.Handled = true;
        }

        private async void SettingsPage_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (!_isRecordingHotkey || _isApplyingHotkeyChange)
            {
                return;
            }

            string? modifier = NormalizeDoubleTapModifierFromVirtualKey(e.Key);
            if (string.IsNullOrWhiteSpace(modifier))
            {
                return;
            }

            e.Handled = true;
            DateTime now = DateTime.UtcNow;
            bool isDoubleTap = string.Equals(_captureLastModifierKey, modifier, StringComparison.OrdinalIgnoreCase) &&
                               (now - _captureLastModifierReleaseUtc).TotalMilliseconds <= HotkeyTrigger.DefaultDoubleTapIntervalMs;
            if (isDoubleTap)
            {
                await ApplyDoubleTapFromCaptureAsync(modifier);
                return;
            }

            _captureLastModifierKey = modifier;
            _captureLastModifierReleaseUtc = now;
            ClipboardStatusText.Text = $"再按一次 {modifier.ToLowerInvariant()} 以设置双击快捷键";
        }

        private async Task ApplySystemWinVFromCaptureAsync()
        {
            await RunHotkeyChangeAsync(async () =>
            {
                ClipboardStatusText.Text = "正在应用 win + v...";
                EndHotkeyRecording();
                if (Application.Current is App appBefore)
                {
                    appBefore.ReleaseGlobalHotKey();
                }

                bool disabled = ClipboardRegistryService.DisableWinVHotkey_Silent();
                if (!disabled)
                {
                    ClipboardStatusText.Text = "应用 win + v 失败";
                    return;
                }

                ClipboardStatusText.Text = "正在重启 Explorer...";
                await Task.Delay(500);
                bool explorerRestarted = ExplorerRestartService.RestartExplorer();
                if (explorerRestarted)
                {
                    await Task.Delay(1500);
                }

                bool saved = HotkeyConfigService.SaveSystemWinVTrigger();
                if (!saved)
                {
                    ClipboardStatusText.Text = "保存快捷键失败";
                    return;
                }

                if (Application.Current is App appAfter)
                {
                    appAfter.RefreshConfiguredGlobalHotKey();
                }

                ClipboardStatusText.Text = explorerRestarted ? "已应用 win + v" : "已写入 win + v，请稍后手动重启会话";
            });
        }

        private async Task ApplyRegularChordFromCaptureAsync(string modifier, string key)
        {
            await RunHotkeyChangeAsync(async () =>
            {
                EndHotkeyRecording();
                if (!await EnsureSystemWinVRestoredWhenNeededAsync())
                {
                    return;
                }

                var probe = HotKeyService.ProbeRegistration(modifier, key);
                if (!probe.CanRegister)
                {
                    ClipboardStatusText.Text = $"快捷键被占用（{probe.ErrorCode}）";
                    return;
                }

                bool saved = HotkeyConfigService.SaveKeyChordTrigger(modifier, key);
                if (!saved)
                {
                    ClipboardStatusText.Text = "保存快捷键失败";
                    return;
                }

                if (Application.Current is App app)
                {
                    app.RefreshConfiguredGlobalHotKey();
                }

                HotkeyTrigger trigger = HotkeyConfigService.GetCurrentTrigger();
                ClipboardStatusText.Text = $"已应用 {HotkeyConfigService.FormatTriggerForDisplay(trigger)}";
            });
        }

        private async Task ApplyDoubleTapFromCaptureAsync(string doubleTapModifier)
        {
            await RunHotkeyChangeAsync(async () =>
            {
                EndHotkeyRecording();
                if (!await EnsureSystemWinVRestoredWhenNeededAsync())
                {
                    return;
                }

                bool saved = HotkeyConfigService.SaveDoubleTapTrigger(doubleTapModifier, HotkeyTrigger.DefaultDoubleTapIntervalMs);
                if (!saved)
                {
                    ClipboardStatusText.Text = "保存双击快捷键失败";
                    return;
                }

                if (Application.Current is App app)
                {
                    app.RefreshConfiguredGlobalHotKey();
                }

                HotkeyTrigger trigger = HotkeyConfigService.GetCurrentTrigger();
                ClipboardStatusText.Text = $"已应用 {HotkeyConfigService.FormatTriggerForDisplay(trigger)}";
            });
        }

        private async Task RestoreSystemWinVAndClearAsync()
        {
            await RunHotkeyChangeAsync(async () =>
            {
                EndHotkeyRecording();
                ClipboardStatusText.Text = "正在恢复系统 win + v...";
                if (Application.Current is App appBefore)
                {
                    appBefore.ReleaseGlobalHotKey();
                }

                bool restored = ClipboardRegistryService.RestoreSystemClipboard_WithRestart();
                if (restored)
                {
                    await Task.Delay(1200);
                }

                bool cleared = HotkeyConfigService.ClearTrigger();
                if (Application.Current is App appAfter)
                {
                    appAfter.RefreshConfiguredGlobalHotKey();
                }

                ClipboardStatusText.Text = restored && cleared
                    ? "已取消快捷键，系统 win + v 已恢复"
                    : "已取消快捷键，但系统 win + v 可能尚未恢复";
            });
        }

        private async Task ClearRegularHotkeyAsync()
        {
            await RunHotkeyChangeAsync(async () =>
            {
                EndHotkeyRecording();
                if (Application.Current is App appBefore)
                {
                    appBefore.ReleaseGlobalHotKey();
                }

                bool cleared = HotkeyConfigService.ClearTrigger();
                if (Application.Current is App appAfter)
                {
                    appAfter.RefreshConfiguredGlobalHotKey();
                }

                ClipboardStatusText.Text = cleared ? "已取消快捷键" : "取消快捷键失败";
                await Task.CompletedTask;
            });
        }

        private async Task<bool> EnsureSystemWinVRestoredWhenNeededAsync()
        {
            HotkeyTrigger current = HotkeyConfigService.GetCurrentTrigger();
            bool shouldRestore = current.Type == HotkeyTriggerType.SystemWinV ||
                                 (!ClipboardRegistryService.GetPreferSystemWinV() && ClipboardRegistryService.IsWinVDisabled());
            if (!shouldRestore)
            {
                return true;
            }

            ClipboardStatusText.Text = "正在恢复系统 win + v...";
            if (Application.Current is App app)
            {
                app.ReleaseGlobalHotKey();
            }

            bool restored = ClipboardRegistryService.RestoreSystemClipboard_WithRestart();
            if (restored)
            {
                await Task.Delay(1200);
                return true;
            }

            ClipboardStatusText.Text = "恢复系统 win + v 失败";
            return false;
        }

        private async Task RunHotkeyChangeAsync(Func<Task> action)
        {
            if (_isApplyingHotkeyChange)
            {
                return;
            }

            _isApplyingHotkeyChange = true;
            UpdateClipboardStatus();
            try
            {
                await action();
            }
            finally
            {
                _isApplyingHotkeyChange = false;
                UpdateClipboardStatus();
            }
        }

        private static bool IsModifierKey(VirtualKey key)
        {
            return key == VirtualKey.Control ||
                   key == VirtualKey.Menu ||
                   key == VirtualKey.Shift ||
                   key == VirtualKey.LeftWindows ||
                   key == VirtualKey.RightWindows;
        }

        private static string? NormalizeDoubleTapModifierFromVirtualKey(VirtualKey key)
        {
            return key switch
            {
                VirtualKey.Control => "Ctrl",
                VirtualKey.Menu => "Alt",
                VirtualKey.Shift => "Shift",
                _ => null
            };
        }

        private static bool TryNormalizeLetterKey(VirtualKey key, out string keyToken)
        {
            keyToken = string.Empty;
            if (key < VirtualKey.A || key > VirtualKey.Z)
            {
                return false;
            }

            keyToken = ((char)key).ToString();
            return true;
        }

        private static string GetPressedModifierToken()
        {
            bool ctrlDown = IsKeyDown(VirtualKey.Control);
            bool altDown = IsKeyDown(VirtualKey.Menu);
            bool shiftDown = IsKeyDown(VirtualKey.Shift);
            bool winDown = IsKeyDown(VirtualKey.LeftWindows) || IsKeyDown(VirtualKey.RightWindows);

            string[] ordered = new[]
            {
                ctrlDown ? "Ctrl" : string.Empty,
                altDown ? "Alt" : string.Empty,
                shiftDown ? "Shift" : string.Empty,
                winDown ? "Win" : string.Empty
            };

            return string.Join("+", ordered.Where(t => !string.IsNullOrWhiteSpace(t)));
        }

        private static bool IsKeyDown(VirtualKey key)
        {
            CoreVirtualKeyStates state = InputKeyboardSource.GetKeyStateForCurrentThread(key);
            return (state & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;
        }

        private void StartWinVCapture()
        {
            StopWinVCapture();
            try
            {
                WinVHotkeyCaptureService captureService = new();
                if (!captureService.IsActive)
                {
                    captureService.Dispose();
                    return;
                }

                captureService.WinVPressed += WinVCaptureService_WinVPressed;
                _winVCaptureService = captureService;
            }
            catch
            {
            }
        }

        private void StopWinVCapture()
        {
            if (_winVCaptureService == null)
            {
                return;
            }

            _winVCaptureService.WinVPressed -= WinVCaptureService_WinVPressed;
            _winVCaptureService.Dispose();
            _winVCaptureService = null;
        }

        private void EndHotkeyRecording()
        {
            _isRecordingHotkey = false;
            _captureLastModifierKey = null;
            _captureLastModifierReleaseUtc = DateTime.MinValue;
            StopWinVCapture();
        }

        private void WinVCaptureService_WinVPressed(object? sender, EventArgs e)
        {
            if (!_isRecordingHotkey || _isApplyingHotkeyChange || DispatcherQueue == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(async () =>
            {
                if (!_isRecordingHotkey || _isApplyingHotkeyChange)
                {
                    return;
                }

                await ApplySystemWinVFromCaptureAsync();
            });
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
        }

        private void LoadPopupPlacementMode()
        {
            _isLoadingPopupPlacement = true;
            try
            {
                PopupPlacementMode mode = PopupPlacementConfigService.GetMode();
                FollowMousePlacementRadioButton.IsChecked = mode == PopupPlacementMode.FollowMouse;
                FreeDragPlacementRadioButton.IsChecked = mode == PopupPlacementMode.FreeDrag;
                UpdatePopupPlacementStatusText(mode);
            }
            finally
            {
                _isLoadingPopupPlacement = false;
            }
        }

        private void PopupPlacementRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (_isLoadingPopupPlacement)
            {
                return;
            }

            PopupPlacementMode mode = FreeDragPlacementRadioButton?.IsChecked == true
                ? PopupPlacementMode.FreeDrag
                : PopupPlacementMode.FollowMouse;

            bool saved = PopupPlacementConfigService.SetMode(mode);
            if (!saved)
            {
                PopupPlacementStatusText.Text = "保存弹窗方式失败，请稍后重试。";
                return;
            }

            UpdatePopupPlacementStatusText(mode);
        }

        private void UpdatePopupPlacementStatusText(PopupPlacementMode mode)
        {
            if (mode == PopupPlacementMode.FollowMouse)
            {
                PopupPlacementStatusText.Text = "当前方式：跟随鼠标位置。";
                return;
            }

            bool hasSavedPosition = PopupPlacementConfigService.TryGetFreeDragPosition(out _, out _);
            PopupPlacementStatusText.Text = hasSavedPosition
                ? "当前方式：自由拖动（已记住上次停留位置）。"
                : "当前方式：自由拖动（首次弹出仍会跟随鼠标，拖动后开始记忆）。";
        }

        private void RefreshDataStorageCardState()
        {
            string currentPath = StoragePathService.GetCurrentDataRoot();
            DataStoragePathText.Text = $"当前路径：{currentPath}";

            string? pendingTarget = StoragePathService.GetPendingMigrationTargetRoot();
            if (!string.IsNullOrWhiteSpace(pendingTarget))
            {
                DataStorageStatusText.Text = $"已设置迁移目标：{pendingTarget}。重启应用后执行。";
                return;
            }

            if (string.IsNullOrWhiteSpace(DataStorageStatusText.Text))
            {
                DataStorageStatusText.Text = "提示：迁移将在重启应用后执行，旧目录不会自动删除。";
            }
        }

        private async void ChangeDataStoragePathButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataOperationRunning)
            {
                return;
            }

            StorageFolder? folder = await PickFolderAsync();
            if (folder == null)
            {
                return;
            }

            bool queued = StoragePathService.QueueMigration(folder.Path, out string message);
            DataStorageStatusText.Text = message;
            RefreshDataStorageCardState();

            if (queued && Application.Current is App app)
            {
                app.RestartApplicationFromSettings();
            }
        }

        private void OpenDataStorageFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string folderPath = StoragePathService.GetCurrentDataRoot();
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderPath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                DataStorageStatusText.Text = $"打开目录失败：{ex.Message}";
            }
        }

        private async void ExportJsonPackageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataOperationRunning)
            {
                return;
            }

            StorageFile? targetFile = await PickSaveZipFileAsync($"liuyun-json-{DateTime.Now:yyyyMMdd-HHmmss}");
            if (targetFile == null)
            {
                return;
            }

            await RunDataOperationAsync(
                async () =>
                {
                    await DataExchangeService.ExportJsonPackageAsync(targetFile.Path);
                    DataExchangeStatusText.Text = $"JSON 导出完成：{targetFile.Path}";
                },
                ex => DataExchangeStatusText.Text = $"JSON 导出失败：{ex.Message}");
        }

        private async void ImportDataPackageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDataOperationRunning)
            {
                return;
            }

            try
            {
                StorageFile? sourceFile = await PickOpenZipFileAsync();
                if (sourceFile == null)
                {
                    return;
                }

                await RunDataOperationAsync(
                    async () =>
                    {
                        await DataExchangeService.ImportJsonPackageAsync(sourceFile.Path);
                        if (Application.Current is App app)
                        {
                            app.NotifyClipboardDataImported();
                        }
                        DataExchangeStatusText.Text = $"JSON 导入完成：{sourceFile.Path}";
                    },
                    ex => DataExchangeStatusText.Text = $"JSON 导入失败：{ex.Message}");
            }
            catch (Exception ex)
            {
                DataExchangeStatusText.Text = $"导入准备失败：{ex.Message}";
            }
        }

        private async Task RunDataOperationAsync(Func<Task> operation, Action<Exception> onError)
        {
            if (_isDataOperationRunning)
            {
                return;
            }

            _isDataOperationRunning = true;
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                onError(ex);
            }
            finally
            {
                _isDataOperationRunning = false;
            }
        }

        private async Task<StorageFolder?> PickFolderAsync()
        {
            FolderPicker picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add("*");
            InitializePickerWithMainWindow(picker);
            return await picker.PickSingleFolderAsync();
        }

        private async Task<StorageFile?> PickSaveZipFileAsync(string suggestedFileName)
        {
            FileSavePicker picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedFileName
            };
            picker.FileTypeChoices.Add("LiuYun 数据包", new List<string> { ".zip" });
            InitializePickerWithMainWindow(picker);
            return await picker.PickSaveFileAsync();
        }

        private async Task<StorageFile?> PickOpenZipFileAsync()
        {
            FileOpenPicker picker = new FileOpenPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary
            };
            picker.FileTypeFilter.Add(".zip");
            InitializePickerWithMainWindow(picker);
            return await picker.PickSingleFileAsync();
        }

        private static void InitializePickerWithMainWindow(object picker)
        {
            IntPtr hwnd = GetMainWindowHandle();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }

            InitializeWithWindow.Initialize(picker, hwnd);
        }

        private static IntPtr GetMainWindowHandle()
        {
            Window? window = App.MainWindow;
            if (window == null)
            {
                return IntPtr.Zero;
            }

            return WindowNative.GetWindowHandle(window);
        }

        private void LoadClipboardImageCleanupSetting()
        {
            if (ClipboardImageCleanupComboBox == null)
            {
                return;
            }

            _isLoadingClipboardImageCleanupSetting = true;

            ClipboardImageCleanupRetention retention = ClipboardImageCleanupConfigService.GetRetention();
            string targetTag = retention.ToString();

            for (int i = 0; i < ClipboardImageCleanupComboBox.Items.Count; i++)
            {
                if (ClipboardImageCleanupComboBox.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Tag?.ToString(), targetTag, StringComparison.OrdinalIgnoreCase))
                {
                    ClipboardImageCleanupComboBox.SelectedIndex = i;
                    _isLoadingClipboardImageCleanupSetting = false;
                    return;
                }
            }

            ClipboardImageCleanupComboBox.SelectedIndex = ClipboardImageCleanupComboBox.Items.Count - 1;
            _isLoadingClipboardImageCleanupSetting = false;
        }

        private async void ClipboardImageCleanupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingClipboardImageCleanupSetting ||
                sender is not ComboBox comboBox ||
                comboBox.SelectedItem is not ComboBoxItem selectedItem ||
                selectedItem.Tag is not string tag)
            {
                return;
            }

            if (!Enum.TryParse(tag, out ClipboardImageCleanupRetention retention))
            {
                return;
            }

            bool saved = ClipboardImageCleanupConfigService.SetRetention(retention);
            if (!saved || retention == ClipboardImageCleanupRetention.None)
            {
                return;
            }

            await Task.Run(() => ClipboardImageCleanupService.CleanupOrphanedImagesByRetention(retention));
        }

    }
}


