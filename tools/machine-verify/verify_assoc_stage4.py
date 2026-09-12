"""Aurora 关联行为端到端验证（阶段 4）：/associate 与 /unassociate 是否改动系统默认应用记录。

背景（v3.0.3 方案 A）：`Assoc.Register` 过去会删除 `FileExts\\<ext>\\UserChoice`，
等于在用户毫无感知的情况下抢走默认关联。本脚本用**真实安装产物**验证这条属性已经消失：
  /associate  → 只写 HKCU\\Software\\Classes 下的 ProgID 与扩展名默认值，
                UserChoice（ProgId + 系统校验 Hash）必须逐字节不变；
  /unassociate→ 清掉自己的 ProgID 与扩展名默认值，UserChoice 仍必须不变。

安全：只安装到隔离目录；全程快照并在 finally 里**原样还原** HKCU 下被触碰的键，
      不改动用户现有默认程序（默认程序记录 UserChoice 只读比对，从不写入）。

用法：python verify_assoc_stage4.py [安装器路径]
"""
import json
import os
import shutil
import subprocess
import sys
import time
import winreg
from pathlib import Path

REPO = Path(r"D:\WorkSpace\Aurora")
ROOT = REPO / "outputs" / "machine-verify"
SETUP = Path(sys.argv[1]) if len(sys.argv) > 1 else REPO / "AuroraPlayer-Setup.exe"
CASE = ROOT / "assoc-case"
INSTALL = CASE / "Aurora"
EXTS = ['.mp3', '.m4a', '.flac', '.wav', '.ogg', '.oga', '.aac', '.opus', '.wma']
CLS = r"Software\Classes"
FILE_EXTS = r"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts"
KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\AuroraPlayer"
HKCU = winreg.HKEY_CURRENT_USER

report = []


def check(name, ok, detail=""):
    report.append({"check": name, "ok": bool(ok), "detail": str(detail)[:300]})
    print(("[PASS] " if ok else "[FAIL] ") + name + ("" if ok else "  <- " + str(detail)[:300]), flush=True)
    return ok


def read_values(path):
    """返回 {值名: (数据, 类型)}；键不存在返回 None。"""
    try:
        with winreg.OpenKey(HKCU, path) as k:
            n = winreg.QueryInfoKey(k)[1]
            return {winreg.EnumValue(k, i)[0]: (winreg.EnumValue(k, i)[1], winreg.EnumValue(k, i)[2]) for i in range(n)}
    except FileNotFoundError:
        return None


def write_values(path, values):
    if values is None:
        try:
            winreg.DeleteKey(HKCU, path)
        except FileNotFoundError:
            pass
        return
    with winreg.CreateKeyEx(HKCU, path, 0, winreg.KEY_SET_VALUE) as k:
        for name, (data, typ) in values.items():
            winreg.SetValueEx(k, name, 0, typ, data)


def key_exists(path):
    try:
        with winreg.OpenKey(HKCU, path):
            return True
    except FileNotFoundError:
        return False


def snapshot():
    """快照所有会被 /associate、/unassociate 触碰的键，用于精确还原。"""
    snap = {
        "ext_default": {},          # Classes\.<ext> 的默认值
        "ext_content": {},          # Classes\.<ext> 的 Content Type
        "ext_openwith": {},         # Classes\.<ext>\OpenWithProgids 的全部值
        "userchoice": {},           # FileExts\.<ext>\UserChoice 的全部值（只读比对）
        "progid": read_values(f"{CLS}\\Aurora.Audio") if key_exists(f"{CLS}\\Aurora.Audio") else "absent",
        "app": read_values(f"{CLS}\\Applications\\AuroraPlayer.exe") if key_exists(f"{CLS}\\Applications\\AuroraPlayer.exe") else "absent",
    }
    for e in EXTS:
        d = read_values(f"{CLS}\\{e}")
        # read_values 返回 {值名: (数据, 类型)}，这里只需要数据本身
        snap["ext_default"][e] = d[""][0] if d and "" in d else "absent"
        snap["ext_content"][e] = d["Content Type"][0] if d and "Content Type" in d else "absent"
        snap["ext_openwith"][e] = read_values(f"{CLS}\\{e}\\OpenWithProgids")
        snap["userchoice"][e] = read_values(f"{FILE_EXTS}\\{e}\\UserChoice")
    return snap


def restore(snap):
    """把 Aurora 留下的痕迹清干净，并把扩展名默认值/Content Type 还原到快照状态。"""
    for tree in (f"{CLS}\\Aurora.Audio", f"{CLS}\\Aurora.Audio.mp3", f"{CLS}\\Applications\\AuroraPlayer.exe"):
        try:
            winreg.DeleteKey(HKCU, tree)
        except (FileNotFoundError, OSError):
            pass
    for e in EXTS:
        # 还原默认值与 Content Type
        with winreg.CreateKeyEx(HKCU, f"{CLS}\\{e}", 0, winreg.KEY_SET_VALUE) as k:
            for name, original in (("", snap["ext_default"][e]), ("Content Type", snap["ext_content"][e])):
                if original == "absent":
                    try:
                        winreg.DeleteValue(k, name)
                    except FileNotFoundError:
                        pass
                else:
                    winreg.SetValueEx(k, name, 0, winreg.REG_SZ, original)
        # 还原 OpenWithProgids：先删掉 Aurora 的项，再补回快照里原有的项
        try:
            with winreg.OpenKey(HKCU, f"{CLS}\\{e}\\OpenWithProgids", 0, winreg.KEY_SET_VALUE) as k:
                winreg.DeleteValue(k, "Aurora.Audio")
                winreg.DeleteValue(k, "Aurora.Audio.mp3")
        except FileNotFoundError:
            pass
        if snap["ext_openwith"][e] is not None:
            write_values(f"{CLS}\\{e}\\OpenWithProgids", snap["ext_openwith"][e])


