using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Threading;
using Microsoft.UI.Dispatching;
using LiuYun.Services;

namespace LiuYun.Models
{
    public class AllClipboardItems : IDisposable
    {
        private const int MaxClipboardItems = 300;
        public ObservableCollection<ClipboardItem> Items { get; set; } = new ObservableCollection<ClipboardItem>();

        public bool IsLoaded { get; set; } = false;

        private readonly BackgroundTaskQueue? _taskQueue;
        private readonly DispatcherQueue? _dispatcherQueue;

        private readonly Channel<ClipboardItem>? _uiUpdateQueue;
        private readonly Task? _uiUpdateTask;
        private int _disposed = 0;

        private readonly System.Collections.Generic.HashSet<int> _itemIds = new System.Collections.Generic.HashSet<int>();
        private readonly object _itemIdsLock = new object();

        public AllClipboardItems()
        {
            Items.CollectionChanged += ItemsCollectionChangedForIdTracking;
        }

        public AllClipboardItems(BackgroundTaskQueue taskQueue, DispatcherQueue dispatcherQueue)
        {
            _taskQueue = taskQueue;
            _dispatcherQueue = dispatcherQueue;
            Items.CollectionChanged += ItemsCollectionChangedForIdTracking;

            var options = new BoundedChannelOptions(MaxClipboardItems)
            {
                FullMode = BoundedChannelFullMode.Wait
            };
            _uiUpdateQueue = Channel.CreateBounded<ClipboardItem>(options);

            _uiUpdateTask = Task.Run(ProcessUIUpdatesAsync);
        }

        private void ItemsCollectionChangedForIdTracking(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                RebuildTrackedIdsFromItems();

                return;
            }

            if (e.OldItems != null)
            {
                foreach (var old in e.OldItems)
                {
                    if (old is ClipboardItem oldItem && oldItem.Id > 0)
                    {
                        RemoveTrackedId(oldItem.Id);
                    }
                }
            }

            if (e.NewItems != null)
            {
                foreach (var added in e.NewItems)
                {
                    if (added is ClipboardItem newItem && newItem.Id > 0)
                    {
                        AddTrackedId(newItem.Id);
                    }
                }
            }
        }

        private void RebuildTrackedIdsFromItems()
        {
            lock (_itemIdsLock)
            {
                _itemIds.Clear();
                foreach (var item in Items)
                {
                    if (item.Id > 0)
                    {
                        _itemIds.Add(item.Id);
                    }
                }
            }
        }

        private bool AddTrackedId(int id)
        {
            if (id <= 0)
            {
                return false;
            }

            lock (_itemIdsLock)
            {
                return _itemIds.Add(id);
            }
        }

        private void RemoveTrackedId(int id)
        {
            if (id <= 0)
            {
                return;
            }

            lock (_itemIdsLock)
            {
                _itemIds.Remove(id);
            }
        }

        private void ClearTrackedIds()
        {
            lock (_itemIdsLock)
            {
                _itemIds.Clear();
            }
        }

        private async Task ProcessUIUpdatesAsync()
        {
            if (_uiUpdateQueue == null || _dispatcherQueue == null)
                return;

            try
            {
                await foreach (var item in _uiUpdateQueue.Reader.ReadAllAsync())
                {
                    if (Interlocked.CompareExchange(ref _disposed, 0, 0) == 1)
                        break;

                    var tcs = new TaskCompletionSource<bool>();
                    bool enqueued = _dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            if (item.Id > 0 && !AddTrackedId(item.Id))
                            {
                                tcs.TrySetResult(true);
                                return;
                            }

                            Items.Insert(0, item);

                            if (Items.Count > MaxClipboardItems)
                            {
                                var oldItem = Items[Items.Count - 1];
                                if (oldItem.ContentType == ClipboardContentType.Image)
                                {
                                    oldItem.ClearImageCache();
                                }
                                RemoveTrackedId(oldItem.Id);
                                Items.RemoveAt(Items.Count - 1);
                            }

                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Error updating UI collection: {ex.Message}");
                            tcs.TrySetResult(false);
                        }
                    });

