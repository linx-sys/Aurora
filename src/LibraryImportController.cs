#nullable disable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using IOPath = System.IO.Path;
using WinForms = System.Windows.Forms;

namespace Aurora
{
    /// <summary>串行执行后台工作和其 UI 提交；单项失败不阻塞后续任务。</summary>
    public sealed class LibraryWorkQueue : IDisposable
    {
        readonly object gate = new object();
        readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        Task tail = Task.CompletedTask;
        bool disposed;

        public Task Completion { get { lock (gate) return tail; } }

        public Task Enqueue(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            if (work == null) throw new ArgumentNullException(nameof(work));
            lock (gate)
            {
                if (disposed) return Task.FromCanceled(new CancellationToken(true));
                Task previous = tail;
                Task item = Task.Run(async () =>
                {
                    await previous.ConfigureAwait(false);
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        await work(linked.Token).ConfigureAwait(false);
                    }
                });
                // 对每个错误立即建立观察者；返回原任务供 await 调用方读取失败。
                tail = ObserveAsync(item);
                return item;
            }
        }

        static async Task ObserveAsync(Task item)
        {
            try { await item.ConfigureAwait(false); }
            catch { }
        }

        public void Dispose()
        {
            Task completion;
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                completion = tail;
            }
            lifetime.Cancel();
            _ = completion.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public class LibraryImportController : IDisposable
    {
        readonly Window win;
        readonly MainViewModel vm;
        readonly ILibraryStore store;
        readonly Action<string> toast;
        readonly Action<Track, bool> playTrack;
        readonly LibraryWorkQueue queue = new LibraryWorkQueue();
        volatile bool closed;
        volatile bool isLoadingDir;
        long directoryGeneration;
        long playbackRevision;

        public Task Completion => queue.Completion;
        public bool IsLoadingDirectory => isLoadingDir;

        public LibraryImportController(Window win, MainViewModel vm, ILibraryStore store,
            Action<string> toast, Action<Track, bool> playTrack)
        {
            this.win = win;
            this.vm = vm;
            this.store = store;
            this.toast = toast;
            this.playTrack = playTrack;
            vm.PropertyChanged += OnPlaybackChanged;
            win.Closed += OnClosed;
        }

        void OnPlaybackChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentTrack) || e.PropertyName == nameof(MainViewModel.IsPlaying))
                Interlocked.Increment(ref playbackRevision);
        }

        void OnClosed(object sender, EventArgs e) => Dispose();

        public static string SafeDir(string path)
        {
            try { return IOPath.GetDirectoryName(path); } catch { return null; }
        }

        public void PickFolder()
        {
            if (closed) return;
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = "选择存放音乐的文件夹";
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK) LoadDirectory(dlg.SelectedPath, null, false);
            }
        }

        public void LoadDirectory(string dir, string autoPlayPath, bool silent)
            => _ = LoadDirectoryAsync(dir, autoPlayPath, silent);

        public Task LoadDirectoryAsync(string dir, string autoPlayPath, bool silent, CancellationToken cancellationToken = default)
        {
            long generation = Interlocked.Increment(ref directoryGeneration);
            long requestedPlayback = Interlocked.Read(ref playbackRevision);
            return queue.Enqueue(async token =>
            {
                isLoadingDir = true;
                try
                {
                    if (!silent) await SubmitAsync(() => toast("正在扫描文件夹…"), token, generation).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    List<TrackRow> cached = store.GetByPrefix(dir);
                    if (cached.Count > 0)
                    {
                        var quick = Library.BuildTracksFromRows(cached, probeExtras: false, cancellationToken: token);
                        await SubmitAsync(() =>
                        {
                            PreserveCurrentTrack(quick);
                            vm.Playlist.ReplaceTracks(quick);
                        }, token, generation).ConfigureAwait(false);
                    }

                    var files = new List<string>();
                    bool complete = Library.EnumerateFiles(dir, files, 0, token);
                    var built = Library.BuildTracksIncremental(files, store, dir, token, complete);
                    await SubmitAsync(() =>
                    {
                        PreserveCurrentTrack(built);
                        vm.Playlist.ReplaceTracks(built);
                        Settings.Set("lastDir", dir);
                        if (!silent) toast(complete
                            ? (built.Count == 0 ? "文件夹里没有找到音频文件" : "已加载 " + built.Count + " 首歌曲")
                            : "部分目录无法读取，已保留原有媒体库记录");
                        // 不重置搜索，不打断扫描期间由用户选择/暂停/播放的曲目。
                        if (built.Count == 0 || requestedPlayback != Interlocked.Read(ref playbackRevision)) return;
                        if (autoPlayPath != null)
                        {
                            Track hit = FindByPath(built, autoPlayPath);
                            if (hit != null) { playTrack(hit, true); return; }
                        }
                        if (vm.CurrentTrack != null) return;
                        Track restore = FindByPath(built, Settings.Get("lastTrack", "")) ?? built[0];
                        playTrack(restore, false);
                    }, token, generation).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("Library.LoadDirectory FAIL: " + ex.Message);
                    if (!silent) await SubmitAsync(() => toast("读取文件夹失败，请重试"), token, generation).ConfigureAwait(false);
                    throw;
                }
                finally { isLoadingDir = false; }
            }, cancellationToken);
        }

        public void ImportPaths(string[] paths, bool append) => _ = ImportPathsAsync(paths, append);

        public Task ImportPathsAsync(string[] paths, bool append, CancellationToken cancellationToken = default)
        {
            string[] requested = paths == null ? Array.Empty<string>() : (string[])paths.Clone();
            return queue.Enqueue(async token =>
            {
                try
                {
                    var files = new List<string>();
                    foreach (string path in requested)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Directory.Exists(path)) Library.EnumerateFiles(path, files, 0, token);
                        else if (File.Exists(path)) files.Add(path);
                    }
                    var built = Library.BuildTracksIncremental(files, store, null, token);
                    await SubmitAsync(() =>
                    {
                        // 拖放始终合并；同一队列保证之前扫描的最终 Replace 已完成。
                        int added = vm.Playlist.AddRange(built);
                        toast(added > 0 ? "已添加 " + added + " 首歌曲" : "没有新增的歌曲");
                    }, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("Library.ImportPaths FAIL: " + ex.Message);
                    await SubmitAsync(() => toast("添加文件失败，请重试"), token).ConfigureAwait(false);
                    throw;
                }
            }, cancellationToken);
        }

        async Task SubmitAsync(Action action, CancellationToken token, long? generation = null)
        {
            token.ThrowIfCancellationRequested();
            if (closed || win.Dispatcher.HasShutdownStarted || win.Dispatcher.HasShutdownFinished) return;
            await win.Dispatcher.InvokeAsync(() =>
            {
                if (!closed && !token.IsCancellationRequested &&
                    (!generation.HasValue || generation.Value == Interlocked.Read(ref directoryGeneration))) action();
            }, DispatcherPriority.Normal, token).Task.ConfigureAwait(false);
        }

        void PreserveCurrentTrack(List<Track> tracks)
        {
            Track current = vm.CurrentTrack;
            if (current == null) return;
            for (int i = 0; i < tracks.Count; i++)
                if (LibraryPath.Same(tracks[i].FilePath, current.FilePath)) { tracks[i] = current; break; }
        }

        public static Track FindByPath(IEnumerable<Track> list, string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            foreach (Track track in list)
                if (LibraryPath.Same(track.FilePath, path)) return track;
            return null;
        }

        public void Dispose()
        {
            if (closed) return;
            closed = true;
            vm.PropertyChanged -= OnPlaybackChanged;
            win.Closed -= OnClosed;
            queue.Dispose();
        }
    }
}
