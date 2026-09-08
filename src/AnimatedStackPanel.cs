/* ============================================================
 * AnimatedStackPanel.cs — 带动画的垂直堆叠面板
 * 从 MainWindow.cs 拆出（原属"播放列表交互"职责）。
 * 子元素位置变化时用 TranslateTransform 平滑过渡（250ms CubicEaseOut），
 * 用于播放列表拖动排序时的过渡动画。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Aurora
{
    public class AnimatedStackPanel : Panel
    {
        readonly Dictionary<UIElement, Point> lastPos = new Dictionary<UIElement, Point>();
        static readonly Duration animDur = new Duration(TimeSpan.FromMilliseconds(250));
        static readonly IEasingFunction ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        protected override Size MeasureOverride(Size available)
        {
            Size desired = new Size(0, 0);
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(available);
                if (child.DesiredSize.Width > desired.Width) desired.Width = child.DesiredSize.Width;
                desired.Height += child.DesiredSize.Height;
            }
            return desired;
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double y = 0;
            foreach (UIElement child in InternalChildren)
            {
                double h = child.DesiredSize.Height;
                Rect target = new Rect(0, y, finalSize.Width, h);

                Point lp;
                if (lastPos.TryGetValue(child, out lp))
                {
                    double dy = lp.Y - y;
                    if (Math.Abs(dy) > 0.5)
                    {
                        TranslateTransform tt = child.RenderTransform as TranslateTransform;
                        if (tt == null)
                        {
                            tt = new TranslateTransform();
                            child.RenderTransform = tt;
                        }
                        tt.Y = dy;  // 立即偏移到旧位置
                        DoubleAnimation anim = new DoubleAnimation(0, animDur);
                        anim.EasingFunction = ease;
                        tt.BeginAnimation(TranslateTransform.YProperty, anim);
                    }
                }
                lastPos[child] = new Point(0, y);
                child.Arrange(target);
                y += h;
            }
            return finalSize;
        }

        protected override void OnVisualChildrenChanged(DependencyObject added, DependencyObject removed)
        {
            base.OnVisualChildrenChanged(added, removed);
            if (removed != null)
            {
                UIElement el = removed as UIElement;
                if (el != null) lastPos.Remove(el);
            }
        }
    }
}
