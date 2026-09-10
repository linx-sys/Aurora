/* ============================================================
 * AudioDiagnosticsDialog.cs — 音频诊断面板（阶段 7）
 * 展示当前音频链路快照：解码格式 / 采样率 / 位深 / 声道 / ReplayGain /
 * 跨淡设置 / 输出设备 / 会话信息，便于用户反馈与 Bug 定位。
 * 数据由 PlayerEngine.GetDiagnostics() 组装；提供"打开日志文件夹"入口（阶段 8）。
 * 纯代码构建（与 NetMatchSettingsDialog 同风格，配色随当前主题）。
 * ============================================================ */
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Aurora
{
    public static class AudioDiagnosticsDialog
    {
        public static void Show(Window owner, bool dark, string diagnosticsText)
        {
            var panelBg = new SolidColorBrush(dark ? Color.FromRgb(0x10, 0x13, 0x1B) : Color.FromRgb(0xFB, 0xFC, 0xFE));
            var textBrush = new SolidColorBrush(dark ? Color.FromRgb(0xE9, 0xED, 0xF6) : Color.FromRgb(0x1A, 0x20, 0x30));
            var dimBrush = new SolidColorBrush(dark ? Color.FromRgb(0x8A, 0x93, 0xA8) : Color.FromRgb(0x5A, 0x64, 0x74));

            var dlg = new Window
            {
                Title = "音频诊断",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                Background = panelBg,
            };

            var root = new StackPanel { Margin = new Thickness(20) };

            root.Children.Add(new TextBlock
            {
                Text = "当前音频链路快照（反馈问题时请截图或复制本内容，并附日志）：",
                Foreground = dimBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
            });

            var textBox = new TextBox
            {
                Text = diagnosticsText ?? "（无诊断数据）",
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                FontSize = 12,
                Foreground = textBrush,
                Background = dark ? new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x14)) : Brushes.White,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8),
                MaxHeight = 320,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                TextWrapping = TextWrapping.NoWrap,
                IsUndoEnabled = false,
            };
            root.Children.Add(textBox);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 14, 0, 0),
            };

            var logBtn = new Button
            {
                Content = "打开日志文件夹",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            logBtn.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(Logger.LogDir) { UseShellExecute = true });
                }
                catch (Exception ex) { MainViewModel.Dbg("open log dir FAIL: " + ex.Message); }
            };
            btnRow.Children.Add(logBtn);

            var copyBtn = new Button
            {
                Content = "复制",
                Padding = new Thickness(12, 5, 12, 5),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(textBox.Text);
                    copyBtn.Content = "已复制";   // 视觉反馈（对话框无 toast 通道）
                }
                catch { }
            };
            btnRow.Children.Add(copyBtn);

            var doneBtn = new Button
            {
                Content = "完成",
                Padding = new Thickness(20, 5, 20, 5),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            doneBtn.Click += (s, e) => dlg.Close();
            btnRow.Children.Add(doneBtn);

            root.Children.Add(btnRow);
            dlg.Content = root;
            dlg.ShowDialog();
        }
    }
}
