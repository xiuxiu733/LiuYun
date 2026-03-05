using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace LiuYun.Services
{
    public sealed class UpdateBannerSnapshot
    {
        public bool HasUpdate { get; init; }
        public bool IsVisible { get; init; }
        public bool IsChecking { get; init; }
        public bool StartupCheckFailed { get; init; }
        public bool NotesExpanded { get; init; }
        public string CurrentVersion { get; init; } = string.Empty;
        public string RemoteVersion { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public string StatusMessage { get; init; } = string.Empty;
    }

    public static class UpdateBannerService
    {
        public const string ManualDownloadUrl = "https://github.com/xiuxiu733/LiuYun/releases";

        private static readonly object StateLock = new();
        private static readonly SemaphoreSlim UpdateOperationLock = new(1, 1);
        private static readonly UpdateService UpdateService = new();

        private static bool _startupCheckStarted;
        private static bool _startupCheckFailed;
        private static bool _isDismissedInClipboardForProcess;
        private static bool _isChecking;
        private static bool _notesExpanded;
        private static string _currentVersion = string.Empty;
        private static string _remoteVersion = string.Empty;
        private static string _notes = string.Empty;
        private static string _statusMessage = string.Empty;
        private static UpdateManifest? _manifest;

        public static event EventHandler? StateChanged;

        public static string GetCurrentVersionString()
        {
            string? informationalVersion = Assembly
                .GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                int metadataSeparator = informationalVersion.IndexOf('+');
                return metadataSeparator > 0
                    ? informationalVersion.Substring(0, metadataSeparator)
                    : informationalVersion;
            }

            Version? assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion != null)
            {
                int build = Math.Max(assemblyVersion.Build, 0);
                return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{build}";
            }

            return "unknown";
        }

        public static void EnsureStartupCheckStarted(string currentVersion)
        {
            bool shouldStart;
            lock (StateLock)
            {
                shouldStart = !_startupCheckStarted;
                if (shouldStart)
                {
                    _startupCheckStarted = true;
                }
            }

            if (!shouldStart)
            {
                return;
            }

            _ = CheckForUpdatesInternalAsync(currentVersion, isStartup: true);
        }

        public static Task<UpdateCheckResult> CheckForUpdatesByUserAsync(string currentVersion)
        {
            return CheckForUpdatesInternalAsync(currentVersion, isStartup: false);
        }

        public static UpdateBannerSnapshot GetSnapshot(bool forClipboard = false)
        {
            lock (StateLock)
            {
                bool hasUpdate = _manifest != null && !string.IsNullOrWhiteSpace(_remoteVersion);
                bool isVisible = hasUpdate && (!forClipboard || !_isDismissedInClipboardForProcess);
                return new UpdateBannerSnapshot
                {
                    HasUpdate = hasUpdate,
                    IsVisible = isVisible,
                    IsChecking = _isChecking,
                    StartupCheckFailed = _startupCheckFailed,
                    NotesExpanded = _notesExpanded,
                    CurrentVersion = _currentVersion,
                    RemoteVersion = _remoteVersion,
                    Notes = _notes,
                    StatusMessage = _statusMessage
                };
            }
        }

        public static bool TryOpenDownloadPage(out string errorMessage)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ManualDownloadUrl,
                    UseShellExecute = true
                });
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                UpdateDiagnostics.Log("UpdateBanner.DownloadPage", $"Open url failed: {ex}");
                errorMessage = $"打开下载页面失败：{ex.Message}";
                return false;
            }
        }

        public static void DismissInClipboardForCurrentProcess()
        {
            lock (StateLock)
            {
                _isDismissedInClipboardForProcess = true;
            }

            RaiseStateChanged();
        }

        public static void ToggleReleaseNotesExpanded()
        {
            lock (StateLock)
            {
                _notesExpanded = !_notesExpanded;
            }

            RaiseStateChanged();
        }

        public static void CollapseReleaseNotes()
        {
            bool changed = false;
            lock (StateLock)
            {
                if (_notesExpanded)
                {
                    _notesExpanded = false;
                    changed = true;
                }
            }

            if (changed)
            {
                RaiseStateChanged();
            }
        }

        private static async Task<UpdateCheckResult> CheckForUpdatesInternalAsync(string currentVersion, bool isStartup)
        {
            await UpdateOperationLock.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (StateLock)
                {
                    _isChecking = true;
                    _currentVersion = currentVersion;
                    if (!isStartup)
                    {
                        _statusMessage = "正在检查更新...";
                    }
                }

                RaiseStateChanged();

                UpdateCheckResult result = await UpdateService
                    .CheckForUpdatesAsync(currentVersion)
                    .ConfigureAwait(false);

                lock (StateLock)
                {
                    _isChecking = false;
                    if (!result.Success)
                    {
                        if (isStartup)
                        {
                            _startupCheckFailed = true;
                        }
                        else
                        {
                            _statusMessage = $"检查失败：{result.ErrorMessage}";
                        }
                    }
                    else
                    {
                        _startupCheckFailed = false;
                        if (result.HasUpdate && result.Manifest != null)
                        {
                            bool versionChanged = !string.Equals(
                                _remoteVersion,
                                result.RemoteVersion,
                                StringComparison.OrdinalIgnoreCase);

                            _remoteVersion = result.RemoteVersion;
                            _notes = string.IsNullOrWhiteSpace(result.Manifest.Notes)
                                ? "暂无更新说明"
                                : result.Manifest.Notes;
                            _manifest = result.Manifest;
                            _statusMessage = $"发现新版本：{result.RemoteVersion}。";

                            if (versionChanged)
                            {
                                _isDismissedInClipboardForProcess = false;
                                _notesExpanded = false;
                            }
                        }
                        else
                        {
                            _remoteVersion = string.Empty;
                            _notes = string.Empty;
                            _manifest = null;
                            _notesExpanded = false;
                            if (!isStartup)
                            {
                                _statusMessage = $"当前已是最新版本（{currentVersion}）。";
                            }
                        }
                    }
                }

                RaiseStateChanged();
                return result;
            }
            catch (Exception ex)
            {
                UpdateCheckResult fail = UpdateCheckResult.CreateFail(ex.Message);
                lock (StateLock)
                {
                    _isChecking = false;
                    if (isStartup)
                    {
                        _startupCheckFailed = true;
                    }
                    else
                    {
                        _statusMessage = $"检查失败：{ex.Message}";
                    }
                }

                RaiseStateChanged();
                return fail;
            }
            finally
            {
                UpdateOperationLock.Release();
            }
        }

        private static void RaiseStateChanged()
        {
            try
            {
                StateChanged?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
            }
        }
    }
}
