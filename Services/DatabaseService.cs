using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Data.Sqlite;
using LiuYun.Models;

namespace LiuYun.Services
{
    public static class DatabaseService
    {
        private const int MaxClipboardHistoryItems = 300;
        private const int ClipboardPruneBatchSize = 60;
        private const int DefaultQueryLimit = 300;

        private static string? _dbPath;
        private static string? _appFolder;

        private static readonly SemaphoreSlim DbWriteLock = new SemaphoreSlim(1, 1);

        public static string DbPath
        {
            get
            {
                if (_dbPath == null)
                {
                    string appFolder = StoragePathService.GetCurrentDataRoot();

                    Directory.CreateDirectory(appFolder);
                    _dbPath = Path.Combine(appFolder, "data.db");
                    _appFolder = appFolder;
                }
                return _dbPath;
            }
        }

        public static string AppDataFolder
        {
            get
            {
                if (_appFolder == null)
                {
                    _appFolder ??= Path.GetDirectoryName(DbPath) ?? Directory.GetCurrentDirectory();
                }

                return _appFolder;
            }
        }

        public static string BuildConnectionString(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                throw new ArgumentException("Database path cannot be empty.", nameof(dbPath));
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            };
            return builder.ToString();
        }

        public static string ConnectionString => BuildConnectionString(DbPath);

