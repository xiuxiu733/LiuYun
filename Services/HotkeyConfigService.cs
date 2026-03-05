using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace LiuYun.Services
{
    public enum HotkeyTriggerType
    {
        None = 0,
        SystemWinV = 1,
        KeyChord = 2,
        DoubleTap = 3
    }

    public sealed class HotkeyTrigger
    {
        public const int DefaultDoubleTapIntervalMs = 300;

        public HotkeyTriggerType Type { get; init; }
        public string Modifier { get; init; } = string.Empty;
        public string Key { get; init; } = string.Empty;
        public string DoubleTapKey { get; init; } = "Ctrl";
        public int DoubleTapIntervalMs { get; init; } = DefaultDoubleTapIntervalMs;
    }

    public static class HotkeyConfigService
    {
        private const string ConfigKey_TriggerType = "hotkey_trigger_type";
        private const string ConfigKey_Modifier = "hotkey_modifier";
        private const string ConfigKey_Key = "hotkey_key";
        private const string ConfigKey_DoubleTapKey = "hotkey_double_tap_key";
        private const string ConfigKey_DoubleTapIntervalMs = "hotkey_double_tap_interval_ms";

        private const string DefaultModifier = "Ctrl";
        private const string DefaultKey = "L";
        private const string DefaultDoubleTapKey = "Ctrl";

        public static void EnsureInitialized()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();
                EnsureTriggerInitialized(connection);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyConfigService: EnsureInitialized failed: {ex.Message}");
            }
        }

        public static HotkeyTrigger GetCurrentTrigger()
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();
                EnsureTriggerInitialized(connection);
                return ReadTrigger(connection);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyConfigService: Failed to get trigger: {ex.Message}");
                return new HotkeyTrigger
                {
                    Type = HotkeyTriggerType.DoubleTap,
                    DoubleTapKey = DefaultDoubleTapKey,
                    DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs
                };
            }
        }

        public static bool SaveSystemWinVTrigger()
        {
            return SaveTrigger(new HotkeyTrigger
            {
                Type = HotkeyTriggerType.SystemWinV,
                Modifier = "Win",
                Key = "V",
                DoubleTapKey = DefaultDoubleTapKey,
                DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs
            });
        }

        public static bool SaveKeyChordTrigger(string modifier, string key)
        {
            string normalizedModifier = NormalizeChordModifier(modifier);
            string normalizedKey = NormalizeLetterKey(key);
            if (string.IsNullOrWhiteSpace(normalizedModifier) || string.IsNullOrWhiteSpace(normalizedKey))
            {
                return false;
            }

            if (normalizedModifier.Contains("Win", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return SaveTrigger(new HotkeyTrigger
            {
                Type = HotkeyTriggerType.KeyChord,
                Modifier = normalizedModifier,
                Key = normalizedKey,
                DoubleTapKey = DefaultDoubleTapKey,
                DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs
            });
        }

        public static bool SaveDoubleTapTrigger(string doubleTapKey, int intervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs)
        {
            string normalizedTapKey = NormalizeDoubleTapKey(doubleTapKey);
            if (string.IsNullOrWhiteSpace(normalizedTapKey))
            {
                return false;
            }

            int normalizedInterval = Math.Clamp(intervalMs, 200, 500);
            return SaveTrigger(new HotkeyTrigger
            {
                Type = HotkeyTriggerType.DoubleTap,
                DoubleTapKey = normalizedTapKey,
                DoubleTapIntervalMs = normalizedInterval,
                Modifier = DefaultModifier,
                Key = DefaultKey
            });
        }

        public static bool ClearTrigger()
        {
            return SaveTrigger(new HotkeyTrigger
            {
                Type = HotkeyTriggerType.None,
                DoubleTapKey = DefaultDoubleTapKey,
                DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs,
                Modifier = DefaultModifier,
                Key = DefaultKey
            });
        }

        public static string FormatTriggerForDisplay(HotkeyTrigger trigger)
        {
            return trigger.Type switch
            {
                HotkeyTriggerType.SystemWinV => "win + v",
                HotkeyTriggerType.KeyChord => FormatChordDisplay(trigger.Modifier, trigger.Key),
                HotkeyTriggerType.DoubleTap => FormatDoubleTapDisplay(trigger.DoubleTapKey),
                _ => "点击设置快捷键"
            };
        }

        public static string GetModifier()
        {
            HotkeyTrigger trigger = GetCurrentTrigger();
            return trigger.Type switch
            {
                HotkeyTriggerType.SystemWinV => "Win",
                HotkeyTriggerType.KeyChord => trigger.Modifier,
                _ => DefaultModifier
            };
        }

        public static string GetKey()
        {
            HotkeyTrigger trigger = GetCurrentTrigger();
            return trigger.Type switch
            {
                HotkeyTriggerType.SystemWinV => "V",
                HotkeyTriggerType.KeyChord => trigger.Key,
                HotkeyTriggerType.DoubleTap => trigger.DoubleTapKey,
                _ => DefaultKey
            };
        }

        private static bool SaveTrigger(HotkeyTrigger trigger)
        {
            try
            {
                using var connection = new SqliteConnection(DatabaseService.ConnectionString);
                connection.Open();
                EnsureTriggerInitialized(connection);
                SaveTrigger(connection, trigger);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyConfigService: Failed to save trigger: {ex.Message}");
                return false;
            }
        }

        private static void EnsureTriggerInitialized(SqliteConnection connection)
        {
            string? triggerTypeRaw = ReadConfigValue(connection, ConfigKey_TriggerType);
            if (!string.IsNullOrWhiteSpace(triggerTypeRaw))
            {
                return;
            }

            HotkeyTrigger migrated = BuildLegacyTrigger(connection);
            SaveTrigger(connection, migrated);
        }

        private static HotkeyTrigger BuildLegacyTrigger(SqliteConnection connection)
        {
            try
            {
                bool isLegacyWinVMode = !ClipboardRegistryService.GetPreferSystemWinV() && ClipboardRegistryService.IsWinVDisabled();
                if (isLegacyWinVMode)
                {
                    return new HotkeyTrigger
                    {
                        Type = HotkeyTriggerType.SystemWinV,
                        Modifier = "Win",
                        Key = "V",
                        DoubleTapKey = DefaultDoubleTapKey,
                        DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"HotkeyConfigService: Legacy Win+V detection failed: {ex.Message}");
            }

            string? legacyModifierRaw = ReadConfigValue(connection, ConfigKey_Modifier);
            string? legacyKeyRaw = ReadConfigValue(connection, ConfigKey_Key);
            string legacyModifier = NormalizeChordModifier(legacyModifierRaw);
            string legacyKey = NormalizeLetterKey(legacyKeyRaw);

            if (!string.IsNullOrWhiteSpace(legacyModifier) &&
                !string.IsNullOrWhiteSpace(legacyKey) &&
                !string.Equals(legacyModifier, "Win", StringComparison.OrdinalIgnoreCase))
            {
                return new HotkeyTrigger
                {
                    Type = HotkeyTriggerType.KeyChord,
                    Modifier = legacyModifier,
                    Key = legacyKey,
                    DoubleTapKey = DefaultDoubleTapKey,
                    DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs
                };
            }

            return new HotkeyTrigger
            {
                Type = HotkeyTriggerType.DoubleTap,
                DoubleTapKey = DefaultDoubleTapKey,
                DoubleTapIntervalMs = HotkeyTrigger.DefaultDoubleTapIntervalMs,
                Modifier = DefaultModifier,
                Key = DefaultKey
            };
        }

        private static HotkeyTrigger ReadTrigger(SqliteConnection connection)
        {
            string? triggerTypeRaw = ReadConfigValue(connection, ConfigKey_TriggerType);
            if (!Enum.TryParse(triggerTypeRaw, ignoreCase: true, out HotkeyTriggerType triggerType))
            {
                triggerType = HotkeyTriggerType.DoubleTap;
            }

            string modifier = NormalizeChordModifier(ReadConfigValue(connection, ConfigKey_Modifier));
            string key = NormalizeLetterKey(ReadConfigValue(connection, ConfigKey_Key));
            string doubleTapKey = NormalizeDoubleTapKey(ReadConfigValue(connection, ConfigKey_DoubleTapKey));

            if (!int.TryParse(ReadConfigValue(connection, ConfigKey_DoubleTapIntervalMs), out int doubleTapInterval))
            {
                doubleTapInterval = HotkeyTrigger.DefaultDoubleTapIntervalMs;
            }
            doubleTapInterval = Math.Clamp(doubleTapInterval, 200, 500);

            if (string.IsNullOrWhiteSpace(modifier))
            {
                modifier = DefaultModifier;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                key = DefaultKey;
            }

            if (string.IsNullOrWhiteSpace(doubleTapKey))
            {
                doubleTapKey = DefaultDoubleTapKey;
            }

            if (triggerType == HotkeyTriggerType.SystemWinV)
            {
                modifier = "Win";
                key = "V";
            }

            return new HotkeyTrigger
            {
                Type = triggerType,
                Modifier = modifier,
                Key = key,
                DoubleTapKey = doubleTapKey,
                DoubleTapIntervalMs = doubleTapInterval
            };
        }

        private static void SaveTrigger(SqliteConnection connection, HotkeyTrigger trigger)
        {
            string triggerType = trigger.Type.ToString();
            string modifier = NormalizeChordModifier(trigger.Modifier);
            string key = NormalizeLetterKey(trigger.Key);
            string doubleTapKey = NormalizeDoubleTapKey(trigger.DoubleTapKey);
            int doubleTapInterval = Math.Clamp(trigger.DoubleTapIntervalMs, 200, 500);

            if (trigger.Type == HotkeyTriggerType.SystemWinV)
            {
                modifier = "Win";
                key = "V";
            }

            if (string.IsNullOrWhiteSpace(modifier))
            {
                modifier = DefaultModifier;
            }

            if (string.IsNullOrWhiteSpace(key))
            {
                key = DefaultKey;
            }

            if (string.IsNullOrWhiteSpace(doubleTapKey))
            {
                doubleTapKey = DefaultDoubleTapKey;
            }

            UpsertConfigValue(connection, ConfigKey_TriggerType, triggerType);
            UpsertConfigValue(connection, ConfigKey_Modifier, modifier);
            UpsertConfigValue(connection, ConfigKey_Key, key);
            UpsertConfigValue(connection, ConfigKey_DoubleTapKey, doubleTapKey);
            UpsertConfigValue(connection, ConfigKey_DoubleTapIntervalMs, doubleTapInterval.ToString());
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

        private static string NormalizeChordModifier(string? modifier)
        {
            if (string.IsNullOrWhiteSpace(modifier))
            {
                return string.Empty;
            }

            string[] tokens = modifier
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (tokens.Length == 0)
            {
                return string.Empty;
            }

            var normalizedTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string token in tokens)
            {
                string normalized = token.Trim().ToLowerInvariant() switch
                {
                    "ctrl" or "control" => "Ctrl",
                    "alt" => "Alt",
                    "shift" => "Shift",
                    "win" or "windows" => "Win",
                    _ => string.Empty
                };

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    return string.Empty;
                }

                normalizedTokens.Add(normalized);
            }

            string[] ordered = new[] { "Ctrl", "Alt", "Shift", "Win" };
            var result = ordered.Where(t => normalizedTokens.Contains(t)).ToList();
            if (result.Count == 0)
            {
                return string.Empty;
            }

            return string.Join('+', result);
        }

        private static string NormalizeLetterKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            string raw = key.Trim().ToUpperInvariant();
            if (raw.Length == 1 && raw[0] >= 'A' && raw[0] <= 'Z')
            {
                return raw;
            }

            return string.Empty;
        }

        private static string NormalizeDoubleTapKey(string? doubleTapKey)
        {
            if (string.IsNullOrWhiteSpace(doubleTapKey))
            {
                return string.Empty;
            }

            return doubleTapKey.Trim().ToLowerInvariant() switch
            {
                "ctrl" or "control" => "Ctrl",
                "alt" => "Alt",
                "shift" => "Shift",
                _ => string.Empty
            };
        }

        private static string FormatChordDisplay(string modifier, string key)
        {
            string normalizedModifier = NormalizeChordModifier(modifier);
            string normalizedKey = NormalizeLetterKey(key);
            if (string.IsNullOrWhiteSpace(normalizedModifier) || string.IsNullOrWhiteSpace(normalizedKey))
            {
                return "点击设置快捷键";
            }

            var tokens = normalizedModifier
                .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(t => t.ToLowerInvariant())
                .ToList();
            tokens.Add(normalizedKey.ToLowerInvariant());
            return string.Join(" + ", tokens);
        }

        private static string FormatDoubleTapDisplay(string doubleTapKey)
        {
            string normalized = NormalizeDoubleTapKey(doubleTapKey);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                normalized = "Ctrl";
            }

            string lower = normalized.ToLowerInvariant();
            return $"{lower} + {lower}";
        }
    }
}
