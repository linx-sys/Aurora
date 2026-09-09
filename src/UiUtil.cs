/* ============================================================
 * UiUtil.cs — UI 通用小工具（从 MainWindow 拆出的静态助手）
 * 拖条 HookDragBar / 淡入 FadeIn / 时间格式化 / 数值解析 / Clamp
 * ============================================================ */
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Aurora
{
    static class UiUtil
    {
        internal static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

        internal static double ParseDouble(string s)
        {
            double d;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d : 0.8;
        }

        internal static int ParseInt(string s)
        {
            int i;
            return int.TryParse(s, out i) ? i : 0;
        }

        internal static string FmtTime(TimeSpan t)
        {
            int s = Math.Max(0, (int)t.TotalSeconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        /// <summary>元素淡入（切歌时标题/封面/歌词的柔和过渡）。</summary>
        internal static void FadeIn(UIElement el, int ms = 320)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 0;
            var anim = new DoubleAnimation(1, TimeSpan.FromMilliseconds(ms))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /// <summary>
        /// 通用拖条：按水平（X/宽）或竖直（1-Y/高）把鼠标位置换算为 0~1 ratio，
        /// 捕获鼠标期间持续回调 down/move/up。
        /// </summary>
        internal static void HookDragBar(Grid hit, Action<double> down, Action<double> move, Action<double> up, bool vertical = false)
        {
            Func<MouseEventArgs, double> ratioOf = delegate(MouseEventArgs e)
            {
                Point p = e.GetPosition(hit);
                if (vertical)
                {
                    double h = hit.ActualHeight;
                    return h > 0 ? Clamp(1 - p.Y / h, 0, 1) : 0;
                }
                double w = hit.ActualWidth;
                return w > 0 ? Clamp(p.X / w, 0, 1) : 0;
            };
            hit.MouseLeftButtonDown += (s, e) =>
            {
                hit.CaptureMouse();
                double r = ratioOf(e);
                down(r);
                e.Handled = true;
            };
            hit.MouseMove += (s, e) =>
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed && hit.IsMouseCaptured) move(ratioOf(e));
            };
            hit.MouseLeftButtonUp += (s, e) =>
            {
                if (hit.IsMouseCaptured)
                {
                    double r = ratioOf(e);
                    hit.ReleaseMouseCapture();
                    up(r);
                }
                e.Handled = true;
            };
        }
    }
}
