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
        readonly Action<string> toast;
        readonly Action<Track, bool> playTrack;   // 当前曲目切换（业务在 ViewModel.PlayTrack）
        bool isLoadingDir;

        public LibraryImportController(Window win, MainViewModel vm, Action<string> toast, Action<Track, bool> playTrack)
        {
            this.win = win;
            this.vm = vm;
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

        /// <summary>加载目录（替换现有列表）。</summary>
        public void LoadDirectory(string dir, string autoPlayPath, bool silent)
        {
            if (isLoadingDir) return;
            isLoadingDir = true;
            if (!silent) toast("正在扫描文件夹…");
            string d = dir;
            string auto = autoPlayPath;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var files = new List<string>();
                Library.EnumerateFiles(d, files, 0);
                List<Track> built = Library.BuildTracks(files);
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
                List<Track> built = Library.BuildTracks(files);
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
