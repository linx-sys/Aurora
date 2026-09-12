#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * Assoc.cs — 音频文件关联（HKCU，无需管理员权限）
 * 入口：AuroraPlayer.exe /associate（显式）或安装后首启读取安装目录下的 .associate 标记。
 * 只写 HKCU\Software\Classes 下的 ProgID 与扩展名默认值；
 * 不改动 FileExts\<ext>\UserChoice（系统默认应用记录），需要设为默认时引导用户去系统设置。
 * ============================================================ */
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Aurora
{
    static class Assoc
    {
        const string ProgId = "Aurora.Audio";
        const string OldProgId = "Aurora.Audio.mp3";   // 旧版 ProgId，卸载时清理
        const string ExeName = "AuroraPlayer.exe";

        // 支持的 9 种音频格式：扩展名 → MIME Content Type
        static readonly string[,] Formats = {
            { ".mp3",  "audio/mpeg" },
            { ".m4a",  "audio/mp4" },
            { ".flac", "audio/flac" },
            { ".wav",  "audio/wav" },
            { ".ogg",  "audio/ogg" },
            { ".oga",  "audio/ogg" },
            { ".aac",  "audio/aac" },
            { ".opus", "audio/ogg" },
            { ".wma",  "audio/x-ms-wma" },
        };

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        const int SHCNE_ASSOCCHANGED = 0x08000000;

        static RegistryKey CreateClassesKey(params string[] parts)
        {
            // 注意：不能 string.Join(sep, "a", "b", parts)——会误选 Join(string, params object[])
            // 重载，把数组 ToString 成 "System.String[]"。这里用 StringBuilder 显式拼接。
            var sb = new StringBuilder("Software\\Classes");
            foreach (string p in parts)
            {
                sb.Append('\\').Append(p);
            }
            return Registry.CurrentUser.CreateSubKey(sb.ToString());
        }

        /// <summary>文件关联命令中的“被打开文件”占位参数（引号 + 百分号 + 数字一）。</summary>
        static string FileParamToken()
        {
            var token = new char[4];
            token[0] = (char)34;
            token[1] = (char)37;
            token[2] = (char)49;
            token[3] = (char)34;
            return new string(token);
        }

        static string QuotePath(string path)
        {
            return "\"" + path + "\"";
        }

        /// <summary>
        /// 解析用于文件关联的可执行文件路径。
        /// <para>
        /// 不能用 <c>Assembly.Location</c>：框架依赖应用里它返回的是 <c>AuroraPlayer.dll</c>（单文件发布下为空串），
        /// 会把关联命令写成 <c>"…\AuroraPlayer.dll" "%1"</c>，导致"打开方式 → Aurora"无法启动。
        /// 正确来源是进程自身的可执行文件路径，回退到程序目录下的 apphost。
        /// </para>
        /// </summary>
        internal static string ResolveExePath(string processPath, string baseDirectory)
        {
            if (!string.IsNullOrEmpty(processPath)) return processPath;
            string dir = baseDirectory;
            if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(dir)) return "";
            return Path.Combine(dir, ExeName);
        }

        static string CurrentExePath() => ResolveExePath(Environment.ProcessPath, AppContext.BaseDirectory);

        /// <summary>注册全部 9 种格式的文件关联（当前用户）。</summary>
        public static void Register() { Register(null); }

        /// <summary>注册文件关联（当前用户）。selectedExts 为 null 或空时注册全部 9 种。</summary>
        public static void Register(string[] selectedExts)
        {
            try
            {
                string exePath = CurrentExePath();
                Log("Register begin, exe=" + exePath);
                if (string.IsNullOrEmpty(exePath)) { Log("empty exe path"); return; }
                string openValue = QuotePath(exePath) + " " + FileParamToken();
                string iconValue = QuotePath(exePath) + ",0";

                using (var prog = CreateClassesKey(ProgId))
                {
                    Log("progid key=" + (prog != null));
                    prog.SetValue(null, "Aurora 极光音乐音频");
                }
                Log("progid set ok");
                using (var ic = CreateClassesKey(ProgId, "DefaultIcon"))
                    ic.SetValue(null, iconValue);
                using (var cm = CreateClassesKey(ProgId, "shell", "open", "command"))
                    cm.SetValue(null, openValue);
                Log("command set ok");

                // 注册选中的音频格式扩展名关联（selectedExts 为 null/空时全部注册）
                for (int i = 0; i < Formats.GetLength(0); i++)
                {
                    string ext = Formats[i, 0];
                    if (selectedExts != null && selectedExts.Length > 0 && Array.IndexOf(selectedExts, ext) < 0) continue;
                    string mime = Formats[i, 1];
                    using (var extKey = CreateClassesKey(ext))
                    {
                        extKey.SetValue(null, ProgId);
                        extKey.SetValue("Content Type", mime);
                    }
                    using (var ow = CreateClassesKey(ext, "OpenWithProgids"))
                        ow.SetValue(ProgId, "");
                    // 刻意不触碰 FileExts\<ext>\UserChoice：那是用户在系统里显式选定默认程序的记录
                    // （带系统校验 Hash）。删除它等于在用户毫无感知的情况下抢走默认关联，且卸载时无法还原；
                    // 部分系统该键受 ACL 保护，删也会失败，行为因机器而异。
                    // 需要把 Aurora 设为默认时，走设置里的"设为默认播放器"前往系统设置由用户自行选择。
                }
                using (var app = CreateClassesKey("Applications", ExeName, "shell", "open", "command"))
                    app.SetValue(null, openValue);
                using (var cap = CreateClassesKey("Applications", ExeName))
                    cap.SetValue("FriendlyAppName", "Aurora 极光音乐");
                Log("all keys set ok");
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
                Log("register done");
            }
            catch (Exception ex) { Log("Register EX: " + ex); }
        }

        static void Log(string msg)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "aurora_assoc.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff ") + msg + "\r\n");
            }
            catch { }
        }

        /// <summary>解除文件关联（若关联属于我们）。</summary>
        public static void Unregister()
        {
            try
            {
                // 解除全部 9 种格式的扩展名关联
                for (int i = 0; i < Formats.GetLength(0); i++)
                {
                    string ext = Formats[i, 0];
                    using (var k = Registry.CurrentUser.OpenSubKey(string.Join("\\", "Software", "Classes", ext), true))
                    {
                        if (k != null && Convert.ToString(k.GetValue(null)) == ProgId)
                            k.DeleteValue(null, false);
                    }
                }
                // 清理当前和旧版 ProgId
                try { Registry.CurrentUser.DeleteSubKeyTree(string.Join("\\", "Software", "Classes", ProgId), false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(string.Join("\\", "Software", "Classes", OldProgId), false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(string.Join("\\", "Software", "Classes", "Applications", ExeName), false); } catch { }
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        /// <summary>验证指定扩展名的关联是否指向本程序（HKCU 写入后可能被系统 UserChoice 覆盖）。</summary>
        public static bool IsAssociated(string ext)
        {
            try
            {
                // 检查扩展名默认值是否为我们的 ProgId
                using (var k = Registry.CurrentUser.OpenSubKey(
                    string.Join("\\", "Software", "Classes", ext), false))
                {
                    if (k == null) return false;
                    string def = Convert.ToString(k.GetValue(null));
                    if (def != ProgId && def != OldProgId) return false;
                }
                // 检查 UserChoice 是否被其他程序覆盖（Windows 10+ 可能存在）
                using (var uc = Registry.CurrentUser.OpenSubKey(
                    string.Join("\\", "Software", "Microsoft", "Windows", "CurrentVersion",
                        "Explorer", "FileExts", ext, "UserChoice"), false))
                {
                    if (uc != null)
                    {
                        string progId = Convert.ToString(uc.GetValue("ProgId"));
                        if (!string.IsNullOrEmpty(progId) && progId != ProgId && progId != OldProgId)
                            return false; // 被其他程序设为默认
                    }
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 打开 Windows 默认应用设置页面（ms-settings:defaultapps）。
        /// 当 HKCU 写入关联不生效时（被系统 UserChoice 或其他程序覆盖），
        /// 引导用户在设置页面手动将音频格式关联到 Aurora。
        /// </summary>
        public static bool OpenDefaultAppsSettings()
        {
            try
            {
                // ms-settings:defaultapps 是 Windows 10/11 内置 URI 协议
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "ms-settings:defaultapps",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                // 回退：打开控制面板的默认程序页面
                try
                {
                    System.Diagnostics.Process.Start("control.exe", "/name Microsoft.DefaultPrograms");
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
