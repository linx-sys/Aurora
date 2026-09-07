/* genicon.cs — Aurora 应用图标生成器（GDI+ 矢量绘制，输出多尺寸 PNG） */
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class GenIcon
{
    static void Main()
    {
        string outDir = AppDomain.CurrentDomain.BaseDirectory;
        using (var big = Render(512))
        {
            foreach (int s in new[] { 256, 128, 64, 48, 32, 16 })
            {
                using (var bmp = new Bitmap(s, s))
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(big, new Rectangle(0, 0, s, s));
                    bmp.Save(Path.Combine(outDir, "icon_" + s + ".png"), ImageFormat.Png);
                }
                Console.WriteLine("icon_" + s + ".png");
            }
        }
    }

    static Bitmap Render(int size)
    {
        var bmp = new Bitmap(size, size);
        float u = size / 256f; // 设计坐标 256
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            var full = new Rectangle(0, 0, size, size);
            using (var tile = RoundRect(6 * u, 6 * u, 244 * u, 244 * u, 58 * u))
            using (var g2 = Graphics.FromImage(bmp))
            {
                g2.SmoothingMode = SmoothingMode.AntiAlias;

                // ---- 深色底（对角微渐变）----
                using (var bg = new LinearGradientBrush(new Rectangle(0, 0, size, size),
                    Color.FromArgb(0xFF, 0x15, 0x1C, 0x30), Color.FromArgb(0xFF, 0x0A, 0x0C, 0x12), 40f))
                    g2.FillPath(bg, tile);

                // ---- 极光弧（三层辉光 + 主线）----
                g2.SetClip(tile);
                DrawAurora(g2, u);

                // ---- 音符（白，带柔和投影）----
                DrawNote(g2, u);

                // ---- 顶部高光 ----
                using (var hl = new LinearGradientBrush(new Rectangle(0, 0, size, (int)(70 * u)),
                    Color.FromArgb(26, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                    g2.FillRectangle(hl, 0, 0, size, (int)(70 * u));
                g2.ResetClip();

                // ---- 内描边 ----
                using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255), 2 * u))
                    g2.DrawPath(pen, tile);
            }
        }
        return bmp;
    }

    static void DrawAurora(Graphics g, float u)
    {
        using (var curve = new GraphicsPath())
        {
            curve.AddBezier(10 * u, 208 * u, 90 * u, 152 * u, 150 * u, 178 * u, 202 * u, 108 * u);
            curve.AddBezier(202 * u, 108 * u, 228 * u, 72 * u, 238 * u, 62 * u, 250 * u, 50 * u);

            // 辉光层（青、紫两道）
            using (var p1 = new Pen(Color.FromArgb(110, 0x4C, 0xC9, 0xF0), 40 * u))
            using (var p2 = new Pen(Color.FromArgb(120, 0x7C, 0x5C, 0xFF), 20 * u))
            {
                p1.StartCap = p1.EndCap = LineCap.Round;
                p2.StartCap = p2.EndCap = LineCap.Round;
                g.DrawPath(p1, curve);
                g.DrawPath(p2, curve);
            }

            // 主线（青→紫→粉 渐变）
            using (var lg = new LinearGradientBrush(new Rectangle(0, 0, (int)(256 * u), (int)(256 * u)),
                Color.FromArgb(255, 0x4C, 0xC9, 0xF0), Color.FromArgb(255, 0xF4, 0x72, 0xB6), 20f))
            using (var main = new Pen(lg, 11 * u))
            {
                main.StartCap = main.EndCap = LineCap.Round;
                g.DrawPath(main, curve);
            }
        }
    }

    static void DrawNote(Graphics g, float u)
    {
        using (var sh = new SolidBrush(Color.FromArgb(60, 0, 0, 0)))
            NoteShape(g, 3 * u, 5 * u, sh, u);
        using (var w = new SolidBrush(Color.White))
            NoteShape(g, 0, 0, w, u);
    }

    static void NoteShape(Graphics g, float dx, float dy, Brush br, float u)
    {
        // 符头（斜椭圆）
        var st = g.Transform;
        var m = st.Clone();
        m.Translate(150 * u + dx, 176 * u + dy);
        m.Rotate(-20);
        g.Transform = m;
        g.FillEllipse(br, -27 * u, -20 * u, 54 * u, 40 * u);
        g.Transform = st;

        // 符杆
        g.FillRectangle(br, 171 * u + dx, 66 * u + dy, 12 * u, 112 * u);

        // 旗（月牙形下垂）
        using (var flag = new GraphicsPath())
        {
            flag.StartFigure();
            flag.AddBezier(183 * u + dx, 66 * u + dy, 228 * u + dx, 86 * u + dy, 226 * u + dx, 134 * u + dy, 196 * u + dx, 160 * u + dy);
            flag.AddBezier(196 * u + dx, 160 * u + dy, 214 * u + dx, 124 * u + dy, 208 * u + dx, 94 * u + dy, 183 * u + dx, 88 * u + dy);
            flag.CloseFigure();
            g.FillPath(br, flag);
        }
    }

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
