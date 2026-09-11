using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Xunit;

namespace Aurora.Tests
{
    public sealed class InstallTransactionTests
    {
        static Dictionary<string, byte[]> Payload(string label) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["AuroraPlayer.exe"] = Encoding.UTF8.GetBytes(label + " app"),
            ["unins.dll"] = Encoding.UTF8.GetBytes(label + " uninstall")
        };
        // 持久保留在测试输出目录以便审计；不递归清理任意目录。
        static string Root() => Path.Combine(AppContext.BaseDirectory, "transaction-case-" + Guid.NewGuid().ToString("N"), "Aurora");
        static void AssertInstalled(string root, string label)
        {
            var manifest = InstallManifest.Load(root);
            foreach (var f in manifest.Files) Assert.True(InstallManifest.FileMatches(Path.Combine(root, f.RelativePath), f.Sha256));
            Assert.Equal(label + " app", File.ReadAllText(Path.Combine(root, "AuroraPlayer.exe")));
            Assert.NotNull(InstallManifest.ValidateInstallDestination(root));
        }
        [Fact]
        public void NormalInstallUpgradeAndUninstallPlanPreserveUserFiles()
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            tx.Execute(Payload("v1"), "1.0.0");
            File.WriteAllText(Path.Combine(root, "music.mp3"), "user music");
            Directory.CreateDirectory(Path.Combine(root, "user"));
            File.WriteAllText(Path.Combine(root, "user", "notes.txt"), "user notes");
            var second = Payload("v2");
            second.Remove("unins.dll");
            tx.Execute(second, "2.0.0");
            AssertInstalled(root, "v2");
            Assert.False(File.Exists(Path.Combine(root, "unins.dll")));
            var manifest = InstallManifest.Load(root);
            var plan = manifest.CreateDeletionPlan(root, root);
            Assert.Equal("AuroraPlayer.exe", Assert.Single(plan).RelativePath);
            foreach (var f in plan) File.Delete(Path.Combine(root, f.RelativePath));
            Assert.Equal("user music", File.ReadAllText(Path.Combine(root, "music.mp3")));
            Assert.Equal("user notes", File.ReadAllText(Path.Combine(root, "user", "notes.txt")));
        }
        [Theory]
        [InlineData("preparing")]
        [InlineData("staged:0")]
        [InlineData("ready")]
        [InlineData("applied:0")]
        [InlineData("applied:1")]
        [InlineData("applied:2")]
        [InlineData("before-commit")]
        public void ErrorRollsBackAndRetriesSameDirectory(string failAt)
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            tx.Execute(Payload("v1"), "1.0.0");
            byte[] manifestBefore = File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName));
            File.WriteAllText(Path.Combine(root, "music.mp3"), "untouched");
            Assert.Throws<IOException>(() => tx.Execute(Payload("v2"), "2.0.0", checkpoint: stage =>
            { if (stage == failAt) throw new IOException("injected failure"); }));
            AssertInstalled(root, "v1");
            Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(root, InstallManifest.FileName)));
            Assert.Equal("RolledBack", tx.Recover());
            Assert.Equal("RolledBack", tx.Recover());
            tx.Execute(Payload("v2"), "2.0.0");
            AssertInstalled(root, "v2");
            Assert.Equal("untouched", File.ReadAllText(Path.Combine(root, "music.mp3")));
        }
        [Theory]
        [InlineData("preparing")]
        [InlineData("staged:1")]
        [InlineData("ready")]
        [InlineData("applied:0")]
        [InlineData("applied:2")]
        [InlineData("before-commit")]
        public void CancellationFreshInstallRestoresAbsenceAndAllowsRetry(string cancelAt)
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            using var cancel = new CancellationTokenSource();
            Assert.ThrowsAny<OperationCanceledException>(() => tx.Execute(Payload("new"), "1.0.0", cancel.Token,
                stage => { if (stage == cancelAt) cancel.Cancel(); }));
            Assert.False(File.Exists(Path.Combine(root, InstallManifest.FileName)));
            Assert.False(File.Exists(Path.Combine(root, "AuroraPlayer.exe")));
            Assert.Null(InstallManifest.ValidateInstallDestination(root));
            tx.Execute(Payload("retry"), "1.0.0");
            AssertInstalled(root, "retry");
        }
        [Fact]
        public void UserMutationDuringFailureStopsRollbackWithoutOverwritingIt()
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            tx.Execute(Payload("v1"), "1.0.0");
            Assert.Throws<AggregateException>(() => tx.Execute(Payload("v2"), "2.0.0", checkpoint: stage =>
            {
                if (stage == "applied:0")
                {
                    File.WriteAllText(Path.Combine(root, "AuroraPlayer.exe"), "user changed");
                    throw new IOException("interrupted");
                }
            }));
            Assert.Throws<IOException>(() => tx.Recover());
            Assert.Equal("user changed", File.ReadAllText(Path.Combine(root, "AuroraPlayer.exe")));
            Assert.True(File.Exists(Path.Combine(tx.RecoveryDirectory, "active.json")));
        }
        [Fact]
        public void RejectsUnknownPayloadAndConcurrentTransaction()
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            Assert.Throws<IOException>(() => new InstallTransaction(root));
            Assert.Throws<InvalidDataException>(() => tx.Execute(new Dictionary<string, byte[]> { ["music.mp3"] = new byte[1] }, "1.0.0"));
            Assert.False(Directory.Exists(root));
        }
    }
}
