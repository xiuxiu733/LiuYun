using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace LiuYun.Services
{
    public static class StoragePathService
    {
        private sealed class StorageSettings
        {
            public string? CurrentDataRoot { get; set; }
            public string? PendingMigrationTargetRoot { get; set; }
        }

        private static readonly object SyncRoot = new object();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private const string DatabaseFileName = "data.db";
        private const string ClipboardImageFolderName = "clipboard_images";
        private const string FavoriteImageFolderName = "favorite_images";

        public static string RuntimeRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LiuYun");

        public static string LegacyDefaultDataRoot =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "LiuYun");

        private static string StorageSettingsPath => Path.Combine(RuntimeRoot, "storage.json");

        public static string GetCurrentDataRoot()
        {
            lock (SyncRoot)
            {
                StorageSettings settings = LoadSettings_NoLock();

                string configuredRoot = NormalizeDirectoryPath(settings.CurrentDataRoot);
                if (!string.IsNullOrWhiteSpace(configuredRoot))
                {
                    Directory.CreateDirectory(configuredRoot);
                    return configuredRoot;
                }

                string fallbackRoot = NormalizeDirectoryPath(LegacyDefaultDataRoot);
                Directory.CreateDirectory(fallbackRoot);
                return fallbackRoot;
            }
        }

        public static string? GetPendingMigrationTargetRoot()
        {
            lock (SyncRoot)
            {
                StorageSettings settings = LoadSettings_NoLock();
                string pendingRoot = NormalizeDirectoryPath(settings.PendingMigrationTargetRoot);
                return string.IsNullOrWhiteSpace(pendingRoot) ? null : pendingRoot;
            }
        }

        public static bool QueueMigration(string targetRoot, out string message)
        {
            message = string.Empty;

            string normalizedTargetRoot = NormalizeDirectoryPath(targetRoot);
            if (string.IsNullOrWhiteSpace(normalizedTargetRoot))
            {
                message = "目标路径无效。";
                return false;
            }

            try
            {
                Directory.CreateDirectory(normalizedTargetRoot);
            }
            catch (Exception ex)
            {
                message = $"无法创建目标目录：{ex.Message}";
                return false;
            }

            lock (SyncRoot)
            {
                StorageSettings settings = LoadSettings_NoLock();
                string currentRoot = ResolveCurrentRoot_NoLock(settings);
                if (string.Equals(currentRoot, normalizedTargetRoot, StringComparison.OrdinalIgnoreCase))
                {
                    message = "目标路径与当前保存位置一致。";
                    return false;
                }

                settings.PendingMigrationTargetRoot = normalizedTargetRoot;
                SaveSettings_NoLock(settings);
            }

            message = "迁移任务已保存，重启应用后执行。";
            return true;
        }

        public static void ProcessPendingMigrationIfAny()
        {
            string? targetRoot;
            string currentRoot;
            StorageSettings settings;

            lock (SyncRoot)
            {
                settings = LoadSettings_NoLock();
                targetRoot = NormalizeDirectoryPath(settings.PendingMigrationTargetRoot);
                if (string.IsNullOrWhiteSpace(targetRoot))
                {
                    return;
                }

                currentRoot = ResolveCurrentRoot_NoLock(settings);
            }

            if (string.Equals(currentRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                lock (SyncRoot)
                {
                    StorageSettings latest = LoadSettings_NoLock();
                    latest.CurrentDataRoot = targetRoot;
                    latest.PendingMigrationTargetRoot = null;
                    SaveSettings_NoLock(latest);
                }
                return;
            }

            try
            {
                Directory.CreateDirectory(targetRoot!);
                if (HasManagedData(targetRoot!))
                {
                    MergeManagedData(currentRoot, targetRoot!);
                }
                else
                {
                    CopyManagedData(currentRoot, targetRoot!);
                }

                string migratedDbPath = Path.Combine(targetRoot!, DatabaseFileName);
                if (File.Exists(migratedDbPath))
                {
                    DatabaseService.RewriteManagedImagePaths(migratedDbPath, currentRoot, targetRoot!);
                }

                lock (SyncRoot)
                {
                    StorageSettings latest = LoadSettings_NoLock();
                    latest.CurrentDataRoot = targetRoot;
                    latest.PendingMigrationTargetRoot = null;
                    SaveSettings_NoLock(latest);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StoragePathService: migration failed. {ex}");

                lock (SyncRoot)
                {
                    StorageSettings latest = LoadSettings_NoLock();
                    latest.PendingMigrationTargetRoot = null;
                    SaveSettings_NoLock(latest);
                }
            }
        }

        private static string ResolveCurrentRoot_NoLock(StorageSettings settings)
        {
            string configuredRoot = NormalizeDirectoryPath(settings.CurrentDataRoot);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                return configuredRoot;
            }

            return NormalizeDirectoryPath(LegacyDefaultDataRoot);
        }

        private static StorageSettings LoadSettings_NoLock()
        {
            try
            {
                if (!File.Exists(StorageSettingsPath))
                {
                    return new StorageSettings();
                }

                string json = File.ReadAllText(StorageSettingsPath);
                StorageSettings? settings = JsonSerializer.Deserialize<StorageSettings>(json, JsonOptions);
                return settings ?? new StorageSettings();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StoragePathService: failed to read settings. {ex.Message}");
                return new StorageSettings();
            }
        }

        private static void SaveSettings_NoLock(StorageSettings settings)
        {
            try
            {
                Directory.CreateDirectory(RuntimeRoot);
                string json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(StorageSettingsPath, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"StoragePathService: failed to save settings. {ex.Message}");
            }
        }

        private sealed class ClipboardHistoryRowData
        {
            public int ContentType { get; init; }
            public string TextContent { get; init; } = string.Empty;
            public string ImagePath { get; init; } = string.Empty;
            public string Timestamp { get; init; } = string.Empty;
            public int IsPinned { get; init; }
            public string ContentHash { get; init; } = string.Empty;
        }

        private sealed class FavoriteClipboardRowData
        {
            public int ContentType { get; init; }
            public string TextContent { get; init; } = string.Empty;
            public string ImagePath { get; init; } = string.Empty;
            public string Timestamp { get; init; } = string.Empty;
            public string ContentHash { get; init; } = string.Empty;
        }

        private static bool HasManagedData(string rootPath)
        {
            return File.Exists(Path.Combine(rootPath, DatabaseFileName));
        }

        private static void CopyManagedData(string sourceRoot, string targetRoot)
        {
            ClearManagedData(targetRoot);

            CopyFileIfExists(sourceRoot, targetRoot, DatabaseFileName);
            CopyFileIfExists(sourceRoot, targetRoot, $"{DatabaseFileName}-wal");
            CopyFileIfExists(sourceRoot, targetRoot, $"{DatabaseFileName}-shm");
            CopyFileIfExists(sourceRoot, targetRoot, "error.log");

            CopyDirectoryIfExists(
                Path.Combine(sourceRoot, ClipboardImageFolderName),
                Path.Combine(targetRoot, ClipboardImageFolderName));

            CopyDirectoryIfExists(
                Path.Combine(sourceRoot, FavoriteImageFolderName),
                Path.Combine(targetRoot, FavoriteImageFolderName));
        }

        private static void MergeManagedData(string sourceRoot, string targetRoot)
        {
            string sourceDatabasePath = Path.Combine(sourceRoot, DatabaseFileName);
            string targetDatabasePath = Path.Combine(targetRoot, DatabaseFileName);

            var clipboardImageMap = MergeDirectoryWithMap(
                Path.Combine(sourceRoot, ClipboardImageFolderName),
                Path.Combine(targetRoot, ClipboardImageFolderName));

            var favoriteImageMap = MergeDirectoryWithMap(
                Path.Combine(sourceRoot, FavoriteImageFolderName),
                Path.Combine(targetRoot, FavoriteImageFolderName));

            AppendLogIfExists(
                Path.Combine(sourceRoot, "error.log"),
                Path.Combine(targetRoot, "error.log"));

            if (!File.Exists(sourceDatabasePath))
            {
                return;
            }

            if (!File.Exists(targetDatabasePath))
            {
                CopyFileIfExists(sourceRoot, targetRoot, DatabaseFileName);
                CopyFileIfExists(sourceRoot, targetRoot, $"{DatabaseFileName}-wal");
                CopyFileIfExists(sourceRoot, targetRoot, $"{DatabaseFileName}-shm");
                return;
            }

            DatabaseService.InitializeAtPath(targetDatabasePath);
            MergeDatabaseContent(
                sourceDatabasePath,
                targetDatabasePath,
                sourceRoot,
                targetRoot,
                clipboardImageMap,
                favoriteImageMap);
        }

        private static void MergeDatabaseContent(
            string sourceDatabasePath,
            string targetDatabasePath,
            string sourceRoot,
            string targetRoot,
            Dictionary<string, string> clipboardImageMap,
            Dictionary<string, string> favoriteImageMap)
        {
            using var sourceConnection = new SqliteConnection(DatabaseService.BuildConnectionString(sourceDatabasePath));
            sourceConnection.Open();

            using var targetConnection = new SqliteConnection(DatabaseService.BuildConnectionString(targetDatabasePath));
            targetConnection.Open();

            MergeConfig(sourceConnection, targetConnection);
            MergeClipboardHistory(sourceConnection, targetConnection, sourceRoot, targetRoot, clipboardImageMap, favoriteImageMap);
            MergeFavoriteClipboard(sourceConnection, targetConnection, sourceRoot, targetRoot, clipboardImageMap, favoriteImageMap);
        }

        private static void MergeConfig(SqliteConnection sourceConnection, SqliteConnection targetConnection)
        {
            if (!HasTable(sourceConnection, "config") || !HasTable(targetConnection, "config"))
            {
                return;
            }

            using var sourceCommand = sourceConnection.CreateCommand();
            sourceCommand.CommandText = "SELECT key, value FROM config";

            using var reader = sourceCommand.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
                string value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                using var upsertCommand = targetConnection.CreateCommand();
                upsertCommand.CommandText = @"
                    INSERT INTO config (key, value)
                    SELECT @key, @value
                    WHERE NOT EXISTS (SELECT 1 FROM config WHERE key = @key)";
                upsertCommand.Parameters.AddWithValue("@key", key);
                upsertCommand.Parameters.AddWithValue("@value", value);
                upsertCommand.ExecuteNonQuery();
            }
        }

        private static void MergeClipboardHistory(
            SqliteConnection sourceConnection,
            SqliteConnection targetConnection,
            string sourceRoot,
            string targetRoot,
            Dictionary<string, string> clipboardImageMap,
            Dictionary<string, string> favoriteImageMap)
        {
            if (!HasTable(sourceConnection, "ClipboardHistory") || !HasTable(targetConnection, "ClipboardHistory"))
            {
                return;
            }

            foreach (ClipboardHistoryRowData sourceRow in ReadClipboardHistoryRows(sourceConnection))
            {
                string mappedImagePath = RemapManagedImagePath(
                    sourceRow.ImagePath,
                    sourceRoot,
                    targetRoot,
                    clipboardImageMap,
                    favoriteImageMap);

                if (ClipboardHistoryExists(targetConnection, sourceRow, mappedImagePath))
                {
                    continue;
                }

                using var insertCommand = targetConnection.CreateCommand();
                insertCommand.CommandText = @"
                    INSERT INTO ClipboardHistory (ContentType, TextContent, ImagePath, Timestamp, IsPinned, ContentHash)
                    VALUES (@contentType, @textContent, @imagePath, @timestamp, @isPinned, @contentHash)";
                insertCommand.Parameters.AddWithValue("@contentType", sourceRow.ContentType);
                insertCommand.Parameters.AddWithValue("@textContent", ToDbValue(sourceRow.TextContent));
                insertCommand.Parameters.AddWithValue("@imagePath", ToDbValue(mappedImagePath));
                insertCommand.Parameters.AddWithValue("@timestamp", sourceRow.Timestamp);
                insertCommand.Parameters.AddWithValue("@isPinned", sourceRow.IsPinned);
                insertCommand.Parameters.AddWithValue("@contentHash", ToDbValue(sourceRow.ContentHash));
                insertCommand.ExecuteNonQuery();
            }
        }

        private static void MergeFavoriteClipboard(
            SqliteConnection sourceConnection,
            SqliteConnection targetConnection,
            string sourceRoot,
            string targetRoot,
            Dictionary<string, string> clipboardImageMap,
            Dictionary<string, string> favoriteImageMap)
        {
            if (!HasTable(sourceConnection, "FavoriteClipboard") || !HasTable(targetConnection, "FavoriteClipboard"))
            {
                return;
            }

            foreach (FavoriteClipboardRowData sourceRow in ReadFavoriteClipboardRows(sourceConnection))
            {
                string mappedImagePath = RemapManagedImagePath(
                    sourceRow.ImagePath,
                    sourceRoot,
                    targetRoot,
                    clipboardImageMap,
                    favoriteImageMap);

                if (FavoriteClipboardExists(targetConnection, sourceRow, mappedImagePath))
                {
                    continue;
                }

                using var insertCommand = targetConnection.CreateCommand();
                insertCommand.CommandText = @"
                    INSERT INTO FavoriteClipboard (ContentType, TextContent, ImagePath, Timestamp, ContentHash)
                    VALUES (@contentType, @textContent, @imagePath, @timestamp, @contentHash)";
                insertCommand.Parameters.AddWithValue("@contentType", sourceRow.ContentType);
                insertCommand.Parameters.AddWithValue("@textContent", ToDbValue(sourceRow.TextContent));
                insertCommand.Parameters.AddWithValue("@imagePath", ToDbValue(mappedImagePath));
                insertCommand.Parameters.AddWithValue("@timestamp", sourceRow.Timestamp);
                insertCommand.Parameters.AddWithValue("@contentHash", ToDbValue(sourceRow.ContentHash));
                insertCommand.ExecuteNonQuery();
            }
        }

        private static List<ClipboardHistoryRowData> ReadClipboardHistoryRows(SqliteConnection sourceConnection)
        {
            var rows = new List<ClipboardHistoryRowData>();
            bool hasIsPinned = HasColumn(sourceConnection, "ClipboardHistory", "IsPinned");
            bool hasContentHash = HasColumn(sourceConnection, "ClipboardHistory", "ContentHash");

            string isPinnedExpr = hasIsPinned ? "COALESCE(IsPinned, 0)" : "0";
            string contentHashExpr = hasContentHash ? "COALESCE(ContentHash, '')" : "''";

            using var command = sourceConnection.CreateCommand();
            command.CommandText = $@"
                SELECT ContentType,
                       TextContent,
                       ImagePath,
                       Timestamp,
                       {isPinnedExpr} AS IsPinned,
                       {contentHashExpr} AS ContentHash
                FROM ClipboardHistory
                ORDER BY Timestamp ASC, Id ASC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ClipboardHistoryRowData
                {
                    ContentType = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    TextContent = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ImagePath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Timestamp = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    IsPinned = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                    ContentHash = reader.IsDBNull(5) ? string.Empty : reader.GetString(5)
                });
            }

            return rows;
        }

        private static List<FavoriteClipboardRowData> ReadFavoriteClipboardRows(SqliteConnection sourceConnection)
        {
            var rows = new List<FavoriteClipboardRowData>();
            bool hasContentHash = HasColumn(sourceConnection, "FavoriteClipboard", "ContentHash");
            string contentHashExpr = hasContentHash ? "COALESCE(ContentHash, '')" : "''";

            using var command = sourceConnection.CreateCommand();
            command.CommandText = $@"
                SELECT ContentType,
                       TextContent,
                       ImagePath,
                       Timestamp,
                       {contentHashExpr} AS ContentHash
                FROM FavoriteClipboard
                ORDER BY Timestamp ASC, Id ASC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new FavoriteClipboardRowData
                {
                    ContentType = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    TextContent = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    ImagePath = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    Timestamp = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    ContentHash = reader.IsDBNull(4) ? string.Empty : reader.GetString(4)
                });
            }

            return rows;
        }

        private static bool ClipboardHistoryExists(SqliteConnection targetConnection, ClipboardHistoryRowData row, string mappedImagePath)
        {
            using var command = targetConnection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(row.ContentHash))
            {
                command.CommandText = @"
                    SELECT 1
                    FROM ClipboardHistory
                    WHERE ContentType = @contentType
                      AND COALESCE(ContentHash, '') = @contentHash
                    LIMIT 1";
                command.Parameters.AddWithValue("@contentType", row.ContentType);
                command.Parameters.AddWithValue("@contentHash", row.ContentHash);
            }
            else
            {
                command.CommandText = @"
                    SELECT 1
                    FROM ClipboardHistory
                    WHERE ContentType = @contentType
                      AND COALESCE(TextContent, '') = @textContent
                      AND COALESCE(ImagePath, '') = @imagePath
                      AND Timestamp = @timestamp
                    LIMIT 1";
                command.Parameters.AddWithValue("@contentType", row.ContentType);
                command.Parameters.AddWithValue("@textContent", row.TextContent ?? string.Empty);
                command.Parameters.AddWithValue("@imagePath", mappedImagePath ?? string.Empty);
                command.Parameters.AddWithValue("@timestamp", row.Timestamp ?? string.Empty);
            }

            return command.ExecuteScalar() != null;
        }

        private static bool FavoriteClipboardExists(SqliteConnection targetConnection, FavoriteClipboardRowData row, string mappedImagePath)
        {
            using var command = targetConnection.CreateCommand();
            if (!string.IsNullOrWhiteSpace(row.ContentHash))
            {
                command.CommandText = @"
                    SELECT 1
                    FROM FavoriteClipboard
                    WHERE ContentType = @contentType
                      AND COALESCE(ContentHash, '') = @contentHash
                    LIMIT 1";
                command.Parameters.AddWithValue("@contentType", row.ContentType);
                command.Parameters.AddWithValue("@contentHash", row.ContentHash);
            }
            else
            {
                command.CommandText = @"
                    SELECT 1
                    FROM FavoriteClipboard
                    WHERE ContentType = @contentType
                      AND COALESCE(TextContent, '') = @textContent
                      AND COALESCE(ImagePath, '') = @imagePath
                      AND Timestamp = @timestamp
                    LIMIT 1";
                command.Parameters.AddWithValue("@contentType", row.ContentType);
                command.Parameters.AddWithValue("@textContent", row.TextContent ?? string.Empty);
                command.Parameters.AddWithValue("@imagePath", mappedImagePath ?? string.Empty);
                command.Parameters.AddWithValue("@timestamp", row.Timestamp ?? string.Empty);
            }

            return command.ExecuteScalar() != null;
        }

        private static string RemapManagedImagePath(
            string originalPath,
            string sourceRoot,
            string targetRoot,
            Dictionary<string, string> clipboardImageMap,
            Dictionary<string, string> favoriteImageMap)
        {
            if (string.IsNullOrWhiteSpace(originalPath))
            {
                return string.Empty;
            }

            try
            {
                string normalizedOriginal = Path.GetFullPath(originalPath);
                if (TryMapDirectoryPath(
                    normalizedOriginal,
                    Path.Combine(sourceRoot, ClipboardImageFolderName),
                    Path.Combine(targetRoot, ClipboardImageFolderName),
                    clipboardImageMap,
                    out string mappedClipboard))
                {
                    return mappedClipboard;
                }

                if (TryMapDirectoryPath(
                    normalizedOriginal,
                    Path.Combine(sourceRoot, FavoriteImageFolderName),
                    Path.Combine(targetRoot, FavoriteImageFolderName),
                    favoriteImageMap,
                    out string mappedFavorite))
                {
                    return mappedFavorite;
                }
            }
            catch
            {
            }

            return originalPath;
        }

        private static bool TryMapDirectoryPath(
            string normalizedOriginalPath,
            string sourceDirectory,
            string targetDirectory,
            Dictionary<string, string> copiedPathMap,
            out string mappedPath)
        {
            mappedPath = string.Empty;

            string sourcePrefix = EnsureTrailingSeparator(Path.GetFullPath(sourceDirectory));
            if (!normalizedOriginalPath.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (copiedPathMap.TryGetValue(normalizedOriginalPath, out string? copiedTargetPath) &&
                !string.IsNullOrWhiteSpace(copiedTargetPath))
            {
                mappedPath = copiedTargetPath;
                return true;
            }

            string relativePath = normalizedOriginalPath.Substring(sourcePrefix.Length);
            mappedPath = Path.Combine(targetDirectory, relativePath);
            return true;
        }

        private static Dictionary<string, string> MergeDirectoryWithMap(string sourceDirectory, string targetDirectory)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(sourceDirectory))
            {
                return map;
            }

            Directory.CreateDirectory(targetDirectory);

            foreach (string sourceFilePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string normalizedSourcePath = Path.GetFullPath(sourceFilePath);
                string relativePath = Path.GetRelativePath(sourceDirectory, sourceFilePath);
                string targetPath = Path.Combine(targetDirectory, relativePath);
                string? targetFolder = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(targetFolder))
                {
                    Directory.CreateDirectory(targetFolder);
                }

                string resolvedTargetPath = ResolveMergeDestinationPath(normalizedSourcePath, targetPath);
                if (!File.Exists(resolvedTargetPath))
                {
                    File.Copy(normalizedSourcePath, resolvedTargetPath, overwrite: false);
                }

                map[normalizedSourcePath] = Path.GetFullPath(resolvedTargetPath);
            }

            return map;
        }

        private static string ResolveMergeDestinationPath(string sourceFilePath, string expectedTargetPath)
        {
            if (!File.Exists(expectedTargetPath))
            {
                return expectedTargetPath;
            }

            if (AreFilesIdentical(sourceFilePath, expectedTargetPath))
            {
                return expectedTargetPath;
            }

            string? directory = Path.GetDirectoryName(expectedTargetPath);
            string baseName = Path.GetFileNameWithoutExtension(expectedTargetPath);
            string extension = Path.GetExtension(expectedTargetPath);
            directory ??= Path.GetDirectoryName(sourceFilePath) ?? string.Empty;

            int suffix = 1;
            while (true)
            {
                string candidate = Path.Combine(directory, $"{baseName}_merged_{suffix}{extension}");
                if (!File.Exists(candidate))
                {
                    return candidate;
                }

                if (AreFilesIdentical(sourceFilePath, candidate))
                {
                    return candidate;
                }

                suffix++;
            }
        }

        private static bool AreFilesIdentical(string firstFilePath, string secondFilePath)
        {
            try
            {
                var firstInfo = new FileInfo(firstFilePath);
                var secondInfo = new FileInfo(secondFilePath);
                if (!firstInfo.Exists || !secondInfo.Exists || firstInfo.Length != secondInfo.Length)
                {
                    return false;
                }

                const int bufferSize = 81920;
                byte[] firstBuffer = new byte[bufferSize];
                byte[] secondBuffer = new byte[bufferSize];

                using var firstStream = new FileStream(firstFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var secondStream = new FileStream(secondFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                while (true)
                {
                    int firstRead = firstStream.Read(firstBuffer, 0, bufferSize);
                    int secondRead = secondStream.Read(secondBuffer, 0, bufferSize);
                    if (firstRead != secondRead)
                    {
                        return false;
                    }

                    if (firstRead == 0)
                    {
                        return true;
                    }

                    for (int i = 0; i < firstRead; i++)
                    {
                        if (firstBuffer[i] != secondBuffer[i])
                        {
                            return false;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool HasTable(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table'
                  AND name = @tableName";
            command.Parameters.AddWithValue("@tableName", tableName);
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                if (string.Equals(name, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void ClearManagedData(string targetRoot)
        {
            TryDeleteFile(Path.Combine(targetRoot, DatabaseFileName));
            TryDeleteFile(Path.Combine(targetRoot, $"{DatabaseFileName}-wal"));
            TryDeleteFile(Path.Combine(targetRoot, $"{DatabaseFileName}-shm"));

            string clipboardFolder = Path.Combine(targetRoot, ClipboardImageFolderName);
            string favoriteFolder = Path.Combine(targetRoot, FavoriteImageFolderName);

            if (Directory.Exists(clipboardFolder))
            {
                Directory.Delete(clipboardFolder, recursive: true);
            }

            if (Directory.Exists(favoriteFolder))
            {
                Directory.Delete(favoriteFolder, recursive: true);
            }
        }

        private static void CopyFileIfExists(string sourceRoot, string targetRoot, string fileName)
        {
            string source = Path.Combine(sourceRoot, fileName);
            if (!File.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(targetRoot);
            string target = Path.Combine(targetRoot, fileName);
            File.Copy(source, target, overwrite: true);
        }

        private static void CopyDirectoryIfExists(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);

            foreach (string sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, sourceFile);
                string destination = Path.Combine(targetDirectory, relative);
                string? destinationFolder = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourceFile, destination, overwrite: true);
            }
        }

        private static void AppendLogIfExists(string sourceLogPath, string targetLogPath)
        {
            if (!File.Exists(sourceLogPath))
            {
                return;
            }

            string? targetDirectory = Path.GetDirectoryName(targetLogPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            if (!File.Exists(targetLogPath))
            {
                File.Copy(sourceLogPath, targetLogPath, overwrite: true);
                return;
            }

            string content = File.ReadAllText(sourceLogPath);
            if (string.IsNullOrEmpty(content))
            {
                return;
            }

            if (new FileInfo(targetLogPath).Length > 0)
            {
                File.AppendAllText(targetLogPath, Environment.NewLine);
            }

            File.AppendAllText(targetLogPath, content);
        }

        private static object ToDbValue(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;
        }

        private static string EnsureTrailingSeparator(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        private static void TryDeleteFile(string filePath)
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                }
            }
            catch
            {
            }
        }

        private static string NormalizeDirectoryPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
