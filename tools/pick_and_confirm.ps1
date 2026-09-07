Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W2 {
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
$dlg = [IntPtr]0x000F07F0
$r = New-Object W2+RECT
[W2]::GetWindowRect($dlg, [ref]$r) | Out-Null
Write-Output ('dlg rect: ' + $r.L + ',' + $r.T)
function Click($x, $y) {
  [W2]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 150
  [W2]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [W2]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
# 树中选中 Program Files
Click ($r.L + 110) ($r.T + 281)
Start-Sleep -Milliseconds 500
# 确定
Click ($r.L + 885) ($r.T + 354)
Start-Sleep -Milliseconds 700
# 读选项页路径 Edit (00320756) 文本
$sb = New-Object System.Text.StringBuilder 512
[W2]::GetWindowText([IntPtr]0x00320756, $sb, 512) | Out-Null
Write-Output ('dirBox now: ' + $sb.ToString())
