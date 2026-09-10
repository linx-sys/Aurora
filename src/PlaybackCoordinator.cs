/* ============================================================
 * PlaybackCoordinator.cs — 播放协调器（阶段 3，Application 层）
 * 从 MainViewModel 迁出的全部播放业务：
 *   PlayTrack / Next / Prev / DeleteTrack / SeekTo /
 *   PreloadNextIfNearEnd（预载决策）/ CrossfadeToTrack（跨淡决策）/
 *   ReplayGain 懒分析调度 / 引擎会话竞态过滤（第二道防线）。
 * MainViewModel 收敛为 UI 状态 + 命令转发 + 事件源，仅经本类交互。
 * 依赖方向：Coordinator → IPlaybackService / PlaybackController /
 *           PlaylistManager / ILibraryStore（不依赖任何 UI 类型）。
 * ============================================================ */
using System;
using System.Collections.Generic;

namespace Aurora
{
    public class PlaybackCoordinator
    {
        readonly IPlaybackService _player;
        readonly ILibraryStore _library;
        readonly PlaylistManager _playlist;
        readonly PlaybackController _playback;
        readonly Action<Action> _ui;          // UI 线程调度（VM 传 Dispatcher.BeginInvoke；测试传内联）

        readonly HashSet<string> _rgScanning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Track? _pendingNext;                   // 预载决策的下一首
        Track? _current;

        /// <summary>当前曲目变更事件（UI/VM 层订阅；含 LoadFailed 失败通知）。</summary>
        public event EventHandler<CurrentTrackChangedEventArgs>? CurrentTrackChanged;

        /// <summary>播放状态同步（参数 = 是否播放中；引擎事件驱动，UI 线程回调）。</summary>
        public event EventHandler<bool>? PlayStateChanged;

        public PlaybackCoordinator(IPlaybackService player, ILibraryStore library,
            PlaylistManager playlist, Action<Action> ui)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _library = library ?? throw new ArgumentNullException(nameof(library));
            _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
            _ui = ui ?? (a => a());
            _playback = new PlaybackController();

            _player.StateChanged += OnPlayerStateChanged;
            _player.PlaybackEnded += OnPlaybackEnded;
        }

        /* ---------- 供 VM 透出的协作对象 ---------- */

        public PlaybackController Playback { get { return _playback; } }
        public PlaylistManager Playlist { get { return _playlist; } }
        public Track? CurrentTrack { get { return _current; } }

        public PlayMode Mode
        {
            get { return _playback.Mode; }
            set { _playback.Mode = value; }
        }

        public PlayMode ToggleMode() { return _playback.ToggleMode(); }
        public void ResetShuffleHistory() { _playback.ResetShuffleHistory(); }
        public void RefreshView() { _playlist.RefreshView(); }

        public static bool ReplayGainEnabled { get { return Settings.Get("replaygain", "1") != "0"; } }

        /// <summary>跨淡时长秒（0=关）；设置键 crossfade。</summary>
        public static double CrossfadeSeconds
        {
            get
            {
                double d;
                return double.TryParse(Settings.Get("crossfade", "0"), out d) ? Math.Max(0, Math.Min(12, d)) : 0;
            }
        }

        /* ============================================================
         * 播放控制
         * ============================================================ */

        /// <summary>播放指定曲目（recordHistory 默认入随机轨迹）。</summary>
        public void PlayTrack(Track? t, bool autoplay = true)
        {
            PlayTrack(t, autoplay, recordHistory: true);
        }

        /// <summary>播放指定曲目。recordHistory=false 用于沿随机轨迹回退/前进（不重复入栈）。</summary>
        public void PlayTrack(Track? t, bool autoplay, bool recordHistory)
        {
            if (t == null) return;
            MainViewModel.Dbg("PlayTrack: " + t.Title + " autoplay=" + autoplay + " record=" + recordHistory);
            _pendingNext = null;   // 手动/显式切歌：作废预载决策
            if (recordHistory) _playback.RecordPlay(t);
            SetCurrent(t);
            bool loadOk = _player.Load(t.FilePath);
            if (!loadOk)
            {
                SetCurrent(null);
                RaisePlayState(false);
                CurrentTrackChanged?.Invoke(this, new CurrentTrackChangedEventArgs
                {
                    Track = t, AutoPlay = autoplay, LoadFailed = true,
                    FailMessage = "无法播放「" + t.Title + "」"
                });
                return;
            }
            if (t.Duration == TimeSpan.Zero && _player.Duration > TimeSpan.Zero)
                t.Duration = _player.Duration;
            _player.Position = TimeSpan.Zero;
            if (autoplay) { _player.Play(); RaisePlayState(true); }
            else RaisePlayState(false);
            ApplyReplayGain(t);
            CurrentTrackChanged?.Invoke(this, new CurrentTrackChangedEventArgs
            {
                Track = t, AutoPlay = autoplay, LoadFailed = false
            });
        }

