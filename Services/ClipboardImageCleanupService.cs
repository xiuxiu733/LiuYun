using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace LiuYun.Services
{
    public static class ClipboardImageCleanupService
    {
        private const string ClipboardImageFolderName = "clipboard_images";

        public static int CleanupOrphanedImagesByRetention(ClipboardImageCleanupRetention retention)
        {
            TimeSpan? retentionWindow = ClipboardImageCleanupConfigService.GetRetentionWindow(retention);
            if (!retentionWindow.HasValue)
            {
                return 0;
            }

            string folder = Path.Combine(DatabaseService.AppDataFolder, ClipboardImageFolderName);
            if (!Directory.Exists(folder))
            {
                return 0;
            }

            HashSet<string> referencedImagePaths = DatabaseService.GetClipboardImagePaths();
            DateTime cutoffUtc = DateTime.UtcNow - retentionWindow.Value;
            int deletedCount = 0;

            foreach (string filePath in Directory.EnumerateFiles(folder))
            {
                try
                {
                    string normalizedPath = Path.GetFullPath(filePath);
                    if (referencedImagePaths.Contains(normalizedPath))
                    {
                        continue;
                    }

                    DateTime candidateTimestampUtc = GetCandidateTimestampUtc(normalizedPath);
                    if (candidateTimestampUtc > cutoffUtc)
                    {
                        continue;
                    }

                    File.Delete(normalizedPath);
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"ClipboardImageCleanupService: failed to clean file {filePath}. {ex.Message}");
                }
            }

            return deletedCount;
        }

        private static DateTime GetCandidateTimestampUtc(string filePath)
        {
            string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);

            if (TryParseClipboardFileTimestampUtc(fileNameWithoutExtension, out DateTime timestampUtc))
            {
                return timestampUtc;
            }

            return File.GetLastWriteTimeUtc(filePath);
        }

        private static bool TryParseClipboardFileTimestampUtc(string fileNameWithoutExtension, out DateTime timestampUtc)
        {
            timestampUtc = DateTime.MinValue;

            const string prefix = "clipboard_";
            if (!fileNameWithoutExtension.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string[] parts = fileNameWithoutExtension.Split('_');
            if (parts.Length < 3)
            {
                return false;
            }

            string datePart = parts[1];
            string timePart = parts[2];
            if (DateTime.TryParseExact(
                    $"{datePart}_{timePart}",
                    "yyyyMMdd_HHmmss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime localTime))
            {
                timestampUtc = localTime.ToUniversalTime();
                return true;
            }

            return false;
        }
    }
}
