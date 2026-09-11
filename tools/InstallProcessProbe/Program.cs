using Aurora;
using System.Text;

// 此探针链接与安装器相同的事务代码；只允许项目输出目录用于自动故障验证。
if (args.Length < 2) return 2;
string root = Path.GetFullPath(args[1]);
// 调用方必须显式提供项目 outputs 作为边界，禁止针对真实安装目录注入终止。
string projectOutputs = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "outputs")).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
if (!root.StartsWith(projectOutputs, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("探针仅允许当前项目 outputs 下的新测试目录。");
using var tx = new InstallTransaction(root);
if (args[0] == "recover") { Console.WriteLine(tx.Recover()); return 0; }
string label = args.Length > 2 ? args[2] : "v1";
string? killStage = args.Length > 3 ? args[3] : null;
var payload = new Dictionary<string, byte[]> { ["AuroraPlayer.exe"] = Encoding.UTF8.GetBytes(label + " app"), ["unins.dll"] = Encoding.UTF8.GetBytes(label + " uninstall") };
if (label == "real")
{
    payload.Clear();
    foreach (string name in InstallManifest.PayloadFiles)
        payload[name] = File.ReadAllBytes(Path.Combine(Environment.CurrentDirectory, "build", name));
}
tx.Execute(payload, label == "v1" ? "1.0.0" : "2.0.0", checkpoint: stage =>
{
    if (stage == killStage)
    {
        // os 级进程终止，不抛异常，不运行 Dispose/catch，真正验证持久日志恢复。
        System.Diagnostics.Process.GetCurrentProcess().Kill();
        Thread.Sleep(Timeout.Infinite);
    }
});
Console.WriteLine("Committed");
return 0;
