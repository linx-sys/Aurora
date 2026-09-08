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
using System.IO.Pipes;
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
        Popup VolPopup; ListBox ListList;
        Border ListPanel; bool listOpen;
        StackPanel CapBar;
        Button BtnSort; TextBox SearchBox; TextBlock SearchHint, ListCountTb;
        DispatcherTimer volAutoCloseTimer;   // 音量弹层自动收起
        Brush accentBrushCache, normalBrushCache;   // 歌词笔刷缓存（frozen，避免每行 new）

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
        LrcDoc lrcDoc;
        readonly List<TextBlock> fullLrcBlocks = new List<TextBlock>();
        readonly List<int> docToBlock = new List<int>();   // doc.Lines 下标 → fullLrcBlocks 下标（-1=元数据/跳过）
        int fullLrcIndex = -2;
        Color lyricAccent = Color.FromRgb(0x60, 0xA5, 0xFA);  // 当前歌词高亮色（取自歌曲配色）
        double fullScrollTarget;
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
                EnableRoundedCorners();
                // 挂 Win32 消息钩子：最大化时限制为工作区，不盖任务栏
                var hwndSrc = System.Windows.Interop.HwndSource.FromHwnd(
                    new System.Windows.Interop.WindowInteropHelper(Win).Handle);
                if (hwndSrc != null) hwndSrc.AddHook(WndProc);
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

            isDark = Settings.Get("theme", "dark") != "light";
            ApplyTheme(isDark);

            HookEvents();
            // 播放模式/音量从设置恢复：直接写入 ViewModel（唯一数据源，
            // 图标/Tooltip/音量条由 OnVmPropertyChanged 联动刷新）
            ViewModel.Mode = (PlayMode)ParseInt(Settings.Get("mode", "0"));
            ApplyVolume(Clamp(ParseDouble(Settings.Get("volume", "0.8")), 0, 1));
            SetPlaying(false);
            UpdateTrackInfo(null);
            ListCountTb.Text = "共 0 首歌曲";

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
                EnableRoundedCorners();
                StartPipeServer();
                if (!string.IsNullOrEmpty(openFile) && File.Exists(openFile))
                {
                    string dir = SafeDir(openFile);
                    if (dir != null) LoadDirectory(dir, openFile, false);
                }
                else
                {
                    string lastDir = Settings.Get("lastDir", "");
                    if (lastDir.Length > 0 && Directory.Exists(lastDir))
                        LoadDirectory(lastDir, null, true);
                    else
                        PickFolder(); // 首次使用：选择音乐文件夹
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
            ListPanel = F<Border>("ListPanel");
            CapBar = F<StackPanel>("CapBar");
            ListList = F<ListBox>("ListList");
            // 用代码设置 ItemsPanel 为 AnimatedStackPanel（松散 XAML 无法解析自定义类型）
            FrameworkElementFactory apf = new FrameworkElementFactory(typeof(AnimatedStackPanel));
            ListList.ItemsPanel = new ItemsPanelTemplate(apf);
            BtnSort = F<Button>("BtnSort");
            SearchBox = F<TextBox>("SearchBox");
            SearchHint = F<TextBlock>("SearchHint");
            ListCountTb = F<TextBlock>("ListCountTb");
            BtnList = F<Button>("BtnList");
            BtnFull = F<Button>("BtnFull");
            ToastCard = F<Border>("ToastCard");
            FullPlayer = F<Grid>("FullPlayer");
            VinylDisc = F<System.Windows.Shapes.Ellipse>("VinylDisc");
            VinylSpin = F<Grid>("VinylSpin");
            VinylRotate = F<RotateTransform>("VinylRotate");
            ArmRotate = F<RotateTransform>("ArmRotate");
            VinylCover = F<ImageBrush>("VinylCover");
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
            BtnTheme.Click += (s, e) => ToggleTheme();
            BtnOpenFolder.Click += (s, e) => PickFolder();
            BtnNet.Click += (s, e) => ShowNetMatchSettings();

            // 音量：点击图标弹出竖直调节
            BtnMute.Click += (s, e) => { VolPopup.IsOpen = !VolPopup.IsOpen; };
            VolPopup.Closed += (s, e) => { /* 点外部自动收起 */ };

            // 播放列表面板（右侧内嵌，点击切换显隐，带滑入/滑出动画）
            BtnList.Click += (s, e) => ToggleListPanel();
            BtnSort.Click += (s, e) =>
            {
                ViewModel.SortMode = (ViewModel.SortMode + 1) % 5;   // setter 内自动刷新视图
                string[] names = { "文件名", "标题", "歌手", "时长（短→长）", "时长（长→短）", "自定义顺序" };
                BtnSort.ToolTip = "排序方式：" + names[ViewModel.SortMode];
                Toast("排序：" + names[ViewModel.SortMode]);
            };
            // 搜索框已通过 Text Binding 绑定到 ViewModel.SearchText（唯一数据源，
            // PlaylistManager.SearchText setter 即时批量刷新），变更时在
            // OnVmPropertyChanged 中仅切换占位提示

            // 列表计数跟随 View 集合变化（排序/搜索/导入/删除/拖动全覆盖）
            ViewModel.View.CollectionChanged += (s, e) =>
                ListCountTb.Text = "共 " + ViewModel.View.Count + " 首歌曲";

            ListList.SelectionChanged += (s, e) =>
            {
                Track t = ListList.SelectedItem as Track;
                MainViewModel.Dbg("ListSelectionChanged: " + (t != null ? t.Title : "null") + " current=" + (current != null ? current.Title : "null"));
                if (t != null && t != current) SetCurrentTrack(t, true);
            };

            // ===== 播放列表：拖动排序 + 删除 =====
            Point dragStart = new Point();
            Track dragItem = null;
            ListList.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // 检测删除按钮点击（Tag="delete"）
                DependencyObject dep = e.OriginalSource as DependencyObject;
                while (dep != null && !(dep is Button))
                    dep = VisualTreeHelper.GetParent(dep);
                Button btn = dep as Button;
                if (btn != null && btn.Tag != null && btn.Tag.ToString() == "delete")
                {
                    Track t = btn.DataContext as Track;
                    if (t != null) DeleteTrack(t);
                    e.Handled = true;
                    return;
                }
                dragStart = e.GetPosition(ListList);
                dragItem = TrackFromPoint(dragStart);
            };
            ListList.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point pos = e.GetPosition(ListList);
                if (Math.Abs(pos.X - dragStart.X) < 5 && Math.Abs(pos.Y - dragStart.Y) < 5) return;
                if (dragItem == null) dragItem = TrackFromPoint(pos);  // 按下时没取到则移动时补取
                if (dragItem == null) return;
                Track item = dragItem;
                dragItem = null;
                DragDrop.DoDragDrop(ListList, item, DragDropEffects.Move);
                e.Handled = true;
            };
            ListList.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(Track))) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            ListList.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(Track))) { e.Handled = true; return; }
                Track dragged = e.Data.GetData(typeof(Track)) as Track;
                if (dragged == null) { e.Handled = true; return; }
                // 搜索过滤时 view 是子集，拖动排序会导致 tracks 索引错位，禁止并提示
                if (SearchBox != null && SearchBox.Text.Trim().Length > 0)
                {
                    Toast("请先清空搜索再拖动排序");
                    e.Handled = true;
                    return;
                }
                Point pos = e.GetPosition(ListList);
                // DropIndexFromPoint 返回的是"视图(view)中的插入位置"，而 Move 操作的是
                // tracks（顺序通常与 view 不同：文件名/标题等排序）。必须经目标曲引用
                // 换算回 tracks 坐标系，否则非默认排序下拖动会跳到完全错误的位置
                // （实测：标题排序下拖到列表顶部，结果被移到第 8 位）
                int viewIdx = DropIndexFromPoint(pos);
                int oldIdx = tracks.IndexOf(dragged);
                int targetIdx = viewIdx >= view.Count ? tracks.Count : tracks.IndexOf(view[viewIdx]);
                if (targetIdx < 0) targetIdx = tracks.Count;
                if (oldIdx >= 0 && targetIdx != oldIdx)
                {
                    if (targetIdx > oldIdx) targetIdx--;
                    ViewModel.SortMode = 5;   // 拖动后进入自定义顺序（setter 内已刷新一次）
                    ViewModel.Playlist.Move(oldIdx, targetIdx);
                    ViewModel.RefreshView();
                    Toast("已移到第 " + (targetIdx + 1) + " 位");
                }
                // DoDragDrop 模态循环会干扰 MediaPlayer 时钟，正在播放时重新激活
                if (playing)
                {
                    try { Player.Play(); } catch { }
                }
                e.Handled = true;
            };
            ListList.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    Track t = ListList.SelectedItem as Track;
                    if (t != null) { DeleteTrack(t); e.Handled = true; }
                }
            };

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
                if (listOpen && e.GetPosition(ListPanel).X < 0)
                {
                    ToggleListPanel();
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
                ImportPaths(files, true);
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
                if (SearchHint != null)
                    SearchHint.Visibility = ViewModel.SearchText.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
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

        /// <summary>播放列表抽屉开关（右侧滑入/滑出动画；打开时定位当前播放曲）。</summary>
        void ToggleListPanel()
        {
            listOpen = !listOpen;
            // 面板从窗口顶部开始，与右上角窗口按钮重叠：打开时隐藏三连，关闭后恢复
            CapBar.Visibility = listOpen ? Visibility.Collapsed : Visibility.Visible;
            var tt = ListPanel.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                ListPanel.RenderTransform = tt;
            }
            var slide = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = listOpen ? 400 : 0,
                To = listOpen ? 0 : 400,
                Duration = TimeSpan.FromMilliseconds(listOpen ? 260 : 200),
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = listOpen ? System.Windows.Media.Animation.EasingMode.EaseOut
                                          : System.Windows.Media.Animation.EasingMode.EaseIn
                }
            };
            if (!listOpen)
            {
                slide.Completed += (s, e) =>
                {
                    if (!listOpen) ListPanel.Visibility = Visibility.Collapsed;
                };
            }
            ListPanel.Visibility = Visibility.Visible;
            tt.BeginAnimation(TranslateTransform.XProperty, slide);
            if (listOpen)
            {
                SearchBox.Focus();
                if (current != null)
                {
                    ListList.SelectedItem = current;
                    ListList.ScrollIntoView(current);
                }
            }
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
         * 主题 / 圆角
         * ============================================================ */

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        bool isDark = true;

        void EnableRoundedCorners()
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(Win).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
            }
            catch { }
        }

        /* ============================================================
         * 最大化不盖任务栏：WM_GETMINMAXINFO → 限制为工作区（rcWork）
         * WindowStyle=None 的 WPF 窗口最大化默认用整屏 rcMonitor，会盖住任务栏
         * ============================================================ */
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        struct MmiPoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public MmiPoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WorkRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public WorkRect rcMonitor, rcWork; public int dwFlags; }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    IntPtr mon = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                    if (mon != IntPtr.Zero)
                    {
                        var info = new MONITORINFO();
                        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                        if (GetMonitorInfo(mon, ref info))
                        {
                            mmi.ptMaxPosition.x = info.rcWork.left;
                            mmi.ptMaxPosition.y = info.rcWork.top;
                            mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                            mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                        }
                    }
                    // 无边框窗口：WPF 的 MinWidth/MinHeight 在 Win32 拖拽调整大小时不生效，
                    // 必须在这里设置最小跟踪尺寸（物理像素，需按 DPI 缩放换算）
                    var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                    double dpiX = 1.0, dpiY = 1.0;
                    if (src != null && src.CompositionTarget != null)
                    {
                        dpiX = src.CompositionTarget.TransformToDevice.M11;
                        dpiY = src.CompositionTarget.TransformToDevice.M22;
                    }
                    mmi.ptMinTrackSize.x = (int)Math.Ceiling(Win.MinWidth * dpiX);
                    mmi.ptMinTrackSize.y = (int)Math.Ceiling(Win.MinHeight * dpiY);
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
                catch { }
            }
            return IntPtr.Zero;
        }

        static void SetBrush(Grid r, string key, bool dark, uint darkArgb, uint lightArgb)
        {
            uint v = dark ? darkArgb : lightArgb;
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            brush.Freeze();
            r.Resources[key] = brush;
        }

        void ApplyTheme(bool dark)
        {
            try
            {
                isDark = dark;   // 必须同步状态，否则 ToggleTheme 会一直切向同一边
                SetBrush(root, "Bg", dark, 0xFF0B0D12, 0xFFF2F4F9);
                SetBrush(root, "Panel", dark, 0xFF10131B, 0xFFFBFCFE);
                SetBrush(root, "Bar", dark, 0xCC0D1017, 0xE6FFFFFF);
                SetBrush(root, "Field", dark, 0x0FFFFFFF, 0x0D1F2937);
                SetBrush(root, "FieldLine", dark, 0x18FFFFFF, 0x1A94A3B8);
                SetBrush(root, "Line", dark, 0x1FFFFFFF, 0x1F94A3B8);
                SetBrush(root, "Hover", dark, 0x14FFFFFF, 0x14000000);
                SetBrush(root, "TrackBg", dark, 0x16FFFFFF, 0x14000000);
                SetBrush(root, "Card", dark, 0xF01B2030, 0xF5FFFFFF);
                SetBrush(root, "Ph", dark, 0x33FFFFFF, 0x2E94A3B8);
                SetBrush(root, "Text", dark, 0xFFE9EDF6, 0xFF1A2030);
                SetBrush(root, "Dim", dark, 0xFF8A93A8, 0xFF5A6474);
                SetBrush(root, "Dim2", dark, 0xFF5D6579, 0xFF8B95A7);
                SetBrush(root, "Accent", dark, 0xFF4CC9F0, 0xFF0E8AB8);
                SetBrush(root, "Pink", dark, 0xFFF472B6, 0xFFDB2777);
                Win.Background = (Brush)root.Resources["Bg"];
                IcoMoon.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
                IcoSun.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;
                InvalidateLyricBrushes();   // 普通歌词色随主题变化，重建缓存
                RecolorLyrics();
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aurora_theme_err.txt"),
                        "root=" + (root == null) + " IcoMoon=" + (IcoMoon == null) + " IcoSun=" + (IcoSun == null)
                        + " Win=" + (Win == null) + "\r\n" + ex);
                }
                catch { }
                throw;
            }
        }

        void RecolorLyrics()
        {
            Brush normal = NormalBrush();
            Brush current = AccentBrush();
            for (int k = 0; k < fullLrcBlocks.Count; k++)
                fullLrcBlocks[k].Foreground = (k == fullLrcIndex) ? current : normal;
        }

        /// <summary>歌词高亮色笔刷（缓存并 freeze；lyricAccent/主题变化后由调用方置空重建）。</summary>
        Brush AccentBrush()
        {
            if (accentBrushCache == null)
            {
                var b = new SolidColorBrush(lyricAccent);
                b.Freeze();
                accentBrushCache = b;
            }
            return accentBrushCache;
        }

        /// <summary>歌词普通色笔刷（缓存并 freeze，避免每行高亮切换都 new）。</summary>
        Brush NormalBrush()
        {
            if (normalBrushCache == null)
            {
                var b = fullStyleNormal();
                if (b.CanFreeze) b.Freeze();
                normalBrushCache = b;
            }
            return normalBrushCache;
        }

        void InvalidateLyricBrushes()
        {
            accentBrushCache = null;
            normalBrushCache = null;
        }

        Brush fullStyleNormal()
        {
            return isDark
                ? (Brush)root.TryFindResource("Dim")
                : new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0x93, 0xA8));
        }

        void ToggleTheme()
        {
            ApplyTheme(!isDark);
            Settings.Set("theme", isDark ? "dark" : "light");
            Toast(isDark ? "已切换到深色模式" : "已切换到浅色模式");
        }

        /// <summary>窗口最大化时显示"还原"双方框，还原时显示"最大化"单方框。</summary>
        void UpdateWinIcon()
        {
            if (IcoMax == null || IcoRestore == null) return;
            bool max = Win.WindowState == WindowState.Maximized;
            IcoMax.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            IcoRestore.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        }

        /* ============================================================
         * 媒体库加载
         * ============================================================ */

        static string SafeDir(string path)
        {
            try { return IOPath.GetDirectoryName(path); } catch { return null; }
        }

        void PickFolder()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "选择存放音乐的文件夹";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    LoadDirectory(dlg.SelectedPath, null, false);
            }
        }

        /// <summary>加载目录（替换现有列表）。</summary>
        void LoadDirectory(string dir, string autoPlayPath, bool silent)
        {
            if (isLoadingDir) return;
            isLoadingDir = true;
            if (!silent) Toast("正在扫描文件夹…");
            string d = dir;
            string auto = autoPlayPath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = new List<string>();
                Library.EnumerateFiles(d, files, 0);
                List<Track> built = Library.BuildTracks(files);
                Win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    isLoadingDir = false;
                    ViewModel.ResetShuffleHistory();   // 换目录清空随机播放轨迹
                    SearchBox.Text = "";   // 换文件夹清空搜索词，避免旧词把新列表全过滤掉
                    Playlist.ReplaceTracks(built);   // 整表替换 + 单次批量刷新（唯一数据源）
                    Settings.Set("lastDir", d);
                    if (view.Count == 0)
                    {
                        if (!silent) Toast("文件夹里没有找到音频文件");
                        return;
                    }
                    if (!silent) Toast("已加载 " + view.Count + " 首歌曲");

                    if (auto != null)
                    {
                        Track hit = FindByPath(view, auto) ?? FindByPath(tracks, auto);
                        if (hit != null) { SetCurrentTrack(hit, true); return; }
                    }

                    // 恢复上次播放的曲目（不自动出声），否则定位到第一首
                    string lastTrack = Settings.Get("lastTrack", "");
                    Track restore = lastTrack.Length > 0 ? (FindByPath(view, lastTrack) ?? FindByPath(tracks, lastTrack)) : null;
                    if (restore == null) restore = view[0];
                    SetCurrentTrack(restore, false);
                }));
            });
        }

        /// <summary>拖放 / 添加文件：追加导入。</summary>
        void ImportPaths(string[] paths, bool append)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = new List<string>();
                foreach (string p in paths)
                {
                    try
                    {
                        if (Directory.Exists(p)) Library.EnumerateFiles(p, files, 0);
                        else if (File.Exists(p)) files.Add(p);
                    }
                    catch { }
                }
                List<Track> built = Library.BuildTracks(files);
                Win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    int added = Playlist.AddRange(built);   // 去重 + 单次批量刷新（唯一数据源）
                    if (added > 0) Toast("已添加 " + added + " 首歌曲");
                    else Toast("没有新增的歌曲");
                }));
            });
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
                RenderFullLyrics(null);
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
                RenderFullLyrics(null);
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
            RenderFullLyrics(t);
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
            if (listOpen && ListList.SelectedItem != t)
            {
                ListList.SelectedItem = t;
                ListList.ScrollIntoView(t);
            }
            UpdateTaskbarProgress();
        }

        /// <summary>从点击/拖拽坐标获取对应的 Track（遍历可视树找到 ListBoxItem 的 DataContext）。</summary>
        Track TrackFromPoint(Point pt)
        {
            IInputElement hit = ListList.InputHitTest(pt);
            DependencyObject dep = hit as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep == null) return null;
            return (dep as ListBoxItem).DataContext as Track;
        }

        /// <summary>计算拖放目标下标：根据鼠标位置找到目标项，判断落在该项上半/下半部分。</summary>
        int DropIndexFromPoint(Point pt)
        {
            IInputElement hit = ListList.InputHitTest(pt);
            DependencyObject dep = hit as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep == null) return tracks.Count;  // 落在空白处 → 追加到末尾
            ListBoxItem item = dep as ListBoxItem;
            Track t = item.DataContext as Track;
            int idx = tracks.IndexOf(t);
            if (idx < 0) return tracks.Count;
            // 落在该项下半部分 → 插到该项之后
            Point itemTop = item.TranslatePoint(new Point(0, 0), ListList);
            if (pt.Y > itemTop.Y + item.RenderSize.Height / 2) idx++;
            return idx;
        }

        /// <summary>从播放列表移除一首歌曲；若正在播放则停止。</summary>
        void DeleteTrack(Track t)
        {
            if (t == null) return;
            // 业务逻辑下沉到 ViewModel（停止播放、清空当前歌曲、触发 CurrentTrackChanged）
            ViewModel.DeleteTrack(t);
            // 计数由 View.CollectionChanged 统一维护
            Toast("已移除：" + t.Title);
        }

        static Track FindByPath(IEnumerable<Track> list, string path)
        {
            foreach (Track t in list)
                if (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
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
            lyricAccent = CoverArt.PaletteFor(t.Title, t.Artist)[0];
            InvalidateLyricBrushes();   // 高亮色随歌曲配色变化，重建缓存
            if (FpTitleBar != null) FpTitleBar.Fill = new SolidColorBrush(lyricAccent) { Opacity = 0.7 };

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
                if (refreshLyrics) RenderFullLyrics(t);
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
         * 联网匹配设置对话框：按格式开关联网匹配 + 清除歌词/封面缓存。
         * 纯代码构建（一次性 UI，不新增 BAML 资源）；配色随当前主题。
         * ============================================================ */

        static readonly string[] NetMatchFormats = { "mp3", "flac", "m4a", "wav", "wma", "ogg", "oga", "opus", "aac" };

        void ShowNetMatchSettings()
        {
            bool dark = isDark;
            var panelBg = new SolidColorBrush(dark ? Color.FromRgb(0x10, 0x13, 0x1B) : Color.FromRgb(0xFB, 0xFC, 0xFE));
            var textBrush = new SolidColorBrush(dark ? Color.FromRgb(0xE9, 0xED, 0xF6) : Color.FromRgb(0x1A, 0x20, 0x30));
            var dimBrush = new SolidColorBrush(dark ? Color.FromRgb(0x8A, 0x93, 0xA8) : Color.FromRgb(0x5A, 0x64, 0x74));

            var dlg = new Window
            {
                Title = "联网匹配设置",
                Owner = Win,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 430,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = panelBg,
            };

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "播放缺少歌词 / 封面的歌曲时，自动联网匹配并缓存到本地。\n按音频格式选择是否启用：",
                Foreground = dimBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 3×3 格式复选框（即时写入设置）
            var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
            foreach (string ext in NetMatchFormats)
            {
                string key = NetMatch.SettingsKeyForExt(ext);
                var cb = new CheckBox
                {
                    Content = ext.ToUpperInvariant(),
                    IsChecked = Settings.Get(key, "1") != "0",
                    Foreground = textBrush,
                    Margin = new Thickness(4, 6, 4, 6),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                cb.Checked += (s, e) => Settings.Set(key, "1");
                cb.Unchecked += (s, e) => Settings.Set(key, "0");
                grid.Children.Add(cb);
            }
            root.Children.Add(grid);

            root.Children.Add(new Separator
            {
                Background = dimBrush,
                Opacity = 0.3,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 缓存行：统计文本 + 清除按钮
            var cacheText = new TextBlock { Foreground = dimBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Action updateCacheText = () =>
            {
                var st = NetMatch.CacheStats();
                cacheText.Text = "缓存：" + st.files + " 项（" + (st.bytes / 1048576.0).ToString("0.0") + " MB）";
            };
            updateCacheText();

            var clearBtn = new Button
            {
                Content = "清除缓存",
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = Cursors.Hand,
            };
            clearBtn.Click += (s, e) =>
            {
                var removed = NetMatch.ClearCache();
                netMatchFailed.Clear();   // 已删除缓存，允许本次会话内重新匹配
                cacheText.Text = "已清除 " + removed.files + " 项（" + (removed.bytes / 1048576.0).ToString("0.0") + " MB）";
                Toast("已清除歌词/封面缓存：" + removed.files + " 项");
            };

            var cacheRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(clearBtn, Dock.Right);
            cacheRow.Children.Add(clearBtn);
            cacheRow.Children.Add(cacheText);
            root.Children.Add(cacheRow);

            var doneBtn = new Button
            {
                Content = "完成",
                Padding = new Thickness(20, 5, 20, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                Cursor = Cursors.Hand,
            };
            doneBtn.Click += (s, e) => dlg.Close();
            root.Children.Add(doneBtn);

            dlg.Content = root;
            dlg.ShowDialog();
        }

        /* ============================================================
         * 歌词（居左）
         * ============================================================ */

        /* ============================================================
         * 歌词渲染（元数据分组 + 当前行高亮 + 居中滚动）
         * ============================================================ */

        // LRC 中常见的元数据行前缀（作词/作曲/编曲等），单独成组展示，不参与歌词高亮
        static readonly Regex MetaLineRe = new Regex(
            @"^[^:：]{1,60}[:：]",
            RegexOptions.Compiled);

        /// <summary>判断是否为片头行（"歌手 - 歌名"或"歌名 - 歌手"，与顶部标题重复，应跳过）。</summary>
        static bool IsTitleArtistIntro(string text, string artist)
        {
            if (string.IsNullOrEmpty(artist)) return false;
            int dash = text.IndexOf(" - ");
            if (dash <= 0) return false;
            string left = text.Substring(0, dash).Trim();
            string right = text.Substring(dash + 3).Trim();
            return left.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   right.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   artist.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   artist.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>判断是否为"歌名 - 演唱者"署名行（如"老情书 - 匡耿-玉面"，应归入元数据）。</summary>
        static bool IsTitlePerformerLine(string text, string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            int dash = text.IndexOf(" - ");
            if (dash <= 0) return false;
            string left = text.Substring(0, dash).Trim();
            return string.Equals(left, title, StringComparison.OrdinalIgnoreCase);
        }

        void RenderFullLyrics(Track t)
        {
            FpLyricsPanel.Children.Clear();
            fullLrcBlocks.Clear();
            docToBlock.Clear();
            fullLrcIndex = -2;
            lrcDoc = null;
            if (t == null) return;

            LrcDoc doc = t.LrcText != null ? Lrc.Parse(t.LrcText) : null;
            lrcDoc = doc;
            if (doc == null)
            {
                FpLyricsPanel.Children.Add(new TextBlock
                {
                    Text = "暂无歌词",
                    Foreground = fullStyleNormal(),
                    FontSize = 17,
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                return;
            }

            // 第一遍：分离元数据行与正文歌词行
            var metaLines = new List<string>();
            var lyricDocIdx = new List<int>();
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                string text = doc.Lines[i].Text.Trim();
                if (text.Length == 0) { lyricDocIdx.Add(i); continue; }
                if (MetaLineRe.IsMatch(text)) { metaLines.Add(text); continue; }
                if (i < 5 && IsTitleArtistIntro(text, t.Artist)) continue;  // 与顶部标题重复，跳过
                if (IsTitlePerformerLine(text, t.Title)) { metaLines.Add(text); continue; }  // "歌名 - 演唱者"署名行
                lyricDocIdx.Add(i);
            }

            // 元数据组：紧凑、居中、暗色，底部一条强调色分隔线；超过 6 行折叠为"…等 N 项"
            if (metaLines.Count > 0)
            {
                var metaPanel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14),
                };
                const int MetaMaxVisible = 6;
                var visibleMeta = metaLines.Count <= MetaMaxVisible
                    ? metaLines
                    : metaLines.GetRange(0, MetaMaxVisible - 1);
                foreach (string m in visibleMeta)
                {
                    metaPanel.Children.Add(new TextBlock
                    {
                        Text = m,
                        FontSize = 11.5,
                        Foreground = fullStyleNormal(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0.5, 0, 0.5),
                    });
                }
                if (metaLines.Count > MetaMaxVisible)
                {
                    metaPanel.Children.Add(new TextBlock
                    {
                        Text = "…等 " + metaLines.Count + " 项制作人员",
                        FontSize = 11,
                        Foreground = fullStyleNormal(),
                        Opacity = 0.65,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0.5),
                    });
                }
                metaPanel.Children.Add(new Rectangle
                {
                    Width = 30,
                    Height = 1.5,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(lyricAccent) { Opacity = 0.45 },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                });
                FpLyricsPanel.Children.Add(metaPanel);
            }

            // 建立 doc.Lines 下标 → 渲染块下标的映射
            for (int i = 0; i < doc.Lines.Count; i++) docToBlock.Add(-1);
            int blockIdx = 0;
            foreach (int docIdx in lyricDocIdx)
            {
                LrcLine ln = doc.Lines[docIdx];
                var tb = new TextBlock
                {
                    Text = ln.Text.Length > 0 ? ln.Text : " ",
                    Foreground = fullStyleNormal(),
                    FontSize = 17,
                    Margin = new Thickness(0, 6, 0, 6),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = ln.Text.Length > 0 ? Cursors.Hand : Cursors.Arrow,
                    Tag = docIdx,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.0, 1.0),
                };
                tb.MouseLeftButtonDown += LrcLineClick;
                FpLyricsPanel.Children.Add(tb);
                fullLrcBlocks.Add(tb);
                docToBlock[docIdx] = blockIdx;
                blockIdx++;
            }

            FpLyricsScroll.ScrollToTop();
            SyncLyrics(0);
        }

        void LrcLineClick(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as TextBlock;
            if (tb == null || lrcDoc == null || !(Player.Duration > TimeSpan.Zero)) return;
            int docIdx = (int)tb.Tag;
            if (docIdx >= 0 && docIdx < lrcDoc.Lines.Count)
            {
                Player.Position = TimeSpan.FromSeconds(lrcDoc.Lines[docIdx].Time);
                // 点击歌词跳转后收起列表面板（若开着），与"点击左侧收起"的抽屉行为一致；
                // 此处 e.Handled=true 会阻断冒泡，故需在此显式收起
                if (listOpen) ToggleListPanel();
            }
            e.Handled = true;
        }

        void SyncLyrics(double pos)
        {
            if (lrcDoc == null || fullLrcBlocks.Count == 0) return;
            int docIdx = Lrc.IndexAt(lrcDoc.Lines, pos + 0.02);

            // doc 下标 → 渲染块下标；若当前是元数据行，高亮下一条正文歌词
            int blockIdx = -1;
            if (docIdx >= 0 && docIdx < docToBlock.Count)
            {
                blockIdx = docToBlock[docIdx];
                if (blockIdx < 0)
                {
                    for (int k = docIdx + 1; k < docToBlock.Count; k++)
                    {
                        if (docToBlock[k] >= 0) { blockIdx = docToBlock[k]; break; }
                    }
                    if (blockIdx < 0) blockIdx = fullLrcBlocks.Count - 1;
                }
            }

            if (blockIdx != fullLrcIndex)
            {
                fullLrcIndex = blockIdx;
                Brush curBrush = AccentBrush();
                Brush dimBrush = NormalBrush();
                for (int k = 0; k < fullLrcBlocks.Count; k++)
                {
                    bool cur = k == blockIdx;
                    var tb = fullLrcBlocks[k];
                    tb.FontSize = cur ? 19 : 17;
                    tb.FontWeight = cur ? FontWeights.Bold : FontWeights.Normal;
                    tb.Foreground = cur ? curBrush : dimBrush;
                    tb.Margin = cur ? new Thickness(0, 8, 0, 8) : new Thickness(0, 6, 0, 6);
                    // 当前行轻微放大，用动画过渡（RenderTransform 不影响布局）
                    var st = tb.RenderTransform as ScaleTransform;
                    if (st != null)
                    {
                        double target = cur ? 1.08 : 1.0;
                        var anim = new System.Windows.Media.Animation.DoubleAnimation(
                            target, TimeSpan.FromMilliseconds(220));
                        anim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                    }
                }
                if (blockIdx >= 0 && blockIdx < fullLrcBlocks.Count)
                {
                    TextBlock tb = fullLrcBlocks[blockIdx];
                    double y = tb.TransformToVisual(FpLyricsPanel).Transform(new Point(0, 0)).Y;
                    fullScrollTarget = Math.Max(0, y - FpLyricsScroll.ViewportHeight / 2 + tb.ActualHeight / 2);
                }
            }
            double curOff = FpLyricsScroll.VerticalOffset;
            double diff = fullScrollTarget - curOff;
            if (Math.Abs(diff) > 0.5)
                FpLyricsScroll.ScrollToVerticalOffset(curOff + diff * 0.18);
        }

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
                    SyncLyrics(pos);
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
         * 单实例：命名管道接收后续打开的文件
         * ============================================================ */

        void StartPipeServer()
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream("AuroraPlayer.Instance", PipeDirection.In))
                        {
                            server.WaitForConnection();
                            using (var reader = new StreamReader(server, Encoding.Unicode))
                            {
                                string path = reader.ReadLine();
                                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                                {
                                    Win.Dispatcher.BeginInvoke((Action)(() => OpenExternalFile(path)));
                                }
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
        }

        void OpenExternalFile(string path)
        {
            if (Win.WindowState == WindowState.Minimized) Win.WindowState = WindowState.Normal;
            Win.Activate();
            string dir = SafeDir(path);
            if (dir == null) return;
            if (current != null && string.Equals(SafeDir(current.FilePath), dir, StringComparison.OrdinalIgnoreCase))
            {
                Track t = FindByPath(view, path) ?? FindByPath(tracks, path);
                if (t != null) { SetCurrentTrack(t, true); return; }
            }
            LoadDirectory(dir, path, false);
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
                if (listOpen) ToggleListPanel();
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
                    if (listOpen) ToggleListPanel();
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

    /// <summary>
    /// 带动画的垂直堆叠面板：子元素位置变化时用 TranslateTransform 平滑过渡（250ms CubicEaseOut）。
    /// 用于播放列表拖动排序时的过渡动画。
    /// </summary>
    public class AnimatedStackPanel : Panel
    {
        readonly Dictionary<UIElement, Point> lastPos = new Dictionary<UIElement, Point>();
        static readonly Duration animDur = new Duration(TimeSpan.FromMilliseconds(250));
        static readonly IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        protected override Size MeasureOverride(Size available)
        {
            Size desired = new Size(0, 0);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(available);
                if (child.DesiredSize.Width > desired.Width) desired.Width = child.DesiredSize.Width;
                desired.Height += child.DesiredSize.Height;
            }
            return desired;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                double h = child.DesiredSize.Height;
                Rect target = new Rect(0, y, finalSize.Width, h);

                Point lp;
                if (lastPos.TryGetValue(child, out lp))
                {
                    double dy = lp.Y - y;
                    if (Math.Abs(dy) > 0.5)
                    {
                        TranslateTransform tt = child.RenderTransform as TranslateTransform;
                        if (tt == null)
                        {
                            tt = new TranslateTransform();
                            child.RenderTransform = tt;
                        }
                        tt.Y = dy;  // 立即偏移到旧位置
                        DoubleAnimation anim = new DoubleAnimation(0, animDur);
                        anim.EasingFunction = ease;
                        tt.BeginAnimation(TranslateTransform.YProperty, anim);
                    }
                }
                lastPos[child] = new Point(0, y);
                child.Arrange(target);
                y += h;
            }
            return finalSize;
        }

        protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
        {
            base.OnVisualChildrenChanged(added, removed);
            if (removed != null)
            {
                UIElement el = removed as UIElement;
                if (el != null) lastPos.Remove(el);
            }
        }
    }
}
