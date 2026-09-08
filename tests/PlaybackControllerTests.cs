/* ============================================================
 * PlaybackControllerTests.cs — 播放模式状态机单元测试
 * 覆盖：模式切换、列表循环/单曲循环、随机轨迹回退/前进、
 *       RecordPlay 截断与去重、轨迹重置。
 * ============================================================ */
using System.Collections.Generic;
using Xunit;

namespace Aurora.Tests
{
    public class PlaybackControllerTests
    {
        static List<Track> MakeTracks(int n)
        {
            var list = new List<Track>();
            for (int i = 0; i < n; i++)
                list.Add(new Track { FilePath = "f" + i + ".mp3", FileName = "f" + i + ".mp3", Title = "T" + i });
            return list;
        }

        [Fact]
        public void ToggleMode_CyclesThroughThreeModes()
        {
            var c = new PlaybackController();
            Assert.Equal(PlayMode.ListRepeat, c.Mode);
            Assert.Equal(PlayMode.SingleRepeat, c.ToggleMode());
            Assert.Equal(PlayMode.Shuffle, c.ToggleMode());
            Assert.Equal(PlayMode.ListRepeat, c.ToggleMode());
        }

        [Fact]
        public void GetNextForAuto_ListRepeat_AdvancesAndWraps()
        {
            var c = new PlaybackController();
            var ts = MakeTracks(3);
            Assert.Equal(ts[1], c.GetNextForAuto(ts, ts[0]));
            Assert.Equal(ts[2], c.GetNextForAuto(ts, ts[1]));
            Assert.Equal(ts[0], c.GetNextForAuto(ts, ts[2]));   // 末尾回到开头
            Assert.Equal(ts[0], c.GetNextForAuto(ts, null));    // 无当前曲 → 第一首
        }

        [Fact]
        public void GetNextForAuto_SingleRepeat_ReturnsNull()
        {
            var c = new PlaybackController { Mode = PlayMode.SingleRepeat };
            var ts = MakeTracks(3);
            Assert.Null(c.GetNextForAuto(ts, ts[0]));
        }

        [Fact]
        public void GetNextForManual_SingleRepeat_StillAdvances()
        {
            var c = new PlaybackController { Mode = PlayMode.SingleRepeat };
            var ts = MakeTracks(3);
            Assert.Equal(ts[1], c.GetNextForManual(ts, ts[0]));
        }

        [Fact]
        public void GetPrev_WrapsBackwards()
        {
            var c = new PlaybackController();
            var ts = MakeTracks(3);
            Assert.Equal(ts[2], c.GetPrev(ts, ts[0]));
            Assert.Equal(ts[0], c.GetPrev(ts, ts[1]));
        }

        [Fact]
        public void Shuffle_RecordPlay_BuildsTrajectory()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(5);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[2]);
            c.RecordPlay(ts[4]);

            // 沿轨迹前进/回退
            Assert.True(c.TryGetShufflePrev(out var prev));
            Assert.Equal(ts[2], prev);
            Assert.True(c.TryGetShuffleNext(out var next));
            Assert.Equal(ts[4], next);
            // 已在轨迹末端，无可前进
            Assert.False(c.TryGetShuffleNext(out _));
        }

        [Fact]
        public void Shuffle_RecordPlay_TruncatesForwardBranch()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(5);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[1]);
            c.RecordPlay(ts[2]);
            Assert.True(c.TryGetShufflePrev(out _));   // 回退到 ts[1]
            Assert.True(c.TryGetShufflePrev(out _));   // 回退到 ts[0]
            // 此时播放新曲 → 截断前进分支（与浏览器历史一致）
            c.RecordPlay(ts[3]);
            Assert.False(c.TryGetShuffleNext(out _));
            Assert.True(c.TryGetShufflePrev(out var prev));
            Assert.Equal(ts[0], prev);
        }

        [Fact]
        public void Shuffle_RecordPlay_IgnoresConsecutiveRepeat()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(3);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[1]);
            c.RecordPlay(ts[1]);   // 连续重复不重复入栈
            c.RecordPlay(ts[1]);
            // 轨迹仍为 [ts0, ts1]：从末端回退应到 ts[0] 而不是 ts[1]
            Assert.True(c.TryGetShufflePrev(out var prev));
            Assert.Equal(ts[0], prev);
            Assert.True(c.TryGetShuffleNext(out var next));
            Assert.Equal(ts[1], next);
            Assert.True(c.TryGetShufflePrev(out var prev2));   // 再次回退
            Assert.Equal(ts[0], prev2);
            Assert.False(c.TryGetShufflePrev(out _));          // 已回到轨迹起点，无可回退
        }

        [Fact]
        public void Shuffle_TryGetShufflePrev_AtStart_ReturnsFalse()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(3);
            c.RecordPlay(ts[0]);
            Assert.False(c.TryGetShufflePrev(out _));
        }

        [Fact]
        public void Shuffle_GetNext_AvoidsImmediateRepeat()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(2);
            for (int i = 0; i < 10; i++)
            {
                var next = c.GetNextForAuto(ts, ts[0]);
                Assert.NotSame(ts[0], next);   // 两首歌时随机也不应连续重复
            }
        }

        [Fact]
        public void RecordPlay_IgnoresNonShuffleMode()
        {
            var c = new PlaybackController();   // ListRepeat
            var ts = MakeTracks(3);
            c.RecordPlay(ts[0]);
            Assert.False(c.TryGetShufflePrev(out _));
        }

        [Fact]
        public void ResetShuffleHistory_ClearsTrajectory()
        {
            var c = new PlaybackController { Mode = PlayMode.Shuffle };
            var ts = MakeTracks(3);
            c.RecordPlay(ts[0]);
            c.RecordPlay(ts[1]);
            c.ResetShuffleHistory();
            Assert.False(c.TryGetShufflePrev(out _));
            Assert.False(c.TryGetShuffleNext(out _));
        }

        [Fact]
        public void GetNextForAuto_EmptyOrNullList_ReturnsNull()
        {
            var c = new PlaybackController();
            Assert.Null(c.GetNextForAuto(new List<Track>(), null));
            Assert.Null(c.GetNextForAuto(null, null));
        }
    }
}
