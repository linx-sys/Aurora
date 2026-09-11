using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Aurora.Tests
{
    public sealed class InstallSafetyTests : IDisposable
    {
        readonly string root;
        readonly List<string> createdFiles = new List<string>();
        readonly List<string> createdDirectories = new List<string>();

        public InstallSafetyTests()
        {
            // 仅在测试程序集输出目录创建自己的随机目录，不访问或清理个人数据。
            root = Path.Combine(AppContext.BaseDirectory, "install-safety-" + Guid.NewGuid().ToString("N"));
            InstallManifest.EnsureNoReparsePoints(root);
            Directory.CreateDirectory(root);
            createdDirectories.Add(root);
        }

        string Write(string name, string content)
        {
            string path = Path.Combine(root, name);
            InstallManifest.EnsureNoReparsePoints(path);
            using (var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))) writer.Write(content);
            createdFiles.Add(path);
            return path;
        }

        InstallManifest Manifest(params string[] names)
        {
            return new InstallManifest
            {
                Version = "3.0.0",
                CanonicalInstallDir = InstallManifest.ValidateInstallDirectory(root),
                Files = names.Select(name => new InstallFile { RelativePath = name, Sha256 = InstallManifest.HashFile(Path.Combine(root, name)) }).ToList()
            };
        }

        void Save(InstallManifest manifest)
        {
            string path = Path.Combine(root, InstallManifest.FileName);
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(manifest.Serialize());
            createdFiles.Add(path);
        }

        [Theory]
        [InlineData("")]
        [InlineData("relative\\AuroraPlayer")]
        [InlineData("C:AuroraPlayer")]
        [InlineData("C:\\")]
        [InlineData("C:/")]
        [InlineData("\\\\server\\share\\AuroraPlayer")]
        [InlineData("\\\\?\\C:\\AuroraPlayer")]
        [InlineData("C:\\apps\\..\\AuroraPlayer")]
        [InlineData("C:\\apps\\.\\AuroraPlayer")]
        [InlineData("C:\\apps\\AuroraPlayer.")]
        [InlineData("C:\\apps\\AuroraPlayer ")]
        [InlineData("C:\\apps\\Aurora:stream")]
        [InlineData("C:\\apps\\CON")]
        public void InstallPath_RejectsAmbiguousOrUnsafePaths(string path)
        {
            Assert.ThrowsAny<Exception>(() => InstallManifest.ValidateInstallDirectory(path));
        }

        [Fact]
        public void InstallPath_RejectsPersonalRootsAndWindowsDescendants()
        {
            foreach (var folder in new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.LocalApplicationData })
            {
                string path = Environment.GetFolderPath(folder);
                if (path.Length > 0) Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDirectory(path));
            }
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDirectory(Path.Combine(windows, "Temp", "AuroraPlayer")));
        }

        [Fact]
        public void InstallPath_AllowsDedicatedLocalProgramsDirectory()
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "AuroraPlayer");
            Assert.True(InstallManifest.SamePath(path, InstallManifest.ValidateInstallDirectory(path)));
            Assert.Equal(root, InstallManifest.ValidateInstallDirectory(root + Path.DirectorySeparatorChar));
        }

        [Theory]
        [InlineData("..\\music.mp3")]
        [InlineData("../music.mp3")]
        [InlineData("C:\\music.mp3")]
        [InlineData("music.mp3")]
        [InlineData("sub\\AuroraPlayer.dll")]
        [InlineData("AuroraPlayer.dll:stream")]
        [InlineData("AuroraPlayer.dll.")]
        [InlineData("aurora-install-manifest.json")]
        public void Manifest_CannotAuthorizeArbitraryOrEscapingFiles(string relativePath)
        {
            var manifest = new InstallManifest { Version = "3.0.0", CanonicalInstallDir = root };
            manifest.Files.Add(new InstallFile { RelativePath = relativePath, Sha256 = new string('A', 64) });
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
        }

        [Fact]
        public void Manifest_RejectsWrongProductSchemaVersionHashDuplicateAndDirectory()
        {
            Write("AuroraPlayer.exe", "generated");
            var manifest = Manifest("AuroraPlayer.exe");
            manifest.Product = "OtherProduct";
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
            manifest.Product = InstallManifest.ProductId;
            manifest.Schema = 2;
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
            manifest.Schema = 1;
            manifest.Version = "invalid";
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
            manifest.Version = "3.0.0";
            manifest.Files[0].Sha256 = "bad-hash";
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
            manifest = Manifest("AuroraPlayer.exe");
            manifest.Files.Add(new InstallFile { RelativePath = "AURORAPLAYER.EXE", Sha256 = manifest.Files[0].Sha256 });
            Assert.Throws<InvalidDataException>(() => manifest.Validate(root));
            manifest = Manifest("AuroraPlayer.exe");
            Assert.Throws<InvalidDataException>(() => manifest.Validate(Path.Combine(root, "other")));
            Assert.Throws<InvalidDataException>(() => manifest.CreateDeletionPlan(root, Path.Combine(root, "other")));
            Assert.Throws<InvalidDataException>(() => manifest.CreateDeletionPlan(root, ""));
        }

        [Fact]
        public void Manifest_RoundTripsOnlyGeneratedWhitelistFiles()
        {
            Write("AuroraPlayer.exe", "generated app");
            Write("unins.dll", "generated uninstaller");
            var manifest = Manifest("AuroraPlayer.exe", "unins.dll");
            Save(manifest);
            var loaded = InstallManifest.Load(root, root);
            Assert.Equal(2, loaded.Files.Count);
            Assert.Equal(InstallManifest.ProductId, loaded.Product);
            Assert.Equal("3.0.0", loaded.Version);
            Assert.Equal(manifest.Files[0].Sha256, loaded.Files[0].Sha256);
        }

        [Fact]
        public void Destination_RejectsLegacyNonemptyDirectoryWithoutManifest()
        {
            Assert.Null(InstallManifest.ValidateInstallDestination(root));
            string userFile = Write("music.mp3", "user music");
            Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDestination(root));
            Assert.Equal("user music", File.ReadAllText(userFile));
        }

        [Fact]
        public void Destination_RejectsMalformedManifest()
        {
            Write(InstallManifest.FileName, "{}");
            Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDestination(root));
        }

        [Fact]
        public void Destination_RefusesOverwritingModifiedOrUnregisteredPayloadNames()
        {
            string app = Write("AuroraPlayer.exe", "original app");
            Save(Manifest("AuroraPlayer.exe"));
            Assert.NotNull(InstallManifest.ValidateInstallDestination(root));
            string other = Write("unins.dll", "unknown same-name file");
            Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDestination(root));
            Assert.Equal("unknown same-name file", File.ReadAllText(other));
            File.WriteAllText(app, "modified app");
            Assert.Throws<InvalidDataException>(() => InstallManifest.ValidateInstallDestination(root));
            Assert.Equal("modified app", File.ReadAllText(app));
        }

        [Fact]
        public void DeletionPlan_PreservesUnknownModifiedMissingAndNestedFiles()
        {
            string app = Write("AuroraPlayer.exe", "generated app");
            string unins = Write("unins.dll", "generated uninstaller");
            string missing = Write("unins.deps.json", "generated deps");
            string userFile = Write("music.mp3", "user music");
            string subdir = Path.Combine(root, "music");
            Directory.CreateDirectory(subdir);
            createdDirectories.Add(subdir);
            string nested = Write(Path.Combine("music", "song.mp3"), "nested user music");
            var manifest = Manifest("AuroraPlayer.exe", "unins.dll", "unins.deps.json");
            File.WriteAllText(unins, "modified uninstaller");
            File.Delete(missing);
            var plan = manifest.CreateDeletionPlan(root, root);
            Assert.Equal("AuroraPlayer.exe", Assert.Single(plan).RelativePath);
            Assert.Equal("generated app", File.ReadAllText(app));
            Assert.Equal("modified uninstaller", File.ReadAllText(unins));
            Assert.Equal("user music", File.ReadAllText(userFile));
            Assert.Equal("nested user music", File.ReadAllText(nested));
        }

        [Fact]
        public void FileMatches_RejectsChangedContentAfterPlanning()
        {
            string app = Write("AuroraPlayer.exe", "original app");
            var manifest = Manifest("AuroraPlayer.exe");
            InstallFile planned = Assert.Single(manifest.CreateDeletionPlan(root, root));
            File.WriteAllText(app, "changed after planning");
            Assert.False(InstallManifest.FileMatches(app, planned.Sha256));
        }

        [Fact]
        public void ReparseCheck_RejectsAncestorBeforeInspectingDescendants()
        {
            string linked = Path.Combine(root, "linked");
            string child = Path.Combine(linked, "child", "AuroraPlayer.exe");
            var visited = new List<string>();
            Assert.Throws<InvalidDataException>(() => InstallManifest.EnsureNoReparsePoints(child, path =>
            {
                visited.Add(path);
                return InstallManifest.SamePath(path, linked) ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory;
            }));
            Assert.Contains(linked, visited);
            Assert.DoesNotContain(child, visited);
            Assert.DoesNotContain(Path.Combine(linked, "child"), visited);
        }

        [Fact]
        public void ReparseCheck_RejectsLeafAndFailsClosedForInaccessibleAncestors()
        {
            string file = Path.Combine(root, "AuroraPlayer.exe");
            Assert.Throws<InvalidDataException>(() => InstallManifest.EnsureNoReparsePoints(file, path =>
                InstallManifest.SamePath(path, file) ? FileAttributes.ReparsePoint : FileAttributes.Directory));
            Assert.Throws<UnauthorizedAccessException>(() => InstallManifest.EnsureNoReparsePoints(file, _ => throw new UnauthorizedAccessException()));
        }

        public void Dispose()
        {
            // 只删除本测试创建的确切路径，禁止递归清理。
            for (int i = createdFiles.Count - 1; i >= 0; i--)
            {
                InstallManifest.EnsureNoReparsePoints(createdFiles[i]);
                File.Delete(createdFiles[i]);
            }
            for (int i = createdDirectories.Count - 1; i >= 0; i--)
            {
                InstallManifest.EnsureNoReparsePoints(createdDirectories[i]);
                Directory.Delete(createdDirectories[i], false);
            }
        }
    }
}
