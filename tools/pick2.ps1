param([string]$DlgHwnd)
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W3 {
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
if (-not $DlgHwnd) { Write-Output 'NO DLG HWND'; exit }
$dlg = [IntPtr][Convert]::ToInt64(($DlgHwnd -replace '^0x',''), 16)
$r = New-Object W3+RECT
[W3]::GetWindowRect($dlg, [ref]$r) | Out-Null
Write-Output ('dlg rect (virtual): ' + $r.L + ',' + $r.T + ' ' + ($r.R - $r.L) + 'x' + ($r.B - $r.T))
function Click($x, $y) {
  [W3]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 150
  [W3]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W3]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
# 树中 Program Files（相对对话框 虚拟约 88,225）
Click ($r.L + 88) ($r.T + 225)
Start-Sleep -Milliseconds 500
# 确定（相对约 708,283）
Click ($r.L + 708) ($r.T + 283)
Start-Sleep -Milliseconds 700
$sb = New-Object System.Text.StringBuilder 512
[W3]::GetWindowText([IntPtr]0x00320756, $sb, 512) | Out-Null
Write-Output ('dirBox now: ' + $sb.ToString())
