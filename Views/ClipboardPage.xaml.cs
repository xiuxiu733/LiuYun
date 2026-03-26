using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.Storage;
using Windows.Storage.Streams;
using LiuYun.Models;
using LiuYun.Services;

namespace LiuYun.Views
{
    public sealed partial class ClipboardPage : Page, INotifyPropertyChanged, IBackgroundAwarePage
    {
        private enum ClipboardListMode
        {
            History,
            Favorite
        }

        private const int ClipboardLoadLimit = 300;
        private const double InlineActionPanelOffset = 112;
        private const int InlineActionAnimationMs = 180;
        private const string FavoriteImageFolderName = "favorite_images";
        private const int FavoriteItemsLoadLimit = 300;
        private AllClipboardItems clipboardModel => App.ClipboardModel;
        private Border? _currentOpenDeleteButton = null;
        private Border? _currentOpenMainCard = null;
        private Border? _lastClosingDeleteButton = null;
        private bool _hasLoadedFromDatabase = false;
        private bool _isLoading = false;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _currentDeleteStoryboard;
        private bool _isHostWindowVisible = true;
        private bool _pendingRefreshWhileHidden;
        private ClipboardItem? _keyboardSelectedItem;
        private bool _isSubmittingKeyboardSelection;
        private bool _forceSelectFirstOnNextRefresh = true;
        private ClipboardListMode _currentListMode = ClipboardListMode.History;
        private readonly ObservableCollection<ClipboardItem> _favoriteItems = new ObservableCollection<ClipboardItem>();
        private readonly KeyEventHandler _previewKeyDownHandler;
        private bool _favoriteItemsLoaded;
        private bool _isBulkReloadingFromImport;
        private bool _subscriptionsAttached;
        private bool _updateBannerSubscribed;
        private bool _isRefreshingFilteredItems;
        private bool _pendingRefreshAfterCurrentPass;
        private double? _savedClipboardScrollOffset = null;
        private readonly List<ClipboardItem> _orderedFilteredItems = new List<ClipboardItem>();
        private static readonly bool ManualRepeaterSourceToggleEnabled = false;

        public event PropertyChangedEventHandler? PropertyChanged;

        private string _searchText = string.Empty;
        private DateTime? _filterStartTime;
        private DateTime? _filterEndTime;
        private ClipboardCategoryFilter _categoryFilter = ClipboardFilterState.Current;
        public ObservableCollection<ClipboardItem> FilteredItems { get; } = new ObservableCollection<ClipboardItem>();
        public Visibility HistoryEmptyHintVisibility => _currentListMode == ClipboardListMode.History && FilteredItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        public string ContentPanelBackground => "#15000000";
        public string ListModeToggleGlyph => _currentListMode == ClipboardListMode.History ? "\uE734" : "\uE8FD";
        public string ListModeToggleTooltip => _currentListMode == ClipboardListMode.History ? "切换到常用 (→)" : "切换到历史 (←)";
        public string FavoriteQuickButtonText => _currentListMode == ClipboardListMode.History ? "存常用" : "取消常用";

        public ClipboardPage()
        {
            this.InitializeComponent();
            _previewKeyDownHandler = ClipboardPage_KeyDown;
            AttachRepeaterItemsSource();
            Loaded += ClipboardPage_Loaded;
            Unloaded += ClipboardPage_Unloaded;

            _filterStartTime = ClipboardTimeFilterState.StartTime;
            _filterEndTime = ClipboardTimeFilterState.EndTime;
            AttachPageSubscriptions();
        }

        private void AttachPageSubscriptions()
        {
            if (_subscriptionsAttached)
            {
                return;
            }

            AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler, true);
            if (clipboardModel?.Items != null)
            {
                clipboardModel.Items.CollectionChanged += Items_CollectionChanged;
            }

            ClipboardFilterState.FilterChanged += ClipboardFilterState_FilterChanged;
            ClipboardTimeFilterState.FilterChanged += ClipboardTimeFilterState_FilterChanged;
            if (App.Current is App app)
            {
                app.ClipboardDataReloading += App_ClipboardDataReloading;
                app.ClipboardDataReplaced += App_ClipboardDataReplaced;
            }
            _subscriptionsAttached = true;
        }

        private void DetachPageSubscriptions()
        {
            if (!_subscriptionsAttached)
            {
                return;
            }

            RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler);
            if (clipboardModel?.Items != null)
            {
                clipboardModel.Items.CollectionChanged -= Items_CollectionChanged;
            }

