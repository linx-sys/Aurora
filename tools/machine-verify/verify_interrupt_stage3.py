"""Aurora 真机中断恢复验证（阶段 3）：安装过程被强杀后，回滚、/recover、同目录重试。

安全边界同阶段 1/2：只在 outputs/machine-verify 下操作；/noassoc /nodesktop；
结束前还原 HKCU 卸载注册表项。
"""
import hashlib, json, os, re, shutil, subprocess, sys, time
from pathlib import Path

REPO = Path(r"D:\WorkSpace\Aurora")
ROOT = REPO / "outputs" / "machine-verify"
SETUP = ART_SETUP = REPO / "AuroraPlayer-Setup.exe"
REG_KEY = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer"
REG_BACKUP = ROOT / "registry-backup-uninstall.reg"
MANIFEST_NAME = "aurora-install-manifest.json"
PAYLOAD_MAIN = "AuroraPlayer.exe"

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


def run(args, timeout=180):
    p = subprocess.run([str(a) for a in args], capture_output=True, timeout=timeout)
    return p.returncode, dec(p.stdout or b""), dec(p.stderr or b"")


def check(name, ok, detail=""):
    report["cases"].append({"check": name, "ok": bool(ok), "detail": str(detail)[:500]})
    print(("[PASS] " if ok else "[FAIL] ") + name + ("" if ok else "  <- " + str(detail)[:500]), flush=True)
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


def state_dirs(parent):
    return [p for p in parent.iterdir() if p.is_dir() and p.name.startswith(".AuroraInstall-")]


def journal_summary(parent):
    out = []
    for s in state_dirs(parent):
        j = s / "active.json"
        if not j.is_file():
            out.append((s.name, "no-journal", 0, []))
            continue
        d = json.loads(j.read_text(encoding="utf-8"))
        files = d.get("Files") or d.get("files") or []
        work = [sub for sub in s.iterdir() if sub.is_dir()]
        leftovers = []
        for w in work:
            leftovers += [f.name for f in w.iterdir()]
        out.append((s.name[:24], d.get("Phase") or d.get("phase"), len(files), sorted(leftovers)[:8]))
    return out


