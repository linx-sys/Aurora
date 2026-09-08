/* ============================================================
 * MainWindow.cs — Aurora 播放页（唯一界面）
 * 黑胶唱片 + 居左歌词 + 底部控制条；双击 MP3 / 拖放 / 文件夹导入
 * ============================================================ */
using System;
using System.Linq;
using System.ComponentModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;
using IOPath = System.IO.Path;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    public class MainWindow
    {
        public Window Win;
        Grid root;
        PlayerEngine Player;
        MainViewModel ViewModel;
        Button BtnOpenFolder, BtnMin, BtnClose, BtnMode, BtnPrev, BtnPlay, BtnNext, BtnMute, BtnTheme, BtnNet, BtnList, BtnFull;
        WPath IcoRepeat, IcoShuffle; Grid IcoOne;
        WPath IcoPlay, IcoPause, IcoMoon, IcoSun, IcoMax, IcoRestore;
        TextBlock FpTitle, FpArtist, CurTime, DurTime, VolLabelV, ToastTb;
        System.Windows.Shapes.Rectangle FpTitleBar;
        StackPanel FpLyricsPanel;
        Border SeekFill, VolFillV, ToastCard, SeekTrackBg; Grid SeekHit, VolHitV, FullPlayer;
        Ellipse SeekThumb, VolThumbV; ScrollViewer FpLyricsScroll;
        WPath IcoVol; Grid IcoMuted;
        System.Windows.Shapes.Ellipse VinylDisc; Grid VinylSpin;
        RotateTransform VinylRotate, ArmRotate; ImageBrush VinylCover;
        Popup VolPopup;
        DispatcherTimer volAutoCloseTimer;   // 音量弹层自动收起

        LyricsViewController Lyrics;   // 歌词渲染控制器（渲染/高亮/滚动/配色）
        PlaylistViewController PlaylistView;   // 播放列表视图控制器（抽屉/拖动排序/删除/计数）
        LibraryImportController Importer;   // 媒体库导入控制器（扫描/拖放导入/播放恢复）

        // ===== 播放状态只读视图（唯一数据源在 MainViewModel / PlaylistManager）=====
        // 此前本类与 ViewModel 各存一份 tracks/view/current/mode/volume/playing，
        // 靠 PropertyChanged 手工互相同步，属典型状态双写；现全部收敛为 VM 单源，
        // 这些标识符只是只读代理，任何写入都必须经由 ViewModel。
        PlaylistManager Playlist { get { return ViewModel.Playlist; } }
        ObservableCollection<Track> tracks { get { return ViewModel.Tracks; } }
        ObservableCollection<Track> view { get { return ViewModel.View; } }
        Track current { get { return ViewModel.CurrentTrack; } }
        bool playing { get { return ViewModel.IsPlaying; } }

        readonly HashSet<string> netMatchFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool isLoadingDir;
        DispatcherTimer uiTimer, toastTimer;
        bool seekingDrag;

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
            ViewModel.Mode = (PlayMode)ParseInt(Settings.Get("mode", "0"));
            ApplyVolume(Clamp(ParseDouble(Settings.Get("volume", "0.8")), 0, 1));
            SetPlaying(false);
            UpdateTrackInfo(null);

            // 任务栏进度条（Windows 播放器惯例：播放中绿色进度/暂停黄色）
            Win.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo
            {
                ProgressState = System.Windows.Shell.TaskbarItemProgressState.None
            };

            uiTimer = new DispatcherTimer(DispatcherPriority.Render);
            uiTimer.Interval = TimeSpan.FromMilliseconds(33);
            uiTimer.Tick += UiTick;
            uiTimer.Start();

            Win.Loaded += (s, e) =>
            {
                WindowChromeController.EnableRoundedCorners(Win);
                StartPipeServer();
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
            ViewModel = new MainViewModel(Player);
            Win.DataContext = ViewModel;
            ViewModel.PropertyChanged += OnVmPropertyChanged;
            ViewModel.CurrentTrackChanged += OnCurrentTrackChanged;
            BtnOpenFolder = F<Button>("BtnOpenFolder");
            BtnMin = F<Button>("BtnMin");
            BtnClose = F<Button>("BtnClose");
            BtnMode = F<Button>("BtnMode");
            BtnPrev = F<Button>("BtnPrev");
            BtnPlay = F<Button>("BtnPlay");
            BtnNext = F<Button>("BtnNext");
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
            IcoMax = F<WPath>("IcoMax");
            IcoRestore = F<WPath>("IcoRestore");
            FpTitle = F<TextBlock>("FpTitle");
            FpArtist = F<TextBlock>("FpArtist");
            FpTitleBar = F<Rectangle>("FpTitleBar");
            CurTime = F<TextBlock>("CurTime");
            DurTime = F<TextBlock>("DurTime");
            VolLabelV = F<TextBlock>("VolLabelV");
            ToastTb = F<TextBlock>("ToastTb");
            FpLyricsPanel = F<StackPanel>("FpLyricsPanel");
            FpLyricsScroll = F<ScrollViewer>("FpLyricsScroll");
            SeekFill = F<Border>("SeekFill");
            SeekTrackBg = F<Border>("SeekTrackBg");
            SeekThumb = F<System.Windows.Shapes.Ellipse>("SeekThumb");
            SeekHit = F<Grid>("SeekHit");
            VolFillV = F<Border>("VolFillV");
            VolThumbV = F<System.Windows.Shapes.Ellipse>("VolThumbV");
            VolHitV = F<Grid>("VolHitV");
            VolPopup = F<Popup>("VolPopup");
            IcoVol = F<WPath>("IcoVol");
            IcoMuted = F<Grid>("IcoMuted");
            BtnList = F<Button>("BtnList");
            BtnFull = F<Button>("BtnFull");
            ToastCard = F<Border>("ToastCard");
            FullPlayer = F<Grid>("FullPlayer");
            VinylDisc = F<System.Windows.Shapes.Ellipse>("VinylDisc");
            VinylSpin = F<Grid>("VinylSpin");
            VinylRotate = F<RotateTransform>("VinylRotate");
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

            // 媒体库导入控制器：扫描/导入/播放恢复
            Importer = new LibraryImportController(Win, ViewModel, Toast,
                (t, auto) => ViewModel.PlayTrack(t, auto));

            // 主题控制器：切主题后联动歌词重染
            Theme = new ThemeController(root, Win, IcoMoon, IcoSun, onApplied: () => Lyrics.OnThemeChanged());
        }

        void HookEvents()
        {
            Win.Closing += (s, e) =>
            {
                try { uiTimer?.Stop(); } catch { }
                try { toastTimer?.Stop(); } catch { }
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
            BtnNet.Click += (s, e) => ShowNetMatchSettings();

            // 音量：点击图标弹出竖直调节
            BtnMute.Click += (s, e) => { VolPopup.IsOpen = !VolPopup.IsOpen; };
            VolPopup.Closed += (s, e) => { /* 点外部自动收起 */ };

            // 播放列表面板（右侧内嵌，点击切换显隐，带滑入/滑出动画；
            // 排序/选中/拖动/计数等交互由 PlaylistViewController 接管）
            BtnList.Click += (s, e) => PlaylistView.Toggle();

            // 全屏切换
            BtnFull.Click += (s, e) =>
                Win.WindowState = Win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

            // 全屏/还原图标随窗口状态切换（最大化 → 还原双方框；还原 → 最大化单方框）
            Win.StateChanged += (s, e) => UpdateWinIcon();
            UpdateWinIcon();

            // 竖直音量条
            HookDragBar(VolHitV,
                down: ratio => ApplyVolume(ratio),
                move: ratio => ApplyVolume(ratio),
                up: ratio => ApplyVolume(ratio),
                vertical: true);

            // 播放控制按钮已通过 Command Binding 绑定到 ViewModel（TogglePlay/Next/Prev/ToggleMode）

            // 进度条拖动
            HookDragBar(SeekHit,
                down: ratio =>
                {
                    if (current != null && (Player.Duration > TimeSpan.Zero))
                    {
                        seekingDrag = true;
                        SetSeekUi(ratio, true);
                    }
                },
                move: ratio => { if (seekingDrag) SetSeekUi(ratio, true); },
                up: ratio =>
                {
                    if (seekingDrag)
                    {
                        seekingDrag = false;
                        if (current != null && (Player.Duration > TimeSpan.Zero))
                            Player.Position = TimeSpan.FromSeconds(ratio * Player.Duration.TotalSeconds);
                        if (!SeekHit.IsMouseOver) { SeekTrackBg.Height = 4; SeekFill.Height = 4; }
                    }
                });
            SeekHit.MouseEnter += (s, e) =>
            {
                SeekTrackBg.Height = 6;
                SeekFill.Height = 6;
                if ((Player.Duration > TimeSpan.Zero)) SeekThumb.Visibility = Visibility.Visible;
            };
            SeekHit.MouseLeave += (s, e) =>
            {
                if (!seekingDrag)
                {
                    SeekTrackBg.Height = 4;
                    SeekFill.Height = 4;
                    SeekThumb.Visibility = Visibility.Collapsed;
                }
            };

            // 播放器事件（PlayerEngine）
            // 注意：PlaybackEnded 由 ViewModel 订阅处理（自动下一首/单曲循环），
            // MainWindow 不再重复订阅，避免双重调用导致跳过歌曲或单曲循环冲突。

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

            // 快捷键
            Win.KeyDown += OnKeyDown;
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

        void HookDragBar(Grid hit, Action<double> down, Action<double> move, Action<double> up, bool vertical = false)
        {
            Func<MouseEventArgs, double> ratioOf = delegate(MouseEventArgs e)
            {
                Point p = e.GetPosition(hit);
                if (vertical)
                {
                    double h = hit.ActualHeight;
                    return h > 0 ? Clamp(1 - p.Y / h, 0, 1) : 0;
                }
                double w = hit.ActualWidth;
                return w > 0 ? Clamp(p.X / w, 0, 1) : 0;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                hit.CaptureMouse();
                double r = ratioOf(e);
                down(r);
                e.Handled = true;
            };
            hit.MouseMove += (s, e) =>
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed && hit.IsMouseCaptured) move(ratioOf(e));
            };
            hit.MouseLeftButtonUp += (s, e) =>
            {
                if (hit.IsMouseCaptured)
                {
                    double r = ratioOf(e);
                    hit.ReleaseMouseCapture();
                    up(r);
                }
                e.Handled = true;
            };
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

        /// <summary>元素淡入（切歌时标题/封面/歌词的柔和过渡）。</summary>
        void FadeIn(UIElement el, int ms = 320)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /* ============================================================
         * 主题：已拆至 ThemeController
         * ============================================================ */

        ThemeController Theme;

        /// <summary>窗口最大化时显示"还原"双方框，还原时显示"最大化"单方框。</summary>
        void UpdateWinIcon()
        {
            if (IcoMax == null || IcoRestore == null) return;
            bool max = Win.WindowState == WindowState.Maximized;
            IcoMax.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            IcoRestore.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        }

        /* ============================================================
         * 视图（播放顺序）
         * 搜索/排序已全部下沉到 PlaylistManager（唯一数据源）：
         * SearchText/SortMode setter 即时批量刷新，本类不再维护
         * 平行的 RefreshViewData 管道。列表计数由 View.CollectionChanged 统一维护。
         * ============================================================ */

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
                SetSeekUi(0, false);
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
                SetSeekUi(0, false);
                UpdateTaskbarProgress();
                return;
            }
            if (t.Duration > TimeSpan.Zero)
            {
                t.RefreshDurationText();
                ViewModel.TotalTimeText = FmtTime(t.Duration);
            }
            UpdateTrackInfo(t);
            Lyrics.Render(t);
            TryNetMatch(t);
            ViewModel.CurrentTimeText = "0:00";
            SetSeekUi(0, false);
            Win.Title = t.Title + (t.Artist.Length > 0 ? " - " + t.Artist : "") + " · Aurora";

            // 切歌过渡：标题/歌手/黑胶封面/歌词区柔和淡入，替代生硬的内容跳变
            FadeIn(FpTitle);
            FadeIn(FpArtist);
            FadeIn(VinylDisc);
            FadeIn(FpLyricsPanel);

            // 列表面板打开时，高亮跟随当前播放曲（含自动切歌），并滚动到可见处；
            // SelectionChanged 里 t == current 判断保证此回写不会二次触发切歌
            PlaylistView.HighlightCurrent();
            UpdateTaskbarProgress();
        }

        /* ============================================================
         * 播放控制（业务逻辑唯一入口：ViewModel.PlayTrack）
         * ============================================================ */

        void SetCurrentTrack(Track t, bool autoplay)
        {
            // 业务逻辑已下沉到 ViewModel.PlayTrack，UI 更新通过 CurrentTrackChanged 事件处理
            ViewModel.PlayTrack(t, autoplay);
        }

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
            // 任务栏进度颜色随播放状态切换（绿/黄）
            UpdateTaskbarProgress();
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

        /* ============================================================
         * 联网匹配歌词 / 封面（后台线程执行，不阻塞播放）
         * 播放缺歌词或缺封面的歌曲时触发；缓存命中则不联网；
         * 结果回到 UI 线程刷新歌词渲染与黑胶封面。
         * ============================================================ */

        void TryNetMatch(Track t)
        {
            if (t == null || netMatchFailed.Contains(t.FilePath)) return;
            // 按格式的联网匹配开关（联网匹配设置中配置，默认全部启用）
            if (!NetMatch.EnabledFor(t.FilePath)) return;
            bool needLyrics = !t.HasLrc && NetMatch.FindLyricFile(t.FilePath) == null;
            bool needCover = (t.Cover == null || t.Cover.Length == 0) && !NetMatch.HasCover(t.FilePath);
            if (!needLyrics && !needCover) return;

            NetMatch.Start(t.FilePath, t.Title, t.Artist, result =>
            {
                Win.Dispatcher.BeginInvoke((Action)(() => OnNetMatchDone(t, result)));
            });
        }

        void OnNetMatchDone(Track t, NetMatchResult r)
        {
            if (t == null || r == null) return;
            bool refreshLyrics = false, refreshCover = false;

            if (r.LyricsMatched && File.Exists(r.LyricPath))
            {
                try { t.LrcText = File.ReadAllText(r.LyricPath, Encoding.UTF8); } catch { }
                refreshLyrics = true;
            }
            if (r.CoverMatched && File.Exists(r.CoverPath))
            {
                try { t.Cover = File.ReadAllBytes(r.CoverPath); } catch { }
                refreshCover = true;
                t.InvalidateThumb();   // 丢弃旧的生成封面缩略图，列表改用新封面
            }

            if (current == t)
            {
                if (refreshLyrics) Lyrics.Render(t);
                if (refreshCover) UpdateTrackInfo(t);
            }

            if (r.LyricsMatched || r.CoverMatched)
            {
                var parts = new List<string>();
                if (r.LyricsMatched) parts.Add("歌词");
                if (r.CoverMatched) parts.Add("封面");
                Toast("已联网匹配" + string.Join("、", parts) + "（" + r.Source + "）");
            }
            else if (r.NotFound)
            {
                netMatchFailed.Add(t.FilePath);   // 确认不存在，本次会话不再重试
                if (current == t) Toast("未找到「" + t.Title + "」的歌词/封面");
            }
            else if (current == t)
            {
                Toast("联网匹配失败：" + r.Message);
            }
        }

        /* ============================================================
         * 联网匹配设置对话框已拆至 NetMatchSettingsDialog（纯静态展示层）
         * ============================================================ */

        void ShowNetMatchSettings()
        {
            NetMatchSettingsDialog.Show(Win, Theme.IsDark, onCacheCleared: () => netMatchFailed.Clear(), toast: Toast);
        }

        /* ============================================================
         * 歌词（居左）：渲染/高亮/滚动/配色已拆至 LyricsViewController
         * ============================================================ */

        /* ============================================================
         * UI 计时（进度 / 歌词 / 黑胶）
         * ============================================================ */

        void SetSeekUi(double ratio, bool showThumb)
        {
            double w = SeekHit.ActualWidth;
            SeekFill.Width = Math.Max(0, Math.Min(1, ratio)) * w;
            SeekThumb.Margin = new Thickness(ratio * w - 6, 0, 0, 0);
            if (showThumb) SeekThumb.Visibility = Visibility.Visible;
        }

        void UiTick(object sender, EventArgs e)
        {
            // 黑胶旋转（播放时约 10 秒/圈）
            if (playing)
                VinylRotate.Angle = (VinylRotate.Angle + 1.2) % 360;

            // 进度
            if ((Player.Duration > TimeSpan.Zero) && current != null)
            {
                double dur = Player.Duration.TotalSeconds;
                double pos = Player.Position.TotalSeconds;
                if (dur > 0)
                {
                    if (!seekingDrag)
                    {
                        SetSeekUi(pos / dur, false);
                        CurTime.Text = FmtTime(TimeSpan.FromSeconds(pos));
                        DurTime.Text = FmtTime(TimeSpan.FromSeconds(dur));
                    }
                    Lyrics.Sync(pos);
                }
            }

            // 任务栏进度（节流：进度值变化超过 0.5% 才写，避免每帧更新绑定）
            double ratio = (current != null && Player.Duration > TimeSpan.Zero)
                ? Player.Position.TotalSeconds / Player.Duration.TotalSeconds : 0;
            if (Math.Abs(ratio - _lastTaskbarRatio) > 0.005 || (ratio == 0 && _lastTaskbarRatio != 0))
            {
                _lastTaskbarRatio = ratio;
                UpdateTaskbarProgress();
            }
        }

        double _lastTaskbarRatio = -1;

        /// <summary>Windows 任务栏进度：播放=绿色 / 暂停=黄色 / 无曲=隐藏。</summary>
        void UpdateTaskbarProgress()
        {
            var info = Win.TaskbarItemInfo;
            if (info == null) return;
            if (current == null || Player.Duration <= TimeSpan.Zero)
            {
                info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                _lastTaskbarRatio = -1;
                return;
            }
            double ratio = Clamp(Player.Position.TotalSeconds / Player.Duration.TotalSeconds, 0, 1);
            info.ProgressState = playing
                ? System.Windows.Shell.TaskbarItemProgressState.Normal
                : System.Windows.Shell.TaskbarItemProgressState.Paused;
            info.ProgressValue = ratio;
            _lastTaskbarRatio = ratio;
        }

        static string FmtTime(TimeSpan t)
        {
            int s = Math.Max(0, (int)t.TotalSeconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        /* ============================================================
         * Toast
         * ============================================================ */

        void Toast(string msg)
        {
            ToastTb.Text = msg;
            ToastCard.Visibility = Visibility.Visible;
            ToastCard.Opacity = 1;
            if (toastTimer == null)
            {
                toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
                toastTimer.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s).Stop();
                    var anim = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                    anim.Completed += (s2, e2) => { ToastCard.Visibility = Visibility.Collapsed; };
                    ToastCard.BeginAnimation(UIElement.OpacityProperty, anim);
                };
            }
            toastTimer.Stop();
            toastTimer.Start();
        }

                /* ============================================================
         * OGG / Opus 格式：FFmpeg 转 WAV 后播放
         * ============================================================ */

