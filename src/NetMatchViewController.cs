#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * NetMatchViewController.cs — 联网匹配歌词/封面视图控制（从 MainWindow 拆出）
 * 后台线程执行不阻塞播放；缓存命中则不联网；
 * 结果回 UI 线程刷新歌词渲染与黑胶封面；确认不存在本会话不再重试。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;

namespace Aurora
{
    class NetMatchViewController : IDisposable
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly LyricsViewController lyrics;
        readonly Action<Track> updateTrackInfo;
        readonly Action<string> toast;
        readonly Func<bool> isDark;
        readonly Func<string> audioDiagnostics;   // 音频诊断文本提供器（阶段 7；可为 null）

        readonly HashSet<string> netMatchFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly CancellationToken token;
        long generation;
        bool disposed;

        public NetMatchViewController(Window win, MainViewModel vm, LyricsViewController lyrics,
            Action<Track> updateTrackInfo, Action<string> toast, Func<bool> isDark,
            Func<string> audioDiagnostics = null)
        {
            this.win = win;
            this.vm = vm;
            this.lyrics = lyrics;
            this.updateTrackInfo = updateTrackInfo;
            this.toast = toast;
            this.isDark = isDark;
            this.audioDiagnostics = audioDiagnostics;
            token = lifetime.Token;
            win.Closed += OnClosed;
        }

        public void TryNetMatch(Track t)
        {
            long requestGeneration = ++generation;
            if (token.IsCancellationRequested) return;
            if (t == null || netMatchFailed.Contains(t.FilePath)) return;
            // 按格式的联网匹配开关（联网匹配设置中配置，默认全部启用）
            if (!NetMatch.EnabledFor(t.FilePath)) return;
            bool needLyrics = !t.HasLrc && NetMatch.FindLyricFile(t.FilePath) == null;
            bool needCover = (t.Cover == null || t.Cover.Length == 0) && !NetMatch.HasCover(t.FilePath);
            if (!needLyrics && !needCover) return;

            NetMatch.Start(t.FilePath, t.Title, t.Artist, result =>
            {
                if (token.IsCancellationRequested || win.Dispatcher.HasShutdownStarted) return;
                try
                {
                    win.Dispatcher.BeginInvoke((Action)(() =>
                    {
                        if (!token.IsCancellationRequested && requestGeneration == generation) OnNetMatchDone(t, result);
                    }));
                }
                catch (InvalidOperationException) { }
            }, token);
        }

        void OnNetMatchDone(Track t, NetMatchResult r)
        {
            if (token.IsCancellationRequested || t == null || r == null) return;
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

            if (vm.CurrentTrack == t)
            {
                if (refreshLyrics) lyrics.Render(t);
                vm.PreloadNextIfNearEnd();   // P2：临结束预热下一首 / 到点跨淡切换
                if (refreshCover) updateTrackInfo(t);
            }

            if (r.LyricsMatched || r.CoverMatched)
            {
                var parts = new List<string>();
                if (r.LyricsMatched) parts.Add("歌词");
                if (r.CoverMatched) parts.Add("封面");
                if (vm.CurrentTrack == t) toast("已联网匹配" + string.Join("、", parts) + "（" + r.Source + "）");
            }
            else if (r.NotFound)
            {
                netMatchFailed.Add(t.FilePath);   // 确认不存在，本次会话不再重试
                if (vm.CurrentTrack == t) toast("未找到「" + t.Title + "」的歌词/封面");
            }
            else if (vm.CurrentTrack == t)
            {
                toast("联网匹配失败：" + r.Message);
            }
        }

        /// <summary>联网匹配设置对话框（清缓存时同步清本会话失败重试表）。</summary>
        public void ShowSettings()
        {
            if (token.IsCancellationRequested) return;
            NetMatchSettingsDialog.Show(win, isDark(), onCacheCleared: netMatchFailed.Clear,
                toast: message => { if (!token.IsCancellationRequested) toast(message); },
                audioDiagnostics: audioDiagnostics);
        }

        void OnClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ++generation;
            win.Closed -= OnClosed;
            lifetime.Cancel();
            lifetime.Dispose();
        }
    }
}
