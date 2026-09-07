param([string]$DlgHwnd)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W4 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
if (-not $DlgHwnd) { Write-Output 'NO DLG HWND'; exit }
$dlg = [IntPtr][Convert]::ToInt64(($DlgHwnd -replace '^0x',''), 16)
function Click($x, $y) {
  [W4]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 150
  [W4]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W4]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
# 树中 Program Files（虚拟坐标）
Click 618 349
Start-Sleep -Milliseconds 600
# 确定（虚拟坐标）
Click 817 532
Start-Sleep -Milliseconds 700
$sb = New-Object System.Text.StringBuilder 512
[W4]::GetWindowText([IntPtr]0x00320756, $sb, 512) | Out-Null
Write-Output ('dirBox now: ' + $sb.ToString())
