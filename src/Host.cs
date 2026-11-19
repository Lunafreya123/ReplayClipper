// Host.cs —— 程序自己的窗口。
//
// 没有系统标题栏：整个窗口都是界面（用系统自带的 WebView2 显示 ui\index.html），
// 最小化 / 最大化 / 关闭三个键画在页面右上角，点了以后发消息到这里执行。
// 这样窗口从上到下是同一种颜色，任务栏上显示的也是程序自己的图标。
//
// 用不了的时候（缺少旁边的三个 WebView2 文件，或系统里没有 WebView2 运行时），
// Program.cs 会退回到用 Edge 的应用窗口打开界面。

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ReplayClipper
{
    static partial class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern uint GetDpiForSystem();
        [DllImport("user32.dll")]
        static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")]
        static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ReleaseCapture();
        [DllImport("user32.dll")]
        static extern IntPtr SendMessageW(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hWnd, int attr, ref int value, int size);
        [StructLayout(LayoutKind.Sequential)]
        struct MARGINS { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hWnd, ref MARGINS margins);
        [DllImport("dwmapi.dll")]
        static extern int DwmFlush();

        // ---- 系统的"选择文件夹"对话框（和资源管理器里的是同一个），见 HostWindow.PickFolder

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        class FileOpenDialogCom { }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr owner);
            void SetFileTypes(uint count, IntPtr types);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr events, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint options);
            void GetOptions(out uint options);
            void SetDefaultFolder(IShellItem item);
            void SetFolder(IShellItem item);
            void GetFolder(out IShellItem item);
            void GetCurrentSelection(out IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string label);
            void GetResult(out IShellItem item);
            void AddPlace(IShellItem item, int alignment);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string extension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr filter);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItem
        {
            void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);
            void GetParent(out IShellItem parent);
            void GetDisplayName(uint form, [MarshalAs(UnmanagedType.LPWStr)] out string name);
            void GetAttributes(uint mask, out uint attributes);
            void Compare(IShellItem other, uint hint, out int order);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

        // 让窗口按显示器的真实像素绘制（否则在 150% 缩放下整个界面会被系统放大而发虚）。要在创建任何窗口之前调用。
        static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; } catch { }      // 每显示器 V2
            try { SetProcessDPIAware(); } catch { }
        }

        // 打开自己的窗口并一直运行到它关闭。返回 false 表示这种方式用不了。
        static bool RunHostWindow(string url)
        {
            string version = null;
            try { version = CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { }
            if (string.IsNullOrEmpty(version))
            {
                Log("WebView2 runtime not installed");
                return false;
            }
            using (var form = new HostWindow(url))
            {
                // 托盘、快捷键、再次启动的另一份程序，都通过这几个入口来显示窗口、通知页面、结束程序（见 Background.cs）
                Action<Action> onUi = delegate(Action a) { try { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(a); } catch { } };
                ShowWindowHandler = delegate { onUi(form.ShowFromTray); };
                HideWindowHandler = delegate { onUi(form.HideToTray); };
                FolderPicker = delegate(string initial, string title)
                {
                    if (!form.IsHandleCreated || form.IsDisposed) return null;
                    return (string)form.Invoke(new Func<string>(delegate { return form.PickFolder(initial, title); }));
                };
                ExitHandler = delegate { onUi(form.CloseForReal); };
                PageNotifier = delegate(string message) { onUi(delegate { form.PostToPage(message); }); };
                try
                {
                    Application.Run(form);
                }
                finally
                {
                    ShowWindowHandler = null;
                    HideWindowHandler = null;
                    FolderPicker = null;
                    ExitHandler = null;
                    PageNotifier = null;
                }
                return form.Started;
            }
        }

        // ---- 「识别」：在每台显示器的左下角显示它的编号，两三秒后自动消失。
        // 录制设置里的示意图用的就是这些编号，对一下就知道哪个方块是哪块屏。

        static int IdentifyBusy;

        static void IdentifyMonitors()
        {
            if (Interlocked.Exchange(ref IdentifyBusy, 1) == 1) return;      // 上一次的还没消失
            List<MonitorInfo> monitors;
            try { monitors = ListMonitors(false); }
            catch { Interlocked.Exchange(ref IdentifyBusy, 0); throw; }

            // 这些小窗口需要自己的消息循环，单独开一个线程
            var t = new Thread(delegate()
            {
                try
                {
                    var badges = new List<Form>();
                    foreach (MonitorInfo m in monitors) badges.Add(new IdentifyBadge(m));
                    var timer = new System.Windows.Forms.Timer();
                    timer.Interval = 2500;
                    timer.Tick += delegate
                    {
                        timer.Stop();
                        foreach (Form f in badges) { try { f.Close(); } catch { } }
                        Application.ExitThread();
                    };
                    foreach (Form f in badges) f.Show();
                    timer.Start();
                    Application.Run();
                }
                catch (Exception ex) { Log("IdentifyMonitors: " + ex.Message); }
                finally { Interlocked.Exchange(ref IdentifyBusy, 0); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        class IdentifyBadge : Form
        {
            readonly string number;
            readonly Rectangle target;

            public IdentifyBadge(MonitorInfo m)
            {
                number = m.Number.ToString();
                int shortSide = Math.Min(m.W, m.H);
                int side = Math.Max(120, shortSide / 6), margin = Math.Max(24, shortSide / 18);
                target = new Rectangle(m.X + margin, m.Y + m.H - margin - side, side, side);

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                TopMost = true;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = Color.FromArgb(0x20, 0x20, 0x20);
                Opacity = 0.96;
                Bounds = target;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            }

            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    // 不抢焦点、不进任务栏和 Alt+Tab、鼠标点击穿透、保持在最上面
                    cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020 | 0x00000008;
                    return cp;
                }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                // 落在缩放比例不同的显示器上时，系统可能会改窗口大小：按实际像素再摆一次
                SetWindowPos(Handle, new IntPtr(-1), target.X, target.Y, target.Width, target.Height, 0x0010);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                Rectangle r = ClientRectangle;
                float line = Math.Max(2f, r.Width / 48f);
                using (var pen = new Pen(Color.FromArgb(0x60, 0xcd, 0xff), line))
                    g.DrawRectangle(pen, line / 2, line / 2, r.Width - line, r.Height - line);
                using (var font = new Font("Segoe UI", r.Height * 0.5f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat())
                {
                    format.Alignment = StringAlignment.Center;
                    format.LineAlignment = StringAlignment.Center;
                    g.DrawString(number, font, Brushes.White, new RectangleF(0, 0, r.Width, r.Height), format);
                }
            }
        }

        class HostWindow : Form
        {
            const int WM_NCCALCSIZE = 0x0083, WM_SYSCOMMAND = 0x0112, WM_WINDOWPOSCHANGED = 0x0047;
            Rectangle normalBounds;      // 窗口在"正常"状态（非最大化、非最小化）下的位置和大小
            bool restoring;              // 正在从最大化 / 最小化变回正常
            bool wasSpecial;             // 上一次位置变化后，窗口是否处于最大化或最小化

            [StructLayout(LayoutKind.Sequential)]
            struct RECT { public int Left, Top, Right, Bottom; }

            readonly string url;
            readonly WebView2 web = new WebView2();
            readonly System.Windows.Forms.Timer idleTimer = new System.Windows.Forms.Timer();
            bool lastMaximized;
            bool reallyClose;            // 这次关闭是真的要结束，而不是缩到托盘
            bool stayHidden = StartInBackground;      // 开机自启动的那一次：窗口先不出现，页面也等到第一次打开窗口时才加载
            public bool Started;

            public HostWindow(string url)
            {
                this.url = url;
                Started = stayHidden;      // 还没试着加载页面：先当它没问题
                Text = AppName;
                BackColor = Color.FromArgb(0x20, 0x20, 0x20);
                AutoScaleMode = AutoScaleMode.None;
                StartPosition = FormStartPosition.Manual;
                try
                {
                    using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                        if (s != null) Icon = new Icon(s);
                }
                catch { }

                // 尺寸按"缩放前"的大小来定，再乘上系统缩放比例（150% 缩放时就是 1.5）
                float scale = 1f;
                try { scale = Math.Max(1f, GetDpiForSystem() / 96f); } catch { }
                MinimumSize = new Size((int)(960 * scale), (int)(620 * scale));
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                int w = Math.Min((int)(1500 * scale), wa.Width - 40), h = Math.Min((int)(940 * scale), wa.Height - 40);
                Bounds = new Rectangle(wa.Left + (wa.Width - w) / 2, wa.Top + (wa.Height - h) / 2, w, h);
                RestorePlacement();

                web.Dock = DockStyle.Fill;
                web.DefaultBackgroundColor = BackColor;
                Controls.Add(web);

                Load += OnLoadAsync;
                Resize += delegate { NotifyState(); };
                FormClosing += delegate(object s, FormClosingEventArgs e)
                {
                    // 用户关窗口（Alt+F4、任务栏上的"关闭窗口"）：按设置来——问一句、留在后台，或者退出（见 UserClose）。
                    // 其余情况——托盘菜单的「退出」、系统关机或注销、别的程序发来关闭消息——直接结束，不问。
                    if (e.CloseReason == CloseReason.UserClosing && !reallyClose)
                    {
                        e.Cancel = true;
                        UserClose();
                        return;
                    }
                    SavePlacement();
                };
                idleTimer.Interval = 5000;
                idleTimer.Tick += delegate { idleTimer.Stop(); SuspendPage(); };
            }

            public void CloseForReal()
            {
                reallyClose = true;
                Close();
            }

            // 用户点了关闭。设置里的"关闭窗口时"：tray 留在后台；exit 退出程序；ask 让页面弹出对话框问一句
            // （用户选好以后页面再发 /api/close 过来）。页面还没准备好、问不了的时候按留在后台处理。
            void UserClose()
            {
                string action = SettingCloseAction;
                if (action == "exit") { RequestExit(true); return; }
                if (action == "ask" && Visible && web.CoreWebView2 != null && Started)
                {
                    if (IsIconic(Handle)) ShowWindow(Handle, 9);      // 窗口最小化着：先还原，对话框才看得见
                    Activate();
                    PostToPage("askclose");
                    return;
                }
                HideToTray();
            }

            // 窗口藏起来，程序留在托盘里（录制和全局快捷键照常）
            public void HideToTray()
            {
                if (!Visible) return;
                SavePlacement();
                PostToPage("hidden");      // 页面收到后会停下正在播放的视频
                // 先单独把页面那一层设为不可见，再藏窗口：只藏窗口的话它并不知道自己看不见了，
                // 页面会照常定时刷新，之后也没法让它暂停
                web.Visible = false;
                Hide();
                idleTimer.Start();
            }

            // 开机自启动时主循环一开始就会要显示窗口：这一次不显示，只把窗口建出来（别的线程要靠它传话）
            protected override void SetVisibleCore(bool value)
            {
                if (value && stayHidden)
                {
                    if (!IsHandleCreated) CreateHandle();
                    value = false;
                }
                base.SetVisibleCore(value);
            }

            // 从托盘图标、托盘菜单，或再次启动程序时：把窗口显示出来并切到前台
            public void ShowFromTray()
            {
                stayHidden = false;
                idleTimer.Stop();
                try
                {
                    if (web.CoreWebView2 != null)
                    {
                        web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                        if (web.CoreWebView2.IsSuspended) web.CoreWebView2.Resume();
                    }
                }
                catch { }
                if (!Visible) Show();
                web.Visible = true;
                if (IsIconic(Handle)) ShowWindow(Handle, 9);      // SW_RESTORE
                Activate();
                SetForegroundWindow(Handle);
            }

            // 「浏览…」：打开系统的选择文件夹对话框，停在 initial（不存在就取它上面最近的一层）。返回选中的文件夹，取消则返回 null。
            // 对话框归属于本窗口，开着的时候窗口点不了。
            public string PickFolder(string initial, string title)
            {
                var dialog = (IFileDialog)new FileOpenDialogCom();
                try
                {
                    uint options;
                    dialog.GetOptions(out options);
                    dialog.SetOptions(options | 0x20 | 0x40 | 0x800);      // 选文件夹；只要真实的文件夹；必须存在
                    if (!string.IsNullOrEmpty(title)) dialog.SetTitle(title);
                    try
                    {
                        string start = (initial ?? "").Trim();
                        while (start.Length > 0 && !Directory.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
                        if (start.Length > 0)
                        {
                            Guid iid = typeof(IShellItem).GUID;
                            IShellItem item;
                            SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out item);
                            dialog.SetFolder(item);
                        }
                    }
                    catch { }
                    if (dialog.Show(Handle) != 0) return null;
                    IShellItem picked;
                    dialog.GetResult(out picked);
                    string path;
                    picked.GetDisplayName(0x80058000, out path);      // 文件系统里的完整路径
                    return path;
                }
                catch (Exception ex)
                {
                    Log("PickFolder: " + ex.Message);
                    return null;
                }
                finally { Marshal.FinalReleaseComObject(dialog); }
            }

            // 窗口藏起来一会儿之后：让页面停下来、把占用的内存尽量还给系统；再显示时自动恢复
            async void SuspendPage()
            {
                try
                {
                    if (Visible || web.CoreWebView2 == null) return;
                    web.CoreWebView2.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    bool suspended = await web.CoreWebView2.TrySuspendAsync();
                    Log("window hidden, page " + (suspended ? "suspended" : "not suspended"));
                }
                catch (Exception ex) { Log("SuspendPage: " + ex.Message); }
            }

            public void PostToPage(string message)
            {
                try { if (web.CoreWebView2 != null && !web.CoreWebView2.IsSuspended) web.CoreWebView2.PostWebMessageAsString(message); } catch { }
            }

            // 上次关闭时的位置和大小。
            // 上次所在的显示器可能已经拔掉或换了排布：窗口顶上那一条（用来拖动窗口、放着关闭键）
            // 必须有足够一段落在现有的某块屏幕里，否则窗口就够不着了，这时改用默认位置。
            void RestorePlacement()
            {
                try
                {
                    string[] p = (SettingWindow ?? "").Split(',');
                    if (p.Length != 5) return;
                    var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                    if (r.Width < MinimumSize.Width || r.Height < MinimumSize.Height) return;
                    var topStrip = new Rectangle(r.X, r.Y, r.Width, 40);
                    bool visible = false;
                    foreach (Screen s in Screen.AllScreens)
                    {
                        Rectangle hit = Rectangle.Intersect(s.WorkingArea, topStrip);
                        if (hit.Width > 240 && hit.Height >= 32) visible = true;
                    }
                    if (!visible) return;
                    Bounds = r;
                    normalBounds = r;
                    if (p[4] == "1") WindowState = FormWindowState.Maximized;
                }
                catch { }
            }

            void SavePlacement()
            {
                try
                {
                    Rectangle b = WindowState == FormWindowState.Normal ? Bounds : normalBounds;
                    if (b.Width <= 0 || b.Height <= 0) return;
                    SettingWindow = b.X + "," + b.Y + "," + b.Width + "," + b.Height + "," + (WindowState == FormWindowState.Maximized ? "1" : "0");
                    SaveSettings();
                }
                catch { }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try
                {
                    int on = 1, color = 0x00202020;                        // 0x00BBGGRR，和界面底色一致
                    DwmSetWindowAttribute(Handle, 20, ref on, 4);          // 深色窗口框
                    DwmSetWindowAttribute(Handle, 34, ref color, 4);       // 边框颜色
                    // 标题栏颜色。拖边把窗口拉大时，系统先把窗口框画到新的大小，网页内容要晚一帧才跟上；
                    // 多出来的那一条在这一帧里是透的，能看到窗口后面的东西，边上就一闪一闪。
                    // 让系统把"标题栏"铺满整个窗口，这一条就由系统用上面的颜色同步填上。
                    // 窗口自己的底色随之改成纯黑：在铺满的标题栏上纯黑等于不画，别的颜色会和它叠加而变亮。
                    // （Windows 10 不能指定标题栏颜色，那里保持原样。）
                    if (DwmSetWindowAttribute(Handle, 35, ref color, 4) == 0)
                    {
                        var all = new MARGINS();
                        all.Left = all.Right = all.Top = all.Bottom = -1;
                        if (DwmExtendFrameIntoClientArea(Handle, ref all) == 0) BackColor = Color.Black;
                    }
                }
                catch { }
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                // 让系统按下面 WndProc 里的规则重新计算窗口框
                SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
            }

            async void OnLoadAsync(object sender, EventArgs e)
            {
                try
                {
                    string args = "--autoplay-policy=no-user-gesture-required";
                    string dbg = Environment.GetEnvironmentVariable("REPLAYCLIP_DEBUG_PORT");
                    if (!string.IsNullOrEmpty(dbg)) args += " --remote-debugging-port=" + dbg + " --remote-allow-origins=*";
                    var options = new CoreWebView2EnvironmentOptions(args);
                    CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "webview"), options);
                    await web.EnsureCoreWebView2Async(env);

                    CoreWebView2Settings st = web.CoreWebView2.Settings;
                    st.AreDefaultContextMenusEnabled = false;
                    st.IsStatusBarEnabled = false;
                    st.IsZoomControlEnabled = false;
                    st.AreBrowserAcceleratorKeysEnabled = false;      // 不要 F5 刷新、Ctrl+F 查找这些浏览器快捷键
                    st.AreDevToolsEnabled = !string.IsNullOrEmpty(dbg);
                    st.IsPasswordAutosaveEnabled = false;
                    st.IsGeneralAutofillEnabled = false;
                    st.IsSwipeNavigationEnabled = false;

                    // 页面顶部那一条可以像标题栏一样拖动窗口、双击最大化。新版运行时自己支持；
                    // 老版本不支持时，页面会改为发 "drag" 消息让这里来移动窗口。
                    bool nativeDrag = false;
                    try { st.IsNonClientRegionSupportEnabled = true; nativeDrag = true; } catch { }

                    await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
                        "window.__host={frameless:true,nativeDrag:" + (nativeDrag ? "true" : "false")
                        + ",maximized:" + (WindowState == FormWindowState.Maximized ? "true" : "false") + "};");
                    lastMaximized = WindowState == FormWindowState.Maximized;

                    web.CoreWebView2.WebMessageReceived += OnWebMessage;
                    web.CoreWebView2.NewWindowRequested += delegate(object s, CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; };
                    web.CoreWebView2.ProcessFailed += delegate(object s, CoreWebView2ProcessFailedEventArgs a) { Log("WebView2 process failed: " + a.ProcessFailedKind); };
                    // 页面每次重新载入（比如换了语言）后，再告诉它一次窗口现在是不是最大化：上面那段脚本里写的是程序启动时的状态
                    web.CoreWebView2.NavigationCompleted += delegate
                    {
                        lastMaximized = WindowState != FormWindowState.Maximized;
                        NotifyState();
                    };
                    web.CoreWebView2.Navigate(url);
                    Started = true;
                }
                catch (Exception ex)
                {
                    Log("WebView2 init failed: " + ex);
                    Started = false;
                    CloseForReal();
                }
            }

            // 页面右上角三个键（以及老版本下的拖动）发来的消息
            void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                string msg;
                try { msg = e.TryGetWebMessageAsString(); } catch { return; }
                switch (msg)
                {
                    case "min":
                        WindowState = FormWindowState.Minimized;
                        break;
                    case "max":
                        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                        break;
                    case "close":
                        UserClose();
                        break;
                    case "drag":
                        ReleaseCapture();
                        SendMessageW(Handle, WM_SYSCOMMAND, new IntPtr(0xF012), IntPtr.Zero);     // 开始拖动窗口
                        break;
                }
            }

            // 最大化 / 还原后告诉页面，好让它换一下那个按钮的图案
            void NotifyState()
            {
                bool max = WindowState == FormWindowState.Maximized;
                if (max == lastMaximized) return;
                lastMaximized = max;
                try { if (web.CoreWebView2 != null) web.CoreWebView2.PostWebMessageAsString(max ? "maximized" : "normal"); } catch { }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
                {
                    // 去掉系统标题栏：让界面从窗口最顶上开始。左、右、下三边仍用系统的边框，所以照常可以拖边缩放。
                    var before = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    base.WndProc(ref m);
                    var after = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                    after.Top = before.Top;
                    if (IsZoomed(Handle))
                    {
                        // 最大化时窗口四周会伸出屏幕一圈边框的宽度，顶上要让出来，否则最上面几像素看不到
                        uint dpi = GetDpiForWindow(Handle);
                        after.Top += GetSystemMetricsForDpi(33, dpi) + GetSystemMetricsForDpi(92, dpi);
                    }
                    Marshal.StructureToPtr(after, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                if (m.Msg == WM_WINDOWPOSCHANGED)
                {
                    // 从最大化 / 最小化还原时，系统已经把窗口摆到了该在的位置和大小，WinForms 却还要按
                    // "还有一条系统标题栏"再设一次，结果每还原一次窗口就高出一截。还原的这一步里不让它改
                    // （见下面的 SetBoundsCore），窗口就停在系统摆好的地方——从最大化拖下来、贴边时位置是新的，也照此办理。
                    // （是否最大化 / 最小化直接问系统：WinForms 自己的 WindowState 在这条消息到来前就已经改过了。）
                    if (restoring)
                    {
                        base.WndProc(ref m);     // 还原过程中引发的消息，不算数
                        return;
                    }
                    bool special = IsZoomed(Handle) || IsIconic(Handle);
                    restoring = wasSpecial && !special;
                    // 还原有一段缩小的过渡动画，系统要先留下窗口原来的样子。等它合成完这一帧再往下走（下面一排版，
                    // 网页那一层就缩小了），否则有时留下的是"内容已经缩小"的样子：动画一开始，新大小以外的内容就突然没了。
                    if (restoring) { try { DwmFlush(); } catch { } }
                    try { base.WndProc(ref m); }
                    finally { restoring = false; }
                    if (!special) normalBounds = Bounds;
                    wasSpecial = special;
                    return;
                }
                base.WndProc(ref m);
            }

            // 还原的那一步里 WinForms 想设的位置和大小不对（见上）：换成窗口此刻实际的位置和大小，等于不动
            protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
            {
                if (restoring)
                {
                    Rectangle now = Bounds;
                    x = now.X; y = now.Y; width = now.Width; height = now.Height;
                }
                base.SetBoundsCore(x, y, width, height, specified);
            }
        }
    }
}