        public void TogglePlay()
        {
            if (_player.State == PlaybackState.Playing) { _player.Pause(); RaisePlayState(false); }
            else { _player.Play(); RaisePlayState(true); }
        }

        public void NextTrack(bool manual)
        {
            var tracks = _playlist.Tracks;
            if (tracks.Count == 0) return;
            Track? next;
            if (manual)
            {
                // 随机模式：优先沿播放轨迹"前进"（重播回退过的歌），无可前进再随机新曲
                if (_playback.Mode == PlayMode.Shuffle && _playback.TryGetShuffleNext(out next))
                {
                    PlayTrack(next, true, recordHistory: false);
                    return;
                }
                next = _playback.GetNextForManual(tracks, _current);
            }
            else
            {
                // 自动切歌：优先用预载决策（保证与预热解码的是同一首，衔接零开销）
                if (_pendingNext != null) { next = _pendingNext; _pendingNext = null; }
                else next = _playback.GetNextForAuto(tracks, _current);
                if (next == null) // 单曲循环：重新播放当前曲
                {
                    // P2 管线：播放结束时输入已从混音器移除、解码器已释放，
                    // 不能再复用旧 reader（Position/Play 均无效），必须走完整 Load 重播
                    var cur = _current;
                    if (cur != null) PlayTrack(cur, true);
                    return;
                }
            }
            if (next != null) PlayTrack(next, true);
        }

        public void PrevTrack()
        {
            var tracks = _playlist.Tracks;
            if (tracks.Count == 0) return;
            // 随机模式：真正回退刚听过的歌（播放轨迹），而不是简单索引-1
            if (_playback.Mode == PlayMode.Shuffle && _playback.TryGetShufflePrev(out Track? prev))
            {
                PlayTrack(prev, true, recordHistory: false);
                return;
            }
            var prev2 = _playback.GetPrev(tracks, _current);
            if (prev2 != null) PlayTrack(prev2, true);
        }

        public void DeleteTrack(Track t)
        {
            if (t == null) return;
            bool wasCurrent = t == _current;
            _playlist.Remove(t);
            if (wasCurrent)
            {
                _player.Stop();
                RaisePlayState(false);
                SetCurrent(null);
                CurrentTrackChanged?.Invoke(this, new CurrentTrackChangedEventArgs
                {
                    Track = null, AutoPlay = false, LoadFailed = false
                });
            }
        }

        public void SeekTo(double ratio)
        {
            if (_player.Duration > TimeSpan.Zero)
            {
                _player.Position = TimeSpan.FromSeconds(ratio * _player.Duration.TotalSeconds);
            }
        }

        /// <summary>
        /// 临结束巡检（PlaybackTickController 每帧调用）：
        /// 剩余时间进入窗口时预选并预热下一首；开启跨淡时到点直接切换。
        /// </summary>
        public void PreloadNextIfNearEnd()
        {
            var tracks = _playlist.Tracks;
            if (tracks.Count == 0 || _current == null || _player.State != PlaybackState.Playing) return;
            TimeSpan dur = _player.Duration;
            if (dur <= TimeSpan.Zero) return;
            double cf = CrossfadeSeconds;
            double remaining = (dur - _player.Position).TotalSeconds;
            if (remaining <= 0 || remaining > (cf > 0 ? cf + 1.5 : 4.0)) return;

            bool singleRepeat = _playback.Mode == PlayMode.SingleRepeat;

            if (_pendingNext == null)
            {
                if (singleRepeat)
                    _pendingNext = _current;   // 单曲循环：预载当前曲（播完时 reader 已释放，走 Load 重播才有效）
                else
                    _pendingNext = _playback.GetNextForAuto(tracks, _current);
                if (_pendingNext != null)
                {
                    MainViewModel.Dbg("Preload next: " + _pendingNext.Title + " (remaining=" + remaining.ToString("0.0") + "s)");
                    _player.Preload(_pendingNext.FilePath);
                }
            }

            // 跨淡只对"切到另一首"生效；单曲循环走自然结束+重播（预载保证近零间隙）
            if (cf > 0 && !singleRepeat && _pendingNext != null && remaining <= cf)
            {
                Track t = _pendingNext;
                _pendingNext = null;
                CrossfadeToTrack(t);
            }
        }

