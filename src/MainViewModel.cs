/* ============================================================
 * MainViewModel.cs — 主视图模型（MVVM 核心）
 * 阶段 3 收敛后职责：可观察 UI 状态 + 命令转发 + CurrentTrackChanged 事件源。
 * 播放业务（PlayTrack/Next/Prev/预载/跨淡/ReplayGain/会话竞态过滤）
 * 已全部迁至 PlaybackCoordinator，本类不再包含任何播放流程判断；
 * 音量/静音为纯 UI 状态透传（无流程逻辑），经 IPlaybackService 接口设置。
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
    /// 主视图模型。UI 通过 Binding 绑定属性，通过 ICommand 触发操作；
    /// 播放操作一律转发给 PlaybackCoordinator（唯一播放业务入口）。
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly IPlaybackService _player;   // 仅音量/静音透传（UI 状态，无流程逻辑）
        private readonly PlaybackCoordinator _coordinator;
        private readonly PlaylistManager _playlist;

        /// <summary>当前曲目变更事件（转发自协调器；UI 层订阅以更新歌词/封面/标题等）。</summary>
        public event EventHandler<CurrentTrackChangedEventArgs> CurrentTrackChanged;

        private bool _isPlaying;
        private double _volume;
        private string _currentTimeText = "0:00";
        private string _totalTimeText = "0:00";
        private double _seekRatio;
        private bool _isMuted;
        private double _savedVolume = 0.8;

        public MainViewModel(IPlaybackService player, ILibraryStore library)
        {
            _player = player ?? throw new ArgumentNullException(nameof(player));
            _playlist = new PlaylistManager();

            // 捕获 UI 线程 Dispatcher。
            // 注意：不能用 SynchronizationContext.Current——.NET Core/10 的 WPF 在
            // app.Run() 之前不会安装 DispatcherSynchronizationContext，此时 Current
            // 为 null，兜底 new SynchronizationContext() 的 Post 会直接在线程池执行，
            // 导致"调用线程无法访问此对象"跨线程闪退（切歌必经此路径）。
            var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            _volume = 0.8;

            // 播放协调器（唯一播放业务入口）；引擎后台回调经 Dispatcher 切回 UI 线程
            _coordinator = new PlaybackCoordinator(player, library, _playlist,
                action => dispatcher.BeginInvoke(action));
            _coordinator.CurrentTrackChanged += OnCoordinatorCurrentTrackChanged;
            _coordinator.PlayStateChanged += (s, playing) => IsPlaying = playing;

            InitCommands();
        }

        /* ============================================================
         * 公共属性
         * ============================================================ */

        /// <summary>当前曲目（唯一事实源在协调器；随 CurrentTrackChanged 事件同步通知）。</summary>
        public Track CurrentTrack { get { return _coordinator.CurrentTrack; } }

        public bool HasCurrentTrack { get { return _coordinator.CurrentTrack != null; } }
        public string Title { get { return _coordinator.CurrentTrack != null ? _coordinator.CurrentTrack.Title : ""; } }
        public string Artist { get { return _coordinator.CurrentTrack != null ? _coordinator.CurrentTrack.Artist : ""; } }

        public bool IsPlaying
        {
            get { return _isPlaying; }
            set { SetProperty(ref _isPlaying, value); }
        }

        public PlayMode Mode
        {
            get { return _coordinator.Mode; }
            set { _coordinator.Mode = value; OnPropertyChanged(); }
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

        System.Windows.Threading.DispatcherTimer _searchDebounce;   // 搜索防抖（P2-5）
        string _searchRaw;

        public string SearchText
        {
            get { return _searchRaw ?? _playlist.SearchText; }
            set
            {
                _searchRaw = value ?? "";
                // P2-5 搜索防抖：按键输入 200ms 合并后批量刷新（原实现每键全量 O(n log n)）；
                // 清空走立即路径——换目录等场景需要立刻看到全量列表
                if (_searchRaw.Length == 0)
                {
                    if (_searchDebounce != null) _searchDebounce.Stop();
                    _playlist.SearchText = "";
                    OnPropertyChanged();
                    return;
                }
                if (_searchDebounce == null)
                {
                    _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                    _searchDebounce.Tick += (s, e) =>
                    {
                        ((System.Windows.Threading.DispatcherTimer)s).Stop();
                        if (_playlist.SearchText != _searchRaw)
                        {
                            _playlist.SearchText = _searchRaw;
                            OnPropertyChanged();
                        }
                    };
                }
                _searchDebounce.Stop();
                _searchDebounce.Start();
            }
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

        public PlaybackController Playback { get { return _coordinator.Playback; } }
        public PlaylistManager Playlist { get { return _playlist; } }

        /// <summary>播放列表（委托给 PlaylistManager）。</summary>
        public ObservableCollection<Track> Tracks => _playlist.Tracks;

        /// <summary>筛选后的视图（委托给 PlaylistManager）。</summary>
        public ObservableCollection<Track> View => _playlist.View;

        /* ============================================================
         * 命令（全部转发协调器 / 本类纯 UI 状态）
         * ============================================================ */

        public ICommand TogglePlayCommand { get; private set; }
        public ICommand NextCommand { get; private set; }
        public ICommand PrevCommand { get; private set; }
        public ICommand ToggleModeCommand { get; private set; }
        public ICommand ToggleMuteCommand { get; private set; }
        public ICommand DeleteTrackCommand { get; private set; }
        public ICommand PlayTrackCommand { get; private set; }

        private void InitCommands()
        {
            TogglePlayCommand = new RelayCommand(() => _coordinator.TogglePlay(), () => HasCurrentTrack);
            NextCommand = new RelayCommand(() => NextTrack(true), () => Tracks.Count > 0);
            PrevCommand = new RelayCommand(() => PrevTrack(), () => Tracks.Count > 0);
            ToggleModeCommand = new RelayCommand(() => Mode = _coordinator.ToggleMode());
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

        /* ============================================================
         * 播放操作（转发协调器；不含任何流程判断）
         * ============================================================ */

        /// <summary>播放指定曲目。</summary>
        public void PlayTrack(Track t, bool autoplay = true)
        {
            _coordinator.PlayTrack(t, autoplay);
        }

        /// <summary>播放指定曲目。recordHistory=false 用于沿随机轨迹回退/前进（不重复入栈）。</summary>
        public void PlayTrack(Track t, bool autoplay, bool recordHistory)
        {
            _coordinator.PlayTrack(t, autoplay, recordHistory);
        }

        public void NextTrack(bool manual) { _coordinator.NextTrack(manual); }

        public void PrevTrack() { _coordinator.PrevTrack(); }

        public void DeleteTrack(Track t) { _coordinator.DeleteTrack(t); }

        public void SeekTo(double ratio) { _coordinator.SeekTo(ratio); }

        /// <summary>临结束巡检（PlaybackTickController 每帧调用；预载/跨淡决策在协调器）。</summary>
        public void PreloadNextIfNearEnd() { _coordinator.PreloadNextIfNearEnd(); }

        /// <summary>刷新筛选视图（委托给 PlaylistManager）。</summary>
        public void RefreshView() => _playlist.RefreshView();

        /// <summary>清空随机播放轨迹（切换目录时调用）。</summary>
        public void ResetShuffleHistory() => _coordinator.ResetShuffleHistory();

        /* ============================================================
         * 协调器事件桥（VM → UI 绑定）
         * ============================================================ */

        void OnCoordinatorCurrentTrackChanged(object sender, CurrentTrackChangedEventArgs e)
        {
            // CurrentTrack 事实源在协调器；这里同步派生属性并转发事件给 UI 层
            OnPropertyChanged(nameof(CurrentTrack));
            OnPropertyChanged(nameof(HasCurrentTrack));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Artist));
            CurrentTrackChanged?.Invoke(this, e);
        }

        internal static void Dbg(string msg)
        {
            Logger.Dbg(msg);   // P3：统一到 %LOCALAPPDATA%\Aurora\Logs\aurora.log（带轮转）
        }
    }
}
