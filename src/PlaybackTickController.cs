#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * PlaybackTickController.cs — 进度/黑胶/任务栏 UI 计时（从 MainWindow 拆出）
 * 持有 33ms DispatcherTimer 拉模型：每帧直读 PlayerEngine.Position 直写控件
 * （绕过 VM 热路径绑定，性能取舍，行为与拆分前一致）。
 * 同时接管进度条拖动交互与任务栏进度（绿=播放/黄=暂停）。
 * ============================================================ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Aurora
{
    class PlaybackTickController
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly IPlaybackService player;
        readonly LyricsViewController lyrics;

        readonly Grid seekHit;
        readonly Border seekFill, seekTrackBg;
        readonly Ellipse seekThumb;
        readonly TextBlock curTime, durTime;
        readonly RotateTransform vinylRotate;

        DispatcherTimer timer;
        bool seekingDrag;
        double lastTaskbarRatio = -1;

        public PlaybackTickController(Window win, MainViewModel vm, IPlaybackService player, LyricsViewController lyrics,
            Grid seekHit, Border seekFill, Border seekTrackBg, Ellipse seekThumb,
            TextBlock curTime, TextBlock durTime, RotateTransform vinylRotate)
        {
            this.win = win;
            this.vm = vm;
            this.player = player;
            this.lyrics = lyrics;
            this.seekHit = seekHit;
            this.seekFill = seekFill;
            this.seekTrackBg = seekTrackBg;
            this.seekThumb = seekThumb;
            this.curTime = curTime;
            this.durTime = durTime;
            this.vinylRotate = vinylRotate;

            timer = new DispatcherTimer(DispatcherPriority.Render);
            timer.Interval = TimeSpan.FromMilliseconds(33);
            timer.Tick += UiTick;

            HookSeekDrag();
        }

        public void Start() { timer.Start(); }

        public void Stop() { try { timer?.Stop(); } catch { } }

        void HookSeekDrag()
        {
            // 进度条拖动（水平拖条；up 时按比例写入引擎播放位置）
            UiUtil.HookDragBar(seekHit,
                down: ratio =>
                {
                    if (vm.CurrentTrack != null && (player.Duration > TimeSpan.Zero))
                    {
                        seekingDrag = true;
                        SetSeekUi(ratio, true);
                    }
                },
                move: ratio => { if (seekingDrag) SetSeekUi(ratio, true); },
                up: ratio =>
                {
                    if (seekingDrag)
                    {
                        seekingDrag = false;
                        if (vm.CurrentTrack != null && (player.Duration > TimeSpan.Zero))
                            player.Position = TimeSpan.FromSeconds(ratio * player.Duration.TotalSeconds);
                        if (!seekHit.IsMouseOver) { seekTrackBg.Height = 4; seekFill.Height = 4; }
                    }
                });
            seekHit.MouseEnter += (s, e) =>
            {
                seekTrackBg.Height = 6;
                seekFill.Height = 6;
                if ((player.Duration > TimeSpan.Zero)) seekThumb.Visibility = Visibility.Visible;
            };
            seekHit.MouseLeave += (s, e) =>
            {
                if (!seekingDrag)
                {
                    seekTrackBg.Height = 4;
                    seekFill.Height = 4;
                    seekThumb.Visibility = Visibility.Collapsed;
                }
            };
        }

        public void SetSeekUi(double ratio, bool showThumb)
        {
            double w = seekHit.ActualWidth;
            seekFill.Width = Math.Max(0, Math.Min(1, ratio)) * w;
            seekThumb.Margin = new Thickness(ratio * w - 6, 0, 0, 0);
            if (showThumb) seekThumb.Visibility = Visibility.Visible;
        }

        void UiTick(object sender, EventArgs e)
        {
            bool playing = vm.IsPlaying;
            Track current = vm.CurrentTrack;

            // 黑胶旋转（播放时约 10 秒/圈）
            if (playing)
                vinylRotate.Angle = (vinylRotate.Angle + 1.2) % 360;

            // 进度
            if ((player.Duration > TimeSpan.Zero) && current != null)
            {
                double dur = player.Duration.TotalSeconds;
                double pos = player.Position.TotalSeconds;
                if (dur > 0)
                {
                    if (!seekingDrag)
                    {
                        SetSeekUi(pos / dur, false);
                        curTime.Text = UiUtil.FmtTime(TimeSpan.FromSeconds(pos));
                        durTime.Text = UiUtil.FmtTime(TimeSpan.FromSeconds(dur));
                    }
                    lyrics.Sync(pos);
                }
            }

            // 任务栏进度（节流：进度值变化超过 0.5% 才写，避免每帧更新绑定）
            double ratio = (current != null && player.Duration > TimeSpan.Zero)
                ? player.Position.TotalSeconds / player.Duration.TotalSeconds : 0;
            if (Math.Abs(ratio - lastTaskbarRatio) > 0.005 || (ratio == 0 && lastTaskbarRatio != 0))
            {
                lastTaskbarRatio = ratio;
                UpdateTaskbarProgress();
            }
        }

        /// <summary>Windows 任务栏进度：播放=绿色 / 暂停=黄色 / 无曲=隐藏。</summary>
        public void UpdateTaskbarProgress()
        {
            var info = win.TaskbarItemInfo;
            if (info == null) return;
            Track current = vm.CurrentTrack;
            if (current == null || player.Duration <= TimeSpan.Zero)
            {
                info.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
                lastTaskbarRatio = -1;
                return;
            }
            double ratio = UiUtil.Clamp(player.Position.TotalSeconds / player.Duration.TotalSeconds, 0, 1);
            info.ProgressState = vm.IsPlaying
                ? System.Windows.Shell.TaskbarItemProgressState.Normal
                : System.Windows.Shell.TaskbarItemProgressState.Paused;
            info.ProgressValue = ratio;
            lastTaskbarRatio = ratio;
        }
    }
}
