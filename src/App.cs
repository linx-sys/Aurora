/* ============================================================
 * App.cs — 入口：单实例 + 双击音乐文件唤醒
 * ============================================================ */
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace Aurora
{
    static class App
    {
        static Mutex _instanceMutex;

        [STAThread]
        static void Main(string[] args)
        {
            // .NET Core/10 默认不含代码页编码（GBK 等），必须注册 CodePagesEncodingProvider，
            // 否则老歌 GBK 标签/歌词的解码回退静默失效（Id3.MakeGbk 返回 null → 乱码）
            try { Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance); }
            catch { }

            // 关联管理：AuroraPlayer.exe /associate 或 /unassociate（手动调用）
            foreach (string a in args)
            {
                if (string.Equals(a, "/associate", StringComparison.OrdinalIgnoreCase))
                {
                    Assoc.Register();
                    Environment.Exit(0);
                }
                if (string.Equals(a, "/unassociate", StringComparison.OrdinalIgnoreCase))
                {
                    Assoc.Unregister();
                    Environment.Exit(0);
                }
            }

            string fileArg = null;
            foreach (string a in args)
            {
                if (!a.StartsWith("/") && !a.StartsWith("-")) { fileArg = a; break; }
            }

            // 静态字段持有，防止 GC 回收互斥体导致单实例失效
            bool created;
            _instanceMutex = new Mutex(true, "AuroraPlayer.Instance.Mutex", out created);
            if (!created)
            {
                if (fileArg != null && File.Exists(fileArg)) ForwardToRunning(fileArg);
                Environment.Exit(0); // 转发完成后立即退出，避免进程残留
            }

            // 关联管理：安装器留下的 .associate 标记 → 仅由首实例处理（避免双实例竞争删除）
            try
            {
                string flag = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".associate");
                if (File.Exists(flag))
                {
                    string content = File.ReadAllText(flag).Trim();
                    if (content == "1" || content.Length == 0)
                        Assoc.Register();
                    else
                    {
                        // 逗号分隔的扩展名列表，如 ".mp3,.flac,.m4a"
                        string[] exts = content.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                        for (int i = 0; i < exts.Length; i++) exts[i] = exts[i].Trim().ToLowerInvariant();
                        Assoc.Register(exts);
                    }
                    File.Delete(flag);
                }
            }
            catch { }

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 全局异常钩子：Dispatcher（UI 定时器/事件）与后台线程的未处理异常都会闪退
            // 但不会经过 Main 的 try-catch，这里统一落盘以便定位
            app.DispatcherUnhandledException += (s, e) =>
            {
                LogCrash("DispatcherUnhandledException", e.Exception);
                e.Handled = true;   // 尽量不闪退；若状态已损坏由用户手动重启
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                LogCrash("AppDomain.UnhandledException (terminating=" + e.IsTerminating + ")", e.ExceptionObject as Exception);

            try
            {
                var win = new MainWindow(fileArg);
                app.Run(win.Win);
            }
            catch (Exception ex)
            {
                LogCrash("Main", ex);
                throw;
            }
        }

        static void LogCrash(string source, Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "aurora_crash.log"),
                    "\r\n==== " + DateTime.Now + "  [" + source + "] ====\r\n" + ex);
            }
            catch { }
        }

        static void ForwardToRunning(string path)
        {
            NamedPipeClientStream client = null;
            try
            {
                client = new NamedPipeClientStream(".", "AuroraPlayer.Instance", PipeDirection.Out);
                client.Connect(2000);
                using (var w = new StreamWriter(client, Encoding.Unicode))
                {
                    w.WriteLine(path);
                    w.Flush();
                }
            }
            catch { /* 主实例可能刚退出，忽略 */ }
            finally
            {
                try { if (client != null) client.Dispose(); } catch { }
            }
        }
    }
}
