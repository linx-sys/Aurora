param([string]$ShotName = "toast.png")
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class TC {
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$p = Get-Process AuroraPlayer -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
[TC]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300
$r = New-Object TC+RECT
[TC]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
# BtnMode：底栏控制行最左
$mx = [int]($r.L + ($r.R - $r.L) * 0.031)
$my = [int]($r.T + ($r.B - $r.T) * 0.941)
[TC]::SetCursorPos($mx, $my) | Out-Null
Start-Sleep -Milliseconds 150
[TC]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[TC]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 700
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$ShotName saved (clicked $mx,$my)"
