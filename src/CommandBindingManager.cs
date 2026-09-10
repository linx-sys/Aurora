/* ============================================================
 * CommandBindingManager.cs — 窗口命令与交互接线（阶段 2 从 MainWindow 拆出）
 * 职责：窗口控制按钮、音量条拖动、全屏切换、列表面板开合、
 *       主题/联网设置/打开文件夹入口、窗口拖动、拖放导入、快捷键挂接。
 * 只做"控件事件 → 控制器方法"的薄委托，不含任何业务判断。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Aurora
{
    static class CommandBindingManager
    {
        internal static void Hook(Window win,
            Button btnMin, Button btnClose, Button btnTheme, Button btnOpenFolder,
            Button btnNet, Button btnList, Button btnFull, Button btnMute,
            Grid fullPlayer, Grid volHitV, Popup volPopup,
            ThemeController theme, LibraryImportController importer,
            PlaylistViewController playlistView, NetMatchViewController netMatchView,
            WindowMiscController misc, PlaybackStateViewController stateView,
            Action<string> toast, HotkeyController hotkey)
        {
            // 窗口控制
            btnMin.Click += (s, e) => win.WindowState = WindowState.Minimized;
            btnClose.Click += (s, e) => win.Close();
            btnTheme.Click += (s, e) => theme.Toggle(toast);
            btnOpenFolder.Click += (s, e) => importer.PickFolder();
            btnNet.Click += (s, e) => netMatchView.ShowSettings();

            // 音量：点击图标弹出竖直调节
            btnMute.Click += (s, e) => { volPopup.IsOpen = !volPopup.IsOpen; };
            volPopup.Closed += (s, e) => { /* 点外部自动收起 */ };

            // 播放列表面板（右侧内嵌，点击切换显隐；排序/选中/拖动/计数由 PlaylistViewController 接管）
            btnList.Click += (s, e) => playlistView.Toggle();

            // 全屏切换
            btnFull.Click += (s, e) =>
                win.WindowState = win.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

            // 全屏/还原图标随窗口状态切换（最大化 → 还原双方框；还原 → 最大化单方框）
            win.StateChanged += (s, e) => misc.UpdateWinIcon();
            misc.UpdateWinIcon();

            // 竖直音量条
            UiUtil.HookDragBar(volHitV,
                down: ratio => stateView.ApplyVolume(ratio),
                move: ratio => stateView.ApplyVolume(ratio),
                up: ratio => stateView.ApplyVolume(ratio),
                vertical: true);

            // 播放控制按钮已通过 Command Binding 绑定到 ViewModel（TogglePlay/Next/Prev/ToggleMode）；
            // 进度条拖动在 PlaybackTickController；快捷键在 HotkeyController

            // 窗口拖动（黑胶与空白区；歌词/按钮除外）
            fullPlayer.MouseLeftButtonDown += (s, e) =>
            {
                // 播放列表面板打开时，点击左侧主区域 = 收起面板（抽屉交互惯例）。
                // 判定：相对面板坐标 X<0 即点击在面板左侧的主内容区；
                // 底栏（进度条/按钮）在面板正下方，X>=0 不受影响。
                // 歌词行的跳转由内层 LrcLineClick 先行处理，冒泡到这里时仅收起。
                if (playlistView.IsOpen && e.GetPosition(playlistView.PanelElement).X < 0)
                {
                    playlistView.Toggle();
                    return;
                }
                if (e.OriginalSource is TextBlock || e.OriginalSource is System.Windows.Controls.Button) return;
                if (e.ButtonState == MouseButtonState.Pressed) { try { win.DragMove(); } catch { } }
            };

            // 拖放导入
            win.AllowDrop = true;
            win.DragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            win.Drop += (s, e) =>
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                importer.ImportPaths(files, true);
            };

            // 快捷键
            hotkey.Hook();
        }
    }
}
