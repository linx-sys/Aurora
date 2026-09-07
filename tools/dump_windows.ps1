Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class W {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public static uint WantPid;
  public static bool Cb(IntPtr h, IntPtr l) {
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid != WantPid) return true;
    if (!IsWindowVisible(h)) return true;
    var sb = new StringBuilder(256); GetWindowText(h, sb, 256);
    var cn = new StringBuilder(256); GetClassName(h, cn, 256);
    Console.WriteLine(h.ToString("X8") + " class=" + cn + " title=[" + sb + "]");
    return true;
  }
  public static void Dump(uint pid) { WantPid = pid; EnumWindows(Cb, IntPtr.Zero); }
}
"@
$p = Get-Process 'AuroraPlayer-Setup' -EA SilentlyContinue
if (-not $p) { Write-Output 'NOPROC'; exit }
[W]::Dump([uint32]$p.Id)
