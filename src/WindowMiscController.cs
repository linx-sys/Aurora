#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * WindowMiscController.cs — 窗口杂项（从 MainWindow 拆出）
 * 窗口最大化/还原图标、单实例命名管道唤醒、后台更新检查、外部文件打开。
 * ============================================================ */
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using WPath = System.Windows.Shapes.Path;

namespace Aurora
{
    class WindowMiscController : IDisposable
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly LibraryImportController importer;
        readonly Action<string> toast;
        WPath icoMax, icoRestore;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly CancellationToken token;
        SingleInstanceServer pipeServer;
        bool updateStarted;
        bool disposed;

        public WindowMiscController(Window win, MainViewModel vm, LibraryImportController importer, Action<string> toast)
        {
            this.win = win;
            this.vm = vm;
            this.importer = importer;
            this.toast = toast;
            token = lifetime.Token;
            win.Closed += OnClosed;
        }

        /// <summary>注入最大化/还原图标控件（FindControls 阶段调用）。</summary>
        public void SetWinIcons(WPath icoMax, WPath icoRestore)
        {
            this.icoMax = icoMax;
            this.icoRestore = icoRestore;
        }

        /// <summary>窗口最大化时显示"还原"双方框，还原时显示"最大化"单方框。</summary>
        public void UpdateWinIcon()
        {
            if (icoMax == null || icoRestore == null) return;
            bool max = win.WindowState == WindowState.Maximized;
            icoMax.Visibility = max ? Visibility.Collapsed : Visibility.Visible;
            icoRestore.Visibility = max ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>单实例：命名管道接收后续打开的文件（管道服务在 SingleInstanceServer）。</summary>
        public void StartPipeServer()
        {
            if (disposed || pipeServer != null) return;
            pipeServer = SingleInstanceServer.Start(path => Post(() => OpenExternalFile(path)), token);
        }

        /// <summary>启动 5 秒后后台检查更新（24h 节流，P2-5 新体验）：
        /// 便携版仍 Toast+打开发布页；安装版展示更新日志并确认后带进度下载、完成后唤起安装器。
        /// 不做静默替换。</summary>
        public void StartUpdateCheck()
        {
            if (disposed || updateStarted) return;
            updateStarted = true;
            _ = CheckAfterStartupAsync();
        }

        async Task CheckAfterStartupAsync()
        {
            try
            {
                await Task.Delay(5000, token).ConfigureAwait(false);
                await UpdateChecker.CheckAsync(info => Post(() => ShowUpdateOffer(info)), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MainViewModel.Dbg("Update startup FAIL: " + ex.Message); }
        }

        async void ShowUpdateOffer(UpdateChecker.UpdateInfo info)
        {
            if (token.IsCancellationRequested) return;
            string title = "发现新版本 v" + info.Version + "（当前 v" + AppInfo.Version + "）";

            // 便携版：无安装器承载，保持原体验——提示 + 打开发布页
            if (AppInfo.IsPortable)
            {
                toast(title + "，正在打开发布页…");
                OpenReleasesPage();
                return;
            }

            // 安装版：更新日志 + 确认下载
            string notes = string.IsNullOrEmpty(info.Notes) ? "（无更新日志）" : info.Notes;
            var choice = System.Windows.Forms.MessageBox.Show(
                notes, title,
                System.Windows.Forms.MessageBoxButtons.YesNo,
                System.Windows.Forms.MessageBoxIcon.Information);
            if (token.IsCancellationRequested || choice != System.Windows.Forms.DialogResult.Yes || string.IsNullOrEmpty(info.SetupUrl)) return;

            // 文件名不使用网络提供的版本；版本与安装包地址仍在下载边界严格校验。
            string target = Path.Combine(Path.GetTempPath(), "AuroraPlayer-Setup-" + Guid.NewGuid().ToString("N") + ".exe");
            toast("开始下载 v" + info.Version + "…");
            try
            {
                await UpdateChecker.DownloadSetupAsync(info, target,
                    percent => Post(() => toast("正在下载 v" + info.Version + "（" + percent + "%）…")), token);
                if (token.IsCancellationRequested) return;
                var install = System.Windows.Forms.MessageBox.Show(
                    "v" + info.Version + " 下载完成，立即运行安装器？",
                    "Aurora 更新", System.Windows.Forms.MessageBoxButtons.YesNo,
                    System.Windows.Forms.MessageBoxIcon.Question);
                if (token.IsCancellationRequested || install != System.Windows.Forms.DialogResult.Yes) return;
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                }
                catch (Exception ex) { MainViewModel.Dbg("run setup FAIL: " + ex.Message); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                MainViewModel.Dbg("update download FAIL: " + ex.Message);
                if (token.IsCancellationRequested) return;
                toast("下载失败，请到发布页手动下载");
                OpenReleasesPage();
            }
        }

        void OpenReleasesPage()
        {
            if (token.IsCancellationRequested) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    AppInfo.ReleasesUrl) { UseShellExecute = true });
            }
            catch (Exception ex) { MainViewModel.Dbg("open releases FAIL: " + ex.Message); }
        }

        /// <summary>单实例唤醒：定位到被打开的文件（同目录已在列表则直接切歌，否则载入目录）。</summary>
        public void OpenExternalFile(string path)
        {
            if (token.IsCancellationRequested) return;
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
            string dir = LibraryImportController.SafeDir(path);
            if (dir == null) return;
            Track current = vm.CurrentTrack;
            if (current != null && string.Equals(LibraryImportController.SafeDir(current.FilePath), dir, StringComparison.OrdinalIgnoreCase))
            {
                Track t = LibraryImportController.FindByPath(vm.View, path) ?? LibraryImportController.FindByPath(vm.Tracks, path);
                if (t != null) { vm.PlayTrack(t, true); return; }
            }
            importer.LoadDirectory(dir, path, false);
        }

        void Post(Action action)
        {
            if (token.IsCancellationRequested || win.Dispatcher.HasShutdownStarted) return;
            try
            {
                win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (!token.IsCancellationRequested) action();
                }));
            }
            catch (InvalidOperationException) { }
        }

        void OnClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            win.Closed -= OnClosed;
            lifetime.Cancel();
            pipeServer?.Dispose();
            lifetime.Dispose();
        }
    }
}
