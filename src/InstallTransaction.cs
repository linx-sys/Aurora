/* ============================================================
 * InstallTransaction.cs — 安装文件事务：先落盘日志与备份，再逐文件原子替换。
 * 报错/取消回滚；异常退出由下一次安装或 /recover 回滚。
 * 同卷旁路目录保留日志和备份，不把用户目录中的未知文件纳入管理。
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Aurora
{
    internal sealed class InstallTransaction : IDisposable
    {
        internal sealed class Entry
        {
            public string Name { get; set; } = "";
            public string? OldHash { get; set; }
            public string? NewHash { get; set; }
        }
        internal sealed class Journal
        {
            public int Schema { get; set; } = 1;
            public string Product { get; set; } = InstallManifest.ProductId;
            public string Directory { get; set; } = "";
            public string Id { get; set; } = "";
            public string Phase { get; set; } = "Preparing";
            public List<Entry> Files { get; set; } = new();
        }
        readonly string dir;
        readonly string state;
        readonly FileStream gate;
        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        internal string RecoveryDirectory => state;
        internal static string StateDirectory(string directory)
        {
            string canonical = InstallManifest.ValidateInstallDirectory(directory);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToUpperInvariant())));
            return Path.Combine(Path.GetDirectoryName(canonical)!, ".AuroraInstall-" + key);
        }
        internal InstallTransaction(string directory)
        {
            dir = InstallManifest.ValidateInstallDirectory(directory);
            state = StateDirectory(dir);
            InstallManifest.EnsureNoReparsePoints(state);
            // 无恢复日志时先拒绝未知/旧版目录，不能为失败安装留下新旁路文件。
            if (!File.Exists(Path.Combine(state, "active.json"))) InstallManifest.ValidateInstallDestination(dir);
            Directory.CreateDirectory(state);
            string owner = Path.Combine(state, "owner.txt");
            InstallManifest.EnsureNoReparsePoints(owner);
            if (!File.Exists(owner))
            {
                // 不接管同名的非空用户目录。
                if (Directory.EnumerateFileSystemEntries(state).Any()) throw new IOException("恢复目录未登记，已停止：" + state);
                using var output = new FileStream(owner, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(Encoding.UTF8.GetBytes(dir));
                output.Flush(true);
            }
            if (!string.Equals(File.ReadAllText(owner), dir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("恢复目录归属不匹配：" + state);
            string lockPath = Path.Combine(state, "transaction.lock");
            InstallManifest.EnsureNoReparsePoints(lockPath);
            gate = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        string Active => Path.Combine(state, "active.json");
        string Work(Journal j) => Path.Combine(state, j.Id);
        string Backup(Journal j, int i) => Path.Combine(Work(j), i + ".old");
        string Stage(Journal j, int i) => Path.Combine(Work(j), i + ".new");
        string Target(Entry e) => e.Name == InstallManifest.FileName ? Path.Combine(dir, e.Name) : InstallManifest.ResolveFile(dir, e.Name);
        static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
        static bool Matches(string path, string? hash) => hash != null && InstallManifest.FileMatches(path, hash);
        static void WriteNew(string path, byte[] data)
        {
            InstallManifest.EnsureNoReparsePoints(path);
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            output.Write(data);
            output.Flush(true);
        }
        void Save(Journal j)
        {
            InstallManifest.EnsureNoReparsePoints(Active);
            string temp = Path.Combine(state, "journal-" + Guid.NewGuid().ToString("N") + ".tmp");
            WriteNew(temp, JsonSerializer.SerializeToUtf8Bytes(j, JsonOptions));
            if (File.Exists(Active)) File.Replace(temp, Active, null);
            else File.Move(temp, Active);
        }
        Journal? Load()
        {
            InstallManifest.EnsureNoReparsePoints(Active);
            if (!File.Exists(Active)) return null;
            if (new FileInfo(Active).Length > 131072) throw new InvalidDataException("恢复日志过大。");
            var j = JsonSerializer.Deserialize<Journal>(File.ReadAllBytes(Active)) ?? throw new InvalidDataException("恢复日志为空。");
            if (j.Schema != 1 || j.Product != InstallManifest.ProductId || !InstallManifest.SamePath(j.Directory, dir) ||
                !Guid.TryParseExact(j.Id, "N", out _) || j.Files == null || j.Files.Count == 0 || j.Files.Count > InstallManifest.PayloadFiles.Count + 2 ||
                !new[] { "Preparing", "Ready", "Committed", "RolledBack" }.Contains(j.Phase))
                throw new InvalidDataException("恢复日志格式或归属错误。");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in j.Files)
            {
                if (e == null || !names.Add(e.Name) || (e.OldHash != null && !InstallManifest.IsSha256(e.OldHash)) ||
                    (e.NewHash != null && !InstallManifest.IsSha256(e.NewHash))) throw new InvalidDataException("恢复文件记录无效。");
                _ = Target(e); // 固定白名单，禁止相对路径、ADS、任意用户文件。
            }
            if (j.Files[^1].Name != InstallManifest.FileName || j.Files[^1].NewHash == null)
                throw new InvalidDataException("恢复日志缺少最终清单。");
            InstallManifest.EnsureNoReparsePoints(Work(j));
            return j;
        }
        // 已完成事务保留审计备份，不在卸载时递归删除旁路目录。
        internal string Recover(Action<string>? checkpoint = null)
        {
            var j = Load();
            if (j == null || j.Phase == "RolledBack" || j.Phase == "Committed") return j?.Phase ?? "None";
            if (j.Phase == "Ready") Rollback(j, checkpoint);
            else { j.Phase = "RolledBack"; Save(j); } // Preparing 阶段从未修改目标。
            return "RolledBack";
        }
        void ValidateBackups(Journal j)
        {
            // 恢复日志不能借旧文件名扩大权限：旧内容必须来自已登记清单。
            int last = j.Files.Count - 1;
            InstallManifest? old = null;
            if (j.Files[last].OldHash != null)
            {
                if (!Matches(Backup(j, last), j.Files[last].OldHash)) throw new InvalidDataException("旧清单备份损坏，停止恢复并保留全部文件。");
                old = JsonSerializer.Deserialize<InstallManifest>(File.ReadAllBytes(Backup(j, last)), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (old == null) throw new InvalidDataException("旧清单备份无效。");
                old.Validate(dir);
            }
            for (int i = 0; i < last; i++)
            {
                var e = j.Files[i];
                if (e.OldHash != null && (old == null || !old.Files.Any(f => string.Equals(f.RelativePath, e.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.Sha256, e.OldHash, StringComparison.OrdinalIgnoreCase)))) throw new InvalidDataException("旧文件缺少归属证明。");
            }
            for (int i = 0; i < j.Files.Count; i++)
                if (j.Files[i].OldHash != null && !Matches(Backup(j, i), j.Files[i].OldHash))
                    throw new InvalidDataException("备份缺失或改变，停止恢复：" + j.Files[i].Name);
        }
        void Rollback(Journal j, Action<string>? checkpoint)
        {
            ValidateBackups(j);
            // 先检查全量目标，遇到用户修改时 fail closed，不覆盖也不删除。
            foreach (var e in j.Files)
            {
                string target = Target(e);
                InstallManifest.EnsureNoReparsePoints(target);
                if (Directory.Exists(target) || (File.Exists(target) && !Matches(target, e.OldHash) && !Matches(target, e.NewHash)))
                    throw new IOException("恢复遇到不属于本事务的内容，已保留：" + target + "；备份：" + Work(j));
            }
            // 清单最后恢复，恢复本身中断也能由原日志重复执行。
            for (int i = 0; i < j.Files.Count; i++)
            {
                var e = j.Files[i];
                string target = Target(e);
                if (e.OldHash != null)
                {
                    if (!Matches(target, e.OldHash))
                    {
                        string restore = Path.Combine(Work(j), "restore-" + Guid.NewGuid().ToString("N"));
                        InstallManifest.EnsureNoReparsePoints(Backup(j, i));
                        byte[] original = File.ReadAllBytes(Backup(j, i));
                        if (!string.Equals(Hash(original), e.OldHash, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("恢复备份在读取期间改变。");
                        WriteNew(restore, original);
                        ReplaceChecked(restore, target, e.NewHash);
                    }
                }
                else if (File.Exists(target))
                {
                    // 按锁定句柄校验与移走同一文件；不删除校验后被替换的同名文件。
                    if (e.NewHash == null || !OwnedInstallFile.MoveVerified(target,
                        Path.Combine(Work(j), "rolled-back-" + Guid.NewGuid().ToString("N")), e.NewHash))
                        throw new IOException("回滚期间文件已改变：" + target);
                }
                checkpoint?.Invoke("rollback:" + i);
            }
            j.Phase = "RolledBack";
            Save(j);
        }
        static void ReplaceChecked(string source, string target, string? expected)
        {
            using var guard = new OwnedInstallFile.DirectoryGuard(Path.GetDirectoryName(source)!, Path.GetDirectoryName(target)!);
            string? current = OwnedInstallFile.ReadHash(target);
            if (current != null)
            {
                if (expected == null || !OwnedInstallFile.MoveVerified(target,
                    Path.Combine(Path.GetDirectoryName(source)!, "replaced-" + Guid.NewGuid().ToString("N")), expected))
                    throw new IOException("文件在提交期间改变：" + target);
            }
            // 中断发生在旧文件移走后，新文件落地前，日志仍可从已刷盘备份恢复。
            string sourceHash = InstallManifest.HashFile(source);
            if (!OwnedInstallFile.MoveVerified(source, target, sourceHash)) throw new IOException("暂存文件发生改变。");
        }
        internal InstallManifest Execute(IReadOnlyDictionary<string, byte[]> payload, string version,
            CancellationToken token = default, Action<string>? checkpoint = null)
        {
            Recover();
            token.ThrowIfCancellationRequested();
            var previous = InstallManifest.ValidateInstallDestination(dir);
            var manifest = new InstallManifest { Version = version, CanonicalInstallDir = dir };
            var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in payload)
            {
                _ = InstallManifest.ResolveFile(dir, p.Key);
                data.Add(p.Key, p.Value);
                manifest.Files.Add(new InstallFile { RelativePath = p.Key, Sha256 = Hash(p.Value) });
            }
            manifest.Validate(dir);
            data.Add(InstallManifest.FileName, manifest.Serialize());
            var j = new Journal { Directory = dir, Id = Guid.NewGuid().ToString("N") };
            foreach (var p in data.Where(p => p.Key != InstallManifest.FileName))
                j.Files.Add(new Entry { Name = p.Key, NewHash = Hash(p.Value) });
            // 旧版本受管理但新版本不再分发的文件也须纳入事务，未知文件保持原状。
            if (previous != null)
                foreach (var p in previous.Files.Where(p => !data.ContainsKey(p.RelativePath))) j.Files.Add(new Entry { Name = p.RelativePath });
            j.Files.Add(new Entry { Name = InstallManifest.FileName, NewHash = Hash(data[InstallManifest.FileName]) });
            foreach (var e in j.Files)
            {
                string target = Target(e);
                InstallManifest.EnsureNoReparsePoints(target);
                if (File.Exists(target))
                {
                    if (previous == null) throw new IOException("准备期间出现未登记文件：" + target);
                    e.OldHash = InstallManifest.HashFile(target);
                }
            }
            Directory.CreateDirectory(Work(j));
            Save(j); // 准备日志先于任何备份写入；中途损坏的 staging 不影响目标。
            try
            {
                checkpoint?.Invoke("preparing");
                for (int i = 0; i < j.Files.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var e = j.Files[i];
                    if (e.OldHash != null)
                    {
                        byte[] original = File.ReadAllBytes(Target(e));
                        if (!string.Equals(Hash(original), e.OldHash, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份期间文件改变：" + e.Name);
                        WriteNew(Backup(j, i), original);
                    }
                    if (data.TryGetValue(e.Name, out var bytes)) WriteNew(Stage(j, i), bytes);
                    checkpoint?.Invoke("staged:" + i);
                }
                ValidateBackups(j);
                token.ThrowIfCancellationRequested();
                j.Phase = "Ready";
                Save(j);
                checkpoint?.Invoke("ready");
                Directory.CreateDirectory(dir);
                for (int i = 0; i < j.Files.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    var e = j.Files[i];
                    if (e.NewHash != null)
                    {
                        if (!Matches(Stage(j, i), e.NewHash)) throw new InvalidDataException("暂存内容校验失败。");
                        ReplaceChecked(Stage(j, i), Target(e), e.OldHash);
                    }
                    else if (File.Exists(Target(e)))
                    {
                        if (!Matches(Target(e), e.OldHash)) throw new IOException("旧文件在提交时已改变。");
                        File.Move(Target(e), Path.Combine(Work(j), i + ".removed"));
                    }
                    checkpoint?.Invoke("applied:" + i);
                }
                token.ThrowIfCancellationRequested();
                foreach (var e in j.Files)
                    if (e.NewHash != null ? !Matches(Target(e), e.NewHash) : File.Exists(Target(e))) throw new IOException("提交后文件校验不一致。");
                checkpoint?.Invoke("before-commit");
                token.ThrowIfCancellationRequested();
                j.Phase = "Committed";
                Save(j); // 持久提交点；之后取消不再回滚，登记可通过重试幂等补齐。
                return manifest;
            }
            catch (Exception error)
            {
                // 提交标记写入失败也要以磁盘日志为准，避免内存 Phase 提前改变。
                try { Recover(); }
                catch (Exception rollbackError) { throw new AggregateException("安装未完成且需要恢复；请保留恢复目录：" + state, error, rollbackError); }
                throw;
            }
        }
        public void Dispose() => gate.Dispose();
    }
}
