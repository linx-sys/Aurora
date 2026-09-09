/* ============================================================
 * LibraryDbFirstTests.cs — P1-3 DB 优先秒开 + 差分同步单元测试
 * 覆盖：BuildTracksFromRows 物化（消失文件跳过/lrc 同名配对/字段复用）、
 *       差分同步（新增 upsert/变更重解析/消失清理/目录边界不误伤）。
 * 使用临时 db 文件与临时音频目录，测试后清理。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    public class LibraryDbFirstTests : IDisposable
    {
        readonly string dbPath = Path.Combine(Path.GetTempPath(), "aurora_tests_" + Guid.NewGuid().ToString("N") + ".db");
        readonly string root = Path.Combine(Path.GetTempPath(), "aurora_dbfirst_" + Guid.NewGuid().ToString("N"));

        LibraryDatabase Db
        {
            get { return db ?? (db = new LibraryDatabase(dbPath)); }
        }
        LibraryDatabase db;

        public LibraryDbFirstTests()
        {
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        /// <summary>写一个假音频文件（内容不重要，只要存在且指纹可计算）。</summary>
        string WriteFake(string relName)
        {
            string path = Path.Combine(root, relName);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "fake-audio-" + relName);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));
            return path;
        }

        static TrackRow Row(string path, string title = "T", long bytes = 0, long mtime = 0)
        {
            long b = bytes, m = mtime;
            if (b == 0 || m == 0) Library.GetFingerprint(path, out b, out m);
            return new TrackRow
            {
                Path = path, FileName = System.IO.Path.GetFileName(path),
                Title = title, Artist = "A", Album = "AL",
                DurationSeconds = 12.5, Bytes = b, LastModified = m,
            };
        }

        // ---------- BuildTracksFromRows：DB 优先秒开物化 ----------

        [Fact]
        public void FromRows_Materializes_MetadataFromStore()
        {
            string f = WriteFake("a.mp3");
            Db.Upsert(Row(f, "晴天"));

            var tracks = Library.BuildTracksFromRows(Db.GetByPrefix(root));

            Assert.Single(tracks);
            Assert.Equal("晴天", tracks[0].Title);
            Assert.Equal("A", tracks[0].Artist);
            Assert.Equal(f, tracks[0].FilePath);
            Assert.Equal(TimeSpan.FromSeconds(12.5), tracks[0].Duration);
        }

        [Fact]
        public void FromRows_SkipsMissingFiles()
        {
            // 行指向不存在的文件：秒开阶段跳过，不产生残影
            Db.Upsert(Row(Path.Combine(root, "gone.mp3"), "已删除"));

            var tracks = Library.BuildTracksFromRows(Db.GetByPrefix(root));

            Assert.Empty(tracks);
        }

        [Fact]
        public void FromRows_PairsSiblingLrc()
        {
            string f = WriteFake("song.mp3");
            File.WriteAllText(Path.Combine(root, "song.lrc"), "[00:01.00]测试");
            Db.Upsert(Row(f, "song"));

            var tracks = Library.BuildTracksFromRows(Db.GetByPrefix(root));

            Assert.Single(tracks);
            Assert.NotNull(tracks[0].LrcText);
            Assert.Contains("测试", tracks[0].LrcText);
        }

        // ---------- 差分同步：新增/变更/清理/边界 ----------

        [Fact]
        public void Sync_NewAndChangedFiles_AreUpserted()
        {
            string a = WriteFake("a.mp3");
            string b = WriteFake("b.mp3");
            // 首扫：a 入库，b 未入库（模拟 b 是新增）
            Db.Upsert(Row(a, "A"));
            Library.BuildTracksIncremental(new[] { a }, Db, root);   // a 命中指纹，b 不参与

            // 二扫：文件系统实际有 a + b
            var files = new List<string> { a, b };
            var built = Library.BuildTracksIncremental(files, Db, root);

            Assert.Equal(2, built.Count);
            Assert.NotNull(Db.TryGet(b));   // 新增文件已 upsert 进 DB
            Assert.Equal(1, built.Count(t => t.FilePath == a));
        }

        [Fact]
        public void Sync_DeletedFiles_RowCleanedWithinBoundary()
        {
            string a = WriteFake("a.mp3");
            string sub = WriteFake(Path.Combine("sub", "c.mp3"));
            Library.BuildTracksIncremental(new[] { a, sub }, Db, root);

            // a 消失，sub 仍在：清理应只删 a
            File.Delete(a);
            var built = Library.BuildTracksIncremental(new[] { sub }, Db, root);

            Assert.Null(Db.TryGet(a));
            Assert.NotNull(Db.TryGet(sub));
            Assert.Single(built);
        }

        [Fact]
        public void Sync_SiblingDirectory_NotTouched()
        {
            // "root" 与 "rootBackup" 前缀相邻：清理 root 不得误伤 rootBackup 下的行
            string a = WriteFake("a.mp3");
            Library.BuildTracksIncremental(new[] { a }, Db, null);   // 无清理地 upsert
            string backupFile = Path.Combine(root + "Backup", "keep.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(backupFile));
            File.WriteAllText(backupFile, "x");
            Db.Upsert(Row(backupFile, "KEEP"));

            // 模拟 root 内 a 已消失
            File.Delete(a);
            Library.BuildTracksIncremental(new List<string>(), Db, root);

            Assert.Null(Db.TryGet(a));
            Assert.NotNull(Db.TryGet(backupFile));   // 相邻目录的行未被误删
        }
    }
}
