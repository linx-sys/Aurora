/* ============================================================
 * MainWindow.cs — Aurora 播放页（唯一界面）
 * 黑胶唱片 + 居左歌词 + 底部控制条；双击 MP3 / 拖放 / 文件夹导入
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;
using IOPath = System.IO.Path;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    public class MainWindow
    {
        public Window Win;
        Grid root;
        MediaElement Player;
        Button BtnOpenFolder, BtnMin, BtnClose, BtnMode, BtnPrev, BtnPlay, BtnNext, BtnMute, BtnTheme, BtnList, BtnFull;
        WPath IcoRepeat, IcoShuffle; Grid IcoOne;
        WPath IcoPlay, IcoPause, IcoMoon, IcoSun, IcoMax, IcoRestore;
        TextBlock FpTitle, FpArtist, CurTime, DurTime, VolLabelV, ToastTb, TrackLabel;
        StackPanel FpLyricsPanel;
        Border SeekFill, VolFillV, ToastCard, SeekTrackBg; Grid SeekHit, VolHitV, FullPlayer;
        Ellipse SeekThumb, VolThumbV; ScrollViewer FpLyricsScroll;
        System.Windows.Shapes.Ellipse VinylDisc; Grid VinylSpin;
        RotateTransform VinylRotate, ArmRotate; ImageBrush VinylCover;
        Popup VolPopup; ListBox ListList;
        Border ListPanel; bool listOpen;
        StackPanel CapBar;
        Button BtnSort; TextBox SearchBox; TextBlock SearchHint, ListCountTb;
        int sortMode;   // 0=文件名 1=标题 2=歌手 3=时长↑ 4=时长↓

        readonly ObservableCollection<Track> tracks = new ObservableCollection<Track>();
        readonly List<Track> view = new List<Track>();
        Track current;
        int mode;                       // 0=列表循环 1=单曲循环 2=随机
        readonly List<string> shuffleHistory = new List<string>();
        int errStreak;
        bool playing;
        bool isLoadingDir;
        readonly Random rng = new Random();
        LrcDoc lrcDoc;
        readonly List<TextBlock> fullLrcBlocks = new List<TextBlock>();
        int fullLrcIndex = -2;
        double fullScrollTarget;
        double volume = 0.8, savedVolume = 0.8;
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

            root = (Grid)ParseXaml(LoadResource("Aurora.ui.xaml"));
            Win.Content = root;
            FindControls();

            mode = ParseInt(Settings.Get("mode", "0"));
            volume = Clamp(ParseDouble(Settings.Get("volume", "0.8")), 0, 1);
            savedVolume = volume;
            isDark = Settings.Get("theme", "dark") != "light";
            ApplyTheme(isDark);

            HookEvents();
            ApplyMode(mode, false);
            ApplyVolume(volume);
            SetPlaying(false);
            UpdateTrackInfo(null);

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

        static string LoadResource(string name)
        {
            using (Stream s = typeof(MainWindow).Assembly.GetManifestResourceStream(name))
            using (var r = new StreamReader(s, Encoding.UTF8))
                return r.ReadToEnd();
        }

        static object ParseXaml(string xaml)
        {
            return System.Windows.Markup.XamlReader.Parse(xaml);
        }

        T F<T>(string name) where T : class { return (T)root.FindName(name); }

        void FindControls()
        {
            Player = F<MediaElement>("Player");
            BtnOpenFolder = F<Button>("BtnOpenFolder");
            BtnMin = F<Button>("BtnMin");
            BtnClose = F<Button>("BtnClose");
            BtnMode = F<Button>("BtnMode");
            BtnPrev = F<Button>("BtnPrev");
            BtnPlay = F<Button>("BtnPlay");
            BtnNext = F<Button>("BtnNext");
            BtnMute = F<Button>("BtnMute");
            BtnTheme = F<Button>("BtnTheme");
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
            CurTime = F<TextBlock>("CurTime");
            DurTime = F<TextBlock>("DurTime");
            VolLabelV = F<TextBlock>("VolLabelV");
            ToastTb = F<TextBlock>("ToastTb");
            TrackLabel = F<TextBlock>("TrackLabel");
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
            ListPanel = F<Border>("ListPanel");
            CapBar = F<StackPanel>("CapBar");
            ListList = F<ListBox>("ListList");
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
                Settings.Set("volume", savedVolume.ToString("0.00", CultureInfo.InvariantCulture));
                Settings.Set("mode", mode.ToString());
            };

            BtnMin.Click += (s, e) => Win.WindowState = WindowState.Minimized;
            BtnClose.Click += (s, e) => Win.Close();
            BtnTheme.Click += (s, e) => ToggleTheme();
            BtnOpenFolder.Click += (s, e) => PickFolder();

            // 音量：点击图标弹出竖直调节
            BtnMute.Click += (s, e) => { VolPopup.IsOpen = !VolPopup.IsOpen; };
            VolPopup.Closed += (s, e) => { /* 点外部自动收起 */ };

            // 播放列表面板（右侧内嵌，点击切换显隐）
            BtnList.Click += (s, e) =>
            {
                listOpen = !listOpen;
                ListPanel.Visibility = listOpen ? Visibility.Visible : Visibility.Collapsed;
                // 面板从窗口顶部开始，与右上角窗口按钮重叠：打开时隐藏三连，关闭后恢复
                CapBar.Visibility = listOpen ? Visibility.Collapsed : Visibility.Visible;
                if (listOpen)
                {
                    SearchBox.Focus();
                    if (current != null)
                    {
                        ListList.SelectedItem = current;
                        ListList.ScrollIntoView(current);
                    }
                }
            };
            BtnSort.Click += (s, e) =>
            {
                sortMode = (sortMode + 1) % 5;
                string[] names = { "文件名", "标题", "歌手", "时长（短→长）", "时长（长→短）" };
                BtnSort.ToolTip = "排序方式：" + names[sortMode];
                RefreshViewData();
                Toast("排序：" + names[sortMode]);
            };
            SearchBox.TextChanged += (s, e) =>
            {
                SearchHint.Visibility = SearchBox.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
                RefreshViewData();
            };
            ListList.SelectionChanged += (s, e) =>
            {
                Track t = ListList.SelectedItem as Track;
                if (t != null && t != current) SetCurrentTrack(t, true);
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

            BtnPlay.Click += (s, e) => TogglePlay();
            BtnNext.Click += (s, e) => NextTrack(true);
            BtnPrev.Click += (s, e) => PrevTrack();
            BtnMode.Click += (s, e) => ApplyMode((mode + 1) % 3, true);

            // 进度条拖动
            HookDragBar(SeekHit,
                down: ratio =>
                {
                    if (current != null && Player.NaturalDuration.HasTimeSpan)
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
                        if (current != null && Player.NaturalDuration.HasTimeSpan)
                            Player.Position = TimeSpan.FromSeconds(ratio * Player.NaturalDuration.TimeSpan.TotalSeconds);
                        if (!SeekHit.IsMouseOver) { SeekTrackBg.Height = 4; SeekFill.Height = 4; }
                    }
                });
            SeekHit.MouseEnter += (s, e) =>
            {
                SeekTrackBg.Height = 6;
                SeekFill.Height = 6;
                if (Player.NaturalDuration.HasTimeSpan) SeekThumb.Visibility = Visibility.Visible;
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

            // 播放器事件
            Player.MediaOpened += (s, e) =>
            {
                if (Player.NaturalDuration.HasTimeSpan && current != null)
                {
                    current.Duration = Player.NaturalDuration.TimeSpan;
                    current.RefreshDurationText();
                    DurTime.Text = FmtTime(current.Duration);
                }
            };
            Player.MediaEnded += (s, e) =>
            {
                SetPlaying(false);
                if (mode == 1) { Player.Position = TimeSpan.Zero; Player.Play(); SetPlaying(true); }
                else NextTrack(false);
            };
            Player.MediaFailed += (s, e) =>
            {
                SetPlaying(false);
                Track t = current;
                Toast("无法播放「" + (t != null ? t.Title : "") + "」");
                if (++errStreak < Math.Max(view.Count, 3))
                {
                    var dt = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    dt.Tick += (s2, e2) => { ((DispatcherTimer)s2).Stop(); NextTrack(false); };
                    dt.Start();
                }
                else errStreak = 0;
            };

            // 窗口拖动（黑胶与空白区；歌词/按钮除外）
            FullPlayer.MouseLeftButtonDown += (s, e) =>
            {
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

        void BtnSearchShortcut() { /* 预留 */ }

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
            Brush normal = fullStyleNormal();
            Brush current = (Brush)root.TryFindResource("Text");
            for (int k = 0; k < fullLrcBlocks.Count; k++)
                fullLrcBlocks[k].Foreground = (k == fullLrcIndex) ? current : normal;
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
                    tracks.Clear();
                    shuffleHistory.Clear();
                    foreach (Track t in built) tracks.Add(t);
                    SearchBox.Text = "";   // 换文件夹清空搜索词，避免旧词把新列表全过滤掉
                    RefreshViewData();
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
                    int added = 0;
                    foreach (Track t in built)
                    {
                        if (FindByPath(tracks, t.FilePath) != null) continue;
                        tracks.Add(t);
                        added++;
                    }
                    if (added > 0) RefreshViewData();
                    if (added > 0) Toast("已添加 " + added + " 首歌曲");
                    else Toast("没有新增的歌曲");
                }));
            });
        }

        /* ============================================================
         * 视图（播放顺序）
         * ============================================================ */

        void RefreshViewData()
        {
            string q = SearchBox != null ? SearchBox.Text.Trim() : "";
            view.Clear();
            foreach (Track t in tracks)
            {
                if (q.Length > 0)
                {
                    string hay = (t.Title + " " + t.Artist + " " + t.Album + " " + t.FileName).ToLowerInvariant();
                    if (!hay.Contains(q.ToLowerInvariant())) continue;
                }
                view.Add(t);
            }
            switch (sortMode)
            {
                case 1: view.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase)); break;
                case 2: view.Sort((a, b) => string.Compare(a.Artist, b.Artist, StringComparison.CurrentCultureIgnoreCase)); break;
                case 3: view.Sort((a, b) => a.Duration.CompareTo(b.Duration)); break;
                case 4: view.Sort((a, b) => b.Duration.CompareTo(a.Duration)); break;
                default: view.Sort((a, b) => string.Compare(a.FileName, b.FileName, StringComparison.CurrentCultureIgnoreCase)); break;
            }
            // view 是普通 List，WPF 不感知内容变化，必须重设 ItemsSource 强制刷新
            ListList.ItemsSource = null;
            ListList.ItemsSource = view;
            ListCountTb.Text = "共 " + view.Count + " 首歌曲";
        }

        static Track FindByPath(IEnumerable<Track> list, string path)
        {
            foreach (Track t in list)
                if (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        void ScrollToCurrent()
        {
            // 播放页无列表；保留空实现以兼容调用
        }

        /* ============================================================
         * 播放控制
         * ============================================================ */

        void PlayTrack(Track t)
        {
            SetCurrentTrack(t, true);
        }

        void SetCurrentTrack(Track t, bool autoplay)
        {
            if (t == null) return;
            errStreak = 0;
            current = t;
            Settings.Set("lastTrack", t.FilePath);

            try { Player.Source = new Uri(t.FilePath); } catch { return; }
            Player.Position = TimeSpan.Zero;
            if (autoplay) { Player.Play(); SetPlaying(true); }
            else SetPlaying(false);

            UpdateTrackInfo(t);
            RenderFullLyrics(t);
            CurTime.Text = "0:00";
            SetSeekUi(0, false);
            Win.Title = t.Title + (t.Artist.Length > 0 ? " - " + t.Artist : "") + " · Aurora";
        }

        void TogglePlay()
        {
            if (current == null)
            {
                if (view.Count > 0) PlayTrack(view[0]);
                else if (tracks.Count > 0) PlayTrack(tracks[0]);
                return;
            }
            if (playing) { Player.Pause(); SetPlaying(false); }
            else { Player.Play(); SetPlaying(true); }
        }

        void NextTrack(bool manual)
        {
            if (view.Count == 0) return;
            if (mode == 2)
            {
                if (manual && current != null) shuffleHistory.Add(current.FilePath);
                var pool = view.FindAll(t => t != current);
                if (pool.Count == 0) pool = view;
                SetCurrentTrack(pool[rng.Next(pool.Count)], true);
                return;
            }
            int idx = current != null ? view.IndexOf(current) : -1;
            SetCurrentTrack(view[(idx + 1 + view.Count) % view.Count], true);
        }

        void PrevTrack()
        {
            if (view.Count == 0) return;
            if (mode == 2 && shuffleHistory.Count > 0)
            {
                string last = shuffleHistory[shuffleHistory.Count - 1];
                shuffleHistory.RemoveAt(shuffleHistory.Count - 1);
                Track t = FindByPath(view, last) ?? FindByPath(tracks, last);
                if (t != null) { SetCurrentTrack(t, true); return; }
            }
            int idx = current != null ? view.IndexOf(current) : 0;
            SetCurrentTrack(view[(idx - 1 + view.Count) % view.Count], true);
        }

        void ApplyMode(int m, bool notify)
        {
            mode = m;
            IcoRepeat.Visibility = m == 0 ? Visibility.Visible : Visibility.Collapsed;
            IcoOne.Visibility = m == 1 ? Visibility.Visible : Visibility.Collapsed;
            IcoShuffle.Visibility = m == 2 ? Visibility.Visible : Visibility.Collapsed;
            BtnMode.ToolTip = m == 0 ? "播放模式：列表循环" : m == 1 ? "播放模式：单曲循环" : "播放模式：随机播放";
            if (notify) Toast(m == 0 ? "列表循环" : m == 1 ? "单曲循环" : "随机播放");
        }

        void ApplyVolume(double v)
        {
            volume = v;
            Player.Volume = v;
            Player.IsMuted = false;
            if (v > 0) savedVolume = v;
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

        void SetPlaying(bool p)
        {
            playing = p;
            IcoPlay.Visibility = p ? Visibility.Collapsed : Visibility.Visible;
            IcoPause.Visibility = p ? Visibility.Visible : Visibility.Collapsed;
            // 唱臂：播放搭在盘面上，暂停移出盘面
            try
            {
                var anim = new System.Windows.Media.Animation.DoubleAnimation(
                    p ? 0 : -38, TimeSpan.FromMilliseconds(280));
                anim.EasingFunction = new System.Windows.Media.Animation.QuadraticEase();
                ArmRotate.BeginAnimation(RotateTransform.AngleProperty, anim);
            }
            catch { }
        }

        void UpdateTrackInfo(Track t)
        {
            if (t == null) return;
            FpTitle.Text = t.Title;
            FpArtist.Text = t.Artist.Length > 0 ? t.Artist : "未知歌手";
            TrackLabel.Text = t.Title + (t.Artist.Length > 0 ? " - " + t.Artist : "");

            try { VinylCover.ImageSource = CoverArt.ForTrack(t, 512); } catch { }
        }

        /* ============================================================
         * 歌词（居左）
         * ============================================================ */

        void RenderFullLyrics(Track t)
        {
            FpLyricsPanel.Children.Clear();
            fullLrcBlocks.Clear();
            fullLrcIndex = -2;
            lrcDoc = null;
            if (t == null) return;

            LrcDoc doc = t.LrcText != null ? Lrc.Parse(t.LrcText) : null;
            lrcDoc = doc;   // 供 SyncLyrics / LrcLineClick 使用
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

            for (int i = 0; i < doc.Lines.Count; i++)
            {
                LrcLine ln = doc.Lines[i];
                var tb = new TextBlock
                {
                    Text = ln.Text.Length > 0 ? ln.Text : " ",
                    Foreground = fullStyleNormal(),
                    FontSize = 17,
                    Margin = new Thickness(0, 11, 0, 11),
                    TextWrapping = TextWrapping.Wrap,
                    Cursor = ln.Text.Length > 0 ? Cursors.Hand : Cursors.Arrow,
                    Tag = i,
                };
                tb.MouseLeftButtonDown += LrcLineClick;
                FpLyricsPanel.Children.Add(tb);
                fullLrcBlocks.Add(tb);
            }
            FpLyricsScroll.ScrollToTop();
            // 立刻强制高亮第一行（UiTick 要等 MediaOpened 异步触发 + HasTimeSpan=true 后才跑 SyncLyrics，
            // 切歌瞬间到那之间所有歌词保持创建时的 dim 颜色，看着像没高亮）
            SyncLyrics(0);
        }

        void LrcLineClick(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as TextBlock;
            if (tb == null || lrcDoc == null || !Player.NaturalDuration.HasTimeSpan) return;
            int i = (int)tb.Tag;
            if (i >= 0 && i < lrcDoc.Lines.Count)
                Player.Position = TimeSpan.FromSeconds(lrcDoc.Lines[i].Time);
            e.Handled = true;
        }

        void SyncLyrics(double pos)
        {
            if (lrcDoc == null || fullLrcBlocks.Count == 0) return;
            int i = Lrc.IndexAt(lrcDoc.Lines, pos + 0.02);
            if (i != fullLrcIndex)
            {
                fullLrcIndex = i;
                Brush curBrush = (Brush)root.TryFindResource("Text");
                Brush dimBrush = fullStyleNormal();
                for (int k = 0; k < fullLrcBlocks.Count; k++)
                {
                    bool cur = k == i;
                    var tb = fullLrcBlocks[k];
                    tb.FontSize = cur ? 20 : 17;
                    tb.FontWeight = cur ? FontWeights.Bold : FontWeights.Normal;
                    tb.Foreground = cur ? curBrush : dimBrush;
                }
                if (i >= 0 && i < fullLrcBlocks.Count)
                {
                    TextBlock tb = fullLrcBlocks[i];
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
            if (Player.NaturalDuration.HasTimeSpan && current != null)
            {
                double dur = Player.NaturalDuration.TimeSpan.TotalSeconds;
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
            switch (e.Key)
            {
                case Key.Space: e.Handled = true; TogglePlay(); break;
                case Key.Right:
                    if (Player.NaturalDuration.HasTimeSpan)
                        Player.Position += TimeSpan.FromSeconds(5);
                    break;
                case Key.Left:
                    if (Player.NaturalDuration.HasTimeSpan)
                        Player.Position -= TimeSpan.FromSeconds(5);
                    break;
                case Key.Up: ApplyVolume(Clamp(volume + 0.05, 0, 1)); break;
                case Key.Down: ApplyVolume(Clamp(volume - 0.05, 0, 1)); break;
                case Key.N: NextTrack(true); break;
                case Key.P: PrevTrack(); break;
                case Key.M: BtnMute.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); break;
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
