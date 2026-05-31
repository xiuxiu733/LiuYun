using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiuYun.Models;
using LiuYun.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace LiuYun.Views
{
    public sealed partial class NavigationHubPage : Page
    {
        private const string FavoriteImageFolderName = "favorite_images";
        private bool _isFavoriteMode;
        private bool _isClipboardItemsSubscribed;

        public NavigationHubPage()
        {
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isFavoriteMode = e.Parameter is bool isFavorite && isFavorite;

            UpdateClearActionTexts();
            StartDatePicker.Date = ClipboardTimeFilterState.StartTime;
            EndDatePicker.Date = ClipboardTimeFilterState.EndTime;

            AttachClipboardItemsSubscription();
            RefreshCategoryCounts();
            // Ensure UI reflects current global category and time filters
            ClipboardFilterState.FilterChanged += ClipboardFilterState_FilterChanged;
            UpdateActiveCategoryVisual(ClipboardFilterState.Current);

            ClipboardTimeFilterState.FilterChanged += ClipboardTimeFilterState_FilterChanged_Nav;
            UpdateActiveTimeVisual(ClipboardTimeFilterState.StartTime, ClipboardTimeFilterState.EndTime);

            UpdateStatusTextFromFilter();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            DetachClipboardItemsSubscription();
            // unsubscribe from global filter changes
            ClipboardFilterState.FilterChanged -= ClipboardFilterState_FilterChanged;
            ClipboardTimeFilterState.FilterChanged -= ClipboardTimeFilterState_FilterChanged_Nav;
            base.OnNavigatedFrom(e);
        }

        private void ClipboardFilterState_FilterChanged(object? sender, ClipboardCategoryFilter filter)
        {
            // If no dispatcher queue is available, nothing we can do safely.
            if (DispatcherQueue == null)
            {
                return;
            }

            // If we're already on the UI thread, update both visual and status immediately.
            if (DispatcherQueue.HasThreadAccess)
            {
                UpdateActiveCategoryVisual(filter);
                UpdateStatusTextFromFilter();
                return;
            }

            // Otherwise, enqueue a single action that updates both to avoid multiple dispatches.
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                UpdateActiveCategoryVisual(filter);
                UpdateStatusTextFromFilter();
            });
        }

        private void UpdateStatusTextFromFilter()
        {
            try
            {
                var category = ClipboardFilterState.Current;
                string categoryText = GetCategoryFilterLabel(category);

                StatusText.Text = $"已应用筛选：分类 {categoryText}";
            }
            catch
            {
            }
        }

        private void ClipboardTimeFilterState_FilterChanged_Nav(object? sender, EventArgs e)
        {
            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                UpdateActiveTimeVisual(ClipboardTimeFilterState.StartTime, ClipboardTimeFilterState.EndTime);
                return;
            }

            _ = DispatcherQueue.TryEnqueue(() => UpdateActiveTimeVisual(ClipboardTimeFilterState.StartTime, ClipboardTimeFilterState.EndTime));
        }

        private void UpdateActiveCategoryVisual(ClipboardCategoryFilter filter)
        {
            // List all buttons for easy iteration
            var allButtons = new Button?[]
            {
                AllCategoryButton,
                TextCategoryButton,
                CodeCategoryButton,
                JsonCategoryButton,
                LongNumberCategoryButton,
                ImageCategoryButton,
                FileCategoryButton,
                LinkCategoryButton,
                EmailCategoryButton
            };

            // Dim all buttons first
            foreach (var btn in allButtons)
            {
                if (btn == null)
                {
                    continue;
                }

                btn.Opacity = 0.7;
            }

            // Determine which button corresponds to the filter and highlight it
            Button? selected = filter switch
            {
                ClipboardCategoryFilter.All => AllCategoryButton,
                ClipboardCategoryFilter.Text => TextCategoryButton,
                ClipboardCategoryFilter.Code => CodeCategoryButton,
                ClipboardCategoryFilter.Json => JsonCategoryButton,
                ClipboardCategoryFilter.LongNumber => LongNumberCategoryButton,
                ClipboardCategoryFilter.Image => ImageCategoryButton,
                ClipboardCategoryFilter.File => FileCategoryButton,
                ClipboardCategoryFilter.Link => LinkCategoryButton,
                ClipboardCategoryFilter.Email => EmailCategoryButton,
                _ => AllCategoryButton
            };

            if (selected != null)
            {
                selected.Opacity = 1.0;
            }
        }

        private void UpdateActiveTimeVisual(DateTime? start, DateTime? end)
        {
            // Dim all quick buttons and date pickers by default
            var quickButtons = new Button?[] { QuickTodayButton, QuickSevenDaysButton, QuickFourteenDaysButton, QuickAllButton };
            foreach (var b in quickButtons)
            {
                if (b != null) b.Opacity = 0.7;
            }

            if (StartDatePicker != null) StartDatePicker.Opacity = 0.7;
            if (EndDatePicker != null) EndDatePicker.Opacity = 0.7;

            // Decide which quick filter this matches (Today / 7 / 14 / All) or custom
            if (!start.HasValue && !end.HasValue)
            {
                if (QuickAllButton != null) QuickAllButton.Opacity = 1.0;
                return;
            }

            DateTime today = DateTime.Today;
            // Normalize boundaries produced by BuildStartBoundary/BuildEndBoundary
            DateTime? s = start?.Date;
            DateTime? e = end?.Date;

            if (s.HasValue && e.HasValue)
            {
                if (s.Value == today && e.Value == today)
                {
                    if (QuickTodayButton != null) QuickTodayButton.Opacity = 1.0;
                    return;
                }

                if (s.Value == today.AddDays(-6) && e.Value == today)
                {
                    if (QuickSevenDaysButton != null) QuickSevenDaysButton.Opacity = 1.0;
                    return;
                }

                if (s.Value == today.AddDays(-13) && e.Value == today)
                {
                    if (QuickFourteenDaysButton != null) QuickFourteenDaysButton.Opacity = 1.0;
                    return;
                }
            }

            // Custom range: highlight the date pickers
            if (StartDatePicker != null) StartDatePicker.Opacity = 1.0;
            if (EndDatePicker != null) EndDatePicker.Opacity = 1.0;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame?.CanGoBack == true)
            {
                Frame.GoBack();
                return;
            }

            Frame?.Navigate(typeof(ClipboardPage));
        }

        private void OpenSettingsPageButton_Click(object sender, RoutedEventArgs e)
        {
            Frame?.Navigate(typeof(SettingsPage));
        }

        private void CategoryFilterButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string tag)
            {
                return;
            }

            if (!Enum.TryParse(tag, out ClipboardCategoryFilter filter))
            {
                return;
            }

            ClipboardFilterState.Current = filter;
            StatusText.Text = $"已应用分类筛选：{GetCategoryFilterLabel(filter)}";
            NavigateToClipboardHistoryPage();
        }

        private void ApplyTimeFilterButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime? start = BuildStartBoundary(StartDatePicker.Date);
            DateTime? end = BuildEndBoundary(EndDatePicker.Date);

            if (start.HasValue && end.HasValue && start.Value > end.Value)
            {
                StatusText.Text = "时间范围无效：开始日期晚于结束日期。";
                return;
            }

            ClipboardTimeFilterState.SetRange(start, end);
            StatusText.Text = "时间筛选已应用。";
            NavigateToClipboardHistoryPage();
        }

        private void QuickTodayFilterButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime today = DateTime.Today;
            StartDatePicker.Date = new DateTimeOffset(today);
            EndDatePicker.Date = new DateTimeOffset(today);
            ApplyTimeFilterButton_Click(sender, e);
        }

        private void QuickSevenDaysFilterButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime today = DateTime.Today;
            StartDatePicker.Date = new DateTimeOffset(today.AddDays(-6));
            EndDatePicker.Date = new DateTimeOffset(today);
            ApplyTimeFilterButton_Click(sender, e);
        }

        private void QuickFourteenDaysFilterButton_Click(object sender, RoutedEventArgs e)
        {
            DateTime today = DateTime.Today;
            StartDatePicker.Date = new DateTimeOffset(today.AddDays(-13));
            EndDatePicker.Date = new DateTimeOffset(today);
            ApplyTimeFilterButton_Click(sender, e);
        }

        private void QuickAllFilterButton_Click(object sender, RoutedEventArgs e)
        {
            StartDatePicker.Date = null;
            EndDatePicker.Date = null;
            ApplyTimeFilterButton_Click(sender, e);
        }

        private void ClearTimeFilterButton_Click(object sender, RoutedEventArgs e)
        {
            StartDatePicker.Date = null;
            EndDatePicker.Date = null;
            ClipboardTimeFilterState.Clear();
            StatusText.Text = "时间筛选已清除。";
        }

        private async void ClearCurrentButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isFavoriteMode)
            {
                await ClearFavoritesAsync();
                return;
            }

            await ClearHistoryAsync();
            NavigateToClipboardHistoryPage();
        }

        private void NavigateToClipboardHistoryPage()
        {
            Frame?.Navigate(typeof(ClipboardPage));
        }

        private void UpdateClearActionTexts()
        {
            if (_isFavoriteMode)
            {
                ClearCurrentButton.Content = "清空常用列表";
                ClearCurrentDescriptionText.Text = "清空全部常用项目记录";
                return;
            }

            ClearCurrentButton.Content = "清空历史列表";
            ClearCurrentDescriptionText.Text = "清空全部剪切历史记录";
        }

        private static DateTime? BuildStartBoundary(DateTimeOffset? date)
        {
            if (!date.HasValue)
            {
                return null;
            }

            return date.Value.LocalDateTime.Date;
        }

        private static DateTime? BuildEndBoundary(DateTimeOffset? date)
        {
            if (!date.HasValue)
            {
                return null;
            }

            return date.Value.LocalDateTime.Date.AddDays(1).AddTicks(-1);
        }

        private async Task ClearHistoryAsync()
        {
            if (App.ClipboardModel == null)
            {
                StatusText.Text = "无法清空历史：模型不可用。";
                return;
            }

            await App.ClipboardModel.ClearAllAsync();
            StatusText.Text = "历史列表已清空。";
        }

        private async Task ClearFavoritesAsync()
        {
            await Task.Run(DatabaseService.ClearAllFavoriteClipboardItems);

            string favoriteImageFolder = Path.Combine(DatabaseService.AppDataFolder, FavoriteImageFolderName);
            if (Directory.Exists(favoriteImageFolder))
            {
                try
                {
                    Directory.Delete(favoriteImageFolder, true);
                }
                catch
                {
                }
            }

            StatusText.Text = "常用列表已清空。";
        }

        private void AttachClipboardItemsSubscription()
        {
            if (_isClipboardItemsSubscribed || App.ClipboardModel?.Items == null)
            {
                return;
            }

            App.ClipboardModel.Items.CollectionChanged += ClipboardItems_CollectionChanged;
            _isClipboardItemsSubscribed = true;
        }

        private void DetachClipboardItemsSubscription()
        {
            if (!_isClipboardItemsSubscribed || App.ClipboardModel?.Items == null)
            {
                return;
            }

            App.ClipboardModel.Items.CollectionChanged -= ClipboardItems_CollectionChanged;
            _isClipboardItemsSubscribed = false;
        }

        private void ClipboardItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (DispatcherQueue == null || DispatcherQueue.HasThreadAccess)
            {
                RefreshCategoryCounts();
                return;
            }

            _ = DispatcherQueue.TryEnqueue(RefreshCategoryCounts);
        }

        private void RefreshCategoryCounts()
        {
            var items = App.ClipboardModel?.Items ?? Enumerable.Empty<ClipboardItem>();

            int allCount = 0;
            int textCount = 0;
            int codeCount = 0;
            int jsonCount = 0;
            int longNumberCount = 0;
            int imageCount = 0;
            int fileCount = 0;
            int linkCount = 0;
            int emailCount = 0;

            foreach (ClipboardItem item in items)
            {
                allCount++;

                if (item.ContentType == ClipboardContentType.Text)
                {
                    textCount++;
                }

                switch (item.SemanticType)
                {
                    case ClipboardSemanticType.Code:
                        codeCount++;
                        break;
                    case ClipboardSemanticType.Json:
                        jsonCount++;
                        break;
                    case ClipboardSemanticType.LongNumber:
                        longNumberCount++;
                        break;
                    case ClipboardSemanticType.Image:
                        imageCount++;
                        break;
                    case ClipboardSemanticType.FilePath:
                        fileCount++;
                        break;
                    case ClipboardSemanticType.Link:
                        linkCount++;
                        break;
                    case ClipboardSemanticType.Email:
                        emailCount++;
                        break;
                }
            }

            SetCategoryCountBadge(AllCategoryCountBadge, AllCategoryCountText, allCount);
            SetCategoryCountBadge(TextCategoryCountBadge, TextCategoryCountText, textCount);
            SetCategoryCountBadge(CodeCategoryCountBadge, CodeCategoryCountText, codeCount);
            SetCategoryCountBadge(JsonCategoryCountBadge, JsonCategoryCountText, jsonCount);
            SetCategoryCountBadge(LongNumberCategoryCountBadge, LongNumberCategoryCountText, longNumberCount);
            SetCategoryCountBadge(ImageCategoryCountBadge, ImageCategoryCountText, imageCount);
            SetCategoryCountBadge(FileCategoryCountBadge, FileCategoryCountText, fileCount);
            SetCategoryCountBadge(LinkCategoryCountBadge, LinkCategoryCountText, linkCount);
            SetCategoryCountBadge(EmailCategoryCountBadge, EmailCategoryCountText, emailCount);
        }

        private static void SetCategoryCountBadge(Border badgeBorder, TextBlock countText, int count)
        {
            countText.Text = $"({count})";
            badgeBorder.Opacity = count > 0 ? 1.0 : 0.85;
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
    }
}
