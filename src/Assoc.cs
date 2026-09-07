/* ============================================================
 * Assoc.cs — MP3 文件关联（HKCU，无需管理员权限）
 * 由 AuroraPlayer.exe 自身执行：AuroraPlayer.exe /associate
 * 路径取自程序自身位置，安装向导以 /associate 参数调用。
 * ============================================================ */
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Aurora
{
    static class Assoc
    {
        const string ProgId = "Aurora.Audio.mp3";
        const string ExeName = "AuroraPlayer.exe";

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

        /// <summary>注册文件关联（当前用户）。</summary>
        public static void Register()
        {
            try
            {
                string exePath = Assembly.GetExecutingAssembly().Location;
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

                using (var ext = CreateClassesKey(".mp3"))
                {
                    ext.SetValue(null, ProgId);
                    ext.SetValue("Content Type", "audio/mpeg");
                }
                using (var ow = CreateClassesKey(".mp3", "OpenWithProgids"))
                    ow.SetValue(ProgId, "");
                using (var app = CreateClassesKey("Applications", ExeName, "shell", "open", "command"))
                    app.SetValue(null, openValue);
                using (var cap = CreateClassesKey("Applications", ExeName))
                    cap.SetValue("FriendlyAppName", "Aurora 极光音乐");
                Log("all keys set ok");

                // 移除旧的用户选择，让关联立即生效
                try
                {
                    Registry.CurrentUser.DeleteSubKeyTree(string.Join(
                        "\\", "Software", "Microsoft", "Windows", "CurrentVersion", "Explorer", "FileExts", ".mp3", "UserChoice"),
                        false);
                }
                catch (Exception ex) { Log("UserChoice: " + ex.Message); }
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
                using (var k = Registry.CurrentUser.OpenSubKey(string.Join("\\", "Software", "Classes", ".mp3"), true))
                {
                    if (k != null && Convert.ToString(k.GetValue(null)) == ProgId)
                        k.DeleteValue(null, false);
                }
                try { Registry.CurrentUser.DeleteSubKeyTree(string.Join("\\", "Software", "Classes", ProgId), false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(string.Join("\\", "Software", "Classes", "Applications", ExeName), false); } catch { }
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }
    }
}
