/* ============================================================
 * ToastController.cs — 底部 Toast 提示条（从 MainWindow 拆出）
 * 显示 2.6s 后淡出；重复调用重置计时。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Aurora
{
    class ToastController
    {
        readonly Border card;
        readonly TextBlock tb;
        DispatcherTimer timer;

        public ToastController(Border card, TextBlock tb)
        {
            this.card = card;
            this.tb = tb;
        }

        public void Toast(string msg)
        {
            tb.Text = msg;
            card.Visibility = Visibility.Visible;
            card.Opacity = 1;
            if (timer == null)
            {
                timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
                timer.Tick += (s, e) =>
                {
                    ((DispatcherTimer)s).Stop();
                    var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(250));
                    anim.Completed += (s2, e2) => { card.Visibility = Visibility.Collapsed; };
                    card.BeginAnimation(UIElement.OpacityProperty, anim);
                };
            }
            timer.Stop();
            timer.Start();
        }

        /// <summary>窗口关闭时停掉计时器。</summary>
        public void Stop()
        {
            try { timer?.Stop(); } catch { }
        }
    }
}