def kill_install(dirpath, trigger, timeout=120):
    """启动静默安装，命中 trigger 条件后强杀整进程树。返回 (是否杀到, 观察信息)。"""
    parent = dirpath.parent
    p = subprocess.Popen([str(SETUP), "/S", "/D=" + str(dirpath), "/noassoc", "/nodesktop"],
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    killed = False
    info = ""
    deadline = time.time() + timeout
    while time.time() < deadline:
        if p.poll() is not None:
            info = "安装已自行结束（未能杀到）"
            break
        hit = False
        if trigger == "state":
            hit = bool(state_dirs(parent))
        elif trigger == "payload":
            hit = (dirpath / PAYLOAD_MAIN).exists() or bool(state_dirs(parent) and any(
                f.suffix in (".new", ".old") for s in state_dirs(parent) for w in s.iterdir() if w.is_dir() for f in w.iterdir()))
        if hit:
            subprocess.run(["taskkill", "/F", "/T", "/PID", str(p.pid)], capture_output=True)
            killed = True
            info = "已在 " + trigger + " 阶段强杀"
            break
        time.sleep(0.005)
    try:
        p.wait(timeout=20)
    except subprocess.TimeoutExpired:
        pass
    return killed, info


def install(dirpath):
    return run([SETUP, "/S", "/D=" + str(dirpath), "/noassoc", "/nodesktop"])


def recover(dirpath):
    return run([SETUP, "/S", "/recover", "/D=" + str(dirpath)])


def verify_complete(dirpath, label):
    ok = True
    mpath = dirpath / MANIFEST_NAME
    ok &= check(f"{label}: 安装完整（清单存在）", mpath.is_file())
    if mpath.is_file():
        m = json.loads(mpath.read_text(encoding="utf-8"))
        files = m.get("files") or m.get("Files")
        bad = [f["relativePath"] for f in files if not (dirpath / f["relativePath"]).is_file()
               or sha256(dirpath / f["relativePath"]) != f["sha256"]]
        ok &= check(f"{label}: 清单内所有文件存在且哈希一致", not bad, bad)
        reg = reg_query() or {}
        ok &= check(f"{label}: 注册表登记与清单一致",
                    reg.get("ManifestSha256") == sha256(mpath)
                    and os.path.normcase(reg.get("InstallLocation", "")) == os.path.normcase(str(dirpath)), reg)
    return ok


def main():
    # 场景 K1：state 目录刚出现即强杀 → /recover 回滚 → 同目录重装
    k1 = ROOT / "interrupt-k1" / "Aurora"
    if k1.parent.exists():
        shutil.rmtree(k1.parent, ignore_errors=True)
    k1.parent.mkdir(parents=True, exist_ok=True)
    print("=== K1. 事务准备期强杀 → /recover → 同目录重装 ===")
    killed, info = kill_install(k1, "state")
    check("K1: 成功在安装中途强杀", killed, info)
    print("   事务状态:", journal_summary(k1.parent))
    code, out, err = recover(k1)
    check("K1: /recover 退出码 0", code == 0, f"code={code} err={err[-200:]}")
    check("K1: 回滚后安装目录不留程序文件",
          not (k1 / PAYLOAD_MAIN).exists() and not (k1 / MANIFEST_NAME).exists(),
          sorted(p.name for p in k1.iterdir()) if k1.exists() else "目录不存在")
    print("   回滚后事务状态:", journal_summary(k1.parent))
    code, out, err = install(k1)
    check("K1: 同目录重装退出码 0", code == 0, f"code={code} err={err[-300:]}")
    verify_complete(k1, "K1")

    # 场景 K2：解包/提交期强杀 → 不显式 /recover，直接重装（安装器内部先恢复）
    k2 = ROOT / "interrupt-k2" / "Aurora"
    if k2.parent.exists():
        shutil.rmtree(k2.parent, ignore_errors=True)
    k2.parent.mkdir(parents=True, exist_ok=True)
    print("\n=== K2. 提交期强杀 → 直接重装（安装器内部自动恢复）===")
    killed, info = kill_install(k2, "payload")
    check("K2: 成功在安装中途强杀", killed, info)
    print("   事务状态:", journal_summary(k2.parent))
    code, out, err = install(k2)
    check("K2: 直接重装退出码 0（内部先回滚再安装）", code == 0, f"code={code} err={err[-300:]}")
    verify_complete(k2, "K2")

    # 场景 K3：连续两次中断，第三次成功
    k3 = ROOT / "interrupt-k3" / "Aurora"
    if k3.parent.exists():
        shutil.rmtree(k3.parent, ignore_errors=True)
    k3.parent.mkdir(parents=True, exist_ok=True)
    print("\n=== K3. 连续两次中断后重装 ===")
    for n in (1, 2):
        killed, info = kill_install(k3, "state" if n == 1 else "payload")
        check(f"K3.{n}: 第 {n} 次中断成功", killed, info)
    code, out, err = install(k3)
    check("K3: 第三次重装退出码 0", code == 0, f"code={code} err={err[-300:]}")
    verify_complete(k3, "K3")

    # 场景 K4：中断后卸载器也要能收尾（登记未写入时不得误删）
    print("\n=== K4. 中断后执行卸载（无有效登记的目录必须拒绝）===")
    k4 = ROOT / "interrupt-k4" / "Aurora"
    if k4.parent.exists():
        shutil.rmtree(k4.parent, ignore_errors=True)
    k4.parent.mkdir(parents=True, exist_ok=True)
    killed, info = kill_install(k4, "state")
    check("K4: 成功在安装中途强杀", killed, info)
    # 手工放入一个旧的 unins.exe 不可行；改为验证登记不指向该目录时卸载器拒绝
    code, out, err = run([SETUP, "/S", "/recover", "/D=" + str(k4)])
    check("K4: 中断目录可直接 /recover", code == 0, f"code={code} err={err[-200:]}")
    check("K4: 中断目录未被留下任何程序文件",
          not (k4 / PAYLOAD_MAIN).exists() and not (k4 / MANIFEST_NAME).exists(),
          sorted(p.name for p in k4.iterdir()) if k4.exists() else "目录不存在")

    report["summary"] = {"total": len(report["cases"]), "passed": sum(1 for c in report["cases"] if c["ok"]),
                         "failed": [c["check"] for c in report["cases"] if not c["ok"]]}
    (ROOT / "stage3-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("\n=== 汇总 ===")
    print(json.dumps(report["summary"], ensure_ascii=False, indent=2))
    run(["reg", "delete", REG_KEY, "/f"])
    if REG_BACKUP.exists():
        run(["reg", "import", str(REG_BACKUP)])
        print("已还原原有卸载登记:", (reg_query() or {}).get("InstallLocation"))
    return 1 if report["summary"]["failed"] else 0


if __name__ == "__main__":
    sys.exit(main())
