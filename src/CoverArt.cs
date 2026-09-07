/* ============================================================
 * CoverArt.cs — 封面获取与程序化生成
 * 有内嵌封面 → 解码缩放；无封面 → 按歌名哈希选配色，
 * 生成“渐变底 + 歌名首字 + 底部信息”的专属封面。
 * ============================================================ */
using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Aurora
{
    static class CoverArt
    {
        static readonly Color[][] Palettes = new Color[][]
        {
            new[] { Color.FromRgb(0x4C, 0xC9, 0xF0), Color.FromRgb(0x7C, 0x5C, 0xFF) },
            new[] { Color.FromRgb(0xF4, 0x72, 0xB6), Color.FromRgb(0x7C, 0x5C, 0xFF) },
            new[] { Color.FromRgb(0x34, 0xD3, 0x99), Color.FromRgb(0x0E, 0xA5, 0xE9) },
            new[] { Color.FromRgb(0xFB, 0xBF, 0x24), Color.FromRgb(0xF4, 0x72, 0xB6) },
            new[] { Color.FromRgb(0xA7, 0x8B, 0xFA), Color.FromRgb(0xF4, 0x72, 0xB6) },
            new[] { Color.FromRgb(0x60, 0xA5, 0xFA), Color.FromRgb(0x34, 0xD3, 0x99) },
            new[] { Color.FromRgb(0xF8, 0x71, 0x71), Color.FromRgb(0xFB, 0xBF, 0x24) },
            new[] { Color.FromRgb(0x2D, 0xD4, 0xBF), Color.FromRgb(0x81, 0x8C, 0xF8) },
            new[] { Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xF8, 0x71, 0x71) },
            new[] { Color.FromRgb(0x81, 0x8C, 0xF8), Color.FromRgb(0x60, 0xA5, 0xFA) },
        };

        /// <summary>按歌名取配色组（黑胶彩胶颜色与生成封面共用）。</summary>
        public static Color[] PaletteFor(string title, string artist)
        {
            if (string.IsNullOrEmpty(title)) title = "?";
            int idx = Math.Abs(StableHash(title + "|" + (artist ?? ""))) % Palettes.Length;
            return Palettes[idx];
        }

        /// <summary>轨道封面：内嵌封面优先（缩放到 size），否则程序化生成。</summary>
        public static ImageSource ForTrack(Track t, int size)
        {
            if (t == null) return null;
            if (t.Cover != null && t.Cover.Length > 64)
            {
                ImageSource img = FromBytes(t.Cover, size);
                if (img != null) return img;
            }
            try { return Generated(t.Title, t.Artist, size); }
            catch { return null; }
        }

        /// <summary>内嵌封面字节解码（失败返回 null）。</summary>
        public static ImageSource FromBytes(byte[] cover, int size)
        {
            if (cover == null || cover.Length <= 64) return null;
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.DecodePixelWidth = size;
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = new MemoryStream(cover);
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
            catch { return null; }
        }

        /// <summary>黑胶圆心专用：纯图形版（无底部文字，避免圆形裁切）。</summary>
        public static ImageSource GeneratedPlain(string title, string artist, int size)
        {
            try { return GeneratedCore(title, artist, size, false); }
            catch { return null; }
        }

        /// <summary>生成封面：对角渐变底 + 歌名首字 + 底部歌曲信息。</summary>
        public static ImageSource Generated(string title, string artist, int size)
        {
            try { return GeneratedCore(title, artist, size, true); }
            catch { return null; }
        }

        static ImageSource GeneratedCore(string title, string artist, int size, bool withText)
        {
            if (string.IsNullOrEmpty(title)) title = "?";
            int idx = Math.Abs(StableHash(title + "|" + (artist ?? ""))) % Palettes.Length;
            Color c1 = Palettes[idx][0], c2 = Palettes[idx][1];

            var dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                var full = new Rect(0, 0, size, size);
                dc.DrawRectangle(new LinearGradientBrush(c1, c2, 35), null, full);

                // 柔光斑（右上）
                dc.PushTransform(new TranslateTransform(size * 0.45, -size * 0.25));
                var glow = new RadialGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(0, 255, 255, 255));
                dc.DrawEllipse(glow, null, new Point(0, 0), size * 0.55, size * 0.55);
                dc.Pop();

                // 中央歌名首字（半透明白）
                string ch = FirstGrapheme(title).ToUpperInvariant();
                var face = new Typeface(new FontFamily("微软雅黑, Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
                var ft = new FormattedText(ch, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    face, size * 0.44, new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)));
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2 - size * 0.06));

                if (withText && size >= 256)
                {
                    // 底部：歌名 + 歌手（加深色渐变条保证可读）
                    double pad = size * 0.085;
                    var f1 = new FormattedText(Trim(title, 14), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        face, size * 0.062, Brushes.White);
                    var f2face = new Typeface(new FontFamily("微软雅黑, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    var f2 = new FormattedText(Trim(artist, 20), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        f2face, size * 0.042, new SolidColorBrush(Color.FromArgb(210, 255, 255, 255)));
                    double y = size - size * 0.155;
                    var shade = new LinearGradientBrush();
                    shade.StartPoint = new Point(0, 0);
                    shade.EndPoint = new Point(0, 1);
                    shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0));
                    shade.GradientStops.Add(new GradientStop(Color.FromArgb(130, 0, 0, 0), 1));
                    dc.DrawRectangle(shade, null, new Rect(0, y - size * 0.10, size, size * 0.25));
                    dc.DrawText(f1, new Point(pad, y));
                    dc.DrawText(f2, new Point(pad, y + f1.Height + size * 0.012));
                }

                // 内描边
                double b = Math.Max(1, size / 256.0);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(45, 255, 255, 255)), b),
                    new Rect(b / 2, b / 2, size - b, size - b));
            }

            var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        static string FirstGrapheme(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "♪";
            return s.Substring(0, 1);
        }

        static string Trim(string s, int max)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return "未知歌手";
            if (s.Length <= max) return s;
            return s.Substring(0, max - 1) + "…";
        }

        static int StableHash(string s)
        {
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return h;
            }
        }
    }
}
