#nullable disable // Nullable 迁移过渡（阶段 1 批次 2）：UI 层控件/WinRT/注册表互操作字段较多，待后续批次清理
/* ============================================================
 * WindowChromeController.cs — 无边框窗口的 Win32 行为
 * 从 MainWindow.cs 拆出（原属"窗口生命周期/Win32 interop"职责）。
 * 1. DWM 圆角（Windows 11）
 * 2. 最大化不盖任务栏：WM_GETMINMAXINFO → 限制为工作区（rcWork）
 *    WindowStyle=None 的 WPF 窗口最大化默认用整屏 rcMonitor，会盖住任务栏
 * 3. 无边框窗口的最小跟踪尺寸（WPF MinWidth/MinHeight 在 Win32 拖拽
 *    调整大小时不生效，必须按 DPI 缩放换算后在 MMIC 里设置）
 * ============================================================ */
using System;
using System.Runtime.InteropServices;
using System.Windows;

namespace Aurora
{
    public static class WindowChromeController
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        /// <summary>启用 Windows 11 风格圆角（DWMWCP_ROUND；旧系统静默失败）。</summary>
        public static void EnableRoundedCorners(Window win)
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                int pref = 2; // DWMWCP_ROUND
                DwmSetWindowAttribute(hwnd, 33, ref pref, 4);
            }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        [StructLayout(LayoutKind.Sequential)]
        struct MmiPoint { public int x, y; }
        [StructLayout(LayoutKind.Sequential)]
        struct MINMAXINFO { public MmiPoint ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
        [StructLayout(LayoutKind.Sequential)]
        struct WorkRect { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public WorkRect rcMonitor, rcWork; public int dwFlags; }

        /// <summary>挂载 Win32 消息钩子（须在 SourceInitialized 之后调用）。</summary>
        public static void Attach(Window win)
        {
            var hwndSrc = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(win).Handle);
            if (hwndSrc != null) hwndSrc.AddHook((hwnd, msg, wParam, lParam, ref handled) => WndProc(win, hwnd, msg, wParam, lParam, ref handled));
        }

        static IntPtr WndProc(Window win, IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_GETMINMAXINFO = 0x0024;
            if (msg == WM_GETMINMAXINFO)
            {
                try
                {
                    var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO));
                    IntPtr mon = MonitorFromWindow(hwnd, 2);   // MONITOR_DEFAULTTONEAREST
                    if (mon != IntPtr.Zero)
                    {
                        var info = new MONITORINFO();
                        info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                        if (GetMonitorInfo(mon, ref info))
                        {
                            mmi.ptMaxPosition.x = info.rcWork.left;
                            mmi.ptMaxPosition.y = info.rcWork.top;
                            mmi.ptMaxSize.x = info.rcWork.right - info.rcWork.left;
                            mmi.ptMaxSize.y = info.rcWork.bottom - info.rcWork.top;
                        }
                    }
                    // 无边框窗口：WPF 的 MinWidth/MinHeight 在 Win32 拖拽调整大小时不生效，
                    // 必须在这里设置最小跟踪尺寸（物理像素，需按 DPI 缩放换算）
                    var src = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
                    double dpiX = 1.0, dpiY = 1.0;
                    if (src != null && src.CompositionTarget != null)
                    {
                        dpiX = src.CompositionTarget.TransformToDevice.M11;
                        dpiY = src.CompositionTarget.TransformToDevice.M22;
                    }
                    mmi.ptMinTrackSize.x = (int)Math.Ceiling(win.MinWidth * dpiX);
                    mmi.ptMinTrackSize.y = (int)Math.Ceiling(win.MinHeight * dpiY);
                    Marshal.StructureToPtr(mmi, lParam, true);
                    handled = true;
                }
                catch { }
            }
            return IntPtr.Zero;
        }
    }
}
