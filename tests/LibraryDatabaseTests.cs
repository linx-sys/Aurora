/* ============================================================
 * LibraryDatabaseTests.cs — SQLite 媒体库缓存与增量扫描单元测试
 * 覆盖：CRUD 往返、冲突更新、批量事务、前缀过滤、过期行清理、
 *       BuildTracksIncremental 的指纹复用/失效重解析/清理语义。
 * 使用临时 db 文件与临时音频目录，测试后清理。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    public class LibraryDatabaseTests : IDisposable
    {
        readonly string dbPath = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N") + ".db");
        LibraryDatabase db;

        LibraryDatabase Db
        {
            get { return db ?? (db = new LibraryDatabase(dbPath)); }
        }

        static TrackRow Row(string path, string title = "T", long bytes = 100, long mtime = 12345, byte[] cover = null)
        {
            return new TrackRow
            {
                Path = path, FileName = System.IO.Path.GetFileName(path),
                Title = title, Artist = "A", Album = "AL",
                DurationSeconds = 12.5, Bytes = bytes, LastModified = mtime, Cover = cover,
            };
        }

        public void Dispose()
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        [Fact]
        public void Upsert_TryGet_Roundtrip()
        {
            var cover = new byte[] { 1, 2, 3, 4 };
            Db.Upsert(Row("D:\\M\\a.mp3", "晴天", cover: cover));

            var got = Db.TryGet("D:\\M\\a.mp3");
            Assert.NotNull(got);
            Assert.Equal("晴天", got.Title);
            Assert.Equal("A", got.Artist);
            Assert.Equal("AL", got.Album);
            Assert.Equal(12.5, got.DurationSeconds, 3);
            Assert.Equal(100, got.Bytes);
            Assert.Equal(12345, got.LastModified);
            Assert.Equal(cover, got.Cover);
        }

        [Fact]
        public void Upsert_NullableFields_SurviveRoundtrip()
        {
            var r = Row("D:\\M\\b.mp3");
            r.Title = null; r.Artist = null; r.Album = null; r.Cover = null;
            Db.Upsert(r);

            var got = Db.TryGet("D:\\M\\b.mp3");
            Assert.Null(got.Title);
            Assert.Null(got.Artist);
            Assert.Null(got.Album);
            Assert.Null(got.Cover);
        }

        [Fact]
        public void Upsert_Conflict_UpdatesRow()
        {
            Db.Upsert(Row("D:\\M\\c.mp3", "old", bytes: 1, mtime: 1));
            Db.Upsert(Row("D:\\M\\c.mp3", "new", bytes: 2, mtime: 2));

            var got = Db.TryGet("D:\\M\\c.mp3");
            Assert.Equal("new", got.Title);
            Assert.Equal(2, got.Bytes);
            Assert.Equal(2, got.LastModified);
        }

        [Fact]
        public void UpsertMany_BatchWrites()
        {
            var rows = new List<TrackRow>();
            for (int i = 0; i < 100; i++) rows.Add(Row("D:\\M\\f" + i + ".mp3"));
            Db.UpsertMany(rows);

            Assert.NotNull(Db.TryGet("D:\\M\\f0.mp3"));
            Assert.NotNull(Db.TryGet("D:\\M\\f99.mp3"));
            Assert.Null(Db.TryGet("D:\\M\\f100.mp3"));
        }

        [Fact]
        public void Upsert_NullOrPathlessRow_Ignored()
        {
            Db.Upsert(null);
            Db.Upsert(new TrackRow { Path = null });
            Assert.Null(Db.TryGet("anything"));
        }

        [Fact]
        public void TryGet_MissingOrInvalid_ReturnsNull()
        {
            Assert.Null(Db.TryGet("missing.mp3"));
            Assert.Null(Db.TryGet(null));
            Assert.Null(Db.TryGet(""));
        }

        [Fact]
        public void Delete_RemovesSpecifiedRows()
        {
            Db.Upsert(Row("D:\\M\\a.mp3"));
            Db.Upsert(Row("D:\\M\\b.mp3"));
            Db.Delete(new[] { "D:\\M\\a.mp3" });
            Assert.Null(Db.TryGet("D:\\M\\a.mp3"));
            Assert.NotNull(Db.TryGet("D:\\M\\b.mp3"));
        }

        [Fact]
        public void DeleteMissingUnder_RemovesStale_ButOnlyUnderExactPrefix()
        {
            // 关键回归：前缀 "D:\Music" 不得误伤 "D:\MusicBackup"（无目录分隔符边界）
            Db.Upsert(Row("D:\\Music\\a.mp3"));
            Db.Upsert(Row("D:\\Music\\sub\\b.mp3"));
            Db.Upsert(Row("D:\\MusicBackup\\x.mp3"));

            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "D:\\Music\\a.mp3" };
            int removed = Db.DeleteMissingUnder("D:\\Music", present);

            Assert.Equal(1, removed);   // 仅 sub\b.mp3（目录内已消失）
            Assert.Null(Db.TryGet("D:\\Music\\sub\\b.mp3"));
            // MusicBackup 行必须保留（前缀不带分隔符边界，不属于清理范围）
            var backup = Db.TryGet("D:\\MusicBackup\\x.mp3");
            Assert.NotNull(backup);
        }

        [Fact]
        public void FingerprintMatches_RequiresBothFieldsEqual()
        {
            var row = Row("p", bytes: 10, mtime: 20);
            Assert.True(LibraryDatabase.FingerprintMatches(row, 10, 20));
            Assert.False(LibraryDatabase.FingerprintMatches(row, 11, 20));
            Assert.False(LibraryDatabase.FingerprintMatches(row, 10, 21));
            Assert.False(LibraryDatabase.FingerprintMatches(null, 10, 20));
        }

        [Fact]
        public void Persistence_RowsSurviveReopen()
        {
            Db.Upsert(Row("D:\\M\\persist.mp3", "kept"));
            // 同一路径重新打开（模拟进程重启）
            var db2 = new LibraryDatabase(dbPath);
            var got = db2.TryGet("D:\\M\\persist.mp3");
            Assert.NotNull(got);
            Assert.Equal("kept", got.Title);
        }
    }

    public class IncrementalScanTests : IDisposable
    {
        readonly string dbPath = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N") + ".db");
        readonly string dir = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N"));

        LibraryDatabase Db { get { return new LibraryDatabase(dbPath); } }

        string WriteAudio(string name)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllBytes(p, new byte[] { 0xFF, 0xFB, 0x90, 0x00 });
            return p;
        }

        public void Dispose()
        {
            try { Directory.Delete(dir, true); } catch { }
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        [Fact]
        public void FirstScan_Parses_AndWritesCache()
        {
            string p = WriteAudio("周杰伦 - 晴天.mp3");
            var db = Db;

            var tracks = Library.BuildTracksIncremental(new[] { p }, db, dir);
            Assert.Single(tracks);
            Assert.Equal("晴天", tracks[0].Title);

            var row = db.TryGet(p);
            Assert.NotNull(row);
            Assert.Equal("晴天", row.Title);
            Library.GetFingerprint(p, out long bytes, out long mtime);
            Assert.Equal(bytes, row.Bytes);
            Assert.Equal(mtime, row.LastModified);
        }

        [Fact]
        public void SecondScan_UnchangedFile_ReusesCachedMetadata()
        {
            string p = WriteAudio("周杰伦 - 晴天.mp3");
            var db = Db;
            Library.BuildTracksIncremental(new[] { p }, db, dir);

            // 直接篡改缓存行：若第二次扫描复用缓存，构建出的 Track 会带上篡改标记
            var row = db.TryGet(p);
            row.Title = "CACHED_MARKER";
            db.Upsert(row);

            var tracks2 = Library.BuildTracksIncremental(new[] { p }, db, dir);
            Assert.Equal("CACHED_MARKER", tracks2[0].Title);   // 未重新解析 → 复用缓存
        }

        [Fact]
        public void ModifiedFile_InvalidatesCache_AndReparses()
        {
            string p = WriteAudio("周杰伦 - 晴天.mp3");
            var db = Db;
            Library.BuildTracksIncremental(new[] { p }, db, dir);

            var row = db.TryGet(p);
            row.Title = "CACHED_MARKER";
            db.Upsert(row);

            // 修改文件最后写入时间 → 指纹失效 → 重新解析
            File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddMinutes(1));
            var tracks2 = Library.BuildTracksIncremental(new[] { p }, db, dir);
            Assert.Equal("晴天", tracks2[0].Title);   // 回到文件名推断结果（缓存未复用）

            var row2 = db.TryGet(p);
            Assert.Equal("晴天", row2.Title);          // 缓存同步更新
        }

        [Fact]
        public void DeletedFile_RowCleanedUp_WhenCleanupDirGiven()
        {
            string p1 = WriteAudio("a.mp3");
            string p2 = WriteAudio("b.mp3");
            var db = Db;
            Library.BuildTracksIncremental(new[] { p1, p2 }, db, dir);

            File.Delete(p2);
            var files = Directory.GetFiles(dir);
            Library.BuildTracksIncremental(files, db, dir);

            Assert.NotNull(db.TryGet(p1));
            Assert.Null(db.TryGet(p2));   // 已消失 → 过期行被清理
        }

        [Fact]
        public void DeletedFile_RowKept_WhenNoCleanupDir()
        {
            // ImportPaths（追加导入）路径不清理过期行
            string p1 = WriteAudio("a.mp3");
            var db = Db;
            Library.BuildTracksIncremental(new[] { p1 }, db, dir);

            var row = db.TryGet(p1);
            row.Title = "KEEP_ME";
            db.Upsert(row);

            string p2 = WriteAudio("b.mp3");
            Library.BuildTracksIncremental(new[] { p2 }, db, null);
            Assert.Equal("KEEP_ME", db.TryGet(p1).Title);
        }

        [Fact]
        public void NullDb_BehavesLikeFullParse()
        {
            string p = WriteAudio("周杰伦 - 晴天.mp3");
            var tracks = Library.BuildTracksIncremental(new[] { p }, null, null);
            Assert.Single(tracks);
            Assert.Equal("晴天", tracks[0].Title);
        }

        [Fact]
        public void LrcStillPaired_OnCacheHit()
        {
            string p = WriteAudio("song.mp3");
            string lrc = Path.Combine(dir, "song.lrc");
            File.WriteAllBytes(lrc, System.Text.Encoding.UTF8.GetBytes("[00:01]cached line"));

            var db = Db;
            Library.BuildTracksIncremental(new[] { p, lrc }, db, dir);

            // 第二次扫描（缓存命中）lrc 仍要现读配对
            var tracks2 = Library.BuildTracksIncremental(new[] { p, lrc }, db, dir);
            Assert.NotNull(tracks2[0].LrcText);
            Assert.Contains("cached line", tracks2[0].LrcText);
        }

        [Fact]
        public void NewLrcContent_PickedUp_OnNextScan()
        {
            // lrc 不缓存：内容变化后即使音频指纹未变，下次扫描也能拿到新歌词
            string p = WriteAudio("song.mp3");
            string lrc = Path.Combine(dir, "song.lrc");
            File.WriteAllBytes(lrc, System.Text.Encoding.UTF8.GetBytes("[00:01]v1"));
            var db = Db;
            Library.BuildTracksIncremental(new[] { p, lrc }, db, dir);

            File.WriteAllBytes(lrc, System.Text.Encoding.UTF8.GetBytes("[00:01]v2"));
            var tracks2 = Library.BuildTracksIncremental(new[] { p, lrc }, db, dir);
            Assert.Contains("v2", tracks2[0].LrcText);
        }
    }
}
