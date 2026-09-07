param([string]$Hwnd, [string]$Action)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class C {
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
}
"@
# BM_CLICK = 0x00F5（click 同步 / postclick 异步）；WM_CLOSE = 0x0010
$h = [IntPtr][Convert]::ToInt64(($Hwnd -replace '^0x',''), 16)
switch ($Action) {
  'click' { [C]::SendMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Write-Output "clicked $h" }
  'postclick' { [C]::PostMessage($h, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Write-Output "posted $h" }
  'close' { [C]::PostMessage($h, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Write-Output "closed $h" }
}
