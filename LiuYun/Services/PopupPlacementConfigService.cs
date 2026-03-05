using System;
using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace LiuYun.Services
{
    public enum PopupPlacementMode
    {
        FollowMouse = 0,
        FreeDrag = 1
    }

    public static class PopupPlacementConfigService
    {
        private const string ConfigKey_Mode = "popup_placement_mode";
        private const string ConfigKey_FreeDragX = "popup_free_drag_x";
        private const string ConfigKey_FreeDragY = "popup_free_drag_y";
        private const PopupPlacementMode DefaultMode = PopupPlacementMode.FollowMouse;

        public static PopupPlacementMode GetMode()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                string? value = ReadConfigValue(connection, ConfigKey_Mode);
                if (Enum.TryParse(value, ignoreCase: true, out PopupPlacementMode mode))
                {
                    return mode;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PopupPlacementConfigService: Failed to read mode: {ex.Message}");
            }

            return DefaultMode;
        }

        public static bool SetMode(PopupPlacementMode mode)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                UpsertConfigValue(connection, ConfigKey_Mode, mode.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PopupPlacementConfigService: Failed to save mode: {ex.Message}");
                return false;
            }
        }

        public static bool TryGetFreeDragPosition(out int x, out int y)
        {
            x = 0;
            y = 0;

            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                string? xRaw = ReadConfigValue(connection, ConfigKey_FreeDragX);
                string? yRaw = ReadConfigValue(connection, ConfigKey_FreeDragY);
                if (!int.TryParse(xRaw, out int parsedX) || !int.TryParse(yRaw, out int parsedY))
                {
                    return false;
                }

                x = parsedX;
                y = parsedY;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PopupPlacementConfigService: Failed to read free-drag position: {ex.Message}");
                return false;
            }
        }

        public static bool SetFreeDragPosition(int x, int y)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();

                UpsertConfigValue(connection, ConfigKey_FreeDragX, x.ToString());
                UpsertConfigValue(connection, ConfigKey_FreeDragY, y.ToString());
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PopupPlacementConfigService: Failed to save free-drag position: {ex.Message}");
                return false;
            }
        }

        private static string? ReadConfigValue(SqliteConnection connection, string key)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM config WHERE key = @key LIMIT 1";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() as string;
        }

        private static void UpsertConfigValue(SqliteConnection connection, string key, string value)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR REPLACE INTO config (key, value)
                VALUES (@key, @value)";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@value", value);
            command.ExecuteNonQuery();
        }
    }
}
