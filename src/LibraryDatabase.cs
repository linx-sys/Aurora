/* ============================================================
 * LibraryDatabase.cs — 媒体库 SQLite 缓存
 * P1（评审建议 #5/#6）：解决"每次启动/换目录都要全量解析 ID3/FLAC 标签"
 * 的性能问题。扫描时按指纹（文件大小 + 最后写入时间 UTC）命中缓存直接
 * 复用元数据，未命中才重新解析并回写；目录中已消失的文件清理过期行。
 *
 * 只缓存"贵的"数据：标签文本、时长、标签内嵌封面。
 * lrc 文本与外部封面兜底（联网匹配缓存/同名 jpg）保持每次现读——
 * 它们会在缓存写入之后发生变化（联网匹配后到），缓存反而会吃掉更新。
 *
 * 依赖：Microsoft.Data.Sqlite（自动携带 e_sqlite3 原生库）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Aurora
{
    /// <summary>tracks 表行（缓存 DTO，与 Track 解耦以便测试）。</summary>
    public class TrackRow
    {
        public string Path;            // 主键
        public string FileName;
        public string Title;
        public string Artist;
        public string Album;
        public double DurationSeconds; // 0 = 未知
        public long Bytes;             // 指纹之一
        public long LastModified;      // 指纹之二：LastWriteTimeUtc.Ticks
        public byte[] Cover;           // 标签内嵌封面（可为 null）
    }

    public class LibraryDatabase
    {
        readonly string _dbPath;
        SqliteConnection _conn;
        readonly object _lock = new object();

        /// <summary>默认库路径：%APPDATA%\AuroraPlayer\library.db。</summary>
        public static string DefaultPath
        {
            get { return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AuroraPlayer", "library.db"); }
        }

        public LibraryDatabase(string dbPath)
        {
            _dbPath = dbPath ?? DefaultPath;
        }

        void EnsureOpen()
        {
            if (_conn != null) return;
            var dir = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _conn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            _conn.Open();

            using (var cmd = _conn.CreateCommand())
            {
                // WAL：后台扫描写 + UI 读不互相阻塞
                cmd.CommandText = "PRAGMA journal_mode=WAL;";
                cmd.ExecuteNonQuery();
            }
            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS tracks (
    path          TEXT PRIMARY KEY,
    file_name     TEXT NOT NULL,
    title         TEXT,
    artist        TEXT,
    album         TEXT,
    duration      REAL NOT NULL DEFAULT 0,
    bytes         INTEGER NOT NULL DEFAULT 0,
    last_modified INTEGER NOT NULL DEFAULT 0,
    cover         BLOB
);
CREATE INDEX IF NOT EXISTS idx_tracks_prefix ON tracks(path);";
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>指纹命中判定：大小与最后写入时间均未变化。</summary>
        public static bool FingerprintMatches(TrackRow row, long bytes, long lastModifiedUtcTicks)
        {
            return row != null && row.Bytes == bytes && row.LastModified == lastModifiedUtcTicks;
        }

        /// <summary>按路径取缓存行；无缓存返回 null。</summary>
        public TrackRow TryGet(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                EnsureOpen();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT path,file_name,title,artist,album,duration,bytes,last_modified,cover FROM tracks WHERE path=$p";
                    cmd.Parameters.AddWithValue("$p", path);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        return ReadRow(r);
                    }
                }
            }
        }

        /// <summary>取目录前缀下的所有缓存行（增量扫描清理过期行用）。</summary>
        public List<TrackRow> GetByPrefix(string dirPrefix)
        {
            var result = new List<TrackRow>();
            if (string.IsNullOrEmpty(dirPrefix)) return result;
            lock (_lock)
            {
                EnsureOpen();
                using (var cmd = _conn.CreateCommand())
                {
                    // 前缀匹配在 C# 侧做（OrdinalIgnoreCase），SQL 只做范围过滤走索引
                    cmd.CommandText = "SELECT path,file_name,title,artist,album,duration,bytes,last_modified,cover FROM tracks WHERE path >= $lo AND path < $hi";
                    cmd.Parameters.AddWithValue("$lo", dirPrefix);
                    cmd.Parameters.AddWithValue("$hi", dirPrefix + "\uffff");
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read()) result.Add(ReadRow(r));
                    }
                }
            }
            return result;
        }

        /// <summary>写入或更新一行（按路径主键）。</summary>
        public void Upsert(TrackRow row)
        {
            if (row == null || string.IsNullOrEmpty(row.Path)) return;
            lock (_lock)
            {
                EnsureOpen();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = @"
INSERT INTO tracks(path,file_name,title,artist,album,duration,bytes,last_modified,cover)
VALUES($path,$file_name,$title,$artist,$album,$duration,$bytes,$last_modified,$cover)
ON CONFLICT(path) DO UPDATE SET
    file_name=$file_name, title=$title, artist=$artist, album=$album,
    duration=$duration, bytes=$bytes, last_modified=$last_modified, cover=$cover";
                    Bind(cmd, row);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        /// <summary>批量写入（单事务；整目录扫描后一次性回写）。</summary>
        public void UpsertMany(IEnumerable<TrackRow> rows)
        {
            if (rows == null) return;
            lock (_lock)
            {
                EnsureOpen();
                using (var tx = _conn.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO tracks(path,file_name,title,artist,album,duration,bytes,last_modified,cover)
VALUES($path,$file_name,$title,$artist,$album,$duration,$bytes,$last_modified,$cover)
ON CONFLICT(path) DO UPDATE SET
    file_name=$file_name, title=$title, artist=$artist, album=$album,
    duration=$duration, bytes=$bytes, last_modified=$last_modified, cover=$cover";
                    var pPath = cmd.CreateParameter(); pPath.ParameterName = "$path"; cmd.Parameters.Add(pPath);
                    var pName = cmd.CreateParameter(); pName.ParameterName = "$file_name"; cmd.Parameters.Add(pName);
                    var pTitle = cmd.CreateParameter(); pTitle.ParameterName = "$title"; cmd.Parameters.Add(pTitle);
                    var pArtist = cmd.CreateParameter(); pArtist.ParameterName = "$artist"; cmd.Parameters.Add(pArtist);
                    var pAlbum = cmd.CreateParameter(); pAlbum.ParameterName = "$album"; cmd.Parameters.Add(pAlbum);
                    var pDur = cmd.CreateParameter(); pDur.ParameterName = "$duration"; cmd.Parameters.Add(pDur);
                    var pBytes = cmd.CreateParameter(); pBytes.ParameterName = "$bytes"; cmd.Parameters.Add(pBytes);
                    var pMtime = cmd.CreateParameter(); pMtime.ParameterName = "$last_modified"; cmd.Parameters.Add(pMtime);
                    var pCover = cmd.CreateParameter(); pCover.ParameterName = "$cover"; cmd.Parameters.Add(pCover);

                    foreach (var row in rows)
                    {
                        if (row == null || string.IsNullOrEmpty(row.Path)) continue;
                        pPath.Value = row.Path;
                        pName.Value = row.FileName ?? "";
                        pTitle.Value = (object)row.Title ?? DBNull.Value;
                        pArtist.Value = (object)row.Artist ?? DBNull.Value;
                        pAlbum.Value = (object)row.Album ?? DBNull.Value;
                        pDur.Value = row.DurationSeconds;
                        pBytes.Value = row.Bytes;
                        pMtime.Value = row.LastModified;
                        pCover.Value = (object)row.Cover ?? DBNull.Value;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        /// <summary>删除指定路径集合中的行。</summary>
        public void Delete(IEnumerable<string> paths)
        {
            if (paths == null) return;
            lock (_lock)
            {
                EnsureOpen();
                using (var tx = _conn.BeginTransaction())
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM tracks WHERE path=$p";
                    var p = cmd.CreateParameter(); p.ParameterName = "$p"; cmd.Parameters.Add(p);
                    foreach (string path in paths)
                    {
                        p.Value = path;
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
            }
        }

        /// <summary>
        /// 增量扫描清理：删除 dirPrefix 目录下不在 present 集合中的过期行。
        /// 前缀按"目录边界"匹配（补全分隔符），避免 "D:\Music" 误伤 "D:\MusicBackup"。
        /// 返回删除的行数。
        /// </summary>
        public int DeleteMissingUnder(string dirPrefix, HashSet<string> presentPaths)
        {
            if (string.IsNullOrEmpty(dirPrefix)) return 0;
            string prefix = dirPrefix;
            if (!prefix.EndsWith("\\")) prefix += "\\";
            var stale = new List<string>();
            foreach (var row in GetByPrefix(dirPrefix))
            {
                if (!row.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (presentPaths == null || !presentPaths.Contains(row.Path))
                    stale.Add(row.Path);
            }
            if (stale.Count > 0) Delete(stale);
            return stale.Count;
        }

        static TrackRow ReadRow(SqliteDataReader r)
        {
            return new TrackRow
            {
                Path = r.GetString(0),
                FileName = r.IsDBNull(1) ? "" : r.GetString(1),
                Title = r.IsDBNull(2) ? null : r.GetString(2),
                Artist = r.IsDBNull(3) ? null : r.GetString(3),
                Album = r.IsDBNull(4) ? null : r.GetString(4),
                DurationSeconds = r.IsDBNull(5) ? 0 : r.GetDouble(5),
                Bytes = r.IsDBNull(6) ? 0 : r.GetInt64(6),
                LastModified = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                Cover = r.IsDBNull(8) ? null : (byte[])r.GetValue(8),
            };
        }

        static void Bind(SqliteCommand cmd, TrackRow row)
        {
            cmd.Parameters.AddWithValue("$path", row.Path);
            cmd.Parameters.AddWithValue("$file_name", row.FileName ?? "");
            cmd.Parameters.AddWithValue("$title", (object)row.Title ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$artist", (object)row.Artist ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$album", (object)row.Album ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", row.DurationSeconds);
            cmd.Parameters.AddWithValue("$bytes", row.Bytes);
            cmd.Parameters.AddWithValue("$last_modified", row.LastModified);
            cmd.Parameters.AddWithValue("$cover", (object)row.Cover ?? DBNull.Value);
        }
    }
}
