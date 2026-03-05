using System;

namespace LiuYun.Services
{
    public static class ClipboardTimeFilterState
    {
        private static DateTime? _startTime;
        private static DateTime? _endTime;

        public static event EventHandler? FilterChanged;

        public static DateTime? StartTime => _startTime;
        public static DateTime? EndTime => _endTime;

        public static void SetRange(DateTime? startTime, DateTime? endTime)
        {
            if (_startTime == startTime && _endTime == endTime)
            {
                return;
            }

            _startTime = startTime;
            _endTime = endTime;
            FilterChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void Clear()
        {
            SetRange(null, null);
        }
    }
}
