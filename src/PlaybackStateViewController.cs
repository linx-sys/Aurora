#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * PlaybackStateViewController.cs — 播放状态视图控制（阶段 2 从 MainWindow 拆出）
 * 职责：播放/暂停图标与唱臂、音量 UI 与弹层、曲目信息（标题/歌手/黑胶封面）、
 *       模式图标联动、切歌编排（OnCurrentTrackChanged）、静音图标。
 * 交互原则：写入一律经 ViewModel（唯一数据源）；本类只做 View 呈现。
 * ============================================================ */
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    class PlaybackStateViewController
    {
        readonly Window win;
        readonly Grid root;
        readonly MainViewModel vm;
        readonly LyricsViewController lyrics;
        readonly PlaybackTickController tick;
        readonly SmTcController smtc;            // 可为 null（SMTC 初始化失败时）
        readonly NetMatchViewController netMatchView;
        readonly PlaylistViewController playlistView;
        readonly Action<string> toast;

        readonly TextBlock fpTitle, fpArtist, volLabelV;
        readonly System.Windows.Shapes.Rectangle fpTitleBar;
        readonly StackPanel fpLyricsPanel;
        readonly System.Windows.Shapes.Ellipse vinylDisc;
        readonly ImageBrush vinylCover;
        readonly RotateTransform armRotate;
        readonly Button btnPlay, btnMode, btnMute;
        readonly WPath icoPlay, icoPause, icoRepeat, icoShuffle, icoVol;
        readonly Grid icoOne, icoMuted;
        readonly Border volFillV;
        readonly Ellipse volThumbV;
        readonly Popup volPopup;

        DispatcherTimer volAutoCloseTimer;   // 音量弹层自动收起

        public PlaybackStateViewController(Window win, Grid root, MainViewModel vm,
            TextBlock fpTitle, TextBlock fpArtist, System.Windows.Shapes.Rectangle fpTitleBar,
            StackPanel fpLyricsPanel, System.Windows.Shapes.Ellipse vinylDisc, ImageBrush vinylCover,
            RotateTransform armRotate, Button btnPlay,
            WPath icoPlay, WPath icoPause, WPath icoRepeat, Grid icoOne, WPath icoShuffle, Button btnMode,
            Border volFillV, Ellipse volThumbV, TextBlock volLabelV, Popup volPopup,
            WPath icoVol, Grid icoMuted, Button btnMute,
            LyricsViewController lyrics, PlaybackTickController tick, SmTcController smtc,
            NetMatchViewController netMatchView, PlaylistViewController playlistView,
            Action<string> toast)
        {
            this.win = win;
            this.root = root;
            this.vm = vm;
            this.fpTitle = fpTitle;
            this.fpArtist = fpArtist;
            this.fpTitleBar = fpTitleBar;
            this.fpLyricsPanel = fpLyricsPanel;
            this.vinylDisc = vinylDisc;
            this.vinylCover = vinylCover;
            this.armRotate = armRotate;
            this.btnPlay = btnPlay;
            this.icoPlay = icoPlay;
            this.icoPause = icoPause;
            this.icoRepeat = icoRepeat;
            this.icoOne = icoOne;
            this.icoShuffle = icoShuffle;
            this.btnMode = btnMode;
            this.volFillV = volFillV;
            this.volThumbV = volThumbV;
            this.volLabelV = volLabelV;
            this.volPopup = volPopup;
            this.icoVol = icoVol;
            this.icoMuted = icoMuted;
            this.btnMute = btnMute;
            this.lyrics = lyrics;
            this.tick = tick;
            this.smtc = smtc;
            this.netMatchView = netMatchView;
            this.playlistView = playlistView;
            this.toast = toast;
        }

        /// <summary>挂接 VM 事件（属性变更 → 图标/音量联动；切歌 → 编排刷新）。</summary>
        public void Attach()
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            vm.CurrentTrackChanged += OnCurrentTrackChanged;
        }

        /// <summary>初始状态（构造后调用一次）。</summary>
        public void InitInitialState()
        {
            SetPlaying(false);
            UpdateTrackInfo(null);
        }

        /// <summary>窗口关闭时停掉本控制器的计时器。</summary>
        public void StopTimers()
        {
            try { volAutoCloseTimer?.Stop(); } catch { }
        }

        /* ============================================================
         * VM 属性变更 → UI 联动
         * ============================================================ */

        void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(vm.IsPlaying))
            {
                // 统一走 SetPlaying：图标/唱臂动画/播放键阴影/任务栏/SMTC 全联动
                SetPlaying(vm.IsPlaying);
            }
            else if (e.PropertyName == nameof(vm.Mode))
            {
                int m = (int)vm.Mode;
                icoRepeat.Visibility = (m == 0) ? Visibility.Visible : Visibility.Collapsed;
                icoOne.Visibility = (m == 1) ? Visibility.Visible : Visibility.Collapsed;
                icoShuffle.Visibility = (m == 2) ? Visibility.Visible : Visibility.Collapsed;
                string[] modeNames = { "列表循环", "单曲循环", "随机播放" };
                btnMode.ToolTip = "播放模式：" + modeNames[m];
            }
            else if (e.PropertyName == nameof(vm.Volume))
            {
                // 快捷键 ↑/↓ 等途径改音量时同步 UI（填充条/滑块/数值）
                ApplyVolumeUi(vm.Volume);
            }
            else if (e.PropertyName == nameof(vm.SearchText))
            {
                // 搜索过滤由 PlaylistManager.SearchText setter 即时批量刷新，这里只管占位提示显隐
                playlistView.UpdateSearchHint();
            }
            else if (e.PropertyName == nameof(vm.IsMuted))
            {
                UpdateMuteIcon();
            }
        }

        /// <summary>静音状态切换图标：正常喇叭 ↔ 带叉喇叭（比单纯降透明度直观）。</summary>
        public void UpdateMuteIcon()
        {
            if (icoVol == null || icoMuted == null) return;
            bool muted = vm.IsMuted;
            icoMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            icoVol.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            btnMute.ToolTip = muted ? "取消静音 (M)" : "音量 / 静音 (M)";
        }

        /// <summary>音量弹层（快捷键调节时自动弹出并延时收起，让用户看到数值变化）。</summary>
        public void ShowVolumePopupTemporarily()
        {
            volPopup.IsOpen = true;
            if (volAutoCloseTimer == null)
            {
                volAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                volAutoCloseTimer.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s).Stop();
                    if (volPopup.IsOpen && !volPopup.IsMouseOver) volPopup.IsOpen = false;
                };
            }
            volAutoCloseTimer.Stop();
            volAutoCloseTimer.Start();
        }

        /* ============================================================
         * 切歌编排（自 ViewModel.CurrentTrackChanged）
         * ============================================================ */

        void OnCurrentTrackChanged(object sender, CurrentTrackChangedEventArgs e)
        {
            MainViewModel.Dbg("OnCurrentTrackChanged: " + (e.Track != null ? e.Track.Title : "null") + " failed=" + e.LoadFailed);
            if (e.LoadFailed)
            {
                UpdateTrackInfo(null);
                lyrics.Render(null);
                // 复位残留状态：窗口标题/时间/进度条不能停留在上一首
                win.Title = "Aurora · 极光音乐";
                vm.TotalTimeText = "0:00";
                vm.CurrentTimeText = "0:00";
                tick.SetSeekUi(0, false);
                toast(e.FailMessage);
                return;
            }
            Track t = e.Track;
            if (t == null)
            {
                // 删除当前曲等场景：完整复位界面，不留上一首的标题/时间/进度残影
                UpdateTrackInfo(null);
                lyrics.Render(null);
                win.Title = "Aurora · 极光音乐";
                vm.TotalTimeText = "0:00";
                vm.CurrentTimeText = "0:00";
                tick.SetSeekUi(0, false);
                tick.UpdateTaskbarProgress();
                return;
            }
            if (t.Duration > TimeSpan.Zero)
            {
                t.RefreshDurationText();
                vm.TotalTimeText = UiUtil.FmtTime(t.Duration);
            }
            UpdateTrackInfo(t);
            lyrics.Render(t);
            netMatchView.TryNetMatch(t);
            if (smtc != null) smtc.UpdateMetadata(t);   // P2-5：系统媒体浮层元数据
            vm.CurrentTimeText = "0:00";
            tick.SetSeekUi(0, false);
            win.Title = t.Title + (t.Artist.Length > 0 ? " - " + t.Artist : "") + " · Aurora";

            // 切歌过渡：标题/歌手/黑胶封面/歌词区柔和淡入，替代生硬的内容跳变
            UiUtil.FadeIn(fpTitle);
            UiUtil.FadeIn(fpArtist);
            UiUtil.FadeIn(vinylDisc);
            UiUtil.FadeIn(fpLyricsPanel);

            // 列表面板打开时，高亮跟随当前播放曲（含自动切歌），并滚动到可见处；
            // SelectionChanged 里 t == current 判断保证此回写不会二次触发切歌
            playlistView.HighlightCurrent();
            tick.UpdateTaskbarProgress();
        }

        /* ============================================================
         * 音量 UI
         * ============================================================ */

        /// <summary>设置音量（唯一数据源：ViewModel；随后刷新本类 UI）。</summary>
        public void ApplyVolume(double v)
        {
            vm.Volume = v;
            ApplyVolumeUi(v);
        }

        /// <summary>仅刷新音量 UI（填充条/滑块/数值），不回写引擎/ViewModel。</summary>
        public void ApplyVolumeUi(double v)
        {
            // 竖直音量条（弹出层内）
            // 用固定 130（XAML 中轨道 Grid 高度），不能用 VolHitV.ActualHeight——
            // 弹窗未打开时 ActualHeight=0，会把填充高度算成 0、thumb 位置算错，且弹窗打开后无人重算
            const double trackH = 130;
            double h = Math.Max(0, Math.Min(1, v)) * trackH;
            if (h < 2 && v > 0) h = 2;
            volFillV.Height = h;
            volThumbV.Margin = new Thickness(0, 0, 0, Math.Max(0, h - 6));
            volLabelV.Text = Math.Round(v * 100).ToString();
        }

        /* ============================================================
         * 播放状态 / 曲目信息 UI
         * ============================================================ */

        /// <summary>仅刷新播放状态 UI（图标/阴影/唱臂/任务栏/SMTC）；状态本身以 ViewModel.IsPlaying 为唯一数据源。</summary>
        public void SetPlaying(bool p)
        {
            icoPlay.Visibility = p ? Visibility.Collapsed : Visibility.Visible;
            icoPause.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
            // 播放按钮：播放中减弱阴影（视觉重心让给内容），暂停时全高亮吸引点击
            // 注意：模板中的 DropShadowEffect 已被 WPF 冻结，不能直接改属性，需新建实例赋值
            var bg = btnPlay.Template.FindName("Bg", btnPlay) as Ellipse;
            if (bg != null)
            {
                bg.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Color.FromRgb(0x4C, 0xC9, 0xF0),
                    BlurRadius = p ? 10 : 14,
                    ShadowDepth = 0,
                    Opacity = p ? 0.25 : 0.45,
                };
            }
            // 唱臂：播放搭在盘面上，暂停移出盘面
            try
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    p ? 0 : -38, TimeSpan.FromMilliseconds(280));
                anim.EasingFunction = new System.Windows.Media.Animation.QuadraticEase();
                armRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            catch { }
            // 任务栏进度颜色随播放状态切换（绿/黄）；同步系统媒体浮层（SMTC）状态
            tick.UpdateTaskbarProgress();
            if (smtc != null) smtc.UpdateStatus(p);
        }

        public void UpdateTrackInfo(Track t)
        {
            if (t == null)
            {
                fpTitle.Text = "未在播放";
                fpArtist.Text = "双击 MP3 文件、或将音乐直接拖进窗口";
                fpTitle.Foreground = (Brush)root.TryFindResource("Dim");
                fpArtist.Foreground = (Brush)root.TryFindResource("Dim2");
                if (fpTitleBar != null) fpTitleBar.Visibility = Visibility.Collapsed;
                return;
            }
            fpTitle.Text = t.Title;
            fpArtist.Text = t.Artist.Length > 0 ? t.Artist : "未知歌手";
            fpTitle.Foreground = (Brush)root.TryFindResource("Text");
            fpArtist.Foreground = (Brush)root.TryFindResource("Dim");
            if (fpTitleBar != null) fpTitleBar.Visibility = Visibility.Visible;

            // 当前歌词高亮色 + 标题装饰条色，取自歌曲专属配色（与黑胶彩胶呼应）
            Color accent = lyrics.SetAccent(CoverArt.PaletteFor(t.Title, t.Artist)[0]);
            if (fpTitleBar != null) fpTitleBar.Fill = new SolidColorBrush(accent) { Opacity = 0.7 };

            try { vinylCover.ImageSource = CoverArt.ForTrack(t, 512); } catch { }
        }
    }
}
