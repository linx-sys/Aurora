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

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            // ---- 静默模式 ----
            bool silent = false, assoc = true, desktop = true;
            string dir = DefaultDir();
            // 正确处理 /D= 路径含空格和引号的边界情况
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.IsNullOrEmpty(arg)) continue;
                if (string.Equals(arg, "/S", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(arg, "/silent", StringComparison.OrdinalIgnoreCase))
                    silent = true;
                else if (arg.StartsWith("/D=", StringComparison.OrdinalIgnoreCase))
                {
                    string pathPart = arg.Substring(3);
                    if (pathPart.StartsWith("\"") && pathPart.EndsWith("\"") && pathPart.Length >= 2)
                        dir = pathPart.Trim('"');
                    else if (pathPart.StartsWith("\"") && !pathPart.EndsWith("\""))
                    {
                        string combined = pathPart.Substring(1);
                        while (i + 1 < args.Length)
                        {
                            i++;
                            string next = args[i];
                            if (next.EndsWith("\"")) { combined += " " + next.Substring(0, next.Length - 1); break; }
                            combined += " " + next;
                        }
                        dir = combined;
                    }
                    else if (!pathPart.StartsWith("\""))
                    {
                        string combined = pathPart;
                        while (i + 1 < args.Length && !args[i + 1].StartsWith("/"))
                        { i++; combined += " " + args[i]; }
                        dir = combined.Trim('"');
                    }
                    else dir = pathPart.Trim('"');
                    dir = dir.TrimEnd('\\', '/', ' ', '\t');
                    if (dir.Length == 2 && char.IsLetter(dir[0]) && dir[1] == ':') dir += "\\";
                }
                else if (string.Equals(arg, "/noassoc", StringComparison.OrdinalIgnoreCase)) assoc = false;
                else if (string.Equals(arg, "/nodesktop", StringComparison.OrdinalIgnoreCase)) desktop = false;
            }

            if (silent)
            {
                string[] silentExts = assoc
                    ? new[] { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus", ".wma" }
                    : null;
                try { RunInstall(dir, desktop, silentExts, null); Environment.Exit(0); }
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

        static void RunInstall(string dirRaw, bool desktopShortcut, string[] selectedExts, Action<int, string> progress)
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
            EmitResource("Aurora.AuroraPlayer.dll", Path.Combine(dir, "AuroraPlayer.dll"));
            EmitResource("Aurora.AuroraPlayer.runtimeconfig.json", Path.Combine(dir, "AuroraPlayer.runtimeconfig.json"));
            EmitResource("Aurora.AuroraPlayer.deps.json", Path.Combine(dir, "AuroraPlayer.deps.json"));
            if (progress != null) progress(45, "正在释放卸载器…");
            EmitResource("Aurora.unins.exe", uninsPath);
            EmitResource("Aurora.unins.runtimeconfig.json", Path.Combine(dir, "unins.runtimeconfig.json"));
            EmitResource("Aurora.unins.deps.json", Path.Combine(dir, "unins.deps.json"));
            // 释放第三方依赖 DLL（全部 MIT 许可证）
            EmitResource("Aurora.NVorbis.dll", Path.Combine(dir, "NVorbis.dll"));
            EmitResource("Aurora.Concentus.dll", Path.Combine(dir, "Concentus.dll"));
            EmitResource("Aurora.Concentus.Oggfile.dll", Path.Combine(dir, "Concentus.Oggfile.dll"));
            EmitResource("Aurora.NAudio.dll", Path.Combine(dir, "NAudio.dll"));
            EmitResource("Aurora.NAudio.Core.dll", Path.Combine(dir, "NAudio.Core.dll"));
            EmitResource("Aurora.NAudio.WinMM.dll", Path.Combine(dir, "NAudio.WinMM.dll"));
            EmitResource("Aurora.NAudio.Wasapi.dll", Path.Combine(dir, "NAudio.Wasapi.dll"));

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
                // EstimatedSize 按安装目录实际文件合计（KB），始终与真实体积一致
                long totalBytes = 0;
                foreach (string f in Directory.GetFiles(dir))
                {
                    try { totalBytes += new FileInfo(f).Length; } catch { }
                }
                k.SetValue("EstimatedSize", (int)(totalBytes / 1024), RegistryValueKind.DWord); // KB
            }

            if (progress != null) progress(80, "正在创建快捷方式…");
            CreateShortcut(exePath, dir, "Aurora Player.lnk", false);
            if (desktopShortcut) CreateShortcut(exePath, dir, "Aurora Player.lnk", true);

            if (selectedExts != null && selectedExts.Length > 0)
            {
                if (progress != null) progress(90, "正在准备文件关联…");
                // 标记文件：播放器首次启动时读取并注册文件关联（见 App.Main）
                // 内容为 "1"（全部 9 种）或逗号分隔的扩展名列表
                string content = selectedExts.Length >= 9 ? "1" : string.Join(",", selectedExts);
                File.WriteAllText(Path.GetFullPath(Path.Combine(dir, ".associate")), content);
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
            CheckBox[] fmtChecks;   // 9 种格式复选框（3x3）
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
                    using (var ofd = new OpenFileDialog())
                    {
                        ofd.CheckFileExists = false;
                        ofd.ValidateNames = false;
                        ofd.FileName = "安装目录";
                        ofd.Title = "选择安装文件夹";
                        if (!string.IsNullOrEmpty(dirBox.Text) && Directory.Exists(dirBox.Text))
                            ofd.InitialDirectory = dirBox.Text;
                        // 反射设置 FOS_PICKFOLDERS，启用 Windows 现代文件夹选择对话框
                        try
                        {
                            var fi = typeof(FileDialog).GetField("Options", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (fi != null)
                            {
                                int opts = (int)fi.GetValue(ofd);
                                fi.SetValue(ofd, opts | 0x20);
                            }
                        }
                        catch { }
                        if (ofd.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(ofd.FileName))
                        {
                            // FOS_PICKFOLDERS 下 FileName 可能带占位符；若不是已存在目录则取父目录
                            string picked = ofd.FileName;
                            if (!Directory.Exists(picked)) picked = Path.GetDirectoryName(picked);
                            dirBox.Text = picked;
                        }
                    }
                };
                chkDesktop = new CheckBox { Text = "创建桌面快捷方式", Checked = defDesktop, Location = new Point(56, 152), AutoSize = true, ForeColor = Color.FromArgb(0x3D, 0x45, 0x57) };
                chkAssoc = new CheckBox
                {
                    Text = "关联音频格式（勾选后双击用 Aurora 打开）",
                    Checked = defAssoc,
                    Location = new Point(56, 186),
                    AutoSize = true,
                    ForeColor = Color.FromArgb(0x3D, 0x45, 0x57),
                };
                // 9 种格式 3x3 排列，每个带独立复选框
                string[] fmtExts = { ".mp3", ".m4a", ".flac", ".wav", ".ogg", ".oga", ".aac", ".opus", ".wma" };
                string[] fmtNames = { "MP3", "M4A", "FLAC", "WAV", "OGG", "OGA", "AAC", "OPUS", "WMA" };
                fmtChecks = new CheckBox[9];
                for (int i = 0; i < 9; i++)
                {
                    int row = i / 3, col = i % 3;
                    fmtChecks[i] = new CheckBox
                    {
                        Text = fmtNames[i],
                        Tag = fmtExts[i],
                        Checked = defAssoc,
                        Location = new Point(78 + col * 185, 214 + row * 28),
                        AutoSize = true,
                        ForeColor = Color.FromArgb(0x6B, 0x73, 0x86),
                        Font = new Font("Microsoft YaHei UI", 9f),
                    };
                }
                // 全选开关联动：勾选总开关 → 全选子项；子项全选/取消 → 同步总开关
                bool suppressLink = false;
                chkAssoc.CheckedChanged += (s, e) =>
                {
                    if (suppressLink) return;
                    suppressLink = true;
                    foreach (var cb in fmtChecks) cb.Checked = chkAssoc.Checked;
                    suppressLink = false;
                };
                foreach (var cb in fmtChecks)
                {
                    cb.CheckedChanged += (s, e) =>
                    {
                        if (suppressLink) return;
                        bool all = true;
                        foreach (var c in fmtChecks) if (!c.Checked) { all = false; break; }
                        suppressLink = true;
                        chkAssoc.Checked = all;
                        suppressLink = false;
                    };
                }
                btnBack = new DarkButton { Text = "上一步", Size = new Size(120, 42), Location = new Point(332, 354) };
                btnBack.Click += (s, e) => ShowPage(pageWelcome);
                btnInstall = new GradientButton { Text = "立即安装", Size = new Size(120, 42), Location = new Point(464, 354) };
                btnInstall.Click += (s, e) => StartInstall();
                pageOptions.Controls.AddRange(new Control[] { l1, dirBox, btnBrowse, chkDesktop, chkAssoc, fmtChecks[0], fmtChecks[1], fmtChecks[2], fmtChecks[3], fmtChecks[4], fmtChecks[5], fmtChecks[6], fmtChecks[7], fmtChecks[8], btnBack, btnInstall });

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
                chkRun = new CheckBox { Text = "立即运行 Aurora 极光音乐", Checked = true, Location = new Point(Width / 2 - 96, 210), AutoSize = true, ForeColor = Color.FromArgb(0x3D, 0x45, 0x57) };
                btnDone = new GradientButton { Text = "完成", Size = new Size(140, 42), Location = new Point(Width / 2 - 70, 262) };
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
                pageDone.Controls.AddRange(new Control[] { ok, done, chkRun, btnDone });

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
                    // 收集选中的格式扩展名
                    var sel = new System.Collections.Generic.List<string>();
                    if (chkAssoc.Checked)
                        foreach (var cb in fmtChecks)
                            if (cb.Checked) sel.Add((string)cb.Tag);
                    string[] selectedExts = sel.Count > 0 ? sel.ToArray() : null;
                    RunInstall(dir, chkDesktop.Checked, selectedExts, (pct, msg) =>
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

    /* ============================ 现代文件夹选择对话框 ============================ */

    /// <summary>
    /// 调用 Windows 现代文件选择对话框（IFileOpenDialog + FOS_PICKFOLDERS），
    /// 在 Win10/11 上显示与资源管理器一致的文件夹选择界面。
    /// </summary>
    static class ModernFolderPicker
    {
        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogRCW { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            [PreserveSig] int SetFileTypeIndex(uint iFileType);
            [PreserveSig] int GetFileTypeIndex(out uint piFileType);
            [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOptions(FOS fos);
            [PreserveSig] int GetOptions(out FOS pfos);
            [PreserveSig] int SetDefaultFolder(IShellItem psi);
            [PreserveSig] int GetFolder(out IShellItem ppsi);
            [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            [PreserveSig] int AddPlace(IShellItem psi, int alignment);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            [PreserveSig] int Close(int hr);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int ClearClientData();
            [PreserveSig] int SetFilter(IntPtr pFilter);
            [PreserveSig] int GetResults(out IntPtr ppenum);
            [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        enum SIGDN : uint { FILESYSPATH = 0x80058000 }

        [Flags]
        enum FOS : uint
        {
            PICKFOLDERS = 0x20,
            FORCEFILESYSTEM = 0x40,
            PATHMUSTEXIST = 0x800,
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            out IShellItem ppv);

        /// <summary>弹出 Windows 现代文件夹选择对话框，返回选中的路径；取消返回 null。</summary>
        public static string PickFolder(IWin32Window owner, string initialPath, string title)
        {
            IFileOpenDialog dlg = null;
            try
            {
                dlg = (IFileOpenDialog)new FileOpenDialogRCW();
                dlg.SetOptions(FOS.PICKFOLDERS | FOS.FORCEFILESYSTEM | FOS.PATHMUSTEXIST);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
                {
                    IShellItem psi;
                    Guid iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out psi) == 0)
                    {
                        dlg.SetDefaultFolder(psi);
                        Marshal.ReleaseComObject(psi);
                    }
                }
                if (dlg.Show(owner.Handle) == 0)
                {
                    string path;
                    if (dlg.GetFileName(out path) == 0 && !string.IsNullOrEmpty(path))
                        return path;
                }
            }
            catch { }
            finally
            {
                if (dlg != null) Marshal.ReleaseComObject(dlg);
            }
            return null;
        }
    }
}
