using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Aurora.Tests
{
    /// <summary>
    /// 安装审计目录（.AuroraInstall-&lt;hash&gt;）的清理策略回归：
    /// ① 提交成功后不再保留载荷副本（.old/.new/replaced-*）；
    /// ② 被取代的旧事务目录在下一次事务开始时回收（回滚不等于清理，故回滚目录会留到下一次）；
    /// ③ 卸载彻底完成后可删除状态目录，但有未决事务时必须拒绝。
    /// </summary>
    public sealed class InstallStateCleanupTests
    {
        static Dictionary<string, byte[]> Payload(string label) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["AuroraPlayer.exe"] = Encoding.UTF8.GetBytes(label + " app"),
            ["unins.dll"] = Encoding.UTF8.GetBytes(label + " uninstall")
        };

        static string Root() => Path.Combine(AppContext.BaseDirectory, "state-case-" + Guid.NewGuid().ToString("N"), "Aurora");
        static string StateOf(string root) => InstallTransaction.StateDirectory(root);
        static string[] WorkDirs(string state) => Directory.Exists(state)
            ? Directory.GetDirectories(state).Where(d => Path.GetFileName(d).Length == 32).ToArray()
            : Array.Empty<string>();

        [Fact]
        public void CommittedTransactionKeepsNoPayloadCopies()
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            tx.Execute(Payload("v1"), "1.0.0");

            var second = Payload("v2");
            second.Remove("unins.dll");               // 覆盖“旧版有、新版不再分发”的移除路径
            tx.Execute(second, "2.0.0");

            string state = StateOf(root);
            Assert.True(Directory.Exists(state));
            Assert.Empty(WorkDirs(state));                                        // 载荷副本已清理
            string journal = File.ReadAllText(Path.Combine(state, "active.json"));
            Assert.Contains("\"Phase\": \"Committed\"", journal);                  // 事务日志保留
            Assert.True(File.Exists(Path.Combine(root, "AuroraPlayer.exe")));
            Assert.False(File.Exists(Path.Combine(root, "unins.dll")));
        }

        [Fact]
        public void SupersededTransactionDirectoryIsReclaimedByNextTransaction()
        {
            string root = Root();
            using var tx = new InstallTransaction(root);
            tx.Execute(Payload("v1"), "1.0.0");
            string state = StateOf(root);

            // 注入失败：回滚后该事务目录（备份/暂存）仍保留 —— 回滚不等于清理
            Assert.Throws<IOException>(() => tx.Execute(Payload("v2"), "2.0.0",
                checkpoint: stage => { if (stage == "applied:0") throw new IOException("injected failure"); }));
            Assert.Equal("RolledBack", tx.Recover());
            string[] rolledBack = WorkDirs(state);
            Assert.Single(rolledBack);

            // 下一次成功的事务应回收上面那个目录，且自身提交后也不留载荷副本
            tx.Execute(Payload("v3"), "3.0.0");
            Assert.Empty(WorkDirs(state));
        }

        [Fact]
        public void TryRemoveStateDirectoryRemovesStateWithoutPendingJournal()
        {
            string root = Root();
            using (var tx = new InstallTransaction(root)) { }   // 仅构造（等价 /recover 在空目录上的留痕场景）
            string state = StateOf(root);
            Assert.True(Directory.Exists(state));

            Assert.True(InstallTransaction.TryRemoveStateDirectory(root));
            Assert.False(Directory.Exists(state));
        }

        [Fact]
        public void TryRemoveStateDirectoryRemovesFinishedStateAfterUninstall()
        {
            string root = Root();
            using (var tx = new InstallTransaction(root))
            {
                tx.Execute(Payload("v1"), "1.0.0");
            }
            string state = StateOf(root);
            Assert.True(Directory.Exists(state));

            Assert.True(InstallTransaction.TryRemoveStateDirectory(root));
            Assert.False(Directory.Exists(state));
        }

        [Fact]
        public void TryRemoveStateDirectoryRefusesPendingTransaction()
        {
            string root = Root();
            string state = StateOf(root);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(state);
            File.WriteAllText(Path.Combine(state, "owner.txt"), root);

            // 伪造一份未决（Ready）事务日志：卸载完成也必须拒绝删除，避免丢掉可恢复现场
            string hash = new string('A', 64);
            string journal = JsonSerializer.Serialize(new
            {
                Schema = 1,
                Product = "AuroraPlayer",
                Directory = root,
                Id = Guid.NewGuid().ToString("N"),
                Phase = "Ready",
                Files = new object[]
                {
                    new { Name = "AuroraPlayer.exe", NewHash = hash },
                    new { Name = InstallManifest.FileName, NewHash = hash }
                }
            });
            File.WriteAllText(Path.Combine(state, "active.json"), journal);

            Assert.False(InstallTransaction.TryRemoveStateDirectory(root));
            Assert.True(Directory.Exists(state));
        }
    }
}
