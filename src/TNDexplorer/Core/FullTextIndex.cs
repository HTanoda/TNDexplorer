using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.IO;

namespace TNDexplorer.Core
{
    /// <summary>
    /// Search result from full-text index.
    /// </summary>
    public class FullTextSearchResult
    {
        public string FilePath { get; set; }
        public string Snippet { get; set; }
        public double Rank { get; set; }
    }

    /// <summary>
    /// SQLite FTS4-based full-text search index.
    /// Stores extracted text content for file content search.
    /// Uses WAL mode for concurrent read/write access.
    /// Thread-safe: uses separate connections for read and write.
    /// Uses unicode61 tokenizer for word-based search including Japanese.
    /// </summary>
    public class FullTextIndex : IDisposable
    {
        private readonly string _dbPath;
        private SQLiteConnection _writeConnection;
        private readonly object _writeLock = new object();
        private bool _disposed;

        /// <summary>
        /// Last initialization error message, if any.
        /// </summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Default database path: %LOCALAPPDATA%\TNDexplorer\fulltext.db
        /// </summary>
        public static string DefaultDbPath
        {
            get
            {
                string appDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TNDexplorer");
                return Path.Combine(appDataDir, "fulltext.db");
            }
        }

        public string DatabasePath => _dbPath;

        public FullTextIndex() : this(DefaultDbPath) { }

        public FullTextIndex(string dbPath)
        {
            _dbPath = dbPath;
            EnsureDirectory();
            InitializeDatabase();
        }

        private void EnsureDirectory()
        {
            string dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private SQLiteConnection CreateConnection(bool readOnly = false)
        {
            var builder = new SQLiteConnectionStringBuilder
            {
                DataSource = _dbPath,
                JournalMode = SQLiteJournalModeEnum.Wal,
                SyncMode = SynchronizationModes.Normal,
                CacheSize = -2000, // 2MB cache
                ReadOnly = readOnly,
            };
            var conn = new SQLiteConnection(builder.ToString());
            conn.Open();

            // Set busy timeout for concurrent access
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA busy_timeout = 5000;";
                cmd.ExecuteNonQuery();
            }

            return conn;
        }

        /// <summary>
        /// Detect whether FTS4 is available in this SQLite build.
        /// </summary>
        private bool IsFts4Available(SQLiteConnection conn)
        {
            try
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE VIRTUAL TABLE IF NOT EXISTS _fts_test_basic
                        USING fts4(test_col, tokenize=unicode61);
                    ";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DROP TABLE IF EXISTS _fts_test_basic;";
                    cmd.ExecuteNonQuery();
                }

