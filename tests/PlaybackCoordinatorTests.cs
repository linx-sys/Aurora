/* ============================================================
 * PlaybackCoordinatorTests.cs — 播放协调器单元测试（阶段 3）
 * FakePlaybackService 实现 IPlaybackService（可注入加载结果/会话/事件），
 * UI 调度用内联执行（a => a()），协调器逻辑全程同步可断言。
 * 覆盖：PlayTrack 成败事件流 / Next-Prev 选曲 / 预载 / 删除当前曲 /
 *       Seek / ReplayGain 缓存应用 / 会话竞态过滤 / TogglePlay。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    /// <summary>IPlaybackService 假实现：记录调用轨迹，事件可手动触发。</summary>
    class FakePlaybackService : IPlaybackService
    {
        public PlaybackState State { get; set; } = PlaybackState.Stopped;
        public float Volume { get; set; } = 1f;
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(180);
        public long SessionId { get; set; }
        public string CurrentPath { get; set; }
        public bool LoadResult { get; set; } = true;

        public List<string> LoadedPaths = new List<string>();
        public List<string> PreloadedPaths = new List<string>();
        public List<float> GainHistory = new List<float>();
        public int PlayCount, PauseCount, StopCount;

        public event EventHandler<PlaybackStateChangedEventArgs> StateChanged;
        public event EventHandler<PlaybackEndedEventArgs> PlaybackEnded;

        public bool Load(string path)
        {
            if (!LoadResult) return false;
            LoadedPaths.Add(path);
            CurrentPath = path;
            SessionId++;
            return true;
        }

        public bool CrossfadeTo(string path, double fadeSeconds)
        {
            LoadedPaths.Add(path);
            CurrentPath = path;
            SessionId++;
            State = PlaybackState.Playing;
            return true;
        }

        public void Play() { PlayCount++; State = PlaybackState.Playing; }
        public void Pause() { PauseCount++; State = PlaybackState.Paused; }
        public void Stop() { StopCount++; State = PlaybackState.Stopped; }
        public void Seek(TimeSpan position) { Position = position; }
        public void Preload(string path) { PreloadedPaths.Add(path); }
        public void SetReplayGain(float linear) { GainHistory.Add(linear); }
        public void Dispose() { }

        public void RaiseEnded(long session, string path)
        {
            var handler = PlaybackEnded;
            if (handler != null) handler(this, new PlaybackEndedEventArgs { SessionId = session, Path = path });
        }
    }

    public class PlaybackCoordinatorTests : IDisposable
    {
        readonly string dbPath = Path.Combine(Path.GetTempPath(), "aurora_coord_" + Guid.NewGuid().ToString("N") + ".db");
        readonly FakePlaybackService fake = new FakePlaybackService();
        readonly PlaybackCoordinator coordinator;

        public PlaybackCoordinatorTests()
        {
            var store = new LibraryDatabase(dbPath);
            // UI 调度内联执行：引擎后台回调在测试线程同步跑完，可断言
            coordinator = new PlaybackCoordinator(fake, store, new PlaylistManager(), a => a());
        }

        public void Dispose()
        {
            try { File.Delete(dbPath); } catch { }
            try { File.Delete(dbPath + "-wal"); } catch { }
            try { File.Delete(dbPath + "-shm"); } catch { }
        }

        static Track T(string name)
        {
            return new Track { FilePath = @"Z:\M\" + name + ".mp3", Title = name, Artist = "A", Album = "AL" };
        }

        /// <summary>灌入列表并返回实例（PlaylistManager 按引用匹配，选曲/删除必须用列表内实例）。</summary>
        List<Track> Seed(params string[] names)
        {
            var list = names.Select(T).ToList();
            coordinator.Playlist.ReplaceTracks(list);
            return list;
        }

        /* ---------- PlayTrack 事件流 ---------- */

        [Fact]
        public void PlayTrack_Success_SetsCurrent_AndRaisesEvent()
        {
            CurrentTrackChangedEventArgs args = null;
            coordinator.CurrentTrackChanged += (s, e) => args = e;

            var t = T("a");
            coordinator.PlayTrack(t, autoplay: true);

            Assert.Same(t, coordinator.CurrentTrack);
            Assert.NotNull(args);
            Assert.Same(t, args.Track);
            Assert.False(args.LoadFailed);
            Assert.Equal(@"Z:\M\a.mp3", fake.LoadedPaths.Last());
            Assert.Equal(PlaybackState.Playing, fake.State);   // autoplay → Play
        }

        [Fact]
        public void PlayTrack_LoadFail_ClearsCurrent_RaisesLoadFailed()
        {
            fake.LoadResult = false;
            CurrentTrackChangedEventArgs args = null;
            coordinator.CurrentTrackChanged += (s, e) => args = e;

            coordinator.PlayTrack(T("bad"));

            Assert.Null(coordinator.CurrentTrack);
            Assert.NotNull(args);
            Assert.True(args.LoadFailed);
            Assert.Equal("bad", args.Track.Title);
        }

        [Fact]
        public void PlayTrack_NoAutoplay_DoesNotPlay()
        {
            coordinator.PlayTrack(T("a"), autoplay: false);
            Assert.Equal(0, fake.PlayCount);
            Assert.Equal(PlaybackState.Stopped, fake.State);
        }

        [Fact]
        public void PlayTrack_NullTrack_NoOp()
        {
            coordinator.PlayTrack(null);
            Assert.Empty(fake.LoadedPaths);
        }

        /* ---------- Next / Prev 选曲 ---------- */

        [Fact]
        public void NextTrack_Manual_SequentialFromPlaylist()
        {
            var list = Seed("a", "b", "c");
            coordinator.PlayTrack(list[0]);

            coordinator.NextTrack(true);

            Assert.Same(list[1], coordinator.CurrentTrack);
            Assert.Equal(@"Z:\M\b.mp3", fake.LoadedPaths.Last());
        }

        [Fact]
        public void NextTrack_Auto_Sequential_WhenNoPending()
        {
            var list = Seed("a", "b", "c");
            coordinator.PlayTrack(list[0]);

            coordinator.NextTrack(false);   // 自动（无预载决策）

            Assert.Same(list[1], coordinator.CurrentTrack);
        }

        [Fact]
        public void PreloadNearEnd_PreploadsNext_AndAutoNext_UsesIt()
        {
            var list = Seed("a", "b");
            coordinator.PlayTrack(list[0]);
            fake.Play();                      // 引擎处于播放态（巡检前置条件）
            fake.Position = TimeSpan.FromSeconds(176.5);   // 剩余 3.5s：进入预载窗口、不触发跨淡（任意 cf 设置下）

            coordinator.PreloadNextIfNearEnd();
            Assert.Contains(@"Z:\M\b.mp3", fake.PreloadedPaths);

            coordinator.NextTrack(false);     // 自然结束 → 自动切歌应复用预载决策的同一首
            Assert.Equal(@"Z:\M\b.mp3", fake.LoadedPaths.Last());
        }

        [Fact]
        public void PrevTrack_GoesBack()
        {
            var list = Seed("a", "b", "c");
            coordinator.PlayTrack(list[1]);

            coordinator.PrevTrack();

            Assert.Same(list[0], coordinator.CurrentTrack);
        }

        /* ---------- 删除 ---------- */

        [Fact]
        public void DeleteTrack_Current_StopsEngine_ClearsCurrent()
        {
            var list = Seed("a", "b");
            coordinator.PlayTrack(list[0]);

            coordinator.DeleteTrack(list[0]);

            Assert.Equal(1, fake.StopCount);
            Assert.Null(coordinator.CurrentTrack);
            Assert.Single(coordinator.Playlist.Tracks);   // 列表只剩 b
        }

        [Fact]
        public void DeleteTrack_NonCurrent_KeepsCurrentPlaying()
        {
            var list = Seed("a", "b");
            coordinator.PlayTrack(list[0]);

            coordinator.DeleteTrack(list[1]);

            Assert.Equal(0, fake.StopCount);
            Assert.Same(list[0], coordinator.CurrentTrack);
        }

        /* ---------- Seek / 模式 ---------- */

        [Fact]
        public void SeekTo_Ratio_ClampedByDuration()
        {
            fake.Duration = TimeSpan.FromSeconds(180);
            coordinator.SeekTo(0.5);
            Assert.Equal(TimeSpan.FromSeconds(90), fake.Position);
        }

        [Fact]
        public void Mode_SetToggle_Roundtrip()
        {
            coordinator.Mode = PlayMode.Shuffle;
            Assert.Equal(PlayMode.Shuffle, coordinator.Mode);
            Assert.Equal(PlayMode.ListRepeat, coordinator.ToggleMode());   // Shuffle → ListRepeat（状态机循环）
        }

        [Fact]
        public void TogglePlay_PauseResume()
        {
            coordinator.PlayTrack(T("a"));   // autoplay → Playing
            coordinator.TogglePlay();
            Assert.Equal(PlaybackState.Paused, fake.State);
            coordinator.TogglePlay();
            Assert.Equal(PlaybackState.Playing, fake.State);
        }

        /* ---------- ReplayGain 缓存应用 ---------- */

        [Fact]
        public void ReplayGain_CachedGain_AppliedToEngine()
        {
            var store = new LibraryDatabase(dbPath);   // 与协调器同一库文件（LibraryDatabase 内部加锁，各实例可共存）
            store.Upsert(new TrackRow
            {
                Path = @"Z:\M\rg.mp3", FileName = "rg.mp3",
                Bytes = 100, LastModified = 12345,
                TrackGain = -3.0, TrackPeak = 0.5,
            });

            coordinator.PlayTrack(T("rg"));

            float expected = (float)Loudness.LinearFor(-3.0, 0.5);
            Assert.Equal(expected, fake.GainHistory.Last());
        }

        [Fact]
        public void ReplayGain_NoCachedRow_DefaultUnity()
        {
            coordinator.PlayTrack(T("nogain"));
            Assert.Equal(1f, fake.GainHistory.Last());
        }

        /* ---------- 会话竞态过滤（第二道防线） ---------- */

        [Fact]
        public void Ended_StaleSession_Ignored()
        {
            var list = Seed("a", "b");
            coordinator.PlayTrack(list[0]);   // session = 1
            int loadedBefore = fake.LoadedPaths.Count;

            fake.RaiseEnded(0, @"Z:\M\a.mp3");   // 过期会话（当前 1）

            Assert.Equal(loadedBefore, fake.LoadedPaths.Count);   // 未触发自动切歌
        }

        [Fact]
        public void Ended_FreshSession_TriggersAutoNext()
        {
            var list = Seed("a", "b");
            coordinator.PlayTrack(list[0]);   // session = 1
            long s = fake.SessionId;

            fake.RaiseEnded(s, list[0].FilePath);

            Assert.Equal(@"Z:\M\b.mp3", fake.LoadedPaths.Last());   // 自动切到下一首
        }
    }
}
