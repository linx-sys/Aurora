#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * MainWindow.cs — Aurora 播放页（唯一界面）
 * 阶段 2 收敛后只保留三件事：
 *   1. Initialize（窗口创建 + BAML 加载 + 控件查找 + 控制器装配）
 *   2. Constructor 参数处理（恢复设置 / 初始状态）
 *   3. Lifecycle（Closing 保存状态 / Loaded 启动服务与目录恢复）
 * 播放状态 UI、切歌编排 → PlaybackStateViewController；
 * 按钮接线/拖动/拖放 → CommandBindingManager；
 * 快捷键/进度计时/Toast/联网匹配/窗口杂项 → 各自 Controller。
 * 本类不含：播放逻辑、数据查询、文件扫描、业务判断。
 * ============================================================ */
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    public class MainWindow
    {
        public Window Win;
        Grid root;
        IPlaybackService Player;
        PlayerEngine Engine;                     // 具体引擎引用（诊断文本等引擎级能力）
        MainViewModel ViewModel;
        LibraryDatabase LibraryStore;
        bool closing, closeReady;

        // ===== 装配出的控制器（View 行为全部下沉） =====
        LyricsViewController Lyrics;             // 歌词渲染/高亮/滚动/配色
        PlaylistViewController PlaylistView;     // 列表抽屉/拖动排序/删除/计数
        LibraryImportController Importer;        // DB 秒开 + 差分同步/导入/播放恢复
        ThemeController Theme;                   // 主题注入/切换/持久化
        ToastController ToastCtrl;               // Toast 提示条
        PlaybackTickController Tick;             // 33ms 进度/黑胶/任务栏计时（含进度条拖动）
        HotkeyController Hotkey;                 // 全局快捷键
        NetMatchViewController NetMatchView;     // 联网匹配歌词/封面
        WindowMiscController Misc;               // 窗口图标/单实例/更新检查/外部文件
        SmTcController SmTc;                     // 系统媒体传输控制（可 null：初始化失败静默禁用）
        PlaybackStateViewController StateView;   // 播放状态/音量/曲目信息/切歌编排

        public MainWindow(string openFile)
        {
            /* ---------- 1. Initialize ---------- */
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
                try { SmTc?.Attach(); }
                catch (Exception ex) { Logger.Dbg("SMTC attach: " + ex.Message); }
            };

            var chrome = new System.Windows.Shell.WindowChrome();
            chrome.CaptionHeight = 0;
            chrome.ResizeBorderThickness = new Thickness(6);
            chrome.GlassFrameThickness = new Thickness(0);
            chrome.CornerRadius = new CornerRadius(0);
            chrome.UseAeroCaptionButtons = false;
            System.Windows.Shell.WindowChrome.SetWindowChrome(Win, chrome);

            // ui.xaml 编译为 BAML，LoadComponent 加载（启动提速 + 编译期校验）
            root = (Grid)Application.LoadComponent(new Uri("/AuroraPlayer;component/src/ui.xaml", UriKind.Relative));
            Win.Content = root;
            FindControls();

            Theme.Apply(Settings.Get("theme", "dark") != "light");
            StateView.Attach();   // VM 属性变更/切歌事件 → 图标/音量/曲目信息联动

            /* ---------- 2. 命令接线与初始状态 ---------- */
            CommandBindingManager.Hook(Win,
                F<Button>("BtnMin"), F<Button>("BtnClose"), F<Button>("BtnTheme"), F<Button>("BtnOpenFolder"),
                F<Button>("BtnNet"), F<Button>("BtnList"), F<Button>("BtnFull"), F<Button>("BtnMute"),
                F<Grid>("FullPlayer"), F<Grid>("VolHitV"), F<Popup>("VolPopup"),
                Theme, Importer, PlaylistView, NetMatchView, Misc, StateView, Toast, Hotkey);

            // 播放模式/音量从设置恢复：写入 ViewModel（唯一数据源），图标经 StateView 联动刷新
            ViewModel.Mode = Settings.GetEnum("mode", PlayMode.ListRepeat);
            StateView.ApplyVolume(Settings.GetDouble("volume", 0.8, 0, 1));
            ViewModel.PlaybackError += (s, message) => { if (!closing) Toast(message); };
            StateView.InitInitialState();

            // 任务栏进度条（Windows 播放器惯例：播放中绿色进度/暂停黄色）
            Win.TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo
            {
                ProgressState = System.Windows.Shell.TaskbarItemProgressState.None
            };
            Tick.Start();

            /* ---------- 3. Lifecycle ---------- */
            WireClosing();
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
         * 装配：控件查找 + 控制器创建（本类唯一"知道一切"的位置）
         * ============================================================ */

        T F<T>(string name) where T : class { return (T)root.FindName(name); }

        void FindControls()
        {
            LibraryStore = new LibraryDatabase(LibraryDatabase.DefaultPath);
            ILibraryStore library = LibraryStore;
            Engine = new PlayerEngine();
            Player = Engine;
            ViewModel = new MainViewModel(Player, library);
            Win.DataContext = ViewModel;

            // 歌词渲染控制器：View 行为（滚动/点击跳转/配色）独立于 ViewModel
            Lyrics = new LyricsViewController(root, F<StackPanel>("FpLyricsPanel"), F<ScrollViewer>("FpLyricsScroll"), Player,
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
            Theme = new ThemeController(root, Win, F<WPath>("IcoMoon"), F<WPath>("IcoSun"),
                onApplied: () => Lyrics.OnThemeChanged());

            ToastCtrl = new ToastController(F<Border>("ToastCard"), F<TextBlock>("ToastTb"));

            NetMatchView = new NetMatchViewController(Win, ViewModel, Lyrics,
                updateTrackInfo: t => StateView.UpdateTrackInfo(t),
                toast: Toast, isDark: () => Theme.IsDark,
                audioDiagnostics: () => Engine.GetDiagnostics());

            Misc = new WindowMiscController(Win, ViewModel, Importer, Toast);
            Misc.SetWinIcons(F<WPath>("IcoMax"), F<WPath>("IcoRestore"));

            Hotkey = new HotkeyController(Win, ViewModel, Player,
                isListOpen: () => PlaylistView.IsOpen,
                toggleList: () => PlaylistView.Toggle(),
                // 注意：必须用 lambda 延迟解析——StateView 在最后才构造，
                // 直接绑定实例方法组会因 null this 抛 ArgumentException（启动崩溃）
                showVolumePopup: () => StateView.ShowVolumePopupTemporarily());

            // 33ms 进度/黑胶/任务栏计时（含进度条拖动交互）
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
                // Attach 延迟到 SourceInitialized，此时 HWND 才有效。
            }
            catch (Exception smtcEx) { MainViewModel.Dbg("SMTC init FAIL: " + smtcEx.Message); SmTc = null; }

            // 播放状态视图控制（图标/音量/曲目信息/切歌编排）最后装配
            StateView = new PlaybackStateViewController(Win, root, ViewModel,
                F<TextBlock>("FpTitle"), F<TextBlock>("FpArtist"), F<System.Windows.Shapes.Rectangle>("FpTitleBar"),
                F<StackPanel>("FpLyricsPanel"),
                F<System.Windows.Shapes.Ellipse>("VinylDisc"), F<ImageBrush>("VinylCover"),
                F<RotateTransform>("ArmRotate"), F<Button>("BtnPlay"),
                F<WPath>("IcoPlay"), F<WPath>("IcoPause"), F<WPath>("IcoRepeat"),
                F<Grid>("IcoOne"), F<WPath>("IcoShuffle"), F<Button>("BtnMode"),
                F<Border>("VolFillV"), F<System.Windows.Shapes.Ellipse>("VolThumbV"),
                F<TextBlock>("VolLabelV"), F<Popup>("VolPopup"),
                F<WPath>("IcoVol"), F<Grid>("IcoMuted"), F<Button>("BtnMute"),
                Lyrics, Tick, SmTc, NetMatchView, PlaylistView, Toast);
        }

        /// <summary>Toast 转发（控制器间经注入回调协作，不互相持有）。</summary>
        void Toast(string msg) { ToastCtrl.Toast(msg); }

        /* ============================================================
         * Lifecycle：窗口关闭（保存状态 / 释放资源）
         * ============================================================ */

        void WireClosing()
        {
            Win.Closing += async (s, e) =>
            {
                if (closeReady) return;
                e.Cancel = true;
                if (closing) return;
                closing = true;
                Win.IsEnabled = false;
                Tick?.Stop();
                ToastCtrl?.Stop();
                StateView?.StopTimers();
                Importer?.Dispose();
                Misc?.Dispose();
                NetMatchView?.Dispose();
                SmTc?.Dispose();
                ViewModel.Dispose();
                try
                {
                    Settings.Set("volume", ViewModel.SavedVolume.ToString("0.00", CultureInfo.InvariantCulture));
                    Settings.Set("mode", ((int)ViewModel.Mode).ToString(CultureInfo.InvariantCulture));
                    Settings.Set("lastTrack", ViewModel.CurrentTrack?.FilePath ?? "");
                    App.UpdateJumpList(ViewModel.CurrentTrack?.FilePath);
                    Player?.Dispose();
                    // 不同步阻塞 Dispatcher；导入与响度任务退出后才能关闭共享数据库。
                    await System.Threading.Tasks.Task.WhenAll(Importer.Completion, ViewModel.Completion);
                }
                catch (Exception ex) { Logger.Dbg("Shutdown: " + ex); }
                finally
                {
                    LibraryStore?.Dispose();
                    closeReady = true;
                    // WhenAll 可能同步完成，必须在本次 Closing 返回后再关闭，避免重入。
                    _ = Win.Dispatcher.BeginInvoke(new Action(() => Win.Close()));
                }
            };
        }
    }
}
