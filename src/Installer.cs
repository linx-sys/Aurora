/* ============================================================
 * Installer.cs — Aurora 极光音乐 安装向导（WinForms，深色 UI）
 * 自包含单文件：内嵌播放器与卸载器。
 * MP3 文件关联：安装器落一个 .associate 标记，播放器首次启动时
 * 读取并注册自身路径（见 App.Main / Assoc.cs）。
 * 支持静默安装：AuroraPlayer-Setup.exe /S /D=安装目录
 * ============================================================ */
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Aurora
{
    static class Installer
    {
        const string Version = "1.0.0";
        const string ExeName = "AuroraPlayer.exe";

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        static extern int SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr ShellExecute(IntPtr hwnd, string verb, string file, string parameters, string directory, int showCmd);

        const int SW_SHOWNORMAL = 1;
        const int WM_NCLBUTTONDOWN = 0xA1;
        const int HTCAPTION = 0x2;

        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            // ---- 静默模式 ----
            bool silent = false, assoc = true, desktop = true;
            string dir = DefaultDir();
            foreach (string a in args)
            {
                string arg = a.Trim('"');
                if (string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase)) silent = true;
                else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase)) dir = arg.Substring(3).Trim('"');
                else if (string.Equals(arg, "/noassoc", StringComparison.OrdinalIgnoreCase)) assoc = false;
                else if (string.Equals(arg, "/nodesktop", StringComparison.OrdinalIgnoreCase)) desktop = false;
            }

            if (silent)
            {
                try { RunInstall(dir, desktop, assoc, null); Environment.Exit(0); }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Aurora 安装失败"); Environment.Exit(1); }
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new SetupForm(dir, desktop, assoc));
        }

        static string DefaultDir()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "AuroraPlayer");
        }

        /// <summary>校验安装目录：绝对路径、无非法字符、不在系统关键目录。</summary>
        static bool IsSafeInstallDir(string dir, out string full)
        {
            full = null;
            if (string.IsNullOrWhiteSpace(dir)) return false;
            try
            {
                if (dir.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
                if (!Path.IsPathRooted(dir)) return false;
                full = Path.GetFullPath(dir);
            }
            catch { return false; }
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sys32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (full.Equals(win, StringComparison.OrdinalIgnoreCase)) return false;
            if (full.StartsWith(sys32, StringComparison.OrdinalIgnoreCase)) return false;
            if (full.TrimEnd('\\').Length <= 3) return false; // 盘根，如 C:\
            return true;
        }

        /* ============================================================
         * 安装执行
         * ============================================================ */

        static void EmitResource(string resName, string targetFile)
        {
            // 规范化目标路径，确保落在安装目录内
            string targetPath = Path.GetFullPath(targetFile);
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resName))
            {
                if (s == null) throw new Exception("缺少内嵌资源 " + resName);
                var buf = new byte[s.Length];
                using (var ms = new MemoryStream())
                {
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
                    File.WriteAllBytes(targetPath, ms.ToArray());
                }
            }
        }

        static void RunInstall(string dirRaw, bool desktopShortcut, bool associateMp3, Action<int, string> progress)
        {
            // 规范化安装目录
            string dir = Path.GetFullPath(dirRaw);

            if (progress != null) progress(5, "准备安装目录…");
            Directory.CreateDirectory(dir);

            string exePath = Path.Combine(dir, ExeName);
            string uninsPath = Path.Combine(dir, "unins.exe");

            // 若播放器正在运行，先结束
            try { foreach (var p in Process.GetProcessesByName("AuroraPlayer")) p.Kill(); } catch { }
            try { foreach (var p in Process.GetProcessesByName("unins")) p.Kill(); } catch { }

            if (progress != null) progress(25, "正在释放程序文件…");
            EmitResource("Aurora.AuroraPlayer.exe", exePath);
            if (progress != null) progress(45, "正在释放卸载器…");
            EmitResource("Aurora.unins.exe", uninsPath);

            if (progress != null) progress(60, "正在写入注册表…");
            using (var k = Registry.CurrentUser.CreateSubKey(string.Join(
                "\\", "Software", "Microsoft", "Windows", "CurrentVersion", "Uninstall", "AuroraPlayer")))
            {
                k.SetValue("DisplayName", "Aurora 极光音乐");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "Aurora Player");
                k.SetValue("DisplayIcon", string.Format("\"{0}\"", exePath));
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", string.Format("\"{0}\"", uninsPath));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", 640, RegistryValueKind.DWord); // KB
            }

            if (progress != null) progress(80, "正在创建快捷方式…");
            CreateShortcut(exePath, dir, "Aurora Player.lnk", false);
            if (desktopShortcut) CreateShortcut(exePath, dir, "Aurora Player.lnk", true);

            if (associateMp3)
            {
                if (progress != null) progress(90, "正在准备 MP3 关联…");
                // 标记文件：播放器首次启动时读取并注册文件关联（见 App.Main）
                File.WriteAllText(Path.GetFullPath(Path.Combine(dir, ".associate")), "1");
            }

            if (progress != null) progress(100, "安装完成");
        }

        /// <summary>通过 IShellLink COM 接口创建 .lnk 快捷方式。</summary>
        static void CreateShortcut(string exePath, string workDir, string name, bool desktop)
        {
            try
            {
                string folder = desktop
                    ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Aurora Player");
                Directory.CreateDirectory(folder);
                string lnkPath = Path.Combine(folder, name);

                IShellLinkW link = (IShellLinkW)new ShellLinkComObject();
                link.SetPath(exePath);
                link.SetWorkingDirectory(workDir);
                link.SetIconLocation(exePath, 0);
                link.SetDescription("Aurora 极光音乐");
                IPersistFile persist = (IPersistFile)link;
                persist.Save(lnkPath, true);
                Marshal.ReleaseComObject(link);
            }
            catch { /* 快捷方式失败不阻断安装 */ }
        }

        /* ============================================================
         * UI — 深色安装向导
         * ============================================================ */

        class SetupForm : Form
        {
            [DllImport("dwmapi.dll")]
            static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

            readonly string defaultDir;
            readonly bool defDesktop, defAssoc;

            Panel topBar, body;
            Label title;
            Button btnClose;
            Panel pageWelcome, pageOptions, pageProgress, pageDone;
            ComboBox dirBox;
            CheckBox chkDesktop, chkAssoc, chkRun;
            GradientButton btnNext, btnInstall, btnDone;
            DarkButton btnBack;
            ProgressBar bar;
            Label barLabel;
            Image logo;

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try
                {
                    if (Environment.OSVersion.Version.Build >= 22000)
                    {
                        int pref = 2; // DWMWCP_ROUND：Win11 系统圆角
                        DwmSetWindowAttribute(Handle, 33, ref pref, 4);
                    }
                    else
                    {
                        using (var p = RoundRect(new Rectangle(0, 0, Width, Height), 16))
                            Region = new Region(p);
                    }
                }
                catch { }
            }

            public SetupForm(string dir, bool desktop, bool assoc)
            {
                defaultDir = dir; defDesktop = desktop; defAssoc = assoc;

                FormBorderStyle = FormBorderStyle.None;
                StartPosition = FormStartPosition.CenterScreen;
                Size = new Size(640, 460);
                BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9);
                ForeColor = Color.FromArgb(0x1A, 0x20, 0x30);
                Font = new Font("微软雅黑", 9.5f);
                Text = "Aurora 极光音乐 安装向导";

                try
                {
                    using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aurora.icon.png"))
                        if (s != null) logo = Image.FromStream(s);
                }
                catch { }

                BuildTopBar();
                BuildPages();
                ShowPage(pageWelcome);
            }

            void BuildTopBar()
            {
                topBar = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Color.White };
                title = new Label
                {
                    Text = "Aurora 极光音乐 · 安装向导",
                    ForeColor = Color.FromArgb(0x1A, 0x20, 0x30),
                    Font = new Font("微软雅黑", 10.5f, FontStyle.Bold),
                    AutoSize = true,
                    Location = new Point(20, 11),
                };
                btnClose = new Button
                {
                    Text = "✕",
                    Size = new Size(36, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(0x5A, 0x64, 0x74),
                    Font = new Font("Segoe UI", 10f),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                };
                btnClose.FlatAppearance.BorderSize = 0;
                btnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE8, 0x11, 0x23);
                btnClose.Location = new Point(Width - 48, 7);
                btnClose.Click += (s, e) => Close();

                var btnMin = new Button
                {
                    Text = "—",
                    Size = new Size(36, 30),
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(0x5A, 0x64, 0x74),
                    Font = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                    Cursor = Cursors.Hand,
                    TabStop = false,
                };
                btnMin.FlatAppearance.BorderSize = 0;
                btnMin.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE6, 0xEA, 0xF2);
                btnMin.Location = new Point(Width - 86, 7);
                btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;

                topBar.Controls.Add(title);
                topBar.Controls.Add(btnClose);
                topBar.Controls.Add(btnMin);
                topBar.MouseDown += Drag;
                title.MouseDown += Drag;
                Controls.Add(topBar);
            }

            void Drag(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    ReleaseCapture();
                    SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
                }
            }

            void BuildPages()
            {
                body = new Panel { Location = new Point(0, 44), Size = new Size(Width, Height - 44), BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9) };

                // ---- 欢迎页 ----
                pageWelcome = NewPage();
                var pic = new PictureBox
                {
                    Image = logo,
                    Size = new Size(96, 96),
                    Location = new Point((Width - 96) / 2, 100),
                    SizeMode = PictureBoxSizeMode.Zoom,
                };
                var name = new Label
                {
                    Text = "Aurora 极光音乐",
                    Font = new Font("微软雅黑", 21f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(0x1A, 0x20, 0x30),
                    AutoSize = true,
                };
                name.Location = new Point((Width - name.PreferredWidth) / 2, 212);
                var slogan = new Label
                {
                    Text = "一个纯粹、好用的本地音乐播放器",
                    Font = new Font("微软雅黑", 10f),
                    ForeColor = Color.FromArgb(0x5A, 0x64, 0x74),
                    AutoSize = true,
                };
                slogan.Location = new Point((Width - slogan.PreferredWidth) / 2, 258);
                btnNext = new GradientButton { Text = "下一步", Size = new Size(140, 42), Location = new Point(Width / 2 - 70, 354) };
                btnNext.Click += (s, e) => ShowPage(pageOptions);
                pageWelcome.Controls.AddRange(new Control[] { pic, name, slogan, btnNext });

                // ---- 选项页 ----
                pageOptions = NewPage();
                var l1 = new Label { Text = "选择安装位置", Font = new Font("微软雅黑", 13f, FontStyle.Bold), ForeColor = Color.FromArgb(0x1A, 0x20, 0x30), Location = new Point(56, 46), AutoSize = true };
                dirBox = new ComboBox
                {
                    Text = defaultDir,
                    Location = new Point(56, 86),
                    Size = new Size(418, 30),
                    BackColor = Color.White,
                    ForeColor = Color.FromArgb(0x1A, 0x20, 0x30),
                    FlatStyle = FlatStyle.Flat,
                    DropDownStyle = ComboBoxStyle.DropDown, // 可编辑：支持任意自选路径
                };
                // 预置候选：默认位置 + 各固定磁盘分区（可再通过「浏览…」或手动输入其他路径）
                try
                {
                    dirBox.Items.Add(DefaultDir());
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string candidate = Path.Combine(d.Name, "AuroraPlayer");
                        if (candidate.Length > 3) dirBox.Items.Add(candidate);
                    }
                }
                catch { }
                var btnBrowse = new DarkButton { Text = "浏览…", Location = new Point(486, 80) };
                btnBrowse.Click += (s, e) =>
                {
                    using (var dlg = new DirPickerDialog(dirBox.Text))
                    {
                        if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.SelectedPath))
                            dirBox.Text = dlg.SelectedPath;
                    }
                };
                chkDesktop = new CheckBox { Text = "创建桌面快捷方式", Checked = defDesktop, Location = new Point(56, 152), AutoSize = true, ForeColor = Color.FromArgb(0x3D, 0x45, 0x57) };
                chkAssoc = new CheckBox
                {
                    Text = "将 Aurora 设为 MP3 默认播放器（推荐）",
                    Checked = defAssoc,
                    Location = new Point(56, 186),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(0x3D, 0x45, 0x57),
                };
                btnBack = new DarkButton { Text = "上一步", Size = new Size(120, 42), Location = new Point(332, 354) };
                btnBack.Click += (s, e) => ShowPage(pageWelcome);
                btnInstall = new GradientButton { Text = "立即安装", Size = new Size(120, 42), Location = new Point(464, 354) };
                btnInstall.Click += (s, e) => StartInstall();
                pageOptions.Controls.AddRange(new Control[] { l1, dirBox, btnBrowse, chkDesktop, chkAssoc, btnBack, btnInstall });

                // ---- 进度页 ----
                pageProgress = NewPage();
                barLabel = new Label { Text = "正在安装…", ForeColor = Color.FromArgb(0x5A, 0x64, 0x74), Location = new Point(60, 190), AutoSize = true };
                bar = new ProgressBar
                {
                    Location = new Point(60, 222),
                    Size = new Size(Width - 120, 10),
                    Style = ProgressBarStyle.Continuous,
                    Minimum = 0, Maximum = 100,
                };
                pageProgress.Controls.AddRange(new Control[] { barLabel, bar });

                // ---- 完成页 ----
                pageDone = NewPage();
                var ok = new Label { Text = "✔", Font = new Font("Segoe UI Symbol", 40f), ForeColor = Color.FromArgb(0x4C, 0xC9, 0xF0), AutoSize = true };
                ok.Location = new Point(Width / 2 - 32, 60);
                var done = new Label { Text = "安装完成！", Font = new Font("微软雅黑", 18f, FontStyle.Bold), ForeColor = Color.FromArgb(0x1A, 0x20, 0x30), AutoSize = true };
                done.Location = new Point((Width - done.PreferredWidth) / 2, 150);
                var hint = new Label
                {
                    Text = "双击任意 MP3 文件即可用 Aurora 打开播放\n也可以从开始菜单或桌面快捷方式启动",
                    ForeColor = Color.FromArgb(0x5A, 0x64, 0x74),
                    Location = new Point(0, 198),
                    AutoSize = false,
                    Size = new Size(Width, 52),
                    TextAlign = ContentAlignment.TopCenter,
                };
                chkRun = new CheckBox { Text = "立即运行 Aurora 极光音乐", Checked = true, Location = new Point(Width / 2 - 96, 268), AutoSize = true, ForeColor = Color.FromArgb(0x3D, 0x45, 0x57) };
                btnDone = new GradientButton { Text = "完成", Size = new Size(140, 42), Location = new Point(Width / 2 - 70, 320) };
                btnDone.Click += (s, e) =>
                {
                    if (chkRun.Checked)
                    {
                        try
                        {
                            // 通过 ShellExecute 以“打开”方式启动（等同资源管理器双击）
                            ShellExecute(IntPtr.Zero, "open", Path.Combine(dirBox.Text.Trim(), ExeName), null, null, SW_SHOWNORMAL);
                        }
                        catch { }
                    }
                    Close();
                };
                pageDone.Controls.AddRange(new Control[] { ok, done, hint, chkRun, btnDone });

                Controls.Add(body);
            }

            Panel NewPage()
            {
                var p = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9), Visible = false };
                body.Controls.Add(p);
                return p;
            }

            void ShowPage(Panel page)
            {
                foreach (Control c in body.Controls) ((Panel)c).Visible = false;
                page.Visible = true;
            }

            void StartInstall()
            {
                string dir;
                if (!IsSafeInstallDir(dirBox.Text.Trim().Trim('"'), out dir))
                {
                    MessageBox.Show("请输入有效的安装目录（绝对路径，且不位于系统目录）");
                    return;
                }
                ShowPage(pageProgress);
                btnClose.Enabled = false;
                try
                {
                    RunInstall(dir, chkDesktop.Checked, chkAssoc.Checked, (pct, msg) =>
                    {
                        bar.Value = pct;
                        barLabel.Text = msg;
                        Application.DoEvents();
                    });
                    ShowPage(pageDone);
                }
                catch (Exception ex)
                {
                    btnClose.Enabled = true;
                    MessageBox.Show("安装失败：" + ex.Message, "Aurora", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    ShowPage(pageOptions);
                }
            }
        }

        /* ---------- 控件 ---------- */

        internal class GradientButton : Button
        {
            bool hover;
            public GradientButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                // 圆角外四角必须露出页面背景色，否则出现直角残影
                BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9);
                ForeColor = Color.White;
                Font = new Font("微软雅黑", 11f, FontStyle.Bold);
                Cursor = Cursors.Hand;
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // ButtonBase.OnPaintBackground 为空实现，必须先铺页面背景避免圆角外残留
                g.Clear(Color.FromArgb(0xF3, 0xF5, 0xF9));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundRect(rect, 9))
                {
                    Color c1 = hover ? Color.FromArgb(0x6E, 0xD8, 0xFF) : Color.FromArgb(0x4C, 0xC9, 0xF0);
                    Color c2 = hover ? Color.FromArgb(0x9A, 0x7E, 0xFF) : Color.FromArgb(0x7C, 0x5C, 0xFF);
                    using (var lg = new LinearGradientBrush(rect, c1, c2, 25f))
                        g.FillPath(lg, path);
                    TextRenderer.DrawText(g, Text, Font, rect, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
        }

        /// <summary>浅色描边按钮：与 GradientButton 同构（圆角/字体/尺寸规格一致）。</summary>
        internal class DarkButton : Button
        {
            bool hover, pressed;
            public DarkButton()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9); // 圆角外四角露出页面背景色
                ForeColor = Color.FromArgb(0x1A, 0x20, 0x30);
                Font = new Font("微软雅黑", 11f);
                Cursor = Cursors.Hand;
                AutoSize = true;
                MinimumSize = new Size(0, 42);
                Padding = new Padding(16, 0, 16, 0);
            }
            protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hover = false; pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { pressed = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                // ButtonBase.OnPaintBackground 为空实现，双缓冲位图的圆角外区域会残留
                // 垃圾像素（黑色直角），必须先手动铺页面背景
                g.Clear(Color.FromArgb(0xF3, 0xF5, 0xF9));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = RoundRect(rect, 9))
                {
                    Color fill = pressed ? Color.FromArgb(0xE2, 0xE7, 0xF0) : hover ? Color.FromArgb(0xED, 0xF0, 0xF6) : Color.White;
                    using (var b = new SolidBrush(fill))
                        g.FillPath(b, path);
                    using (var pen = new Pen(Color.FromArgb(0xD0, 0xD6, 0xE2)))
                        g.DrawPath(pen, path);
                    TextRenderer.DrawText(g, Text, Font, rect, ForeColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
        }

        static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    /* ---------- IShellLink COM 声明 ---------- */

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkComObject { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        IntPtr GetIDList();
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        short GetHotkey();
        void SetHotkey(short wHotkey);
        int GetShowCmd();
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    /* ============================ 文件夹浏览对话框 ============================ */

    /// <summary>NSIS 风格的树形文件夹浏览器（桌面/文档/下载/此电脑 → 驱动器 → 目录）。</summary>
    class DirPickerDialog : Form
    {
        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, IntPtr token, out IntPtr path);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint attrs, ref SHFILEINFO info, uint cbSize, uint flags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr h);

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_SMALLICON = 0x1;
        static readonly string ComputerPath = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        static readonly Guid DownloadsId = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        public string SelectedPath;
        readonly TreeView tree;
        readonly TextBox pathBox;
        readonly ImageList icons = new ImageList { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
        readonly Dictionary<string, string> iconKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly string initialPath;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (Environment.OSVersion.Version.Build >= 22000)
                {
                    int pref = 2;
                    DwmSetWindowAttribute(Handle, 33, ref pref, 4);
                }
            }
            catch { }
        }

        public DirPickerDialog(string initial)
        {
            initialPath = initial;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            Text = "浏览文件夹";
            ClientSize = new Size(624, 526);
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = Color.FromArgb(0xF3, 0xF5, 0xF9);
            Font = new Font("微软雅黑", 9.5f);

            var label = new Label
            {
                Text = "选择要安装 Aurora 极光音乐的文件夹位置:",
                Location = new Point(20, 16),
                AutoSize = true,
                ForeColor = Color.FromArgb(0x1A, 0x20, 0x30),
            };

            tree = new TreeView
            {
                Location = new Point(20, 48),
                Size = new Size(584, 386),
                ImageList = icons,
                ItemHeight = 24,
                HideSelection = false,
                FullRowSelect = true,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.White,
            };
            tree.BeforeExpand += TreeBeforeExpand;
            tree.AfterSelect += (s, e) =>
            {
                if (e.Node.Tag is string)
                {
                    SelectedPath = (string)e.Node.Tag;
                    pathBox.Text = SelectedPath;
                }
            };

            pathBox = new TextBox
            {
                Location = new Point(20, 442),
                Size = new Size(584, 26),
                ReadOnly = true,
                BackColor = Color.White,
                ForeColor = Color.FromArgb(0x5A, 0x64, 0x74),
                BorderStyle = BorderStyle.FixedSingle,
            };

            var btnNew = new Installer.DarkButton { Text = "新建文件夹(M)", Location = new Point(20, 478) };
            btnNew.Click += (s, e) => CreateNewFolder();

            var btnOk = new Installer.GradientButton { Text = "确定", Size = new Size(110, 36), Location = new Point(374, 478) };
            var btnCancel = new Installer.DarkButton { Text = "取消", Size = new Size(110, 36), Location = new Point(494, 478) };
            btnOk.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(SelectedPath)) DialogResult = DialogResult.OK;
                else MessageBox.Show("请先选择一个文件夹");
            };
            btnCancel.Click += (s, e) => DialogResult = DialogResult.Cancel;

            Controls.Add(label);
            Controls.Add(tree);
            Controls.Add(pathBox);
            Controls.Add(btnNew);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);

            Load += (s, e) =>
            {
                AddSpecial("桌面", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                AddSpecial("文档", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
                AddSpecial("下载", KnownFolder(DownloadsId));
                AddSpecial("音乐", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));
                AddSpecial("图片", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                AddSpecial("视频", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

                var pcKey = IconKey(ComputerPath);
                var pc = new TreeNode("此电脑") { ImageKey = pcKey, SelectedImageKey = pcKey, Tag = null };
                pc.Nodes.Add(new TreeNode("…"));
                tree.Nodes.Add(pc);

                // 自动展开到初始路径所在驱动器
                try
                {
                    if (!string.IsNullOrEmpty(initialPath))
                    {
                        string root = Path.GetPathRoot(Path.GetFullPath(initialPath));
                        foreach (TreeNode n in tree.Nodes)
                        {
                            if (n.Text != "此电脑") continue;
                            n.Expand();
                            foreach (TreeNode drive in n.Nodes)
                            {
                                string dp = drive.Tag as string;
                                if (dp != null && root != null && dp.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                                {
                                    drive.Expand();
                                    tree.SelectedNode = drive;
                                    break;
                                }
                            }
                            break;
                        }
                    }
                }
                catch { }
            };
        }

        /// <summary>取（或提取并缓存）路径对应的系统图标在 ImageList 中的键。</summary>
        string IconKey(string shellPath)
        {
            string key;
            if (iconKeys.TryGetValue(shellPath, out key)) return key;
            key = "ic:" + iconKeys.Count;
            Icon icon = GetShellIcon(shellPath);
            icons.Images.Add(key, icon != null ? icon.ToBitmap() : FallbackFolderBitmap());
            if (icon != null) icon.Dispose();
            iconKeys[shellPath] = key;
            return key;
        }

        static Icon GetShellIcon(string path)
        {
            try
            {
                var fi = new SHFILEINFO();
                if (SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_ICON | SHGFI_SMALLICON) != IntPtr.Zero
                    && fi.hIcon != IntPtr.Zero)
                {
                    try { return (Icon)Icon.FromHandle(fi.hIcon).Clone(); }
                    finally { DestroyIcon(fi.hIcon); }
                }
            }
            catch { }
            return null;
        }

        static Bitmap FallbackFolderBitmap()
        {
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                using (var b = new SolidBrush(Color.FromArgb(0xFF, 0xC9, 0x3C)))
                {
                    g.FillRectangle(b, 1, 4, 6, 3);
                    g.FillRectangle(b, 1, 6, 14, 8);
                }
            }
            return bmp;
        }

        void AddSpecial(string name, string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            string key = IconKey(path);
            var n = new TreeNode(name) { ImageKey = key, SelectedImageKey = key, Tag = path };
            n.Nodes.Add(new TreeNode("…"));
            tree.Nodes.Add(n);
        }

        static string KnownFolder(Guid id)
        {
            try
            {
                IntPtr p;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) == 0)
                {
                    string s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return null;
        }

        void TreeBeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            TreeNode n = e.Node;
            if (n.Nodes.Count != 1 || n.Nodes[0].Text != "…") return; // 已展开过
            n.Nodes.Clear();

            if (n.Text == "此电脑" && n.Tag == null)
            {
                try
                {
                    foreach (DriveInfo d in DriveInfo.GetDrives())
                    {
                        if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                        string key = IconKey(d.Name);
                        var dn = new TreeNode(d.Name) { ImageKey = key, SelectedImageKey = key, Tag = d.Name };
                        dn.Nodes.Add(new TreeNode("…"));
                        n.Nodes.Add(dn);
                    }
                }
                catch { }
                return;
            }

            string path = n.Tag as string;
            if (path == null) return;
            FillChildren(n, path);
        }

        void FillChildren(TreeNode n, string path)
        {
            try
            {
                foreach (string dir in Directory.GetDirectories(path))
                {
                    try
                    {
                        var attr = new DirectoryInfo(dir).Attributes;
                        if ((attr & FileAttributes.Hidden) != 0 || (attr & FileAttributes.System) != 0) continue;
                    }
                    catch { continue; }
                    string key = IconKey(dir);
                    var child = new TreeNode(Path.GetFileName(dir)) { ImageKey = key, SelectedImageKey = key, Tag = dir };
                    if (HasSubDirs(dir)) child.Nodes.Add(new TreeNode("…"));
                    n.Nodes.Add(child);
                }
            }
            catch { } // 无权限目录静默跳过
        }

        static bool HasSubDirs(string dir)
        {
            try
            {
                foreach (string d in Directory.GetDirectories(dir))
                {
                    try
                    {
                        var attr = new DirectoryInfo(d).Attributes;
                        if ((attr & (FileAttributes.Hidden | FileAttributes.System)) == 0) return true;
                    }
                    catch { }
                }
            }
            catch { }
            return false;
        }

        void CreateNewFolder()
        {
            TreeNode sel = tree.SelectedNode;
            string parent = sel != null ? (sel.Tag as string) : null;
            if (parent == null)
            {
                MessageBox.Show("请先选择一个磁盘或文件夹");
                return;
            }
            try
            {
                string name = "新建文件夹";
                string candidate = Path.Combine(parent, name);
                int i = 2;
                while (Directory.Exists(candidate)) { candidate = Path.Combine(parent, name + " (" + i + ")"); i++; }
                Directory.CreateDirectory(candidate);

                // 重列父节点子项并选中新文件夹
                sel.Nodes.Clear();
                FillChildren(sel, parent);
                foreach (TreeNode c in sel.Nodes)
                    if (string.Equals((string)c.Tag, candidate, StringComparison.OrdinalIgnoreCase)) { tree.SelectedNode = c; break; }
                if (tree.SelectedNode == null) tree.SelectedNode = sel;
                SelectedPath = candidate;
                if (!sel.IsExpanded) sel.Expand();
            }
            catch (Exception ex)
            {
                MessageBox.Show("创建文件夹失败：" + ex.Message);
            }
        }
    }
}
