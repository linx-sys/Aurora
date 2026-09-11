#nullable disable
/* ============================================================
 * Uninstaller.cs — 两阶段卸载，仅删除本产品清单登记且内容未变的文件。
 * 临时副本保留在独立目录，不通过命令解释器执行延迟删除。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aurora
{
    static class Uninstaller
    {
        static readonly string[] UninstallerFiles = { "unins.exe", "unins.dll", "unins.runtimeconfig.json", "unins.deps.json" };

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("shell32.dll")]
        static extern void SHChangeNotify(int wEventId, int uFlags, IntPtr dwItem1, IntPtr dwItem2);

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => string.Equals(a, "/S", StringComparison.OrdinalIgnoreCase));
            try
            {
                try { SetProcessDPIAware(); } catch { }
                string selfExe = Environment.ProcessPath ?? throw new IOException("无法确定卸载器的完整路径。");
                string selfDir = Path.GetDirectoryName(selfExe);
                bool secondPhase = args.Length > 0 && string.Equals(args[0], "/clean", StringComparison.OrdinalIgnoreCase);
                if (secondPhase)
                {
                    if (args.Length != 5 || !int.TryParse(args[3], out int parentId) || (args[4] != "/S" && args[4] != "/interactive"))
                        throw new InvalidDataException("卸载参数无效，未执行清理。");
                    string dir = InstallManifest.ValidateInstallDirectory(args[1]);
                    WaitForOriginalUninstaller(parentId, Path.Combine(dir, "unins.exe"));
                    using var transaction = new InstallTransaction(dir);
                    transaction.Recover();
                    InstallManifest manifest = LoadRegisteredManifest(dir, args[2], out string manifestHash);
                    ValidateTemporaryCopy(selfExe, manifest);
                    WaitForOriginalUninstaller(parentId, Path.Combine(dir, "unins.exe"));
                    PerformUninstall(dir, manifest, manifestHash);
                    if (!silent) MessageBox.Show("卸载完成。修改过的文件、未知文件及用户音乐均已保留。", "Aurora", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (args.Length > 1 || (args.Length == 1 && !silent)) throw new InvalidDataException("不支持的卸载参数。");
                string installDir = InstallManifest.ValidateInstallDirectory(selfDir);
                using var initialTransaction = new InstallTransaction(installDir);
                initialTransaction.Recover();
                InstallManifest installed = LoadRegisteredManifest(installDir, null, out string expectedManifestHash);
                if (!InstallManifest.SamePath(selfExe, Path.Combine(installDir, "unins.exe")))
                    throw new InvalidDataException("请从登记的安装目录运行卸载器。");
                if (!silent && MessageBox.Show(
                    "确定卸载 Aurora 极光音乐吗？\n\n只删除安装清单登记且未修改的程序文件。\n未知文件、修改文件和音乐文件将保留。",
                    "卸载 Aurora 极光音乐", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 0;

                string tempRoot = Path.GetFullPath(Path.GetTempPath());
                string tempDir = Path.Combine(tempRoot, "aurora_unins_" + Guid.NewGuid().ToString("N"));
                InstallManifest.EnsureNoReparsePoints(tempDir);
                if (Directory.Exists(tempDir) || File.Exists(tempDir)) throw new IOException("临时卸载目录已存在。");
                Directory.CreateDirectory(tempDir);
                InstallManifest.EnsureNoReparsePoints(tempDir);
                foreach (string name in UninstallerFiles)
                {
                    var record = installed.Files.SingleOrDefault(f => string.Equals(f.RelativePath, name, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException("安装清单缺少卸载器依赖：" + name);
                    string source = InstallManifest.ResolveFile(installDir, name);
                    if (!InstallManifest.FileMatches(source, record.Sha256)) throw new InvalidDataException("卸载器依赖已改变或缺失：" + name);
                    string destination = Path.Combine(tempDir, name);
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                    if (!InstallManifest.FileMatches(destination, record.Sha256)) throw new InvalidDataException("临时卸载器依赖校验失败：" + name);
                }
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(tempDir, "unins.exe"),
                    WorkingDirectory = tempDir,
                    UseShellExecute = false
                };
                start.ArgumentList.Add("/clean");
                start.ArgumentList.Add(installDir);
                start.ArgumentList.Add(expectedManifestHash);
                start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                start.ArgumentList.Add(silent ? "/S" : "/interactive");
                using var child = Process.Start(start) ?? throw new IOException("无法启动临时卸载器。");
                return 0;
            }
            catch (Exception ex)
            {
                string message = "卸载未完成：" + ex.Message + "\n未确认归属或已修改的文件不会被删除。";
                if (silent) Console.Error.WriteLine(message);
                else MessageBox.Show(message, "Aurora", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        static InstallManifest LoadRegisteredManifest(string dir, string expectedHash, out string hash)
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallManifest.RegistryPath);
            string registeredDir = key?.GetValue("InstallLocation") as string;
            hash = key?.GetValue("ManifestSha256") as string;
            if (string.IsNullOrWhiteSpace(registeredDir) || !InstallManifest.IsSha256(hash))
                throw new InvalidDataException("缺少有效安装登记，旧版本或未登记目录不能自动清理。");
            if (expectedHash != null && !string.Equals(expectedHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("两阶段之间安装登记发生变化，已停止卸载。");
            string path = Path.Combine(InstallManifest.ValidateInstallDirectory(dir), InstallManifest.FileName);
            if (!InstallManifest.FileMatches(path, hash)) throw new InvalidDataException("安装清单缺失、已修改或不可信。");
            var manifest = InstallManifest.Load(dir, registeredDir);
            if (!InstallManifest.FileMatches(path, hash)) throw new InvalidDataException("读取期间安装清单发生变化。");
            return manifest;
        }

        static void ValidateTemporaryCopy(string selfExe, InstallManifest manifest)
        {
            string selfDir = Path.GetDirectoryName(selfExe);
            string name = Path.GetFileName(selfDir);
            if (!InstallManifest.SamePath(Path.GetDirectoryName(selfDir), Path.GetTempPath()) ||
                !name.StartsWith("aurora_unins_", StringComparison.Ordinal) || !Guid.TryParseExact(name.Substring("aurora_unins_".Length), "N", out _) ||
                !InstallManifest.SamePath(selfExe, Path.Combine(selfDir, "unins.exe")))
                throw new InvalidDataException("清理阶段必须从随机独立临时目录运行。");
            InstallManifest.EnsureNoReparsePoints(selfExe);
            foreach (string file in UninstallerFiles)
            {
                var record = manifest.Files.SingleOrDefault(f => string.Equals(f.RelativePath, file, StringComparison.OrdinalIgnoreCase));
                if (record == null || !InstallManifest.FileMatches(Path.Combine(selfDir, file), record.Sha256))
                    throw new InvalidDataException("临时卸载器文件与安装登记不符：" + file);
            }
        }

        static void WaitForOriginalUninstaller(int parentId, string expectedExe)
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                if (!InstallManifest.SamePath(parent.MainModule?.FileName, expectedExe)) return;
                if (!parent.WaitForExit(5000)) throw new IOException("原卸载器尚未退出，请关闭后重试。");
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        static void PerformUninstall(string dir, InstallManifest manifest, string manifestHash)
        {
            LoadRegisteredManifest(dir, manifestHash, out _);
            InstallManifest.CloseInstalledProcess(Path.Combine(dir, "AuroraPlayer.exe"));
            var plan = manifest.CreateDeletionPlan(dir, dir);
            var failures = new List<string>();
            foreach (var file in plan)
            {
                try
                {
                    LoadRegisteredManifest(dir, manifestHash, out _);
                    string path = InstallManifest.ResolveFile(dir, file.RelativePath);
                    // 执行前再次校验；清单不授权删除同名但已改变的文件。
                    if (!InstallManifest.FileMatches(path, file.Sha256)) continue;
                    InstallManifest.EnsureNoReparsePoints(path);
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { failures.Add(file.RelativePath + "：" + ex.Message); }
            }
            if (failures.Count > 0) throw new IOException("部分登记文件未能删除，请关闭占用程序后重试。\n" + string.Join("\n", failures));
            LoadRegisteredManifest(dir, manifestHash, out _);
            RemoveShortcuts(dir);
            RemoveRegistry(dir);
            InstallManifest.EnsureNoReparsePoints(dir);
            string manifestPath = Path.Combine(dir, InstallManifest.FileName);
            if (InstallManifest.FileMatches(manifestPath, manifestHash)) File.Delete(manifestPath);
            // 仅移除已经为空的安装目录，任何未知子目录都保留。
            InstallManifest.EnsureNoReparsePoints(dir);
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir, false);
        }

        static bool CommandBelongsTo(string commandKey, string exe)
        {
            using var key = Registry.CurrentUser.OpenSubKey(commandKey);
            string command = key?.GetValue(null) as string;
            return string.Equals(command, "\"" + exe + "\" \"%1\"", StringComparison.OrdinalIgnoreCase);
        }

        static void RemoveRegistry(string installDir)
        {
            string exe = Path.Combine(installDir, "AuroraPlayer.exe");
            string[] exts = { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus", ".wma" };
            foreach (string progId in new[] { "Aurora.Audio", "Aurora.Audio.mp3" })
            {
                string progKey = @"Software\Classes\" + progId;
                if (!CommandBelongsTo(progKey + @"\shell\open\command", exe)) continue;
                foreach (string ext in exts)
                {
                    using var key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext, true);
                    if (string.Equals(key?.GetValue(null) as string, progId, StringComparison.Ordinal)) key.DeleteValue(null, false);
                    using var openWith = Registry.CurrentUser.OpenSubKey(@"Software\Classes\" + ext + @"\OpenWithProgids", true);
                    openWith?.DeleteValue(progId, false);
                }
                Registry.CurrentUser.DeleteSubKeyTree(progKey, false);
            }
            string appKey = @"Software\Classes\Applications\AuroraPlayer.exe";
            if (CommandBelongsTo(appKey + @"\shell\open\command", exe)) Registry.CurrentUser.DeleteSubKeyTree(appKey, false);
            using (var key = Registry.CurrentUser.OpenSubKey(InstallManifest.RegistryPath))
                if (!InstallManifest.SamePath(key?.GetValue("InstallLocation") as string, installDir))
                    throw new InvalidDataException("安装登记已改变，保留注册表条目。");
            Registry.CurrentUser.DeleteSubKeyTree(InstallManifest.RegistryPath, false);
            SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero);
        }

        static void RemoveShortcuts(string installDir)
        {
            using var key = Registry.CurrentUser.OpenSubKey(InstallManifest.RegistryPath);
            if (!InstallManifest.SamePath(key?.GetValue("InstallLocation") as string, installDir))
                throw new InvalidDataException("安装登记已改变，保留快捷方式。");
            string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Aurora Player", "Aurora Player.lnk");
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Aurora Player.lnk");
            foreach (var item in new[] { (startMenu, "StartMenuShortcutSha256"), (desktop, "DesktopShortcutSha256") })
            {
                string hash = key.GetValue(item.Item2) as string;
                if (!InstallManifest.FileMatches(item.Item1, hash)) continue;
                InstallManifest.EnsureNoReparsePoints(item.Item1);
                File.Delete(item.Item1);
            }
        }
    }
}
