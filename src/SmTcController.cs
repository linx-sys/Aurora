/* ============================================================
 * SmTcController.cs — Windows 系统媒体传输控制（SMTC，P2-5）
 * 锁屏/系统媒体浮层显示曲目信息 + 媒体键（播放/暂停/上一首/下一首）。
 * 桌面应用不能用 GetForCurrentView()，走 SystemMediaTransportControlsInterop
 * .GetForWindow(hwnd)；须在 UI 线程创建（WinRT 对象线程约束）。
 * 任何失败（旧系统/投影缺失）静默降级：媒体键失效，其余功能不受影响。
 * ============================================================ */
using System;
using System.Windows;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

namespace Aurora
{
    class SmTcController
    {
        readonly Window win;
        readonly MainViewModel vm;
        SystemMediaTransportControls smtc;

        public SmTcController(Window win, MainViewModel vm)
        {
            this.win = win;
            this.vm = vm;
        }

        /// <summary>挂接 SMTC（UI 线程调用一次；失败静默禁用）。</summary>
        public void Attach()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                if (hwnd == IntPtr.Zero) return;

                smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;
                smtc.IsNextEnabled = true;
                smtc.IsPreviousEnabled = true;
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                smtc.ButtonPressed += OnButtonPressed;
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("SMTC attach FAIL: " + ex.Message);
                smtc = null;
            }
        }

        /// <summary>媒体键 → 转发 ViewModel 命令（切回 UI 线程）。</summary>
        void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
        {
            win.Dispatcher.BeginInvoke((Action)(() =>
            {
                try
                {
                    switch (e.Button)
                    {
                        case SystemMediaTransportControlsButton.Play:
                            if (!vm.IsPlaying && vm.TogglePlayCommand.CanExecute(null)) vm.TogglePlayCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Pause:
                            if (vm.IsPlaying && vm.TogglePlayCommand.CanExecute(null)) vm.TogglePlayCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Next:
                            if (vm.NextCommand.CanExecute(null)) vm.NextCommand.Execute(null);
                            break;
                        case SystemMediaTransportControlsButton.Previous:
                            if (vm.PrevCommand.CanExecute(null)) vm.PrevCommand.Execute(null);
                            break;
                    }
                }
                catch (Exception ex) { MainViewModel.Dbg("SMTC button FAIL: " + ex.Message); }
            }));
        }

        /// <summary>播放状态变化 → 更新系统浮层状态（UI 线程调用）。</summary>
        public void UpdateStatus(bool playing)
        {
            if (smtc == null) return;
            try
            {
                smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            }
            catch { }
        }

        /// <summary>曲目变更 → 推送元数据（标题/歌手/专辑/封面缩略图）到系统浮层。</summary>
        public void UpdateMetadata(Track t)
        {
            if (smtc == null) return;
            try
            {
                SystemMediaTransportControlsDisplayUpdater upd = smtc.DisplayUpdater;
                if (t == null)
                {
                    smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    upd.ClearAll();
                    upd.Update();
                    return;
                }
                upd.Type = MediaPlaybackType.Music;
                MusicDisplayProperties music = upd.MusicProperties;
                music.Title = t.Title ?? "";
                music.Artist = t.Artist ?? "";
                music.AlbumTitle = t.Album ?? "";
                if (t.Cover != null && t.Cover.Length > 0)
                    upd.Thumbnail = CreateThumbnail(t.Cover);
                upd.Update();
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("SMTC metadata FAIL: " + ex.Message);
            }
        }

        /// <summary>内嵌封面字节 → 随机访问流缩略图（best-effort，失败返回 null 不推缩略图）。</summary>
        static RandomAccessStreamReference CreateThumbnail(byte[] cover)
        {
            try
            {
                var stream = new InMemoryRandomAccessStream();
                var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(cover);
                writer.StoreAsync().AsTask().Wait(500);
                writer.FlushAsync().AsTask().Wait(500);
                writer.DetachStream();
                stream.Seek(0);
                return RandomAccessStreamReference.CreateFromStream(stream);
            }
            catch { return null; }
        }
    }
}