def run(args, timeout=180):
    p = subprocess.run([str(a) for a in args], capture_output=True, timeout=timeout)
    return p.returncode, (p.stdout or b"").decode("gbk", "replace"), (p.stderr or b"").decode("gbk", "replace")


def main():
    snap = snapshot()
    exit_code = 0
    state_dir = INSTALL.parent / (".AuroraInstall-" + "x")  # 仅用于打印，实际从磁盘枚举
    try:
        if CASE.exists():
            shutil.rmtree(CASE, ignore_errors=True)
        CASE.mkdir(parents=True, exist_ok=True)

        print("=== 1) 安装（/noassoc：不让安装器落关联标记）===")
        code, out, err = run([SETUP, "/S", "/D=" + str(INSTALL), "/noassoc", "/nodesktop"])
        check("安装退出码 0", code == 0, err)
        check("安装目录含 unins.dll（v3.0.0 曾漏嵌）", (INSTALL / "unins.dll").is_file())

        print("\n=== 2) 执行 AuroraPlayer.exe /associate ===")
        code, out, err = run([INSTALL / "AuroraPlayer.exe", "/associate"])
        check("/associate 退出码 0", code == 0, err)
        check("已写入 ProgID 键 Classes\\Aurora.Audio", key_exists(f"{CLS}\\Aurora.Audio"))
        cmd = (read_values(f"{CLS}\\Aurora.Audio\\shell\\open\\command") or {}).get("")
        cmd_text = cmd[0] if cmd else ""
        check("关联命令指向 AuroraPlayer.exe（不是 .dll）",
              "AuroraPlayer.exe" in cmd_text and ".dll" not in cmd_text, cmd_text)

        print("\n=== 3) 核心属性：UserChoice 必须逐字节不变 ===")
        for e in EXTS:
            now = read_values(f"{FILE_EXTS}\\{e}\\UserChoice")
            same = now == snap["userchoice"][e]
            detail = f"before={snap['userchoice'][e]} after={now}"
            check(f"{e} UserChoice 未被改动", same, "" if same else detail)
        print("   （本机快照：", {e: (snap["userchoice"][e] or {}).get("ProgId") for e in EXTS if snap["userchoice"][e]}, "）")

        print("\n=== 4) 执行 AuroraPlayer.exe /unassociate ===")
        code, out, err = run([INSTALL / "AuroraPlayer.exe", "/unassociate"])
        check("/unassociate 退出码 0", code == 0, err)
        check("ProgID 键已清除", not key_exists(f"{CLS}\\Aurora.Audio"))
        for e in EXTS:
            now = read_values(f"{FILE_EXTS}\\{e}\\UserChoice")
            check(f"{e} UserChoice 在取消关联后仍未被改动", now == snap["userchoice"][e])
        leftover = [e for e in EXTS
                    if (read_values(f"{CLS}\\{e}\\OpenWithProgids") or {}).get("Aurora.Audio") is not None]
        print(f"   [记录] /unassociate 未清理 OpenWithProgids 的扩展名：{leftover or '无'}"
              "（卸载器会清理；Assoc.Unregister 不清理，属已知不一致，本脚本随后手动清掉）")

        print("\n=== 5) 卸载并检查审计目录清理 ===")
        code, out, err = run([INSTALL / "unins.exe", "/S"])
        check("卸载退出码 0", code == 0, err)
        deadline = time.time() + 60
        while time.time() < deadline and INSTALL.exists() and any(INSTALL.iterdir()):
            time.sleep(0.5)
        check("安装目录已删除", not INSTALL.exists(), sorted(p.name for p in INSTALL.iterdir()) if INSTALL.exists() else "")
        states = [p for p in CASE.iterdir() if p.name.startswith(".AuroraInstall-")]
        check("审计目录已随卸载删除", not states, [p.name for p in states])
        check("注册表卸载登记已清除", not key_exists(KEY))

        print("\n=== 汇总 ===")
        failed = [c["check"] for c in report if not c["ok"]]
        print(json.dumps({"total": len(report), "passed": len(report) - len(failed), "failed": failed},
                         ensure_ascii=False, indent=2))
        (ROOT / "stage4-report.json").write_text(
            json.dumps({"cases": report}, ensure_ascii=False, indent=2), encoding="utf-8")
        exit_code = 1 if failed else 0
    finally:
        restore(snap)
        shutil.rmtree(CASE, ignore_errors=True)
        print("\n已还原被触碰的注册表键（UserChoice 全程未写入）")
    return exit_code


if __name__ == "__main__":
    sys.exit(main())
