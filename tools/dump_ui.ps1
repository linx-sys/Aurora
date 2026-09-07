param([string]$Target)
$p = Get-Process 'AuroraPlayer-Setup' -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
if (-not $Target) { $Target = '0x' + $p.MainWindowHandle.ToString('X') }
$main = [Convert]::ToInt64(($Target -replace '^0x',''), 16)
Write-Output ("TARGET 0x" + $main.ToString('X'))

$dir = 'D:\WorkSpace\MP3Player\ui_dump'
[System.IO.Directory]::CreateDirectory($dir) | Out-Null

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
public class E {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc f, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static string Dir;
  public static bool Cb(IntPtr h, IntPtr l) {
    var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
    var cn = new StringBuilder(256); GetClassName(h, cn, 256);
    string file = Path.Combine(Dir, h.ToString("X8") + ".txt");
    File.WriteAllText(file, sb.ToString() + "\n" + cn.ToString(), Encoding.UTF8);
    Console.WriteLine(h.ToString("X8") + " vis=" + IsWindowVisible(h) + " textlen=" + sb.Length + " class=" + cn.ToString());
    return true;
  }
  public static void Dump(IntPtr parent) { EnumChildWindows(parent, Cb, IntPtr.Zero); }
}
"@

[E]::Dir = $dir
[E]::Dump($main)