            ClipboardFilterState.FilterChanged -= ClipboardFilterState_FilterChanged;
            ClipboardTimeFilterState.FilterChanged -= ClipboardTimeFilterState_FilterChanged;
            if (App.Current is App app)
            {
                app.ClipboardDataReloading -= App_ClipboardDataReloading;
                app.ClipboardDataReplaced -= App_ClipboardDataReplaced;
            }
            _subscriptionsAttached = false;
        }

        private void SyncFilterStateFromGlobal()
        {
            _categoryFilter = ClipboardFilterState.Current;
            _filterStartTime = ClipboardTimeFilterState.StartTime;
            _filterEndTime = ClipboardTimeFilterState.EndTime;
            // sync quick category dropdown selection
            UpdateQuickFilterButtonVisual(_categoryFilter);
        }

        public void OnHostWindowVisibilityChanged(bool isVisible)
        {
            bool effectiveVisible = isVisible;
            if (App.Current is App app)
            {
                effectiveVisible = app.IsMainWindowVisible;
                if (effectiveVisible != isVisible)
                {
                    Debug.WriteLine($"ClipboardPage visibility corrected: requested={isVisible}, effective={effectiveVisible}");
                }
            }

            if (_isHostWindowVisible == effectiveVisible)
            {
                return;
            }

            _isHostWindowVisible = effectiveVisible;
            if (!IsLoaded)
            {
                return;
            }

            if (effectiveVisible)
            {
                _forceSelectFirstOnNextRefresh = true;
                RestoreFromBackgroundMemorySavingMode();
            }
            else
            {
                EnterBackgroundMemorySavingMode();
            }
        }

        private void EnterBackgroundMemorySavingMode()
        {
            _pendingRefreshWhileHidden = true;
            CloseAllDeleteButtons();
            SetKeyboardSelectedItem(null);
            ClipboardItem.SetThumbnailLoadingEnabled(false);
            DetachRepeaterItemsSource();
            FilteredItems.Clear();
            _orderedFilteredItems.Clear();
            TrimClipboardImageCaches();
        }

        private void RestoreFromBackgroundMemorySavingMode()
        {
            ClipboardItem.SetThumbnailLoadingEnabled(true);
            AttachRepeaterItemsSource();

            if (App.Current is App app && app.ConsumeClipboardHistoryDirtyFlag())
            {
                _isBulkReloadingFromImport = true;
                try
                {
                    clipboardModel.LoadItems();
                }
                finally
                {
                    _isBulkReloadingFromImport = false;
                }

                _pendingRefreshWhileHidden = true;
            }

            if (_pendingRefreshWhileHidden || FilteredItems.Count == 0)
            {
                RefreshFilteredItems();
                return;
            }

            ApplyKeyboardSelectionAfterRefresh();
        }

        private void AttachRepeaterItemsSource()
        {
            if (!ManualRepeaterSourceToggleEnabled)
            {
                return;
            }

            if (ClipboardItemsRepeater != null &&
                !ReferenceEquals(ClipboardItemsRepeater.ItemsSource, FilteredItems))
            {
                ClipboardItemsRepeater.ItemsSource = FilteredItems;
            }
        }

        private void DetachRepeaterItemsSource()
        {
            if (!ManualRepeaterSourceToggleEnabled)
            {
                return;
            }

            if (ClipboardItemsRepeater != null &&
                ClipboardItemsRepeater.ItemsSource != null)
            {
                ClipboardItemsRepeater.ItemsSource = null;
            }
        }

        private void TrimClipboardImageCaches()
        {
            if (clipboardModel?.Items != null)
            {
                foreach (var imageItem in clipboardModel.Items.Where(i => i.ContentType == ClipboardContentType.Image))
                {
                    imageItem.ClearImageCache();
                }
            }

            ClipboardItem.ClearGlobalImageCache();
        }

        private void RefreshOverlayBrushForClipboardTransientSurfaces()
        {
            if (App.Current is App app && app.m_window is MainWindow mainWindow)
            {
                mainWindow.RefreshOverlayBrushForTransientSurfaces();
            }
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

            _ = DispatcherQueue.TryEnqueue(() => ApplyUpdateBannerState(UpdateBannerService.GetSnapshot(forClipboard: true)));
        }

        private void ApplyUpdateBannerState(UpdateBannerSnapshot state)
        {
            if (UpdateBannerContainer == null)
            {
                return;
            }

            bool wasBannerVisible = UpdateBannerContainer.Visibility == Visibility.Visible;
            bool wasDetailsVisible = UpdateBannerDetailsCard.Visibility == Visibility.Visible;
            bool wasStatusPanelVisible = UpdateBannerStatusPanel.Visibility == Visibility.Visible;

            UpdateBannerContainer.Visibility = state.IsVisible ? Visibility.Visible : Visibility.Collapsed;
            if (!state.IsVisible)
            {
                UpdateBannerDetailsCard.Visibility = Visibility.Collapsed;
                UpdateBannerStatusPanel.Visibility = Visibility.Collapsed;
                if (wasBannerVisible || wasDetailsVisible || wasStatusPanelVisible)
                {
                    ClampClipboardScrollOffset();
                }

                return;
            }

            UpdateBannerTitleText.Text = $"发现新版本 {state.RemoteVersion}";
            UpdateBannerSubtitleText.Visibility = Visibility.Collapsed;

            bool hasNotes = !string.IsNullOrWhiteSpace(state.Notes);
            UpdateBannerToggleNotesButton.Visibility = hasNotes ? Visibility.Visible : Visibility.Collapsed;
            UpdateBannerDetailsCard.Visibility = hasNotes && state.NotesExpanded ? Visibility.Visible : Visibility.Collapsed;
            UpdateBannerToggleGlyph.Glyph = state.NotesExpanded ? "\uE70E" : "\uE70D";
            UpdateBannerDetailsVersionText.Text = $"LiuYun {state.RemoteVersion}";
            UpdateBannerDetailsNotesText.Text = FormatReleaseNotes(state.Notes);

            UpdateBannerInstallButton.IsEnabled = true;
            UpdateBannerInstallText.Text = "前往GitHub下载";

            UpdateBannerCloseButton.IsEnabled = true;
            UpdateBannerStatusPanel.Visibility = Visibility.Collapsed;
            UpdateBannerProgressBar.IsIndeterminate = false;
            UpdateBannerProgressBar.Value = 0;
            UpdateBannerProgressBar.Visibility = Visibility.Collapsed;

            bool isBannerVisible = UpdateBannerContainer.Visibility == Visibility.Visible;
            bool isDetailsVisible = UpdateBannerDetailsCard.Visibility == Visibility.Visible;
            bool isStatusPanelVisible = UpdateBannerStatusPanel.Visibility == Visibility.Visible;
            if (wasBannerVisible != isBannerVisible ||
                wasDetailsVisible != isDetailsVisible ||
                wasStatusPanelVisible != isStatusPanelVisible)
            {
                ClampClipboardScrollOffset();
            }
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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private static string FavoriteImageFolderPath => Path.Combine(DatabaseService.AppDataFolder, FavoriteImageFolderName);

        private IEnumerable<ClipboardItem> GetCurrentSourceItems()
        {
            if (_currentListMode == ClipboardListMode.Favorite)
            {
                return _favoriteItems;
            }

            return clipboardModel?.Items ?? Enumerable.Empty<ClipboardItem>();
        }

        private void NotifyListModeChanged()
        {
            OnPropertyChanged(nameof(ListModeToggleGlyph));
            OnPropertyChanged(nameof(ListModeToggleTooltip));
            OnPropertyChanged(nameof(FavoriteQuickButtonText));
            OnPropertyChanged(nameof(HistoryEmptyHintVisibility));
            // show filter button only in history mode
            try
            {
                if (QuickFilterButton != null)
                {
                    QuickFilterButton.Visibility = _currentListMode == ClipboardListMode.History ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            catch
            {
                // ignore if UI not ready
            }
        }

        private void UpdateInlineFavoriteButtonText(Border deleteButton)
        {
            if (deleteButton.Child is not StackPanel actionsPanel ||
                actionsPanel.Children.Count == 0 ||
                actionsPanel.Children[0] is not Button favoriteButton ||
                favoriteButton.Content is not StackPanel favoriteContentPanel)
            {
                return;
            }

            foreach (var child in favoriteContentPanel.Children)
            {
                if (child is TextBlock textBlock)
                {
                    textBlock.Text = FavoriteQuickButtonText;
                    break;
                }
            }
        }

        private static bool IsPathUnderFolder(string path, string folderPath)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folderPath))
            {
                return false;
            }

            try
            {
                string fullFilePath = Path.GetFullPath(path);
                string fullFolderPath = Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return fullFilePath.StartsWith(fullFolderPath, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string CopyImageToFavoriteStorage(string sourceImagePath)
        {
            Directory.CreateDirectory(FavoriteImageFolderPath);
            string extension = Path.GetExtension(sourceImagePath);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            string targetFileName = $"{Guid.NewGuid():N}{extension}";
            string targetPath = Path.Combine(FavoriteImageFolderPath, targetFileName);
            File.Copy(sourceImagePath, targetPath, true);
            return targetPath;
        }

        private static void TryDeleteFavoriteImageFile(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || !IsPathUnderFolder(imagePath, FavoriteImageFolderPath))
            {
                return;
            }

            try
            {
                if (File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete favorite image file: {ex.Message}");
            }
        }

        private async Task EnsureFavoriteItemsLoadedAsync(bool forceReload = false)
        {
            if (_favoriteItemsLoaded && !forceReload)
            {
                return;
            }

            var items = await Task.Run(() =>
            {
                try
                {
                    return DatabaseService.GetAllFavoriteClipboardItems(FavoriteItemsLoadLimit);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load favorite clipboard items: {ex.Message}");
                    return new List<ClipboardItem>();
                }
            });

            _favoriteItems.Clear();
            foreach (var item in items)
            {
                _favoriteItems.Add(item);
            }

            _favoriteItemsLoaded = true;
        }

        private async Task WarmupFavoriteItemsAsync()
        {
            try
            {
                await EnsureFavoriteItemsLoadedAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WarmupFavoriteItemsAsync failed: {ex.Message}");
            }
        }

        private async Task SwitchListModeAsync(ClipboardListMode mode)
        {
            if (_currentListMode == mode)
            {
                return;
            }

            CloseAllDeleteButtons();
            SetKeyboardSelectedItem(null);

            _currentListMode = mode;
            NotifyListModeChanged();
            _forceSelectFirstOnNextRefresh = true;

            if (_currentListMode == ClipboardListMode.Favorite)
            {
                await EnsureFavoriteItemsLoadedAsync();
            }

            RefreshFilteredItems();
        }

        private async Task<bool> AddToFavoritesAsync(ClipboardItem sourceItem)
        {
            var favoriteItem = new ClipboardItem
            {
                ContentType = sourceItem.ContentType,
                TextContent = sourceItem.TextContent,
                Timestamp = DateTime.Now,
                ContentHash = sourceItem.ContentHash,
                IsPinned = false
            };

            if (sourceItem.ContentType == ClipboardContentType.Image)
            {
                if (string.IsNullOrWhiteSpace(sourceItem.ImagePath) || !File.Exists(sourceItem.ImagePath))
                {
                    return false;
                }

                try
                {
                    favoriteItem.ImagePath = CopyImageToFavoriteStorage(sourceItem.ImagePath);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to copy image to favorite storage: {ex.Message}");
                    return false;
                }
            }
            else
            {
                favoriteItem.ImagePath = string.Empty;
            }

            try
            {
                long id = await Task.Run(() => DatabaseService.InsertFavoriteClipboardItem(favoriteItem));
                favoriteItem.Id = (int)id;
                favoriteItem.WarmupSemanticInfo();
                _favoriteItems.Insert(0, favoriteItem);
                if (_favoriteItems.Count > FavoriteItemsLoadLimit)
                {
                    var overflowItem = _favoriteItems[_favoriteItems.Count - 1];
                    if (overflowItem.ContentType == ClipboardContentType.Image)
                    {
                        overflowItem.ClearImageCache();
                    }
                    if (ReferenceEquals(_keyboardSelectedItem, overflowItem))
                    {
                        SetKeyboardSelectedItem(null);
                    }

                    _favoriteItems.RemoveAt(_favoriteItems.Count - 1);
                }

                if (_currentListMode == ClipboardListMode.Favorite)
                {
                    RefreshFilteredItems();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to save favorite clipboard item: {ex.Message}");
                TryDeleteFavoriteImageFile(favoriteItem.ImagePath);
                return false;
            }
        }

        private async Task<bool> RemoveFromFavoritesAsync(ClipboardItem favoriteItem, bool refreshList = true)
        {
            try
            {
                // prevent other flows from forcing selection to first on next refresh
                _forceSelectFirstOnNextRefresh = false;

                await Task.Run(() => DatabaseService.DeleteFavoriteClipboardItem(favoriteItem.Id));
                TryDeleteFavoriteImageFile(favoriteItem.ImagePath);

                if (favoriteItem.ContentType == ClipboardContentType.Image)
                {
                    favoriteItem.ClearImageCache();
                }

                // record neighbor selection to preserve viewport
                int filteredIndex = -1;
                ClipboardItem? neighborSelection = null;
                try
                {
                    if (FilteredItems != null)
                    {
                        filteredIndex = FilteredItems.IndexOf(favoriteItem);
                        if (filteredIndex >= 0)
                        {
                            if (filteredIndex < FilteredItems.Count - 1)
                            {
                                neighborSelection = FilteredItems[filteredIndex + 1];
                            }
                            else if (filteredIndex - 1 >= 0)
                            {
                                neighborSelection = FilteredItems[filteredIndex - 1];
                            }
                        }
                    }
                }
                catch
                {
                    // ignore concurrency issues when reading FilteredItems
                }

                _favoriteItems.Remove(favoriteItem);

                if (ReferenceEquals(_keyboardSelectedItem, favoriteItem))
                {
                    SetKeyboardSelectedItem(neighborSelection);
                }

                if (refreshList && _currentListMode == ClipboardListMode.Favorite)
                {
                    RefreshFilteredItems();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove favorite clipboard item: {ex.Message}");
                return false;
            }
        }

        private void ClipboardPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_currentDeleteStoryboard != null)
            {
                _currentDeleteStoryboard.Stop();
                _currentDeleteStoryboard.Children.Clear();
                _currentDeleteStoryboard = null;
            }

            _currentOpenDeleteButton = null;
            _currentOpenMainCard = null;
            DetachPageSubscriptions();

            _isHostWindowVisible = false;
            _pendingRefreshWhileHidden = false;
            _forceSelectFirstOnNextRefresh = true;
            SetKeyboardSelectedItem(null);
            ClipboardItem.SetThumbnailLoadingEnabled(false);
            DetachRepeaterItemsSource();
            FilteredItems.Clear();
            _orderedFilteredItems.Clear();
            TrimClipboardImageCaches();
            DetachUpdateBannerSubscription();

        }

        private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isBulkReloadingFromImport)
            {
                return;
            }

            if (_currentListMode == ClipboardListMode.History)
            {
                RefreshFilteredItems();
            }
        }

        private void App_ClipboardDataReloading(object? sender, EventArgs e)
        {
            _isBulkReloadingFromImport = true;
        }

        private void App_ClipboardDataReplaced(object? sender, EventArgs e)
        {
            _isBulkReloadingFromImport = false;

            if (DispatcherQueue == null)
            {
                return;
            }

            if (DispatcherQueue.HasThreadAccess)
            {
                _ = ReloadViewAfterImportAsync();
                return;
            }

            DispatcherQueue.TryEnqueue(() => _ = ReloadViewAfterImportAsync());
        }

        private Task ReloadViewAfterImportAsync()
        {
            try
            {
                _favoriteItemsLoaded = false;
                _forceSelectFirstOnNextRefresh = true;
                RefreshFilteredItems();
                _ = ReloadFavoriteItemsAfterImportAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReloadViewAfterImportAsync failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private async Task ReloadFavoriteItemsAfterImportAsync()
        {
            try
            {
                await EnsureFavoriteItemsLoadedAsync(forceReload: true);
                if (_currentListMode == ClipboardListMode.Favorite)
                {
                    _forceSelectFirstOnNextRefresh = true;
                    RefreshFilteredItems();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ReloadFavoriteItemsAfterImportAsync failed: {ex.Message}");
            }
        }

        private void ClipboardFilterState_FilterChanged(object? sender, ClipboardCategoryFilter filter)
        {
            _categoryFilter = filter;
            _forceSelectFirstOnNextRefresh = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshFilteredItems();
                UpdateCategoryFilterTooltip();
                UpdateQuickFilterButtonVisual(filter);
            });
        }

        private void UpdateQuickFilterButtonVisual(ClipboardCategoryFilter filter)
        {
            try
            {
                if (QuickFilterGlyph != null && QuickFilterButton != null)
                {
                    var glyph = GetCategoryGlyph(filter);
                    QuickFilterGlyph.Glyph = string.IsNullOrEmpty(glyph) ? "\uE71C" : glyph;
                    QuickFilterButton.Opacity = filter == ClipboardCategoryFilter.All ? 1.0 : 0.95;
                    // tooltip
                    var label = GetCategoryFilterLabel(filter);
                    ToolTipService.SetToolTip(QuickFilterButton, $"筛选: {label}");
                }
            }
            catch
            {
            }
        }

        private void ClipboardTimeFilterState_FilterChanged(object? sender, EventArgs e)
        {
            _filterStartTime = ClipboardTimeFilterState.StartTime;
            _filterEndTime = ClipboardTimeFilterState.EndTime;
            _forceSelectFirstOnNextRefresh = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                RefreshFilteredItems();
                UpdateTimeFilterLabel();
            });
        }

        private void UpdateCategoryFilterTooltip()
        {
            UpdateSettingsActionTooltip();
        }

        private static string GetCategoryFilterLabel(ClipboardCategoryFilter filter)
        {
            return filter switch
            {
                ClipboardCategoryFilter.All => "全部",
                ClipboardCategoryFilter.Text => "文本",
                ClipboardCategoryFilter.Image => "图片",
                ClipboardCategoryFilter.Link => "链接",
                ClipboardCategoryFilter.Email => "邮箱",
                ClipboardCategoryFilter.File => "文件",
                ClipboardCategoryFilter.Code => "代码",
                ClipboardCategoryFilter.Json => "JSON",
                ClipboardCategoryFilter.LongNumber => "数字",
                _ => "全部"
            };
        }

        private void RefreshFilteredItems()
        {
            if (_isRefreshingFilteredItems)
            {
                _pendingRefreshAfterCurrentPass = true;
                return;
            }

            _isRefreshingFilteredItems = true;
            if (!_isHostWindowVisible)
            {
                _pendingRefreshWhileHidden = true;
                _isRefreshingFilteredItems = false;
                return;
            }

            // save current scroll offset so we can attempt to restore it after incremental updates
            SaveClipboardScrollOffsetSnapshot();

            try
            {
                _pendingRefreshWhileHidden = false;

                var query = GetCurrentSourceItems();

                query = query.Where(MatchesCategoryFilter);

                if (!string.IsNullOrWhiteSpace(_searchText))
                {
                    string searchText = _searchText.Trim();
                    query = query.Where(item =>
                    {
                        if (item.ContentType == ClipboardContentType.Text)
                        {
                            return item.TextContent?.Contains(searchText, StringComparison.OrdinalIgnoreCase) == true;
                        }
                        else if (item.ContentType == ClipboardContentType.Image)
                        {
                            if (string.IsNullOrWhiteSpace(item.ImagePath))
                            {
                                return false;
                            }

                            string fileName = Path.GetFileName(item.ImagePath);
                            return fileName.Contains(searchText, StringComparison.OrdinalIgnoreCase) ||
                                   item.ImagePath.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                        }
                        return false;
                    });
                }

                if (_filterStartTime.HasValue)
                {
                    query = query.Where(item => item.Timestamp >= _filterStartTime.Value);
                }

                if (_filterEndTime.HasValue)
                {
                    query = query.Where(item => item.Timestamp <= _filterEndTime.Value);
                }

                var orderedItems = (_currentListMode == ClipboardListMode.History
                    ? query.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp)
                    : query.OrderByDescending(i => i.Timestamp))
                    .ToList();

                _orderedFilteredItems.Clear();
                _orderedFilteredItems.AddRange(orderedItems);

                ApplyFilteredItemsIncrementally(_orderedFilteredItems);

                OnPropertyChanged(nameof(HistoryEmptyHintVisibility));
                ApplyKeyboardSelectionAfterRefresh();

                // try to restore saved scroll offset if present; otherwise clamp to valid range
                TryRestoreClipboardScrollOffsetSnapshot();
                ClampClipboardScrollOffset();
            }
            finally
            {
                _isRefreshingFilteredItems = false;
            }

            if (_pendingRefreshAfterCurrentPass)
            {
                _pendingRefreshAfterCurrentPass = false;
                if (DispatcherQueue != null)
                {
                    _ = DispatcherQueue.TryEnqueue(RefreshFilteredItems);
                }
                else
                {
                    RefreshFilteredItems();
                }
            }
        }

        private void ApplyFilteredItemsIncrementally(IReadOnlyList<ClipboardItem> orderedItems)
        {
            var targetSet = new HashSet<ClipboardItem>(orderedItems);
            for (int i = FilteredItems.Count - 1; i >= 0; i--)
            {
                if (!targetSet.Contains(FilteredItems[i]))
                {
                    FilteredItems.RemoveAt(i);
                }
            }

            for (int targetIndex = 0; targetIndex < orderedItems.Count; targetIndex++)
            {
                ClipboardItem targetItem = orderedItems[targetIndex];
                if (targetIndex < FilteredItems.Count && ReferenceEquals(FilteredItems[targetIndex], targetItem))
                {
                    continue;
                }

                int existingIndex = FilteredItems.IndexOf(targetItem);
                if (existingIndex >= 0)
                {
                    FilteredItems.Move(existingIndex, targetIndex);
                }
                else
                {
                    FilteredItems.Insert(targetIndex, targetItem);
                }
            }

            while (FilteredItems.Count > orderedItems.Count)
            {
                FilteredItems.RemoveAt(FilteredItems.Count - 1);
            }
        }

        private void ApplyKeyboardSelectionAfterRefresh()
        {
            if (_orderedFilteredItems.Count == 0)
            {
                SetKeyboardSelectedItem(null);
                _forceSelectFirstOnNextRefresh = false;
                return;
            }

            int targetGlobalIndex = 0;
            if (_forceSelectFirstOnNextRefresh)
            {
                _forceSelectFirstOnNextRefresh = false;
            }
            else if (_keyboardSelectedItem != null)
            {
                int existingIndex = _orderedFilteredItems.IndexOf(_keyboardSelectedItem);
                if (existingIndex >= 0)
                {
                    targetGlobalIndex = existingIndex;
                }
            }

            SetKeyboardSelectedItem(_orderedFilteredItems[targetGlobalIndex]);
        }

        private void SetKeyboardSelectedItem(ClipboardItem? item, bool wrappedToEdge = false)
        {
            if (ReferenceEquals(_keyboardSelectedItem, item))
            {
                BringKeyboardSelectionIntoView(item, wrappedToEdge);
                return;
            }

            if (_keyboardSelectedItem != null)
            {
                _keyboardSelectedItem.IsKeyboardSelected = false;
            }

            _keyboardSelectedItem = item;

            if (_keyboardSelectedItem != null)
            {
                _keyboardSelectedItem.IsKeyboardSelected = true;
            }

            BringKeyboardSelectionIntoView(item, wrappedToEdge);
        }

        private void BringKeyboardSelectionIntoView(ClipboardItem? item, bool wrappedToEdge = false)
        {
            if (item == null)
            {
                return;
            }

            int index = FilteredItems.IndexOf(item);

            if (index < 0)
            {
                return;
            }

            if (wrappedToEdge && ClipboardScrollViewer != null)
            {
                if (index == 0)
                {
                    ClipboardScrollViewer.ChangeView(null, 0, null, true);
                    return;
                }

                if (index == FilteredItems.Count - 1)
                {
                    ClipboardScrollViewer.ChangeView(null, ClipboardScrollViewer.ScrollableHeight, null, true);
                    return;
                }
            }

            ClipboardItemsRepeater?.UpdateLayout();
            if (ClipboardItemsRepeater?.TryGetElement(index) is FrameworkElement element)
            {
                if (!element.IsLoaded || element.ActualHeight < 1)
                {
                    if (ClipboardScrollViewer != null)
                    {
                        if (index == 0)
                        {
                            ClipboardScrollViewer.ChangeView(null, 0, null, true);
                        }
                        else if (index == FilteredItems.Count - 1)
                        {
                            ClipboardScrollViewer.ChangeView(null, ClipboardScrollViewer.ScrollableHeight, null, true);
                        }
                        else
                        {
                            element.StartBringIntoView(new BringIntoViewOptions
                            {
                                AnimationDesired = false,
                                VerticalAlignmentRatio = 0.5
                            });
                        }
                    }

                    return;
                }

                CenterKeyboardSelectionInViewport(element);
                return;
            }

            if (ClipboardScrollViewer != null)
            {
                if (index == 0)
                {
                    ClipboardScrollViewer.ChangeView(null, 0, null, true);
                }
                else if (index == FilteredItems.Count - 1)
                {
                    ClipboardScrollViewer.ChangeView(null, ClipboardScrollViewer.ScrollableHeight, null, true);
                }
            }
        }

        private void CenterKeyboardSelectionInViewport(FrameworkElement element)
        {
            if (ClipboardScrollViewer == null || !element.IsLoaded || element.ActualHeight < 1)
            {
                return;
            }

            var transform = element.TransformToVisual(ClipboardScrollViewer);
            Point topLeft = transform.TransformPoint(new Point(0, 0));

            double currentOffset = ClipboardScrollViewer.VerticalOffset;
            double elementCenterInContent = currentOffset + topLeft.Y + (element.ActualHeight / 2d);
            double targetOffset = elementCenterInContent - (ClipboardScrollViewer.ViewportHeight / 2d);
            double clampedOffset = Math.Clamp(targetOffset, 0, ClipboardScrollViewer.ScrollableHeight);

            if (Math.Abs(clampedOffset - currentOffset) > 0.5d)
            {
                ClipboardScrollViewer.ChangeView(null, clampedOffset, null, true);
            }
        }

        private void MoveKeyboardSelection(int direction)
        {
            if (_orderedFilteredItems.Count == 0 || direction == 0)
            {
                SetKeyboardSelectedItem(null);
                return;
            }

            int currentIndex = _keyboardSelectedItem == null
                ? -1
                : _orderedFilteredItems.IndexOf(_keyboardSelectedItem);

            int nextIndex;
            bool wrappedToEdge = false;
            if (currentIndex < 0)
            {
                nextIndex = direction > 0 ? 0 : _orderedFilteredItems.Count - 1;
                wrappedToEdge = nextIndex == 0 || nextIndex == _orderedFilteredItems.Count - 1;
            }
            else
            {
                nextIndex = (currentIndex + direction) % _orderedFilteredItems.Count;
                if (nextIndex < 0)
                {
                    nextIndex += _orderedFilteredItems.Count;
                }

                wrappedToEdge =
                    (direction > 0 && currentIndex == _orderedFilteredItems.Count - 1 && nextIndex == 0) ||
                    (direction < 0 && currentIndex == 0 && nextIndex == _orderedFilteredItems.Count - 1);
            }

            SetKeyboardSelectedItem(_orderedFilteredItems[nextIndex], wrappedToEdge);
        }

        private bool MatchesCategoryFilter(ClipboardItem item)
        {
            return _categoryFilter switch
            {
                ClipboardCategoryFilter.All => true,
                ClipboardCategoryFilter.Image => item.SemanticType == ClipboardSemanticType.Image,
                ClipboardCategoryFilter.Text => item.ContentType == ClipboardContentType.Text,
                ClipboardCategoryFilter.Link => item.SemanticType == ClipboardSemanticType.Link,
                ClipboardCategoryFilter.Email => item.SemanticType == ClipboardSemanticType.Email,
                ClipboardCategoryFilter.File => item.SemanticType == ClipboardSemanticType.FilePath,
                ClipboardCategoryFilter.Code => item.SemanticType == ClipboardSemanticType.Code,
                ClipboardCategoryFilter.Json => item.SemanticType == ClipboardSemanticType.Json,
                ClipboardCategoryFilter.LongNumber => item.SemanticType == ClipboardSemanticType.LongNumber,
                _ => true
            };
        }

        private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                _searchText = sender.Text;
                _forceSelectFirstOnNextRefresh = true;
                RefreshFilteredItems();
            }
        }

        private void ClipboardScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
        }

        private void QuickFilterButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var flyout = new Flyout();

                // compute counts snapshot
                var itemsSnapshot = clipboardModel?.Items ?? Enumerable.Empty<ClipboardItem>();
                int cnt_all = itemsSnapshot.Count();
                int cnt_text = itemsSnapshot.Count(i => i.ContentType == ClipboardContentType.Text);
                int cnt_code = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.Code);
                int cnt_json = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.Json);
                int cnt_longNumber = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.LongNumber);
                int cnt_image = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.Image);
                int cnt_file = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.FilePath);
                int cnt_link = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.Link);
                int cnt_email = itemsSnapshot.Count(i => i.SemanticType == ClipboardSemanticType.Email);

                var panel = new StackPanel { Orientation = Orientation.Vertical };

                void addItem(ClipboardCategoryFilter f, string label)
                {
                    string suffix = f switch
                    {
                        ClipboardCategoryFilter.All => $" ({cnt_all})",
                        ClipboardCategoryFilter.Text => $" ({cnt_text})",
                        ClipboardCategoryFilter.Code => $" ({cnt_code})",
                        ClipboardCategoryFilter.Json => $" ({cnt_json})",
                        ClipboardCategoryFilter.LongNumber => $" ({cnt_longNumber})",
                        ClipboardCategoryFilter.Image => $" ({cnt_image})",
                        ClipboardCategoryFilter.File => $" ({cnt_file})",
                        ClipboardCategoryFilter.Link => $" ({cnt_link})",
                        ClipboardCategoryFilter.Email => $" ({cnt_email})",
                        _ => string.Empty,
                    };

                    var item = new RadioButton { Tag = f, GroupName = "QuickFilterGroup" };

                    // build content: Grid with icon | label | count (right-aligned)
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var iconGlyph = GetCategoryGlyph(f);
                    if (!string.IsNullOrEmpty(iconGlyph))
                    {
                        var iconEl = new FontIcon { Glyph = iconGlyph, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
                        Grid.SetColumn(iconEl, 0);
                        grid.Children.Add(iconEl);
                    }

                    var labelText = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                    Grid.SetColumn(labelText, 1);
                    grid.Children.Add(labelText);

                    var countText = new TextBlock { Text = suffix, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                    try
                    {
                        if (Application.Current != null && Application.Current.Resources.ContainsKey("TextFillColorSecondaryBrush"))
                        {
                            countText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
                        }
                    }
                    catch
                    {
                        // ignore resource lookup failures
                    }
                    Grid.SetColumn(countText, 2);
                    grid.Children.Add(countText);

                    item.Content = grid;
                    item.IsChecked = ClipboardFilterState.Current == f;
                    item.Checked += (_, __) =>
                    {
                        try
                        {
                            ClipboardFilterState.Current = f;
                            flyout.Hide();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"QuickFilter selection failed: {ex.Message}");
                        }
                    };

                    panel.Children.Add(item);
                }

                addItem(ClipboardCategoryFilter.All, "全部");
                addItem(ClipboardCategoryFilter.Text, "文本");
                addItem(ClipboardCategoryFilter.Image, "图片");
                addItem(ClipboardCategoryFilter.Link, "链接");
                addItem(ClipboardCategoryFilter.Email, "邮箱");
                addItem(ClipboardCategoryFilter.File, "文件");
                addItem(ClipboardCategoryFilter.Code, "代码");
                addItem(ClipboardCategoryFilter.Json, "JSON");
                addItem(ClipboardCategoryFilter.LongNumber, "数字");

                flyout.Content = panel;
                if (sender is FrameworkElement fe)
                {
                    flyout.ShowAt(fe);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QuickFilterButton_Click failed: {ex.Message}");
            }
        }

        private static string GetCategoryGlyph(ClipboardCategoryFilter f)
        {
            return f switch
            {
                ClipboardCategoryFilter.All => "\uE71D",
                ClipboardCategoryFilter.Text => "\uE8C8",
                ClipboardCategoryFilter.Image => "\uE91B",
                ClipboardCategoryFilter.Link => "\uE71B",
                ClipboardCategoryFilter.Email => "\uE715",
                ClipboardCategoryFilter.File => "\uE8B7",
                ClipboardCategoryFilter.Code => "\uE943",
                ClipboardCategoryFilter.Json => "\uE943",
                ClipboardCategoryFilter.LongNumber => "\uE8C8",
                _ => string.Empty,
            };
        }

        private async void ClipboardPage_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_isHostWindowVisible || _isLoading || _isSubmittingKeyboardSelection)
            {
                return;
            }

            if (e.Key == VirtualKey.Left)
            {
                e.Handled = true;
                await SwitchListModeAsync(ClipboardListMode.History);
                return;
            }

            if (e.Key == VirtualKey.Right)
            {
                e.Handled = true;
                await SwitchListModeAsync(ClipboardListMode.Favorite);
                return;
            }

            if (e.Key == VirtualKey.Down)
            {
                MoveKeyboardSelection(1);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Up)
            {
                MoveKeyboardSelection(-1);
                e.Handled = true;
                return;
            }

            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                await SubmitKeyboardSelectedItemAsync();
            }
        }

        private async Task SubmitKeyboardSelectedItemAsync()
        {
            if (_isSubmittingKeyboardSelection)
            {
                return;
            }

            ClipboardItem? selected = _keyboardSelectedItem ?? _orderedFilteredItems.FirstOrDefault();
            if (selected == null)
            {
                return;
            }

            PromoteClipboardItem(selected);

            _isSubmittingKeyboardSelection = true;
            try
            {
                bool submitted = await SubmitClipboardItemAsync(selected);
                if (submitted && App.Current is App app)
                {
                    await app.TriggerWindowHideAsync();
                }
            }
            finally
            {
                _isSubmittingKeyboardSelection = false;
            }
        }

        private void OpenNavigationHubButton_Click(object sender, RoutedEventArgs e)
        {
            bool isFavoriteMode = _currentListMode == ClipboardListMode.Favorite;
            Frame.Navigate(typeof(NavigationHubPage), isFavoriteMode);
        }

        private void OpenEmojiPageButton_Click(object sender, RoutedEventArgs e)
        {
            Frame.Navigate(typeof(EmojiPage));
        }

        private void UpdateTimeFilterLabel()
        {
            UpdateSettingsActionTooltip();
        }

        private void UpdateSettingsActionTooltip()
        {
            if (SettingsActionButton == null)
            {
                return;
            }

            string categoryText = GetCategoryFilterLabel(_categoryFilter);
            string timeText;
            if (!_filterStartTime.HasValue && !_filterEndTime.HasValue)
            {
                timeText = "全部时间";
            }
            else if (_filterStartTime.HasValue && _filterEndTime.HasValue)
            {
                timeText = $"{_filterStartTime:MM-dd}~{_filterEndTime:MM-dd}";
            }
            else if (_filterStartTime.HasValue)
            {
                timeText = $">={_filterStartTime:MM-dd}";
            }
            else
            {
                timeText = $"<={_filterEndTime:MM-dd}";
            }

            ToolTipService.SetToolTip(SettingsActionButton, "分类: " + categoryText + " | 时间: " + timeText);
        }

        private async void ClipboardPage_Loaded(object sender, RoutedEventArgs e)
        {
            var model = clipboardModel;
            if (model == null)
            {
                return;
            }

            AttachPageSubscriptions();
            AttachUpdateBannerSubscription();
            SyncFilterStateFromGlobal();
            bool isHostVisible = App.Current is App app && app.IsMainWindowVisible;
            _isHostWindowVisible = isHostVisible;
            ClipboardItem.SetThumbnailLoadingEnabled(isHostVisible);
            if (isHostVisible)
            {
                AttachRepeaterItemsSource();
            }
            else
            {
                EnterBackgroundMemorySavingMode();
            }
            RefreshOverlayBrushForClipboardTransientSurfaces();
            UpdateBannerService.EnsureStartupCheckStarted(UpdateBannerService.GetCurrentVersionString());
            ApplyUpdateBannerState(UpdateBannerService.GetSnapshot(forClipboard: true));
            UpdateCategoryFilterTooltip();
            UpdateTimeFilterLabel();
            NotifyListModeChanged();
            if (_currentListMode == ClipboardListMode.Favorite)
            {
                await EnsureFavoriteItemsLoadedAsync();
            }
            else
            {
                _ = WarmupFavoriteItemsAsync();
            }

            if (model.IsLoaded)
            {
                _hasLoadedFromDatabase = true;
                RefreshFilteredItems();
                return;
            }

            if (!_hasLoadedFromDatabase && !_isLoading)
            {
                _isLoading = true;

                try
                {
                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.Visibility = Visibility.Visible;
                    }

                    int waitCount = 0;
                    while (!model.IsLoaded && waitCount < 100)
                    {
                        await Task.Delay(50);
                        waitCount++;
                    }

                    if (!model.IsLoaded)
                    {
                        System.Diagnostics.Debug.WriteLine("Background loading timeout, loading manually...");

                        var items = await Task.Run(() =>
                        {
                            try
                            {
                                return DatabaseService.GetRecentClipboardItems(ClipboardLoadLimit);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error loading clipboard items: {ex.Message}");
                                return new System.Collections.Generic.List<ClipboardItem>();
                            }
                        });

                        if (model.Items != null)
                        {
                            var existingIds = new System.Collections.Generic.HashSet<int>();
                            foreach (var existingItem in model.Items)
                            {
                                existingIds.Add(existingItem.Id);
                            }

                            foreach (var item in items)
                            {
                                if (!existingIds.Contains(item.Id))
                                {
                                    model.Items.Add(item);
                                }
                            }

                            model.IsLoaded = true;
                        }
                    }

                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    }

                    _hasLoadedFromDatabase = true;
                    RefreshFilteredItems();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error in ClipboardPage_Loaded: {ex.Message}");

                    if (LoadingOverlay != null)
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    }
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }

        private void PromoteClipboardItem(ClipboardItem item)
        {
            if (item == null)
            {
                return;
            }

            DateTime now = DateTime.Now;

            if (_currentListMode == ClipboardListMode.History && item.Id > 0)
            {
                item.Timestamp = now;
                bool updated = DatabaseService.UpdateClipboardItemTimestamp(item.Id, now);
                if (updated)
                {
                    RefreshFilteredItems();
                }

                return;
            }

            if (_currentListMode == ClipboardListMode.Favorite)
            {
                var historyMatch = clipboardModel?.Items?.FirstOrDefault(x =>
                    x.ContentType == item.ContentType &&
                    string.Equals(x.ContentHash, item.ContentHash, StringComparison.Ordinal));

                if (historyMatch != null && historyMatch.Id > 0)
                {
                    historyMatch.Timestamp = now;
                    bool updated = DatabaseService.UpdateClipboardItemTimestamp(historyMatch.Id, now);
                    if (updated)
                    {
                        RefreshFilteredItems();
                    }
                }
            }
        }

        private async void ClipboardCard_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;

            if (_isSubmittingKeyboardSelection)
            {
                return;
            }

            if (sender is Border border && border.Tag is ClipboardItem item)
            {
                PromoteClipboardItem(item);

                _isSubmittingKeyboardSelection = true;
                try
                {
                    SetKeyboardSelectedItem(item);

                    bool submitted = await SubmitClipboardItemAsync(item);
                    if (submitted && App.Current is App app)
                    {
                        await Task.Yield();
                        await app.TriggerWindowHideAsync();
                    }
                }
                finally
                {
                    _isSubmittingKeyboardSelection = false;
                }
            }
        }

        private void Page_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseAllDeleteButtons();
        }

        private void UpdateBannerInstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (UpdateBannerService.TryOpenDownloadPage(out _))
            {
                return;
            }
        }

        private void UpdateBannerToggleNotesButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBannerService.ToggleReleaseNotesExpanded();
        }

        private void UpdateBannerCloseButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateBannerService.DismissInClipboardForCurrentProcess();
            RefreshFilteredItems();
            ClampClipboardScrollOffset();
        }

        private void ClampClipboardScrollOffset()
        {
            if (ClipboardScrollViewer == null || DispatcherQueue == null)
            {
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() =>
            {
                ClampClipboardScrollOffsetCore();
                _ = DispatcherQueue.TryEnqueue(ClampClipboardScrollOffsetCore);
            });
        }

        private void SaveClipboardScrollOffsetSnapshot()
        {
            try
            {
                if (ClipboardScrollViewer != null)
                {
                    _savedClipboardScrollOffset = ClipboardScrollViewer.VerticalOffset;
                }
                else
                {
                    _savedClipboardScrollOffset = null;
                }
            }
            catch
            {
                _savedClipboardScrollOffset = null;
            }
        }

        private void TryRestoreClipboardScrollOffsetSnapshot()
        {
            if (_savedClipboardScrollOffset == null || ClipboardScrollViewer == null || DispatcherQueue == null)
            {
                _savedClipboardScrollOffset = null;
                return;
            }

            double targetOffset = Math.Clamp(_savedClipboardScrollOffset.Value, 0, ClipboardScrollViewer.ScrollableHeight);

            // perform restore after layout passes to ensure ScrollableHeight is accurate
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ClipboardItemsRepeater?.UpdateLayout();
                    ClipboardScrollViewer.UpdateLayout();
                    double clamped = Math.Clamp(targetOffset, 0, ClipboardScrollViewer.ScrollableHeight);
                    ClipboardScrollViewer.ChangeView(null, clamped, null, true);
                }
                catch
                {
                    // ignore failures
                }
            });

            _savedClipboardScrollOffset = null;
        }

        private void ClampClipboardScrollOffsetCore()
        {
            if (ClipboardScrollViewer == null)
            {
                return;
            }

            ClipboardItemsRepeater?.UpdateLayout();
            ClipboardScrollViewer.UpdateLayout();

            double maxOffset = ClipboardScrollViewer.ScrollableHeight;
            double currentOffset = ClipboardScrollViewer.VerticalOffset;
            double clampedOffset = Math.Clamp(currentOffset, 0, maxOffset);
            if (Math.Abs(clampedOffset - currentOffset) > 0.5)
            {
                ClipboardScrollViewer.ChangeView(null, clampedOffset, null, true);
            }
        }

        private void CloseDeleteStoryboard_Completed(object? sender, object e)
        {
            if (sender is Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard)
            {
                storyboard.Completed -= CloseDeleteStoryboard_Completed;
                storyboard.Stop();
                storyboard.Children.Clear();
            }

            if (_lastClosingDeleteButton != null)
            {
                _lastClosingDeleteButton.Visibility = Visibility.Collapsed;
                _lastClosingDeleteButton.IsHitTestVisible = false;
                _lastClosingDeleteButton.Opacity = 0;
                _lastClosingDeleteButton = null;
            }

            _currentDeleteStoryboard = null;
        }

        private void CloseAllDeleteButtons()
        {
            if (_currentDeleteStoryboard != null)
            {
                _currentDeleteStoryboard.Completed -= CloseDeleteStoryboard_Completed;
                _currentDeleteStoryboard.Stop();
                _currentDeleteStoryboard.Children.Clear();
                _currentDeleteStoryboard = null;
            }

            if (_lastClosingDeleteButton != null)
            {
                _lastClosingDeleteButton.Visibility = Visibility.Collapsed;
                _lastClosingDeleteButton.IsHitTestVisible = false;
                _lastClosingDeleteButton.Opacity = 0;
                _lastClosingDeleteButton = null;
            }

            if (_currentOpenDeleteButton != null && _currentOpenMainCard != null)
            {
                var cardTransform = _currentOpenMainCard.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
                var deleteButtonTransform = _currentOpenDeleteButton.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;

                _lastClosingDeleteButton = _currentOpenDeleteButton;

                if (cardTransform != null && deleteButtonTransform != null)
                {
                    var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                    var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };

                    var cardAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                        EasingFunction = easing
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(cardAnimation, cardTransform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(cardAnimation, "X");
                    storyboard.Children.Add(cardAnimation);

                    var deleteButtonAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        To = InlineActionPanelOffset,
                        Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                        EasingFunction = easing
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(deleteButtonAnimation, deleteButtonTransform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(deleteButtonAnimation, "X");
                    storyboard.Children.Add(deleteButtonAnimation);

                    var opacityAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                    {
                        To = 0,
                        Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                        EasingFunction = easing
                    };
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnimation, _currentOpenDeleteButton);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
                    storyboard.Children.Add(opacityAnimation);

                    storyboard.Completed += CloseDeleteStoryboard_Completed;
                    storyboard.Begin();
                }
                else
                {
                    _lastClosingDeleteButton.Visibility = Visibility.Collapsed;
                    _lastClosingDeleteButton.IsHitTestVisible = false;
                    _lastClosingDeleteButton.Opacity = 0;
                    _lastClosingDeleteButton = null;
                }

                _currentOpenDeleteButton = null;
                _currentOpenMainCard = null;
            }
        }

        private void MenuButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private void MenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button)
            {
                var parent = button.Parent;
                while (parent != null && !(parent is Grid))
                {
                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }

                while (parent != null)
                {
                    if (parent is Grid grid)
                    {
                        var deleteButton = FindChildByName(grid, "DeleteButton") as Border;
                        var mainCard = FindChildByName(grid, "MainCard") as Border;

                        if (deleteButton == null && button.Tag is ClipboardItem taggedItem)
                        {
                            taggedItem.IsInlineActionsLoaded = true;
                            grid.UpdateLayout();
                            deleteButton = FindChildByName(grid, "DeleteButton") as Border;
                            mainCard ??= FindChildByName(grid, "MainCard") as Border;
                        }

                        if (deleteButton != null && mainCard != null)
                        {
                            UpdateInlineFavoriteButtonText(deleteButton);

                            bool isSameCard = (_currentOpenDeleteButton == deleteButton && _currentOpenMainCard == mainCard);

                            if (isSameCard)
                            {
                                CloseAllDeleteButtons();
                                return;
                            }

                            CloseAllDeleteButtons();

                            var cardTransform = mainCard.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
                            var deleteButtonTransform = deleteButton.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;

                            if (cardTransform != null && deleteButtonTransform != null)
                            {
                                _currentOpenDeleteButton = deleteButton;
                                _currentOpenMainCard = mainCard;
                                deleteButton.Visibility = Visibility.Visible;
                                deleteButton.IsHitTestVisible = true;
                                deleteButton.Opacity = 0;
                                deleteButtonTransform.X = InlineActionPanelOffset;

                                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                                var easing = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut };

                                var cardAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                                {
                                    To = -InlineActionPanelOffset,
                                    Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                                    EasingFunction = easing
                                };
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(cardAnimation, cardTransform);
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(cardAnimation, "X");
                                storyboard.Children.Add(cardAnimation);

                                var deleteButtonAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                                {
                                    To = 0,
                                    Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                                    EasingFunction = easing
                                };
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(deleteButtonAnimation, deleteButtonTransform);
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(deleteButtonAnimation, "X");
                                storyboard.Children.Add(deleteButtonAnimation);

                                var opacityAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                                {
                                    To = 1,
                                    Duration = TimeSpan.FromMilliseconds(InlineActionAnimationMs),
                                    EasingFunction = easing
                                };
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(opacityAnimation, deleteButton);
                                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(opacityAnimation, "Opacity");
                                storyboard.Children.Add(opacityAnimation);

                                storyboard.Begin();
                            }
                            break;
                        }
                    }
                    parent = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(parent) as FrameworkElement;
                }
            }
        }

        private async void DeleteQuickButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ClipboardItem item)
            {
                CloseAllDeleteButtons();
                if (_currentListMode == ClipboardListMode.Favorite)
                {
                    await RemoveFromFavoritesAsync(item);
                    return;
                }

                await RemoveFromHistoryAsync(item);
            }
        }

        private async void FavoriteQuickButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not ClipboardItem item)
            {
                return;
            }

            CloseAllDeleteButtons();

            if (_currentListMode == ClipboardListMode.History)
            {
                await AddToFavoritesAsync(item);
                return;
            }

            await RemoveFromFavoritesAsync(item);
        }

        private async Task<bool> RemoveFromHistoryAsync(ClipboardItem historyItem, bool refreshList = true)
        {
            try
            {
                await Task.Run(() => DatabaseService.DeleteClipboardItem(historyItem.Id));

                if (historyItem.ContentType == ClipboardContentType.Image)
                {
                    historyItem.ClearImageCache();
                    if (!string.IsNullOrWhiteSpace(historyItem.ImagePath) && File.Exists(historyItem.ImagePath))
                    {
                        try
                        {
                            File.Delete(historyItem.ImagePath);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to delete history image file: {ex.Message}");
                        }
                    }
                }


                // Record neighbor selection to preserve viewport after removal.
                int filteredIndex = -1;
                ClipboardItem? neighborSelection = null;
                try
                {
                    if (FilteredItems != null)
                    {
                        filteredIndex = FilteredItems.IndexOf(historyItem);
                        if (filteredIndex >= 0)
                        {
                            // prefer next item; if none, pick previous
                            if (filteredIndex < FilteredItems.Count - 1)
                            {
                                neighborSelection = FilteredItems[filteredIndex + 1];
                            }
                            else if (filteredIndex - 1 >= 0)
                            {
                                neighborSelection = FilteredItems[filteredIndex - 1];
                            }
                        }
                    }
                }
                catch
                {
                    // ignore any concurrency issues reading FilteredItems
                }

                bool removedFromCollection = clipboardModel.Items.Remove(historyItem);
                if (!removedFromCollection && historyItem.Id > 0)
                {
                    ClipboardItem? historyMatch = clipboardModel.Items.FirstOrDefault(x => x.Id == historyItem.Id);
                    if (historyMatch != null)
                    {
                        _ = clipboardModel.Items.Remove(historyMatch);
                        removedFromCollection = true;
                    }
                }

                // If the removed item was the keyboard-selected one, try to select a neighbor to keep viewport stable.
                if (ReferenceEquals(_keyboardSelectedItem, historyItem))
                {
                    SetKeyboardSelectedItem(neighborSelection);
                }

                if (refreshList && _currentListMode == ClipboardListMode.History)
                {
                    RefreshFilteredItems();
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to remove history clipboard item: {ex.Message}");
                return false;
            }
        }

        private FrameworkElement? FindChildByName(DependencyObject parent, string name)
        {
            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is FrameworkElement element && element.Name == name)
                {
                    return element;
                }

                var result = FindChildByName(child, name);
                if (result != null)
                {
                    return result;
                }
            }
            return null;
        }

        private async Task<bool> SubmitClipboardItemAsync(ClipboardItem item)
        {
            try
            {
                var dataPackage = new DataPackage();
                dataPackage.RequestedOperation = DataPackageOperation.Copy;

                if (item.ContentType == ClipboardContentType.Text)
                {
                    dataPackage.SetText(item.TextContent);
                }
                else if (item.ContentType == ClipboardContentType.Image)
                {
                    if (File.Exists(item.ImagePath))
                    {
                        var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
                        var stream = RandomAccessStreamReference.CreateFromFile(file);
                        dataPackage.SetBitmap(stream);
                    }
                    else
                    {
                        return false;
                    }
                }

                if (App.Current is App currentApp)
                {
                    currentApp.SuppressClipboardMonitorFor(TimeSpan.FromMilliseconds(1000));
                }

                var tcs = new TaskCompletionSource<bool>();
                bool enqueued = DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Clipboard.SetContent(dataPackage);
                        Clipboard.Flush();
                        tcs.TrySetResult(true);
                    }
                    catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x80004005))
                    {
                        System.Diagnostics.Debug.WriteLine($"Clipboard not ready (HRESULT: 0x{ex.HResult:X8}): {ex.Message}");
                        tcs.TrySetResult(false);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to set clipboard: {ex.Message}");
                        tcs.TrySetResult(false);
                    }
                });

                if (!enqueued)
                {
                    return false;
                }

                var timeoutTask = Task.Delay(3000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    return false;
                }

                bool success = await tcs.Task;
                if (success)
                {
                    bool pasted = false;
                    App.AutoPasteFallbackReason fallbackReason = App.AutoPasteFallbackReason.None;
                    if (App.Current is App app)
                    {
                        app.BeginAutoHideSuppression();
                        try
                        {
                            pasted = await app.TryAutoPasteToCapturedTargetAsync();
                            fallbackReason = app.LastAutoPasteFallbackReason;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Auto paste attempt failed: {ex.Message}");
                        }
                        finally
                        {
                            app.EndAutoHideSuppression();
                        }
                    }
                    else
                    {
                        Debug.WriteLine("Auto paste unavailable: app instance missing.");
                    }

                    if (!pasted)
                    {
                        Debug.WriteLine($"Auto paste failed (reason: {fallbackReason}), clipboard content copied only.");
                        return false;
                    }

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Submit clipboard item failed: {ex.Message}");
                return false;
            }
        }
    }
}



