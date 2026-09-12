"""Aurora 真机安装/卸载验证（阶段 1：全新安装、拒绝路径、卸载保留用户文件）

安全边界：
- 只在 outputs/machine-verify 下安装，绝不触碰 D:\\Applications\\Aurora（用户既有安装）。
- 静默安装带 /noassoc /nodesktop：不写 .associate 标记、不创建桌面快捷方式，
  因此不会改动用户的文件关联。
- 测试前备份 HKCU 卸载注册表项，脚本结束（含异常）后原样恢复。
"""
import json, os, re, shutil, subprocess, sys, time, hashlib
from pathlib import Path

REPO = Path(r"D:\WorkSpace\Aurora")
ROOT = REPO / "outputs" / "machine-verify"
SETUP = REPO / "AuroraPlayer-Setup.exe"
REG_KEY = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer"
REG_BACKUP = ROOT / "registry-backup-uninstall.reg"
MANIFEST_NAME = "aurora-install-manifest.json"

PAYLOAD = [
    "AuroraPlayer.exe", "AuroraPlayer.dll", "AuroraPlayer.runtimeconfig.json", "AuroraPlayer.deps.json",
    "unins.exe", "unins.dll", "unins.runtimeconfig.json", "unins.deps.json",
    "NVorbis.dll", "Concentus.dll", "Concentus.Oggfile.dll", "NAudio.dll", "NAudio.Core.dll",
    "NAudio.WinMM.dll", "NAudio.Wasapi.dll", "Microsoft.Windows.SDK.NET.dll", "WinRT.Runtime.dll",
    "Microsoft.Data.Sqlite.dll", "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll",
    "SQLitePCLRaw.batteries_v2.dll", "e_sqlite3.dll",
]

report = {"cases": []}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest().upper()


