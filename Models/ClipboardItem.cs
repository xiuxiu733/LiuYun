using System;
using System.ComponentModel;
using LiuYun.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LiuYun.Models
{
    public enum ClipboardContentType
    {
        Text = 0,
        Image = 1
    }

    public class ClipboardItem : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private static readonly ImageCacheService ImageCache = new ImageCacheService(10);
        private static volatile bool _thumbnailLoadingEnabled = true;

        private ClipboardContentType _contentType;
        private string _textContent = string.Empty;
        private string _imagePath = string.Empty;
        private bool _isPinned;
        private bool _isKeyboardSelected;
        private bool _isInlineActionsLoaded;
        private WeakReference<BitmapImage>? _weakImageSource;
        private ClipboardSemanticInfo? _semanticInfo;
        private string _contentHash = string.Empty;
        private bool _displayTextCacheDirty = true;
        private string _displayTextCache = string.Empty;

        public int Id { get; set; }

        public ClipboardContentType ContentType
        {
            get => _contentType;
            set
            {
                if (_contentType == value)
                {
                    return;
                }

                _contentType = value;
                _displayTextCacheDirty = true;
                InvalidateSemanticInfo();
                OnPropertyChanged(nameof(ContentType));
                OnPropertyChanged(nameof(IsImage));
                OnPropertyChanged(nameof(IsText));
                OnPropertyChanged(nameof(TextVisibility));
                OnPropertyChanged(nameof(ImageVisibility));
                OnPropertyChanged(nameof(OuterCardBackground));
                OnPropertyChanged(nameof(OuterCardBorderThickness));
                OnPropertyChanged(nameof(ContentPanelBackground));
                OnPropertyChanged(nameof(ContentPanelBorderThickness));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ImageSource));
            }
        }

        public string TextContent
        {
            get => _textContent;
            set
            {
                if (string.Equals(_textContent, value, StringComparison.Ordinal))
                {
                    return;
                }

                _textContent = value ?? string.Empty;
                _displayTextCacheDirty = true;
                InvalidateSemanticInfo();
                OnPropertyChanged(nameof(TextContent));
                OnPropertyChanged(nameof(DisplayText));
            }
        }

        public string ImagePath
        {
            get => _imagePath;
            set
            {
                if (string.Equals(_imagePath, value, StringComparison.Ordinal))
                {
                    return;
                }

                _imagePath = value ?? string.Empty;
                _weakImageSource = null;
                _displayTextCacheDirty = true;
                InvalidateSemanticInfo();
                OnPropertyChanged(nameof(ImagePath));
                OnPropertyChanged(nameof(DisplayText));
                OnPropertyChanged(nameof(ImageSource));
            }
        }

        private DateTime _timestamp;

        public DateTime Timestamp
        {
            get => _timestamp;
            set
            {
                if (_timestamp == value)
                {
                    return;
                }

                _timestamp = value;
                OnPropertyChanged(nameof(Timestamp));
                OnPropertyChanged(nameof(DisplayTime));
            }
        }

        public string ContentHash
        {
            get => _contentHash;
            set => _contentHash = value ?? string.Empty;
        }

        public bool IsPinned
        {
            get => _isPinned;
            set
            {
                if (_isPinned == value)
                {
                    return;
                }

                _isPinned = value;
                OnPropertyChanged(nameof(IsPinned));
                OnPropertyChanged(nameof(PinIcon));
            }
        }

        public bool IsKeyboardSelected
        {
            get => _isKeyboardSelected;
            set
            {
                if (_isKeyboardSelected == value)
                {
                    return;
                }

                _isKeyboardSelected = value;
                OnPropertyChanged(nameof(IsKeyboardSelected));
                OnPropertyChanged(nameof(OuterCardBackground));
                OnPropertyChanged(nameof(OuterCardBorderThickness));
                OnPropertyChanged(nameof(ContentPanelBackground));
                OnPropertyChanged(nameof(ContentPanelBorderThickness));
            }
        }

        public bool IsInlineActionsLoaded
        {
            get => _isInlineActionsLoaded;
            set
            {
                if (_isInlineActionsLoaded == value)
                {
                    return;
                }

                _isInlineActionsLoaded = value;
                OnPropertyChanged(nameof(IsInlineActionsLoaded));
            }
        }

        public string PinIcon => IsPinned ? "\uE840" : "\uE718";

        public string DisplayText
        {
            get
            {
                if (!_displayTextCacheDirty)
                {
                    return _displayTextCache;
                }

                if (ContentType == ClipboardContentType.Text)
                {
                    if (string.IsNullOrWhiteSpace(TextContent))
                    {
                        _displayTextCache = "(Empty)";
                    }
                    else
                    {
                        string singleLine = TextContent.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                        _displayTextCache = singleLine.Length > 80 ? singleLine.Substring(0, 80) + "..." : singleLine;
                    }
                }
                else
                {
                    _displayTextCache = ImagePath.Replace("\\", "/");
                }

                _displayTextCacheDirty = false;
                return _displayTextCache;
            }
        }

        public string DisplayTime
        {
            get
            {
                TimeSpan timeSpan = DateTime.Now - Timestamp;

                if (timeSpan.TotalMinutes < 1)
                {
                    return "刚刚";
                }

                if (timeSpan.TotalMinutes < 60)
                {
                    return $"{(int)timeSpan.TotalMinutes}分钟前";
                }

                if (timeSpan.TotalHours < 24)
                {
                    return $"{(int)timeSpan.TotalHours}小时前";
                }

                if (timeSpan.TotalDays < 7)
                {
                    return $"{(int)timeSpan.TotalDays}天前";
                }

                return Timestamp.ToString("yyyy-MM-dd HH:mm");
            }
        }

        public BitmapImage? ImageSource
        {
            get
            {
                if (_weakImageSource != null &&
                    _weakImageSource.TryGetTarget(out BitmapImage? weakCachedImage))
                {
                    return weakCachedImage;
                }

                if (!_thumbnailLoadingEnabled)
                {
                    return null;
                }

                if (ContentType != ClipboardContentType.Image ||
                    string.IsNullOrEmpty(ImagePath) ||
                    !System.IO.File.Exists(ImagePath))
                {
                    return null;
                }

                try
                {
                    BitmapImage? cachedImage = ImageCache.Get(ImagePath);
                    if (cachedImage != null)
                    {
                        _weakImageSource = new WeakReference<BitmapImage>(cachedImage);
                        return cachedImage;
                    }

                    BitmapImage newImage = new BitmapImage
                    {
                        DecodePixelWidth = 96,
                        CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                        UriSource = new Uri(ImagePath)
                    };

                    ImageCache.Put(ImagePath, newImage);
                    _weakImageSource = new WeakReference<BitmapImage>(newImage);
                    return newImage;
                }
                catch
                {
                    return null;
                }
            }
        }

        public bool IsImage => ContentType == ClipboardContentType.Image;
        public bool IsText => ContentType == ClipboardContentType.Text;

        public Visibility TextVisibility => IsText ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ImageVisibility => IsImage ? Visibility.Visible : Visibility.Collapsed;

        public string ContentKindLabel => SemanticInfo.Label;
        public string ContentKindGlyph => SemanticInfo.Glyph;
        public string ContentPreviewTitle => SemanticInfo.PreviewTitle;
        public string ContentPreviewSubtitle => SemanticInfo.PreviewSubtitle;
        public string ContentPreviewBody => SemanticInfo.PreviewBody;
        public string OpenActionGlyph => SemanticInfo.OpenActionGlyph;
        public string OpenActionTarget => SemanticInfo.OpenTarget;
        public ClipboardSemanticType SemanticType => SemanticInfo.Type;
        public bool HasOpenAction => SemanticInfo.HasOpenAction;
        public Visibility OpenActionVisibility => HasOpenAction ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ContentPreviewSubtitleVisibility =>
            string.IsNullOrWhiteSpace(ContentPreviewSubtitle) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ContentPreviewTitleVisibility =>
            string.IsNullOrWhiteSpace(ContentPreviewTitle) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility ContentPreviewBodyVisibility =>
            string.IsNullOrWhiteSpace(ContentPreviewBody) ? Visibility.Collapsed : Visibility.Visible;
        public string OuterCardBackground => "Transparent";
        public Thickness OuterCardBorderThickness => new Thickness(0);
        public string ContentPanelBackground => IsKeyboardSelected ? "#22FFFFFF" : "#15000000";
        public Thickness ContentPanelBorderThickness => new Thickness(1);

        public void ClearImageCache()
        {
            if (_weakImageSource != null &&
                _weakImageSource.TryGetTarget(out BitmapImage? cachedImage))
            {
                try
                {
                    cachedImage.UriSource = null;
                }
                catch
                {
                }
            }

            _weakImageSource = null;

            if (!string.IsNullOrEmpty(ImagePath))
            {
                ImageCache.Remove(ImagePath);
            }
        }

        public static void ClearGlobalImageCache()
        {
            ImageCache.Clear();
        }

        public static void SetThumbnailLoadingEnabled(bool enabled)
        {
            _thumbnailLoadingEnabled = enabled;
        }

        public void WarmupSemanticInfo()
        {
            _ = SemanticInfo;
        }

        private ClipboardSemanticInfo SemanticInfo =>
            _semanticInfo ??= ClipboardContentClassifierService.Classify(this);

        private void InvalidateSemanticInfo()
        {
            _semanticInfo = null;
            OnPropertyChanged(nameof(ContentKindLabel));
            OnPropertyChanged(nameof(ContentKindGlyph));
            OnPropertyChanged(nameof(ContentPreviewTitle));
            OnPropertyChanged(nameof(ContentPreviewSubtitle));
            OnPropertyChanged(nameof(ContentPreviewBody));
            OnPropertyChanged(nameof(ContentPreviewTitleVisibility));
            OnPropertyChanged(nameof(ContentPreviewSubtitleVisibility));
            OnPropertyChanged(nameof(ContentPreviewBodyVisibility));
            OnPropertyChanged(nameof(OpenActionGlyph));
            OnPropertyChanged(nameof(OpenActionTarget));
            OnPropertyChanged(nameof(SemanticType));
            OnPropertyChanged(nameof(HasOpenAction));
            OnPropertyChanged(nameof(OpenActionVisibility));
        }

        private void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
