using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Media.Imaging;

namespace LiuYun.Services
{
    public class ImageCacheService
    {
        private readonly int _maxCacheSize;
        private readonly Dictionary<string, CacheEntry> _cache;
        private readonly LinkedList<string> _lruList;

        private class CacheEntry
        {
            public BitmapImage? Image { get; set; }
            public LinkedListNode<string>? Node { get; set; }
        }

        private static void ReleaseImage(BitmapImage? image)
        {
            if (image == null)
            {
                return;
            }

            // Keep the UriSource intact when releasing from cache.
            // Clearing UriSource can cause already-bound Image controls to become blank.
            //if (image.UriSource != null)
            //{
            //    try
            //    {
            //        image.UriSource = null;
            //    }
            //    catch
            //    {
            //    }
            //}
        }

        public ImageCacheService(int maxCacheSize = 30)
        {
            _maxCacheSize = maxCacheSize;
            _cache = new Dictionary<string, CacheEntry>();
            _lruList = new LinkedList<string>();
        }

        public BitmapImage? Get(string imagePath)
        {
            if (_cache.TryGetValue(imagePath, out var entry))
            {
                if (entry.Node != null)
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                }
                return entry.Image;
            }
            return null;
        }

        public void Put(string imagePath, BitmapImage image)
        {
            if (_cache.TryGetValue(imagePath, out var entry))
            {
                if (!ReferenceEquals(entry.Image, image))
                {
                    ReleaseImage(entry.Image);
                }
                entry.Image = image;
                if (entry.Node != null)
                {
                    _lruList.Remove(entry.Node);
                    _lruList.AddFirst(entry.Node);
                }
                return;
            }

            if (_cache.Count >= _maxCacheSize)
            {
                var lastNode = _lruList.Last;
                if (lastNode != null)
                {
                    if (_cache.TryGetValue(lastNode.Value, out var evicted))
                    {
                        ReleaseImage(evicted.Image);
                    }
                    _cache.Remove(lastNode.Value);
                    _lruList.RemoveLast();
                }
            }

            var newNode = _lruList.AddFirst(imagePath);
            _cache[imagePath] = new CacheEntry
            {
                Image = image,
                Node = newNode
            };
        }

        public void Clear()
        {
            foreach (var entry in _cache.Values)
            {
                ReleaseImage(entry.Image);
            }

            _cache.Clear();
            _lruList.Clear();
        }

        public void Remove(string imagePath)
        {
            if (_cache.TryGetValue(imagePath, out var entry))
            {
                if (entry.Node != null)
                {
                    _lruList.Remove(entry.Node);
                }
                ReleaseImage(entry.Image);
                _cache.Remove(imagePath);
            }
        }

        public int Count => _cache.Count;
    }
}
