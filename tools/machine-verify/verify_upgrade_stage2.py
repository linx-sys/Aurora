"""Aurora 真机升级验证（阶段 2）：构造 3.0.1 产物，覆盖 3.0.0 安装。

安全边界同阶段 1：
- 只在 outputs/machine-verify 下操作，不触碰 D:\\Applications\\Aurora。
- 临时把 AppInfo.Version 改为 3.0.1 构建升级包，finally 里必定还原源码并重建，
  保证结束时不留下任何源码改动。
- 静默安装带 /noassoc /nodesktop，不改动用户文件关联。
"""
import hashlib, json, os, re, shutil, subprocess, sys, time
from pathlib import Path

REPO = Path(r"D:\WorkSpace\Aurora")
ROOT = REPO / "outputs" / "machine-verify"
ART = ROOT / "artifacts"
SETUP = REPO / "AuroraPlayer-Setup.exe"
SDK = r"C:\Users\Jinwei\AppData\Local\Microsoft\dotnet\dotnet.exe"
APPINFO = REPO / "src" / "AppInfo.cs"
REG_KEY = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer"
REG_BACKUP = ROOT / "registry-backup-uninstall.reg"
MANIFEST_NAME = "aurora-install-manifest.json"

report = {"cases": []}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def dec(raw):
    for enc in ("gbk", "utf-8"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", "replace")


def run(args, timeout=600):
    p = subprocess.run([str(a) for a in args], capture_output=True, timeout=timeout)
    return p.returncode, dec(p.stdout or b""), dec(p.stderr or b"")


def check(name, ok, detail=""):
    report["cases"].append({"check": name, "ok": bool(ok), "detail": str(detail)[:400]})
    print(("[PASS] " if ok else "[FAIL] ") + name + ("" if ok else "  <- " + str(detail)[:400]), flush=True)
    return ok


def reg_query():
    code, out, _ = run(["reg", "query", REG_KEY])
    if code != 0:
        return None
    values = {}
    for line in out.splitlines()[1:]:
        parts = re.split(r"\s{4,}", line.strip(), maxsplit=2)
        if len(parts) == 3:
            values[parts[0]] = parts[2]
    return values


def pick(data, *names):
    """安装清单是 camelCase、事务日志是 PascalCase，统一容错读取。"""
    for n in names:
        if n in data:
            return data[n]
    return None


def read_manifest(dirpath):
    return json.loads((dirpath / MANIFEST_NAME).read_text(encoding="utf-8"))


def install(dirpath, setup):
    return run([setup, "/S", "/D=" + str(dirpath), "/noassoc", "/nodesktop"])


def build_variant(version, target_exe):
    """临时改版本号 → 重建主程序 → 发布安装器 → 还原版本号并重建。"""
    original = APPINFO.read_text(encoding="utf-8")
    try:
        patched = re.sub(r'public const string Version = "[^"]*";',
                         f'public const string Version = "{version}";', original, count=1)
        assert patched != original, "版本号替换失败"
        APPINFO.write_text(patched, encoding="utf-8")
        code, out, err = run([SDK, "build", str(REPO / "AuroraPlayer.csproj"), "-c", "Release", "-v", "minimal", "--nologo"])
        assert code == 0, f"主程序构建失败: {err[-400:]}"
        code, out, err = run([SDK, "publish", str(REPO / "installer.csproj"), "-c", "Release", "-r", "win-x64",
                              "--self-contained", "false", "-v", "minimal", "--nologo"])
        assert code == 0, f"安装器发布失败: {err[-400:]}"
        shutil.copy2(REPO / "win-x64" / "publish" / "AuroraPlayer-Setup.exe", target_exe)
        return True
    finally:
        APPINFO.write_text(original, encoding="utf-8")
        run([SDK, "build", str(REPO / "AuroraPlayer.csproj"), "-c", "Release", "-v", "minimal", "--nologo"])


def main():
    ART.mkdir(parents=True, exist_ok=True)
    v0 = ART / "setup-3.0.0.exe"
    v1 = ART / "setup-3.0.1.exe"

    print("=== 准备产物 ===")
    shutil.copy2(SETUP, v0)
    print("3.0.0 安装器:", v0.stat().st_size, "bytes")
    ok = build_variant("3.0.1", v1)
    check("准备: 3.0.1 升级包构建成功", ok and v1.is_file(), f"{v1.stat().st_size if v1.is_file() else '-'} bytes")
    check("准备: 源码版本号已还原", 'Version = "3.0.0"' in APPINFO.read_text(encoding="utf-8"))

    target = ROOT / "upgrade" / "Aurora"
    if target.parent.exists():
        shutil.rmtree(target.parent, ignore_errors=True)
    target.parent.mkdir(parents=True, exist_ok=True)

    print("\n=== 安装 3.0.0 ===")
    code, out, err = install(target, v0)
    check("升级前: 3.0.0 安装成功", code == 0, f"code={code} err={err[-200:]}")
    m = read_manifest(target)
    check("升级前: 清单版本 3.0.0", pick(m, "version", "Version") == "3.0.0", pick(m, "version", "Version"))
    check("升级前: 注册表 DisplayVersion 3.0.0", (reg_query() or {}).get("DisplayVersion") == "3.0.0", reg_query())
    (target / "music.mp3").write_text("user music", encoding="utf-8")
    (target / "user").mkdir(exist_ok=True)
    (target / "user" / "notes.txt").write_text("user notes", encoding="utf-8")
    before = {pick(f, "relativePath", "RelativePath"): pick(f, "sha256", "Sha256") for f in pick(m, "files", "Files")}

    print("\n=== 覆盖安装 3.0.1（升级）===")
    code, out, err = install(target, v1)
    check("升级: 安装器退出码 0", code == 0, f"code={code} err={err[-300:]}")
    m2 = read_manifest(target)
    check("升级: 清单版本已变为 3.0.1", pick(m2, "version", "Version") == "3.0.1", pick(m2, "version", "Version"))
    check("升级: 注册表 DisplayVersion 已变为 3.0.1", (reg_query() or {}).get("DisplayVersion") == "3.0.1", reg_query())
    changed = [pick(f, "relativePath", "RelativePath") for f in pick(m2, "files", "Files") if before.get(pick(f, "relativePath", "RelativePath")) != pick(f, "sha256", "Sha256")]
    # apphost 桩 AuroraPlayer.exe 不随版本变化，版本号体现在主 DLL 与 runtimeconfig 上
    check("升级: 主程序文件确实被替换（哈希变化）", "AuroraPlayer.dll" in changed and "AuroraPlayer.runtimeconfig.json" in changed, changed)
    bad = [pick(f, "relativePath", "RelativePath") for f in pick(m2, "files", "Files") if sha256(target / pick(f, "relativePath", "RelativePath")) != pick(f, "sha256", "Sha256")]
    check("升级: 升级后每个文件哈希与清单一致", not bad, bad)
    check("升级: 注册表 ManifestSha256 已同步", (reg_query() or {}).get("ManifestSha256") == sha256(target / MANIFEST_NAME))
    check("升级: 用户文件保留", (target / "music.mp3").exists() and (target / "user" / "notes.txt").exists())
    state = [p for p in target.parent.iterdir() if p.name.startswith(".AuroraInstall-")]
    backups = []
    for s in state:
        for sub in s.iterdir():
            if sub.is_dir():
                backups += [f.name for f in sub.iterdir() if f.suffix == ".old"]
    check("升级: 旧文件已留审计备份（.old）", any(n.endswith(".old") for n in backups), sorted(set(backups))[:6])
    phases = []
    for s in state:
        j = s / "active.json"
        if j.is_file():
            d = json.loads(j.read_text(encoding="utf-8"))
            phases.append(d.get("Phase") or d.get("phase"))
    check("升级: 事务日志处于 Committed 终态", phases == ["Committed"], phases)

    print("\n=== 升级后卸载 ===")
    code, out, err = run([target / "unins.exe", "/S"])
    check("升级后: 卸载器退出码 0", code == 0, f"code={code} err={err[-300:]}")
    deadline = time.time() + 60
    while time.time() < deadline and (target / "AuroraPlayer.exe").exists():
        time.sleep(0.5)
    check("升级后: 程序文件与清单已清除",
          not (target / "AuroraPlayer.exe").exists() and not (target / MANIFEST_NAME).exists())
    check("升级后: 用户文件保留", (target / "music.mp3").exists())
    check("升级后: 注册表卸载项已清除", reg_query() is None, reg_query())

    report["summary"] = {"total": len(report["cases"]), "passed": sum(1 for c in report["cases"] if c["ok"]),
                         "failed": [c["check"] for c in report["cases"] if not c["ok"]]}
    (ROOT / "stage2-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("\n=== 汇总 ===")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    # 还原用户原有卸载登记
    run(["reg", "delete", REG_KEY, "/f"])
    if REG_BACKUP.exists():
        run(["reg", "import", str(REG_BACKUP)])
        print("已还原原有卸载登记:", reg_query())
    return 1 if report["summary"]["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
