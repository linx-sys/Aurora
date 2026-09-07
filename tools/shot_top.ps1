param([string]$ShotName = "shot.png", [int]$SleepMs = 0)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class T {
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
$p = Get-Process AuroraPlayer -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
[T]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 3) | Out-Null
[T]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds (400 + $SleepMs)
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$ShotName saved"
