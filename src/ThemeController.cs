/* ============================================================
 * ThemeController.cs — 主题控制器
 * 从 MainWindow.cs 拆出（原属"主题"职责）。
 * 负责深/浅色主题的资源画布注入、图标切换、持久化与切换联动。
 * ============================================================ */
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IOPath = System.IO.Path;

namespace Aurora
{
    public class ThemeController
    {
        readonly Grid root;
        readonly Window win;
        readonly System.Windows.Shapes.Path icoMoon, icoSun;
        readonly Action onApplied;   // 主题应用后的联动（歌词重染等）

        public bool IsDark { get; private set; } = true;

        public ThemeController(Grid root, Window win,
            System.Windows.Shapes.Path icoMoon, System.Windows.Shapes.Path icoSun,
            Action onApplied)
        {
            this.root = root;
            this.win = win;
            this.icoMoon = icoMoon;
            this.icoSun = icoSun;
            this.onApplied = onApplied;
        }

        static void SetBrush(Grid r, string key, bool dark, uint darkArgb, uint lightArgb)
        {
            uint v = dark ? darkArgb : lightArgb;
            var brush = new SolidColorBrush(Color.FromArgb(
                (byte)((v >> 24) & 0xFF), (byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            brush.Freeze();
            r.Resources[key] = brush;
        }

        /// <summary>应用主题（dark=true 深色）；并触发 onApplied 联动。</summary>
        public void Apply(bool dark)
        {
            try
            {
                IsDark = dark;   // 必须同步状态，否则 Toggle 会一直切向同一边
                SetBrush(root, "Bg", dark, 0xFF0B0D12, 0xFFF2F4F9);
                SetBrush(root, "Panel", dark, 0xFF10131B, 0xFFFBFCFE);
                SetBrush(root, "Bar", dark, 0xCC0D1017, 0xE6FFFFFF);
                SetBrush(root, "Field", dark, 0x0FFFFFFF, 0x0D1F2937);
                SetBrush(root, "FieldLine", dark, 0x18FFFFFF, 0x1A94A3B8);
                SetBrush(root, "Line", dark, 0x1FFFFFFF, 0x1F94A3B8);
                SetBrush(root, "Hover", dark, 0x14FFFFFF, 0x14000000);
                SetBrush(root, "TrackBg", dark, 0x16FFFFFF, 0x14000000);
                SetBrush(root, "Card", dark, 0xF01B2030, 0xF5FFFFFF);
                SetBrush(root, "Ph", dark, 0x33FFFFFF, 0x2E94A3B8);
                SetBrush(root, "Text", dark, 0xFFE9EDF6, 0xFF1A2030);
                SetBrush(root, "Dim", dark, 0xFF8A93A8, 0xFF5A6474);
                SetBrush(root, "Dim2", dark, 0xFF5D6579, 0xFF8B95A7);
                SetBrush(root, "Accent", dark, 0xFF4CC9F0, 0xFF0E8AB8);
                SetBrush(root, "Pink", dark, 0xFFF472B6, 0xFFDB2777);
                win.Background = (Brush)root.Resources["Bg"];
                icoMoon.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;
                icoSun.Visibility = dark ? Visibility.Collapsed : Visibility.Visible;
                if (onApplied != null) onApplied();
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(IOPath.Combine(IOPath.GetTempPath(), "aurora_theme_err.txt"),
                        "root=" + (root == null) + " icoMoon=" + (icoMoon == null) + " icoSun=" + (icoSun == null)
                        + " win=" + (win == null) + "\r\n" + ex);
                }
                catch { }
                throw;
            }
        }

        /// <summary>切换主题并持久化；toast 用于提示（Aurora 无返回值 UI 通道）。</summary>
        public void Toggle(Action<string> toast)
        {
            Apply(!IsDark);
            Settings.Set("theme", IsDark ? "dark" : "light");
            if (toast != null) toast(IsDark ? "已切换到深色模式" : "已切换到浅色模式");
        }
    }
}
