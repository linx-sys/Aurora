/* ============================================================
 * LyricsViewController.cs — 歌词视图控制器
 * 从 MainWindow.cs 拆出（原属"歌词渲染"职责）。
 * 负责全屏歌词的渲染（元数据分组 + 当前行高亮 + 居中滚动）与配色笔刷缓存。
 * 歌词滚动/点击跳转本质是 View 行为，因此做成 View-specific Controller
 * 而非 ViewModel（评审建议 #3：MVVM + View Controllers）。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Aurora
{
    public class LyricsViewController
    {
        readonly Grid root;                 // 主题资源查找（TryFindResource）
        readonly StackPanel panel;          // FpLyricsPanel
        readonly ScrollViewer scroll;       // FpLyricsScroll
        readonly IPlaybackService player;   // 点击歌词跳转时读 Duration / 写 Position
        readonly Func<bool> isDark;         // 主题状态（普通歌词色随主题变化）
        readonly Func<bool> isListOpen;     // 点击歌词跳转后收起列表面板（若开着）
        readonly Action closeListPanel;

        LrcDoc lrcDoc;
        readonly List<TextBlock> fullLrcBlocks = new List<TextBlock>();
        readonly List<int> docToBlock = new List<int>();   // doc.Lines 下标 → fullLrcBlocks 下标（-1=元数据/跳过）
        int fullLrcIndex = -2;
        double fullScrollTarget;
        Brush accentBrushCache, normalBrushCache;   // 歌词笔刷缓存（frozen，避免每行 new）

        /// <summary>当前歌词高亮色（取自歌曲配色；与黑胶彩胶/标题装饰条呼应）。</summary>
        Color lyricAccent = Color.FromRgb(0x60, 0xA5, 0xFA);

        public LyricsViewController(Grid root, StackPanel panel, ScrollViewer scroll, IPlaybackService player,
            Func<bool> isDark, Func<bool> isListOpen, Action closeListPanel)
        {
            this.root = root;
            this.panel = panel;
            this.scroll = scroll;
            this.player = player;
            this.isDark = isDark;
            this.isListOpen = isListOpen;
            this.closeListPanel = closeListPanel;
        }

        /* ============================================================
         * LRC 中常见的元数据行前缀（作词/作曲/编曲等），单独成组展示，不参与歌词高亮
         * ============================================================ */
        static readonly Regex MetaLineRe = new Regex(
            @"^[^:：]{1,60}[:：]",
            RegexOptions.Compiled);

        /// <summary>判断是否为片头行（"歌手 - 歌名"或"歌名 - 歌手"，与顶部标题重复，应跳过）。</summary>
        static bool IsTitleArtistIntro(string text, string artist)
        {
            if (string.IsNullOrEmpty(artist)) return false;
            int dash = text.IndexOf(" - ");
            if (dash <= 0) return false;
            string left = text.Substring(0, dash).Trim();
            string right = text.Substring(dash + 3).Trim();
            return left.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   right.IndexOf(artist, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   artist.IndexOf(left, StringComparison.OrdinalIgnoreCase) >= 0 ||
                   artist.IndexOf(right, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>判断是否为"歌名 - 演唱者"署名行（如"老情书 - 匡耿-玉面"，应归入元数据）。</summary>
        static bool IsTitlePerformerLine(string text, string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            int dash = text.IndexOf(" - ");
            if (dash <= 0) return false;
            string left = text.Substring(0, dash).Trim();
            return string.Equals(left, title, StringComparison.OrdinalIgnoreCase);
        }

        /* ============================================================
         * 渲染
         * ============================================================ */

        public void Render(Track t)
        {
            panel.Children.Clear();
            fullLrcBlocks.Clear();
            docToBlock.Clear();
            fullLrcIndex = -2;
            lrcDoc = null;
            if (t == null) return;

            LrcDoc doc = t.LrcText != null ? Lrc.Parse(t.LrcText) : null;
            lrcDoc = doc;
            if (doc == null)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "暂无歌词",
                    Foreground = NormalStyleBrush(),
                    FontSize = 17,
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
                return;
            }

            // 第一遍：分离元数据行与正文歌词行
            var metaLines = new List<string>();
            var lyricDocIdx = new List<int>();
            for (int i = 0; i < doc.Lines.Count; i++)
            {
                string text = doc.Lines[i].Text.Trim();
                if (text.Length == 0) { lyricDocIdx.Add(i); continue; }
                if (MetaLineRe.IsMatch(text)) { metaLines.Add(text); continue; }
                if (i < 5 && IsTitleArtistIntro(text, t.Artist)) continue;  // 与顶部标题重复，跳过
                if (IsTitlePerformerLine(text, t.Title)) { metaLines.Add(text); continue; }  // "歌名 - 演唱者"署名行
                lyricDocIdx.Add(i);
            }

            // 元数据组：紧凑、居中、暗色，底部一条强调色分隔线；超过 6 行折叠为"…等 N 项"
            if (metaLines.Count > 0)
            {
                var metaPanel = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 14),
                };
                const int MetaMaxVisible = 6;
                var visibleMeta = metaLines.Count <= MetaMaxVisible
                    ? metaLines
                    : metaLines.GetRange(0, MetaMaxVisible - 1);
                foreach (string m in visibleMeta)
                {
                    metaPanel.Children.Add(new TextBlock
                    {
                        Text = m,
                        FontSize = 11.5,
                        Foreground = NormalStyleBrush(),
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 0.5, 0, 0.5),
                    });
                }
                if (metaLines.Count > MetaMaxVisible)
                {
                    metaPanel.Children.Add(new TextBlock
                    {
                        Text = "…等 " + metaLines.Count + " 项制作人员",
                        FontSize = 11,
                        Foreground = NormalStyleBrush(),
                        Opacity = 0.65,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 1, 0, 0.5),
                    });
                }
                metaPanel.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 30,
                    Height = 1.5,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = new SolidColorBrush(lyricAccent) { Opacity = 0.45 },
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 10, 0, 0),
                });
                panel.Children.Add(metaPanel);
            }

            // 建立 doc.Lines 下标 → 渲染块下标的映射
            for (int i = 0; i < doc.Lines.Count; i++) docToBlock.Add(-1);
            int blockIdx = 0;
            foreach (int docIdx in lyricDocIdx)
            {
                LrcLine ln = doc.Lines[docIdx];
                var tb = new TextBlock
                {
                    Text = ln.Text.Length > 0 ? ln.Text : " ",
                    Foreground = NormalStyleBrush(),
                    FontSize = 17,
                    Margin = new Thickness(0, 6, 0, 6),
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Cursor = ln.Text.Length > 0 ? Cursors.Hand : Cursors.Arrow,
                    Tag = docIdx,
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = new ScaleTransform(1.0, 1.0),
                };
                tb.MouseLeftButtonDown += LrcLineClick;
                panel.Children.Add(tb);
                fullLrcBlocks.Add(tb);
                docToBlock[docIdx] = blockIdx;
                blockIdx++;
            }

            scroll.ScrollToTop();
            Sync(0);
        }

        void LrcLineClick(object sender, MouseButtonEventArgs e)
        {
            var tb = sender as TextBlock;
            if (tb == null || lrcDoc == null || !(player.Duration > TimeSpan.Zero)) return;
            int docIdx = (int)tb.Tag;
            if (docIdx >= 0 && docIdx < lrcDoc.Lines.Count)
            {
                player.Position = TimeSpan.FromSeconds(lrcDoc.Lines[docIdx].Time);
                // 点击歌词跳转后收起列表面板（若开着），与"点击左侧收起"的抽屉行为一致；
                // 此处 e.Handled=true 会阻断冒泡，故需在此显式收起
                if (isListOpen()) closeListPanel();
            }
            e.Handled = true;
        }

        /// <summary>随播放进度同步歌词高亮与滚动（UI 计时器每帧调用）。</summary>
        public void Sync(double pos)
        {
            if (lrcDoc == null || fullLrcBlocks.Count == 0) return;
            int docIdx = Lrc.IndexAt(lrcDoc.Lines, pos + 0.02);

            // doc 下标 → 渲染块下标；若当前是元数据行，高亮下一条正文歌词
            int blockIdx = -1;
            if (docIdx >= 0 && docIdx < docToBlock.Count)
            {
                blockIdx = docToBlock[docIdx];
                if (blockIdx < 0)
                {
                    for (int k = docIdx + 1; k < docToBlock.Count; k++)
                    {
                        if (docToBlock[k] >= 0) { blockIdx = docToBlock[k]; break; }
                    }
                    if (blockIdx < 0) blockIdx = fullLrcBlocks.Count - 1;
                }
            }

            if (blockIdx != fullLrcIndex)
            {
                fullLrcIndex = blockIdx;
                Brush curBrush = AccentBrush();
                Brush dimBrush = NormalBrush();
                for (int k = 0; k < fullLrcBlocks.Count; k++)
                {
                    bool cur = k == blockIdx;
                    var tb = fullLrcBlocks[k];
                    tb.FontSize = cur ? 19 : 17;
                    tb.FontWeight = cur ? FontWeights.Bold : FontWeights.Normal;
                    tb.Foreground = cur ? curBrush : dimBrush;
                    tb.Margin = cur ? new Thickness(0, 8, 0, 8) : new Thickness(0, 6, 0, 6);
                    // 当前行轻微放大，用动画过渡（RenderTransform 不影响布局）
                    var st = tb.RenderTransform as ScaleTransform;
                    if (st != null)
                    {
                        double target = cur ? 1.08 : 1.0;
                        var anim = new System.Windows.Media.Animation.DoubleAnimation(
                            target, TimeSpan.FromMilliseconds(220));
                        anim.EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                        st.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
                        st.BeginAnimation(ScaleTransform.ScaleYProperty, anim);
                    }
                }
                if (blockIdx >= 0 && blockIdx < fullLrcBlocks.Count)
                {
                    TextBlock tb = fullLrcBlocks[blockIdx];
                    double y = tb.TransformToVisual(panel).Transform(new Point(0, 0)).Y;
                    fullScrollTarget = Math.Max(0, y - scroll.ViewportHeight / 2 + tb.ActualHeight / 2);
                }
            }
            double curOff = scroll.VerticalOffset;
            double diff = fullScrollTarget - curOff;
            if (Math.Abs(diff) > 0.5)
                scroll.ScrollToVerticalOffset(curOff + diff * 0.18);
        }

        /* ============================================================
         * 配色 / 笔刷缓存
         * ============================================================ */

        /// <summary>主题切换后重建全部歌词相关笔刷并重染现有行。</summary>
        public void OnThemeChanged()
        {
            InvalidateBrushes();
            Recolor();
        }

        public void Recolor()
        {
            Brush normal = NormalBrush();
            Brush current = AccentBrush();
            for (int k = 0; k < fullLrcBlocks.Count; k++)
                fullLrcBlocks[k].Foreground = (k == fullLrcIndex) ? current : normal;
        }

        /// <summary>切歌时更新高亮色（取自歌曲专属配色）；返回该色供标题装饰条复用。</summary>
        public Color SetAccent(Color accent)
        {
            lyricAccent = accent;
            InvalidateBrushes();   // 高亮色随歌曲配色变化，重建缓存
            return accent;
        }

        /// <summary>歌词高亮色笔刷（缓存并 freeze；lyricAccent/主题变化后由调用方置空重建）。</summary>
        Brush AccentBrush()
        {
            if (accentBrushCache == null)
            {
                var b = new SolidColorBrush(lyricAccent);
                b.Freeze();
                accentBrushCache = b;
            }
            return accentBrushCache;
        }

        /// <summary>歌词普通色笔刷（缓存并 freeze，避免每行高亮切换都 new）。</summary>
        Brush NormalBrush()
        {
            if (normalBrushCache == null)
            {
                var b = NormalStyleBrush();
                if (b.CanFreeze) b.Freeze();
                normalBrushCache = b;
            }
            return normalBrushCache;
        }

        void InvalidateBrushes()
        {
            accentBrushCache = null;
            normalBrushCache = null;
        }

        Brush NormalStyleBrush()
        {
            return isDark()
                ? (Brush)root.TryFindResource("Dim")
                : new SolidColorBrush(Color.FromArgb(0x66, 0x8A, 0x93, 0xA8));
        }
    }
}
