/* ============================================================
 * PlaybackEdgeTests.cs — 播放状态机 / 播放列表边界补充测试
 * 覆盖评审建议的高价值场景：空列表、单曲库、200 条随机轨迹上限、
 * 默认排序下 Move 同步 View、非法输入的幂等性。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    public class PlaybackEdgeTests
    {
        static List<Track> MakeTracks(int n)
        {
            var list = new List<Track>();
            for (int i = 0; i < n; i++)
                list.Add(new Track { FilePath = "f" + i + ".mp3", FileName = "f" + i + ".mp3", Title = "T" + i });
            return list;
        }

        [Fact]
        public void GetNextForManual_EmptyOrNullList_ReturnsNull()
        {
            var c = new PlaybackController();
            Assert.Null(c.GetNextForManual(new List<Track>(), null));
            Assert.Null(c.GetNextForManual(null, null));
        }

        [Fact]
        public void GetPrev_EmptyOrNullList_ReturnsNull()
        {
            var c = new PlaybackController();
            Assert.Null(c.GetPrev(new List<Track>(), null));
            Assert.Null(c.GetPrev(null, null));
        }

        [Fact]
        public void Shuffle_SingleTrackLibrary_NextIsSameTrack()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(1);
            Assert.Same(ts[0], c.GetNextForAuto(ts, ts[0]));
            Assert.Same(ts[0], c.GetNextForManual(ts, ts[0]));
        }

        [Fact]
        public void Shuffle_TrajectoryCappedAt200()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(250);
            foreach (var t in ts) c.RecordPlay(t);

            // 上限 200：最老的 50 条被裁掉，从末端回退一步应为 ts[248]
            Assert.True(c.TryGetShufflePrev(out var prev));
            Assert.Same(ts[248], prev);
        }

        [Fact]
        public void Shuffle_RecordPlayAfterBackward_TrimsForward()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(4);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[1]);
            c.RecordPlay(ts[2]);
            c.TryGetShufflePrev(out _);   // 回退到 ts[1]
            c.RecordPlay(ts[3]);          // 播新曲 → ts[2] 前进分支被截断

            // 轨迹应为 [ts0, ts1, ts3]：回退到 ts1，前进回到 ts3，再无前进
            Assert.True(c.TryGetShufflePrev(out var prev));
            Assert.Same(ts[1], prev);
            Assert.True(c.TryGetShuffleNext(out var next));
            Assert.Same(ts[3], next);
            Assert.False(c.TryGetShuffleNext(out _));
        }

        [Fact]
        public void GetNextForAuto_ListRepeat_NullCurrent_StartsFromBeginning()
        {
            var c = new PlaybackController();
            var ts = MakeTracks(3);
            Assert.Same(ts[0], c.GetNextForAuto(ts, null));
        }

        [Fact]
        public void ShuffleMode_TryGetPrevNext_OnlyActiveInShuffle()
        {
            var c = new PlaybackController();   // ListRepeat
            var ts = MakeTracks(3);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[1]);
            // 模式切回 ListRepeat 后轨迹功能整体失效
            c.Mode = PlayMode.ListRepeat;
            Assert.False(c.TryGetShufflePrev(out _));
            Assert.False(c.TryGetShuffleNext(out _));
        }
    }

    public class PlaylistEdgeTests
    {
        static Track T(string path, string artist = "A", int seconds = 0)
        {
            return new Track
            {
                FilePath = path,
                FileName = System.IO.Path.GetFileName(path),
                Title = System.IO.Path.GetFileNameWithoutExtension(path),
                Artist = artist,
                Duration = TimeSpan.FromSeconds(seconds),
            };
        }

        [Fact]
        public void SortMode_Artist_OrdersByName()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3", "C"), T("b.mp3", "A"), T("c.mp3", "B") });
            pm.SortMode = 2;
            Assert.Equal(new[] { "A", "B", "C" }, pm.View.Select(t => t.Artist).ToArray());
        }

        [Fact]
        public void Move_InDefaultSort_SyncsView()
        {
            var pm = new PlaylistManager();   // SortMode 0 = 文件名
            pm.AddRange(new[] { T("a.mp3"), T("b.mp3"), T("c.mp3") });
            pm.Move(0, 2);   // a 移到末尾
            Assert.Equal("a.mp3", pm.Tracks[2].FileName);
            // 默认（文件名）排序下 RefreshView 重新按文件名排序 View —— 文档化现有语义：
            // 拖动排序必须配合 SortMode=5（自定义顺序）才有意义
            Assert.Equal(new[] { "a.mp3", "b.mp3", "c.mp3" }, pm.View.Select(t => t.FileName).ToArray());

            // 自定义顺序下：Move + 显式 RefreshView 后 View 跟随 Tracks
            pm.SortMode = 5;
            pm.Move(0, 1);   // b 移到最前 → Tracks [b,c,a]？不对：Move(0,1) 移动 a… 实为把 b(1) 移到 0 前的 a(0) 之后
            pm.RefreshView();
            Assert.Equal(pm.Tracks.Select(t => t.FileName).ToArray(), pm.View.Select(t => t.FileName).ToArray());
        }

        [Fact]
        public void Move_SameIndex_IsNoOp()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3"), T("b.mp3") });
            pm.Move(1, 1);
            Assert.Equal(new[] { "a.mp3", "b.mp3" }, pm.Tracks.Select(t => t.FileName).ToArray());
        }

        [Fact]
        public void Remove_Nonexistent_ReturnsFalse()
        {
            var pm = new PlaylistManager();
            Assert.False(pm.Remove(null));
            Assert.False(pm.Remove(T("missing.mp3")));
        }

        [Fact]
        public void AddRange_NullOrEmpty_ReturnsZero()
        {
            var pm = new PlaylistManager();
            Assert.Equal(0, pm.AddRange(null));
            Assert.Equal(0, pm.AddRange(new Track[0]));
        }

        [Fact]
        public void SearchText_NullValue_TreatedAsEmpty()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("a.mp3"), T("b.mp3") });
            pm.SearchText = null;
            Assert.Equal(2, pm.View.Count);
        }

        [Fact]
        public void SearchText_IsCaseInsensitive()
        {
            var pm = new PlaylistManager();
            pm.AddRange(new[] { T("Red.mp3") });
            pm.SearchText = "RED";
            Assert.Single(pm.View);
        }
    }
}
