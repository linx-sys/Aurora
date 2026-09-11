using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Aurora
{
    /// <summary>Windows 路径身份；不要求文件存在，非法/测试伪路径保留为文本。</summary>
    public static class LibraryPath
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string value = path.Replace('/', '\\');
            try { value = Path.GetFullPath(value); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
            catch (PathTooLongException) { }
            return value.TrimEnd('\\');
        }

        public static string Key(string path) => Normalize(path).ToUpperInvariant();
        public static bool Same(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        public static bool IsUnder(string path, string directory) => Normalize(path).StartsWith(Normalize(directory) + "\\", StringComparison.OrdinalIgnoreCase);
    }

    public class TrackRow
    {
        public string Path = null!;
        public string FileName = null!;
        public string? Title;
        public string? Artist;
        public string? Album;
        public double DurationSeconds;
        public long Bytes;
        public long LastModified;
        public byte[]? Cover;
        public double? TrackGain;
        public double? TrackPeak;
    }

    /// <summary>单连接、锁内串行访问；WAL 提供崩溃恢复及跨连接读写能力，不使本实例并行。</summary>
    public class LibraryDatabase : ILibraryStore, IDisposable
    {
        readonly string _dbPath;
        SqliteConnection? _conn;
        readonly object _lock = new object();
        bool _disposed;
        const string Columns = "path,file_name,title,artist,album,duration,bytes,last_modified,cover,track_gain,track_peak";

        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AuroraPlayer", "library.db");

        public LibraryDatabase(string dbPath) { _dbPath = dbPath ?? DefaultPath; }

        void EnsureOpen()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(LibraryDatabase));
            if (_conn != null) return;
            string? dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString());
            try
            {
                connection.Open();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode=WAL;";
                    cmd.ExecuteNonQuery();
                }
                using (var tx = connection.BeginTransaction())
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"CREATE TABLE IF NOT EXISTS tracks (
path TEXT PRIMARY KEY, file_name TEXT NOT NULL, title TEXT, artist TEXT, album TEXT,
duration REAL NOT NULL DEFAULT 0, bytes INTEGER NOT NULL DEFAULT 0,
last_modified INTEGER NOT NULL DEFAULT 0, cover BLOB);";
                    cmd.ExecuteNonQuery();
                    var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    cmd.CommandText = "PRAGMA table_info(tracks);";
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) columns.Add(reader.GetString(1));
                    foreach (string column in new[] { "track_gain", "track_peak", "path_key" })
                    {
                        if (columns.Contains(column)) continue;
                        cmd.CommandText = "ALTER TABLE tracks ADD COLUMN " + column + (column == "path_key" ? " TEXT;" : " REAL;");
                        cmd.ExecuteNonQuery();
                    }
                    // 不合并或删除旧库中仅大小写不同的行，保留全部原始元数据。
                    var pending = new List<string>();
                    cmd.CommandText = "SELECT path FROM tracks WHERE path_key IS NULL;";
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read()) pending.Add(reader.GetString(0));
                    foreach (string path in pending)
                    {
                        cmd.Parameters.Clear();
                        cmd.CommandText = "UPDATE tracks SET path_key=$key WHERE path=$path;";
                        cmd.Parameters.AddWithValue("$key", LibraryPath.Key(path));
                        cmd.Parameters.AddWithValue("$path", path);
                        cmd.ExecuteNonQuery();
                    }
                    cmd.Parameters.Clear();
                    cmd.CommandText = "CREATE INDEX IF NOT EXISTS idx_tracks_path_key ON tracks(path_key);";
                    cmd.ExecuteNonQuery();
                    cmd.CommandText = "PRAGMA user_version;";
                    if (Convert.ToInt64(cmd.ExecuteScalar()) < 2)
                    {
                        cmd.CommandText = "PRAGMA user_version=2;";
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                _conn = connection;
            }
            catch { connection.Dispose(); throw; }
        }

        public static bool FingerprintMatches(TrackRow? row, long bytes, long lastModifiedUtcTicks)
            => row != null && row.Bytes == bytes && row.LastModified == lastModifiedUtcTicks;

        public TrackRow? TryGet(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                EnsureOpen();
                using (var cmd = _conn!.CreateCommand())
                {
                    cmd.CommandText = "SELECT " + Columns + " FROM tracks WHERE path_key=$key ORDER BY last_modified DESC, path;";
                    cmd.Parameters.AddWithValue("$key", LibraryPath.Key(path));
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            var row = ReadRow(reader);
                            if (LibraryPath.Same(row.Path, path)) return row;
                        }
                }
                return null;
            }
        }

        public List<TrackRow> GetByPrefix(string dirPrefix)
        {
            var result = new List<TrackRow>();
            if (string.IsNullOrEmpty(dirPrefix)) return result;
            lock (_lock)
            {
                EnsureOpen();
                using (var cmd = _conn!.CreateCommand())
                {
                    string prefix = LibraryPath.Key(dirPrefix) + "\\";
                    // '\\' 的下一个 ASCII 字符是 ']'；此上界包含任意 Unicode 后缀。
                    cmd.CommandText = "SELECT " + Columns + " FROM tracks WHERE path_key >= $lo AND path_key < $hi ORDER BY last_modified DESC, path;";
                    cmd.Parameters.AddWithValue("$lo", prefix);
                    cmd.Parameters.AddWithValue("$hi", prefix.Substring(0, prefix.Length - 1) + "]");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                        {
                            var row = ReadRow(reader);
                            if (LibraryPath.IsUnder(row.Path, dirPrefix) && seen.Add(LibraryPath.Normalize(row.Path))) result.Add(row);
                        }
                }
            }
            return result;
        }

        public void Upsert(TrackRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.Path)) return;
            UpsertMany(new[] { row });
        }

        public void UpsertMany(IEnumerable<TrackRow> rows)
        {
            if (rows == null) return;
            lock (_lock)
            {
                EnsureOpen();
                using (var tx = _conn!.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    foreach (var row in rows)
                    {
                        if (row == null || string.IsNullOrEmpty(row.Path)) continue;
                        cmd.Parameters.Clear();
                        Bind(cmd, row);
                        cmd.CommandText = @"UPDATE tracks SET file_name=$file_name,title=$title,artist=$artist,album=$album,
duration=$duration,bytes=$bytes,last_modified=$last_modified,cover=$cover,track_gain=$track_gain,track_peak=$track_peak
WHERE path_key=$key;";
                        if (cmd.ExecuteNonQuery() != 0) continue;
                        cmd.CommandText = @"INSERT INTO tracks(path,path_key,file_name,title,artist,album,duration,bytes,last_modified,cover,track_gain,track_peak)
VALUES($path,$key,$file_name,$title,$artist,$album,$duration,$bytes,$last_modified,$cover,$track_gain,$track_peak);";
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        public void Delete(IEnumerable<string> paths)
        {
            if (paths == null) return;
            lock (_lock)
            {
                EnsureOpen();
                using (var tx = _conn!.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM tracks WHERE path_key=$key;";
                    var parameter = cmd.Parameters.AddWithValue("$key", "");
                    foreach (string path in paths)
                    {
                        if (string.IsNullOrEmpty(path)) continue;
                        parameter.Value = LibraryPath.Key(path);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        public int DeleteMissingUnder(string dirPrefix, HashSet<string> presentPaths)
        {
            if (string.IsNullOrEmpty(dirPrefix)) return 0;
            lock (_lock)
            {
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (presentPaths != null)
                    foreach (string path in presentPaths) present.Add(LibraryPath.Normalize(path));
                var stale = new List<string>();
                foreach (var row in GetByPrefix(dirPrefix))
                    if (!present.Contains(LibraryPath.Normalize(row.Path))) stale.Add(row.Path);
                if (stale.Count > 0) Delete(stale);
                return stale.Count;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                _conn?.Dispose();
                _conn = null;
            }
        }

        static TrackRow ReadRow(SqliteDataReader r) => new TrackRow
        {
            Path = r.GetString(0), FileName = r.IsDBNull(1) ? "" : r.GetString(1),
            Title = r.IsDBNull(2) ? null : r.GetString(2), Artist = r.IsDBNull(3) ? null : r.GetString(3),
            Album = r.IsDBNull(4) ? null : r.GetString(4), DurationSeconds = r.IsDBNull(5) ? 0 : r.GetDouble(5),
            Bytes = r.IsDBNull(6) ? 0 : r.GetInt64(6), LastModified = r.IsDBNull(7) ? 0 : r.GetInt64(7),
            Cover = r.IsDBNull(8) ? null : (byte[])r.GetValue(8),
            TrackGain = r.IsDBNull(9) ? (double?)null : r.GetDouble(9), TrackPeak = r.IsDBNull(10) ? (double?)null : r.GetDouble(10)
        };

        static void Bind(SqliteCommand cmd, TrackRow row)
        {
            cmd.Parameters.AddWithValue("$path", row.Path);
            cmd.Parameters.AddWithValue("$key", LibraryPath.Key(row.Path));
            cmd.Parameters.AddWithValue("$file_name", row.FileName ?? "");
            cmd.Parameters.AddWithValue("$title", (object?)row.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist", (object?)row.Artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$album", (object?)row.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", row.DurationSeconds);
            cmd.Parameters.AddWithValue("$bytes", row.Bytes);
            cmd.Parameters.AddWithValue("$last_modified", row.LastModified);
            cmd.Parameters.AddWithValue("$cover", (object?)row.Cover ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_gain", (object?)row.TrackGain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$track_peak", (object?)row.TrackPeak ?? DBNull.Value);
        }
    }
}
