#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * PlaylistViewController.cs — 播放列表视图控制器
 * 从 MainWindow.cs 拆出（原属"播放列表交互"职责）。
 * 负责抽屉开合动画、拖动排序、删除、排序切换、计数与选中联动。
 * 列表交互（拖拽/点击定位）本质是 View 行为，做成 View-specific Controller；
 * 播放业务入口仍是 ViewModel.PlayTrack / ViewModel.DeleteTrack（唯一数据源）。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aurora
{
    public class PlaylistViewController
    {
        readonly Button btnSort;
        readonly ListBox list;
        readonly Border listPanel;
        readonly StackPanel capBar;
        readonly TextBox searchBox;
        readonly TextBlock searchHint;
        readonly TextBlock listCount;
        readonly MainViewModel vm;
        readonly IPlaybackService player;
        readonly Action<string> toast;

        bool listOpen;

        /// <summary>列表面板当前是否展开。</summary>
        public bool IsOpen { get { return listOpen; } }

        /// <summary>面板元素（供窗口拖动判定"点击在面板左侧=收起"使用）。</summary>
        public Border PanelElement { get { return listPanel; } }

        public PlaylistViewController(Button btnSort, ListBox list, Border listPanel, StackPanel capBar,
            TextBox searchBox, TextBlock searchHint, TextBlock listCount,
            MainViewModel vm, IPlaybackService player, Action<string> toast)
        {
            this.btnSort = btnSort;
            this.list = list;
            this.listPanel = listPanel;
            this.capBar = capBar;
            this.searchBox = searchBox;
            this.searchHint = searchHint;
            this.listCount = listCount;
            this.vm = vm;
            this.player = player;
            this.toast = toast;

            // 回收虚拟化只创建可见行；不能用普通 Panel 换掉默认面板。
            list.ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(VirtualizingStackPanel)));
            VirtualizingPanel.SetIsVirtualizing(list, true);
            VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
            ScrollViewer.SetCanContentScroll(list, true);

            HookEvents();
            UpdateSortHint();
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(vm.SortMode)) UpdateSortHint();
            };
            listCount.Text = "共 0 首歌曲";
        }

        void UpdateSortHint()
        {
            btnSort.ToolTip = "排序方式：" + PlaylistManager.GetSortModeName(vm.SortMode);
        }

        void HookEvents()
        {
            btnSort.Click += (s, e) =>
            {
                vm.SortMode = (vm.SortMode + 1) % PlaylistManager.SortModeCount;
                UpdateSortHint();
                toast("排序：" + PlaylistManager.GetSortModeName(vm.SortMode));
            };

            // 列表计数跟随 View 集合变化（排序/搜索/导入/删除/拖动全覆盖）
            vm.View.CollectionChanged += (s, e) =>
                listCount.Text = "共 " + vm.View.Count + " 首歌曲";

            list.SelectionChanged += (s, e) =>
            {
                Track t = list.SelectedItem as Track;
                MainViewModel.Dbg("ListSelectionChanged: " + (t != null ? t.Title : "null") + " current=" + (vm.CurrentTrack != null ? vm.CurrentTrack.Title : "null"));
                if (t != null && t != vm.CurrentTrack) vm.PlayTrack(t, true);
            };

            // ===== 播放列表：拖动排序 + 删除 =====
            Point dragStart = new Point();
            Track dragItem = null;
            list.PreviewMouseLeftButtonDown += (s, e) =>
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
                dragStart = e.GetPosition(list);
                dragItem = TrackFromPoint(dragStart);
            };
            list.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed) return;
                Point pos = e.GetPosition(list);
                if (Math.Abs(pos.X - dragStart.X) < 5 && Math.Abs(pos.Y - dragStart.Y) < 5) return;
                if (dragItem == null) dragItem = TrackFromPoint(pos);  // 按下时没取到则移动时补取
                if (dragItem == null) return;
                Track item = dragItem;
                dragItem = null;
                DragDrop.DoDragDrop(list, item, DragDropEffects.Move);
                e.Handled = true;
            };
            list.DragOver += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(Track))) return;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;
            };
            list.Drop += (s, e) =>
            {
                if (!e.Data.GetDataPresent(typeof(Track))) { e.Handled = true; return; }
                Track dragged = e.Data.GetData(typeof(Track)) as Track;
                if (dragged == null) { e.Handled = true; return; }
                // 搜索过滤时 view 是子集，拖动排序会导致 tracks 索引错位，禁止并提示
                if (searchBox != null && searchBox.Text.Trim().Length > 0)
                {
                    toast("请先清空搜索再拖动排序");
                    e.Handled = true;
                    return;
                }
                Point pos = e.GetPosition(list);
                // DropIndexFromPoint 返回的是"视图(view)中的插入位置"，而 Move 操作的是
                // tracks（顺序通常与 view 不同：文件名/标题等排序）。必须经目标曲引用
                // 换算回 tracks 坐标系，否则非默认排序下拖动会跳到完全错误的位置
                // （实测：标题排序下拖到列表顶部，结果被移到第 8 位）
                int viewIdx = DropIndexFromPoint(pos);
                var tracks = vm.Tracks;
                var view = vm.View;
                int oldIdx = tracks.IndexOf(dragged);
                int targetIdx = viewIdx >= view.Count ? tracks.Count : tracks.IndexOf(view[viewIdx]);
                if (targetIdx < 0) targetIdx = tracks.Count;
                if (oldIdx >= 0 && targetIdx != oldIdx)
                {
                    if (targetIdx > oldIdx) targetIdx--;
                    vm.SortMode = 5;   // 拖动后进入自定义顺序（setter 内已刷新一次）
                    vm.Playlist.Move(oldIdx, targetIdx);
                    vm.RefreshView();
                    toast("已移到第 " + (targetIdx + 1) + " 位");
                }
                // 当前音频输出由引擎持有；拖放不直接发播放命令，避免绕过协调器。
                e.Handled = true;
            };
            list.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete || e.Key == Key.Back)
                {
                    Track t = list.SelectedItem as Track;
                    if (t != null) { DeleteTrack(t); e.Handled = true; }
                }
            };
        }

        /// <summary>播放列表抽屉开关（右侧滑入/滑出动画；打开时定位当前播放曲）。</summary>
        public void Toggle()
        {
            listOpen = !listOpen;
            // 面板从窗口顶部开始，与右上角窗口按钮重叠：打开时隐藏三连，关闭后恢复
            capBar.Visibility = listOpen ? Visibility.Collapsed : Visibility.Visible;
            var tt = listPanel.RenderTransform as TranslateTransform;
            if (tt == null)
            {
                tt = new TranslateTransform();
                listPanel.RenderTransform = tt;
            }
            var slide = new DoubleAnimation
            {
                From = listOpen ? 400 : 0,
                To = listOpen ? 0 : 400,
                Duration = TimeSpan.FromMilliseconds(listOpen ? 260 : 200),
                EasingFunction = new CubicEase
                {
                    EasingMode = listOpen ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };
            if (!listOpen)
            {
                slide.Completed += (s, e) =>
                {
                    if (!listOpen) listPanel.Visibility = Visibility.Collapsed;
                };
            }
            listPanel.Visibility = Visibility.Visible;
            tt.BeginAnimation(TranslateTransform.XProperty, slide);
            if (listOpen)
            {
                searchBox.Focus();
                if (vm.CurrentTrack != null)
                {
                    list.SelectedItem = vm.CurrentTrack;
                    list.ScrollIntoView(vm.CurrentTrack);
                }
            }
        }

        /// <summary>当前曲目变更时高亮跟随（含自动切歌）并滚动到可见处；面板关闭时不动。</summary>
        public void HighlightCurrent()
        {
            if (!listOpen || vm.CurrentTrack == null) return;
            if (list.SelectedItem != vm.CurrentTrack)
            {
                list.SelectedItem = vm.CurrentTrack;
                list.ScrollIntoView(vm.CurrentTrack);
            }
        }

        /// <summary>搜索占位提示显隐（搜索过滤本身在 PlaylistManager.SearchText setter 内完成）。</summary>
        public void UpdateSearchHint()
        {
            if (searchHint != null)
                searchHint.Visibility = vm.SearchText.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>从播放列表移除一首歌曲；业务下沉到 ViewModel（停止播放/清空当前曲/事件）。</summary>
        public void DeleteTrack(Track t)
        {
            if (t == null) return;
            vm.DeleteTrack(t);
            // 计数由 View.CollectionChanged 统一维护
            toast("已移除：" + t.Title);
        }

        /// <summary>从点击/拖拽坐标获取对应的 Track（遍历可视树找到 ListBoxItem 的 DataContext）。</summary>
        Track TrackFromPoint(Point pt)
        {
            IInputElement hit = list.InputHitTest(pt);
            DependencyObject dep = hit as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep == null) return null;
            return (dep as ListBoxItem).DataContext as Track;
        }

        /// <summary>计算拖放目标下标：根据鼠标位置找到目标项，判断落在该项上半/下半部分。</summary>
        int DropIndexFromPoint(Point pt)
        {
            IInputElement hit = list.InputHitTest(pt);
            DependencyObject dep = hit as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep == null) return vm.View.Count;  // 返回视图坐标，调用方统一转换
            ListBoxItem item = dep as ListBoxItem;
            Track t = item.DataContext as Track;
            int idx = vm.View.IndexOf(t);
            if (idx < 0) return vm.View.Count;
            // 落在该项下半部分 → 插到该项之后
            Point itemTop = item.TranslatePoint(new Point(0, 0), list);
            if (pt.Y > itemTop.Y + item.RenderSize.Height / 2) idx++;
            return idx;
        }
    }
}
