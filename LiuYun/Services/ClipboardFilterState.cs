using System;

namespace LiuYun.Services
{
    public enum ClipboardCategoryFilter
    {
        All,
        Text,
        Image,
        Link,
        Email,
        File,
        Code,
        Json,
        LongNumber
    }

    public static class ClipboardFilterState
    {
        private static ClipboardCategoryFilter _current = ClipboardCategoryFilter.All;

        public static event EventHandler<ClipboardCategoryFilter>? FilterChanged;

        public static ClipboardCategoryFilter Current
        {
            get => _current;
            set
            {
                if (_current == value)
                {
                    return;
                }

                _current = value;
                FilterChanged?.Invoke(null, _current);
            }
        }
    }
}
