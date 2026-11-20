// Replay Clipper
// 后台持续录制屏幕，随时把最近的一段画面存成片段；列出保存的片段，在窗口里播放、拖动选择范围，
// 用 ffmpeg 无损裁出更短的一段。
//
// 结构：本程序在 127.0.0.1 上开一个只供自己用的小服务，界面是 ui\index.html，
// 显示在程序自己的窗口里（Host.cs），用不了时改用 Edge/Chrome 的"应用窗口"。
// 窗口关掉后程序留在托盘里，全局快捷键照常可用（Background.cs）；从托盘菜单退出。
// 录制由单独的辅助进程完成，本程序这一侧的逻辑在 Recorder.cs。
//
// 整个文件夹可以直接拷给别人用，自带的东西：
//   ffmpeg\ffmpeg.exe   读时长、取缩略图、拼接和导出片段
//   engine\             录制组件（ReplayClipperEngine.exe 和它需要的文件）
//
// 编译见 build.cmd（用系统自带的 .NET Framework 编译器，C# 5 语法）。

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ReplayClipper
{
    class ProbeInfo
    {
        public bool Ok;
        public double Duration;
        public int Width, Height;
        public double Fps;
        public string VCodec = "";
    }

    class ProcResult
    {
        public int ExitCode = -1;
        public byte[] Stdout = new byte[0];
        public string Stderr = "";
        public bool TimedOut;
    }

    class ApiError : Exception
    {
        public int Status;
        public object Details;      // 需要逐项说明时（比如哪一行的快捷键不行），随错误一起交给页面
        public ApiError(int status, string message) : base(message) { Status = status; }
    }

    static partial class Program
    {
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly string[] VideoExts = { ".mp4", ".mov", ".m4v", ".mkv" };

        static readonly Regex ReDuration = new Regex(@"Duration:\s*(\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);
        static readonly Regex ReVideo = new Regex(@"Stream #\d+:\d+[^\n]*?: Video: (\w+)[^\n]*?[ ,](\d{2,5})x(\d{2,5})[^\n]*", RegexOptions.Compiled);
        static readonly Regex ReFps = new Regex(@"([\d.]+)\s*fps", RegexOptions.Compiled);

        static string AppDir, DataDir, Folder, Ffmpeg, Token;
        static int Port;
        static HttpListener Listener;
        static long LastActivityTicks = DateTime.UtcNow.Ticks;

        static readonly object LogLock = new object();
        static readonly ConcurrentDictionary<string, ProbeInfo> ProbeCache = new ConcurrentDictionary<string, ProbeInfo>();
        static readonly ConcurrentDictionary<string, bool> Probing = new ConcurrentDictionary<string, bool>();
        static readonly ConcurrentDictionary<string, byte[]> ThumbCache = new ConcurrentDictionary<string, byte[]>();
        static readonly SemaphoreSlim ThumbSem = new SemaphoreSlim(6);
        static readonly SemaphoreSlim ProbeSem = new SemaphoreSlim(3);
        static readonly SemaphoreSlim SaveSem = new SemaphoreSlim(1);

        // 程序的名字：界面、窗口标题、「已安装的应用」、默认的保存文件夹都用它
        const string AppName = "Replay Clipper";

        // 开机时自动启动的那一次（命令行带 --background）：不开窗口，只留托盘图标
        static bool StartInBackground;

        // 界面语言："zh" 或 "en"。设置里没选过时跟随系统的显示语言。
        static string SettingLang;
        static string Lang = SystemLang();

        static string SystemLang()
        {
            try { return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh" : "en"; }
            catch { return "en"; }
        }

        // 按当前语言二选一
        static string Tr(string zh, string en) { return Lang == "en" ? en : zh; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        static string InstanceFile { get { return Path.Combine(DataDir, "instance.txt"); } }

        // ------------------------------------------------------------------ 启动

        // 程序自己的数据（设置、日志、窗口组件的缓存）放在哪里。
        // 默认放在程序文件夹里的 data\，不往系统盘写；只有程序文件夹不可写时
        // （比如被放进了 Program Files）才退回到 %LOCALAPPDATA%\ReplayClipper。
        static string ChooseDataDir()
        {
            string local = Path.Combine(AppDir, "data");
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReplayClipper");
            try
            {
                Directory.CreateDirectory(local);
                string probe = Path.Combine(local, ".write-test");
                File.WriteAllText(probe, "");
                File.Delete(probe);

                // 以前的版本把设置放在系统盘：第一次换到新位置时带过来
                string oldSettings = Path.Combine(legacy, "settings.json"), newSettings = Path.Combine(local, "settings.json");
                if (!File.Exists(newSettings) && File.Exists(oldSettings)) File.Copy(oldSettings, newSettings);
                return local;
            }
            catch
            {
                return legacy;
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            EnableDpiAwareness();
            AppDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string dataOverride = Environment.GetEnvironmentVariable("REPLAYCLIP_DATA");
            // 卸载的第二步：从临时文件夹里的副本运行，原文件夹的位置在参数里（见 Uninstall.cs）
            if (args.Length > 0 && args[0] == "--uninstall-stage2") return UninstallStage2(args);

            // 让从这个文件夹启动的程序真正退出（它平时关了窗口也留在托盘里），录制组件也一并结束：ReplayClipper.exe --quit
            // 给安装程序这类需要替换文件的场合用。
            if (args.Length > 0 && args[0] == "--quit")
            {
                NoLog = true;
                CloseOtherInstances();
                StopEngineHelpers(AppDir);
                return 0;
            }

            DataDir = !string.IsNullOrEmpty(dataOverride) ? dataOverride : ChooseDataDir();
            Directory.CreateDirectory(DataDir);

            // 卸载的第一步：Uninstall.exe 就是用这个参数启动本程序
            if (args.Length > 0 && args[0] == "--uninstall") return UninstallStage1();

            // 安装程序复制完文件后调用：ReplayClipper.exe --register [zh|en]
            // 登记到「已安装的应用」（这样没打开过程序也能在那里卸载），并把安装时选的语言记为界面语言。不开窗口。
            if (args.Length > 0 && args[0] == "--register")
            {
                try
                {
                    LoadSettings();
                    if (args.Length > 1 && (args[1] == "zh" || args[1] == "en") && string.IsNullOrEmpty(SettingLang))
                    {
                        SettingLang = Lang = args[1];
                        SaveSettings();
                    }
                    RegisterInstalledApp();
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("register failed: " + ex.Message);
                    return 1;
                }
            }

            StartInBackground = args.Contains("--background");

            // 名字固定的标记：安装程序靠它知道程序正开着（开着时要先请用户关掉，否则文件换不了）
            var runningMark = new Mutex(false, "ReplayClipper.Running");
            bool created;
            using (var mutex = new Mutex(true, "Local\\ReplayClipper." + ShortHash(DataDir.ToLowerInvariant()), out created))
            {
                // 已经有一份在运行：平时把它的窗口叫出来；开机自启动的这一次什么都不用做
                if (!created) return StartInBackground ? 0 : OpenSecondWindow();
                try
                {
                    Run();
                    return 0;
                }
                catch (Exception ex)
                {
                    Log("FATAL " + ex);
                    MessageBoxW(IntPtr.Zero, Tr(AppName + " 启动失败：\n", AppName + " failed to start:\n") + ex.Message, AppName, 0x10);
                    return 1;
                }
                finally
                {
                    DisposeEngine();      // 录制组件跟着程序一起结束
                    DiscardBufferIfAsked();
                    try { File.Delete(InstanceFile); } catch { }
                    GC.KeepAlive(mutex);
                    GC.KeepAlive(runningMark);      // 一直留到程序结束
                }
            }
        }

        static void Run()
        {
            TrimLog();
            Token = RandomHex(16);
            LoadSettings();
            // 从 1.0 版升级上来的第一次启动：那时片段存在 OBS 设置的录像路径里，接着用它，以前存下的片段照常看得到
            if (SettingsFromV1)
            {
                string old = LegacySaveFolder();
                if (!string.IsNullOrEmpty(old) && Directory.Exists(old)) SettingFolder = old;
                SettingsMigrated = true;
            }
            if (SettingsMigrated) SaveSettings();
            ResolveTools();
            Folder = ResolveFolder();
            StartListener();
            Log("start port=" + Port + " folder=" + Folder + " ffmpeg=" + Ffmpeg + (StartInBackground ? " (background)" : ""));
            File.WriteAllText(InstanceFile, Port + "\n" + Token + "\n" + Process.GetCurrentProcess().Id);
            Task.Run(() => RegisterInstalledApp());
            Task.Run(() => LegacyCleanup());

            Task accept = AcceptLoop();
            string url = "http://127.0.0.1:" + Port + "/?k=" + Token;

            // 托盘图标和全局快捷键（在它们自己的线程上，见 Background.cs）。只有不开窗口的测试方式不需要。
            bool headless = Environment.GetEnvironmentVariable("REPLAYCLIP_HEADLESS") == "1";
            if (!headless)
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                StartBackground();
            }
            InitRecorder();      // 上次开着录制的话，接着录（见 Recorder.cs）

            // 首选：程序自己的无边框窗口。用不了时退回到 Edge 的应用窗口。
            if (!headless && Environment.GetEnvironmentVariable("REPLAYCLIP_NO_WEBVIEW") != "1" && TryRunHost(url))
            {
                Log("exit");
                StopBackground();
                try { Listener.Stop(); } catch { }
                GC.KeepAlive(accept);
                return;
            }

            // 开机自启动的这一次先不开窗口，等用户从托盘里打开
            Process browser = StartInBackground && !headless ? null : LaunchBrowser(url);

            if (headless)
            {
                if (browser != null) browser.WaitForExit();
                else
                {
                    Process.Start(url);
                    WaitUntilIdle(TimeSpan.FromMinutes(3));
                }
            }
            else
            {
                // 没找到 Edge/Chrome 时用默认浏览器打开
                if (browser == null && !StartInBackground) Process.Start(url);
                ShowWindowHandler = delegate
                {
                    if (FocusExistingWindow()) return;
                    if (LaunchBrowser(url) == null) Process.Start(url);
                };

                // 窗口关掉以后程序留在托盘里（快捷键要靠它），直到从托盘菜单里退出。
                // （窗口在不在要自己去找：启动的那个浏览器进程有时会把窗口交给已有的进程后立刻结束）
                bool open = false;
                DateTime lastSeen = DateTime.UtcNow;
                while (!ExitRequested.WaitOne(1000))
                {
                    DateTime now = DateTime.UtcNow;
                    if (FindToolWindow() != IntPtr.Zero) { open = true; lastSeen = now; }
                    else if (open && (now - lastSeen).TotalSeconds > 3)
                    {
                        // 浏览器窗口被关掉了。这里没法弹出对话框问，"每次询问"按留在后台处理；设置成退出的就退出
                        open = false;
                        if (SettingCloseAction == "exit") { DiscardBufferOnExit = true; break; }
                    }
                }
                ShowWindowHandler = null;
                IntPtr window = FindToolWindow();
                if (window != IntPtr.Zero) PostMessageW(window, 0x0010, IntPtr.Zero, IntPtr.Zero);     // 界面窗口还开着的话一并关掉
            }

            Log("exit");
            StopBackground();
            try { Listener.Stop(); } catch { }
            GC.KeepAlive(accept);
        }

        // 自己的窗口依赖程序旁边的三个 WebView2 文件。这里单独成一个方法：
        // 文件缺失或加载失败时异常只会在这里冒出来，被接住后改用 Edge 窗口。
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static bool TryRunHost(string url)
        {
            try
            {
                foreach (string f in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
                {
                    if (File.Exists(Path.Combine(AppDir, f))) continue;
                    Log("own window unavailable, missing " + f);
                    return false;
                }
                return RunHostWindow(url);
            }
            catch (Exception ex)
            {
                Log("own window unavailable: " + ex.Message);
                return false;
            }
        }

        static void WaitUntilIdle(TimeSpan idle)
        {
            while (DateTime.UtcNow.Ticks - Interlocked.Read(ref LastActivityTicks) < idle.Ticks)
                Thread.Sleep(1000);
        }

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hWnd);

        // 已经打开的"回放剪辑"窗口（窗口标题就是页面标题）；没有则返回 0
        static IntPtr FindToolWindow()
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                var cls = new StringBuilder(64);
                GetClassNameW(h, cls, 64);
                if (cls.ToString() != "Chrome_WidgetWin_1") return true;
                var title = new StringBuilder(128);
                GetWindowTextW(h, title, 128);
                if (title.ToString() != AppName) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!IsOurBrowser(pid)) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        static readonly HashSet<uint> OurBrowserPids = new HashSet<uint>();
        static readonly HashSet<uint> OtherBrowserPids = new HashSet<uint>();

        // 这个窗口所属的浏览器是不是用本程序的配置目录启动的。
        // 标题相同的窗口可能属于另一份"回放剪辑"（比如别处还放着一份拷贝），不能认错。
        static bool IsOurBrowser(uint pid)
        {
            lock (OurBrowserPids)
            {
                if (OurBrowserPids.Contains(pid)) return true;
                if (OtherBrowserPids.Contains(pid)) return false;
                bool ours = true;      // 查不到命令行时按"是"处理
                try
                {
                    string profile = Path.Combine(DataDir, "browser");
                    using (var s = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                    {
                        foreach (System.Management.ManagementBaseObject mo in s.Get())
                        {
                            string cl = mo["CommandLine"] as string;
                            if (cl != null) ours = cl.IndexOf(profile, StringComparison.OrdinalIgnoreCase) >= 0;
                        }
                    }
                }
                catch { }
                (ours ? OurBrowserPids : OtherBrowserPids).Add(pid);
                return ours;
            }
        }

        // 把某个进程的"回放剪辑"窗口切到前台（程序自己的窗口就属于程序进程本身）
        static bool FocusWindowOfProcess(uint ownerPid)
        {
            IntPtr found = IntPtr.Zero;
            EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!IsWindowVisible(h)) return true;
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (pid != ownerPid) return true;
                var title = new StringBuilder(128);
                GetWindowTextW(h, title, 128);
                if (title.ToString() != AppName) return true;
                found = h;
                return false;
            }, IntPtr.Zero);
            if (found == IntPtr.Zero) return false;
            if (IsIconic(found)) ShowWindow(found, 9);   // SW_RESTORE
            SetForegroundWindow(found);
            return true;
        }

        // 把已经打开的窗口切到前台
        static bool FocusExistingWindow()
        {
            IntPtr found = FindToolWindow();
            if (found == IntPtr.Zero) return false;
            if (IsIconic(found)) ShowWindow(found, 9);   // SW_RESTORE
            SetForegroundWindow(found);
            return true;
        }

        static int OpenSecondWindow()
        {
            // 已经有一个实例在运行：请它把窗口显示出来（它可能正缩在托盘里）。
            // 它不答应时（比如是不认识这个请求的旧版本）：自己把它的窗口切到前台；找不到窗口就再开一个指向它
            if (AskRunningInstanceToShow()) return 0;
            try
            {
                string[] info = File.ReadAllLines(InstanceFile);
                uint ownerPid;
                if (info.Length >= 3 && uint.TryParse(info[2].Trim(), out ownerPid) && FocusWindowOfProcess(ownerPid)) return 0;
            }
            catch { }
            if (FocusExistingWindow()) return 0;
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    string[] lines = File.ReadAllLines(InstanceFile);
                    if (lines.Length >= 2)
                    {
                        string url = "http://127.0.0.1:" + lines[0].Trim() + "/?k=" + lines[1].Trim();
                        if (LaunchBrowser(url) == null) Process.Start(url);
                        return 0;
                    }
                }
                catch { }
                Thread.Sleep(250);
            }
            return 1;
        }

        static Process LaunchBrowser(string url)
        {
            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] candidates =
            {
                Path.Combine(pf86, "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(pf, "Microsoft\\Edge\\Application\\msedge.exe"),
                Path.Combine(pf, "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(pf86, "Google\\Chrome\\Application\\chrome.exe"),
                Path.Combine(local, "Google\\Chrome\\Application\\chrome.exe"),
            };
            string exe = candidates.FirstOrDefault(File.Exists);
            if (exe == null) return null;

            // 用独立的浏览器配置目录：不受日常浏览器的扩展影响，也能准确知道窗口何时关闭
            string profile = Path.Combine(DataDir, "browser");
            var sb = new StringBuilder();
            sb.Append("--user-data-dir=\"" + profile + "\" --no-first-run --no-default-browser-check");
            sb.Append(" --disable-background-mode --autoplay-policy=no-user-gesture-required");
            // 这个窗口只是本程序的界面：不登录账号、不同步、不加载扩展。
            // （不加 msImplicitSignin 这一项，Edge 会自动用 Windows 的微软账号登录并把扩展同步进来）
            sb.Append(" --disable-sync --disable-extensions");
            sb.Append(" --disable-features=msImplicitSignin,Translate,msEdgeSidebarV2");

            string dbg = Environment.GetEnvironmentVariable("REPLAYCLIP_DEBUG_PORT");
            if (!string.IsNullOrEmpty(dbg)) sb.Append(" --remote-debugging-port=" + dbg + " --remote-allow-origins=*");

            if (Environment.GetEnvironmentVariable("REPLAYCLIP_HEADLESS") == "1")
                sb.Append(" --headless=new --window-size=1500,940 \"" + url + "\"");
            else
                sb.Append(" --window-size=1500,940 --app=\"" + url + "\"");

            try
            {
                var psi = new ProcessStartInfo(exe, sb.ToString());
                psi.UseShellExecute = false;
                return Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log("LaunchBrowser failed: " + ex.Message);
                return null;
            }
        }

        static void StartListener()
        {
            Exception last = null;
            for (int p = 47800; p < 47820; p++)
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + p + "/");
                try
                {
                    l.Start();
                    Listener = l;
                    Port = p;
                    return;
                }
                catch (HttpListenerException ex)
                {
                    last = ex;
                    try { l.Close(); } catch { }
                }
            }
            throw new Exception(Tr("无法在本机端口 47800–47819 上启动服务。", "Couldn't start the local service on ports 47800–47819. ") + (last != null ? last.Message : ""));
        }

        static void ResolveTools()
        {
            // 先用程序自带的，没有再找系统里装的
            var dirs = new List<string>();
            dirs.Add(Path.Combine(AppDir, "ffmpeg"));
            dirs.Add(AppDir);
            foreach (string d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                if (d.Trim().Length > 0) dirs.Add(d.Trim().Trim('"'));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft\\WinGet\\Links"));

            foreach (string d in dirs)
            {
                try
                {
                    string f = Path.Combine(d, "ffmpeg.exe");
                    if (File.Exists(f)) { Ffmpeg = f; return; }
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ 录像文件夹

        static bool IsVideo(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            return Array.IndexOf(VideoExts, ext) >= 0;
        }

        // 把界面传来的文件名变成完整路径；只允许录像文件夹里的视频文件
        static string SafePath(string name)
        {
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name)
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || !IsVideo(name))
                throw new ApiError(400, Tr("文件名无效。", "Invalid file name."));
            string full = Path.Combine(Folder, name);
            if (!File.Exists(full)) throw new ApiError(404, Tr("文件不存在：", "File not found: ") + name);
            return full;
        }

        static string FileKey(FileInfo fi)
        {
            return fi.Name + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
        }

        // 文件是否还在被写入（正在保存 / ffmpeg 正在导出）
        static bool IsBeingWritten(FileInfo fi)
        {
            DateTime recent = DateTime.UtcNow.AddMinutes(-10);
            if (fi.LastWriteTimeUtc < recent && fi.CreationTimeUtc < recent) return false;
            try
            {
                using (new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                return fi.Length == 0;
            }
            catch (IOException) { return true; }
            catch (UnauthorizedAccessException) { return true; }
        }

        static Dictionary<string, object> FileEntry(FileInfo fi)
        {
            string key = FileKey(fi);
            bool busy = IsBeingWritten(fi);
            ProbeInfo p;
            ProbeCache.TryGetValue(key, out p);
            if (p == null && !busy) QueueProbe(key, fi.FullName);

            var d = new Dictionary<string, object>();
            d["name"] = fi.Name;
            d["size"] = fi.Length;
            d["mtime"] = (long)(fi.LastWriteTimeUtc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
            d["v"] = ShortHash(key);
            d["busy"] = busy;
            d["probed"] = p != null;
            d["duration"] = (p != null && p.Ok) ? (object)p.Duration : null;
            d["width"] = (p != null && p.Ok) ? p.Width : 0;
            d["height"] = (p != null && p.Ok) ? p.Height : 0;
            d["fps"] = (p != null && p.Ok) ? Math.Round(p.Fps, 2) : 0;
            d["codec"] = (p != null && p.Ok) ? p.VCodec : "";
            return d;
        }

        static Dictionary<string, object> BuildState()
        {
            var files = new List<object>();
            long total = 0;
            var di = new DirectoryInfo(Folder);
            if (di.Exists)
            {
                // 正在保存时，还没写完的那个文件先不列出来，免得它在列表里闪一下又消失
                long saveStart = Interlocked.Read(ref SaveStartedTicks);
                foreach (FileInfo fi in di.GetFiles().Where(f => IsVideo(f.Name)).OrderByDescending(f => f.LastWriteTimeUtc))
                {
                    if (saveStart != 0 && fi.CreationTimeUtc.Ticks >= saveStart - TimeSpan.TicksPerSecond * 3) continue;
                    files.Add(FileEntry(fi));
                    total += fi.Length;
                }
            }

            long free = -1;
            try { free = new DriveInfo(Path.GetPathRoot(Folder)).AvailableFreeSpace; } catch { }

            var st = new Dictionary<string, object>();
            st["ok"] = true;
            st["folder"] = Folder;
            st["folderExists"] = di.Exists;
            st["files"] = files;
            st["totalSize"] = total;
            st["free"] = free;
            st["ffmpeg"] = Ffmpeg != null;
            st["rec"] = RecStateJson();
            st["ui"] = UiRequestJson();
            st["slots"] = SlotsJson(true);
            st["lang"] = Lang;
            bool saving = Interlocked.Read(ref SaveStartedTicks) != 0;
            st["saving"] = saving;
            st["savingSeconds"] = saving ? (object)SavingSeconds : null;
            double saveProgress = SaveProgressShown();      // 存了一秒以上才有
            st["saveProgress"] = saveProgress < 0 ? null : (object)saveProgress;
            st["saves"] = RecentSaves();
            st["now"] = UnixMs(DateTime.UtcNow);
            return st;
        }

        // ------------------------------------------------------------------ ffmpeg / ffprobe

        static Task<ProcResult> RunProcess(string exe, string args, int timeoutMs)
        {
            return Task.Run(() =>
            {
                var res = new ProcResult();
                var psi = new ProcessStartInfo(exe, args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardErrorEncoding = Encoding.UTF8;
                using (var p = new Process())
                {
                    p.StartInfo = psi;
                    p.Start();
                    // 低优先级运行，游戏进行中也不抢 CPU
                    try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
                    var ms = new MemoryStream();
                    Task outTask = p.StandardOutput.BaseStream.CopyToAsync(ms);
                    Task<string> errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs))
                    {
                        res.TimedOut = true;
                        try { p.Kill(); } catch { }
                        p.WaitForExit(5000);
                    }
                    try { Task.WaitAll(new Task[] { outTask, errTask }, 5000); } catch { }
                    res.ExitCode = res.TimedOut ? -1 : p.ExitCode;
                    res.Stdout = ms.ToArray();
                    res.Stderr = errTask.Status == TaskStatus.RanToCompletion ? errTask.Result : "";
                }
                return res;
            });
        }

        static double ToD(object o)
        {
            if (o == null) return 0;
            double v;
            string s = o as string;
            if (s != null) return double.TryParse(s, NumberStyles.Float, Inv, out v) ? v : 0;
            try { return Convert.ToDouble(o, Inv); } catch { return 0; }
        }

        static async Task<ProbeInfo> Probe(string full)
        {
            var info = new ProbeInfo();
            if (Ffmpeg == null) return info;
            await ProbeSem.WaitAsync();
            try
            {
                // 不指定输出文件：ffmpeg 打印完文件信息就退出（此时返回码非 0 是正常的）。
                // 这样只需要带一个 ffmpeg.exe，不用再带同样大小的 ffprobe.exe。
                ProcResult r = await RunProcess(Ffmpeg, "-hide_banner -nostdin -i \"" + full + "\"", 30000);
                string s = r.Stderr ?? "";
                Match m = ReDuration.Match(s);
                if (m.Success)
                    info.Duration = int.Parse(m.Groups[1].Value, Inv) * 3600 + int.Parse(m.Groups[2].Value, Inv) * 60
                                    + double.Parse(m.Groups[3].Value, Inv);
                Match v = ReVideo.Match(s);
                if (v.Success)
                {
                    info.VCodec = v.Groups[1].Value;
                    info.Width = int.Parse(v.Groups[2].Value, Inv);
                    info.Height = int.Parse(v.Groups[3].Value, Inv);
                    Match f = ReFps.Match(v.Value);
                    if (f.Success) info.Fps = ToD(f.Groups[1].Value);
                }
                info.Ok = info.Duration > 0;
            }
            catch (Exception ex)
            {
                Log("Probe " + full + ": " + ex.Message);
            }
            finally
            {
                ProbeSem.Release();
            }
            return info;
        }

        static void QueueProbe(string key, string full)
        {
            if (!Probing.TryAdd(key, true)) return;
            Task.Run(async () =>
            {
                try { ProbeCache[key] = await Probe(full); }
                finally { bool b; Probing.TryRemove(key, out b); }
            });
        }

        static async Task<ProbeInfo> ProbeNow(string full)
        {
            var fi = new FileInfo(full);
            string key = FileKey(fi);
            ProbeInfo p;
            if (ProbeCache.TryGetValue(key, out p) && p.Ok) return p;
            p = await Probe(full);
            ProbeCache[key] = p;
            return p;
        }

        // ------------------------------------------------------------------ HTTP

        static async Task AcceptLoop()
        {
            while (Listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await Listener.GetContextAsync(); }
                catch { break; }
                Task ignored = Task.Run(() => Handle(ctx));
            }
        }

        static async Task Handle(HttpListenerContext ctx)
        {
            try
            {
                await Route(ctx);
            }
            catch (ApiError ex)
            {
                TrySendError(ctx, ex.Status, ex.Message, ex.Details);
            }
            catch (HttpListenerException) { }
            catch (Exception ex)
            {
                Log(ctx.Request.Url.AbsolutePath + ": " + ex);
                TrySendError(ctx, 500, ex.Message);
            }
            finally
            {
                try { ctx.Response.Close(); }
                catch { try { ctx.Response.Abort(); } catch { } }
            }
        }

        static void TrySendError(HttpListenerContext ctx, int status, string message, object details = null)
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["ok"] = false;
                d["error"] = message;
                if (details != null) d["errors"] = details;
                SendJson(ctx, status, d);
            }
            catch { }
        }

        static Dictionary<string, string> ParseQuery(string query)
        {
            // 不用 Request.QueryString：它按系统默认编码解码，中文文件名会乱码
            var d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(query)) return d;
            foreach (string part in query.TrimStart('?').Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string k = eq < 0 ? part : part.Substring(0, eq);
                string v = eq < 0 ? "" : part.Substring(eq + 1);
                d[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' '));
            }
            return d;
        }

        static string Q(Dictionary<string, string> q, string key)
        {
            string v;
            return q.TryGetValue(key, out v) ? v : null;
        }

        static Dictionary<string, object> ReadBody(HttpListenerRequest req)
        {
            using (var sr = new StreamReader(req.InputStream, Encoding.UTF8))
            {
                string text = sr.ReadToEnd();
                if (text.Trim().Length == 0) return new Dictionary<string, object>();
                return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text)
                       ?? new Dictionary<string, object>();
            }
        }

        static string BodyStr(Dictionary<string, object> b, string key)
        {
            object o;
            return (b != null && b.TryGetValue(key, out o)) ? (o as string) : null;
        }

        static int IntOf(Dictionary<string, object> b, string key, int def)
        {
            object o;
            if (b == null || !b.TryGetValue(key, out o) || o == null) return def;
            return (int)ToD(o);
        }

        static void SendBytes(HttpListenerContext ctx, int status, string contentType, byte[] data, string cache)
        {
            HttpListenerResponse r = ctx.Response;
            r.StatusCode = status;
            r.ContentType = contentType;
            r.ContentLength64 = data.Length;
            r.AddHeader("Cache-Control", cache ?? "no-store");
            r.AddHeader("X-Content-Type-Options", "nosniff");
            r.OutputStream.Write(data, 0, data.Length);
        }

        static void SendJson(HttpListenerContext ctx, int status, object obj)
        {
            var js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            SendBytes(ctx, status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(js.Serialize(obj)), null);
        }

        static Dictionary<string, object> Ok()
        {
            var d = new Dictionary<string, object>();
            d["ok"] = true;
            return d;
        }

        static int FolderPickerOpen;      // 选择文件夹的对话框正开着

        static async Task Route(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string path = req.Url.AbsolutePath;
            Dictionary<string, string> q = ParseQuery(req.Url.Query);

            if (path == "/favicon.ico") { ctx.Response.StatusCode = 204; return; }

            // 只接受本机、且带着本次运行随机口令的请求
            string k = Q(q, "k") ?? req.Headers["X-Token"];
            if (!req.IsLocal || k != Token) throw new ApiError(403, "forbidden");

            Interlocked.Exchange(ref LastActivityTicks, DateTime.UtcNow.Ticks);
            bool post = req.HttpMethod == "POST";
            Dictionary<string, object> body = post ? ReadBody(req) : null;

            if (!post)
            {
                switch (path)
                {
                    case "/":
                    {
                        // 页面按 <html lang> 决定显示中文还是英文
                        string html = File.ReadAllText(Path.Combine(AppDir, "ui", "index.html"), Encoding.UTF8);
                        html = html.Replace("<html lang=\"zh-CN\"", "<html lang=\"" + (Lang == "en" ? "en" : "zh-CN") + "\"");
                        SendBytes(ctx, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), null);
                        return;
                    }
                    case "/api/ping":
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/rec/info":
                        SendJson(ctx, 200, RecInfo());
                        return;
                    case "/api/rec/encoders":
                        // 第一次问要启动录制组件，可能花几秒钟：放到后台线程上等
                        SendJson(ctx, 200, await Task.Run(() => RecEncodersInfo()));
                        return;
                    case "/api/state":
                        SendJson(ctx, 200, BuildState());
                        return;
                    case "/media":
                        await ServeMedia(ctx, SafePath(Q(q, "f")));
                        return;
                    case "/thumb":
                        await ServeThumb(ctx, q);
                        return;
                }
            }
            else
            {
                switch (path)
                {
                    case "/api/export":
                        SendJson(ctx, 200, await Export(body));
                        return;
                    case "/api/delete":
                        Recycle(SafePath(BodyStr(body, "f")));
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/reveal":
                        Process.Start("explorer.exe", "/select,\"" + SafePath(BodyStr(body, "f")) + "\"");
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/openfolder":
                        if (!Directory.Exists(Folder)) throw new ApiError(404, Tr("文件夹不存在：", "Folder not found: ") + Folder);
                        Process.Start("explorer.exe", "\"" + Folder + "\"");
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/save":
                        SendJson(ctx, 200, await SaveRecent(IntOf(body, "seconds", 0), "ui"));
                        return;
                    case "/api/slots":
                        SendJson(ctx, 200, ApplySlots(body));
                        return;
                    case "/api/hotkeys/suspend":
                    {
                        object on;
                        SuspendHotkeys(body.TryGetValue("on", out on) && on is bool && (bool)on);
                        SendJson(ctx, 200, Ok());
                        return;
                    }
                    case "/api/show":
                        // 再次启动程序时，后启动的那一份发来的：把窗口显示出来
                        ShowMainWindow();
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/settings":
                    {
                        string lang = BodyStr(body, "lang");
                        if (lang == "zh" || lang == "en") { SettingLang = lang; Lang = lang; }
                        SaveSettings();
                        BgPost(UpdateTrayState);
                        SendJson(ctx, 200, Ok());
                        return;
                    }
                    case "/api/close":
                    {
                        // "关闭窗口"对话框里点了确定
                        object remember;
                        CloseChoice(BodyStr(body, "action"), body.TryGetValue("remember", out remember) && remember is bool && (bool)remember);
                        SendJson(ctx, 200, Ok());
                        return;
                    }
                    case "/api/rec/start":
                        BeginStart();
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/rec/stop":
                        await StopRecording();
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/rec/plan":
                        SendJson(ctx, 200, RecPlanInfo(body));
                        return;
                    case "/api/rec/settings":
                        SendJson(ctx, 200, RecApply(body));
                        return;
                    case "/api/pickfolder":
                    {
                        // 「浏览…」：打开系统的选择文件夹对话框（只有程序自己的窗口能开）。用户选好或取消之前这个请求一直等着
                        Func<string, string, string> pick = FolderPicker;
                        if (pick == null) throw new ApiError(404, "not available");
                        string title = BodyStr(body, "which") == "buffer" ? Tr("选择缓存位置", "Choose the buffer location") : Tr("选择保存位置", "Choose the save location");
                        Dictionary<string, object> picked = Ok();
                        picked["path"] = null;
                        if (Interlocked.Exchange(ref FolderPickerOpen, 1) == 0)      // 一次只开一个
                        {
                            try { picked["path"] = pick(BodyStr(body, "path") ?? "", title); }
                            finally { Interlocked.Exchange(ref FolderPickerOpen, 0); }
                        }
                        SendJson(ctx, 200, picked);
                        return;
                    }
                    case "/api/rec/prompted":
                        // 第一次运行时页面已经自动打开过录制设置：记下来，以后不再自动打开
                        if (!SettingSetupPrompted) { SettingSetupPrompted = true; SaveSettings(); }
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/monitors/identify":
                        // 在每台显示器上显示它的编号，方便对上录制设置里的示意图
                        IdentifyMonitors();
                        SendJson(ctx, 200, Ok());
                        return;
                    case "/api/fake":
                        SendJson(ctx, 200, FakeControl(body));
                        return;
                }
            }
            throw new ApiError(404, "not found");
        }

        static string MimeFor(string path)
        {
            return Path.GetExtension(path).ToLowerInvariant() == ".mkv" ? "video/x-matroska" : "video/mp4";
        }

        // 支持 Range 的视频文件输出，浏览器靠它在几个 GB 的文件里随意拖动
        static async Task ServeMedia(HttpListenerContext ctx, string full)
        {
            HttpListenerResponse resp = ctx.Response;
            using (var fs = new FileStream(full, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete, 1 << 16, true))
            {
                long len = fs.Length, start = 0, end = len - 1;
                bool partial = false;
                string range = ctx.Request.Headers["Range"];
                if (!string.IsNullOrEmpty(range) && range.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = range.Substring(6).Split(',')[0].Trim();
                    int dash = spec.IndexOf('-');
                    if (dash >= 0)
                    {
                        string a = spec.Substring(0, dash), b = spec.Substring(dash + 1);
                        long av, bv;
                        if (a.Length == 0)
                        {
                            if (long.TryParse(b, out bv) && bv > 0) { start = Math.Max(0, len - bv); partial = true; }
                        }
                        else if (long.TryParse(a, out av))
                        {
                            start = av;
                            if (b.Length > 0 && long.TryParse(b, out bv)) end = Math.Min(bv, len - 1);
                            partial = true;
                        }
                    }
                    if (partial && (start >= len || start > end))
                    {
                        resp.StatusCode = 416;
                        resp.AddHeader("Content-Range", "bytes */" + len);
                        return;
                    }
                }

                long remaining = end - start + 1;
                resp.StatusCode = partial ? 206 : 200;
                resp.ContentType = MimeFor(full);
                resp.AddHeader("Accept-Ranges", "bytes");
                resp.AddHeader("Cache-Control", "no-store");
                if (partial) resp.AddHeader("Content-Range", "bytes " + start + "-" + end + "/" + len);
                resp.ContentLength64 = remaining;

                fs.Position = start;
                var buf = new byte[256 * 1024];
                try
                {
                    while (remaining > 0)
                    {
                        int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining));
                        if (n <= 0) break;
                        await resp.OutputStream.WriteAsync(buf, 0, n);
                        remaining -= n;
                    }
                }
                catch (HttpListenerException) { }   // 浏览器拖动进度时会中断旧的请求，属于正常情况
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            }
        }

        static async Task ServeThumb(HttpListenerContext ctx, Dictionary<string, string> q)
        {
            if (Ffmpeg == null) throw new ApiError(500, Tr("未找到 ffmpeg。", "ffmpeg not found."));
            string full = SafePath(Q(q, "f"));
            double t = Math.Max(0, ToD(Q(q, "t")));
            int h = (int)ToD(Q(q, "h"));
            if (h <= 0) h = 90;
            h = Math.Max(36, Math.Min(540, h));

            // a=1：要这一时刻确切的那一帧（时间轴放大以后用，相邻缩略图只隔零点几秒）。
            // 平时不需要：直接取前面最近的关键帧，只解码一帧，快得多。
            bool accurate = Q(q, "a") == "1";
            var fi = new FileInfo(full);
            string key = FileKey(fi) + "|" + t.ToString(accurate ? "F2" : "F1", Inv) + "|" + h + (accurate ? "|a" : "");
            byte[] jpg;
            if (!ThumbCache.TryGetValue(key, out jpg))
            {
                await ThumbSem.WaitAsync();
                try
                {
                    string args = "-hide_banner -loglevel error -nostdin " + (accurate ? "" : "-noaccurate_seek ") + "-ss " + t.ToString("F3", Inv)
                                  + " -i \"" + full + "\" -frames:v 1 -an -sn -vf scale=-2:" + h
                                  + " -q:v 5 -f image2pipe -c:v mjpeg pipe:1";
                    ProcResult r = await RunProcess(Ffmpeg, args, 20000);
                    jpg = r.Stdout;
                }
                finally
                {
                    ThumbSem.Release();
                }
                if (jpg != null && jpg.Length > 0)
                {
                    if (ThumbCache.Count > 800) ThumbCache.Clear();
                    ThumbCache[key] = jpg;
                }
            }
            if (jpg == null || jpg.Length == 0) throw new ApiError(404, "no frame");
            SendBytes(ctx, 200, "image/jpeg", jpg, "private, max-age=86400");
        }

        // ------------------------------------------------------------------ 导出 / 删除

        static string TimeTag(double t)
        {
            int s = (int)Math.Round(t);
            int h = s / 3600, m = s % 3600 / 60, sec = s % 60;
            return (h > 0 ? h + "h" : "") + m.ToString("00") + "m" + sec.ToString("00") + "s";
        }

        static async Task<Dictionary<string, object>> Export(Dictionary<string, object> body)
        {
            if (Ffmpeg == null) throw new ApiError(500, Tr("未找到 ffmpeg，无法导出。", "ffmpeg not found. Export is unavailable."));
            string name = BodyStr(body, "f");
            string src = SafePath(name);
            object o;
            double start = body.TryGetValue("start", out o) ? ToD(o) : 0;
            double end = body.TryGetValue("end", out o) ? ToD(o) : 0;
            bool deleteSource = body.TryGetValue("deleteSource", out o) && o is bool && (bool)o;

            ProbeInfo p = await ProbeNow(src);
            if (!p.Ok) throw new ApiError(400, Tr("无法读取视频时长，文件可能尚未写入完成。", "Couldn't read the video's duration. The file may still be being written."));
            start = Math.Max(0, Math.Min(start, p.Duration));
            end = Math.Max(0, Math.Min(end, p.Duration));
            if (end - start < 0.2) throw new ApiError(400, Tr("所选范围过短。", "The selected range is too short."));

            string ext = Path.GetExtension(name).ToLowerInvariant();
            bool mp4 = ext == ".mp4" || ext == ".mov" || ext == ".m4v";
            string stem = Path.GetFileNameWithoutExtension(name) + " (" + TimeTag(start) + "-" + TimeTag(end) + ")";
            string dstName = stem + ext;
            for (int i = 2; File.Exists(Path.Combine(Folder, dstName)); i++) dstName = stem + " " + i + ext;
            string dst = Path.Combine(Folder, dstName);

            // 不重新编码，直接复制码流：速度快、画质不变
            // 页面给的起点、终点正好压在两帧的交界上（"从这一帧起""到这一帧之前"）。两个时刻都提前 2 毫秒再交给
            // ffmpeg：起点那一帧一定算进去，终点那一帧一定不算，不会因为小数取整而差出一帧。
            const double CutBias = 0.002;
            var sb = new StringBuilder("-hide_banner -loglevel error -nostdin -y");
            if (start > 0.05) sb.Append(" -ss " + Math.Max(0, start - CutBias).ToString("F4", Inv));
            if (end < p.Duration - 0.05) sb.Append(" -to " + Math.Max(0, end - CutBias).ToString("F4", Inv));
            sb.Append(" -i \"" + src + "\" -map 0:v:0 -map 0:a? -c copy");
            if (mp4) sb.Append(" -movflags +faststart");
            sb.Append(" \"" + dst + "\"");

            ProcResult r = await RunProcess(Ffmpeg, sb.ToString(), 15 * 60 * 1000);
            var fi = new FileInfo(dst);
            if (r.ExitCode != 0 || !fi.Exists || fi.Length == 0)
            {
                try { if (fi.Exists) fi.Delete(); } catch { }
                string err = (r.Stderr ?? "").Trim();
                if (err.Length > 300) err = err.Substring(err.Length - 300);
                Log("export failed (" + r.ExitCode + "): " + sb + "\n" + err);
                throw new ApiError(500, r.TimedOut ? Tr("ffmpeg 处理超时。", "ffmpeg timed out.")
                    : (Tr("ffmpeg 错误：", "ffmpeg error: ") + (err.Length > 0 ? err : Tr("返回码 ", "exit code ") + r.ExitCode)));
            }

            ProbeInfo dp = await ProbeNow(dst);
            bool sourceDeleted = false;
            string warn = null;
            if (deleteSource)
            {
                // 先确认导出的文件读得出来、长度也对，才动原视频：ffmpeg 返回成功不等于文件完好
                if (!dp.Ok || dp.Duration < (end - start) - 1.0)
                {
                    Log("export check failed: " + dstName + " ok=" + dp.Ok + " duration=" + dp.Duration.ToString("F2", Inv) + " expected=" + (end - start).ToString("F2", Inv));
                    warn = Tr("片段已导出，但导出文件的时长与所选范围不符，原视频已保留。", "The clip was exported, but its length doesn't match the selection, so the original was kept.");
                }
                else
                {
                    try { Recycle(src); sourceDeleted = true; }
                    catch (Exception ex) { warn = Tr("片段已导出，但原视频未能移至回收站：", "The clip was exported, but the original couldn't be moved to the Recycle Bin: ") + ex.Message; }
                }
            }

            Log("export " + dstName + " " + fi.Length + " bytes");
            Dictionary<string, object> d = Ok();
            d["name"] = dstName;
            d["size"] = fi.Length;
            d["duration"] = dp.Ok ? dp.Duration : end - start;
            d["sourceDeleted"] = sourceDeleted;
            d["warn"] = warn;
            return d;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHFileOperationW(ref SHFILEOPSTRUCT op);
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400, FOF_WANTNUKEWARNING = 0x4000;

        // 移到回收站。放不进回收站的（网络位置、U 盘、超过回收站容量的大文件）Windows 会先问一句"要永久删除吗"，
        // 而不是不声不响地直接删掉——页面上说的是"可以从回收站还原"
        static void Recycle(string full)
        {
            IntPtr owner = GetForegroundWindow();       // 用户刚在本程序的窗口里点了删除：确认框跟着这个窗口
            uint pid;
            GetWindowThreadProcessId(owner, out pid);
            if (pid != (uint)Process.GetCurrentProcess().Id) owner = IntPtr.Zero;
            var op = new SHFILEOPSTRUCT();
            op.hwnd = owner;
            op.wFunc = FO_DELETE;
            op.pFrom = full + "\0";                     // 以两个 \0 结尾的列表（结尾的另一个由封送加上）
            op.fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING | FOF_SILENT | FOF_NOERRORUI);
            int rc = SHFileOperationW(ref op);
            if (op.fAnyOperationsAborted) throw new ApiError(500, Tr("删除已取消。", "Deletion was cancelled."));
            if (rc != 0 || File.Exists(full))
            {
                Log("recycle failed (0x" + rc.ToString("X") + "): " + full);
                throw new ApiError(500, Tr("文件正在使用中或无法删除，请稍后重试。", "The file is in use or can't be deleted. Try again later.") + " (0x" + rc.ToString("X") + ")");
            }
            Log("recycled " + full);
        }

        static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a.Replace('/', '\\')).TrimEnd('\\'),
                                     Path.GetFullPath(b.Replace('/', '\\')).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);

        // 文件名里的时长标记：180 -> "3min"，90 -> "90s"，7200 -> "2h"
        static string SpanLabel(int sec)
        {
            return sec % 3600 == 0 ? (sec / 3600) + "h" : sec % 60 == 0 ? (sec / 60) + "min" : sec + "s";
        }

        static long SaveStartedTicks;     // 正在保存时为开始时间，否则为 0；用来在列表里暂时隐藏还没写完的那个文件
        static int SavingSeconds;         // 正在保存的是哪一档（秒）

        // 最近几次保存的结果。按快捷键或从托盘菜单保存时页面并不知情，靠这个补上"新"标记和完成提示。
        static readonly List<object> SaveLog = new List<object>();
        static int SaveSeq;

        static long UnixMs(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }

        static List<object> RecentSaves()
        {
            lock (SaveLog) return new List<object>(SaveLog);
        }

        static void RememberSave(string source, int seconds, Dictionary<string, object> result, string error)
        {
            var d = new Dictionary<string, object>();
            d["source"] = source;      // ui / hotkey / tray
            d["seconds"] = seconds;
            d["at"] = UnixMs(DateTime.UtcNow);
            d["name"] = result != null ? result["name"] : null;
            d["duration"] = result != null ? result["duration"] : null;
            d["warn"] = result != null ? result["warn"] : null;
            d["error"] = error;
            lock (SaveLog)
            {
                d["id"] = ++SaveSeq;
                SaveLog.Add(d);
                if (SaveLog.Count > 8) SaveLog.RemoveAt(0);
            }
        }

        // 托盘菜单里有些事要在窗口里接着做（比如停止录制前先问一句）：把窗口叫出来，页面从状态里看到这个请求后照办
        static int UiRequestSeq;
        static string UiRequestCommand = "";
        static long UiRequestAt;

        static void RequestUi(string command)
        {
            UiRequestCommand = command;
            UiRequestAt = UnixMs(DateTime.UtcNow);
            Interlocked.Increment(ref UiRequestSeq);
            ShowMainWindow();
            NotifyPage("refresh");
        }

        static Dictionary<string, object> UiRequestJson()
        {
            var d = new Dictionary<string, object>();
            d["id"] = UiRequestSeq;
            d["command"] = UiRequestCommand;
            d["at"] = UiRequestAt;
            return d;
        }

        // ------------------------------------------------------------------ 设置

        static string SettingWindow;      // 窗口上次的位置和大小："x,y,宽,高,是否最大化"
        static string SettingCloseAction = "ask";      // 关闭窗口时：ask 每次询问；tray 在后台继续运行；exit 退出程序
        static bool SettingsMigrated;     // 读到的是旧版本的设置，已在内存里换成现在的样子，还没写回去
        static bool SettingsFromV1;       // 设置文件是 1.0 版留下的（那时保存位置记在 OBS 里，设置文件里没有）
        static readonly object SettingsLock = new object();

        static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }

        static void LoadSettings()
        {
            try
            {
                // 设置文件坏了（比如写的时候断了电）就用上一次的备份，不要悄悄全部回到默认值：
                // 那样开机自启动后不会录制，保存位置和快捷键也都变了
                Dictionary<string, object> d = null;
                try { d = ReadSettingsFile(SettingsFile); }
                catch (Exception ex) { Log("LoadSettings: settings.json unreadable (" + ex.Message + "), using the backup"); }
                if (d == null)
                {
                    try { d = ReadSettingsFile(SettingsFile + ".bak"); }
                    catch (Exception ex) { Log("LoadSettings: settings.json.bak unreadable: " + ex.Message); }
                }
                if (d == null) return;
                object o;
                if (d != null && d.TryGetValue("obsPath", out o)) SettingObsPath = o as string;
                if (d != null && d.TryGetValue("window", out o)) SettingWindow = o as string;
                if (d != null && d.TryGetValue("lang", out o)) SettingLang = o as string;
                if (SettingLang == "zh" || SettingLang == "en") Lang = SettingLang;
                if (d != null && d.TryGetValue("closeAction", out o) && (o as string == "tray" || o as string == "exit")) SettingCloseAction = (string)o;

                // 录制（见 Recorder.cs）：保存位置、录制设置（没有这一项 = 还没设置过）、开着还是关着
                SettingsFromV1 = d != null && !d.ContainsKey("folder");
                if (d != null && d.TryGetValue("folder", out o)) SettingFolder = o as string ?? "";
                if (d != null && d.TryGetValue("bufferFolder", out o)) SettingBufferFolder = o as string ?? "";
                if (d != null && d.TryGetValue("recording", out o) && o is Dictionary<string, object>) SettingRec = RecSettingsFrom((Dictionary<string, object>)o);
                if (d != null && d.TryGetValue("recordingOn", out o)) SettingRecOn = o is bool && (bool)o;
                if (d != null && d.TryGetValue("setupPrompted", out o)) SettingSetupPrompted = o is bool && (bool)o;
                if (d != null && d.TryGetValue("legacyCleaned", out o)) SettingLegacyCleaned = o is bool && (bool)o;

                // 保存时长和快捷键（见 Background.cs）
                List<Slot> slots = d != null && d.TryGetValue("slots", out o) ? SlotsFromSettings(o) : new List<Slot>();
                if (slots.Count == 0)
                {
                    // 以前的版本：四档固定时长，外加一个 customSeconds。它和默认的不重复时，接着作为第五档（没有快捷键）
                    slots = DefaultSlots();
                    int custom = d != null && d.TryGetValue("customSeconds", out o) ? (int)ToD(o) : 0;
                    if (custom > 0)
                    {
                        custom = Math.Max(MinSlotSeconds, Math.Min(MaxSlotSeconds, custom));
                        if (!slots.Exists(x => x.Seconds == custom))
                        {
                            var extra = new Slot();
                            extra.Seconds = custom;
                            slots.Add(extra);
                            slots.Sort(delegate(Slot a, Slot b) { return a.Seconds.CompareTo(b.Seconds); });
                        }
                    }
                    SettingsMigrated = d != null && d.ContainsKey("customSeconds");
                }
                lock (SlotLock) Slots = slots;
            }
            catch (Exception ex) { Log("LoadSettings: " + ex.Message); }
        }

        static void SaveSettings()
        {
            try
            {
                var d = new Dictionary<string, object>();
                d["slots"] = SlotsJson(false);
                d["folder"] = SettingFolder ?? "";
                d["bufferFolder"] = SettingBufferFolder ?? "";
                RecSettings rec = SettingRec;
                if (rec != null) d["recording"] = RecSettingsJson(rec);
                d["recordingOn"] = SettingRecOn;
                d["setupPrompted"] = SettingSetupPrompted;
                d["window"] = SettingWindow ?? "";
                d["lang"] = SettingLang ?? "";
                d["closeAction"] = SettingCloseAction;
                d["legacyCleaned"] = SettingLegacyCleaned;
                if (!string.IsNullOrEmpty(SettingObsPath)) d["obsPath"] = SettingObsPath;      // 1.0 版留下的，卸载时清理 OBS 里的旧配置还用得着
                string json = new JavaScriptSerializer().Serialize(d);
                lock (SettingsLock) WriteSettingsFile(json);
                SettingsMigrated = false;
                SettingsFromV1 = false;
            }
            catch (Exception ex) { Log("SaveSettings: " + ex.Message); }
        }

        // 没有这个文件时返回 null；文件是空的、全是 0 或者不是 JSON 对象时抛出异常
        static Dictionary<string, object> ReadSettingsFile(string path)
        {
            if (!File.Exists(path)) return null;
            string text = File.ReadAllText(path, Encoding.UTF8);
            if (text.Trim().Trim('\0').Length == 0) throw new InvalidDataException("the file is empty");
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(text);
            if (d == null) throw new InvalidDataException("not a JSON object");
            return d;
        }

        // 先写到临时文件并落盘，再整个换上去（原来那份留作 settings.json.bak）：写到一半断电也不会只剩一个坏文件
        static void WriteSettingsFile(string json)
        {
            string tmp = SettingsFile + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(json);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (!File.Exists(SettingsFile)) { File.Move(tmp, SettingsFile); return; }
            try { File.Replace(tmp, SettingsFile, SettingsFile + ".bak", true); }
            catch (IOException) { File.Copy(tmp, SettingsFile, true); File.Delete(tmp); }      // 换不了（某些网络盘、文件系统）：退回到直接覆盖
            catch (PlatformNotSupportedException) { File.Copy(tmp, SettingsFile, true); File.Delete(tmp); }
        }

        // ---- 显示器

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplayDevicesW(string device, uint index, ref DISPLAY_DEVICE dd, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);

        class MonitorInfo
        {
            public string Id = "";        // 交给录制组件的 monitor_id（设备接口路径）
            public string Name = "";      // 型号名；读不到则为空
            public int Number;            // 界面上的编号：按系统设备号从小到大排成 1、2、3…（「识别」时屏幕上显示的也是它）
            public int Gdi;               // 系统设备号（\\.\DISPLAY2 -> 2），中间可能有空号
            public int W, H, Hz;          // 实际像素，不受缩放影响
            public int X, Y;              // 在整个桌面里的位置，主显示器是 0,0，在它左边或上边的为负数
            public bool Primary;
        }

        // 显示器型号名：WMI 里的 InstanceName 形如 DISPLAY\ABC1234\5&1a2b3c4d&0&UID4352_0
        static Dictionary<string, string> MonitorFriendlyNames()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var s = new System.Management.ManagementObjectSearcher("root\\wmi", "SELECT InstanceName, UserFriendlyName FROM WmiMonitorID"))
                {
                    foreach (System.Management.ManagementBaseObject mo in s.Get())
                    {
                        string inst = mo["InstanceName"] as string;
                        ushort[] raw = mo["UserFriendlyName"] as ushort[];
                        if (inst == null || raw == null) continue;
                        var sb = new StringBuilder();
                        foreach (ushort c in raw) if (c != 0) sb.Append((char)c);
                        int us = inst.LastIndexOf('_');
                        map[us > 0 ? inst.Substring(0, us) : inst] = sb.ToString().Trim();
                    }
                }
            }
            catch (Exception ex) { Log("MonitorFriendlyNames: " + ex.Message); }
            return map;
        }

        // 当前接着并且在用的显示器。
        // withNames：是否顺便查型号名（要问 WMI，比较慢，只在打开配置窗口时需要）。
        static List<MonitorInfo> ListMonitors(bool withNames)
        {
            var list = new List<MonitorInfo>();

            // 仅测试用：模拟一套显示器排布，格式 "x,y,宽,高[,p];..."，带 p 的是主显示器
            string fake = Environment.GetEnvironmentVariable("REPLAYCLIP_FAKE_MONITORS");
            if (!string.IsNullOrEmpty(fake))
            {
                foreach (string part in fake.Split(';'))
                {
                    string[] f = part.Split(',');
                    if (f.Length < 4) continue;
                    var fm = new MonitorInfo();
                    fm.X = int.Parse(f[0], Inv); fm.Y = int.Parse(f[1], Inv); fm.W = int.Parse(f[2], Inv); fm.H = int.Parse(f[3], Inv);
                    fm.Primary = f.Length > 4 && f[4].Trim() == "p";
                    fm.Hz = 60;
                    fm.Gdi = fm.Number = list.Count + 1;
                    fm.Id = "FAKE#" + fm.Gdi;
                    fm.Name = "Test Display " + fm.Gdi;
                    list.Add(fm);
                }
                return list;
            }

            Dictionary<string, string> names = withNames ? MonitorFriendlyNames() : null;
            for (uint i = 0; i < 64; i++)
            {
                var ad = new DISPLAY_DEVICE();
                ad.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevicesW(null, i, ref ad, 0)) break;
                const int Attached = 0x1, PrimaryFlag = 0x4, Mirroring = 0x8;
                if ((ad.StateFlags & Attached) == 0 || (ad.StateFlags & Mirroring) != 0) continue;

                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (!EnumDisplaySettingsW(ad.DeviceName, -1, ref dm)) continue;      // -1 = 当前显示模式

                var mon = new DISPLAY_DEVICE();
                mon.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
                if (!EnumDisplayDevicesW(ad.DeviceName, 0, ref mon, 1)) continue;      // 1 = 取设备接口路径
                if (string.IsNullOrEmpty(mon.DeviceID)) continue;
                string devId = mon.DeviceID;
                if (list.Any(x => string.Equals(x.Id, devId, StringComparison.OrdinalIgnoreCase))) continue;      // 同一台不列两次

                var m = new MonitorInfo();
                m.Id = devId;
                m.W = dm.dmPelsWidth; m.H = dm.dmPelsHeight; m.X = dm.dmPositionX; m.Y = dm.dmPositionY;
                m.Hz = dm.dmDisplayFrequency;
                m.Primary = (ad.StateFlags & PrimaryFlag) != 0;
                Match num = Regex.Match(ad.DeviceName ?? "", @"(\d{1,6})$");
                m.Gdi = num.Success ? int.Parse(num.Value, Inv) : (int)i + 1;
                // \\?\DISPLAY#ABC1234#5&1a2b3c4d&0&UID4352#{guid}  ->  DISPLAY\ABC1234\5&1a2b3c4d&0&UID4352
                string[] parts = devId.Split('#');
                if (names != null && parts.Length >= 3)
                {
                    string key = parts[0].Replace("\\\\?\\", "") + "\\" + parts[1] + "\\" + parts[2];
                    string nm;
                    if (names.TryGetValue(key, out nm)) m.Name = nm;
                }
                if (m.W > 0 && m.H > 0) list.Add(m);
            }
            list.Sort(delegate(MonitorInfo a, MonitorInfo b) { return a.Gdi.CompareTo(b.Gdi); });
            for (int n = 0; n < list.Count; n++) list[n].Number = n + 1;
            return list;
        }

        // 给每两秒一次的状态查询用：不查型号名，结果留两秒
        static List<MonitorInfo> MonitorsQuick;
        static long MonitorsQuickTicks;

        static List<MonitorInfo> ConnectedMonitors()
        {
            long now = DateTime.UtcNow.Ticks;
            List<MonitorInfo> cached = MonitorsQuick;
            if (cached != null && now - Interlocked.Read(ref MonitorsQuickTicks) < TimeSpan.TicksPerSecond * 2) return cached;
            List<MonitorInfo> list;
            try { list = ListMonitors(false); }
            catch (Exception ex) { Log("ConnectedMonitors: " + ex.Message); list = new List<MonitorInfo>(); }
            MonitorsQuick = list;
            Interlocked.Exchange(ref MonitorsQuickTicks, now);
            return list;
        }

        // 默认录哪一台：主显示器
        static MonitorInfo DefaultMonitor(List<MonitorInfo> monitors)
        {
            return monitors.FirstOrDefault(m => m.Primary) ?? monitors.FirstOrDefault();
        }

        // ------------------------------------------------------------------ 登记到系统的"已安装的应用"

        const string AppVersion = "2.0.2";
        const string UninstallKeyParent = "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall";

        // 注册表里这一条的名字。环境变量仅测试用（让测试拷贝登记到另一条上，不抢正式版的）
        static string UninstallKeyName
        {
            get { return Environment.GetEnvironmentVariable("REPLAYCLIP_REG_KEY") ?? "ReplayClipper"; }
        }

        // 让本程序出现在「设置 → 应用 → 已安装的应用」里，那里的「卸载」会运行 Uninstall.exe。
        // 写在当前用户名下，不需要管理员权限。每次启动核对一遍：文件夹挪了位置会自动改成新位置。
        static void RegisterInstalledApp()
        {
            try
            {
                // 用独立数据目录跑的测试实例不登记
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_DATA"))
                    && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_REG_KEY"))) return;
                string exe = Path.Combine(AppDir, "ReplayClipper.exe"), uninstaller = Path.Combine(AppDir, "Uninstall.exe");
                if (!File.Exists(uninstaller)) return;

                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKeyParent + "\\" + UninstallKeyName))
                {
                    if (key == null) return;
                    bool moved = !SamePath((key.GetValue("InstallLocation") as string) ?? "", AppDir);
                    key.SetValue("DisplayName", AppName);
                    key.SetValue("DisplayVersion", AppVersion);
                    key.SetValue("DisplayIcon", exe + ",0");
                    key.SetValue("InstallLocation", AppDir);
                    key.SetValue("UninstallString", "\"" + uninstaller + "\"");
                    key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    if (moved || key.GetValue("InstallDate") == null)
                        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", Inv));
                    if (moved || key.GetValue("EstimatedSize") == null)
                    {
                        // 列表里显示的大小：程序文件本身，不算运行时产生的 data
                        long bytes = 0;
                        foreach (string f in Directory.GetFiles(AppDir)) bytes += new FileInfo(f).Length;
                        foreach (string d in Directory.GetDirectories(AppDir))
                        {
                            if (string.Equals(Path.GetFileName(d), "data", StringComparison.OrdinalIgnoreCase)) continue;
                            foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
                        }
                        key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, bytes / 1024), RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Log("RegisterInstalledApp: " + ex.Message);
            }
        }

        // 卸载时把上面那条登记去掉（如果登记的是别处的另一份拷贝，就不动它）
        static void UnregisterInstalledApp(string target)
        {
            try
            {
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(UninstallKeyParent, true))
                {
                    if (parent == null) return;
                    using (RegistryKey key = parent.OpenSubKey(UninstallKeyName))
                    {
                        if (key == null) return;
                        string location = key.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrEmpty(location) && !SamePath(location, target)) return;
                    }
                    parent.DeleteSubKeyTree(UninstallKeyName, false);
                }
            }
            catch { }
        }

        // ------------------------------------------------------------------ 杂项

        static string RandomHex(int bytes)
        {
            var b = new byte[bytes];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();
        }

        static string ShortHash(string s)
        {
            using (MD5 md5 = MD5.Create())
                return BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes(s)), 0, 6).Replace("-", "").ToLowerInvariant();
        }

        static void TrimLog()
        {
            try
            {
                var fi = new FileInfo(Path.Combine(DataDir, "log.txt"));
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            }
            catch { }
        }

        static void Log(string msg)
        {
            if (NoLog) return;
            try
            {
                lock (LogLock)
                    File.AppendAllText(Path.Combine(DataDir, "log.txt"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", Inv) + " " + msg + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }
    }
}