def dec(raw):
    """安装器向控制台输出中文（GBK 代码页），优先按 GBK 解码，失败再退回 UTF-8。"""
    for enc in ("gbk", "utf-8"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("utf-8", "replace")


def run(args, timeout=180):
    p = subprocess.run(args, capture_output=True, timeout=timeout)
    return p.returncode, dec(p.stdout or b""), dec(p.stderr or b"")


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


def check(name, ok, detail=""):
    report["cases"].append({"check": name, "ok": bool(ok), "detail": str(detail)[:400]})
    print(("[PASS] " if ok else "[FAIL] ") + name + ("" if ok else "  <- " + str(detail)[:400]), flush=True)
    return ok


def install(dirpath, setup=None, extra=()):
    args = [str(setup or SETUP), "/S", "/D=" + str(dirpath), "/noassoc", "/nodesktop", *extra]
    return run(args)


def inspect_install(dirpath, label):
    ok = True
    missing = [f for f in PAYLOAD if not (dirpath / f).is_file() or (dirpath / f).stat().st_size == 0]
    ok &= check(f"{label}: 22 个载荷文件齐全非空", not missing, missing)
    mpath = dirpath / MANIFEST_NAME
    ok &= check(f"{label}: 安装清单存在", mpath.is_file())
    if mpath.is_file():
        m = json.loads(mpath.read_text(encoding="utf-8"))
        ok &= check(f"{label}: 清单 schema/product/目录正确",
                    m.get("schema") == 1 and m.get("product") == "AuroraPlayer"
                    and os.path.normcase(m.get("canonicalInstallDir", "")) == os.path.normcase(str(dirpath)),
                    m.get("canonicalInstallDir"))
        bad = [f["relativePath"] for f in m["files"] if sha256(dirpath / f["relativePath"]) != f["sha256"]]
        ok &= check(f"{label}: 清单内每个文件哈希一致", not bad, bad)
        ok &= check(f"{label}: 清单文件数与载荷一致", len(m["files"]) == len(PAYLOAD), len(m["files"]))
        report["installed_version"] = m.get("version")
        ok &= check(f"{label}: 清单 sha256 与注册表一致",
                    (reg_query() or {}).get("ManifestSha256") == sha256(mpath))
    reg = reg_query()
    ok &= check(f"{label}: 注册表 InstallLocation 指向本目录",
                bool(reg) and os.path.normcase(reg.get("InstallLocation", "")) == os.path.normcase(str(dirpath)), reg)
    ok &= check(f"{label}: 注册表 UninstallString 指向 unins.exe",
                bool(reg) and "unins.exe" in reg.get("UninstallString", ""))
    ok &= check(f"{label}: /noassoc 未写 .associate 标记", not (dirpath / ".associate").exists())
    ok &= check(f"{label}: 未写桌面快捷方式记录", not (reg or {}).get("DesktopShortcutSha256"))
    return ok


def backup_registry():
    if REG_BACKUP.exists():
        REG_BACKUP.unlink()
    code, _, err = run(["reg", "export", REG_KEY, str(REG_BACKUP), "/y"])
    if code == 0:
        print(f"已备份既有卸载注册表项 -> {REG_BACKUP}")
        return True
    print(f"既有卸载注册表项不存在或无法导出（code={code}）: {err.strip()[:120]}")
    return False


def restore_registry(had_backup):
    run(["reg", "delete", REG_KEY, "/f"])
    if had_backup:
        code, _, err = run(["reg", "import", str(REG_BACKUP)])
        print(f"注册表恢复: code={code} {err.strip()[:120]}")
        print("恢复后:", reg_query())


def main():
    had_backup = backup_registry()
    fresh = ROOT / "fresh" / "Aurora"
    if fresh.parent.exists():
        shutil.rmtree(fresh.parent, ignore_errors=True)
    fresh.parent.mkdir(parents=True, exist_ok=True)

    print("\n=== A. 全新静默安装 ===")
    code, out, err = install(fresh)
    check("A: 安装器退出码 0", code == 0, f"code={code} out={out[-300:]} err={err[-300:]}")
    inspect_install(fresh, "A")
    # 事务提交后刻意保留同级 .AuroraInstall-<sha256> 审计目录（含恢复日志与备份），
    # 这里只断言该目录里没有残留安装目录自身，且日志已进入 Committed 终态。
    state = [p for p in fresh.parent.iterdir() if p.name.startswith(".AuroraInstall-")]
    phases = []
    for s in state:
        j = s / "active.json"
        if j.is_file():
            data = json.loads(j.read_text(encoding="utf-8"))
            phases.append(data.get("Phase") or data.get("phase"))
        else:
            phases.append("missing")
    check("A: 事务日志进入 Committed 终态（审计目录按设计保留）",
          phases == ["Committed"], {"state": [s.name for s in state], "phase": phases})
    check("A: 安装目录自身无遗留暂存文件",
          not any(p.name.startswith(".") for p in fresh.iterdir()),
          [p.name for p in fresh.iterdir() if p.name.startswith(".")])

    print("\n=== B. 覆盖安装（对既有清单安装重装）===")
    (fresh / "music.mp3").write_text("user music", encoding="utf-8")
    code, out, err = install(fresh)
    check("B: 覆盖安装退出码 0", code == 0, f"code={code} err={err[-300:]}")
    check("B: 用户文件在覆盖后仍存在", (fresh / "music.mp3").read_text(encoding="utf-8") == "user music")
    inspect_install(fresh, "B")

    print("\n=== C. 无清单的非空目录必须拒绝（旧版安装目录升级场景）===")
    legacy = ROOT / "legacy" / "Aurora"
    legacy.mkdir(parents=True, exist_ok=True)
    (legacy / "AuroraPlayer.exe").write_text("old build", encoding="utf-8")
    (legacy / "keep.txt").write_text("old user file", encoding="utf-8")
    code, out, err = install(legacy)
    check("C: 安装器拒绝并返回非 0", code != 0, f"code={code}")
    check("C: 给出明确原因（要求选择新的专用空目录）",
          "没有有效安装清单" in err or "空目录" in err, err.strip()[:300])
    check("C: 旧目录内容未被改动",
          (legacy / "AuroraPlayer.exe").read_text(encoding="utf-8") == "old build"
          and (legacy / "keep.txt").read_text(encoding="utf-8") == "old user file")

    print("\n=== D. 已登记文件被改动后必须拒绝覆盖 ===")
    (fresh / "AuroraPlayer.dll").write_text("tampered", encoding="utf-8")
    code, out, err = install(fresh)
    check("D: 安装器拒绝并返回非 0", code != 0, f"code={code}")
    check("D: 指出未登记/已修改文件", "未登记" in err or "已修改" in err, err.strip()[:300])
    check("D: 被改动的文件保持原样", (fresh / "AuroraPlayer.dll").read_text(encoding="utf-8") == "tampered")

    print("\n=== E. 卸载（只删清单内文件，保留用户文件）===")
    # 被改动文件的恢复路径：删除该文件后重装（清单允许缺失文件补齐，不允许覆盖已修改文件）
    (fresh / "AuroraPlayer.dll").unlink()
    code, out, err = install(fresh)
    check("E-前置: 删除被改动文件后可重装修复", code == 0, f"code={code} err={err[-200:]}")
    inspect_install(fresh, "E-前置")
    (fresh / "music.mp3").write_text("user music", encoding="utf-8")
    (fresh / "user").mkdir(exist_ok=True)
    (fresh / "user" / "notes.txt").write_text("user notes", encoding="utf-8")
    code, out, err = run([str(fresh / "unins.exe"), "/S"], timeout=180)
    check("E: 卸载器退出码 0", code == 0, f"code={code} err={err.strip()[:300]}")
    deadline = time.time() + 60
    while time.time() < deadline and (fresh / "AuroraPlayer.exe").exists():
        time.sleep(0.5)
    remaining = sorted(p.name for p in fresh.iterdir()) if fresh.exists() else []
    check("E: 清单登记的程序文件已删除", not (fresh / "AuroraPlayer.exe").exists() and not (fresh / MANIFEST_NAME).exists(), remaining)
    check("E: 用户文件保留", (fresh / "music.mp3").exists() and (fresh / "user" / "notes.txt").exists(), remaining)
    check("E: 安装目录未被递归删除（因含用户文件）", fresh.exists(), remaining)
    check("E: 注册表卸载项已清除", reg_query() is None, reg_query())

    report["summary"] = {
        "total": len(report["cases"]),
        "passed": sum(1 for c in report["cases"] if c["ok"]),
        "failed": [c["check"] for c in report["cases"] if not c["ok"]],
    }
    out_path = ROOT / "stage1-report.json"
    out_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("\n=== 汇总 ===")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    print("报告:", out_path)
    restore_registry(had_backup)
    return 1 if report["summary"]["failed"] else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception:
        import traceback
        traceback.print_exc()
        had = REG_BACKUP.exists()
        restore_registry(had)
        sys.exit(2)
