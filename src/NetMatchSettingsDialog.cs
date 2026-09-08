/* ============================================================
 * NetMatchSettingsDialog.cs — 联网匹配设置对话框
 * 从 MainWindow.cs 拆出（原属"文件关联相关 UI/对话框"职责）。
 * 按音频格式开关联网匹配 + 清除歌词/封面缓存。
 * 纯代码构建（一次性 UI，不新增 BAML 资源）；配色随当前主题。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Aurora
{
    public static class NetMatchSettingsDialog
    {
        static readonly string[] NetMatchFormats = { "mp3", "flac", "m4a", "wav", "wma", "ogg", "oga", "opus", "aac" };

        /// <summary>
        /// 显示联网匹配设置对话框。
        /// onCacheCleared：用户清除缓存后回调（MainWindow 借此清空本次会话的失败标记）。
        /// toast：提示回调。
        /// </summary>
        public static void Show(Window owner, bool dark, Action onCacheCleared, Action<string> toast)
        {
            var panelBg = new SolidColorBrush(dark ? Color.FromRgb(0x10, 0x13, 0x1B) : Color.FromRgb(0xFB, 0xFC, 0xFE));
            var textBrush = new SolidColorBrush(dark ? Color.FromRgb(0xE9, 0xED, 0xF6) : Color.FromRgb(0x1A, 0x20, 0x30));
            var dimBrush = new SolidColorBrush(dark ? Color.FromRgb(0x8A, 0x93, 0xA8) : Color.FromRgb(0x5A, 0x64, 0x74));

            var dlg = new Window
            {
                Title = "联网匹配设置",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 430,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = panelBg,
            };

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "播放缺少歌词 / 封面的歌曲时，自动联网匹配并缓存到本地。\n按音频格式选择是否启用：",
                Foreground = dimBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 3×3 格式复选框（即时写入设置）
            var grid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(0, 0, 0, 14) };
            foreach (string ext in NetMatchFormats)
            {
                string key = NetMatch.SettingsKeyForExt(ext);
                var cb = new CheckBox
                {
                    Content = ext.ToUpperInvariant(),
                    IsChecked = Settings.Get(key, "1") != "0",
                    Foreground = textBrush,
                    Margin = new Thickness(4, 6, 4, 6),
                    VerticalContentAlignment = VerticalAlignment.Center,
                };
                cb.Checked += (s, e) => Settings.Set(key, "1");
                cb.Unchecked += (s, e) => Settings.Set(key, "0");
                grid.Children.Add(cb);
            }
            root.Children.Add(grid);

            root.Children.Add(new Separator
            {
                Background = dimBrush,
                Opacity = 0.3,
                Margin = new Thickness(0, 0, 0, 12),
            });

            // 缓存行：统计文本 + 清除按钮
            var cacheText = new TextBlock { Foreground = dimBrush, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Action updateCacheText = () =>
            {
                var st = NetMatch.CacheStats();
                cacheText.Text = "缓存：" + st.files + " 项（" + (st.bytes / 1048576.0).ToString("0.0") + " MB）";
            };
            updateCacheText();

            var clearBtn = new Button
            {
                Content = "清除缓存",
                Padding = new Thickness(12, 5, 12, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            clearBtn.Click += (s, e) =>
            {
                var removed = NetMatch.ClearCache();
                if (onCacheCleared != null) onCacheCleared();   // 已删除缓存，允许本次会话内重新匹配
                cacheText.Text = "已清除 " + removed.files + " 项（" + (removed.bytes / 1048576.0).ToString("0.0") + " MB）";
                if (toast != null) toast("已清除歌词/封面缓存：" + removed.files + " 项");
            };

            var cacheRow = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(clearBtn, Dock.Right);
            cacheRow.Children.Add(clearBtn);
            cacheRow.Children.Add(cacheText);
            root.Children.Add(cacheRow);

            var doneBtn = new Button
            {
                Content = "完成",
                Padding = new Thickness(20, 5, 20, 5),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            doneBtn.Click += (s, e) => dlg.Close();
            root.Children.Add(doneBtn);

            dlg.Content = root;
            dlg.ShowDialog();
        }
    }
}
