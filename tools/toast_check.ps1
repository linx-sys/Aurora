param([string]$ShotName = "toast2.png")
Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class TM {
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
[TM]::SetWindowPos($p.MainWindowHandle, [IntPtr](-1), 0, 0, 0, 0, 3) | Out-Null
[TM]::SetForegroundWindow($p.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 400
$r = New-Object TM+RECT
[TM]::GetWindowRect($p.MainWindowHandle, [ref]$r) | Out-Null
$mx = [int]($r.L + ($r.R - $r.L) * 0.031)
$my = [int]($r.T + ($r.B - $r.T) * 0.941)
[TM]::SetCursorPos($mx, $my) | Out-Null
Start-Sleep -Milliseconds 150
[TM]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 80
[TM]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
Start-Sleep -Milliseconds 500
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save("D:\WorkSpace\MP3Player\$ShotName", [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "$ShotName saved"
