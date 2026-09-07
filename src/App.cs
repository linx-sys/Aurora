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
            // 关联管理：安装器留下的 .associate 标记 → 首次启动时注册文件关联
            try
            {
                string flag = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".associate");
                if (File.Exists(flag))
                {
                    Assoc.Register();
                    File.Delete(flag);
                }
            }
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

            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            try
            {
                var win = new MainWindow(fileArg);
                app.Run(win.Win);
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(
                        Path.Combine(Path.GetTempPath(), "aurora_crash.log"),
                        DateTime.Now + "\r\n" + ex);
                }
                catch { }
                throw;
            }
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