                return true;
            }
            catch
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "DROP TABLE IF EXISTS _fts_test_basic;";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch { }
                return false;
            }
        }

        private void InitializeDatabase()
        {
            _writeConnection = CreateConnection();

            // Check if fts_content table already exists (from previous run)
            bool tableExists = false;
            bool needsRecreate = false;
            using (var cmd = _writeConnection.CreateCommand())
            {
                cmd.CommandText = "SELECT sql FROM sqlite_master WHERE name='fts_content';";
                var result = cmd.ExecuteScalar();
                if (result != null)
                {
                    tableExists = true;
                    string sql = result.ToString();
                    // If existing table was created with FTS5, we need to drop and recreate as FTS4
                    if (sql.IndexOf("fts5", StringComparison.OrdinalIgnoreCase) >= 0)
                        needsRecreate = true;
                }
            }

            if (needsRecreate)
            {
                // Drop old FTS5 table and related tables that can't be used
                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.CommandText = "DROP TABLE IF EXISTS fts_content;";
                    cmd.ExecuteNonQuery();
                }
                // Also drop indexed_files to force full re-index
                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.CommandText = "DROP TABLE IF EXISTS indexed_files;";
                    cmd.ExecuteNonQuery();
                }
                tableExists = false;
                System.Diagnostics.Debug.WriteLine("Dropped old FTS5 tables, will recreate as FTS4");
            }

            if (!tableExists)
            {
                // Verify FTS4 is available
                if (!IsFts4Available(_writeConnection))
                {
                    LastError = "SQLite FTS4 is not available in this build.";
                    throw new InvalidOperationException(LastError);
                }

                System.Diagnostics.Debug.WriteLine("FTS4 tokenizer: unicode61");

                // Create FTS4 virtual table with unicode61 tokenizer
                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.CommandText = @"
                        CREATE VIRTUAL TABLE IF NOT EXISTS fts_content
                        USING fts4(file_path, content, tokenize=unicode61);
                    ";
                    cmd.ExecuteNonQuery();
                }
            }

            using (var cmd = _writeConnection.CreateCommand())
            {
                // Metadata table for tracking indexed files
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS indexed_files (
                        file_path TEXT PRIMARY KEY,
                        file_size INTEGER,
                        modified_date TEXT,
                        index_date TEXT
                    );
                ";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// Get index statistics without creating/initializing a full FullTextIndex.
        /// Use this for status display in settings dialog to avoid triggering DB creation.
        /// Returns (fileCount, dbSizeBytes) or null if DB doesn't exist.
        /// </summary>
        public static Tuple<int, long> GetIndexStatistics()
        {
            try
            {
                string dbPath = DefaultDbPath;
                if (!File.Exists(dbPath))
                    return null;

                // Just read the metadata table - don't create FTS tables
                var builder = new SQLiteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    ReadOnly = true,
                };

                int count = 0;
                using (var conn = new SQLiteConnection(builder.ToString()))
                {
                    conn.Open();
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "PRAGMA busy_timeout = 3000;";
                        cmd.ExecuteNonQuery();
                    }

                    // Check if indexed_files table exists
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='indexed_files';";
                        var result = cmd.ExecuteScalar();
                        if (result == null)
                            return new Tuple<int, long>(0, 0);
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "SELECT COUNT(*) FROM indexed_files;";
                        var result = cmd.ExecuteScalar();
                        count = Convert.ToInt32(result);
                    }
                }

                // Calculate total size
                long totalSize = 0;
                if (File.Exists(dbPath))
                    totalSize += new FileInfo(dbPath).Length;
                string walPath = dbPath + "-wal";
                if (File.Exists(walPath))
                    totalSize += new FileInfo(walPath).Length;
                string shmPath = dbPath + "-shm";
                if (File.Exists(shmPath))
                    totalSize += new FileInfo(shmPath).Length;

                return new Tuple<int, long>(count, totalSize);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Insert or update a file's content in the index.
        /// </summary>
        public void UpsertFile(string filePath, string content, long fileSize, DateTime modifiedDate)
        {
            if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(content))
                return;

            lock (_writeLock)
            {
                if (_disposed) return;

                using (var transaction = _writeConnection.BeginTransaction())
                {
                    try
                    {
                        // Remove existing FTS entry if any
                        using (var cmd = _writeConnection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "DELETE FROM fts_content WHERE file_path = @path;";
                            cmd.Parameters.AddWithValue("@path", filePath);
                            cmd.ExecuteNonQuery();
                        }

                        // Insert new FTS entry
                        using (var cmd = _writeConnection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = "INSERT INTO fts_content(file_path, content) VALUES(@path, @content);";
                            cmd.Parameters.AddWithValue("@path", filePath);
                            cmd.Parameters.AddWithValue("@content", content);
                            cmd.ExecuteNonQuery();
                        }

                        // Upsert metadata
                        using (var cmd = _writeConnection.CreateCommand())
                        {
                            cmd.Transaction = transaction;
                            cmd.CommandText = @"
                                INSERT OR REPLACE INTO indexed_files(file_path, file_size, modified_date, index_date)
                                VALUES(@path, @size, @modified, @indexDate);
                            ";
                            cmd.Parameters.AddWithValue("@path", filePath);
                            cmd.Parameters.AddWithValue("@size", fileSize);
                            cmd.Parameters.AddWithValue("@modified", modifiedDate.ToString("o"));
                            cmd.Parameters.AddWithValue("@indexDate", DateTime.Now.ToString("o"));
                            cmd.ExecuteNonQuery();
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Batch insert/update files within a single transaction.
        /// More efficient than calling UpsertFile individually.
        /// </summary>
        public void UpsertFileBatch(IEnumerable<(string filePath, string content, long fileSize, DateTime modifiedDate)> items)
        {
            lock (_writeLock)
            {
                if (_disposed) return;

                using (var transaction = _writeConnection.BeginTransaction())
                {
                    try
                    {
                        foreach (var item in items)
                        {
                            if (string.IsNullOrEmpty(item.filePath) || string.IsNullOrEmpty(item.content))
                                continue;

                            using (var cmd = _writeConnection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "DELETE FROM fts_content WHERE file_path = @path;";
                                cmd.Parameters.AddWithValue("@path", item.filePath);
                                cmd.ExecuteNonQuery();
                            }

                            using (var cmd = _writeConnection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = "INSERT INTO fts_content(file_path, content) VALUES(@path, @content);";
                                cmd.Parameters.AddWithValue("@path", item.filePath);
                                cmd.Parameters.AddWithValue("@content", item.content);
                                cmd.ExecuteNonQuery();
                            }

                            using (var cmd = _writeConnection.CreateCommand())
                            {
                                cmd.Transaction = transaction;
                                cmd.CommandText = @"
                                    INSERT OR REPLACE INTO indexed_files(file_path, file_size, modified_date, index_date)
                                    VALUES(@path, @size, @modified, @indexDate);
                                ";
                                cmd.Parameters.AddWithValue("@path", item.filePath);
                                cmd.Parameters.AddWithValue("@size", item.fileSize);
                                cmd.Parameters.AddWithValue("@modified", item.modifiedDate.ToString("o"));
                                cmd.Parameters.AddWithValue("@indexDate", DateTime.Now.ToString("o"));
                                cmd.ExecuteNonQuery();
                            }
                        }

                        transaction.Commit();
                    }
                    catch
                    {
                        transaction.Rollback();
                        throw;
                    }
                }
            }
        }

        /// <summary>
        /// Remove a file from the index.
        /// </summary>
        public void RemoveFile(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            lock (_writeLock)
            {
                if (_disposed) return;

                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM fts_content WHERE file_path = @path;";
                    cmd.Parameters.AddWithValue("@path", filePath);
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = _writeConnection.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM indexed_files WHERE file_path = @path;";
                    cmd.Parameters.AddWithValue("@path", filePath);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>
        /// Check if a file needs re-indexing (based on modified date comparison).
        /// Returns true if the file is not indexed or has been modified since last index.
        /// </summary>
        public bool NeedsReindex(string filePath, DateTime currentModifiedDate)
        {
            if (string.IsNullOrEmpty(filePath)) return true;

            try
            {
                using (var conn = CreateConnection(readOnly: true))
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT modified_date FROM indexed_files WHERE file_path = @path;";
                    cmd.Parameters.AddWithValue("@path", filePath);

                    var result = cmd.ExecuteScalar();
                    if (result == null || result == DBNull.Value)
                        return true;

                    if (DateTime.TryParse(result.ToString(), out DateTime indexedDate))
                    {
                        // Re-index if file has been modified since last indexing
                        return currentModifiedDate > indexedDate.AddSeconds(1);
                    }

                    return true;
                }
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Minimum query length for FTS4 unicode61 tokenizer.
        /// </summary>
        public int MinQueryLength => 1;

        /// <summary>
        /// Search for files containing the query text.
        /// Returns matching files with snippets.
        /// Uses FTS4 MATCH with unicode61 tokenizer (word-based search).
        /// </summary>
        public List<FullTextSearchResult> Search(string query, int maxResults = 1000)
        {
            var results = new List<FullTextSearchResult>();

            if (string.IsNullOrWhiteSpace(query) || query.Length < MinQueryLength)
                return results;

            try
            {
                using (var conn = CreateConnection(readOnly: true))
                using (var cmd = conn.CreateCommand())
                {
                    // FTS4 search with snippet
                    // snippet(table, startMatch, endMatch, ellipsis, columnIndex, approximateTokens)
                    cmd.CommandText = @"
                        SELECT
                            file_path,
                            snippet(fts_content, '<<', '>>', '...', 1, 40) as snippet
                        FROM fts_content
                        WHERE fts_content MATCH @query
                        LIMIT @limit;
                    ";
                    cmd.Parameters.AddWithValue("@query", EscapeFtsQuery(query));
                    cmd.Parameters.AddWithValue("@limit", maxResults);

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            results.Add(new FullTextSearchResult
                            {
                                FilePath = reader.GetString(0),
                                Snippet = reader.IsDBNull(1) ? "" : reader.GetString(1),
                                Rank = 0,
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"FTS search error: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Escape special characters for FTS4 query.
        /// Wraps the query in double quotes for phrase search.
        /// </summary>
        private static string EscapeFtsQuery(string query)
        {
            // Escape any existing double quotes in the query
            string escaped = query.Replace("\"", "\"\"");
            return "\"" + escaped + "\"";
        }

        /// <summary>
        /// Get the number of indexed files.
        /// </summary>
        public int GetIndexedFileCount()
        {
            try
            {
                using (var conn = CreateConnection(readOnly: true))
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM indexed_files;";
                    var result = cmd.ExecuteScalar();
                    return Convert.ToInt32(result);
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Get the database file size in bytes.
        /// </summary>
        public long GetDatabaseSize()
        {
            try
            {
                if (File.Exists(_dbPath))
                {
                    var fi = new FileInfo(_dbPath);
                    long total = fi.Length;

                    // Also account for WAL and SHM files
                    string walPath = _dbPath + "-wal";
                    string shmPath = _dbPath + "-shm";
                    if (File.Exists(walPath))
                        total += new FileInfo(walPath).Length;
                    if (File.Exists(shmPath))
                        total += new FileInfo(shmPath).Length;

                    return total;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>
        /// Delete the entire full-text index database.
        /// </summary>
        public void DeleteDatabase()
        {
            lock (_writeLock)
            {
                CloseWriteConnection();

                try
                {
                    if (File.Exists(_dbPath))
                        File.Delete(_dbPath);

                    string walPath = _dbPath + "-wal";
                    string shmPath = _dbPath + "-shm";
                    if (File.Exists(walPath))
                        File.Delete(walPath);
                    if (File.Exists(shmPath))
                        File.Delete(shmPath);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to delete FTS database: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Rebuild the index by deleting and recreating the database.
        /// </summary>
        public void Rebuild()
        {
            DeleteDatabase();
            EnsureDirectory();
            InitializeDatabase();
        }

        /// <summary>
        /// Optimize the FTS4 index (merge segments).
        /// Should be called after a large batch of inserts.
        /// </summary>
        public void Optimize()
        {
            lock (_writeLock)
            {
                if (_disposed) return;

                try
                {
                    using (var cmd = _writeConnection.CreateCommand())
                    {
                        // FTS4 optimize command
                        cmd.CommandText = "INSERT INTO fts_content(fts_content) VALUES('optimize');";
                        cmd.ExecuteNonQuery();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"FTS optimize error: {ex.Message}");
                }
            }
        }

        private void CloseWriteConnection()
        {
            if (_writeConnection != null)
            {
                try
                {
                    _writeConnection.Close();
                    _writeConnection.Dispose();
                }
                catch { }
                _writeConnection = null;
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                lock (_writeLock)
                {
                    CloseWriteConnection();
                }
            }
        }
    }
}
