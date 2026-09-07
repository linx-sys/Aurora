Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class WR {
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
}
"@
$p = Get-Process AuroraPlayer -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
$r = New-Object WR+RECT
[WR]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
Write-Output ('rect: ' + $r.L + ',' + $r.T + ' ' + ($r.R - $r.L) + 'x' + ($r.B - $r.T) + ' pid=' + $p.Id)

# 封面中心点击（窗口内 比例 0.36 宽, 0.28 高）
function Click($x, $y) {
  [WR]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 150
  [WR]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 80
  [WR]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
$cx = $r.L + [int](($r.R - $r.L) * 0.28)
$cy = $r.T + [int](($r.B - $r.T) * 0.30)
[WR]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
Write-Output ("click cover at " + $cx + "," + $cy)
Click $cx $cy
Start-Sleep -Milliseconds 900
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\WorkSpace\MP3Player\s15.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
's15 saved'