                    if (enqueued)
                    {
                        var timeout = Task.Delay(3000);
                        await Task.WhenAny(tcs.Task, timeout);
                    }

                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UI update processing stopped: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 1)
                return;

            _uiUpdateQueue?.Writer.Complete();

            Items.CollectionChanged -= ItemsCollectionChangedForIdTracking;

            if (_uiUpdateTask != null)
            {
                _ = _uiUpdateTask.ContinueWith(
                    task =>
                    {
                        if (task.IsFaulted)
                        {
                            System.Diagnostics.Debug.WriteLine($"UI update task ended with error: {task.Exception?.GetBaseException().Message}");
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        public void LoadItems()
        {
            Items.Clear();
            ClearTrackedIds();
            var items = DatabaseService.GetRecentClipboardItems(MaxClipboardItems);
            foreach (var item in items)
            {
                Items.Add(item);
                if (item.Id > 0)
                {
                    AddTrackedId(item.Id);
                }
            }
        }

        public async Task AddItemAsync(ClipboardItem item, bool updateInMemory = true)
        {
            if (_taskQueue == null || _dispatcherQueue == null || _uiUpdateQueue == null)
            {
                AddItemSync(item, updateInMemory);
                return;
            }

            await _taskQueue.QueueBackgroundWorkItemAsync(async cancellationToken =>
            {
                try
                {
                    var saveResult = DatabaseService.SaveClipboardItem(item);
                    item.Id = (int)saveResult.Id;

                    if (!saveResult.Inserted)
                    {
                        CleanupUnsavedDuplicateImageFile(item);
                        return;
                    }

                    if (!updateInMemory)
                    {
                        return;
                    }

                    if (!_uiUpdateQueue.Writer.TryWrite(item))
                    {
                        System.Diagnostics.Debug.WriteLine("UI update queue full, item dropped");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to save clipboard item to database: {ex.Message}");
                }

                await Task.CompletedTask;
            });
        }

        private void AddItemSync(ClipboardItem item, bool updateInMemory = true)
        {
            try
            {
                var saveResult = DatabaseService.SaveClipboardItem(item);
                item.Id = (int)saveResult.Id;

                if (!saveResult.Inserted)
                {
                    CleanupUnsavedDuplicateImageFile(item);
                    return;
                }

                if (!updateInMemory)
                {
                    return;
                }

                Items.Insert(0, item);

                if (Items.Count > MaxClipboardItems)
                {
                    var oldItem = Items[Items.Count - 1];
                    if (oldItem.ContentType == ClipboardContentType.Image)
                    {
                        oldItem.ClearImageCache();
                    }
                    Items.RemoveAt(Items.Count - 1);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to save clipboard item: {ex.Message}");
                throw;
            }
        }

        private static void CleanupUnsavedDuplicateImageFile(ClipboardItem item)
        {
            if (item.ContentType != ClipboardContentType.Image ||
                string.IsNullOrWhiteSpace(item.ImagePath))
            {
                return;
            }

            try
            {
                if (System.IO.File.Exists(item.ImagePath))
                {
                    System.IO.File.Delete(item.ImagePath);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to cleanup deduplicated image file: {ex.Message}");
            }
        }

        public async Task ClearAllAsync()
        {
            if (_taskQueue == null || _dispatcherQueue == null)
            {
                ClearAllSync();
                return;
            }

            await _taskQueue.QueueBackgroundWorkItemAsync(async cancellationToken =>
            {
                try
                {
                    var itemsToDelete = new System.Collections.Generic.List<ClipboardItem>();
                    var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            itemsToDelete.AddRange(Items.Where(i => !i.IsPinned));
                            tcs.TrySetResult(true);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to copy items for deletion: {ex.Message}");
                            tcs.TrySetResult(false);
                        }
                    });

                    await tcs.Task;

                    DatabaseService.ClearAllClipboardItems(keepPinned: true);

                    foreach (var item in itemsToDelete)
                    {
                        if (item.ContentType == ClipboardContentType.Image &&
                            !string.IsNullOrEmpty(item.ImagePath) &&
                            System.IO.File.Exists(item.ImagePath))
                        {
                            try
                            {
                                System.IO.File.Delete(item.ImagePath);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed to delete image file: {ex.Message}");
                            }
                        }
                    }

                    foreach (var item in itemsToDelete)
                    {
                        if (item.ContentType == ClipboardContentType.Image)
                        {
                            item.ClearImageCache();
                        }
                    }

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            var toRemove = Items.Where(i => !i.IsPinned).ToList();
                            foreach (var item in toRemove)
                            {
                                Items.Remove(item);
                                RemoveTrackedId(item.Id);
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to clear UI collection: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to clear clipboard history: {ex.Message}");
                }

                await Task.CompletedTask;
            });
        }

        private void ClearAllSync()
        {
            DatabaseService.ClearAllClipboardItems();
            Items.Clear();
        }

        public async Task DeleteItemAsync(ClipboardItem item)
        {
            if (_taskQueue == null || _dispatcherQueue == null)
            {
                DeleteItemSync(item);
                return;
            }

            await _taskQueue.QueueBackgroundWorkItemAsync(async cancellationToken =>
            {
                try
                {
                    DatabaseService.DeleteClipboardItem(item.Id);

                    if (item.ContentType == ClipboardContentType.Image)
                    {
                        item.ClearImageCache();
                    }

                    if (item.ContentType == ClipboardContentType.Image &&
                        !string.IsNullOrEmpty(item.ImagePath) &&
                        System.IO.File.Exists(item.ImagePath))
                    {
                        try
                        {
                            System.IO.File.Delete(item.ImagePath);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to delete image file: {ex.Message}");
                        }
                    }

                    _dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            Items.Remove(item);
                            RemoveTrackedId(item.Id);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to remove item from UI collection: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete clipboard item from database: {ex.Message}");
                }

                await Task.CompletedTask;
            });
        }

        private void DeleteItemSync(ClipboardItem item)
        {
            DatabaseService.DeleteClipboardItem(item.Id);

            if (item.ContentType == ClipboardContentType.Image)
            {
                item.ClearImageCache();
            }

            Items.Remove(item);
        }
    }
}
