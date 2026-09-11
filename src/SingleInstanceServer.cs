/* ============================================================
 * SingleInstanceServer.cs — 可取消、仅当前用户的单实例命名管道服务。
 * ============================================================ */
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Aurora
{
    public sealed class SingleInstanceServer : IDisposable
    {
        readonly CancellationTokenSource lifetime;
        int disposed;
        public Task Completion { get; }

        SingleInstanceServer(Action<string> onFileReceived, CancellationToken cancellationToken)
        {
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Completion = RunAsync(onFileReceived, lifetime.Token);
        }

        /// <summary>返回的监听实例必须随窗口关闭释放；回调不保证位于 UI 线程。</summary>
        public static SingleInstanceServer Start(Action<string> onFileReceived, CancellationToken cancellationToken = default) =>
            new SingleInstanceServer(onFileReceived, cancellationToken);

        static async Task RunAsync(Action<string> onFileReceived, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream("AuroraPlayer.Instance", PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    using var reader = new StreamReader(server, Encoding.Unicode);
                    string? path = await ReadPathAsync(reader, timeout.Token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        token.ThrowIfCancellationRequested();
                        onFileReceived(path);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    MainViewModel.Dbg("Instance pipe FAIL: " + ex.Message);
                    try { await Task.Delay(500, token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
        }

        // 限制客户端输入，防止未换行或过长路径使服务一直等待/无限分配。
        static async Task<string?> ReadPathAsync(StreamReader reader, CancellationToken token)
        {
            var path = new StringBuilder();
            var buffer = new char[256];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0) return path.Length == 0 ? null : path.ToString();
                for (int i = 0; i < count; i++)
                {
                    char c = buffer[i];
                    if (c == '\r' || c == '\n') return path.ToString();
                    if (c == '\0' || path.Length >= 32767) throw new InvalidDataException("管道文件路径无效或过长。");
                    path.Append(c);
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel();
            _ = Completion.ContinueWith(_ => lifetime.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
