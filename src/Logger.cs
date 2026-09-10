/* ============================================================
 * Logger.cs — 统一日志（应用日志 + 崩溃日志）
 * P3（评审建议 #20）：调试输出不再散落 %TEMP%，崩溃日志按次独立成档。
 *
 * 应用日志：%LOCALAPPDATA%\Aurora\Logs\aurora.log（>5MB 轮转为 .old）
 * 崩溃日志：%LOCALAPPDATA%\Aurora\Logs\crash_yyyyMMdd_HHmmss.log
 *          （含版本 / OS / 异常类型与完整堆栈，一次崩溃一个文件）
 * ============================================================ */
using System;
using System.IO;
using System.Text;

namespace Aurora
{
    public static class Logger
    {
        static readonly object _lock = new object();
        const long MaxLogBytes = 5 * 1024 * 1024;

        public static string LogDir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Aurora", "Logs");
            }
        }

        static string AppLogPath { get { return Path.Combine(LogDir, "aurora.log"); } }

        /// <summary>应用调试日志（原 MainViewModel.Dbg 的落盘实现）。</summary>
        public static void Dbg(string msg)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDir);
                    string path = AppLogPath;
                    try
                    {
                        var fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > MaxLogBytes)
                        {
                            string old = Path.Combine(LogDir, "aurora.old.log");
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                        }
                    }
                    catch { }
                    File.AppendAllText(path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [" + Environment.CurrentManagedThreadId + "] " + msg + "\r\n");
                }
            }
            catch { }
        }

        /// <summary>
        /// 崩溃日志：独立文件，含版本、OS、异常类型与完整堆栈。
        /// 返回日志路径（便于 UI 提示用户反馈时附上）；失败返回 null。
        /// </summary>
        public static string? Crash(string source, Exception? ex)
        {
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDir);
                    string path = Path.Combine(LogDir,
                        "crash_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
                    var sb = new StringBuilder();
                    sb.Append("==== Aurora 崩溃日志 ====\r\n");
                    sb.Append("时间: ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
                    sb.Append("版本: ").Append(AppInfo.Version).Append("\r\n");
                    sb.Append("来源: ").Append(source).Append("\r\n");
                    sb.Append("系统: ").Append(Environment.OSVersion.VersionString)
                      .Append(" (").Append(Environment.Is64BitProcess ? "x64" : "x86").Append(")\r\n");
                    sb.Append("运行时: ").Append(Environment.Version).Append("\r\n");
                    if (ex != null)
                    {
                        AppendException(sb, ex, 0);
                    }
                    else
                    {
                        sb.Append("（非 Exception 的异常对象）\r\n");
                    }
                    File.WriteAllText(path, sb.ToString());
                    return path;
                }
            }
            catch { return null; }
        }

        static void AppendException(StringBuilder sb, Exception ex, int depth)
        {
            string indent = depth > 0 ? new string(' ', depth * 2) : "";
            sb.Append(indent).Append("[").Append(ex.GetType().FullName).Append("] ")
              .Append(ex.Message).Append("\r\n");
            sb.Append(ex.StackTrace).Append("\r\n");
            if (ex.InnerException != null)
            {
                sb.Append(indent).Append("-- 内部异常 --\r\n");
                AppendException(sb, ex.InnerException, depth + 1);
            }
        }
    }
}
