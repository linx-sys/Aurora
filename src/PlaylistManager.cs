/* ============================================================
 * PlaylistManager.cs — 播放列表管理器
 * 从 MainViewModel 抽离，负责曲目添加、删除、排序、搜索筛选。
 * 维护 Tracks（全量）和 View（筛选/排序后）两个可观察集合。
 * ============================================================ */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Aurora
{
    /// <summary>
    /// 支持批量替换的 ObservableCollection：ReplaceAll 直接操作底层存储，
    /// 最后只发一次 Reset 通知。避免 Clear + 逐个 Add 时每条 Add 都触发一次
    /// CollectionChanged → ListBox 逐项重建的 O(n²) 开销（搜索输入时最明显）。
    /// </summary>
    public class BatchObservableCollection<T> : ObservableCollection<T>
    {
        /// <summary>整体替换全部元素，只发出一次 Reset 集合变更通知。</summary>
        public void ReplaceAll(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (T item in items)
                Items.Add(item);
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
                System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>
    /// 播放列表管理器。封装曲目集合的增删改查、排序和搜索筛选逻辑。
    /// UI 通过 Binding 绑定 View 属性，操作通过方法调用。
    /// </summary>
    public class PlaylistManager
    {
        /// <summary>全量播放列表（可观察集合，UI 自动更新）。</summary>
        public BatchObservableCollection<Track> Tracks { get; private set; }

        /// <summary>筛选/排序后的视图（UI 绑定此集合）。</summary>
        public BatchObservableCollection<Track> View { get; private set; }

        private string _searchText = "";
        // 0=文件名 1=标题 2=歌手 3=时长升序 4=时长降序 5=自定义（拖动排序，保留 Tracks 原序）
        private int _sortMode;

        public PlaylistManager()
        {
            Tracks = new BatchObservableCollection<Track>();
            View = new BatchObservableCollection<Track>();
        }

        /// <summary>搜索关键词（设置后自动刷新 View）。</summary>
        public string SearchText
        {
            get => _searchText;
            set { _searchText = value ?? ""; RefreshView(); }
        }

        /// <summary>排序模式（设置后自动刷新 View）。</summary>
        public int SortMode
        {
            get => _sortMode;
            set { _sortMode = value; RefreshView(); }
        }

        /// <summary>整体替换全量列表（换目录扫描后重建），单次批量通知。</summary>
        public void ReplaceTracks(IEnumerable<Track> tracks)
        {
            Tracks.ReplaceAll(tracks);
            RefreshView();
        }

        /// <summary>添加曲目（自动去重，按文件路径判断）。</summary>
        public int AddRange(IEnumerable<Track> tracks)
        {
            if (tracks == null) return 0;
            int added = 0;
            foreach (var t in tracks)
            {
                if (t == null) continue;
                bool exists = Tracks.Any(x => string.Equals(x.FilePath, t.FilePath, StringComparison.OrdinalIgnoreCase));
                if (!exists) { Tracks.Add(t); added++; }
            }
            if (added > 0) RefreshView();
            return added;
        }

        /// <summary>删除曲目。</summary>
        public bool Remove(Track t)
        {
            if (t == null) return false;
            bool removed = Tracks.Remove(t);
            if (removed)
            {
                View.Remove(t); // 直接从 View 移除，避免全量刷新
            }
            return removed;
        }

        /// <summary>清空播放列表。</summary>
        public void Clear()
        {
            Tracks.Clear();
            View.Clear();
        }

        /// <summary>移动曲目位置（拖动排序）。</summary>
        public void Move(int oldIndex, int newIndex)
        {
            if (oldIndex < 0 || oldIndex >= Tracks.Count) return;
            if (newIndex < 0 || newIndex >= Tracks.Count) return;
            if (oldIndex == newIndex) return;
            Tracks.Move(oldIndex, newIndex);
            if (_sortMode == 0) RefreshView(); // 默认（文件名）排序下立即同步 View；自定义(5)由拖动流程显式刷新
        }

        /// <summary>根据文件路径查找曲目。</summary>
        public Track? FindByPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            return Tracks.FirstOrDefault(t =>
                string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>重新计算筛选/排序后的 View。</summary>
        public void RefreshView()
        {
            IEnumerable<Track> filtered = Tracks.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(_searchText))
            {
                string q = _searchText.Trim();
                filtered = filtered.Where(t =>
                    (t.Title + " " + t.Artist + " " + t.Album + " " + t.FileName)
                    .IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            var sorted = _sortMode switch
            {
                1 => filtered.OrderBy(t => t.Title, StringComparer.CurrentCultureIgnoreCase),
                2 => filtered.OrderBy(t => t.Artist, StringComparer.CurrentCultureIgnoreCase),
                3 => filtered.OrderBy(t => t.Duration),
                4 => filtered.OrderByDescending(t => t.Duration),
                5 => filtered,   // 自定义顺序（拖动排序）：不排序，保留 Tracks 原始顺序
                _ => filtered.OrderBy(t => t.FileName, StringComparer.CurrentCultureIgnoreCase)
            };

            // 批量替换：单次 Reset 通知，避免逐项 Add 的 O(n²) 集合通知
            View.ReplaceAll(sorted);
        }
    }
}
