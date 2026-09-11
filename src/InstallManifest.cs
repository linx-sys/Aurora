using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Aurora.Tests")]

namespace Aurora
{
    internal sealed class InstallFile
    {
        [JsonRequired]
        public string RelativePath { get; set; } = "";
        [JsonRequired]
        public string Sha256 { get; set; } = "";
    }

    internal sealed class InstallManifest
    {
        internal const string FileName = "aurora-install-manifest.json";
        internal const string ProductId = "AuroraPlayer";
        internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer";
        internal static readonly IReadOnlyList<string> PayloadFiles = Array.AsReadOnly(new[]
        {
            "AuroraPlayer.exe", "AuroraPlayer.dll", "AuroraPlayer.runtimeconfig.json", "AuroraPlayer.deps.json",
            "unins.exe", "unins.dll", "unins.runtimeconfig.json", "unins.deps.json",
            "NVorbis.dll", "Concentus.dll", "Concentus.Oggfile.dll", "NAudio.dll", "NAudio.Core.dll",
            "NAudio.WinMM.dll", "NAudio.Wasapi.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll",
            "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
            "SQLitePCLRaw.batteries_v2.dll", "e_sqlite3.dll"
        });
        static readonly HashSet<string> AllowedFiles = new HashSet<string>(PayloadFiles.Concat(new[] { ".associate" }), StringComparer.OrdinalIgnoreCase);
        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

        [JsonRequired]
        public int Schema { get; set; } = 1;
        [JsonRequired]
        public string Product { get; set; } = ProductId;
        [JsonRequired]
        public string Version { get; set; } = "";
        [JsonRequired]
        public string CanonicalInstallDir { get; set; } = "";
        [JsonRequired]
        public List<InstallFile> Files { get; set; } = new List<InstallFile>();