        /// <summary>跨淡切换到指定曲目（当前曲目淡出、新曲目淡入混入）。</summary>
        void CrossfadeToTrack(Track t)
        {
            MainViewModel.Dbg("CrossfadeTo: " + t.Title);
            _playback.RecordPlay(t);
            bool ok = _player.CrossfadeTo(t.FilePath, CrossfadeSeconds);
            if (!ok)
            {
                PlayTrack(t, true);   // 跨淡失败（文件缺失/解码失败）回退硬切
                return;
            }
            if (t.Duration == TimeSpan.Zero && _player.Duration > TimeSpan.Zero)
                t.Duration = _player.Duration;
            SetCurrent(t);
            RaisePlayState(true);
            ApplyReplayGain(t);
            CurrentTrackChanged?.Invoke(this, new CurrentTrackChangedEventArgs
            {
                Track = t, AutoPlay = true, LoadFailed = false
            });
        }

        /* ============================================================
         * ReplayGain 2.0（懒分析调度随协调器走）
         * ============================================================ */

        /// <summary>应用当前曲目的回放增益（有缓存立即应用；无缓存后台懒分析）。</summary>
        void ApplyReplayGain(Track t)
        {
            if (!ReplayGainEnabled)
            {
                _player.SetReplayGain(1f);
                return;
            }
            TrackRow? row = _library.TryGet(t.FilePath);
            if (row != null && row.TrackGain.HasValue)
            {
                _player.SetReplayGain((float)Loudness.LinearFor(row.TrackGain.Value, row.TrackPeak ?? 1.0));
                return;
            }
            _player.SetReplayGain(1f);
            ScheduleLoudnessScan(t.FilePath);
        }

        /// <summary>后台懒分析响度并回写 DB；完成后若仍是当前曲目则实时应用。</summary>
        void ScheduleLoudnessScan(string path)
        {
            lock (_rgScanning)
            {
                if (_rgScanning.Contains(path)) return;
                _rgScanning.Add(path);
            }
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Loudness.Result? r = Loudness.AnalyzeFile(path);
                    if (r != null)
                    {
                        TrackRow? row = _library.TryGet(path)
                            ?? new TrackRow { Path = path, FileName = System.IO.Path.GetFileName(path) };
                        Library.GetFingerprint(path, out long bytes, out long mtime);
                        if (row.Bytes == 0) row.Bytes = bytes;
                        if (row.LastModified == 0) row.LastModified = mtime;
                        row.TrackGain = r.GainDb;
                        row.TrackPeak = r.Peak;
                        _library.Upsert(row);
                        MainViewModel.Dbg("RG scan done: " + path + " gain=" + r.GainDb.ToString("0.0") + "dB peak=" + r.Peak.ToString("0.00"));
                        _ui(() =>
                        {
                            if (ReplayGainEnabled && _current != null &&
                                string.Equals(_current.FilePath, path, StringComparison.OrdinalIgnoreCase))
                            {
                                _player.SetReplayGain((float)Loudness.LinearFor(r.GainDb, r.Peak));
                            }
                        });
                    }
                }
                catch (Exception ex) { MainViewModel.Dbg("RG scan FAIL: " + ex.Message); }
                finally
                {
                    lock (_rgScanning) { _rgScanning.Remove(path); }
                }
            });
        }

        /* ============================================================
         * 引擎事件（会话竞态第二道防线）
         * ============================================================ */

        void OnPlayerStateChanged(object? sender, PlaybackStateChangedEventArgs e)
        {
            _ui(() =>
            {
                bool playing = e.State == PlaybackState.Playing;
                RaisePlayState(playing);
            });
        }

        void OnPlaybackEnded(object? sender, PlaybackEndedEventArgs e)
        {
            // 后台线程立即快照"结束播放的会话 ID"。PlaybackEnded 经 UI 派发有延迟
            // （天然切歌→回调排队），若期间用户已点击切歌（引擎已 Load 新歌，
            // SessionId 递增），这是一次过期的自动切歌——必须丢弃，否则会覆盖用户的选择
            // （实测：连续点击列表切歌会被迟到的自动切歌抢走，播回随机曲目）。
            // 引擎侧已在 OnDeviceStopped 做过一轮会话过滤，这里是派发延迟的第二道防线。
            long endedSession = e.SessionId;
            MainViewModel.Dbg("PlaybackEnded bg: session=" + endedSession + " path=" + e.Path);
            _ui(() =>
            {
                bool stale = _player.SessionId != endedSession;
                MainViewModel.Dbg("PlaybackEnded ui: endedSession=" + endedSession + " currentSession=" + _player.SessionId + " stale=" + stale);
                if (stale)
                    return;   // 引擎已加载另一首（新会话）：本次自动切歌过期，忽略
                NextTrack(false);
            });
        }

        void SetCurrent(Track? t)
        {
            _current = t;
        }

        void RaisePlayState(bool playing)
        {
            var handler = PlayStateChanged;
            if (handler != null) handler(this, playing);
        }
    }
}
