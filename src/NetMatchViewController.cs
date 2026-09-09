/* ============================================================
 * NetMatchViewController.cs — 联网匹配歌词/封面视图控制（从 MainWindow 拆出）
 * 后台线程执行不阻塞播放；缓存命中则不联网；
 * 结果回 UI 线程刷新歌词渲染与黑胶封面；确认不存在本会话不再重试。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;

namespace Aurora
{
    class NetMatchViewController
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly LyricsViewController lyrics;
        readonly Action<Track> updateTrackInfo;
        readonly Action<string> toast;
        readonly Func<bool> isDark;

        readonly HashSet<string> netMatchFailed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public NetMatchViewController(Window win, MainViewModel vm, LyricsViewController lyrics,
            Action<Track> updateTrackInfo, Action<string> toast, Func<bool> isDark)
        {
            this.win = win;
            this.vm = vm;
            this.lyrics = lyrics;
            this.updateTrackInfo = updateTrackInfo;
            this.toast = toast;
            this.isDark = isDark;
        }

        public void TryNetMatch(Track t)
        {
            if (t == null || netMatchFailed.Contains(t.FilePath)) return;
            // 按格式的联网匹配开关（联网匹配设置中配置，默认全部启用）
            if (!NetMatch.EnabledFor(t.FilePath)) return;
            bool needLyrics = !t.HasLrc && NetMatch.FindLyricFile(t.FilePath) == null;
            bool needCover = (t.Cover == null || t.Cover.Length == 0) && !NetMatch.HasCover(t.FilePath);
            if (!needLyrics && !needCover) return;

            NetMatch.Start(t.FilePath, t.Title, t.Artist, result =>
            {
                win.Dispatcher.BeginInvoke((Action)(() => OnNetMatchDone(t, result)));
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
                toast("已联网匹配" + string.Join("、", parts) + "（" + r.Source + "）");
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
            NetMatchSettingsDialog.Show(win, isDark(), onCacheCleared: netMatchFailed.Clear, toast: toast);
        }
    }
}