/* ============================================================
         * 单实例：命名管道接收后续打开的文件（管道服务已拆至 SingleInstanceServer）
         * ============================================================ */

        void StartPipeServer()
        {
            SingleInstanceServer.Start(path =>
                Win.Dispatcher.BeginInvoke((Action)(() => OpenExternalFile(path))));
        }

        void OpenExternalFile(string path)
        {
            if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
            Win.Activate();
            string dir = LibraryImportController.SafeDir(path);
            if (dir == null) return;
            if (current != null && string.Equals(LibraryImportController.SafeDir(current.FilePath), dir, StringComparison.OrdinalIgnoreCase))
            {
                Track t = LibraryImportController.FindByPath(view, path) ?? LibraryImportController.FindByPath(tracks, path);
                if (t != null) { SetCurrentTrack(t, true); return; }
            }
            Importer.LoadDirectory(dir, path, false);
        }

        /* ============================================================
         * 快捷键
         * ============================================================ */

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 焦点在文本框（搜索框）时，字母/空格/方向键都是文本编辑的一部分，
            // 不得触发全局快捷键（实测：搜索框里输 N/P 会直接切歌、↑↓ 会改音量）；
            // 仅保留 Esc 收起列表面板的交互
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox)
            {
                if (e.Key != Key.Escape) return;
                e.Handled = true;
                if (PlaylistView.IsOpen) PlaylistView.Toggle();
                return;
            }

            // 与界面按钮同源：全部经由 ViewModel 命令 / 属性（唯一播放逻辑入口）
            switch (e.Key)
            {
                case Key.Space:
                    e.Handled = true;
                    if (ViewModel.TogglePlayCommand.CanExecute(null)) ViewModel.TogglePlayCommand.Execute(null);
                    break;
                case Key.Right:
                    if (Player.Duration > TimeSpan.Zero)
                        Player.Position += TimeSpan.FromSeconds(5);
                    break;
                case Key.Left:
                    if (Player.Duration > TimeSpan.Zero)
                        Player.Position -= TimeSpan.FromSeconds(5);
                    break;
                case Key.Up:
                    ViewModel.Volume = Clamp(ViewModel.Volume + 0.05, 0, 1);
                    ShowVolumePopupTemporarily();
                    break;
                case Key.Down:
                    ViewModel.Volume = Clamp(ViewModel.Volume - 0.05, 0, 1);
                    ShowVolumePopupTemporarily();
                    break;
                case Key.N:
                    if (ViewModel.NextCommand.CanExecute(null)) ViewModel.NextCommand.Execute(null);
                    break;
                case Key.P:
                    if (ViewModel.PrevCommand.CanExecute(null)) ViewModel.PrevCommand.Execute(null);
                    break;
                case Key.M:
                    if (ViewModel.ToggleMuteCommand.CanExecute(null)) ViewModel.ToggleMuteCommand.Execute(null);
                    ShowVolumePopupTemporarily();
                    break;
                case Key.Escape:
                    e.Handled = true;
                    // Esc 优先收起播放列表抽屉；主界面时不再退出应用（防误触丢状态）
                    if (PlaylistView.IsOpen) PlaylistView.Toggle();
                    break;
            }
        }

        static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        static double ParseDouble(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0.8;
        }

        static int ParseInt(string s)
        {
            int i;
            return int.TryParse(s, out i) ? i : 0;
        }
    }
}
