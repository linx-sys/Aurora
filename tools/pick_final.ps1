Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W8 {
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
}
"@
# 对话框与向导都置顶，对话框聚焦
[W8]::SetWindowPos([IntPtr]0x0004057C, [IntPtr](-1), 0, 0, 0, 0, 3) | Out-Null
[W8]::SetForegroundWindow([IntPtr]0x0004057C) | Out-Null
Start-Sleep -Milliseconds 300
function Click($x, $y) {
  [W8]::SetCursorPos($x, $y) | Out-Null
  Start-Sleep -Milliseconds 120
  [W8]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
  Start-Sleep -Milliseconds 70
  [W8]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
}
# PS 进程坐标（= 截图虚拟坐标 ÷ 1.25）：
# Program Files 节点：虚拟 (751,513) → PS (601,410)
Click 601 410
Start-Sleep -Milliseconds 500
# 确定：虚拟 (1068,807) → PS (854,646)
Click 854 646
Start-Sleep -Milliseconds 700
$sb = New-Object System.Text.StringBuilder 512
[W8]::GetWindowText([IntPtr]0x00320756, $sb, 512) | Out-Null
Write-Output ('dirBox now: [' + $sb.ToString() + ']')
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
$b = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap $b.Width, $b.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($b.X, $b.Y, 0, 0, $bmp.Size)
$bmp.Save('D:\WorkSpace\MP3Player\s12.png', [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
's12 saved'
