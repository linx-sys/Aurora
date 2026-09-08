/* ============================================================
 * Uninstaller.cs — 卸载器（unins.exe）
 * 流程：确认 → 复制自身到 %TEMP% → 临时副本清理文件/注册表/快捷方式 → 自删
 * ============================================================ */
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aurora
{
    static class Uninstaller
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        const int SHCNE_ASSOCCHANGED = 0x08000000;

        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            string selfExe = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
            string selfDir = Path.GetDirectoryName(selfExe);
            bool secondPhase = args.Length > 0 && args[0] == "/clean";

            if (!secondPhase)
            {
                var r = MessageBox.Show(
                    "确定卸载 Aurora 极光音乐吗？\n\n" +
                    "· 将删除安装目录、快捷方式与文件关联\n" +
                    "· 你的音乐文件不会被删除",
                    "卸载 Aurora 极光音乐", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) return;

                // 杀掉正在运行的播放器
                try
                {
                    foreach (var p in Process.GetProcessesByName("AuroraPlayer"))
                        p.Kill();
                }
                catch { }

                // 第一阶段：把自身复制到 %TEMP%，由临时副本执行清理（运行中的 exe 无法删除自己）
                string tempCopy = Path.Combine(Path.GetTempPath(), "aurora_unins_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                try
                {
                    File.Copy(selfExe, tempCopy, true);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = tempCopy,
                        Arguments = "/clean \"" + selfDir + "\"",
                        UseShellExecute = false,
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("卸载启动失败：" + ex.Message, "Aurora", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                return;
            }

            // ---- 第二阶段（TEMP 副本）：args[1] = 安装目录 ----
            if (args.Length < 2) return;
            string installDir = args[1].Trim('"');

            RemoveRegistry();
            RemoveShortcuts();

            try
            {
                foreach (string f in Directory.GetFiles(installDir))
                {
                    try { File.Delete(f); } catch { }
                }
                foreach (string d in Directory.GetDirectories(installDir))
                {
                    try { Directory.Delete(d, true); } catch { }
                }
                try { Directory.Delete(installDir, false); } catch { }
            }
            catch { }

            // 延迟删除 TEMP 里的自己
            try
            {
                string self = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c ping -n 3 127.0.0.1 > nul & del /f /q \"" + self + "\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                });
            }
            catch { }
        }

        static void RemoveRegistry()
        {
            try
            {
                // 解除全部 9 种音频格式的关联
                string[] exts = { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus", ".wma" };
                foreach (string ext in exts)
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, true))
                    {
                        if (k != null)
                        {
                            string val = Convert.ToString(k.GetValue(null));
                            if (val == "Aurora.Audio" || val == "Aurora.Audio.mp3")
                                k.DeleteValue(null, false);
                        }
                    }
                }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Aurora.Audio", false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Aurora.Audio.mp3", false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\AuroraPlayer.exe", false); } catch { }
                try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer", false); } catch { }
                SHChangeNotify(SHCNE_ASSOCCHANGED, 0, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        static void RemoveShortcuts()
        {
            try
            {
                string startMenu = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Aurora Player");
                if (Directory.Exists(startMenu))
                {
                    Directory.Delete(startMenu, true);
                }
                string desktop = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Aurora Player.lnk");
                if (File.Exists(desktop)) File.Delete(desktop);
            }
            catch { }
        }
    }
}
