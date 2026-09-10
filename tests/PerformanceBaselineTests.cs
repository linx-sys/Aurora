/* ============================================================
 * PerformanceBaselineTests.cs — 性能基线（阶段 0，docs/PERF_BASELINE.md）
 * 数据层可自动化量化：DB 读写 / DB 秒开物化 / 差分同步（热缓存）。
 * [Trait("Category","Perf")] 便于 CI 按需过滤；数字随机器浮动，
 * 结论以 docs/PERF_BASELINE.md 记录的历史值为准。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Aurora.Tests
{
    public class PerformanceBaselineTests : IDisposable
    {
        readonly ITestOutputHelper output;
        readonly string dbPath = Path.Combine(Path.GetTempPath(), "aurora_perf_" + Guid.NewGuid().ToString("N") + ".db");
        readonly string root = Path.Combine(Path.GetTempPath(), "aurora_perf_" + Guid.NewGuid().ToString("N"));

        LibraryDatabase db;

        LibraryDatabase Db
        {
            get { return db ?? (db = new LibraryDatabase(dbPath)); }
        }

        public PerformanceBaselineTests(ITestOutputHelper output)
        {
            this.output = output;
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try { Directory.Delete(root, true); } catch { }
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        static TrackRow Row(string path)
        {
            return new TrackRow
            {
                Path = path, FileName = Path.GetFileName(path),
                Title = "T" + Path.GetFileNameWithoutExtension(path),
                Artist = "Artist", Album = "Album",
                DurationSeconds = 180, Bytes = 1024, LastModified = 638000000000000000,
            };
        }

        /// <summary>生成 count 个真实小文件（空内容即可，物化只做 Exists/探测）。</summary>
        List<string> CreateRealFiles(int count)
        {
            var files = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                string p = Path.Combine(root, "f" + i + ".mp3");
                File.WriteAllText(p, "x");
                files.Add(p);
            }
            return files;
        }

        [Fact]
        [Trait("Category", "Perf")]
        public void Bench_DbWriteRead_10kRows()
        {
            var rows = Enumerable.Range(0, 10000).Select(i => Row(@"Z:\M\f" + i + ".mp3")).ToList();

            var sw = Stopwatch.StartNew();
            Db.UpsertMany(rows);
            sw.Stop();
            long writeMs = sw.ElapsedMilliseconds;

            sw = Stopwatch.StartNew();
            var got = Db.GetByPrefix(@"Z:\M");
            sw.Stop();
            long readMs = sw.ElapsedMilliseconds;

            output.WriteLine("[Perf] 10k UpsertMany: " + writeMs + " ms; GetByPrefix(10k): " + readMs + " ms (" + got.Count + " rows)");
            Assert.Equal(10000, got.Count);
        }

        [Fact]
        [Trait("Category", "Perf")]
        public void Bench_DbFirstMaterialize_1000RealFiles()
        {
            var files = CreateRealFiles(1000);
            Db.UpsertMany(files.Select(Row).ToList());

            // 预热（首访含 EnsureOpen/JIT）
            Library.BuildTracksFromRows(Db.GetByPrefix(root));

            var sw = Stopwatch.StartNew();
            var tracks = Library.BuildTracksFromRows(Db.GetByPrefix(root));
            sw.Stop();

            output.WriteLine("[Perf] DB 秒开物化 1000 首（GetByPrefix+BuildTracksFromRows）: " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(1000, tracks.Count);
        }

        [Fact]
        [Trait("Category", "Perf")]
        public void Bench_DifferentialSync_1000RealFiles_WarmCache()
        {
            var files = CreateRealFiles(1000);
            // 预灌指纹（模拟"二次启动，文件无变化"场景）
            Library.BuildTracksIncremental(files, Db, null);

            var sw = Stopwatch.StartNew();
            var built = Library.BuildTracksIncremental(files, Db, root);
            sw.Stop();

            output.WriteLine("[Perf] 差分同步 1000 首（热缓存，指纹全命中）: " + sw.ElapsedMilliseconds + " ms");
            Assert.Equal(1000, built.Count);
        }
    }
}
