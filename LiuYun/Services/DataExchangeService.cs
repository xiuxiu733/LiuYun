using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace LiuYun.Services
{
    public static class DataExchangeService
    {
        private const string PackageManifestFileName = "manifest.json";
        private const string JsonPayloadFileName = "data.json";
        private const string DatabaseFileName = "data.db";
        private const string ClipboardImageFolderName = "clipboard_images";
        private const string FavoriteImageFolderName = "favorite_images";

        private sealed class DataPackageManifest
        {
            public string Format { get; set; } = string.Empty;
            public int Version { get; set; }
            public string CreatedAtUtc { get; set; } = string.Empty;
            public string SourceDataRoot { get; set; } = string.Empty;
            public string DataFile { get; set; } = string.Empty;
        }

        private sealed class JsonPayload
        {
            public List<ConfigRow> Config { get; set; } = new List<ConfigRow>();
            public List<ClipboardHistoryRow> ClipboardHistory { get; set; } = new List<ClipboardHistoryRow>();
            public List<FavoriteClipboardRow> FavoriteClipboard { get; set; } = new List<FavoriteClipboardRow>();
        }

        private sealed class ConfigRow
        {
            public string Key { get; set; } = string.Empty;
            public string Value { get; set; } = string.Empty;
        }

        private sealed class ClipboardHistoryRow
        {
            public int Id { get; set; }
            public int ContentType { get; set; }
            public string? TextContent { get; set; }
            public string? ImagePath { get; set; }
            public string Timestamp { get; set; } = string.Empty;
            public int IsPinned { get; set; }
            public string? ContentHash { get; set; }
        }

        private sealed class FavoriteClipboardRow
        {
            public int Id { get; set; }
            public int ContentType { get; set; }
            public string? TextContent { get; set; }
            public string? ImagePath { get; set; }
            public string Timestamp { get; set; } = string.Empty;
            public string? ContentHash { get; set; }
        }

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static string ImportWorkspaceRoot => Path.Combine(StoragePathService.RuntimeRoot, "imports");
        private static string ImportBackupDirectory => Path.Combine(ImportWorkspaceRoot, "backups");

        public static Task ExportJsonPackageAsync(string outputZipPath)
        {
            return Task.Run(() => ExportJsonPackage(outputZipPath));
        }

        public static Task ImportJsonPackageAsync(string packagePath)
        {
            return Task.Run(() => ImportJsonPackage(packagePath));
        }
        private static void ExportJsonPackage(string outputZipPath)
        {
            ValidateOutputZipPath(outputZipPath);

            string dataRoot = StoragePathService.GetCurrentDataRoot();
            string tempWorkspace = CreateTempWorkspace("liuyun-export-json");
            try
            {
                JsonPayload payload = BuildJsonPayload();
                string payloadPath = Path.Combine(tempWorkspace, JsonPayloadFileName);
                string payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
                File.WriteAllText(payloadPath, payloadJson);

                CopyDirectoryIfExists(
                    Path.Combine(dataRoot, ClipboardImageFolderName),
                    Path.Combine(tempWorkspace, ClipboardImageFolderName));
                CopyDirectoryIfExists(
                    Path.Combine(dataRoot, FavoriteImageFolderName),
                    Path.Combine(tempWorkspace, FavoriteImageFolderName));

                DataPackageManifest manifest = new DataPackageManifest
                {
                    Format = "json",
                    Version = 1,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    SourceDataRoot = dataRoot,
                    DataFile = JsonPayloadFileName
                };
                WriteManifest(tempWorkspace, manifest);

                CreateZipFromDirectory(tempWorkspace, outputZipPath);
            }
            finally
            {
                TryDeleteDirectory(tempWorkspace);
            }
        }

        private static void ImportJsonPackage(string packagePath)
        {
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                throw new FileNotFoundException("Import package file not found.", packagePath);
            }

            if (!TryReadPackageManifest(packagePath, out DataPackageManifest? manifest, out string validationMessage))
            {
                throw new InvalidDataException(validationMessage);
            }

            if (manifest == null || !string.Equals(manifest.Format, "json", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Only JSON package import is supported.");
            }

            ApplyImportPackage(packagePath);
        }

        private static void ApplyImportPackage(string packagePath)
        {
            string dataRoot = StoragePathService.GetCurrentDataRoot();
            Directory.CreateDirectory(dataRoot);

            CreatePreImportBackup(dataRoot);

            string extractionWorkspace = CreateTempWorkspace("liuyun-import");
            try
            {
                ZipFile.ExtractToDirectory(packagePath, extractionWorkspace);

                DataPackageManifest manifest = ReadManifest(extractionWorkspace);
                string format = manifest.Format?.Trim().ToLowerInvariant() ?? string.Empty;
                if (!string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("Only JSON package import is supported.");
                }

                ImportFromJsonPackage(extractionWorkspace, manifest, dataRoot);
            }
            finally
            {
                TryDeleteDirectory(extractionWorkspace);
            }
        }

        private static void ImportFromJsonPackage(string extractionWorkspace, DataPackageManifest manifest, string dataRoot)
        {
            string payloadPath = Path.Combine(extractionWorkspace, manifest.DataFile ?? JsonPayloadFileName);
            if (!File.Exists(payloadPath))
            {
                throw new FileNotFoundException("JSON package missing payload file.", payloadPath);
            }

            JsonPayload? payload = JsonSerializer.Deserialize<JsonPayload>(File.ReadAllText(payloadPath), JsonOptions);
            if (payload == null)
            {
                throw new InvalidDataException("JSON package payload is invalid.");
            }

            string targetDatabase = Path.Combine(dataRoot, DatabaseFileName);
            if (HasExistingClipboardData(targetDatabase))
            {
                ImportJsonByMergingManagedData(payload, extractionWorkspace, manifest, dataRoot, targetDatabase);
                return;
            }

            ImportJsonByReplacingManagedData(payload, extractionWorkspace, manifest, dataRoot, targetDatabase);
        }

        private static void ImportJsonByReplacingManagedData(
            JsonPayload payload,
            string extractionWorkspace,
            DataPackageManifest manifest,
            string dataRoot,
            string targetDatabase)
        {
            ClearManagedData(dataRoot);
            DatabaseService.InitializeAtPath(targetDatabase);

            using var connection = new SqliteConnection(DatabaseService.BuildConnectionString(targetDatabase));
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                ExecuteNonQuery(connection, "DELETE FROM config");
                ExecuteNonQuery(connection, "DELETE FROM ClipboardHistory");
                ExecuteNonQuery(connection, "DELETE FROM FavoriteClipboard");

                using var configCommand = connection.CreateCommand();
                configCommand.CommandText = "INSERT OR REPLACE INTO config (key, value) VALUES (@key, @value)";
                var configKey = configCommand.Parameters.Add("@key", SqliteType.Text);
                var configValue = configCommand.Parameters.Add("@value", SqliteType.Text);
                configCommand.Prepare();

                foreach (ConfigRow row in payload.Config)
                {
                    configKey.Value = row.Key ?? string.Empty;
                    configValue.Value = row.Value ?? string.Empty;
                    configCommand.ExecuteNonQuery();
                }

                using var historyCommand = connection.CreateCommand();
                historyCommand.CommandText = @"
                    INSERT INTO ClipboardHistory (Id, ContentType, TextContent, ImagePath, Timestamp, IsPinned, ContentHash)
                    VALUES (@id, @contentType, @textContent, @imagePath, @timestamp, @isPinned, @contentHash)";
                var historyId = historyCommand.Parameters.Add("@id", SqliteType.Integer);
                var historyContentType = historyCommand.Parameters.Add("@contentType", SqliteType.Integer);
                var historyTextContent = historyCommand.Parameters.Add("@textContent", SqliteType.Text);
                var historyImagePath = historyCommand.Parameters.Add("@imagePath", SqliteType.Text);
                var historyTimestamp = historyCommand.Parameters.Add("@timestamp", SqliteType.Text);
                var historyIsPinned = historyCommand.Parameters.Add("@isPinned", SqliteType.Integer);
                var historyContentHash = historyCommand.Parameters.Add("@contentHash", SqliteType.Text);
                historyCommand.Prepare();

                foreach (ClipboardHistoryRow row in payload.ClipboardHistory)
                {
                    historyId.Value = row.Id;
                    historyContentType.Value = row.ContentType;
                    historyTextContent.Value = ToDbValue(row.TextContent);
                    historyImagePath.Value = ToDbValue(RemapManagedImagePath(row.ImagePath, manifest.SourceDataRoot, dataRoot));
                    historyTimestamp.Value = row.Timestamp ?? string.Empty;
                    historyIsPinned.Value = row.IsPinned;
                    historyContentHash.Value = ToDbValue(row.ContentHash);
                    historyCommand.ExecuteNonQuery();
                }

                using var favoriteCommand = connection.CreateCommand();
                favoriteCommand.CommandText = @"
                    INSERT INTO FavoriteClipboard (Id, ContentType, TextContent, ImagePath, Timestamp, ContentHash)
                    VALUES (@id, @contentType, @textContent, @imagePath, @timestamp, @contentHash)";
                var favoriteId = favoriteCommand.Parameters.Add("@id", SqliteType.Integer);
                var favoriteContentType = favoriteCommand.Parameters.Add("@contentType", SqliteType.Integer);
                var favoriteTextContent = favoriteCommand.Parameters.Add("@textContent", SqliteType.Text);
                var favoriteImagePath = favoriteCommand.Parameters.Add("@imagePath", SqliteType.Text);
                var favoriteTimestamp = favoriteCommand.Parameters.Add("@timestamp", SqliteType.Text);
                var favoriteContentHash = favoriteCommand.Parameters.Add("@contentHash", SqliteType.Text);
                favoriteCommand.Prepare();

                foreach (FavoriteClipboardRow row in payload.FavoriteClipboard)
                {
                    favoriteId.Value = row.Id;
                    favoriteContentType.Value = row.ContentType;
                    favoriteTextContent.Value = ToDbValue(row.TextContent);
                    favoriteImagePath.Value = ToDbValue(RemapManagedImagePath(row.ImagePath, manifest.SourceDataRoot, dataRoot));
                    favoriteTimestamp.Value = row.Timestamp ?? string.Empty;
                    favoriteContentHash.Value = ToDbValue(row.ContentHash);
                    favoriteCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            CopyDirectoryIfExists(
                Path.Combine(extractionWorkspace, ClipboardImageFolderName),
                Path.Combine(dataRoot, ClipboardImageFolderName));
            CopyDirectoryIfExists(
                Path.Combine(extractionWorkspace, FavoriteImageFolderName),
                Path.Combine(dataRoot, FavoriteImageFolderName));
        }

        private static void ImportJsonByMergingManagedData(
            JsonPayload payload,
            string extractionWorkspace,
            DataPackageManifest manifest,
            string dataRoot,
            string targetDatabase)
        {
            DatabaseService.InitializeAtPath(targetDatabase);

            using var connection = new SqliteConnection(DatabaseService.BuildConnectionString(targetDatabase));
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var mergeConfigCommand = connection.CreateCommand();
                mergeConfigCommand.CommandText = @"
                    INSERT INTO config (key, value)
                    SELECT @key, @value
                    WHERE NOT EXISTS (SELECT 1 FROM config WHERE key = @key)";
                var configKey = mergeConfigCommand.Parameters.Add("@key", SqliteType.Text);
                var configValue = mergeConfigCommand.Parameters.Add("@value", SqliteType.Text);
                mergeConfigCommand.Prepare();

                foreach (ConfigRow row in payload.Config)
                {
                    if (string.IsNullOrWhiteSpace(row.Key))
                    {
                        continue;
                    }

                    configKey.Value = row.Key;
                    configValue.Value = row.Value ?? string.Empty;
                    mergeConfigCommand.ExecuteNonQuery();
                }

                var imagePathCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                using var historyExistsByHash = connection.CreateCommand();
                historyExistsByHash.CommandText = @"
                    SELECT 1
                    FROM ClipboardHistory
                    WHERE ContentType = @contentType
                      AND COALESCE(ContentHash, '') = @contentHash
                    LIMIT 1";
                var historyExistsByHashType = historyExistsByHash.Parameters.Add("@contentType", SqliteType.Integer);
                var historyExistsByHashValue = historyExistsByHash.Parameters.Add("@contentHash", SqliteType.Text);
                historyExistsByHash.Prepare();

                using var historyExistsByFields = connection.CreateCommand();
                historyExistsByFields.CommandText = @"
                    SELECT 1
                    FROM ClipboardHistory
                    WHERE ContentType = @contentType
                      AND COALESCE(TextContent, '') = @textContent
                      AND COALESCE(ImagePath, '') = @imagePath
                      AND Timestamp = @timestamp
                    LIMIT 1";
                var historyExistsByFieldsType = historyExistsByFields.Parameters.Add("@contentType", SqliteType.Integer);
                var historyExistsByFieldsText = historyExistsByFields.Parameters.Add("@textContent", SqliteType.Text);
                var historyExistsByFieldsImage = historyExistsByFields.Parameters.Add("@imagePath", SqliteType.Text);
                var historyExistsByFieldsTime = historyExistsByFields.Parameters.Add("@timestamp", SqliteType.Text);
                historyExistsByFields.Prepare();

                using var insertHistoryCommand = connection.CreateCommand();
                insertHistoryCommand.CommandText = @"
                    INSERT INTO ClipboardHistory (ContentType, TextContent, ImagePath, Timestamp, IsPinned, ContentHash)
                    VALUES (@contentType, @textContent, @imagePath, @timestamp, @isPinned, @contentHash)";
                var insertHistoryType = insertHistoryCommand.Parameters.Add("@contentType", SqliteType.Integer);
                var insertHistoryText = insertHistoryCommand.Parameters.Add("@textContent", SqliteType.Text);
                var insertHistoryImage = insertHistoryCommand.Parameters.Add("@imagePath", SqliteType.Text);
                var insertHistoryTime = insertHistoryCommand.Parameters.Add("@timestamp", SqliteType.Text);
                var insertHistoryPinned = insertHistoryCommand.Parameters.Add("@isPinned", SqliteType.Integer);
                var insertHistoryHash = insertHistoryCommand.Parameters.Add("@contentHash", SqliteType.Text);
                insertHistoryCommand.Prepare();

                foreach (ClipboardHistoryRow row in payload.ClipboardHistory)
                {
                    string mergedImagePath = ResolveMergedImagePath(
                        row.ImagePath,
                        manifest.SourceDataRoot,
                        extractionWorkspace,
                        dataRoot,
                        imagePathCache);

                    string contentHash = row.ContentHash ?? string.Empty;
                    bool exists;
                    if (!string.IsNullOrWhiteSpace(contentHash))
                    {
                        historyExistsByHashType.Value = row.ContentType;
                        historyExistsByHashValue.Value = contentHash;
                        exists = historyExistsByHash.ExecuteScalar() != null;
                    }
                    else
                    {
                        historyExistsByFieldsType.Value = row.ContentType;
                        historyExistsByFieldsText.Value = row.TextContent ?? string.Empty;
                        historyExistsByFieldsImage.Value = mergedImagePath;
                        historyExistsByFieldsTime.Value = row.Timestamp ?? string.Empty;
                        exists = historyExistsByFields.ExecuteScalar() != null;
                    }

                    if (exists)
                    {
                        continue;
                    }

                    insertHistoryType.Value = row.ContentType;
                    insertHistoryText.Value = ToDbValue(row.TextContent);
                    insertHistoryImage.Value = ToDbValue(mergedImagePath);
                    insertHistoryTime.Value = row.Timestamp ?? string.Empty;
                    insertHistoryPinned.Value = row.IsPinned;
                    insertHistoryHash.Value = ToDbValue(row.ContentHash);
                    insertHistoryCommand.ExecuteNonQuery();
                }

                using var favoriteExistsByHash = connection.CreateCommand();
                favoriteExistsByHash.CommandText = @"
                    SELECT 1
                    FROM FavoriteClipboard
                    WHERE ContentType = @contentType
                      AND COALESCE(ContentHash, '') = @contentHash
                    LIMIT 1";
                var favoriteExistsByHashType = favoriteExistsByHash.Parameters.Add("@contentType", SqliteType.Integer);
                var favoriteExistsByHashValue = favoriteExistsByHash.Parameters.Add("@contentHash", SqliteType.Text);
                favoriteExistsByHash.Prepare();

                using var favoriteExistsByFields = connection.CreateCommand();
                favoriteExistsByFields.CommandText = @"
                    SELECT 1
                    FROM FavoriteClipboard
                    WHERE ContentType = @contentType
                      AND COALESCE(TextContent, '') = @textContent
                      AND COALESCE(ImagePath, '') = @imagePath
                      AND Timestamp = @timestamp
                    LIMIT 1";
                var favoriteExistsByFieldsType = favoriteExistsByFields.Parameters.Add("@contentType", SqliteType.Integer);
                var favoriteExistsByFieldsText = favoriteExistsByFields.Parameters.Add("@textContent", SqliteType.Text);
                var favoriteExistsByFieldsImage = favoriteExistsByFields.Parameters.Add("@imagePath", SqliteType.Text);
                var favoriteExistsByFieldsTime = favoriteExistsByFields.Parameters.Add("@timestamp", SqliteType.Text);
                favoriteExistsByFields.Prepare();

                using var insertFavoriteCommand = connection.CreateCommand();
                insertFavoriteCommand.CommandText = @"
                    INSERT INTO FavoriteClipboard (ContentType, TextContent, ImagePath, Timestamp, ContentHash)
                    VALUES (@contentType, @textContent, @imagePath, @timestamp, @contentHash)";
                var insertFavoriteType = insertFavoriteCommand.Parameters.Add("@contentType", SqliteType.Integer);
                var insertFavoriteText = insertFavoriteCommand.Parameters.Add("@textContent", SqliteType.Text);
                var insertFavoriteImage = insertFavoriteCommand.Parameters.Add("@imagePath", SqliteType.Text);
                var insertFavoriteTime = insertFavoriteCommand.Parameters.Add("@timestamp", SqliteType.Text);
                var insertFavoriteHash = insertFavoriteCommand.Parameters.Add("@contentHash", SqliteType.Text);
                insertFavoriteCommand.Prepare();

                foreach (FavoriteClipboardRow row in payload.FavoriteClipboard)
                {
                    string mergedImagePath = ResolveMergedImagePath(
                        row.ImagePath,
                        manifest.SourceDataRoot,
                        extractionWorkspace,
                        dataRoot,
                        imagePathCache);

                    string contentHash = row.ContentHash ?? string.Empty;
                    bool exists;
                    if (!string.IsNullOrWhiteSpace(contentHash))
                    {
                        favoriteExistsByHashType.Value = row.ContentType;
                        favoriteExistsByHashValue.Value = contentHash;
                        exists = favoriteExistsByHash.ExecuteScalar() != null;
                    }
                    else
                    {
                        favoriteExistsByFieldsType.Value = row.ContentType;
                        favoriteExistsByFieldsText.Value = row.TextContent ?? string.Empty;
                        favoriteExistsByFieldsImage.Value = mergedImagePath;
                        favoriteExistsByFieldsTime.Value = row.Timestamp ?? string.Empty;
                        exists = favoriteExistsByFields.ExecuteScalar() != null;
                    }

                    if (exists)
                    {
                        continue;
                    }

                    insertFavoriteType.Value = row.ContentType;
                    insertFavoriteText.Value = ToDbValue(row.TextContent);
                    insertFavoriteImage.Value = ToDbValue(mergedImagePath);
                    insertFavoriteTime.Value = row.Timestamp ?? string.Empty;
                    insertFavoriteHash.Value = ToDbValue(row.ContentHash);
                    insertFavoriteCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static bool HasExistingClipboardData(string databasePath)
        {
            if (!File.Exists(databasePath))
            {
                return false;
            }

            try
            {
                using var connection = new SqliteConnection(DatabaseService.BuildConnectionString(databasePath));
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    SELECT
                        (SELECT COUNT(1) FROM ClipboardHistory) +
                        (SELECT COUNT(1) FROM FavoriteClipboard)";
                long total = (long)(command.ExecuteScalar() ?? 0L);
                return total > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveMergedImagePath(
            string? originalImagePath,
            string sourceDataRoot,
            string extractionWorkspace,
            string targetDataRoot,
            Dictionary<string, string> imagePathCache)
        {
            if (string.IsNullOrWhiteSpace(originalImagePath))
            {
                return string.Empty;
            }

            try
            {
                string normalizedOriginal = Path.GetFullPath(originalImagePath);

                string sourceClipboardRoot = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(sourceDataRoot, ClipboardImageFolderName)));
                if (!string.IsNullOrWhiteSpace(sourceClipboardRoot) &&
                    normalizedOriginal.StartsWith(sourceClipboardRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = normalizedOriginal.Substring(sourceClipboardRoot.Length);
                    string mergedPath = CopyMergedImageWithRelativePath(
                        Path.Combine(extractionWorkspace, ClipboardImageFolderName, relative),
                        Path.Combine(targetDataRoot, ClipboardImageFolderName),
                        relative,
                        imagePathCache);
                    if (!string.IsNullOrWhiteSpace(mergedPath))
                    {
                        return mergedPath;
                    }
                }

                string sourceFavoriteRoot = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(sourceDataRoot, FavoriteImageFolderName)));
                if (!string.IsNullOrWhiteSpace(sourceFavoriteRoot) &&
                    normalizedOriginal.StartsWith(sourceFavoriteRoot, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = normalizedOriginal.Substring(sourceFavoriteRoot.Length);
                    string mergedPath = CopyMergedImageWithRelativePath(
                        Path.Combine(extractionWorkspace, FavoriteImageFolderName, relative),
                        Path.Combine(targetDataRoot, FavoriteImageFolderName),
                        relative,
                        imagePathCache);
                    if (!string.IsNullOrWhiteSpace(mergedPath))
                    {
                        return mergedPath;
                    }
                }
            }
            catch
            {
            }

            return RemapManagedImagePath(originalImagePath, sourceDataRoot, targetDataRoot);
        }

        private static string CopyMergedImageWithRelativePath(
            string sourceImagePath,
            string targetRootDirectory,
            string relativePath,
            Dictionary<string, string> imagePathCache)
        {
            if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
            {
                return string.Empty;
            }

            if (imagePathCache.TryGetValue(sourceImagePath, out string? cachedPath))
            {
                return cachedPath;
            }

            string sanitizedRelativePath = relativePath
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            string normalizedTargetRoot = EnsureTrailingSeparator(Path.GetFullPath(targetRootDirectory));
            string candidatePath = Path.GetFullPath(Path.Combine(targetRootDirectory, sanitizedRelativePath));
            if (!candidatePath.StartsWith(normalizedTargetRoot, StringComparison.OrdinalIgnoreCase))
            {
                string extension = Path.GetExtension(sourceImagePath);
                candidatePath = Path.Combine(targetRootDirectory, $"{Guid.NewGuid():N}{extension}");
            }

            string? candidateDirectory = Path.GetDirectoryName(candidatePath);
            if (!string.IsNullOrWhiteSpace(candidateDirectory))
            {
                Directory.CreateDirectory(candidateDirectory);
            }

            string targetPath = candidatePath;
            if (File.Exists(targetPath))
            {
                string extension = Path.GetExtension(targetPath);
                string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(targetPath);
                string? parentDirectory = Path.GetDirectoryName(targetPath);
                targetPath = Path.Combine(
                    parentDirectory ?? targetRootDirectory,
                    $"{fileNameWithoutExtension}-{Guid.NewGuid():N}{extension}");
            }

            File.Copy(sourceImagePath, targetPath, overwrite: false);
            imagePathCache[sourceImagePath] = targetPath;
            return targetPath;
        }

        private static void CreatePreImportBackup(string dataRoot)
        {
            Directory.CreateDirectory(ImportBackupDirectory);
            string backupPath = Path.Combine(ImportBackupDirectory, $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            string backupWorkspace = CreateTempWorkspace("liuyun-import-backup");
            try
            {
                CopyFileIfExists(
                    Path.Combine(dataRoot, DatabaseFileName),
                    Path.Combine(backupWorkspace, DatabaseFileName));
                CopyFileIfExists(
                    Path.Combine(dataRoot, $"{DatabaseFileName}-wal"),
                    Path.Combine(backupWorkspace, $"{DatabaseFileName}-wal"));
                CopyFileIfExists(
                    Path.Combine(dataRoot, $"{DatabaseFileName}-shm"),
                    Path.Combine(backupWorkspace, $"{DatabaseFileName}-shm"));

                CopyDirectoryIfExists(
                    Path.Combine(dataRoot, ClipboardImageFolderName),
                    Path.Combine(backupWorkspace, ClipboardImageFolderName));
                CopyDirectoryIfExists(
                    Path.Combine(dataRoot, FavoriteImageFolderName),
                    Path.Combine(backupWorkspace, FavoriteImageFolderName));

                DataPackageManifest manifest = new DataPackageManifest
                {
                    Format = "sqlite",
                    Version = 1,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    SourceDataRoot = dataRoot,
                    DataFile = DatabaseFileName
                };
                WriteManifest(backupWorkspace, manifest);

                CreateZipFromDirectory(backupWorkspace, backupPath, CompressionLevel.Fastest);
            }
            finally
            {
                TryDeleteDirectory(backupWorkspace);
            }
        }

        private static JsonPayload BuildJsonPayload()
        {
            EnsureDatabaseExists();

            JsonPayload payload = new JsonPayload();
            using var connection = new SqliteConnection(DatabaseService.ConnectionString);
            connection.Open();

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT key, value FROM config";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    payload.Config.Add(new ConfigRow
                    {
                        Key = reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        Value = reader.IsDBNull(1) ? string.Empty : reader.GetString(1)
                    });
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT Id, ContentType, TextContent, ImagePath, Timestamp, COALESCE(IsPinned, 0), COALESCE(ContentHash, '')
                    FROM ClipboardHistory
                    ORDER BY Id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    payload.ClipboardHistory.Add(new ClipboardHistoryRow
                    {
                        Id = reader.GetInt32(0),
                        ContentType = reader.GetInt32(1),
                        TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                        ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Timestamp = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        IsPinned = reader.GetInt32(5),
                        ContentHash = reader.IsDBNull(6) ? null : reader.GetString(6)
                    });
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
                    SELECT Id, ContentType, TextContent, ImagePath, Timestamp, COALESCE(ContentHash, '')
                    FROM FavoriteClipboard
                    ORDER BY Id";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    payload.FavoriteClipboard.Add(new FavoriteClipboardRow
                    {
                        Id = reader.GetInt32(0),
                        ContentType = reader.GetInt32(1),
                        TextContent = reader.IsDBNull(2) ? null : reader.GetString(2),
                        ImagePath = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Timestamp = reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                        ContentHash = reader.IsDBNull(5) ? null : reader.GetString(5)
                    });
                }
            }

            return payload;
        }

        private static void EnsureDatabaseExists()
        {
            if (!File.Exists(DatabaseService.DbPath))
            {
                DatabaseService.Initialize();
            }
        }

        private static DataPackageManifest ReadManifest(string workspace)
        {
            string manifestPath = Path.Combine(workspace, PackageManifestFileName);
            if (!File.Exists(manifestPath))
            {
                throw new FileNotFoundException("Package manifest not found.", manifestPath);
            }

            DataPackageManifest? manifest = JsonSerializer.Deserialize<DataPackageManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (manifest == null ||
                string.IsNullOrWhiteSpace(manifest.Format) ||
                string.IsNullOrWhiteSpace(manifest.DataFile))
            {
                throw new InvalidDataException("Package manifest is invalid.");
            }

            return manifest;
        }

        private static bool TryReadPackageManifest(string packagePath, out DataPackageManifest? manifest, out string message)
        {
            manifest = null;
            message = string.Empty;

            try
            {
                using ZipArchive archive = ZipFile.OpenRead(packagePath);

                ZipArchiveEntry? manifestEntry = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string fileName = Path.GetFileName(entry.FullName);
                    if (string.Equals(fileName, PackageManifestFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        manifestEntry = entry;
                        break;
                    }
                }

                if (manifestEntry == null)
                {
                    message = "Import package missing manifest.json.";
                    return false;
                }

                using var manifestStream = manifestEntry.Open();
                using var streamReader = new StreamReader(manifestStream);
                string manifestJson = streamReader.ReadToEnd();

                manifest = JsonSerializer.Deserialize<DataPackageManifest>(manifestJson, JsonOptions);
                if (manifest == null ||
                    string.IsNullOrWhiteSpace(manifest.Format) ||
                    string.IsNullOrWhiteSpace(manifest.DataFile))
                {
                    message = "Invalid import package manifest.";
                    return false;
                }

                return true;
            }
            catch (InvalidDataException)
            {
                message = "Invalid import package format.";
                return false;
            }
            catch (JsonException)
            {
                message = "Failed to parse import package manifest.";
                return false;
            }
            catch (Exception ex)
            {
                message = $"Failed to read import package: {ex.Message}";
                return false;
            }
        }

        private static void WriteManifest(string workspace, DataPackageManifest manifest)
        {
            string manifestPath = Path.Combine(workspace, PackageManifestFileName);
            string json = JsonSerializer.Serialize(manifest, JsonOptions);
            File.WriteAllText(manifestPath, json);
        }

        private static string RemapManagedImagePath(string? originalPath, string oldDataRoot, string newDataRoot)
        {
            if (string.IsNullOrWhiteSpace(originalPath) || string.IsNullOrWhiteSpace(oldDataRoot))
            {
                return originalPath ?? string.Empty;
            }

            try
            {
                string normalizedOriginal = Path.GetFullPath(originalPath);

                string oldClipboard = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(oldDataRoot, ClipboardImageFolderName)));
                if (normalizedOriginal.StartsWith(oldClipboard, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = normalizedOriginal.Substring(oldClipboard.Length);
                    return Path.Combine(newDataRoot, ClipboardImageFolderName, relative);
                }

                string oldFavorite = EnsureTrailingSeparator(Path.GetFullPath(Path.Combine(oldDataRoot, FavoriteImageFolderName)));
                if (normalizedOriginal.StartsWith(oldFavorite, StringComparison.OrdinalIgnoreCase))
                {
                    string relative = normalizedOriginal.Substring(oldFavorite.Length);
                    return Path.Combine(newDataRoot, FavoriteImageFolderName, relative);
                }
            }
            catch
            {
            }

            return originalPath ?? string.Empty;
        }

        private static void ValidateOutputZipPath(string outputZipPath)
        {
            if (string.IsNullOrWhiteSpace(outputZipPath))
            {
                throw new ArgumentException("Export file path cannot be empty.", nameof(outputZipPath));
            }

            string? directory = Path.GetDirectoryName(outputZipPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new DirectoryNotFoundException("Export target directory is invalid.");
            }

            Directory.CreateDirectory(directory);
        }

        private static string CreateTempWorkspace(string prefix)
        {
            string workspace = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(workspace);
            return workspace;
        }

        private static void CreateZipFromDirectory(
            string sourceDirectory,
            string zipFilePath,
            CompressionLevel compressionLevel = CompressionLevel.Optimal)
        {
            TryDeleteFile(zipFilePath);
            ZipFile.CreateFromDirectory(sourceDirectory, zipFilePath, compressionLevel, includeBaseDirectory: false);
        }

        private static void ClearManagedData(string dataRoot)
        {
            TryDeleteFile(Path.Combine(dataRoot, DatabaseFileName));
            TryDeleteFile(Path.Combine(dataRoot, $"{DatabaseFileName}-wal"));
            TryDeleteFile(Path.Combine(dataRoot, $"{DatabaseFileName}-shm"));

            string clipboardFolder = Path.Combine(dataRoot, ClipboardImageFolderName);
            string favoriteFolder = Path.Combine(dataRoot, FavoriteImageFolderName);

            if (Directory.Exists(clipboardFolder))
            {
                Directory.Delete(clipboardFolder, recursive: true);
            }

            if (Directory.Exists(favoriteFolder))
            {
                Directory.Delete(favoriteFolder, recursive: true);
            }
        }

        private static void CopyDirectoryIfExists(string sourceDirectory, string targetDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(sourceDirectory, file);
                string destination = Path.Combine(targetDirectory, relative);
                string? destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Copy(file, destination, overwrite: true);
            }
        }

        private static void CopyFileIfExists(string sourceFile, string targetFile)
        {
            if (!File.Exists(sourceFile))
            {
                return;
            }

            string? targetDirectory = Path.GetDirectoryName(targetFile);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourceFile, targetFile, overwrite: true);
        }

        private static void TryDeleteDirectory(string directoryPath)
        {
            try
            {
                if (Directory.Exists(directoryPath))
                {
                    Directory.Delete(directoryPath, recursive: true);
                }
            }
            catch
            {
            }
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

        private static void ExecuteNonQuery(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
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
    }
}
