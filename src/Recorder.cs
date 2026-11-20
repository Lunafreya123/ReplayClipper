// Recorder.cs —— 后台录制，程序这一侧。
//
// 真正录制的是一个单独的辅助进程（engine\bin\64bit\ReplayClipperEngine.exe，由 Engine.cs 里的 EngineClient 启动和看管）：
// 它把所选显示器的画面连续写成磁盘上一圈两秒一段的小文件（回放缓存），要保存时把末尾需要的那几段无损拼成一个 MP4。
//
// 这里管的是：录制设置、什么时候开始和停止、出了问题怎么告诉用户、保存、开机启动。
// 程序的其余部分只通过下面的 IEngine 和录制组件打交道：平时是 RealEngine，测试时是 FakeEngine。

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace ReplayClipper
{
    static partial class Program
    {
        // ------------------------------------------------------------------ 与录制组件之间的那一层

        class RecorderError : Exception
        {
            public string Code;      // 出错的代号：录制组件给的（encoder_failed、not_recording…）或这里自己定的（engine_missing、disk…）
            public RecorderError(string code, string message) : base(string.IsNullOrEmpty(message) ? code : message) { Code = code ?? ""; }
        }

        class RecEncoder
        {
            public string Id = "", Codec = "", Vendor = "";      // Codec: h264 / hevc / av1；Vendor: nvidia / amd / intel / software
            public bool Hardware;
        }

        class RecCandidate { public string Id = "", SettingsJson = "{}"; }

        class RecConfig
        {
            public string MonitorId = "";
            public int Width, Height, Fps;
            public List<RecCandidate> Encoders = new List<RecCandidate>();      // 按顺序试，第一个能启动的就用它
            public bool Mic;
            public int BufferSeconds;
            public string RingDir = "";
        }

        class RecStatus
        {
            public bool Recording;
            public string Encoder = "";
            public int Width, Height, Fps;
            public double BufferedSeconds;
            public string LastError = "", LastErrorMessage = "";
        }

        class RecSaved { public double Duration; public long Size; }

        interface IEngine : IDisposable
        {
            List<RecEncoder> Probe();                          // 这台电脑上能用的编码器；可能要几秒钟
            void Start(RecConfig config);                      // 真的录起来了才返回；可能要几秒钟
            void Stop();
            RecStatus Status { get; }                          // 现成的状态，不会卡住
            // seconds = 0：缓存里有多少存多少。存完才返回；progress 一路报进度（0 到 1，可以为 null），是在保存的那个线程上调的
            RecSaved SaveLast(int seconds, string destPath, Action<double> progress);
            event Action Changed;                              // 状态变了（可能在任何线程上）
        }

        const int SegmentSeconds = 2, AudioKbps = 160;

        // 真正的录制组件。和 EngineClient 那边的类型打交道的只有这一个类。
        class RealEngine : IEngine
        {
            readonly EngineClient client;      // 程序文件夹里没有录制组件时为 null
            public event Action Changed;

            public RealEngine(string engineExe, string ffmpegExe, string dataDir)
            {
                if (!File.Exists(engineExe)) return;
                client = new EngineClient(engineExe, ffmpegExe, dataDir, delegate(string line) { Log(line); });      // 每行自带 "engine: " 开头
                client.Changed += delegate { Action a = Changed; if (a != null) a(); };
            }

            EngineClient Client
            {
                get
                {
                    if (client == null) throw new RecorderError("engine_missing", "ReplayClipperEngine.exe not found");
                    return client;
                }
            }

            public List<RecEncoder> Probe()
            {
                try
                {
                    var list = new List<RecEncoder>();
                    foreach (EncoderInfo e in Client.Probe())
                    {
                        var r = new RecEncoder();
                        r.Id = e.Id ?? ""; r.Codec = e.Codec ?? ""; r.Vendor = e.Vendor ?? ""; r.Hardware = e.Hardware;
                        list.Add(r);
                    }
                    return list;
                }
                catch (EngineException ex) { throw new RecorderError(ex.Code, ex.Message); }
            }

            public void Start(RecConfig c)
            {
                var cfg = new EngineConfig();
                cfg.MonitorId = c.MonitorId;
                cfg.Width = c.Width; cfg.Height = c.Height; cfg.Fps = c.Fps;
                cfg.Cursor = true;
                cfg.Encoders = new List<EncoderChoice>();
                foreach (RecCandidate k in c.Encoders)
                {
                    var choice = new EncoderChoice();
                    choice.Id = k.Id; choice.SettingsJson = k.SettingsJson;
                    cfg.Encoders.Add(choice);
                }
                cfg.AudioBitrateKbps = AudioKbps;
                cfg.DesktopAudio = true;
                cfg.Mic = c.Mic;
                cfg.BufferSeconds = c.BufferSeconds;
                cfg.SegmentSeconds = SegmentSeconds;
                cfg.RingDir = c.RingDir;
                try { Client.Start(cfg); }
                catch (EngineException ex) { throw new RecorderError(ex.Code, ex.Message); }
            }

            public void Stop()
            {
                if (client == null) return;
                try { client.Stop(); }
                catch (EngineException ex) { throw new RecorderError(ex.Code, ex.Message); }
            }

            public RecStatus Status
            {
                get
                {
                    var r = new RecStatus();
                    if (client == null) { r.LastError = "engine_missing"; return r; }
                    EngineStatus s = client.Status;
                    if (s == null) return r;
                    r.Recording = s.Recording;
                    r.Encoder = s.Encoder ?? "";
                    r.Width = s.Width; r.Height = s.Height; r.Fps = s.Fps;
                    r.BufferedSeconds = s.BufferedSeconds;
                    r.LastError = s.LastError ?? ""; r.LastErrorMessage = s.LastErrorMessage ?? "";
                    return r;
                }
            }

            public RecSaved SaveLast(int seconds, string destPath, Action<double> progress)
            {
                try
                {
                    SaveResult s = Client.SaveLast(seconds, destPath, progress);
                    var r = new RecSaved();
                    r.Duration = s.Duration; r.Size = s.Size;
                    return r;
                }
                catch (EngineException ex) { throw new RecorderError(ex.Code, ex.Message); }
            }

            public void Dispose()
            {
                if (client != null) client.Dispose();
            }
        }

        // 仅测试用：设了 REPLAYCLIP_FAKE_ENGINE 就不启动录制组件、不录任何东西，但对外的表现和真的一样：
        // 启动要等一会儿，保存时用 ffmpeg 生成一段相应长度的测试视频。值是用逗号隔开的选项（只写 1 = 全用默认）：
        //   start=毫秒       启动耗时（默认 1200）
        //   save=毫秒        每次保存额外耗时（默认 1500），这段时间里均匀地报进度
        //   fail=代号        启动总是失败，报这个错（如 encoder_failed）
        //   encoders=a+b     能用的编码器，写成 厂商:编码，如 nvidia:h264+amd:hevc+software:h264（这也是默认值）
        //   empty            刚启动时缓存是空的（默认当作已经录满）
        // 运行中还可以用 POST /api/fake 改它的状态，见 FakeControl。
        class FakeEngine : IEngine
        {
            readonly Dictionary<string, string> opt = new Dictionary<string, string>();
            RecConfig config;
            DateTime startedUtc, downUntilUtc;
            public string FailNext = "";
            public int SaveMs = -1;                // 每次保存额外耗时；-1 = 用启动时给的
            public string FailNextSave = "";       // 下一次保存进行到六成时失败，报这个错
            public event Action Changed;

            public FakeEngine(string options)
            {
                foreach (string part in (options ?? "").Split(','))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) opt[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
                    else if (part.Trim().Length > 0) opt[part.Trim()] = "";
                }
            }

            int Ms(string key, int def)
            {
                string v;
                return opt.TryGetValue(key, out v) ? (int)ToD(v) : def;
            }

            void Notify()
            {
                Action a = Changed;
                if (a != null) a();
            }

            public List<RecEncoder> Probe()
            {
                Thread.Sleep(Math.Min(800, Ms("start", 1200)));
                string spec;
                if (!opt.TryGetValue("encoders", out spec)) spec = "nvidia:h264+amd:hevc+software:h264";
                var list = new List<RecEncoder>();
                foreach (string item in spec.Split('+'))
                {
                    string[] f = item.Split(':');
                    if (f.Length != 2) continue;
                    var e = new RecEncoder();
                    e.Vendor = f[0]; e.Codec = f[1]; e.Hardware = f[0] != "software";
                    e.Id = f[0] == "software" ? "obs_x264" : "fake_" + f[0] + "_" + f[1];
                    list.Add(e);
                }
                return list;
            }

            public void Start(RecConfig c)
            {
                if (config != null) throw new RecorderError("already_running", null);
                Thread.Sleep(Ms("start", 1200));
                string fail = FailNext;
                FailNext = "";
                if (fail.Length == 0) opt.TryGetValue("fail", out fail);
                if (!string.IsNullOrEmpty(fail)) throw new RecorderError(fail, "fake engine: " + fail);
                if (c.Encoders.Count == 0) throw new RecorderError("bad_config", "no encoder candidates");
                config = c;
                startedUtc = DateTime.UtcNow;
                Notify();
            }

            public void Stop()
            {
                config = null;
                Notify();
            }

            // 模拟"录制组件意外退出，过一会儿自己恢复"
            public void Crash(int ms)
            {
                downUntilUtc = DateTime.UtcNow.AddMilliseconds(ms);
                Notify();
                var t = new System.Threading.Timer(delegate { Notify(); }, null, ms + 50, Timeout.Infinite);
                GC.KeepAlive(t);
            }

            public RecStatus Status
            {
                get
                {
                    var r = new RecStatus();
                    RecConfig c = config;
                    if (c == null) return r;
                    if (DateTime.UtcNow < downUntilUtc) { r.LastError = "helper_exited"; return r; }
                    r.Recording = true;
                    r.Encoder = c.Encoders[0].Id;
                    r.Width = c.Width; r.Height = c.Height; r.Fps = c.Fps;
                    double elapsed = (DateTime.UtcNow - startedUtc).TotalSeconds;
                    r.BufferedSeconds = opt.ContainsKey("empty") ? Math.Min(c.BufferSeconds, elapsed) : c.BufferSeconds;
                    return r;
                }
            }

            public RecSaved SaveLast(int seconds, string destPath, Action<double> progress)
            {
                RecStatus st = Status;
                if (!st.Recording) throw new RecorderError("not_recording", null);
                if (st.BufferedSeconds < 1) throw new RecorderError("nothing_buffered", null);
                int length = (int)Math.Max(1, seconds <= 0 ? st.BufferedSeconds : Math.Min(seconds, st.BufferedSeconds));
                int total = SaveMs >= 0 ? SaveMs : Ms("save", 1500);
                string fail = FailNextSave;
                FailNextSave = "";
                for (int waited = 0; waited < total; )
                {
                    int step = Math.Min(50, total - waited);
                    Thread.Sleep(step);
                    waited += step;
                    double done = (double)waited / total;
                    if (progress != null) progress(done);
                    if (fail.Length > 0 && done >= 0.6) throw new RecorderError(fail, "fake engine: " + fail);
                }
                string temp = destPath + ".part";
                string args = "-hide_banner -loglevel error -nostdin -y -f lavfi -i testsrc=size=320x180:rate=2:duration=" + length.ToString(Inv)
                              + " -c:v libx264 -preset ultrafast -g 4 -pix_fmt yuv420p -movflags +faststart -f mp4 \"" + temp + "\"";
                ProcResult p = RunProcess(Ffmpeg, args, 10 * 60 * 1000).Result;
                if (p.ExitCode != 0 || !File.Exists(temp))
                {
                    try { File.Delete(temp); } catch { }
                    throw new RecorderError("save_failed", (p.Stderr ?? "").Trim());
                }
                File.Move(temp, destPath);
                var r = new RecSaved();
                r.Duration = length;
                r.Size = new FileInfo(destPath).Length;
                return r;
            }

            public void Dispose() { config = null; }
        }

        // ------------------------------------------------------------------ 录制设置和现在的状态

        const int MinReplaySeconds = 60, MaxReplaySeconds = 12 * 3600;
        const long LowDiskBytes = 1L << 30;      // 录着的时候，缓存所在的盘剩下不到这么多就停下来，不把盘写满

        class RecSettings
        {
            public string MonitorId = "";
            public int Cap = 1080;               // 输出画面短边的上限：1080 / 1440 / 0 = 显示器原有的分辨率
            public int Fps = 60;
            public int Seconds = 1800;           // 回放时长：缓存里保留最近多久的画面
            public string Encoder = "auto";      // auto 或某个编码器的代号
            public bool Mic;
        }

        static RecSettings SettingRec;           // null = 还没设置过
        static bool SettingRecOn;                // 录制是开着还是关着。记下来，下次启动程序时照旧
        static bool SettingSetupPrompted;        // 第一次运行时已经自动打开过一次录制设置
        static string SettingFolder = "";        // 片段的保存位置（空 = 默认）

        static IEngine Engine;
        static readonly object EngineLock = new object();
        static readonly SemaphoreSlim RecGate = new SemaphoreSlim(1);      // 开始、停止、换设置：一次只做一件
        // 进行到哪一步：off 没在录；starting 正在启动；on 已经启动（录制组件中途退出时由它自己恢复）；
        // failed 没能启动；nodisk 磁盘空间不够；nomonitor 设置里选的显示器没接着
        static string RecPhase = "off";
        static string RecErrorCode = "";
        static long RecErrorFree, RecErrorNeed;  // nodisk 时：当时可用多少、需要多少
        static RecConfig RecRunning;             // 现在这次录制用的配置
        // 下次开始录制前先把缓存清空：换了显示器、分辨率、帧率或编码器之后，新旧画面不该出现在同一个片段里。
        // （编码格式变了的话录制组件自己也会清；只换显示器、输出大小不变时它不清，所以这里统一清掉，和对话框里说的一致。）
        static volatile bool RecClearBeforeStart;
        static List<RecEncoder> EncoderCache;
        static System.Threading.Timer RecTimer;
        static int RecWatchBusy;
        static long FakeFreeBytes = -1;          // 仅测试用：假装缓存所在的盘只剩这么多（见 FakeControl）

        // 回放缓存放在哪里。默认在程序的数据文件夹里（data\buffer）；用户在录制设置里另选了位置的话，
        // 用的是那个位置下面程序自己建的子文件夹——清理、删除都只动这个子文件夹，不碰用户文件夹里别的东西。
        const string BufferFolderName = "Replay Clipper buffer";
        static string SettingBufferFolder = "";      // 用户选的位置（空 = 默认）

        static string BufferDirFor(string parent)
        {
            return string.IsNullOrWhiteSpace(parent) ? Path.Combine(DataDir, "buffer") : Path.Combine(parent.Trim(), BufferFolderName);
        }

        static string BufferDir { get { return BufferDirFor(SettingBufferFolder); } }

        // 界面上填的缓存位置：填的就是默认位置（或者没填）时记成空
        static string NormalizeBufferParent(string input)
        {
            string s = (input ?? "").Trim().Trim('"').Trim();
            if (s.Length == 0 || SamePath(s, DataDir)) return "";
            return s;
        }

        static bool IsInside(string path, string dir)
        {
            try
            {
                string a = Path.GetFullPath(path).TrimEnd('\\') + "\\", b = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
                return a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // 路径里有没有文件夹名不能用的字符
        static bool HasBadPathChars(string path)
        {
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOfAny(new[] { '*', '?' }) >= 0) return true;
            return path.Length > 2 && path.IndexOf(':', 2) >= 0;
        }

        static string BadPathCharsText { get { return Tr("路径中有不能使用的字符。", "The path contains characters that can't be used."); } }

        // 这个位置能不能放缓存。null = 可以，否则是给人看的原因。create：真的建出文件夹、写一下试试（保存设置时才这样做）
        static string BufferProblem(string parent, bool create)
        {
            if (string.IsNullOrWhiteSpace(parent)) return null;      // 默认位置
            string network = Tr("缓存不能放在网络位置。", "The buffer can't be on a network location.");
            try
            {
                if (HasBadPathChars(parent)) return BadPathCharsText;
                if (!Path.IsPathRooted(parent) || Path.GetPathRoot(parent).Length < 3)
                    return Tr("请填写完整的文件夹路径。", "Enter a full folder path.");
                string full = Path.GetFullPath(parent);
                if (full.StartsWith("\\\\", StringComparison.Ordinal)) return network;
                string root = Path.GetPathRoot(full);
                if (!Directory.Exists(root)) return Tr("找不到这个磁盘：", "This drive wasn't found: ") + root.TrimEnd('\\');
                if (new DriveInfo(root).DriveType == DriveType.Network) return network;
                if (create)
                {
                    string dir = BufferDirFor(full);
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".write-test");
                    File.WriteAllText(probe, "");
                    File.Delete(probe);
                }
                return null;
            }
            catch (Exception ex)
            {
                return Tr("无法使用该位置：", "This location can't be used: ") + ex.Message;
            }
        }

        // 片段能不能存在这里。程序自己的文件夹和缓存文件夹不行：卸载时那些会被删掉，而保存的片段是说好了不动的。
        static string FolderProblem(string folder, string bufferDir)
        {
            try
            {
                if (HasBadPathChars(folder)) return BadPathCharsText;
                if (!Path.IsPathRooted(folder)) return Tr("请填写完整的文件夹路径。", "Enter a full folder path.");
                foreach (string name in new[] { "data", "src", "engine", "ui", "ffmpeg", "obs" })
                    if (IsInside(folder, Path.Combine(AppDir, name)))
                        return Tr("片段不能保存在程序自己的文件夹里（卸载时会被删除）。请选择其他位置。",
                                  "Clips can't be saved inside the app's own folders (they are deleted on uninstall). Choose another location.");
                if (IsInside(folder, DataDir))
                    return Tr("片段不能保存在程序自己的文件夹里（卸载时会被删除）。请选择其他位置。",
                              "Clips can't be saved inside the app's own folders (they are deleted on uninstall). Choose another location.");
                if (IsInside(folder, bufferDir))
                    return Tr("片段不能保存在缓存文件夹里。请选择其他位置。", "Clips can't be saved inside the buffer folder. Choose another location.");
                return null;
            }
            catch (Exception ex)
            {
                return Tr("无法使用该保存位置：", "This save location can't be used: ") + ex.Message;
            }
        }
        static string EngineExe { get { return Path.Combine(AppDir, "engine", "bin", "64bit", "ReplayClipperEngine.exe"); } }

        static string DefaultFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), AppName); }
        }

        // 片段存在哪里：设置里指定的位置，没指定就是「视频」里的 Replay Clipper 文件夹
        static string ResolveFolder()
        {
            string env = Environment.GetEnvironmentVariable("REPLAYCLIP_FOLDER");      // 仅测试用
            if (!string.IsNullOrEmpty(env)) return env;
            return string.IsNullOrWhiteSpace(SettingFolder) ? DefaultFolder : SettingFolder;
        }

        static IEngine EnsureEngine()
        {
            lock (EngineLock)
            {
                if (Engine != null) return Engine;
                string fake = Environment.GetEnvironmentVariable("REPLAYCLIP_FAKE_ENGINE");
                IEngine e = !string.IsNullOrEmpty(fake)
                    ? (IEngine)new FakeEngine(fake)
                    : new RealEngine(EngineExe, Ffmpeg, DataDir);      // 它自己的配置和日志放在 <数据目录>\engine 里
                e.Changed += delegate { RecChanged(); };
                Engine = e;
                return e;
            }
        }

        static void DisposeEngine()
        {
            IEngine e;
            lock (EngineLock) { e = Engine; Engine = null; }
            if (e == null) return;
            try { e.Dispose(); }
            catch (Exception ex) { Log("engine dispose: " + ex.Message); }
        }

        // 用户退出程序（托盘菜单的「退出」、关窗口时选了退出、--quit、卸载）：录制停下以后把回放缓存也删掉，
        // 程序关着的时候不占着盘。最多等几秒；没删完的，下次开始录制时会清理。系统关机、注销时不删（没有时间稳妥地做）。
        static volatile bool DiscardBufferOnExit;

        static void DiscardBufferIfAsked()
        {
            if (!DiscardBufferOnExit) return;
            try
            {
                Task t = Task.Run(delegate { ClearBuffer(); });
                t.Wait(4000);
            }
            catch { }
        }

        // 程序启动时：上次是开着的就接着录
        static void InitRecorder()
        {
            RecTimer = new System.Threading.Timer(delegate { RecWatch(); }, null, 3000, 3000);
            if (SettingRec != null && SettingRecOn) BeginStart();
        }

        static void SetPhase(string phase)
        {
            RecPhase = phase;
            RecChanged();
        }

        // 状态变了：窗口开着的话让页面马上刷新，托盘上的提示也跟着改
        static void RecChanged()
        {
            NotifyPage("refresh");
            BgPost(UpdateTrayState);
        }

        // 给界面看的状态：unconfigured 还没设置；off 关着；starting；recording；restoring 录制中断、正在自己恢复；
        // error 没能启动；nodisk；nomonitor
        static string RecState()
        {
            if (SettingRec == null) return "unconfigured";
            if (!SettingRecOn) return "off";
            string phase = RecPhase;
            if (phase == "on")
            {
                IEngine e = Engine;
                return e != null && e.Status.Recording ? "recording" : "restoring";
            }
            if (phase == "failed") return "error";
            if (phase == "off") return "starting";      // 要录，只是还没轮到启动
            return phase;
        }

        static int ReplaySeconds()
        {
            RecSettings s = SettingRec;
            return s != null ? s.Seconds : 1800;
        }

        // 托盘菜单第一行、托盘图标提示里的那句话
        static string RecStatusLine(string state)
        {
            switch (state)
            {
                case "recording": return Tr("后台录制中 · 保留最近 ", "Recording in background · keeping the last ") + SpanText(ReplaySeconds());
                case "starting": return Tr("正在启动录制…", "Starting recording…");
                case "restoring": return Tr("录制已中断，正在恢复…", "Recording was interrupted. Restoring…");
                case "error": return Tr("录制未能启动", "Recording couldn't start");
                case "nodisk": return Tr("磁盘空间不足，录制已停止", "Not enough disk space. Recording stopped");
                case "nomonitor": return Tr("所选显示器未连接", "The selected display isn't connected");
                case "unconfigured": return Tr("尚未设置录制", "Recording isn't set up yet");
                default: return Tr("后台录制未开启", "Background recording is off");
            }
        }

        static string GB(long bytes)
        {
            double gb = bytes / 1073741824.0;
            return gb.ToString(gb >= 100 ? "0" : "0.#", Inv) + " GB";
        }

        // 把出错的代号换成给人看的话
        static string RecErrorText(string code)
        {
            switch (code)
            {
                case "encoder_failed":
                    return Tr("没有可用的视频编码器。请更新显卡驱动，或在录制设置中更换编码器。",
                              "No usable video encoder was found. Update the graphics driver, or choose another encoder in Recording settings.");
                case "output_failed":
                    return Tr("无法写入回放缓存：", "The replay buffer couldn't be written: ") + BufferDir;
                case "bad_config":
                    return Tr("录制设置无效。请在录制设置中重新保存。", "The recording settings are invalid. Save them again in Recording settings.");
                case "engine_missing":
                case "helper_missing":
                    return Tr("缺少录制组件。请重新安装 " + AppName + "。", "The recording component is missing. Reinstall " + AppName + ".");
                case "monitor_not_found":
                    return Tr("当前无法录到画面。请在录制设置中选择正在使用的显示器。", "Nothing can be captured right now. Choose a display that's in use in Recording settings.");
                case "disk":
                {
                    string drive = Path.GetPathRoot(BufferDir).TrimEnd('\\');
                    if (RecErrorNeed > 0)
                        return Tr(drive + " 可用 " + GB(RecErrorFree) + "，回放缓存需要约 " + GB(RecErrorNeed) + "。请清理磁盘，或在录制设置中缩短回放时长。",
                                  drive + " has " + GB(RecErrorFree) + " free, and the replay buffer needs about " + GB(RecErrorNeed) + ". Free up space, or shorten the replay length in Recording settings.");
                    return Tr(drive + " 仅剩 " + GB(RecErrorFree) + "。请清理磁盘后重试。", drive + " has only " + GB(RecErrorFree) + " left. Free up space and try again.");
                }
                case "": return "";
                default:
                    return Tr("录制组件未能启动（" + code + "）。", "The recording component couldn't start (" + code + ").");
            }
        }

        static string SaveErrorText(RecorderError ex)
        {
            switch (ex.Code)
            {
                case "not_recording": return Tr("后台录制未开启。", "Background recording is off.");
                case "nothing_buffered": return Tr("还没有可保存的画面。", "There's nothing to save yet.");
                case "busy": return Tr("上一次保存尚未完成。", "The previous save hasn't finished yet.");
                case "ffmpeg_missing": return Tr("未找到 ffmpeg，无法保存。", "ffmpeg not found. Saving is unavailable.");
                case "helper_lost":
                case "helper_hung":
                case "timeout": return Tr("录制已中断，正在恢复。请稍后重试。", "Recording was interrupted and is being restored. Try again in a moment.");
                case "engine_missing":
                case "helper_missing": return RecErrorText(ex.Code);
                default: return Tr("保存失败（" + ex.Code + "）。", "Couldn't save (" + ex.Code + ").");
            }
        }

        static Dictionary<string, object> RecStateJson()
        {
            RecSettings s = SettingRec;
            string state = RecState();
            var d = new Dictionary<string, object>();
            d["configured"] = s != null;
            d["on"] = SettingRecOn;
            d["state"] = state;
            d["seconds"] = ReplaySeconds();
            d["prompted"] = SettingSetupPrompted;
            string code = state == "error" || state == "nodisk" ? RecErrorCode : state == "nomonitor" ? "monitor_not_found" : "";
            d["errorCode"] = code;
            d["errorText"] = RecErrorText(code);

            // 显示器这边：有没有变化（插拔、改了排布），以及设置里选的那一台现在还接着没有
            List<MonitorInfo> mons = ConnectedMonitors();
            var sig = new StringBuilder();
            foreach (MonitorInfo mi in mons)
                sig.Append(mi.Id).Append('@').Append(mi.X).Append(',').Append(mi.Y).Append(',').Append(mi.W).Append('x').Append(mi.H).Append('|');
            d["monitorSig"] = ShortHash(sig.ToString());
            // 一台都没查到多半是查询本身出了问题（比如远程桌面），这时不报"没接着"
            d["monitorMissing"] = s != null && s.MonitorId.Length > 0 && mons.Count > 0
                && !mons.Any(x => string.Equals(x.Id, s.MonitorId, StringComparison.OrdinalIgnoreCase));
            return d;
        }

        // ------------------------------------------------------------------ 磁盘空间

        // 缓存所在的那个盘还剩多少
        static long BufferFreeBytes(string dir)
        {
            if (FakeFreeBytes >= 0) return FakeFreeBytes;
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch { return -1; }      // 查不到：不拦着
        }

        static long RingBytesCache;
        static long RingBytesTicks;

        // 现在用的缓存文件夹占了多少。重新开始录制时这些空间会被接着用或者腾出来，所以算在"可用"里
        static long RingBytes()
        {
            long now = DateTime.UtcNow.Ticks;
            if (now - Interlocked.Read(ref RingBytesTicks) < TimeSpan.TicksPerSecond * 5) return Interlocked.Read(ref RingBytesCache);
            long total = 0;
            try
            {
                var di = new DirectoryInfo(BufferDir);
                if (di.Exists) foreach (FileInfo f in di.EnumerateFiles()) total += f.Length;
            }
            catch { }
            Interlocked.Exchange(ref RingBytesCache, total);
            Interlocked.Exchange(ref RingBytesTicks, now);
            return total;
        }

        static void ClearBuffer()
        {
            ClearBufferAt(BufferDir);
        }

        // 删掉一个缓存文件夹（整个都是程序自己的）
        static void ClearBufferAt(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) Directory.Delete(dir, true); }
            catch (Exception ex) { Log("clear buffer: " + ex.Message); }
            Interlocked.Exchange(ref RingBytesTicks, 0);
        }

        // ------------------------------------------------------------------ 编码器和画质

        // cap = 输出画面短边的上限（1080 / 1440 / 0 表示不缩小）；保持显示器原有的宽高比，结果取偶数
        static void OutputSize(int w, int h, int cap, out int ow, out int oh)
        {
            int shortSide = Math.Min(w, h);
            if (cap <= 0 || shortSide <= cap) { ow = w; oh = h; }
            else
            {
                double k = (double)cap / shortSide;
                ow = (int)Math.Round(w * k / 2) * 2;
                oh = (int)Math.Round(h * k / 2) * 2;
            }
            ow -= ow % 2;
            oh -= oh % 2;
        }

        // 1080p60 用 16 Mbps，像素更多时按 0.75 次方增长（4K 约 45 Mbps），帧率减半时约为 0.7 倍
        static int RecommendedKbps(int w, int h, int fps)
        {
            double k = Math.Pow((double)w * h / (1920.0 * 1080.0), 0.75) * Math.Sqrt(fps / 60.0);
            int kbps = (int)Math.Round(16000 * k / 1000.0) * 1000;
            return Math.Max(4000, Math.Min(60000, kbps));
        }

        // 这台电脑上能用的编码器，按推荐的先后排好。第一次问要启动录制组件，可能花几秒钟；之后用记下来的
        static List<RecEncoder> Encoders(bool refresh)
        {
            List<RecEncoder> cached = EncoderCache;
            if (cached != null && !refresh) return cached;
            List<RecEncoder> list = EnsureEngine().Probe().Where(EncoderOffered).OrderBy(EncoderRank).ToList();
            EncoderCache = list;
            return list;
        }

        // 录制组件报上来的编码器里，哪些列给用户、哪些交给它去试。
        static bool EncoderOffered(RecEncoder e)
        {
            // 临时绕开（录制组件的问题，已反馈）：AV1 录得起来，但存不成片段——回放缓存是 MPEG-TS，ffmpeg 在里面认不出 AV1 的画面。
            // 录制组件那边解决以后，把这一行去掉即可。
            if (e.Codec != "h264" && e.Codec != "hevc") return false;
            // 软件编码只留 x264：其余的（SVT-AV1、AOM）在 1080p60 下实时编不动
            bool software = !e.Hardware || e.Vendor == "software";
            return !software || e.Id == "obs_x264";
        }

        // 推荐顺序：N 卡 H.264 > A 卡 H.265（A 卡的 H.264 画质偏弱）> A 卡 H.264 > 核显 H.264 > 其他硬件 > 软件
        static int EncoderRank(RecEncoder e)
        {
            string v = e.Vendor, c = e.Codec;
            return v == "nvidia" && c == "h264" ? 10
                 : v == "amd" && c == "hevc" ? 20
                 : v == "amd" && c == "h264" ? 30
                 : v == "intel" && c == "h264" ? 40
                 : v == "nvidia" && c == "hevc" ? 50
                 : v == "intel" && c == "hevc" ? 60
                 : !e.Hardware || v == "software" ? 90 : 70;
        }

        static string EncoderLabel(RecEncoder e)
        {
            bool software = !e.Hardware || e.Vendor == "software";
            string who = e.Vendor == "nvidia" ? Tr("NVIDIA 显卡", "NVIDIA GPU") : e.Vendor == "amd" ? Tr("AMD 显卡", "AMD GPU")
                       : e.Vendor == "intel" ? Tr("Intel 核显", "Intel graphics") : Tr("软件编码", "Software (CPU)");
            string what = e.Codec == "hevc" ? "H.265 (HEVC)" : e.Codec == "av1" ? "AV1" : software ? "x264" : "H.264";
            return who + " · " + what;
        }

        static Dictionary<string, object> EncoderSettings(RecEncoder e, int kbps)
        {
            var s = new Dictionary<string, object>();
            s["rate_control"] = "VBR";
            s["bitrate"] = kbps;
            s["keyint_sec"] = SegmentSeconds;
            if (e.Vendor == "amd") s["preset"] = "quality";
            else if (e.Vendor == "nvidia") { s["max_bitrate"] = kbps * 3 / 2; s["preset2"] = "p5"; s["preset"] = "hq"; s["multipass"] = "qres"; }
            else if (e.Vendor == "intel") { s["max_bitrate"] = kbps * 3 / 2; s["target_usage"] = "TU4"; }
            else s["preset"] = "veryfast";
            return s;
        }

        // 交给录制组件按顺序试的编码器：选定的那个在最前面（"自动"就按推荐顺序），其余的作为后备，软件编码垫底。
        // H.264 不支持超过 4096 的画面，那样的分辨率下不列它。
        static List<RecCandidate> EncoderCandidates(List<RecEncoder> usable, string chosen, int kbps, int w, int h)
        {
            var ordered = new List<RecEncoder>();
            RecEncoder first = usable.FirstOrDefault(e => e.Id == chosen);
            if (first != null) ordered.Add(first);
            foreach (RecEncoder e in usable) if (e != first) ordered.Add(e);
            var js = new JavaScriptSerializer();
            var list = new List<RecCandidate>();
            foreach (RecEncoder e in ordered)
            {
                if (e.Codec == "h264" && (w > 4096 || h > 4096)) continue;
                var k = new RecCandidate();
                k.Id = e.Id;
                k.SettingsJson = js.Serialize(EncoderSettings(e, kbps));
                list.Add(k);
            }
            return list;
        }

        // ---- 方案：按设置算出分辨率、码率、缓存要占多少磁盘

        class RecPlan
        {
            public MonitorInfo Monitor;
            public int OutW, OutH, Fps, Kbps, Seconds;
            public long NeedBytes, FreeBytes, RingBytes;
            public string BufferDir = "";      // 这份方案下缓存放在哪个文件夹
            public bool Fits { get { return FreeBytes < 0 || FreeBytes + RingBytes >= NeedBytes; } }
        }

        // strict：真正要用这份设置时。选的那台显示器不在了就报错，而不是悄悄换成另一台。
        // bufferParent：缓存位置（空 = 默认）；磁盘够不够是按它所在的盘算的。
        static RecPlan BuildRecPlan(RecSettings s, bool strict, string bufferParent)
        {
            var plan = new RecPlan();
            plan.BufferDir = BufferDirFor(bufferParent);
            List<MonitorInfo> monitors = ListMonitors(false);
            if (monitors.Count == 0) throw new RecorderError("monitor_not_found", "no display detected");
            plan.Monitor = monitors.FirstOrDefault(m => string.Equals(m.Id, s.MonitorId, StringComparison.OrdinalIgnoreCase));
            if (plan.Monitor == null)
            {
                if (strict && !string.IsNullOrEmpty(s.MonitorId)) throw new RecorderError("monitor_not_found", "display not connected");
                plan.Monitor = DefaultMonitor(monitors);
            }
            plan.Fps = s.Fps;
            plan.Seconds = s.Seconds;
            OutputSize(plan.Monitor.W, plan.Monitor.H, s.Cap, out plan.OutW, out plan.OutH);
            plan.Kbps = RecommendedKbps(plan.OutW, plan.OutH, plan.Fps);
            // 视频加音频的码率乘以时长，再留 3% 给文件格式本身的开销
            plan.NeedBytes = (long)((plan.Kbps + AudioKbps) * 125.0 * plan.Seconds * 1.03);
            plan.FreeBytes = BufferFreeBytes(plan.BufferDir);
            plan.RingBytes = SamePath(plan.BufferDir, BufferDir) ? RingBytes() : 0;      // 换了位置的话，原来那份缓存帮不上忙
            return plan;
        }

        static RecSettings RecSettingsFrom(Dictionary<string, object> d)
        {
            var s = new RecSettings();
            s.MonitorId = BodyStr(d, "monitorId") ?? "";
            int cap = IntOf(d, "cap", 1080);
            s.Cap = cap == 0 || cap == 1440 ? cap : 1080;
            s.Fps = IntOf(d, "fps", 60) == 30 ? 30 : 60;
            s.Seconds = Math.Max(MinReplaySeconds, Math.Min(MaxReplaySeconds, IntOf(d, "seconds", 1800)));
            string enc = BodyStr(d, "encoder");
            s.Encoder = string.IsNullOrEmpty(enc) ? "auto" : enc;
            object mic;
            s.Mic = d != null && d.TryGetValue("mic", out mic) && mic is bool && (bool)mic;
            return s;
        }

        static Dictionary<string, object> RecSettingsJson(RecSettings s)
        {
            var d = new Dictionary<string, object>();
            d["monitorId"] = s.MonitorId;
            d["cap"] = s.Cap;
            d["fps"] = s.Fps;
            d["seconds"] = s.Seconds;
            d["encoder"] = s.Encoder;
            d["mic"] = s.Mic;
            return d;
        }

        // 换成新设置要不要重新开始录制："none" 不用；"restart" 要，缓存里的画面还在；"empty" 要，而且缓存会被清空
        // （画面来源或编码变了，新旧两种画面拼不到一个片段里）
        static string RestartKind(RecSettings old, RecSettings now, RecPlan plan)
        {
            string phase = RecPhase;
            if (!SettingRecOn || old == null || (phase != "on" && phase != "starting")) return "none";
            int ow, oh;
            RecConfig running = RecRunning;
            string monitor = running != null ? running.MonitorId : old.MonitorId;      // 以正在录的那一台为准
            if (running != null) { ow = running.Width; oh = running.Height; }
            else OutputSize(plan.Monitor.W, plan.Monitor.H, old.Cap, out ow, out oh);
            if (!string.Equals(monitor, plan.Monitor.Id, StringComparison.OrdinalIgnoreCase) || ow != plan.OutW || oh != plan.OutH
                || old.Fps != now.Fps || old.Encoder != now.Encoder || !SamePath(plan.BufferDir, BufferDir)) return "empty";
            if (old.Seconds != now.Seconds || old.Mic != now.Mic) return "restart";
            return "none";
        }

        // ------------------------------------------------------------------ 开始和停止

        // 开始录制（或者换了设置以后重新开始）。马上返回，真正的启动在后台线程上做：要几秒钟。
        static void BeginStart()
        {
            if (SettingRec == null) return;
            if (!SettingRecOn) { SettingRecOn = true; SaveSettings(); }
            SetPhase("starting");
            Task ignored = Task.Run(async () =>
            {
                await RecGate.WaitAsync();
                try { StartLocked(); }
                finally { RecGate.Release(); }
            });
        }

        static void StartLocked()
        {
            RecSettings s = SettingRec;
            if (s == null || !SettingRecOn) return;      // 排队等着的时候已经被关掉了
            RecPhase = "starting";
            try
            {
                RecPlan plan = BuildRecPlan(s, true, SettingBufferFolder);
                if (!plan.Fits)
                {
                    RecErrorFree = plan.FreeBytes + plan.RingBytes;
                    RecErrorNeed = plan.NeedBytes;
                    throw new RecorderError("disk", "not enough disk space for the replay buffer");
                }
                IEngine engine = EnsureEngine();
                var cfg = new RecConfig();
                cfg.MonitorId = plan.Monitor.Id;
                cfg.Width = plan.OutW; cfg.Height = plan.OutH; cfg.Fps = plan.Fps;
                cfg.Encoders = EncoderCandidates(Encoders(false), s.Encoder, plan.Kbps, plan.OutW, plan.OutH);
                // 仅测试用：REPLAYCLIP_TEST_ENCODERS=代号,代号…… 改成只让录制组件试这几个（可以写不存在的），看编码器启动不了时的表现
                string testEncoders = Environment.GetEnvironmentVariable("REPLAYCLIP_TEST_ENCODERS");
                if (!string.IsNullOrEmpty(testEncoders))
                {
                    cfg.Encoders.Clear();
                    foreach (string id in testEncoders.Split(','))
                    {
                        var k = new RecCandidate();
                        k.Id = id.Trim();
                        k.SettingsJson = "{\"rate_control\":\"VBR\",\"bitrate\":" + plan.Kbps.ToString(Inv) + "}";
                        cfg.Encoders.Add(k);
                    }
                }
                cfg.Mic = s.Mic;
                cfg.BufferSeconds = plan.Seconds;
                cfg.RingDir = plan.BufferDir;
                if (cfg.Encoders.Count == 0) throw new RecorderError("encoder_failed", "no encoder candidates");
                if (RecRunning != null)
                {
                    string oldRing = RecRunning.RingDir;
                    try { engine.Stop(); } catch (Exception ex) { Log("stop before restart: " + ex.Message); }
                    RecRunning = null;
                    if (!SamePath(oldRing, cfg.RingDir)) ClearBufferAt(oldRing);      // 缓存换了位置：原来那份不要了
                }
                if (RecClearBeforeStart)
                {
                    RecClearBeforeStart = false;
                    ClearBufferAt(cfg.RingDir);
                }
                Directory.CreateDirectory(cfg.RingDir);
                engine.Start(cfg);
                RecRunning = cfg;
                RecErrorCode = "";
                Log("recording started: " + cfg.Width + "x" + cfg.Height + "@" + cfg.Fps + " " + engine.Status.Encoder + " " + plan.Kbps + "kbps buffer=" + cfg.BufferSeconds + "s");
                SetPhase("on");
            }
            catch (RecorderError ex)
            {
                Log("recording could not start: " + ex.Code + " " + ex.Message);
                RecRunning = null;
                RecErrorCode = ex.Code;
                SetPhase(ex.Code == "monitor_not_found" ? "nomonitor" : ex.Code == "disk" ? "nodisk" : "failed");
            }
            catch (Exception ex)
            {
                Log("recording could not start: " + ex);
                RecRunning = null;
                RecErrorCode = "internal";
                SetPhase("failed");
            }
        }

        // 用户关掉录制。缓存里没保存的画面随之删掉（界面上会先问一句）。
        static async Task StopRecording()
        {
            // 停止会删掉回放缓存，而正在保存的片段还在读它：等保存完再停
            if (Interlocked.Read(ref SaveStartedTicks) != 0)
                throw new ApiError(409, Tr("正在保存片段，请等保存完成后再停止录制。", "A clip is being saved. Stop recording after it has finished."));
            SettingRecOn = false;
            SaveSettings();
            RecChanged();
            await RecGate.WaitAsync();
            try
            {
                await Task.Run(delegate
                {
                    try { IEngine e = Engine; if (e != null) e.Stop(); }
                    catch (Exception ex) { Log("stop: " + ex.Message); }
                    RecRunning = null;
                    RecErrorCode = "";
                    if (!SettingRecOn) ClearBuffer();
                    Log("recording stopped");
                });
            }
            finally
            {
                RecGate.Release();
                if (!SettingRecOn) SetPhase("off");
            }
        }

        // 每隔几秒看一眼：盘快满了就停下来；等着的那台显示器接回来了就接着录；托盘上的提示跟着状态走
        static void RecWatch()
        {
            if (Interlocked.Exchange(ref RecWatchBusy, 1) == 1) return;
            try
            {
                RecSettings s = SettingRec;
                if (s != null && SettingRecOn)
                {
                    string phase = RecPhase;
                    if (phase == "on")
                    {
                        long free = BufferFreeBytes(BufferDir);
                        if (free >= 0 && free < LowDiskBytes) PauseForDisk(free);
                    }
                    else if (phase == "nomonitor")
                    {
                        if (ConnectedMonitors().Any(m => string.Equals(m.Id, s.MonitorId, StringComparison.OrdinalIgnoreCase))) BeginStart();
                    }
                }
                BgPost(UpdateTrayState);
            }
            catch (Exception ex) { Log("RecWatch: " + ex.Message); }
            finally { Interlocked.Exchange(ref RecWatchBusy, 0); }
        }

        static void PauseForDisk(long free)
        {
            if (!RecGate.Wait(0)) return;      // 正在开始或停止：下一轮再看
            try
            {
                if (RecPhase != "on") return;
                Log("recording paused: low disk space (" + free + " bytes free)");
                try { IEngine e = Engine; if (e != null) e.Stop(); }
                catch (Exception ex) { Log("stop: " + ex.Message); }
                RecRunning = null;
                RecErrorCode = "disk";
                RecErrorFree = free;
                RecErrorNeed = 0;
                SetPhase("nodisk");
            }
            finally { RecGate.Release(); }
        }

        // ------------------------------------------------------------------ 保存最近的画面

        static string UniquePath(string dir, string stem, string ext)
        {
            string path = Path.Combine(dir, stem + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, stem + " " + i + ext);
            return path;
        }

        // ---- 存得久的时候让人看到进度
        // 很快就存完的（一秒以内）界面上和以前一样，不报进度；满一秒起，进度记在状态里（saveProgress），
        // 同时递给页面（程序自己的窗口；用浏览器窗口显示时页面自己来问）和发起这次保存的那一方（按快捷键时是屏幕角上的提示）。
        static readonly object SaveProgressLock = new object();
        static int SaveProgressPermille;        // 这一次保存读到了千分之几，只增不减
        static long SaveProgressToldTicks;      // 上一次往外报是什么时候
        static Action<double> SaveProgressListener;

        // 正在保存、而且已经存了一秒以上时：进行到哪了（0 到 1）；否则是 -1
        static double SaveProgressShown()
        {
            long start = Interlocked.Read(ref SaveStartedTicks);
            if (start == 0 || DateTime.UtcNow.Ticks - start < TimeSpan.TicksPerSecond) return -1;
            return SaveProgressPermille / 1000.0;
        }

        // 把现在的进度报出去。报得再勤也是每十分之一秒一次。锁着报：保存一结束（SaveStartedTicks 归零）就不会再有迟到的进度冒出来
        static void TellSaveProgress()
        {
            lock (SaveProgressLock)
            {
                double shown = SaveProgressShown();
                long now = DateTime.UtcNow.Ticks;
                if (shown < 0 || now - SaveProgressToldTicks < TimeSpan.TicksPerMillisecond * 100) return;
                SaveProgressToldTicks = now;
                NotifyPage("saving:" + shown.ToString("0.###", Inv));
                Action<double> listener = SaveProgressListener;
                if (listener != null) listener(shown);
            }
        }

        // 保存最近 seconds 秒的画面。点界面上的时长、按快捷键、点托盘菜单，走的都是这一个入口。
        // 不短于回放时长的时长，存的是缓存里的全部。片段的名字：Replay 日期 时间 (3min).mp4；存的是全部时不带括号。
        // onProgress：存了一秒以上以后，进度（0 到 1）变了就告诉它一声。
        static async Task<Dictionary<string, object>> SaveRecent(int seconds, string source, Action<double> onProgress = null)
        {
            if (!await SaveSem.WaitAsync(0)) throw new ApiError(409, Tr("上一次保存尚未完成。", "The previous save hasn't finished yet."));
            SavingSeconds = seconds;
            long started = DateTime.UtcNow.Ticks;
            lock (SaveProgressLock)
            {
                SaveProgressPermille = 0;
                SaveProgressToldTicks = 0;
                SaveProgressListener = onProgress;
                Interlocked.Exchange(ref SaveStartedTicks, started);
            }
            bool background = source != "ui";
            if (background) NotifyPage("refresh");      // 窗口开着的话，让页面马上显示"正在保存"
            // 满一秒时报第一次（哪怕这会儿进度没在变）
            Task first = Task.Delay(1050).ContinueWith(delegate { if (Interlocked.Read(ref SaveStartedTicks) == started) TellSaveProgress(); });
            Action<double> progress = delegate(double done)
            {
                int v = (int)Math.Max(0, Math.Min(1000, done * 1000));
                if (v <= SaveProgressPermille) return;
                SaveProgressPermille = v;
                TellSaveProgress();
            };
            try
            {
                string state = RecState();
                if (state != "recording")
                    throw new ApiError(409, state == "off" || state == "unconfigured" ? Tr("后台录制未开启。", "Background recording is off.")
                        : state == "starting" || state == "restoring" ? Tr("录制尚未就绪，请稍后重试。", "Recording isn't ready yet. Try again in a moment.")
                        : RecStatusLine(state) + Tr("。", "."));      // 没能启动、磁盘不够、显示器没接着：按快捷键时也说清是哪一种
                IEngine engine = EnsureEngine();
                bool whole = seconds <= 0 || seconds >= ReplaySeconds();
                Directory.CreateDirectory(Folder);
                string stamp = "Replay " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss", Inv);
                string dst = UniquePath(Folder, stamp + (whole ? "" : " (" + SpanLabel(seconds) + ")"), ".mp4");
                RecSaved saved;
                try { saved = await Task.Run(() => engine.SaveLast(whole ? 0 : seconds, dst, progress)); }
                catch (RecorderError ex)
                {
                    Log("save failed: " + ex.Code + " " + ex.Message);
                    throw new ApiError(500, SaveErrorText(ex));
                }
                // 刚开始录、缓存里还不到一秒（录制组件会把正在写的那一小段也交出来）：这样的片段没有用，不留
                if (saved.Duration < 1)
                {
                    try { File.Delete(dst); } catch { }
                    throw new ApiError(409, SaveErrorText(new RecorderError("nothing_buffered", null)));
                }
                // 缓存里还没攒够要的时长：存下的就是全部，名字里不带时长
                if (!whole && saved.Duration < seconds - 2)
                {
                    string plain = UniquePath(Folder, stamp, ".mp4");
                    try { File.Move(dst, plain); dst = plain; } catch { }
                }
                ProbeInfo p = await ProbeNow(dst);
                Log("saved " + dst + " (" + source + ")");
                Dictionary<string, object> d = Ok();
                d["name"] = Path.GetFileName(dst);
                d["duration"] = p.Ok ? p.Duration : saved.Duration;
                d["size"] = new FileInfo(dst).Length;
                d["requested"] = whole ? 0 : seconds;
                d["trimmed"] = !whole;
                d["warn"] = null;
                RememberSave(source, seconds, d, null);
                return d;
            }
            catch (Exception ex)
            {
                RememberSave(source, seconds, null, ex.Message);
                throw;
            }
            finally
            {
                lock (SaveProgressLock)
                {
                    Interlocked.Exchange(ref SaveStartedTicks, 0);
                    SaveProgressListener = null;
                }
                SaveSem.Release();
                if (background) NotifyPage("refresh");
            }
        }

        // ------------------------------------------------------------------ 「录制设置」对话框用的接口

        static List<object> EncodersJson(List<RecEncoder> list)
        {
            var el = new List<object>();
            foreach (RecEncoder e in list)
            {
                var o = new Dictionary<string, object>();
                o["id"] = e.Id; o["label"] = EncoderLabel(e); o["vendor"] = e.Vendor; o["codec"] = e.Codec; o["hardware"] = e.Hardware;
                el.Add(o);
            }
            return el;
        }

        // 对话框打开时要的全部信息：显示器、现在的设置（还没设置过就是默认值）、保存位置、开机启动、磁盘
        static Dictionary<string, object> RecInfo()
        {
            var d = Ok();
            List<MonitorInfo> monitors = ListMonitors(true);
            var ml = new List<object>();
            foreach (MonitorInfo m in monitors)
            {
                var o = new Dictionary<string, object>();
                o["id"] = m.Id; o["name"] = m.Name; o["number"] = m.Number;
                o["w"] = m.W; o["h"] = m.H; o["x"] = m.X; o["y"] = m.Y; o["hz"] = m.Hz; o["primary"] = m.Primary;
                ml.Add(o);
            }
            d["monitors"] = ml;

            RecSettings cur = SettingRec;
            Dictionary<string, object> st = RecSettingsJson(cur ?? new RecSettings());
            // 上次选的显示器现在还接着才沿用，否则退回主显示器（保存时会如实换掉）
            string wantMon = cur != null ? cur.MonitorId : "";
            MonitorInfo mon = monitors.FirstOrDefault(m => string.Equals(m.Id, wantMon, StringComparison.OrdinalIgnoreCase)) ?? DefaultMonitor(monitors);
            st["monitorId"] = mon != null ? mon.Id : "";
            st["folder"] = string.IsNullOrWhiteSpace(SettingFolder) ? ResolveFolder() : SettingFolder;
            st["buffer"] = string.IsNullOrWhiteSpace(SettingBufferFolder) ? DataDir : SettingBufferFolder;
            st["autostart"] = AutostartEnabled();
            st["closeAction"] = SettingCloseAction;
            d["settings"] = st;
            d["configured"] = cur != null;
            d["state"] = RecState();
            d["bufferDefault"] = DataDir;      // 缓存的默认位置（「恢复默认」用）

            List<RecEncoder> cached = EncoderCache;
            d["encoders"] = cached != null ? EncodersJson(cached) : null;      // 还没查过：页面接着去问 /api/rec/encoders
            return d;
        }

        static Dictionary<string, object> RecEncodersInfo()
        {
            try
            {
                Dictionary<string, object> d = Ok();
                d["encoders"] = EncodersJson(Encoders(false));
                return d;
            }
            catch (RecorderError ex)
            {
                Log("probe failed: " + ex.Code + " " + ex.Message);
                throw new ApiError(500, RecErrorText(ex.Code));
            }
        }

        static RecPlan PlanFor(RecSettings s, bool strict, string bufferParent)
        {
            try { return BuildRecPlan(s, strict, bufferParent); }
            catch (RecorderError ex)
            {
                throw new ApiError(409, ex.Message.StartsWith("no display", StringComparison.Ordinal)
                    ? Tr("未检测到显示器。", "No display detected.")
                    : Tr("所选显示器已断开，请重新选择。", "The selected display was disconnected. Choose another one."));
            }
        }

        static string DiskShortText(RecPlan plan)
        {
            string drive = Path.GetPathRoot(plan.BufferDir).TrimEnd('\\');
            return Tr("磁盘空间不足：回放缓存需要约 " + GB(plan.NeedBytes) + "，" + drive + " 可用 " + GB(plan.FreeBytes + plan.RingBytes) + "。请缩短回放时长或降低分辨率。",
                      "Not enough disk space: the replay buffer needs about " + GB(plan.NeedBytes) + ", and " + drive + " has " + GB(plan.FreeBytes + plan.RingBytes)
                      + " free. Shorten the replay length or lower the resolution.");
        }

        // 按对话框里现在的选择算一遍：输出多大、码率多少、缓存占多少磁盘、放不放得下、保存后要不要重新开始录制
        static Dictionary<string, object> RecPlanInfo(Dictionary<string, object> body)
        {
            RecSettings s = RecSettingsFrom(body);
            string bufferParent = NormalizeBufferParent(BodyStr(body, "buffer"));
            string bufferError = BufferProblem(bufferParent, false);
            if (bufferError != null) bufferParent = SettingBufferFolder;      // 填的位置不能用：其余的先按现在的位置算
            RecPlan plan = PlanFor(s, false, bufferParent);
            Dictionary<string, object> d = Ok();
            string folder = (BodyStr(body, "folder") ?? "").Trim().Trim('"').Trim();
            d["bufferDir"] = plan.BufferDir;
            d["bufferError"] = bufferError;
            d["folderError"] = folder.Length == 0 ? null : FolderProblem(folder, plan.BufferDir);
            d["monitorId"] = plan.Monitor.Id;
            d["outW"] = plan.OutW; d["outH"] = plan.OutH; d["fps"] = plan.Fps; d["kbps"] = plan.Kbps; d["seconds"] = plan.Seconds;
            d["needBytes"] = plan.NeedBytes;
            d["freeBytes"] = plan.FreeBytes < 0 ? -1 : plan.FreeBytes + plan.RingBytes;
            d["drive"] = Path.GetPathRoot(plan.BufferDir).TrimEnd('\\');
            d["fits"] = plan.Fits;
            d["diskError"] = plan.Fits ? null : DiskShortText(plan);
            d["restart"] = RestartKind(SettingRec, s, plan);

            var warnings = new List<string>();
            List<RecEncoder> usable = EncoderCache;
            RecEncoder chosen = usable != null ? (usable.FirstOrDefault(e => e.Id == s.Encoder) ?? (s.Encoder == "auto" ? usable.FirstOrDefault() : null)) : null;
            if (usable != null && usable.Count > 0 && usable.All(e => !e.Hardware))
                warnings.Add(Tr("未检测到可用的显卡编码器，将使用软件编码，游戏帧率会明显下降。",
                                "No hardware encoder was detected. Software encoding will be used, which noticeably lowers game frame rates."));
            if (chosen != null && s.Encoder != "auto" && chosen.Codec == "h264" && (plan.OutW > 4096 || plan.OutH > 4096))
                warnings.Add(Tr("H.264 不支持该分辨率，请改用 H.265 或 AV1，或降低分辨率。",
                                "H.264 doesn't support this resolution. Choose H.265 or AV1, or lower the resolution."));
            if (chosen != null && s.Encoder != "auto" && chosen.Codec == "av1")
                warnings.Add(Tr("部分播放器和剪辑软件无法打开 AV1 视频。如不确定，请选择 H.264 或 H.265。",
                                "Some players and editors can't open AV1 video. If unsure, choose H.264 or H.265."));
            d["warnings"] = warnings;
            return d;
        }

        // 对话框里点了「保存」（第一次是「开始录制」）
        static Dictionary<string, object> RecApply(Dictionary<string, object> body)
        {
            RecSettings s = RecSettingsFrom(body);
            string folder = (BodyStr(body, "folder") ?? "").Trim().Trim('"').Trim();
            if (folder.Length == 0) throw new ApiError(400, Tr("请填写保存位置。", "Enter a save location.")) { Details = FieldError("folder") };
            string bufferParent = NormalizeBufferParent(BodyStr(body, "buffer"));
            string problem = BufferProblem(bufferParent, false);
            if (problem != null) throw new ApiError(400, problem) { Details = FieldError("buffer") };
            problem = FolderProblem(folder, BufferDirFor(bufferParent));
            if (problem != null) throw new ApiError(400, problem) { Details = FieldError("folder") };
            try { Directory.CreateDirectory(folder); }
            catch (Exception ex) { throw new ApiError(400, Tr("无法使用该保存位置：", "This save location can't be used: ") + ex.Message) { Details = FieldError("folder") }; }

            RecPlan plan = PlanFor(s, true, bufferParent);
            s.MonitorId = plan.Monitor.Id;
            List<RecEncoder> usable = EncoderCache;
            RecEncoder chosen = usable != null ? usable.FirstOrDefault(e => e.Id == s.Encoder) : null;
            if (chosen != null && chosen.Codec == "h264" && (plan.OutW > 4096 || plan.OutH > 4096))
            {
                string size = plan.OutW + "×" + plan.OutH;
                throw new ApiError(400, Tr("H.264 不支持 " + size + " 的分辨率，请改用 H.265，或降低分辨率。",
                                           "H.264 doesn't support " + size + ". Choose H.265, or lower the resolution."));
            }
            if (!plan.Fits) throw new ApiError(400, DiskShortText(plan));
            // 最后才真的去建缓存文件夹、写一下试试：前面有哪一项通不过，就不会留下一个空文件夹
            problem = BufferProblem(bufferParent, true);
            if (problem != null) throw new ApiError(400, problem) { Details = FieldError("buffer") };

            object o;
            bool start = body.TryGetValue("start", out o) && o is bool && (bool)o;
            bool autostart = body.TryGetValue("autostart", out o) && o is bool && (bool)o;
            RecSettings old = SettingRec;
            string restart = RestartKind(old, s, plan);
            string phase = RecPhase;
            // 重新开始录制会清掉或改动回放缓存，而正在保存的片段还在读它：这类改动等保存完再做
            bool changesSource = old != null && !string.Equals(old.MonitorId, s.MonitorId, StringComparison.OrdinalIgnoreCase);
            if ((phase == "on" || phase == "starting") && (restart != "none" || changesSource) && Interlocked.Read(ref SaveStartedTicks) != 0)
                throw new ApiError(409, Tr("正在保存片段，请等保存完成后再更改这些设置。", "A clip is being saved. Change these settings after it has finished."));
            string oldBuffer = BufferDir;
            SettingRec = s;
            SettingBufferFolder = bufferParent;
            SettingFolder = folder;
            Folder = ResolveFolder();
            SettingSetupPrompted = true;
            string close = BodyStr(body, "closeAction");
            if (close == "ask" || close == "tray" || close == "exit") SettingCloseAction = close;
            if (start) SettingRecOn = true;
            SaveSettings();
            if (autostart != AutostartEnabled()) SetAutostart(autostart);
            Log("recording settings: " + plan.Monitor.W + "x" + plan.Monitor.H + " -> " + plan.OutW + "x" + plan.OutH + "@" + plan.Fps
                + " " + s.Encoder + " " + plan.Seconds + "s folder=" + folder + " buffer=" + plan.BufferDir + (autostart ? " autostart" : ""));

            // 要录的话：没在录就开始；在录而设置变了就重新开始
            bool running = phase == "on" || phase == "starting";
            bool sourceChanged = old != null && !string.Equals(old.MonitorId, s.MonitorId, StringComparison.OrdinalIgnoreCase);
            if (restart == "empty" || sourceChanged) RecClearBeforeStart = true;
            if (SettingRecOn && (!running || restart != "none")) BeginStart();
            else RecChanged();
            // 缓存换了位置而这会儿没在录：原来那个文件夹里剩下的直接删掉（在录的话，重新开始时会删）
            if (!running && !SamePath(oldBuffer, BufferDir)) { Task cleanup = Task.Run(delegate { ClearBufferAt(oldBuffer); }); }

            Dictionary<string, object> d = Ok();
            d["state"] = RecState();
            return d;
        }

        static Dictionary<string, object> FieldError(string field)
        {
            var d = new Dictionary<string, object>();
            d["field"] = field;      // 页面据此把出错的说明放到对应的那一栏下面
            return d;
        }

        // 仅测试用（只在用了 REPLAYCLIP_FAKE_ENGINE 时起作用）：POST /api/fake
        //   { "crash": 毫秒 }      录制中断这么久，然后自己恢复
        //   { "failNext": "代号" } 下一次启动失败，报这个错
        //   { "free": 字节数 }     假装缓存所在的盘只剩这么多；-1 = 恢复成真实的值
        static Dictionary<string, object> FakeControl(Dictionary<string, object> body)
        {
            var fake = EnsureEngine() as FakeEngine;
            if (fake == null) throw new ApiError(404, "not found");
            object o;
            if (body.TryGetValue("crash", out o)) fake.Crash((int)ToD(o));
            if (body.TryGetValue("failNext", out o)) fake.FailNext = o as string ?? "";
            if (body.TryGetValue("saveMs", out o)) fake.SaveMs = (int)ToD(o);
            if (body.TryGetValue("failSave", out o)) fake.FailNextSave = o as string ?? "";
            if (body.TryGetValue("free", out o)) FakeFreeBytes = (long)ToD(o);
            return Ok();
        }

        // ------------------------------------------------------------------ 开机时启动

        const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        // 注册表里这一项的名字。环境变量仅测试用（测试实例写到另一个名字上，不留下真的开机启动项）
        static string RunValueName
        {
            get { return Environment.GetEnvironmentVariable("REPLAYCLIP_RUN_VALUE") ?? "ReplayClipper"; }
        }

        // 用另一个数据目录跑的测试实例不写开机启动项，除非指定了测试用的名字
        static bool AutostartAllowed
        {
            get
            {
                return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_DATA"))
                    || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REPLAYCLIP_RUN_VALUE"));
            }
        }

        static string AutostartCommand
        {
            get { return "\"" + Path.Combine(AppDir, "ReplayClipper.exe") + "\" --background"; }
        }

        static bool AutostartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string v = key != null ? key.GetValue(RunValueName) as string : null;
                    return !string.IsNullOrEmpty(v) && v.IndexOf(Path.Combine(AppDir, "ReplayClipper.exe"), StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        // 开机时启动：登录 Windows 后自动运行，不开窗口，只在托盘里（上次开着录制的话就接着录）
        static void SetAutostart(bool on)
        {
            if (!AutostartAllowed) return;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    if (on) key.SetValue(RunValueName, AutostartCommand);
                    else key.DeleteValue(RunValueName, false);
                }
                Log("autostart " + (on ? "on" : "off"));
            }
            catch (Exception ex) { Log("SetAutostart: " + ex.Message); }
        }

        // 卸载时：开机启动项指向的是这个文件夹里的程序才删
        static void RemoveAutostart(string target)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (key == null) return;
                    string v = key.GetValue(RunValueName) as string;
                    if (!string.IsNullOrEmpty(v) && v.IndexOf(target.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) >= 0)
                        key.DeleteValue(RunValueName, false);
                }
            }
            catch { }
        }
    }
}
