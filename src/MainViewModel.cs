/* ============================================================
 * MainViewModel.cs — 主视图模型（MVVM 核心）
 * 统筹播放引擎、播放列表、播放状态、音量、模式等。
 * 业务逻辑已拆分到 PlaybackController（播放模式状态机）
 * 和 PlaylistManager（列表管理），本类负责协调与 UI Binding。
 * 所有 PlayerEngine 后台线程回调均通过 WPF Dispatcher 切回 UI 线程。
 * ============================================================ */
using System;
using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Aurora
{
    /// <summary>播放模式。</summary>
    public enum PlayMode { ListRepeat = 0, SingleRepeat = 1, Shuffle = 2 }

    /// <summary>当前曲目变更事件参数。</summary>
    public class CurrentTrackChangedEventArgs : EventArgs
    {
        public Track Track { get; set; }
        public bool AutoPlay { get; set; }
        public bool LoadFailed { get; set; }
        public string FailMessage { get; set; }
    }

    /// <summary>
    /// 主视图模型。协调播放引擎、播放列表控制器、播放模式控制器。
    /// UI 通过 Binding 绑定属性，通过 ICommand 触发操作。
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly PlayerEngine _player;
        private readonly PlaybackController _playback;
        private readonly PlaylistManager _playlist;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;

        /// <summary>当前曲目变更事件（UI 层订阅以更新歌词/封面/标题等）。</summary>
        public event EventHandler<CurrentTrackChangedEventArgs> CurrentTrackChanged;
        private Track _currentTrack;
        private bool _isPlaying;
        private double _volume;
        private string _currentTimeText = "0:00";
        private string _totalTimeText = "0:00";
        private double _seekRatio;
        private bool _isMuted;
        private double _savedVolume = 0.8;

        public MainViewModel(PlayerEngine player)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _playback = new PlaybackController();
            _playlist = new PlaylistManager();
            // 捕获 UI 线程 Dispatcher。
            // 注意：不能用 SynchronizationContext.Current——.NET Core/10 的 WPF 在
            // app.Run() 之前不会安装 DispatcherSynchronizationContext，此时 Current
            // 为 null，兜底 new SynchronizationContext() 的 Post 会直接在线程池执行，
            // 导致"调用线程无法访问此对象"跨线程闪退（切歌必经此路径）。
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _volume = 0.8;

            // 订阅播放引擎事件（后台线程触发，通过 _dispatcher 切回 UI 线程）
            _player.StateChanged += OnPlayerStateChanged;
            _player.PlaybackEnded += OnPlaybackEnded;

            InitCommands();
        }

        #region 公共属性

        public Track CurrentTrack
        {
            get { return _currentTrack; }
            set
            {
                if (SetProperty(ref _currentTrack, value))
                {
                    OnPropertyChanged(nameof(HasCurrentTrack));
                    OnPropertyChanged(nameof(Title));
                    OnPropertyChanged(nameof(Artist));
                }
            }
        }

        public bool HasCurrentTrack { get { return _currentTrack != null; } }
        public string Title { get { return _currentTrack != null ? _currentTrack.Title : ""; } }
        public string Artist { get { return _currentTrack != null ? _currentTrack.Artist : ""; } }

        public bool IsPlaying
        {
            get { return _isPlaying; }
            set { SetProperty(ref _isPlaying, value); }
        }

        public PlayMode Mode
        {
            get { return _playback.Mode; }
            set { _playback.Mode = value; OnPropertyChanged(); }
        }

        public double Volume
        {
            get { return _volume; }
            set
            {
                if (SetProperty(ref _volume, Math.Max(0, Math.Min(1, value))))
                {
                    _player.Volume = (float)_volume;
                    if (_volume > 0) _savedVolume = _volume;
                    OnPropertyChanged(nameof(VolumePercent));
                }
            }
        }

        public int VolumePercent { get { return (int)(_volume * 100); } }

        /// <summary>静音前的音量（退出时持久化用；静音中 Volume 为 0，不能存 0）。</summary>
        public double SavedVolume { get { return _savedVolume; } }

        public bool IsMuted
        {
            get { return _isMuted; }
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    if (_isMuted) _player.Volume = 0;
                    else _player.Volume = (float)_volume;
                }
            }
        }

        public string SearchText
        {
            get { return _playlist.SearchText; }
            set { _playlist.SearchText = value ?? ""; OnPropertyChanged(); }
        }

        public int SortMode
        {
            get { return _playlist.SortMode; }
            set { _playlist.SortMode = value; OnPropertyChanged(); }
        }

        public string CurrentTimeText
        {
            get { return _currentTimeText; }
            set { SetProperty(ref _currentTimeText, value); }
        }

        public string TotalTimeText
        {
            get { return _totalTimeText; }
            set { SetProperty(ref _totalTimeText, value); }
        }

        public double SeekRatio
        {
            get { return _seekRatio; }
            set { SetProperty(ref _seekRatio, value); }
        }

        public PlayerEngine Player { get { return _player; } }
        public PlaybackController Playback { get { return _playback; } }
        public PlaylistManager Playlist { get { return _playlist; } }

        /// <summary>播放列表（委托给 PlaylistManager）。</summary>
        public ObservableCollection<Track> Tracks => _playlist.Tracks;

        /// <summary>筛选后的视图（委托给 PlaylistManager）。</summary>
        public ObservableCollection<Track> View => _playlist.View;

        #endregion

        #region 命令

        public ICommand TogglePlayCommand { get; private set; }
        public ICommand NextCommand { get; private set; }
        public ICommand PrevCommand { get; private set; }
        public ICommand ToggleModeCommand { get; private set; }
        public ICommand ToggleMuteCommand { get; private set; }
        public ICommand DeleteTrackCommand { get; private set; }
        public ICommand PlayTrackCommand { get; private set; }

        private void InitCommands()
        {
            TogglePlayCommand = new RelayCommand(() =>
            {
                if (IsPlaying) { _player.Pause(); IsPlaying = false; }
                else { _player.Play(); IsPlaying = true; }
            }, () => HasCurrentTrack);

            NextCommand = new RelayCommand(() => NextTrack(true), () => Tracks.Count > 0);
            PrevCommand = new RelayCommand(() => PrevTrack(), () => Tracks.Count > 0);

            ToggleModeCommand = new RelayCommand(() =>
            {
                Mode = _playback.ToggleMode();
            });

            ToggleMuteCommand = new RelayCommand(() =>
            {
                if (IsMuted) { IsMuted = false; Volume = _savedVolume; }
                else { _savedVolume = Volume; IsMuted = true; Volume = 0; }
            });

            DeleteTrackCommand = new RelayCommand(param =>
            {
                if (param is Track t) DeleteTrack(t);
            });

            PlayTrackCommand = new RelayCommand(param =>
            {
                if (param is Track t) PlayTrack(t);
            });
        }

        #endregion

        #region 播放控制逻辑

        /// <summary>播放指定曲目（同步加载，OGG 流式解码无磁盘 I/O）。</summary>
        public void PlayTrack(Track t, bool autoplay = true)
        {
            PlayTrack(t, autoplay, recordHistory: true);
        }

        /// <summary>
        /// 播放指定曲目。recordHistory=false 用于沿随机轨迹回退/前进的播放（不重复入栈）。
        /// </summary>
        public void PlayTrack(Track t, bool autoplay, bool recordHistory)
        {
            if (t == null) return;
            Dbg("PlayTrack: " + t.Title + " autoplay=" + autoplay + " record=" + recordHistory);
            if (recordHistory) _playback.RecordPlay(t);
            CurrentTrack = t;
            // PlayerEngine.Load 内部处理 OGG/OPUS 流式播放（VorbisWaveReader）
            bool loadOk = _player.Load(t.FilePath);
            if (!loadOk)
            {
                CurrentTrack = null;
                IsPlaying = false;
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
            if (autoplay) { _player.Play(); IsPlaying = true; }
            else IsPlaying = false;
            CurrentTrackChanged?.Invoke(this, new CurrentTrackChangedEventArgs
            {
                Track = t, AutoPlay = autoplay, LoadFailed = false
            });
        }

        public void NextTrack(bool manual)
        {
            if (Tracks.Count == 0) return;
            Track next;
            if (manual)
            {
                // 随机模式：优先沿播放轨迹"前进"（重播回退过的歌），无可前进再随机新曲
                if (_playback.Mode == PlayMode.Shuffle && _playback.TryGetShuffleNext(out next))
                {
                    PlayTrack(next, true, recordHistory: false);
                    return;
                }
                next = _playback.GetNextForManual(Tracks, CurrentTrack);
            }
            else
            {
                next = _playback.GetNextForAuto(Tracks, CurrentTrack);
                if (next == null) // 单曲循环：重新播放当前
                {
                    _player.Position = TimeSpan.Zero;
                    _player.Play();
                    return;
                }
            }
            if (next != null) PlayTrack(next, true);
        }

        public void PrevTrack()
        {
            if (Tracks.Count == 0) return;
            // 随机模式：真正回退刚听过的歌（播放轨迹），而不是简单索引-1
            if (_playback.Mode == PlayMode.Shuffle && _playback.TryGetShufflePrev(out Track prev))
            {
                PlayTrack(prev, true, recordHistory: false);
                return;
            }
            var prev2 = _playback.GetPrev(Tracks, CurrentTrack);
            if (prev2 != null) PlayTrack(prev2, true);
        }

        public void DeleteTrack(Track t)
        {
            if (t == null) return;
            bool wasCurrent = t == CurrentTrack;
            _playlist.Remove(t);
            if (wasCurrent)
            {
                _player.Stop();
                IsPlaying = false;
                CurrentTrack = null;
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

        /// <summary>刷新筛选视图（委托给 PlaylistManager）。</summary>
        public void RefreshView() => _playlist.RefreshView();

        /// <summary>清空随机播放轨迹（切换目录时调用）。</summary>
        public void ResetShuffleHistory() => _playback.ResetShuffleHistory();

        #endregion

        #region 播放引擎事件处理（UI 线程调度）

        internal static void Dbg(string msg)
        {
            try { System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aurora_debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Environment.CurrentManagedThreadId + "] " + msg + "\r\n"); }
            catch { }
        }

        /// <summary>在 UI 线程执行操作（PlayerEngine 后台回调统一入口）。</summary>
        private void RunOnUiThread(Action action)
        {
            if (_dispatcher == null) action();
            else _dispatcher.BeginInvoke(action);
        }

        private void OnPlayerStateChanged(object sender, PlaybackStateChangedEventArgs e)
        {
            RunOnUiThread(() =>
            {
                if (e.State == PlaybackState.Playing) IsPlaying = true;
                else if (e.State == PlaybackState.Paused) IsPlaying = false;
                else if (e.State == PlaybackState.Stopped) IsPlaying = false;
            });
        }

        private void OnPlaybackEnded(object sender, PlaybackEndedEventArgs e)
        {
            // 后台线程立即快照"结束播放的会话 ID"。PlaybackEnded 经 BeginInvoke 派发到 UI
            // 线程有延迟（天然切歌→回调排队），若期间用户已点击切歌（引擎已 Load 新歌，
            // SessionId 递增），这是一次过期的自动切歌——必须丢弃，否则会覆盖用户的选择
            // （实测：连续点击列表切歌会被迟到的自动切歌抢走，播回随机曲目）。
            // 引擎侧已在 OnPlaybackStopped 做过一轮会话过滤，这里是派发延迟的第二道防线。
            long endedSession = e.SessionId;
            Dbg("PlaybackEnded bg: session=" + endedSession + " path=" + e.Path);
            RunOnUiThread(() =>
            {
                bool stale = _player.SessionId != endedSession;
                Dbg("PlaybackEnded ui: endedSession=" + endedSession + " currentSession=" + _player.SessionId + " stale=" + stale);
                if (stale)
                    return;   // 引擎已加载另一首（新会话）：本次自动切歌过期，忽略
                NextTrack(false);
            });
        }

        #endregion
    }
}
