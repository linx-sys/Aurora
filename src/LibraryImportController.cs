#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * LibraryImportController.cs — 媒体库导入控制器
 * 从 MainWindow.cs 拆出（原属"媒体库加载"职责）。
 * 负责文件夹选择、目录扫描（后台线程）、拖放导入、播放恢复；
 * 扫描结果统一写入 ViewModel.Playlist（唯一数据源）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace Aurora
{
    public class LibraryImportController
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly ILibraryStore store;   // 音乐库正式核心存储（P1-3：DB 优先秒开 + 差分同步）
        readonly Action<string> toast;
        readonly Action<Track, bool> playTrack;   // 当前曲目切换（业务在 ViewModel.PlayTrack）
        bool isLoadingDir;

        public LibraryImportController(Window win, MainViewModel vm, ILibraryStore store,
            Action<string> toast, Action<Track, bool> playTrack)
        {
            this.win = win;
            this.vm = vm;
            this.store = store;
            this.toast = toast;
            this.playTrack = playTrack;
        }

        public static string SafeDir(string path)
        {
            try { return IOPath.GetDirectoryName(path); } catch { return null; }
        }

        public void PickFolder()
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "选择存放音乐的文件夹";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    LoadDirectory(dlg.SelectedPath, null, false);
            }
        }

        /// <summary>
        /// 加载目录（替换现有列表）。P1-3 两段式：
        /// ① DB 优先秒开——库行直接物化列表立即填充（仅视觉，不触发播放）；
        /// ② 后台差分同步——枚举文件系统做指纹差分 upsert/清理，权威结果整表替换并恢复播放状态。
        /// </summary>
        public void LoadDirectory(string dir, string autoPlayPath, bool silent)
        {
            if (isLoadingDir) return;
            isLoadingDir = true;
            if (!silent) toast("正在扫描文件夹…");
            string d = dir;
            string auto = autoPlayPath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                // ① DB 秒开（有缓存行时几乎瞬时出列表）。零探测快路径：
                //    不做存在性/lrc/外部封面探测（超大库逐文件探测是启动瓶颈）——
                //    已删文件成为短命幽灵行、lrc 与外部封面由 ② 权威同步补齐
                List<TrackRow> cached = store.GetByPrefix(d);
                if (cached.Count > 0)
                {
                    List<Track> quick = Library.BuildTracksFromRows(cached, probeExtras: false);
                    if (quick.Count > 0)
                    {
                        win.Dispatcher.BeginInvoke((Action)(() =>
                        {
                            vm.SearchText = "";   // 换目录清空搜索词，避免旧词把新列表全过滤掉
                            vm.Playlist.ReplaceTracks(quick);   // 单次批量刷新（唯一数据源）
                        }));
                    }
                }

                // ② 差分同步（权威）：枚举 → 指纹差分 upsert / 清理过期行 → 整表替换
                var files = new List<string>();
                Library.EnumerateFiles(d, files, 0);
                List<Track> built = Library.BuildTracksIncremental(files, store, d);
                win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    isLoadingDir = false;
                    vm.ResetShuffleHistory();   // 换目录清空随机播放轨迹
                    vm.SearchText = "";   // 换文件夹清空搜索词，避免旧词把新列表全过滤掉（绑定回写搜索框）
                    vm.Playlist.ReplaceTracks(built);   // 整表替换 + 单次批量刷新（唯一数据源）
                    Settings.Set("lastDir", d);
                    var view = vm.View;
                    var tracks = vm.Tracks;
                    if (view.Count == 0)
                    {
                        if (!silent) toast("文件夹里没有找到音频文件");
                        return;
                    }
                    if (!silent) toast("已加载 " + view.Count + " 首歌曲");

                    if (auto != null)
                    {
                        Track hit = FindByPath(view, auto) ?? FindByPath(tracks, auto);
                        if (hit != null) { playTrack(hit, true); return; }
                    }

                    // 恢复上次播放的曲目（不自动出声），否则定位到第一首
                    string lastTrack = Settings.Get("lastTrack", "");
                    Track restore = lastTrack.Length > 0 ? (FindByPath(view, lastTrack) ?? FindByPath(tracks, lastTrack)) : null;
                    if (restore == null) restore = view[0];
                    playTrack(restore, false);
                }));
            });
        }

        /// <summary>拖放 / 添加文件：追加导入。</summary>
        public void ImportPaths(string[] paths, bool append)
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
                List<Track> built = Library.BuildTracksIncremental(files, store, null);   // 追加导入：只 upsert 不清理
                win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    int added = vm.Playlist.AddRange(built);   // 去重 + 单次批量刷新（唯一数据源）
                    if (added > 0) toast("已添加 " + added + " 首歌曲");
                    else toast("没有新增的歌曲");
                }));
            });
        }

        public static Track FindByPath(IEnumerable<Track> list, string path)
        {
            foreach (Track t in list)
                if (string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }
}
