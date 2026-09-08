/* ============================================================
 * PlaylistManagerTests.cs — 播放列表管理器单元测试
 * 覆盖：添加去重、删除、搜索筛选、五种排序语义（含默认=文件名、
 *       5=自定义原序）、整表替换、拖动排序、批量刷新（单次 Reset）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using Xunit;

namespace Aurora.Tests
{
    public class PlaylistManagerTests
    {
        static Track T(string path, string title = null, string artist = null, string album = null, int seconds = 0)
        {
            return new Track
            {
                FilePath = path,
                FileName = System.IO.Path.GetFileName(path),
                Title = title ?? System.IO.Path.GetFileNameWithoutExtension(path),
                Artist = artist ?? "",
                Album = album ?? "",
                Duration = TimeSpan.FromSeconds(seconds),
            };
        }

        [Fact]
        public void AddRange_AddsAndDedupsByPath()
        {
            var pm = new PlaylistManager();
            int added = pm.AddRange(new[] { T("a.mp3"), T("b.mp3"), T("a.mp3"), T("B.MP3") });
            Assert.Equal(2, added);   // a.mp3 重复；B.MP3 与 b.mp3 大小写不敏感视为同一文件
            Assert.Equal(2, pm.Tracks.Count);
        }

        [Fact]
        public void Remove_RemovesFromTracksAndView()
        {
            var pm = new PlaylistManager();
            var a = T("a.mp3"); var b = T("b.mp3");
            pm.AddRange(new[] { a, b });
            Assert.True(pm.Remove(a));
            Assert.Single(pm.Tracks);
            Assert.Single(pm.View);
            Assert.DoesNotContain(a, pm.View);
        }

        [Fact]
        public void RefreshView_DefaultSort_OrdersByFileName()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("c.mp3"), T("a.mp3"), T("b.mp3") });
            Assert.Equal("a.mp3", pm.View[0].FileName);
            Assert.Equal("b.mp3", pm.View[1].FileName);
            Assert.Equal("c.mp3", pm.View[2].FileName);
        }

        [Fact]
        public void SearchText_FiltersByTitleArtistAlbumFileName()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[]
            {
                T("01 - 晴天.mp3", "晴天", "周杰伦", "叶惠美"),
                T("02 - 夜曲.mp3", "夜曲", "周杰伦", "十一月的萧邦"),
                T("03 - Red.mp3", "Red", "Taylor Swift", "Red"),
            });

            pm.SearchText = "周杰伦";
            Assert.Equal(2, pm.View.Count);
            pm.SearchText = "swift";
            Assert.Single(pm.View);
            pm.SearchText = "萧邦";   // 专辑名命中
            Assert.Single(pm.View);
            pm.SearchText = "夜曲.mp3";   // 文件名命中
            Assert.Single(pm.View);
            pm.SearchText = "";           // 清空恢复全量
            Assert.Equal(3, pm.View.Count);
        }

        [Fact]
        public void SortMode_Duration_OrdersAscendingAndDescending()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[]
            {
                T("a.mp3", "A", seconds: 100),
                T("b.mp3", "B", seconds: 30),
                T("c.mp3", "C", seconds: 200),
            });

            pm.SortMode = 3;   // 时长升序
            Assert.Equal("b.mp3", pm.View[0].FileName);
            Assert.Equal("c.mp3", pm.View[2].FileName);

            pm.SortMode = 4;   // 时长降序
            Assert.Equal("c.mp3", pm.View[0].FileName);
            Assert.Equal("b.mp3", pm.View[2].FileName);
        }

        [Fact]
        public void SortMode_Title_OrdersCaseInsensitive()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3", "banana"), T("b.mp3", "Apple"), T("c.mp3", "cherry") });
            pm.SortMode = 1;
            Assert.Equal("Apple", pm.View[0].Title);
            Assert.Equal("banana", pm.View[1].Title);
        }

        [Fact]
        public void SortMode_Custom_PreservesTracksOrder()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("c.mp3"), T("a.mp3"), T("b.mp3") });
            pm.SortMode = 5;   // 自定义：保留 Tracks 原始顺序
            Assert.Equal("c.mp3", pm.View[0].FileName);
            Assert.Equal("a.mp3", pm.View[1].FileName);
            Assert.Equal("b.mp3", pm.View[2].FileName);
        }

        [Fact]
        public void ReplaceTracks_ReplacesAllAndRefreshes()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3") });
            pm.ReplaceTracks(new[] { T("x.mp3"), T("y.mp3") });
            Assert.Equal(2, pm.Tracks.Count);
            Assert.Equal(2, pm.View.Count);
            Assert.DoesNotContain(pm.Tracks, t => t.FileName == "a.mp3");
        }

        [Fact]
        public void RefreshView_RaisesSingleResetNotification()
        {
            // 回归测试（审查项7）：RefreshView 从 Clear+逐个 Add（n 次通知）
            // 改为 ReplaceAll（单次 Reset 通知），消除搜索输入时的 O(n²) 集合通知
            var pm = new PlaylistManager();
            var items = new List<Track>();
            for (int i = 0; i < 50; i++) items.Add(T("f" + i.ToString("D3") + ".mp3"));
            pm.AddRange(items);

            int notifications = 0;
            pm.View.CollectionChanged += (s, e) => notifications++;

            pm.SearchText = "f0";   // 触发一次刷新（应恰好 1 次通知）
            Assert.Equal(1, notifications);
            notifications = 0;
            pm.SearchText = "";
            Assert.Equal(1, notifications);
        }

        [Fact]
        public void Move_ReordersTracks()
        {
            var pm = new PlaylistManager();
            var a = T("a.mp3"); var b = T("b.mp3"); var c = T("c.mp3");
            pm.AddRange(new[] { a, b, c });
            pm.Move(0, 2);   // a 移到末尾
            Assert.Equal(new[] { b, c, a }, pm.Tracks);
        }

        [Fact]
        public void Move_OutOfRange_IsNoOp()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3"), T("b.mp3") });
            pm.Move(-1, 1);
            pm.Move(0, 5);
            Assert.Equal(2, pm.Tracks.Count);
            Assert.Equal("a.mp3", pm.Tracks[0].FileName);
        }

        [Fact]
        public void Clear_EmptiesTracksAndView()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3"), T("b.mp3") });
            pm.Clear();
            Assert.Empty(pm.Tracks);
            Assert.Empty(pm.View);
        }

        [Fact]
        public void FindByPath_CaseInsensitive()
        {
            var pm = new PlaylistManager();
            var a = T("Music\\Song01.mp3");
            pm.AddRange(new[] { a });
            Assert.Same(a, pm.FindByPath("music\\song01.mp3"));
            Assert.Null(pm.FindByPath("missing.mp3"));
            Assert.Null(pm.FindByPath(null));
        }
    }
}
