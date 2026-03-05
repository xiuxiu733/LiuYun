using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using LiuYun.Models;

namespace LiuYun.Services
{
    public class ClipboardMonitorService : IDisposable
    {
        private enum ImageSaveResult
        {
            Saved,
            Duplicate,
            Failed
        }

        private enum StorageImageAttemptStatus
        {
            NoCandidate,
            Saved,
            Duplicate,
            Failed
        }

        private readonly DispatcherQueue _dispatcherQueue;
        private AllClipboardItems? _clipboardModel;
        private int _disposed = 0;
        private const int MaxHistoryItems = 300;
        private const int MaxTextSizeBytes = 512 * 1024;
        private const int MaxImageSizeBytes = 20 * 1024 * 1024;
        private static readonly HashSet<string> ImageFileExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic"
        };
        private static readonly string[] CustomImageFormatCandidates =
        {
            "PNG",
            "image/png",
            "JFIF",
            "image/jpeg",
            "image/bmp",
            "image/gif"
        };

        private readonly Channel<byte> _clipboardEventQueue;
        private readonly Task _eventProcessingTask;

        private readonly SemaphoreSlim _clipboardReadLock = new SemaphoreSlim(1, 1);

        private long _lastEventTimeTicks = 0;
        private const int MinEventIntervalMs = 0;

        private DateTime _lastOrphanImageCleanupTimeUtc = DateTime.MinValue;
        private static readonly TimeSpan OrphanImageCleanupInterval = TimeSpan.FromDays(1);
        private readonly SemaphoreSlim _orphanImageCleanupLock = new SemaphoreSlim(1, 1);

        private long _eventsDropped = 0;
        private long _eventsProcessed = 0;
        private long _captureSequence = 0;
        private const int ClipboardDispatcherTimeoutMs = 5000;

        private sealed class ImageTooLargeException : InvalidOperationException
        {
            public ImageTooLargeException(long sizeBytes)
                : base($"Clipboard image exceeds the configured size limit: {sizeBytes} bytes.")
            {
            }
        }

        public ClipboardMonitorService(DispatcherQueue dispatcherQueue)
        {
            _dispatcherQueue = dispatcherQueue;

            var options = new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.DropOldest
            };
            _clipboardEventQueue = Channel.CreateBounded<byte>(options);

