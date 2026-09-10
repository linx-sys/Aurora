/* ============================================================
 * SingleInstanceServer.cs — 单实例命名管道服务
 * 从 MainWindow.cs 拆出（原属"IPC"职责）。
 * 后续双击文件时，新进程把文件路径写入命名管道后退出；
 * 已运行的实例通过 onFileReceived 回调打开该文件。
 * ============================================================ */
using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace Aurora
{
    public static class SingleInstanceServer
    {
        /// <summary>启动管道监听（后台线程常驻）。onFileReceived 在线程池线程回调，需自行切 UI 线程。</summary>
        public static void Start(Action<string> onFileReceived)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                while (true)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream("AuroraPlayer.Instance", PipeDirection.In))
                        {
                            server.WaitForConnection();
                            using (var reader = new StreamReader(server, Encoding.Unicode))
                            {
                                string? path = reader.ReadLine();
                                if (!string.IsNullOrEmpty(path) && File.Exists(path) && onFileReceived != null)
                                {
                                    onFileReceived(path);
                                }
                            }
                        }
                    }
                    catch { Thread.Sleep(500); }
                }
            });
        }
    }
}
