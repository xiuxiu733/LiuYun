using System;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace LiuYun.Services
{
    public enum ClipboardImageCleanupRetention
    {
        None,
        Days14,
        Months1,
        Months2,
        Months3,
        Months6,
        Year1
    }

    public static class ClipboardImageCleanupConfigService
    {
        private const string ConfigKey = "clipboard_image_cleanup_retention";
        private const ClipboardImageCleanupRetention DefaultRetention = ClipboardImageCleanupRetention.None;

        public static ClipboardImageCleanupRetention GetRetention()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM config WHERE key = @key";
                command.Parameters.AddWithValue("@key", ConfigKey);

                var result = command.ExecuteScalar() as string;
                if (Enum.TryParse(result, out ClipboardImageCleanupRetention retention))
                {
                    return retention;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardImageCleanupConfigService: Failed to read retention: {ex.Message}");
            }

            return DefaultRetention;
        }

        public static bool SetRetention(ClipboardImageCleanupRetention retention)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT OR REPLACE INTO config (key, value)
                    VALUES (@key, @value)";
                command.Parameters.AddWithValue("@key", ConfigKey);
                command.Parameters.AddWithValue("@value", retention.ToString());
                command.ExecuteNonQuery();
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClipboardImageCleanupConfigService: Failed to save retention: {ex.Message}");
                return false;
            }
        }

        public static TimeSpan? GetRetentionWindow(ClipboardImageCleanupRetention retention)
        {
            return retention switch
            {
                ClipboardImageCleanupRetention.Days14 => TimeSpan.FromDays(14),
                ClipboardImageCleanupRetention.Months1 => TimeSpan.FromDays(30),
                ClipboardImageCleanupRetention.Months2 => TimeSpan.FromDays(60),
                ClipboardImageCleanupRetention.Months3 => TimeSpan.FromDays(90),
                ClipboardImageCleanupRetention.Months6 => TimeSpan.FromDays(180),
                ClipboardImageCleanupRetention.Year1 => TimeSpan.FromDays(365),
                _ => null
            };
        }
    }
}
