#nullable disable
/* ============================================================
 * SmTcController.cs — Windows 系统媒体传输控制。
 * 仅在 UI 线程且窗口 HWND 已就绪时初始化，失败不影响播放器。
 * ============================================================ */
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Windows.Foundation;
using Windows.Media;
using Windows.Storage.Streams;

namespace Aurora
{
    class SmTcController : IDisposable
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly CancellationToken token;
        SystemMediaTransportControls smtc;
        InMemoryRandomAccessStream thumbnailStream;
        long metadataGeneration;
        bool disposed;

        public SmTcController(Window win, MainViewModel vm)
        {
            this.win = win;
            this.vm = vm;
            token = lifetime.Token;
            win.Closed += OnClosed;
        }

        /// <summary>在 SourceInitialized/Loaded 后调用；重复调用不会重复注册媒体键。</summary>
        public void Attach()
        {
            if (disposed || smtc != null) return;
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
                UpdateMetadata(vm.CurrentTrack);
                if (vm.CurrentTrack != null) UpdateStatus(vm.IsPlaying);
            }
            catch (Exception ex)
            {
                MainViewModel.Dbg("SMTC attach FAIL: " + ex.Message);
                ReleaseControls();
            }
        }

        void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
        {
            if (token.IsCancellationRequested || win.Dispatcher.HasShutdownStarted) return;
            try
            {
                win.Dispatcher.BeginInvoke((Action)(() =>
                {
                    if (token.IsCancellationRequested) return;
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
            catch (InvalidOperationException) { }
        }

        public void UpdateStatus(bool playing)
        {
            if (disposed || smtc == null) return;
            try { smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused; }
            catch { }
        }

        public void UpdateMetadata(Track t) => _ = UpdateMetadataAsync(t);

        /// <summary>UI 线程调用；封面写入真正异步，较早曲目的结果不能覆盖较新曲目。</summary>
        public async Task UpdateMetadataAsync(Track t)
        {
            long generation = ++metadataGeneration;
            if (disposed || smtc == null) return;
            InMemoryRandomAccessStream pending = null;
            try
            {
                var controls = smtc;
                var updater = controls.DisplayUpdater;
                updater.ClearAll();
                updater.Thumbnail = null;
                if (t == null)
                {
                    controls.PlaybackStatus = MediaPlaybackStatus.Stopped;
                    updater.Update();
                    ReleaseThumbnail();
                    return;
                }
                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = t.Title ?? "";
                updater.MusicProperties.Artist = t.Artist ?? "";
                updater.MusicProperties.AlbumTitle = t.Album ?? "";
                updater.Update();
                ReleaseThumbnail();
                byte[] cover = t.Cover;
                if (cover == null || cover.Length == 0) return;

                pending = new InMemoryRandomAccessStream();
                using (var output = pending.GetOutputStreamAt(0))
                using (var writer = new DataWriter(output))
                {
                    writer.WriteBytes(cover);
                    await writer.StoreAsync().AsTask(token);
                    await writer.FlushAsync().AsTask(token);
                    writer.DetachStream();
                }
                token.ThrowIfCancellationRequested();
                if (generation != metadataGeneration || controls != smtc) return;
                pending.Seek(0);
                updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(pending);
                updater.Update();
                // 缩略图引用可能稍后读取；保留底层流直到下一首清除或关闭。
                thumbnailStream = pending;
                pending = null;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { MainViewModel.Dbg("SMTC metadata FAIL: " + ex.Message); }
            finally { pending?.Dispose(); }
        }

        void ReleaseThumbnail()
        {
            thumbnailStream?.Dispose();
            thumbnailStream = null;
        }

        void ReleaseControls()
        {
            var controls = smtc;
            smtc = null;
            if (controls != null)
            {
                try { controls.ButtonPressed -= OnButtonPressed; } catch { }
                try { controls.PlaybackStatus = MediaPlaybackStatus.Stopped; } catch { }
                try { controls.DisplayUpdater.ClearAll(); controls.DisplayUpdater.Update(); } catch { }
                try { controls.IsEnabled = false; } catch { }
            }
            ReleaseThumbnail();
        }

        void OnClosed(object sender, EventArgs e) => Dispose();

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            ++metadataGeneration;
            win.Closed -= OnClosed;
            lifetime.Cancel();
            ReleaseControls();
            lifetime.Dispose();
        }
    }
}
