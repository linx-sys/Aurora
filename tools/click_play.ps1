param([string]$ShotName = "play_state.png")
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class PK {
  [StructLayout(LayoutKind.Sequential)]
  public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
}
"@
$p = Get-Process AuroraPlayer -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
[PK]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 3) | Out-Null
Start-Sleep -Milliseconds 300
$r = New-Object PK+RECT
[PK]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
# 播放按钮：底栏中心（窗口中央 x、底部 y-45）
$cx = [int]($r.L + 590)
$cy = [int]($r.B - 45)
[PK]::SetCursorPos($cx, $cy) | Out-Null
Start-Sleep -Milliseconds 150
[PK]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[PK]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 800
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$ShotName saved (clicked $cx,$cy)"