            _eventProcessingTask = Task.Run(ProcessClipboardEventsAsync);
        }

        private static void LogDiag(string message)
        {
            System.Diagnostics.Debug.WriteLine($"CLIP_DIAG|{message}");
        }

        private static string SummarizeFormats(DataPackageView dataPackageView)
        {
            try
            {
                var formats = dataPackageView.AvailableFormats?.ToArray() ?? Array.Empty<string>();
                if (formats.Length == 0)
                {
                    return "(none)";
                }

                const int maxPreview = 12;
                string preview = string.Join(",", formats.Take(maxPreview));
                return formats.Length > maxPreview
                    ? $"{preview},...(+{formats.Length - maxPreview})"
                    : preview;
            }
            catch (Exception ex)
            {
                return $"(error:{ex.GetType().Name})";
            }
        }

        private static string ShortHash(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        private static bool HasAnySupportedClipboardFormat(DataPackageView dataPackageView)
        {
            if (dataPackageView.Contains(StandardDataFormats.Text) ||
                dataPackageView.Contains(StandardDataFormats.Bitmap) ||
                dataPackageView.Contains(StandardDataFormats.StorageItems))
            {
                return true;
            }

            return dataPackageView.AvailableFormats.Any(formatId =>
                CustomImageFormatCandidates.Any(candidate =>
                    string.Equals(formatId, candidate, StringComparison.OrdinalIgnoreCase)));
        }

        private async Task<DataPackageView> RetryForDelayedClipboardFormatsAsync(DataPackageView initialView, long captureId)
        {
            DataPackageView currentView = initialView;
            if (HasAnySupportedClipboardFormat(currentView))
            {
                return currentView;
            }

            int[] retryDelaysMs = { 80, 120, 180 };
            for (int attempt = 0; attempt < retryDelaysMs.Length; attempt++)
            {
                int delayMs = retryDelaysMs[attempt];
                LogDiag($"DELAYED_FORMAT_RETRY|capture={captureId}|attempt={attempt + 1}|delayMs={delayMs}");
                await Task.Delay(delayMs);

                var refreshedView = await TryGetClipboardWithRetryAsync();
                if (refreshedView == null)
                {
                    LogDiag($"DELAYED_FORMAT_RETRY_EMPTY|capture={captureId}|attempt={attempt + 1}");
                    continue;
                }

                currentView = refreshedView;
                if (HasAnySupportedClipboardFormat(currentView))
                {
                    LogDiag($"DELAYED_FORMAT_RETRY_HIT|capture={captureId}|attempt={attempt + 1}");
                    break;
                }
            }

            return currentView;
        }

        public void Initialize(AllClipboardItems clipboardModel)
        {
            _clipboardModel = clipboardModel;
            Clipboard.ContentChanged += Clipboard_ContentChanged;
            _ = TryRunClipboardImageCleanupAsync(force: true);
        }

        private async Task ProcessClipboardEventsAsync()
        {
            try
            {
                await foreach (var _ in _clipboardEventQueue.Reader.ReadAllAsync())
                {
                    if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
                        break;

                    try
                    {
                        await CaptureClipboardContentAsync();

                        Interlocked.Increment(ref _eventsProcessed);

                        if (_eventsProcessed % 100 == 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"Clipboard events: processed={_eventsProcessed}, dropped={_eventsDropped}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Clipboard capture error: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard event processing stopped: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            try
            {
                Clipboard.ContentChanged -= Clipboard_ContentChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error disposing clipboard monitor: {ex.Message}");
            }

            _clipboardEventQueue.Writer.Complete();

            _ = _eventProcessingTask.ContinueWith(
                task =>
                {
                    if (task.IsFaulted)
                    {
                        System.Diagnostics.Debug.WriteLine($"Clipboard event processing ended with error: {task.Exception?.GetBaseException().Message}");
                    }

                    _clipboardReadLock.Dispose();
                    _orphanImageCleanupLock.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            _clipboardModel = null;
        }

        private void Clipboard_ContentChanged(object? sender, object e)
        {
            if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
            {
                LogDiag("DROP_DISPOSED_EVENT");
                return;
            }

            long nowTicks = DateTime.UtcNow.Ticks;
            long lastTicks = Interlocked.Read(ref _lastEventTimeTicks);
            long elapsedMs = (nowTicks - lastTicks) / TimeSpan.TicksPerMillisecond;

            if (MinEventIntervalMs > 0 && elapsedMs < MinEventIntervalMs)
            {
                Interlocked.Increment(ref _eventsDropped);
                System.Diagnostics.Debug.WriteLine($"Clipboard event dropped (too frequent: {elapsedMs}ms), total dropped: {_eventsDropped}");
                LogDiag($"DROP_TOO_FREQUENT|elapsedMs={elapsedMs}|dropped={_eventsDropped}");
                return;
            }

            Interlocked.Exchange(ref _lastEventTimeTicks, nowTicks);

            if (!_clipboardEventQueue.Writer.TryWrite(0))
            {
                Interlocked.Increment(ref _eventsDropped);
                System.Diagnostics.Debug.WriteLine($"Clipboard event dropped (queue full), total dropped: {_eventsDropped}");
                LogDiag($"DROP_QUEUE_FULL|dropped={_eventsDropped}");
            }
        }

        private async Task CaptureClipboardContentAsync()
        {
            long captureId = Interlocked.Increment(ref _captureSequence);
            var clipboardModel = _clipboardModel;
            if (clipboardModel == null)
            {
                LogDiag($"DROP_NO_MODEL|capture={captureId}");
                return;
            }

            DataPackageView? dataPackageView;
            bool hasText;
            bool hasBitmap;
            bool hasStorageItems;

            if (!await _clipboardReadLock.WaitAsync(ClipboardDispatcherTimeoutMs))
            {
                System.Diagnostics.Debug.WriteLine("Clipboard read timeout after 5s");
                LogDiag($"DROP_READ_LOCK_TIMEOUT|capture={captureId}");
                return;
            }

            try
            {
                LogDiag($"CAPTURE_START|capture={captureId}");
                dataPackageView = await TryGetClipboardWithRetryAsync();
                if (dataPackageView == null)
                {
                    LogDiag($"DROP_NO_CONTENT|capture={captureId}");
                    return;
                }

                dataPackageView = await RetryForDelayedClipboardFormatsAsync(dataPackageView, captureId);

                hasText = dataPackageView.Contains(StandardDataFormats.Text);
                hasBitmap = dataPackageView.Contains(StandardDataFormats.Bitmap);
                hasStorageItems = dataPackageView.Contains(StandardDataFormats.StorageItems);
                LogDiag($"FORMATS|capture={captureId}|text={hasText}|bitmap={hasBitmap}|storage={hasStorageItems}|formats={SummarizeFormats(dataPackageView)}");
            }
            finally
            {
                _clipboardReadLock.Release();
            }

            if (dataPackageView == null)
            {
                return;
            }

            try
            {
                bool imageSavedFromBitmap = false;
                StorageImageAttemptStatus storageImageStatus = StorageImageAttemptStatus.NoCandidate;

                bool imageSavedFromStorageItems = false;
                bool textSavedFromStorageItems = false;

                if (hasStorageItems && hasBitmap)
                {
                    try
                    {
                        LogDiag($"STORAGE_PRIORITY_WITH_BITMAP|capture={captureId}");
                        storageImageStatus = await TrySaveStorageItemsImageAsync(dataPackageView);
                        imageSavedFromStorageItems = storageImageStatus == StorageImageAttemptStatus.Saved;
                        LogDiag($"STORAGE_IMAGE_RESULT|capture={captureId}|saved={imageSavedFromStorageItems}|status={storageImageStatus}");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving storage image (priority): {ex.Message}");
                        storageImageStatus = StorageImageAttemptStatus.Failed;
                        LogDiag($"STORAGE_BRANCH_ERROR|capture={captureId}|type={ex.GetType().Name}");
                    }
                }

                if (hasBitmap && !imageSavedFromStorageItems && storageImageStatus != StorageImageAttemptStatus.Duplicate)
                {
                    try
                    {
                        byte[]? imageBytes = null;
                        var bitmapRef = await TryGetBitmapReferenceAsync(dataPackageView);
                        if (bitmapRef != null)
                        {
                            try
                            {
                                using var stream = await bitmapRef.OpenReadAsync();
                                imageBytes = await ReadRandomAccessStreamToBytesAsync(stream, MaxImageSizeBytes);
                            }
                            catch (ImageTooLargeException)
                            {
                                LogDiag($"BITMAP_OVERSIZE|capture={captureId}");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Error reading bitmap stream: {ex.Message}");
                            }
                        }

                        if (imageBytes != null && imageBytes.Length > 0)
                        {
                            ImageSaveResult bitmapSaveResult = await SaveClipboardImageBytesAsync(imageBytes);
                            imageSavedFromBitmap = bitmapSaveResult == ImageSaveResult.Saved;
                            imageBytes = null;
                            LogDiag($"SAVE_IMAGE_BITMAP_BRANCH|capture={captureId}|saved={imageSavedFromBitmap}|result={bitmapSaveResult}");
                        }
                        else
                        {
                            LogDiag($"BITMAP_EMPTY|capture={captureId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving bitmap: {ex.Message}");
                        LogDiag($"BITMAP_BRANCH_ERROR|capture={captureId}|type={ex.GetType().Name}");
                    }
                }
                else if (hasBitmap && storageImageStatus == StorageImageAttemptStatus.Duplicate)
                {
                    LogDiag($"BITMAP_SKIPPED_AFTER_STORAGE_DUPLICATE|capture={captureId}");
                }

                if (hasStorageItems && !hasBitmap)
                {
                    try
                    {
                        storageImageStatus = await TrySaveStorageItemsImageAsync(dataPackageView);
                        imageSavedFromStorageItems = storageImageStatus == StorageImageAttemptStatus.Saved;
                        LogDiag($"STORAGE_IMAGE_RESULT|capture={captureId}|saved={imageSavedFromStorageItems}|status={storageImageStatus}");
                        if (!imageSavedFromStorageItems && storageImageStatus != StorageImageAttemptStatus.Duplicate)
                        {
                            string? storageItemsText = await TryReadStorageItemsTextAsync(dataPackageView);
                            if (!string.IsNullOrWhiteSpace(storageItemsText))
                            {
                                await SaveClipboardTextAsync(storageItemsText, clipboardModel);
                                textSavedFromStorageItems = true;
                                LogDiag($"SAVE_TEXT_STORAGE_BRANCH|capture={captureId}");
                            }
                            else
                            {
                                LogDiag($"STORAGE_TEXT_EMPTY|capture={captureId}");
                            }
                        }
                        else if (storageImageStatus == StorageImageAttemptStatus.Duplicate)
                        {
                            LogDiag($"STORAGE_TEXT_SKIPPED_DUPLICATE_IMAGE|capture={captureId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving storage items as text: {ex.Message}");
                        LogDiag($"STORAGE_BRANCH_ERROR|capture={captureId}|type={ex.GetType().Name}");
                    }
                }

                bool imageSavedFromCustomFormats = false;
                bool hasAnyStandardImageFormat = hasBitmap || hasStorageItems;
                if (!hasAnyStandardImageFormat)
                {
                    imageSavedFromCustomFormats = await TrySaveCustomImageFormatAsync(dataPackageView);
                    LogDiag($"CUSTOM_IMAGE_RESULT|capture={captureId}|saved={imageSavedFromCustomFormats}");
                }
                else
                {
                    LogDiag($"CUSTOM_SKIPPED_STANDARD_FORMAT_PRESENT|capture={captureId}|bitmap={hasBitmap}|storage={hasStorageItems}");
                }

                if (hasText &&
                    !hasBitmap &&
                    !imageSavedFromStorageItems &&
                    !imageSavedFromCustomFormats &&
                    !textSavedFromStorageItems)
                {
                    try
                    {
                        string text = await dataPackageView.GetTextAsync();
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            await SaveClipboardTextAsync(text, clipboardModel);
                            LogDiag($"SAVE_TEXT_TEXT_BRANCH|capture={captureId}");
                        }
                        else
                        {
                            LogDiag($"TEXT_EMPTY|capture={captureId}");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Error saving text: {ex.Message}");
                        LogDiag($"TEXT_BRANCH_ERROR|capture={captureId}|type={ex.GetType().Name}");
                    }
                }

                if (!hasText && !hasBitmap && !hasStorageItems && !imageSavedFromCustomFormats)
                {
                    LogDiag($"DROP_NO_SUPPORTED_STANDARD_FORMAT|capture={captureId}");
                }

                LogDiag($"CAPTURE_END|capture={captureId}|bitmap={imageSavedFromBitmap}|storageImage={imageSavedFromStorageItems}|customImage={imageSavedFromCustomFormats}|storageText={textSavedFromStorageItems}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error capturing clipboard: {ex.Message}");
                LogDiag($"CAPTURE_ERROR|capture={captureId}|type={ex.GetType().Name}");
            }
        }

        private async Task<string?> TryReadStorageItemsTextAsync(DataPackageView dataPackageView)
        {
            var items = await TryGetStorageItemsAsync(dataPackageView);
            if (items == null || items.Count == 0)
            {
                LogDiag("STORAGE_TEXT_NO_ITEMS");
                return null;
            }

            var paths = items
                .Select(item => item.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToArray();

            return paths.Length == 0 ? null : string.Join(Environment.NewLine, paths);
        }

        private async Task<StorageImageAttemptStatus> TrySaveStorageItemsImageAsync(DataPackageView dataPackageView)
        {
            var items = await TryGetStorageItemsAsync(dataPackageView);
            if (items == null || items.Count == 0)
            {
                LogDiag("STORAGE_IMAGE_NO_ITEMS");
                return StorageImageAttemptStatus.NoCandidate;
            }

            bool hasImageCandidate = false;
            bool hasDuplicateCandidate = false;

            foreach (var item in items)
            {
                if (item is not StorageFile file)
                {
                    continue;
                }

                string extension = Path.GetExtension(file.Name);
                if (string.IsNullOrWhiteSpace(extension) || !ImageFileExtensions.Contains(extension))
                {
                    continue;
                }

                hasImageCandidate = true;

                try
                {
                    LogDiag($"STORAGE_IMAGE_CANDIDATE|path={file.Path}");
                    ImageSaveResult saveResult;
                    if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase))
                    {
                        saveResult = await SaveClipboardImageFromStorageFileAsync(file);
                    }
                    else
                    {
                        using var sourceStream = await file.OpenReadAsync();
                        byte[] imageBytes = await ReadRandomAccessStreamToBytesAsync(sourceStream, MaxImageSizeBytes);
                        if (imageBytes.Length == 0)
                        {
                            continue;
                        }

                        saveResult = await SaveClipboardImageBytesAsync(imageBytes);
                    }

                    if (saveResult == ImageSaveResult.Saved)
                    {
                        return StorageImageAttemptStatus.Saved;
                    }

                    if (saveResult == ImageSaveResult.Duplicate)
                    {
                        hasDuplicateCandidate = true;
                    }
                }
                catch (Exception ex)
                {
                    if (ex is ImageTooLargeException)
                    {
                        LogDiag("STORAGE_IMAGE_OVERSIZE");
                    }

                    System.Diagnostics.Debug.WriteLine($"Error reading storage image file: {ex.Message}");
                    LogDiag($"STORAGE_IMAGE_READ_ERROR|type={ex.GetType().Name}");
                }
            }

            if (!hasImageCandidate)
            {
                return StorageImageAttemptStatus.NoCandidate;
            }

            return hasDuplicateCandidate
                ? StorageImageAttemptStatus.Duplicate
                : StorageImageAttemptStatus.Failed;
        }

        private async Task<ImageSaveResult> SaveClipboardImageFromStorageFileAsync(StorageFile sourceFile)
        {
            var clipboardModel = _clipboardModel;
            if (clipboardModel == null || sourceFile == null)
            {
                return ImageSaveResult.Failed;
            }

            string extension = Path.GetExtension(sourceFile.Name);
            if (string.IsNullOrWhiteSpace(extension))
            {
                extension = ".png";
            }

            string imageDirectory = Path.Combine(DatabaseService.AppDataFolder, "clipboard_images");
            Directory.CreateDirectory(imageDirectory);

            string fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}";
            string targetPath = Path.Combine(imageDirectory, fileName);

            try
            {
                using var sourceRandomAccessStream = await sourceFile.OpenReadAsync();
                if (sourceRandomAccessStream.Size > MaxImageSizeBytes)
                {
                    LogDiag($"STORAGE_FASTPATH_OVERSIZE|bytes={sourceRandomAccessStream.Size}");
                    return ImageSaveResult.Failed;
                }

                sourceRandomAccessStream.Seek(0);
                using Stream sourceStream = sourceRandomAccessStream.AsStreamForRead();
                using var destinationStream = new FileStream(
                    targetPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 131072,
                    useAsync: true);

                string imageHash = await CopyStreamAndComputeSha256Async(sourceStream, destinationStream);
                await destinationStream.FlushAsync();

                var item = new ClipboardItem
                {
                    ContentType = ClipboardContentType.Image,
                    ImagePath = targetPath,
                    Timestamp = DateTime.Now,
                    ContentHash = imageHash
                };
                await AddItemToCollectionAsync(item, clipboardModel);
                LogDiag("IMAGE_SAVED_STORAGE_FASTPATH");
                return ImageSaveResult.Saved;
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                }
                catch
                {
                }

                System.Diagnostics.Debug.WriteLine($"Error saving storage image directly: {ex.Message}");
                LogDiag($"STORAGE_FASTPATH_ERROR|type={ex.GetType().Name}");
                return ImageSaveResult.Failed;
            }
        }

        private static async Task<string> CopyStreamAndComputeSha256Async(Stream source, Stream destination)
        {
            using var sha256 = SHA256.Create();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(131072);
            try
            {
                int bytesRead;
                while ((bytesRead = await source.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    sha256.TransformBlock(buffer, 0, bytesRead, null, 0);
                    await destination.WriteAsync(buffer, 0, bytesRead);
                }

                sha256.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return BitConverter.ToString(sha256.Hash!).Replace("-", "").ToLowerInvariant();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private async Task<IRandomAccessStreamReference?> TryGetBitmapReferenceAsync(DataPackageView dataPackageView)
        {
            var tcs = new TaskCompletionSource<IRandomAccessStreamReference?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var bitmapRef = await dataPackageView.GetBitmapAsync();
                    tcs.TrySetResult(bitmapRef);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading bitmap reference: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued)
            {
                LogDiag("BITMAP_ENQUEUE_FAILED");
                return null;
            }

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(ClipboardDispatcherTimeoutMs));
            if (completedTask != tcs.Task)
            {
                LogDiag("BITMAP_TIMEOUT");
                return null;
            }

            return await tcs.Task;
        }

        private async Task<IReadOnlyList<IStorageItem>?> TryGetStorageItemsAsync(DataPackageView dataPackageView)
        {
            var tcs = new TaskCompletionSource<IReadOnlyList<IStorageItem>?>(TaskCreationOptions.RunContinuationsAsynchronously);

            bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    var items = await dataPackageView.GetStorageItemsAsync();
                    tcs.TrySetResult(items);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading storage items: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued)
            {
                LogDiag("STORAGE_ITEMS_ENQUEUE_FAILED");
                return null;
            }

            var timeoutTask = Task.Delay(ClipboardDispatcherTimeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask != tcs.Task)
            {
                LogDiag("STORAGE_ITEMS_TIMEOUT");
                return null;
            }

            return await tcs.Task;
        }

        private async Task<bool> TrySaveCustomImageFormatAsync(DataPackageView dataPackageView)
        {
            foreach (var formatId in CustomImageFormatCandidates)
            {
                if (!dataPackageView.AvailableFormats.Any(format => string.Equals(format, formatId, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                LogDiag($"CUSTOM_FORMAT_MATCH|format={formatId}");
                byte[]? imageBytes = await TryReadCustomFormatImageBytesAsync(dataPackageView, formatId);
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    LogDiag($"CUSTOM_FORMAT_EMPTY|format={formatId}");
                    continue;
                }

                ImageSaveResult saveResult = await SaveClipboardImageBytesAsync(imageBytes);
                bool saved = saveResult == ImageSaveResult.Saved;
                LogDiag($"CUSTOM_FORMAT_SAVED|format={formatId}|saved={saved}|result={saveResult}");
                if (saved)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task<byte[]?> TryReadCustomFormatImageBytesAsync(DataPackageView dataPackageView, string formatId)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool enqueued = _dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    object data = await dataPackageView.GetDataAsync(formatId);
                    tcs.TrySetResult(data);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error reading clipboard format {formatId}: {ex.Message}");
                    tcs.TrySetResult(null);
                }
            });

            if (!enqueued)
            {
                LogDiag($"CUSTOM_FORMAT_ENQUEUE_FAILED|format={formatId}");
                return null;
            }

            var timeoutTask = Task.Delay(ClipboardDispatcherTimeoutMs);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            if (completedTask != tcs.Task)
            {
                LogDiag($"CUSTOM_FORMAT_TIMEOUT|format={formatId}");
                return null;
            }

            object? data = await tcs.Task;
            try
            {
                return await ConvertClipboardDataToBytesAsync(data, MaxImageSizeBytes);
            }
            catch (ImageTooLargeException)
            {
                LogDiag($"CUSTOM_FORMAT_OVERSIZE|format={formatId}");
                return null;
            }
        }

        private static async Task<byte[]?> ConvertClipboardDataToBytesAsync(object? data, int maxBytes)
        {
            if (data == null)
            {
                return null;
            }

            if (data is byte[] bytes)
            {
                if (bytes.Length > maxBytes)
                {
                    throw new ImageTooLargeException(bytes.Length);
                }

                return bytes;
            }

            if (data is IRandomAccessStreamReference streamRef)
            {
                using var readStream = await streamRef.OpenReadAsync();
                return await ReadRandomAccessStreamToBytesAsync(readStream, maxBytes);
            }

            if (data is IRandomAccessStream randomAccessStream)
            {
                try
                {
                    randomAccessStream.Seek(0);
                }
                catch
                {
                }

                return await ReadRandomAccessStreamToBytesAsync(randomAccessStream, maxBytes);
            }

            if (data is Stream streamData)
            {
                if (streamData.CanSeek)
                {
                    streamData.Position = 0;
                }

                return await ReadStreamToBytesAsync(streamData, streamData.CanSeek ? streamData.Length : null, maxBytes);
            }

            if (data is StorageFile file)
            {
                using var fileStream = await file.OpenReadAsync();
                return await ReadRandomAccessStreamToBytesAsync(fileStream, maxBytes);
            }

            return null;
        }

        private static async Task<byte[]> NormalizeImageBytesToPngAsync(byte[] data)
        {
            try
            {
                using var input = new InMemoryRandomAccessStream();
                using (IOutputStream output = input.GetOutputStreamAt(0))
                using (var writer = new DataWriter(output))
                {
                    writer.WriteBytes(data);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                    writer.DetachStream();
                }

                input.Seek(0);
                BitmapDecoder decoder = await BitmapDecoder.CreateAsync(input);
                PixelDataProvider pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    new BitmapTransform(),
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = pixelData.DetachPixelData();

                using var outputStream = new InMemoryRandomAccessStream();
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    decoder.PixelWidth,
                    decoder.PixelHeight,
                    96,
                    96,
                    pixels);
                await encoder.FlushAsync();

                outputStream.Seek(0);
                return await ReadRandomAccessStreamToBytesAsync(outputStream, MaxImageSizeBytes);
            }
            catch
            {
                return data;
            }
        }

        private static bool IsPngBytes(byte[] data)
        {
            if (data.Length < 8)
            {
                return false;
            }

            return data[0] == 0x89 &&
                   data[1] == 0x50 &&
                   data[2] == 0x4E &&
                   data[3] == 0x47 &&
                   data[4] == 0x0D &&
                   data[5] == 0x0A &&
                   data[6] == 0x1A &&
                   data[7] == 0x0A;
        }

        private static string? TryGetImageFileExtension(byte[] data)
        {
            if (data == null || data.Length < 4)
            {
                return null;
            }

            if (IsPngBytes(data))
            {
                return ".png";
            }

            if (data.Length >= 2 &&
                data[0] == 0xFF &&
                data[1] == 0xD8)
            {
                return ".jpg";
            }

            if (data[0] == 0x42 &&
                data[1] == 0x4D)
            {
                return ".bmp";
            }

            if (data.Length >= 6 &&
                data[0] == 0x47 &&
                data[1] == 0x49 &&
                data[2] == 0x46 &&
                data[3] == 0x38 &&
                (data[4] == 0x37 || data[4] == 0x39) &&
                data[5] == 0x61)
            {
                return ".gif";
            }

            if (data.Length >= 12 &&
                data[0] == 0x52 &&
                data[1] == 0x49 &&
                data[2] == 0x46 &&
                data[3] == 0x46 &&
                data[8] == 0x57 &&
                data[9] == 0x45 &&
                data[10] == 0x42 &&
                data[11] == 0x50)
            {
                return ".webp";
            }

            if (data.Length >= 4 &&
                ((data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) ||
                 (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A)))
            {
                return ".tiff";
            }

            return null;
        }

        private static async Task<byte[]> ReadRandomAccessStreamToBytesAsync(IRandomAccessStream stream, int maxBytes = int.MaxValue)
        {
            stream.Seek(0);
            long sizeHint = stream.Size > int.MaxValue ? 0 : (long)stream.Size;
            using Stream readStream = stream.AsStreamForRead();
            return await ReadStreamToBytesAsync(readStream, sizeHint > 0 ? sizeHint : null, maxBytes);
        }

        private static async Task<byte[]> ReadStreamToBytesAsync(Stream stream, long? sizeHint, int maxBytes = int.MaxValue)
        {
            if (sizeHint.HasValue && sizeHint.Value > maxBytes)
            {
                throw new ImageTooLargeException(sizeHint.Value);
            }

            if (sizeHint.HasValue && sizeHint.Value > 0 && sizeHint.Value <= int.MaxValue)
            {
                int expectedLength = (int)sizeHint.Value;
                byte[] buffer = new byte[expectedLength];
                int offset = 0;
                while (offset < expectedLength)
                {
                    int read = await stream.ReadAsync(buffer, offset, expectedLength - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }

                if (offset == expectedLength)
                {
                    return buffer;
                }

                if (offset <= 0)
                {
                    return Array.Empty<byte>();
                }

                byte[] truncated = new byte[offset];
                System.Buffer.BlockCopy(buffer, 0, truncated, 0, offset);
                return truncated;
            }

            using var memoryStream = new MemoryStream();
            byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                long totalRead = 0;
                while ((read = await stream.ReadAsync(copyBuffer, 0, copyBuffer.Length)) > 0)
                {
                    totalRead += read;
                    if (totalRead > maxBytes)
                    {
                        throw new ImageTooLargeException(totalRead);
                    }

                    await memoryStream.WriteAsync(copyBuffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(copyBuffer);
            }

            return memoryStream.ToArray();
        }

        private async Task SaveClipboardTextAsync(string text, AllClipboardItems clipboardModel)
        {
            int textSizeBytes = text.Length * 2;
            if (textSizeBytes > MaxTextSizeBytes)
            {
                System.Diagnostics.Debug.WriteLine($"Text too large: {textSizeBytes / 1024}KB, truncating to {MaxTextSizeBytes / 1024}KB");
                LogDiag($"TEXT_TRUNCATED|kb={textSizeBytes / 1024}");
                int maxChars = MaxTextSizeBytes / 2;
                text = text.Substring(0, maxChars) + "\n\n[... 文本过长已截断 ...]";
            }

            string textHash = ComputeTextHash(text);

            var item = new ClipboardItem
            {
                ContentType = ClipboardContentType.Text,
                TextContent = text,
                Timestamp = DateTime.Now,
                ContentHash = textHash
            };

            await AddItemToCollectionAsync(item, clipboardModel);
            LogDiag("TEXT_SAVED");
        }

        private async Task<ImageSaveResult> SaveClipboardImageBytesAsync(byte[] imageBytes)
        {
            var clipboardModel = _clipboardModel;
            if (clipboardModel == null || imageBytes == null || imageBytes.Length == 0)
            {
                return ImageSaveResult.Failed;
            }

            if (imageBytes.Length > MaxImageSizeBytes)
            {
                LogDiag($"IMAGE_BYTES_OVERSIZE|bytes={imageBytes.Length}");
                return ImageSaveResult.Failed;
            }

            try
            {
                bool isMainWindowVisible = true;
                if (global::Microsoft.UI.Xaml.Application.Current is LiuYun.App app)
                {
                    isMainWindowVisible = app.IsMainWindowVisible;
                }

                bool inputIsPng = IsPngBytes(imageBytes);
                string? inputExtension = TryGetImageFileExtension(imageBytes);

                byte[] imageBytesToStore = imageBytes;
                if (!inputIsPng)
                {
                    bool shouldNormalizeToPng = isMainWindowVisible || string.IsNullOrWhiteSpace(inputExtension);
                    if (shouldNormalizeToPng)
                    {
                        imageBytesToStore = await NormalizeImageBytesToPngAsync(imageBytes);
                    }
                }

                if (imageBytesToStore == null || imageBytesToStore.Length == 0)
                {
                    return ImageSaveResult.Failed;
                }

                if (imageBytesToStore.Length > MaxImageSizeBytes)
                {
                    LogDiag($"IMAGE_NORMALIZED_OVERSIZE|bytes={imageBytesToStore.Length}");
                    return ImageSaveResult.Failed;
                }

                string imageHash = ComputeSHA256Hash(imageBytesToStore);


                string extension = IsPngBytes(imageBytesToStore)
                    ? ".png"
                    : (TryGetImageFileExtension(imageBytesToStore) ?? ".png");
                string fileName = $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{extension}";
                string imagePath = Path.Combine(DatabaseService.AppDataFolder, "clipboard_images");

                if (!Directory.Exists(imagePath))
                {
                    Directory.CreateDirectory(imagePath);
                }

                string fullPath = Path.Combine(imagePath, fileName);

                await File.WriteAllBytesAsync(fullPath, imageBytesToStore);
                LogDiag($"IMAGE_FILE_WRITTEN|path={fullPath}|hash={ShortHash(imageHash)}");

                var item = new ClipboardItem
                {
                    ContentType = ClipboardContentType.Image,
                    ImagePath = fullPath,
                    Timestamp = DateTime.Now,
                    ContentHash = imageHash
                };
                await AddItemToCollectionAsync(item, clipboardModel);
                LogDiag("IMAGE_SAVED");

                return ImageSaveResult.Saved;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving clipboard image: {ex.Message}");
                LogDiag($"IMAGE_SAVE_ERROR|type={ex.GetType().Name}");
                return ImageSaveResult.Failed;
            }
        }

        private static string ComputeSHA256Hash(byte[] data)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(data);
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string ComputeTextHash(string text)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private async Task<DataPackageView?> TryGetClipboardWithRetryAsync(int maxRetries = 3)
        {
            const int baseDelayMs = 50;

            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tcs = new TaskCompletionSource<DataPackageView?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    bool enqueued = _dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            var content = Clipboard.GetContent();
                            tcs.TrySetResult(content);
                        }
                        catch (Exception ex)
                        {
                            tcs.TrySetException(ex);
                        }
                    });

                    if (!enqueued)
                    {
                        System.Diagnostics.Debug.WriteLine("Failed to enqueue clipboard access");
                        LogDiag("GET_CONTENT_ENQUEUE_FAILED");
                        return null;
                    }

                    var timeoutTask = Task.Delay(ClipboardDispatcherTimeoutMs);
                    var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                    if (completedTask == timeoutTask)
                    {
                        System.Diagnostics.Debug.WriteLine("Clipboard access timed out");
                        LogDiag("GET_CONTENT_TIMEOUT");
                        return null;
                    }

                    return await tcs.Task;
                }
                catch (COMException ex) when (
                    ex.HResult == unchecked((int)0x800401D0) ||
                    ex.HResult == unchecked((int)0x80040155) ||
                    ex.HResult == unchecked((int)0x80004005))
                {
                    if (attempt == maxRetries - 1)
                    {
                        System.Diagnostics.Debug.WriteLine($"Clipboard access failed after {maxRetries} retries: 0x{ex.HResult:X8}");
                        LogDiag($"GET_CONTENT_COM_FAIL|hr=0x{ex.HResult:X8}|attempt={attempt + 1}");
                        return null;
                    }

                    int delayMs = baseDelayMs * (1 << attempt);
                    System.Diagnostics.Debug.WriteLine($"Clipboard busy (HRESULT: 0x{ex.HResult:X8}), retrying in {delayMs}ms...");
                    LogDiag($"GET_CONTENT_COM_RETRY|hr=0x{ex.HResult:X8}|delayMs={delayMs}|attempt={attempt + 1}");
                    await Task.Delay(delayMs);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Clipboard access error: {ex.GetType().Name} - {ex.Message}");
                    LogDiag($"GET_CONTENT_ERROR|type={ex.GetType().Name}");
                    return null;
                }
            }

            return null;
        }

        private async Task AddItemToCollectionAsync(ClipboardItem item, AllClipboardItems clipboardModel)
        {
            if (clipboardModel == null || _dispatcherQueue == null)
                return;

            try
            {
                bool updateInMemory = true;
                LiuYun.App? app = null;
                if (global::Microsoft.UI.Xaml.Application.Current is LiuYun.App currentApp)
                {
                    app = currentApp;
                    updateInMemory = currentApp.IsMainWindowVisible;
                }

                if (updateInMemory)
                {
                    item.WarmupSemanticInfo();
                }

                await clipboardModel.AddItemAsync(item, updateInMemory);

                if (!updateInMemory)
                {
                    LogDiag("BACKGROUND_SAVE_DB_ONLY");
                    app?.NotifyClipboardHistoryChangedInBackground();
                    app?.RequestBackgroundMemoryTrimIfHidden();
                }

                int currentCount = DatabaseService.GetClipboardItemCount();
                System.Diagnostics.Debug.WriteLine($"Clipboard history count: {currentCount}/{MaxHistoryItems}");

                if (currentCount > MaxHistoryItems)
                {
                    int excessCount = currentCount - MaxHistoryItems;
                    System.Diagnostics.Debug.WriteLine($"Clipboard history exceeded limit. Deleting {excessCount} oldest items...");

                    await Task.Run(() =>
                    {
                        try
                        {
                            DatabaseService.DeleteOldestClipboardItems(excessCount);
                            System.Diagnostics.Debug.WriteLine($"Successfully deleted {excessCount} oldest clipboard items.");
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to auto-delete old clipboard items: {ex.Message}");
                        }
                    });
                }

                await TryRunClipboardImageCleanupAsync();
            }
            catch (COMException comEx)
            {
                System.Diagnostics.Debug.WriteLine($"COM Error adding clipboard item (HRESULT: 0x{comEx.HResult:X8}): {comEx.Message}");
            }
            catch (InvalidOperationException invalidOpEx)
            {
                System.Diagnostics.Debug.WriteLine($"Invalid operation adding clipboard item: {invalidOpEx.Message}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Unexpected error adding clipboard item: {ex.GetType().Name} - {ex.Message}");
            }
        }

        private async Task TryRunClipboardImageCleanupAsync(bool force = false)
        {
            ClipboardImageCleanupRetention retention = ClipboardImageCleanupConfigService.GetRetention();
            if (retention == ClipboardImageCleanupRetention.None)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            if (!force && (nowUtc - _lastOrphanImageCleanupTimeUtc) < OrphanImageCleanupInterval)
            {
                return;
            }

            if (!await _orphanImageCleanupLock.WaitAsync(0))
            {
                return;
            }

            try
            {
                nowUtc = DateTime.UtcNow;
                if (!force && (nowUtc - _lastOrphanImageCleanupTimeUtc) < OrphanImageCleanupInterval)
                {
                    return;
                }

                _lastOrphanImageCleanupTimeUtc = nowUtc;
                int deletedCount = await Task.Run(() => ClipboardImageCleanupService.CleanupOrphanedImagesByRetention(retention));
                if (deletedCount > 0)
                {
                    System.Diagnostics.Debug.WriteLine($"Clipboard image cleanup deleted {deletedCount} orphaned files.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard image cleanup failed: {ex.Message}");
            }
            finally
            {
                _orphanImageCleanupLock.Release();
            }
        }
    }
}