        internal static string ValidateInstallDirectory(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Length < 3 || !char.IsAsciiLetter(raw[0]) || raw[1] != ':' || (raw[2] != '\\' && raw[2] != '/'))
                throw new InvalidDataException("请选择本地磁盘上的绝对路径，不支持相对路径、网络路径或设备路径。");
            foreach (string part in raw.Substring(3).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string stem = part.Split('.')[0];
                if (part == "." || part == ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                    stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && char.IsDigit(stem[3])))
                    throw new InvalidDataException("安装路径包含不安全的路径段。");
            }
            string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            if (SamePath(dir, Path.GetPathRoot(dir))) throw new InvalidDataException("不能安装到磁盘根目录。");
            foreach (var folder in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86 })
            {
                string systemDir = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(systemDir) && IsWithin(dir, systemDir)) throw new InvalidDataException("不能安装到系统目录中。");
            }
            foreach (var folder in new[]
            {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyPictures,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Favorites, Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.Programs, Environment.SpecialFolder.StartMenu, Environment.SpecialFolder.CommonPrograms,
                Environment.SpecialFolder.CommonStartMenu, Environment.SpecialFolder.CommonDesktopDirectory,
                Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonProgramFiles, Environment.SpecialFolder.CommonProgramFilesX86
            })
            {
                string reserved = Environment.GetFolderPath(folder);
                if (!string.IsNullOrEmpty(reserved) && SamePath(dir, reserved)) throw new InvalidDataException("请选择专用子目录，不能直接使用个人目录或共享程序目录。");
            }
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string name in new[] { "Downloads", "Contacts", "Links", "Saved Games", "Searches" })
                if (!string.IsNullOrEmpty(profile) && SamePath(dir, Path.Combine(profile, name)))
                    throw new InvalidDataException("请选择专用子目录，不能直接使用个人目录。");
            string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localData) && SamePath(dir, Path.Combine(localData, "Programs")))
                throw new InvalidDataException("请选择 Programs 下的专用子目录。");
            EnsureNoReparsePoints(dir);
            if (File.Exists(dir)) throw new InvalidDataException("安装路径不是目录。");
            return dir;
        }

        internal static bool SamePath(string? left, string? right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right) &&
                string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);
        }

        static bool IsWithin(string path, string parent)
        {
            return SamePath(path, parent) || path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void EnsureNoReparsePoints(string path, Func<string, FileAttributes>? readAttributes = null)
        {
            readAttributes ??= File.GetAttributes;
            var chain = new Stack<string>();
            for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current)) chain.Push(current);
            // 从根向下检查，不能先访问经联接重定向的子路径。
            foreach (string current in chain)
            {
                try
                {
                    if ((readAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("路径包含符号链接或目录联接，已停止操作：" + current);
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static string ResolveFile(string dir, string relativePath)
        {
            // 本产品只释放固定根目录文件；清单不能扩大到任意相对路径或用户文件。
            if (string.IsNullOrEmpty(relativePath) || relativePath.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || !AllowedFiles.Contains(relativePath))
                throw new InvalidDataException("安装清单包含不允许的文件路径。");
            return Path.Combine(dir, relativePath);
        }

        internal void Validate(string installDir, string? registeredDir = null)
        {
            string canonical = ValidateInstallDirectory(installDir);
            if (Schema != 1 || Product != ProductId || !System.Version.TryParse(Version, out _) || Files == null || Files.Count == 0 || Files.Count > AllowedFiles.Count)
                throw new InvalidDataException("安装清单的版本、产品或文件列表无效。");
            if (!string.Equals(CanonicalInstallDir, ValidateInstallDirectory(CanonicalInstallDir), StringComparison.OrdinalIgnoreCase) || !SamePath(canonical, CanonicalInstallDir))
                throw new InvalidDataException("安装清单与目标目录不一致。");
            if (registeredDir != null && !SamePath(canonical, ValidateInstallDirectory(registeredDir)))
                throw new InvalidDataException("安装目录与注册表 InstallLocation 不一致。");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (InstallFile file in Files)
            {
                if (file == null) throw new InvalidDataException("安装清单包含空文件记录。");
                ResolveFile(canonical, file.RelativePath);
                if (!seen.Add(file.RelativePath) || !IsSha256(file.Sha256)) throw new InvalidDataException("安装清单包含重复文件或无效 SHA256。");
            }
        }

        internal static bool IsSha256(string? hash) => hash != null && hash.Length == 64 && hash.All(Uri.IsHexDigit);

        internal static string HashFile(string path)
        {
            EnsureNoReparsePoints(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        internal static InstallManifest Load(string installDir, string? registeredDir = null)
        {
            string dir = ValidateInstallDirectory(installDir);
            string path = Path.Combine(dir, FileName);
            EnsureNoReparsePoints(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 65536) throw new InvalidDataException("安装清单过大。");
            try
            {
                var manifest = JsonSerializer.Deserialize<InstallManifest>(stream, JsonOptions) ?? throw new InvalidDataException("安装清单为空。");
                manifest.Validate(dir, registeredDir);
                return manifest;
            }
            catch (JsonException ex) { throw new InvalidDataException("安装清单格式或必需字段无效。", ex); }
        }

        internal byte[] Serialize()
        {
            Validate(CanonicalInstallDir);
            return JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        }

        internal static InstallManifest? ValidateInstallDestination(string raw)
        {
            string dir = ValidateInstallDirectory(raw);
            if (!Directory.Exists(dir) || !Directory.EnumerateFileSystemEntries(dir).Any()) return null;
            string manifestPath = Path.Combine(dir, FileName);
            if (!File.Exists(manifestPath)) throw new InvalidDataException("该目录非空且没有有效安装清单。旧版本不支持直接覆盖，请选择新的专用空目录。");
            InstallManifest manifest = Load(dir);
            foreach (string name in AllowedFiles)
            {
                string target = ResolveFile(dir, name);
                EnsureNoReparsePoints(target);
                if (Directory.Exists(target)) throw new InvalidDataException("安装文件名已被目录占用：" + name);
                if (!File.Exists(target)) continue;
                var record = manifest.Files.FirstOrDefault(f => string.Equals(f.RelativePath, name, StringComparison.OrdinalIgnoreCase));
                if (record == null || !string.Equals(record.Sha256, HashFile(target), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("现有文件未登记或已修改，不会覆盖：" + name + "。请选择新的专用空目录。");
            }
            return manifest;
        }

        internal List<InstallFile> CreateDeletionPlan(string installDir, string registeredDir)
        {
            if (string.IsNullOrWhiteSpace(registeredDir)) throw new InvalidDataException("缺少注册表 InstallLocation，无法安全卸载。");
            Validate(installDir, registeredDir);
            var plan = new List<InstallFile>();
            foreach (var file in Files)
            {
                string path = ResolveFile(CanonicalInstallDir, file.RelativePath);
                if (FileMatches(path, file.Sha256)) plan.Add(new InstallFile { RelativePath = file.RelativePath, Sha256 = file.Sha256 });
            }
            return plan;
        }

        internal static bool FileMatches(string path, string expectedHash)
        {
            if (!IsSha256(expectedHash)) return false;
            try { return string.Equals(HashFile(path), expectedHash, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException) { return false; }
        }

        internal static void CloseInstalledProcess(string expectedExe)
        {
            EnsureNoReparsePoints(expectedExe);
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(expectedExe)))
            {
                using (process)
                {
                    string? actual;
                    try { actual = process.MainModule?.FileName; }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { continue; }
                    if (actual == null || !SamePath(actual, expectedExe)) continue;
                    try
                    {
                        if (process.HasExited) continue;
                        process.CloseMainWindow();
                        if (process.WaitForExit(4000)) continue;
                        // 不强杀：无主窗口或未响应时，请用户保存数据后自行退出。
                        throw new IOException("请先关闭此安装目录中的程序后重试：" + expectedExe);
                    }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }
}
