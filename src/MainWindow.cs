/* ============================================================
 * MainWindow.cs — Aurora 播放页（唯一界面）
 * 黑胶唱片 + 居左歌词 + 底部控制条；双击 MP3 / 拖放 / 文件夹导入
 * 职责收敛：仅保留装配根（FindControls）、VM→UI 唯一桥（OnVmPropertyChanged）、
 * 播放状态 UI（图标/音量/曲目信息）与事件薄委托。
 * Toast/快捷键/进度计时/联网匹配/窗口杂项已拆至对应 Controller。
 * ============================================================ */
using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    public class MainWindow
    {
        public Window Win;
        Grid root;
        IPlaybackService Player;
        MainViewModel ViewModel;
        Button BtnOpenFolder, BtnMin, BtnClose, BtnMode, BtnPlay, BtnMute, BtnTheme, BtnNet, BtnList;
        WPath IcoRepeat, IcoShuffle; Grid IcoOne;
        WPath IcoPlay, IcoPause, IcoMoon, IcoSun;
        TextBlock FpTitle, FpArtist, VolLabelV;
        System.Windows.Shapes.Rectangle FpTitleBar;
        StackPanel FpLyricsPanel;
        Border VolFillV; Grid VolHitV, FullPlayer;
        Ellipse VolThumbV;
        WPath IcoVol; Grid IcoMuted;
        System.Windows.Shapes.Ellipse VinylDisc; ImageBrush VinylCover;
        RotateTransform ArmRotate;
        Popup VolPopup;
        DispatcherTimer volAutoCloseTimer;   // 音量弹层自动收起

        LyricsViewController Lyrics;   // 歌词渲染控制器（渲染/高亮/滚动/配色）
        PlaylistViewController PlaylistView;   // 播放列表视图控制器（抽屉/拖动排序/删除/计数）
        LibraryImportController Importer;   // 媒体库导入控制器（扫描/拖放导入/播放恢复）
        ThemeController Theme;

        // ===== 拆分出的视图控制器 =====
        ToastController ToastCtrl;           // Toast 提示条
        PlaybackTickController Tick;         // 33ms 进度/黑胶/任务栏计时（含进度条拖动）
        HotkeyController Hotkey;             // 全局快捷键
        NetMatchViewController NetMatchView; // 联网匹配歌词/封面
        WindowMiscController Misc;           // 窗口图标/单实例/更新检查/外部文件
        SmTcController SmTc;                 // 系统媒体传输控制（媒体键/锁屏浮层，P2-5）

        // 播放状态只读代理：唯一数据源在 ViewModel，写入必须经由它
        Track current { get { return ViewModel.CurrentTrack; } }

        public MainWindow(string openFile)
        {
            Win = new Window();
            Win.Title = "Aurora · 极光音乐";
            Win.Width = 1180; Win.Height = 760;
            Win.MinWidth = 860; Win.MinHeight = 580;
            Win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Win.WindowStyle = WindowStyle.None;
            Win.ResizeMode = ResizeMode.CanResize;
            Win.UseLayoutRounding = true;
            Win.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI, Microsoft YaHei");
            Win.SourceInitialized += (s, e) =>
            {
                WindowChromeController.EnableRoundedCorners(Win);
                // 挂 Win32 消息钩子：最大化时限制为工作区，不盖任务栏
                WindowChromeController.Attach(Win);
            };

            var chrome = new System.Windows.Shell.WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            System.Windows.Shell.WindowChrome.SetWindowChrome(Win, chrome);

            // P3 完成：ui.xaml 编译为 BAML，使用 Application.LoadComponent 加载（启动提速 + 编译期校验）
            root = (Grid)Application.LoadComponent(new Uri("/AuroraPlayer;component/src/ui.xaml", UriKind.Relative));
            Win.Content = root;
            FindControls();

            Theme.Apply(Settings.Get("theme", "dark") != "light");

            HookEvents();
            // 播放模式/音量从设置恢复：直接写入 ViewModel（唯一数据源，
            // 图标/Tooltip/音量条由 OnVmPropertyChanged 联动刷新）
            ViewModel.Mode = (PlayMode)UiUtil.ParseInt(Settings.Get("mode", "0"));
            ApplyVolume(UiUtil.Clamp(UiUtil.ParseDouble(Settings.Get("volume", "0.8")), 0, 1));
            SetPlaying(false);
            UpdateTrackInfo(null);

            // 任务栏进度条（Windows 播放器惯例：播放中绿色进度/暂停黄色）
            Win.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo
            {
                ProgressState = System.Windows.Shell.TaskbarItemProgressState.None
            };

            Tick.Start();

            Win.Loaded += (s, e) =>
            {
                WindowChromeController.EnableRoundedCorners(Win);
                Misc.StartPipeServer();
                Misc.StartUpdateCheck();
                if (!string.IsNullOrEmpty(openFile) && File.Exists(openFile))
                {
                    string dir = LibraryImportController.SafeDir(openFile);
                    if (dir != null) Importer.LoadDirectory(dir, openFile, false);
                }
                else
                {
                    string lastDir = Settings.Get("lastDir", "");
                    if (lastDir.Length > 0 && Directory.Exists(lastDir))
                        Importer.LoadDirectory(lastDir, null, true);
                    else
                        Importer.PickFolder(); // 首次使用：选择音乐文件夹
                }
            };
        }

        /* ============================================================
         * 装配
         * ============================================================ */

        T F<T>(string name) where T : class { return (T)root.FindName(name); }

        void FindControls()
        {
            Player = new PlayerEngine();
            // 音乐库正式核心存储（P1-3）：VM（RG 回写/查询）与导入控制器（秒开+差分同步）共享同一实例
            ILibraryStore library = new LibraryDatabase(LibraryDatabase.DefaultPath);
            ViewModel = new MainViewModel(Player, library);
            Win.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnVmPropertyChanged;
            ViewModel.CurrentTrackChanged += OnCurrentTrackChanged;
            BtnOpenFolder = F<Button>("BtnOpenFolder");
            BtnMin = F<Button>("BtnMin");
            BtnClose = F<Button>("BtnClose");
            BtnMode = F<Button>("BtnMode");
            BtnPlay = F<Button>("BtnPlay");
            BtnMute = F<Button>("BtnMute");
            BtnTheme = F<Button>("BtnTheme");
            BtnNet = F<Button>("BtnNet");
            IcoRepeat = F<WPath>("IcoRepeat");
            IcoShuffle = F<WPath>("IcoShuffle");
            IcoOne = F<Grid>("IcoOne");
            IcoPlay = F<WPath>("IcoPlay");
            IcoPause = F<WPath>("IcoPause");
            IcoMoon = F<WPath>("IcoMoon");
            IcoSun = F<WPath>("IcoSun");
            FpTitle = F<TextBlock>("FpTitle");
            FpArtist = F<TextBlock>("FpArtist");
            FpTitleBar = F<Rectangle>("FpTitleBar");
            FpLyricsPanel = F<StackPanel>("FpLyricsPanel");
            FpLyricsScroll = F<ScrollViewer>("FpLyricsScroll");
            VolLabelV = F<TextBlock>("VolLabelV");
            VolFillV = F<Border>("VolFillV");
            VolThumbV = F<System.Windows.Shapes.Ellipse>("VolThumbV");
            VolHitV = F<Grid>("VolHitV");
            VolPopup = F<Popup>("VolPopup");
            IcoVol = F<WPath>("IcoVol");
            IcoMuted = F<Grid>("IcoMuted");
            BtnList = F<Button>("BtnList");
            FullPlayer = F<Grid>("FullPlayer");
            VinylDisc = F<System.Windows.Shapes.Ellipse>("VinylDisc");
            ArmRotate = F<RotateTransform>("ArmRotate");
            VinylCover = F<ImageBrush>("VinylCover");

            // 歌词渲染控制器：View 行为（滚动/点击跳转/配色）独立于 ViewModel
            Lyrics = new LyricsViewController(root, FpLyricsPanel, FpLyricsScroll, Player,
                isDark: () => Theme.IsDark,
                isListOpen: () => PlaylistView.IsOpen,
                closeListPanel: () => PlaylistView.Toggle());

            // 播放列表视图控制器：抽屉/拖动排序/删除/排序切换/计数
            PlaylistView = new PlaylistViewController(
                F<Button>("BtnSort"), F<ListBox>("ListList"), F<Border>("ListPanel"),
                F<StackPanel>("CapBar"), F<TextBox>("SearchBox"), F<TextBlock>("SearchHint"),
                F<TextBlock>("ListCountTb"), ViewModel, Player, Toast);

            // 媒体库导入控制器：DB 优先秒开 + 差分同步/导入/播放恢复
            Importer = new LibraryImportController(Win, ViewModel, library, Toast,
                (t, auto) => ViewModel.PlayTrack(t, auto));

            // 主题控制器：切主题后联动歌词重染
            Theme = new ThemeController(root, Win, IcoMoon, IcoSun, onApplied: () => Lyrics.OnThemeChanged());

            // ===== P0 精简拆分出的控制器 =====
            ToastCtrl = new ToastController(F<Border>("ToastCard"), F<TextBlock>("ToastTb"));

            NetMatchView = new NetMatchViewController(Win, ViewModel, Lyrics,
                updateTrackInfo: UpdateTrackInfo, toast: Toast, isDark: () => Theme.IsDark);

            Misc = new WindowMiscController(Win, ViewModel, Importer, Toast);
            Misc.SetWinIcons(F<WPath>("IcoMax"), F<WPath>("IcoRestore"));

            Hotkey = new HotkeyController(Win, ViewModel, Player,
                isListOpen: () => PlaylistView.IsOpen,
                toggleList: () => PlaylistView.Toggle(),
                showVolumePopup: ShowVolumePopupTemporarily);

            // 33ms 进度/黑胶/任务栏计时（含进度条拖动交互）整体迁移
            Tick = new PlaybackTickController(Win, ViewModel, Player, Lyrics,
                F<Grid>("SeekHit"), F<Border>("SeekFill"), F<Border>("SeekTrackBg"),
                F<System.Windows.Shapes.Ellipse>("SeekThumb"),
                F<TextBlock>("CurTime"), F<TextBlock>("DurTime"),
                F<RotateTransform>("VinylRotate"));

            // 系统媒体传输控制：锁屏/系统浮层 + 媒体键（失败静默禁用；
            // try 兜到 JIT 级缺失——极端情况下投影 DLL 不在也不影响启动）
            try
            {
                SmTc = new SmTcController(Win, ViewModel);
                SmTc.Attach();
            }
            catch (Exception smtcEx) { MainViewModel.Dbg("SMTC init FAIL: " + smtcEx.Message); SmTc = null; }
        }

        ScrollViewer FpLyricsScroll;

        void HookEvents()
        {
            Win.Closing += (s, e) =>
            {
                try { Tick?.Stop(); } catch { }
                try { ToastCtrl?.Stop(); } catch { }
                try { volAutoCloseTimer?.Stop(); } catch { }
                try { Player?.Dispose(); } catch { }
                Settings.Set("volume", ViewModel.SavedVolume.ToString("0.00", CultureInfo.InvariantCulture));
                Settings.Set("mode", ((int)ViewModel.Mode).ToString());
                // 记住最后播放曲目（退出时一次写盘；此前每切一首歌写一次磁盘，纯属浪费）
                Settings.Set("lastTrack", current != null ? current.FilePath : "");
            };

            BtnMin.Click += (s, e) => Win.WindowState = WindowState.Minimized;
            BtnClose.Click += (s, e) => Win.Close();
            BtnTheme.Click += (s, e) => Theme.Toggle(Toast);
            BtnOpenFolder.Click += (s, e) => Importer.PickFolder();
            BtnNet.Click += (s, e) => NetMatchView.ShowSettings();

            // 音量：点击图标弹出竖直调节
            BtnMute.Click += (s, e) => { VolPopup.IsOpen = !VolPopup.IsOpen; };
            VolPopup.Closed += (s, e) => { /* 点外部自动收起 */ };

            // 播放列表面板（右侧内嵌，点击切换显隐，带滑入/滑出动画；
            // 排序/选中/拖动/计数等交互由 PlaylistViewController 接管）
            BtnList.Click += (s, e) => PlaylistView.Toggle();

            // 全屏切换
            F<Button>("BtnFull").Click += (s, e) =>
                Win.WindowState = Win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

            // 全屏/还原图标随窗口状态切换（最大化 → 还原双方框；还原 → 最大化单方框）
            Win.StateChanged += (s, e) => Misc.UpdateWinIcon();
            Misc.UpdateWinIcon();

            // 竖直音量条
            UiUtil.HookDragBar(VolHitV,
                down: ratio => ApplyVolume(ratio),
                move: ratio => ApplyVolume(ratio),
                up: ratio => ApplyVolume(ratio),
                vertical: true);

            // 播放控制按钮已通过 Command Binding 绑定到 ViewModel（TogglePlay/Next/Prev/ToggleMode）；
            // 进度条拖动已迁入 PlaybackTickController

            // 窗口拖动（黑胶与空白区；歌词/按钮除外）
            FullPlayer.MouseLeftButtonDown += (s, e) =>
            {
                // 播放列表面板打开时，点击左侧主区域 = 收起面板（抽屉交互惯例）。
                // 判定：相对面板坐标 X<0 即点击在面板左侧的主内容区；
                // 底栏（进度条/按钮）在面板正下方，X>=0 不受影响。
                // 歌词行的跳转由内层 LrcLineClick 先行处理，冒泡到这里时仅收起。
                if (PlaylistView.IsOpen && e.GetPosition(PlaylistView.PanelElement).X < 0)
                {
                    PlaylistView.Toggle();
                    return;
                }
                if (e.OriginalSource is TextBlock || e.OriginalSource is System.Windows.Controls.Button) return;
                if (e.ButtonState == MouseButtonState.Pressed) { try { Win.DragMove(); } catch { } }
            };

            // 拖放导入
            Win.AllowDrop = true;
            Win.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Win.Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                Importer.ImportPaths(files, true);
            };

            // 快捷键（已迁入 HotkeyController）
            Hotkey.Hook();
        }

        /// <summary>ViewModel 属性变更时更新 UI 图标（播放/暂停/模式）。</summary>
        void OnVmPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsPlaying))
            {
                // 统一走 SetPlaying：此前这里只切图标，playing 字段/黑胶旋转/唱臂动画/
                // 播放键阴影全部失联（playing 永远 false，黑胶不转、唱臂不落）
                SetPlaying(ViewModel.IsPlaying);
            }
            else if (e.PropertyName == nameof(ViewModel.Mode))
            {
                int m = (int)ViewModel.Mode;
                IcoRepeat.Visibility = (m == 0) ? Visibility.Visible : Visibility.Collapsed;
                IcoOne.Visibility = (m == 1) ? Visibility.Visible : Visibility.Collapsed;
                IcoShuffle.Visibility = (m == 2) ? Visibility.Visible : Visibility.Collapsed;
                string[] modeNames = { "列表循环", "单曲循环", "随机播放" };
                BtnMode.ToolTip = "播放模式：" + modeNames[m];
            }
            else if (e.PropertyName == nameof(ViewModel.Volume))
            {
                // 快捷键 ↑/↓ 等途径改音量时同步 UI（填充条/滑块/数值）
                ApplyVolumeUi(ViewModel.Volume);
            }
            else if (e.PropertyName == nameof(ViewModel.SearchText))
            {
                // 搜索过滤已由 PlaylistManager.SearchText setter 即时批量刷新（单次 Reset 通知），
                // 这里只负责占位提示显隐
                PlaylistView.UpdateSearchHint();
            }
            else if (e.PropertyName == nameof(ViewModel.IsMuted))
            {
                UpdateMuteIcon();
            }
        }

        /// <summary>静音状态切换图标：正常喇叭 ↔ 带叉喇叭（比单纯降透明度直观）。</summary>
        void UpdateMuteIcon()
        {
            if (IcoVol == null || IcoMuted == null) return;
            bool muted = ViewModel.IsMuted;
            IcoMuted.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
            IcoVol.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
            BtnMute.ToolTip = muted ? "取消静音 (M)" : "音量 / 静音 (M)";
        }

        /// <summary>音量弹层（快捷键调节时自动弹出并延时收起，让用户看到数值变化）。</summary>
        void ShowVolumePopupTemporarily()
        {
            VolPopup.IsOpen = true;
            if (volAutoCloseTimer == null)
            {
                volAutoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                volAutoCloseTimer.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s).Stop();
                    if (VolPopup.IsOpen && !VolPopup.IsMouseOver) VolPopup.IsOpen = false;
                };
            }
            volAutoCloseTimer.Stop();
            volAutoCloseTimer.Start();
        }

        /// <summary>ViewModel 当前曲目变更时更新 UI（歌词/封面/标题/联网匹配）。</summary>
        void OnCurrentTrackChanged(object sender, CurrentTrackChangedEventArgs e)
        {
            MainViewModel.Dbg("OnCurrentTrackChanged: " + (e.Track != null ? e.Track.Title : "null") + " failed=" + e.LoadFailed);
            if (e.LoadFailed)
            {
                UpdateTrackInfo(null);
                Lyrics.Render(null);
                // 复位残留状态：窗口标题/时间/进度条不能停留在上一首
                Win.Title = "Aurora · 极光音乐";
                ViewModel.TotalTimeText = "0:00";
                ViewModel.CurrentTimeText = "0:00";
                Tick.SetSeekUi(0, false);
                Toast(e.FailMessage);
                return;
            }
            Track t = e.Track;
            if (t == null)
            {
                // 删除当前曲等场景：完整复位界面，不留上一首的标题/时间/进度残影
                UpdateTrackInfo(null);
                Lyrics.Render(null);
                Win.Title = "Aurora · 极光音乐";
                ViewModel.TotalTimeText = "0:00";
                ViewModel.CurrentTimeText = "0:00";
                Tick.SetSeekUi(0, false);
                Tick.UpdateTaskbarProgress();
                return;
            }
            if (t.Duration > TimeSpan.Zero)
            {
                t.RefreshDurationText();
                ViewModel.TotalTimeText = UiUtil.FmtTime(t.Duration);
            }
            UpdateTrackInfo(t);
            Lyrics.Render(t);
            NetMatchView.TryNetMatch(t);
            SmTc.UpdateMetadata(t);   // P2-5：系统媒体浮层元数据
            ViewModel.CurrentTimeText = "0:00";
            Tick.SetSeekUi(0, false);
            Win.Title = t.Title + (t.Artist.Length > 0 ? " - " + t.Artist : "") + " · Aurora";

            // 切歌过渡：标题/歌手/黑胶封面/歌词区柔和淡入，替代生硬的内容跳变
            UiUtil.FadeIn(FpTitle);
            UiUtil.FadeIn(FpArtist);
            UiUtil.FadeIn(VinylDisc);
            UiUtil.FadeIn(FpLyricsPanel);

            // 列表面板打开时，高亮跟随当前播放曲（含自动切歌），并滚动到可见处；
            // SelectionChanged 里 t == current 判断保证此回写不会二次触发切歌
            PlaylistView.HighlightCurrent();
            Tick.UpdateTaskbarProgress();
        }

        /* ============================================================
         * 播放控制（业务逻辑唯一入口：ViewModel.PlayTrack）
         * ============================================================ */

        void ApplyVolume(double v)
        {
            // 唯一数据源：引擎音量与静音前记忆（SavedVolume）均由 ViewModel 维护
            ViewModel.Volume = v;
            ApplyVolumeUi(v);
        }

        /// <summary>仅刷新音量 UI（填充条/滑块/数值），不回写引擎/ViewModel。</summary>
        void ApplyVolumeUi(double v)
        {
            // 竖直音量条（弹出层内）
            // 用固定 130（XAML 中轨道 Grid 高度），不能用 VolHitV.ActualHeight——
            // 弹窗未打开时 ActualHeight=0，会把填充高度算成 0、thumb 位置算错，且弹窗打开后无人重算
            const double trackH = 130;
            double h = Math.Max(0, Math.Min(1, v)) * trackH;
            if (h < 2 && v > 0) h = 2;
            VolFillV.Height = h;
            VolThumbV.Margin = new Thickness(0, 0, 0, Math.Max(0, h - 6));
            VolLabelV.Text = Math.Round(v * 100).ToString();
        }

        /// <summary>仅刷新播放状态 UI（图标/阴影/唱臂/任务栏）；状态本身以 ViewModel.IsPlaying 为唯一数据源。</summary>
        void SetPlaying(bool p)
        {
            IcoPlay.Visibility = p ? Visibility.Collapsed : Visibility.Visible;
            IcoPause.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
            // 播放按钮：播放中减弱阴影（视觉重心让给内容），暂停时全高亮吸引点击
            // 注意：模板中的 DropShadowEffect 已被 WPF 冻结，不能直接改属性，需新建实例赋值
            var bg = BtnPlay.Template.FindName("Bg", BtnPlay) as Ellipse;
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
                ArmRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            catch { }
            // 任务栏进度颜色随播放状态切换（绿/黄）；同步系统媒体浮层（SMTC）状态
            Tick.UpdateTaskbarProgress();
            if (SmTc != null) SmTc.UpdateStatus(p);
        }

        void UpdateTrackInfo(Track t)
        {
            if (t == null)
            {
                FpTitle.Text = "未在播放";
                FpArtist.Text = "双击 MP3 文件、或将音乐直接拖进窗口";
                FpTitle.Foreground = (Brush)root.TryFindResource("Dim");
                FpArtist.Foreground = (Brush)root.TryFindResource("Dim2");
                if (FpTitleBar != null) FpTitleBar.Visibility = Visibility.Collapsed;
                return;
            }
            FpTitle.Text = t.Title;
            FpArtist.Text = t.Artist.Length > 0 ? t.Artist : "未知歌手";
            FpTitle.Foreground = (Brush)root.TryFindResource("Text");
            FpArtist.Foreground = (Brush)root.TryFindResource("Dim");
            if (FpTitleBar != null) FpTitleBar.Visibility = Visibility.Visible;

            // 当前歌词高亮色 + 标题装饰条色，取自歌曲专属配色（与黑胶彩胶呼应）
            Color accent = Lyrics.SetAccent(CoverArt.PaletteFor(t.Title, t.Artist)[0]);
            if (FpTitleBar != null) FpTitleBar.Fill = new SolidColorBrush(accent) { Opacity = 0.7 };

            try { VinylCover.ImageSource = CoverArt.ForTrack(t, 512); } catch { }
        }

        /// <summary>Toast 转发（控制器间经注入回调协作，不互相持有）。</summary>
        void Toast(string msg) { ToastCtrl.Toast(msg); }
    }
}