        public static void Initialize()
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            InitializeSchema(connection);
        }

        public static void InitializeAtPath(string dbPath)
        {
            if (string.IsNullOrWhiteSpace(dbPath))
            {
                throw new ArgumentException("Database path cannot be empty.", nameof(dbPath));
            }

            string? directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var connection = new SqliteConnection(BuildConnectionString(dbPath));
            connection.Open();
            InitializeSchema(connection);
        }

        private static void InitializeSchema(SqliteConnection connection)
        {
            using (var walCmd = connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL;";
                walCmd.ExecuteNonQuery();
            }

            using (var timeoutCmd = connection.CreateCommand())
            {
                timeoutCmd.CommandText = "PRAGMA busy_timeout=5000;";
                timeoutCmd.ExecuteNonQuery();
            }

            using var createConfigTable = connection.CreateCommand();
            createConfigTable.CommandText = @"
                CREATE TABLE IF NOT EXISTS config (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                )";
            createConfigTable.ExecuteNonQuery();

            using var createClipboardHistoryTable = connection.CreateCommand();
            createClipboardHistoryTable.CommandText = @"
                CREATE TABLE IF NOT EXISTS ClipboardHistory (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ContentType INTEGER NOT NULL,
                    TextContent TEXT,
                    ImagePath TEXT,
                    Timestamp TEXT NOT NULL,
                    ContentHash TEXT
                )";
            createClipboardHistoryTable.ExecuteNonQuery();

            using var createFavoriteClipboardTable = connection.CreateCommand();
            createFavoriteClipboardTable.CommandText = @"
                CREATE TABLE IF NOT EXISTS FavoriteClipboard (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ContentType INTEGER NOT NULL,
                    TextContent TEXT,
                    ImagePath TEXT,
                    Timestamp TEXT NOT NULL,
                    ContentHash TEXT
                )";
            createFavoriteClipboardTable.ExecuteNonQuery();

            try
            {
                using var addIsPinnedColumn = connection.CreateCommand();
                addIsPinnedColumn.CommandText = "ALTER TABLE ClipboardHistory ADD COLUMN IsPinned INTEGER DEFAULT 0";
                addIsPinnedColumn.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }

            try
            {
                using var addContentHashColumn = connection.CreateCommand();
                addContentHashColumn.CommandText = "ALTER TABLE ClipboardHistory ADD COLUMN ContentHash TEXT";
                addContentHashColumn.ExecuteNonQuery();
            }
            catch (SqliteException)
            {
            }

            using var dropClipboardHashUniqueIndex = connection.CreateCommand();
            dropClipboardHashUniqueIndex.CommandText = "DROP INDEX IF EXISTS UX_ClipboardHistory_ContentHash";
            dropClipboardHashUniqueIndex.ExecuteNonQuery();

            using var createClipboardHashLookupIndex = connection.CreateCommand();
            createClipboardHashLookupIndex.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_ClipboardHistory_ContentHash
                ON ClipboardHistory(ContentType, ContentHash)";
            createClipboardHashLookupIndex.ExecuteNonQuery();

            using var createFavoriteTimestampIndex = connection.CreateCommand();
            createFavoriteTimestampIndex.CommandText = @"
                CREATE INDEX IF NOT EXISTS IX_FavoriteClipboard_Timestamp
                ON FavoriteClipboard(Timestamp DESC)";
            createFavoriteTimestampIndex.ExecuteNonQuery();
        }

        #region Clipboard CRUD

        public static int GetClipboardItemCount()
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM ClipboardHistory";

            var result = command.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : 0;
        }

        public static List<ClipboardItem> GetRecentClipboardItems(int limit = DefaultQueryLimit)
        {
            var items = new List<ClipboardItem>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, ContentType, TextContent, ImagePath, Timestamp, COALESCE(IsPinned, 0), COALESCE(ContentHash, '') FROM ClipboardHistory ORDER BY COALESCE(IsPinned, 0) DESC, Timestamp DESC LIMIT @limit";
            command.Parameters.AddWithValue("@limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new ClipboardItem
                {
                    Id = reader.GetInt32(0),
                    ContentType = (ClipboardContentType)reader.GetInt32(1),
                    TextContent = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ImagePath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Timestamp = DateTime.Parse(reader.GetString(4)),
                    IsPinned = reader.GetInt32(5) == 1,
                    ContentHash = reader.IsDBNull(6) ? string.Empty : reader.GetString(6)
                };
                items.Add(item);
            }
            return items;
        }

        public static HashSet<string> GetClipboardImagePaths()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ImagePath
                FROM ClipboardHistory
                WHERE ImagePath IS NOT NULL
                  AND TRIM(ImagePath) <> ''";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string value = reader.GetString(0);
                try
                {
                    paths.Add(Path.GetFullPath(value));
                }
                catch
                {
                    paths.Add(value);
                }
            }

            return paths;
        }

        public static void DeleteOldestClipboardItems(int count)
        {
            if (count <= 0) return;

            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                command.CommandText = @"
                    DELETE FROM ClipboardHistory
                    WHERE Id IN (
                        SELECT Id FROM ClipboardHistory
                        WHERE COALESCE(IsPinned, 0) = 0
                        ORDER BY Timestamp ASC
                        LIMIT @count
                    )";
                command.Parameters.AddWithValue("@count", count);

                int deleted = command.ExecuteNonQuery();
                Debug.WriteLine($"DatabaseService: Deleted {deleted} oldest clipboard items (excluding pinned).");
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static (long Id, bool Inserted) SaveClipboardItem(ClipboardItem item)
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using (var countCmd = connection.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(*) FROM ClipboardHistory WHERE COALESCE(IsPinned, 0) = 0";
                    var count = Convert.ToInt32(countCmd.ExecuteScalar());

                    if (count >= MaxClipboardHistoryItems)
                    {
                        using var deleteCmd = connection.CreateCommand();
                        deleteCmd.CommandText = @"
                            DELETE FROM ClipboardHistory
                            WHERE Id IN (
                                SELECT Id FROM ClipboardHistory
                                WHERE COALESCE(IsPinned, 0) = 0
                                ORDER BY Timestamp ASC
                                LIMIT @pruneCount
                            )";
                        deleteCmd.Parameters.AddWithValue("@pruneCount", ClipboardPruneBatchSize);
                        deleteCmd.ExecuteNonQuery();
                    }
                }

                if (string.IsNullOrWhiteSpace(item.ContentHash))
                {
                    return (0, false);
                }

                using (var latestCmd = connection.CreateCommand())
                {
                    latestCmd.CommandText = @"
                        SELECT Id, ContentType, COALESCE(ContentHash, '')
                        FROM ClipboardHistory
                        ORDER BY Timestamp DESC, Id DESC
                        LIMIT 1";

                    using var latestReader = latestCmd.ExecuteReader();
                    if (latestReader.Read())
                    {
                        long latestId = latestReader.GetInt64(0);
                        int latestContentType = latestReader.GetInt32(1);
                        string latestHash = latestReader.GetString(2);

                        if (latestContentType == (int)item.ContentType &&
                            string.Equals(latestHash, item.ContentHash, StringComparison.Ordinal))
                        {
                            return (latestId, false);
                        }
                    }
                }

                using (var insertCmd = connection.CreateCommand())
                {
                    insertCmd.CommandText = @"
                    INSERT INTO ClipboardHistory (ContentType, TextContent, ImagePath, Timestamp, IsPinned, ContentHash)
                    VALUES ($contentType, $textContent, $imagePath, $timestamp, $isPinned, $contentHash)";

                    insertCmd.Parameters.AddWithValue("$contentType", (int)item.ContentType);
                    insertCmd.Parameters.AddWithValue("$textContent", item.TextContent ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$imagePath", item.ImagePath ?? (object)DBNull.Value);
                    insertCmd.Parameters.AddWithValue("$timestamp", item.Timestamp.ToString("o"));
                    insertCmd.Parameters.AddWithValue("$isPinned", item.IsPinned ? 1 : 0);
                    insertCmd.Parameters.AddWithValue("$contentHash", item.ContentHash);

                    insertCmd.ExecuteNonQuery();

                    using var idCmd = connection.CreateCommand();
                    idCmd.CommandText = "SELECT last_insert_rowid()";
                    long insertedId = (long)idCmd.ExecuteScalar()!;
                    return (insertedId, true);
                }
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static void DeleteClipboardItem(int id)
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM ClipboardHistory WHERE Id = $id";
                command.Parameters.AddWithValue("$id", id);

                command.ExecuteNonQuery();
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static void ClearAllClipboardItems(bool keepPinned = true)
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                var command = connection.CreateCommand();
                if (keepPinned)
                {
                    command.CommandText = "DELETE FROM ClipboardHistory WHERE COALESCE(IsPinned, 0) = 0";
                }
                else
                {
                    command.CommandText = "DELETE FROM ClipboardHistory";
                }

                command.ExecuteNonQuery();
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static List<ClipboardItem> GetAllFavoriteClipboardItems(int limit = DefaultQueryLimit)
        {
            var items = new List<ClipboardItem>();
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, ContentType, TextContent, ImagePath, Timestamp, COALESCE(ContentHash, '')
                FROM FavoriteClipboard
                ORDER BY Timestamp DESC, Id DESC
                LIMIT @limit";
            command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var item = new ClipboardItem
                {
                    Id = reader.GetInt32(0),
                    ContentType = (ClipboardContentType)reader.GetInt32(1),
                    TextContent = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ImagePath = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Timestamp = DateTime.Parse(reader.GetString(4)),
                    ContentHash = reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    IsPinned = false
                };
                items.Add(item);
            }

            return items;
        }

        public static long InsertFavoriteClipboardItem(ClipboardItem item)
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO FavoriteClipboard (ContentType, TextContent, ImagePath, Timestamp, ContentHash)
                    VALUES ($contentType, $textContent, $imagePath, $timestamp, $contentHash);
                    SELECT last_insert_rowid();";

                command.Parameters.AddWithValue("$contentType", (int)item.ContentType);
                command.Parameters.AddWithValue("$textContent", item.TextContent ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$imagePath", item.ImagePath ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$timestamp", item.Timestamp.ToString("o"));
                command.Parameters.AddWithValue("$contentHash", item.ContentHash ?? string.Empty);

                return (long)command.ExecuteScalar()!;
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static void DeleteFavoriteClipboardItem(int id)
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM FavoriteClipboard WHERE Id = $id";
                command.Parameters.AddWithValue("$id", id);
                command.ExecuteNonQuery();
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        public static void ClearAllFavoriteClipboardItems()
        {
            DbWriteLock.Wait();
            try
            {
                using var connection = new SqliteConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM FavoriteClipboard";
                command.ExecuteNonQuery();
            }
            finally
            {
                DbWriteLock.Release();
            }
        }

        #endregion

        public static void RewriteManagedImagePaths(string dbPath, string oldDataRoot, string newDataRoot)
        {
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return;
            }

            string normalizedOldRoot = NormalizeRootPath(oldDataRoot);
            string normalizedNewRoot = NormalizeRootPath(newDataRoot);

            if (string.IsNullOrWhiteSpace(normalizedOldRoot) ||
                string.IsNullOrWhiteSpace(normalizedNewRoot) ||
                string.Equals(normalizedOldRoot, normalizedNewRoot, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            using var connection = new SqliteConnection(BuildConnectionString(dbPath));
            connection.Open();

            RewriteTableImagePaths(
                connection,
                tableName: "ClipboardHistory",
                oldFolder: Path.Combine(normalizedOldRoot, "clipboard_images"),
                newFolder: Path.Combine(normalizedNewRoot, "clipboard_images"));

            RewriteTableImagePaths(
                connection,
                tableName: "FavoriteClipboard",
                oldFolder: Path.Combine(normalizedOldRoot, "favorite_images"),
                newFolder: Path.Combine(normalizedNewRoot, "favorite_images"));
        }

        private static void RewriteTableImagePaths(SqliteConnection connection, string tableName, string oldFolder, string newFolder)
        {
            if (string.IsNullOrWhiteSpace(tableName) ||
                string.IsNullOrWhiteSpace(oldFolder) ||
                string.IsNullOrWhiteSpace(newFolder))
            {
                return;
            }

            var updates = new List<(long Id, string Path)>();

            using (var selectCommand = connection.CreateCommand())
            {
                selectCommand.CommandText = $@"
                    SELECT Id, ImagePath
                    FROM {tableName}
                    WHERE ImagePath IS NOT NULL
                      AND TRIM(ImagePath) <> ''";

                using var reader = selectCommand.ExecuteReader();
                while (reader.Read())
                {
                    long id = reader.GetInt64(0);
                    string path = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (TryRemapPath(path, oldFolder, newFolder, out string mapped))
                    {
                        updates.Add((id, mapped));
                    }
                }
            }

            if (updates.Count == 0)
            {
                return;
            }

            using var transaction = connection.BeginTransaction();
            try
            {
                using var updateCommand = connection.CreateCommand();
                updateCommand.CommandText = $"UPDATE {tableName} SET ImagePath = $imagePath WHERE Id = $id";
                var idParameter = updateCommand.Parameters.Add("$id", SqliteType.Integer);
                var pathParameter = updateCommand.Parameters.Add("$imagePath", SqliteType.Text);

                foreach (var update in updates)
                {
                    idParameter.Value = update.Id;
                    pathParameter.Value = update.Path;
                    updateCommand.ExecuteNonQuery();
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }

        private static bool TryRemapPath(string originalPath, string oldFolder, string newFolder, out string mappedPath)
        {
            mappedPath = string.Empty;

            if (string.IsNullOrWhiteSpace(originalPath))
            {
                return false;
            }

            try
            {
                string normalizedOriginal = Path.GetFullPath(originalPath);
                string normalizedOldFolder = EnsureTrailingSeparator(Path.GetFullPath(oldFolder));
                if (!normalizedOriginal.StartsWith(normalizedOldFolder, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string relative = normalizedOriginal.Substring(normalizedOldFolder.Length);
                mappedPath = Path.Combine(newFolder, relative);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeRootPath(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFullPath(rootPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return string.Empty;
            }
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
