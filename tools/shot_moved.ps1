param([string]$ShotName = "shot.png")
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class MV {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
}
"@
[MV]::SetProcessDPIAware() | Out-Null
$p = Get-Process AuroraPlayer -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NOPROC'; exit }
# 移到屏幕左上（保持尺寸），置顶并激活
[MV]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 1) | Out-Null
[MV]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 500
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$w = [Math]::Min($b.Width, 1536); $h = [Math]::Min($b.Height, 1024)
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$ShotName saved ($w x $h)"
