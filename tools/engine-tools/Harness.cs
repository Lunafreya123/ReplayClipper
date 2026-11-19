// Console test driver for the recorder client (src\Engine.cs). Not part of the app.
//
//   EngineHarness probe
//   EngineHarness run   --seconds 60 --script "20:save:0:a;30:killhelper;50:save:30:b" [options]
//   EngineHarness fill  --from <ring> --to <ring> --segments 900 [--dummy]
//
// Script actions (time in seconds since recording started):
//   save:<seconds>:<name>             SaveLast(seconds) -> <out>\<name>.mp4
//   savekill:<seconds>:<name>:<ms>    same, and kill the helper <ms> after the save began
//   killhelper | killmux              kill the helper / its obs-ffmpeg-mux child, by pid
//   join:<mode>                       ffmpeg | direct | both | (empty = default) for the saves that follow
//   freezehelper                      suspend the helper (it stays alive but stops answering)
//   tones                             three quiet 1 kHz tones through the default playback device
//   flash                             five flashes of a small on-screen patch, each with a quiet click
//   videofile:<path>                  (--debug) swap the video source to a media file
//   stop | start                      stop / start recording through the client
// Options: --engine --ffmpeg --data --ring --out --monitor primary|<n> --size WxH --fps --buffer --seg
//          --chain hevc|h264|x264 --bitrate --audio 0|1 --mic 0|1 --stat <s> --reference <file> --videofile <file>
//          --join ffmpeg|direct (force one join; default: direct with ffmpeg as fallback) --reserve <bytes>
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ReplayClipper;

static class Harness
{
    static readonly Dictionary<string, string> Opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static StreamWriter _logFile;
    static readonly object LogGate = new object();

    [DllImport("user32.dll")]
    static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("winmm.dll", SetLastError = true)]
    static extern bool PlaySound(IntPtr data, IntPtr module, uint flags);
    const uint SND_ASYNC = 1, SND_MEMORY = 4, SND_NODEFAULT = 2;

    static string O(string key, string def) { string v; return Opt.TryGetValue(key, out v) ? v : def; }
    static int OI(string key, int def) { return int.Parse(O(key, def.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture); }

    static void Say(string fmt, params object[] args)
    {
        string line = string.Format(CultureInfo.InvariantCulture, "{0,9:F3} ", Clock.Elapsed.TotalSeconds)
                      + (args.Length == 0 ? fmt : string.Format(CultureInfo.InvariantCulture, fmt, args));
        lock (LogGate)
        {
            Console.WriteLine(line);
            if (_logFile != null) _logFile.WriteLine(line);
        }
    }

    static int Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "probe";
        for (int i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--")) continue;
            Opt[args[i].Substring(2)] = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
        }
        string here = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
        string logPath = O("log", "");
        if (logPath.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)));
            _logFile = new StreamWriter(logPath, false, new UTF8Encoding(false));
            _logFile.AutoFlush = true;
        }
        try
        {
            if (cmd == "fill") return Fill();
            string engine = O("engine", Path.Combine(here, "engine\\bin\\64bit\\ReplayClipperEngine.exe"));
            string bundled = Path.Combine(here, "ffmpeg\\ffmpeg.exe");      // when the harness sits in the app folder
            string ffmpeg = O("ffmpeg", File.Exists(bundled) ? bundled : "ffmpeg.exe");   // else pass --ffmpeg
            string data = Path.GetFullPath(O("data", Path.Combine(here, "harness-data")));   // helper logs, default ring and clips go here
            using (EngineClient client = new EngineClient(engine, ffmpeg, data, delegate(string s) { Say("  [client] {0}", s); }))
            {
                client.Debug = O("debug", "0") == "1" || O("reference", "").Length > 0 || O("videofile", "").Length > 0;
                client.DebugIndexReserve = OI("reserve", 0);
                client.DebugJoin = O("join", "");
                if (cmd == "probe") return Probe(client);
                if (cmd == "run") return Run(client, data);
                Say("unknown command {0}", cmd);
                return 2;
            }
        }
        catch (EngineException ex) { Say("ENGINE ERROR {0}: {1}", ex.Code, ex.Message); return 1; }
        catch (Exception ex) { Say("EXCEPTION {0}", ex); return 3; }
    }

    static int Probe(EngineClient client)
    {
        Stopwatch sw = Stopwatch.StartNew();
        List<EncoderInfo> encs = client.Probe();
        Say("probe took {0} ms (includes starting the helper)", sw.ElapsedMilliseconds);
        foreach (EncoderInfo e in encs) Say("encoder {0,-22} codec={1,-5} vendor={2,-9} hardware={3}", e.Id, e.Codec, e.Vendor, e.Hardware);
        foreach (EngineMonitor m in client.Monitors())
            Say("monitor {0} {1} {2}x{3} at ({4},{5}){6}  id={7}", m.Device, m.Name, m.Width, m.Height, m.X, m.Y, m.Primary ? " primary" : "", m.Id);
        client.Stop();
        return 0;
    }

    // Candidate chains: what the app should pass by default (see the report's encoder table).
    static List<EncoderChoice> Chain(string name, int kbps)
    {
        string b = kbps.ToString(CultureInfo.InvariantCulture), mb = (kbps * 3 / 2).ToString(CultureInfo.InvariantCulture);
        List<EncoderChoice> l = new List<EncoderChoice>();
        if (name == "hevc")
        {
            l.Add(new EncoderChoice("h265_texture_amf", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"preset\":\"quality\"}"));
            l.Add(new EncoderChoice("obs_nvenc_hevc_tex", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"max_bitrate\":" + mb + ",\"preset\":\"p5\",\"tune\":\"hq\",\"multipass\":\"qres\",\"profile\":\"main\"}"));
            l.Add(new EncoderChoice("obs_qsv11_hevc", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"max_bitrate\":" + mb + ",\"target_usage\":\"TU4\",\"profile\":\"main\"}"));
        }
        if (name == "hevc" || name == "h264")
        {
            l.Add(new EncoderChoice("h264_texture_amf", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"preset\":\"quality\",\"profile\":\"high\"}"));
            l.Add(new EncoderChoice("obs_nvenc_h264_tex", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"max_bitrate\":" + mb + ",\"preset\":\"p5\",\"tune\":\"hq\",\"multipass\":\"qres\",\"profile\":\"high\"}"));
            l.Add(new EncoderChoice("obs_qsv11_v2", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"max_bitrate\":" + mb + ",\"target_usage\":\"TU4\",\"profile\":\"high\"}"));
        }
        if (name == "bogus") l.Add(new EncoderChoice("h264_texture_amf", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"profile\":\"no-such-profile\",\"ffmpeg_opts\":\"Usage=999\"}"));
        l.Add(new EncoderChoice("obs_x264", "{\"rate_control\":\"VBR\",\"bitrate\":" + b + ",\"preset\":\"veryfast\",\"profile\":\"high\"}"));
        return l;
    }

    static EngineMonitor PickMonitor(EngineClient client)
    {
        List<EngineMonitor> mons = client.Monitors();
        string sel = O("monitor", "primary");
        int idx;
        foreach (EngineMonitor m in mons) if (sel == "primary" && m.Primary) return m;
        if (int.TryParse(sel, out idx) && idx >= 0 && idx < mons.Count) return mons[idx];
        foreach (EngineMonitor m in mons) if (string.Equals(m.Id, sel, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Device, sel, StringComparison.OrdinalIgnoreCase)) return m;
        return null;
    }

    static int Run(EngineClient client, string data)
    {
        string outDir = Path.GetFullPath(O("out", Path.Combine(data, "..\\out")));
        Directory.CreateDirectory(outDir);
        EngineConfig cfg = new EngineConfig();
        EngineMonitor mon = null;
        if (O("videofile", "").Length == 0)
        {
            if (O("monitor", "primary") == "bogus") cfg.MonitorId = "\\\\?\\DISPLAY#NOPE#0000";
            else
            {
                mon = PickMonitor(client);
                if (mon == null) { Say("monitor not found"); return 2; }
                cfg.MonitorId = mon.Id;
                Say("display {0} {1}x{2} at ({3},{4})", mon.Device, mon.Width, mon.Height, mon.X, mon.Y);
            }
        }
        string[] wh = O("size", "1920x1080").Split('x');
        cfg.Width = int.Parse(wh[0]); cfg.Height = int.Parse(wh[1]); cfg.Fps = OI("fps", 60);
        cfg.Encoders = Chain(O("chain", "hevc"), OI("bitrate", 16000));
        cfg.AudioBitrateKbps = 160;
        cfg.DesktopAudio = O("audio", "1") == "1"; cfg.Mic = O("mic", "0") == "1";
        cfg.BufferSeconds = OI("buffer", 1800); cfg.SegmentSeconds = OI("seg", 2);
        cfg.RingDir = Path.GetFullPath(O("ring", Path.Combine(data, "buffer")));
        if (O("method", "").Length > 0) cfg.Method = O("method", "");
        cfg.Extra = new Dictionary<string, object>();
        if (O("reference", "").Length > 0) cfg.Extra["debugReference"] = Path.GetFullPath(O("reference", ""));
        if (O("videofile", "").Length > 0) cfg.Extra["debugVideoFile"] = Path.GetFullPath(O("videofile", ""));

        Stopwatch sw = Stopwatch.StartNew();
        client.Start(cfg);
        EngineStatus st = client.Status;
        Say("recording started in {0} ms: {1} ({2}) {3}x{4}@{5}, helper pid {6}", sw.ElapsedMilliseconds, st.Encoder, st.Codec, st.Width, st.Height, st.Fps, st.HelperPid);

        // script
        List<KeyValuePair<double, string>> script = new List<KeyValuePair<double, string>>();
        foreach (string item in O("script", "").Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int c = item.IndexOf(':');
            script.Add(new KeyValuePair<double, string>(double.Parse(item.Substring(0, c), CultureInfo.InvariantCulture), item.Substring(c + 1)));
        }
        int seconds = OI("seconds", 30), statEvery = OI("stat", 10);
        Stopwatch run = Stopwatch.StartNew();
        double nextStat = statEvery;
        List<Thread> background = new List<Thread>();
        while (run.Elapsed.TotalSeconds < seconds)
        {
            Thread.Sleep(50);
            double now = run.Elapsed.TotalSeconds;
            if (script.Count > 0 && now >= script[0].Key)
            {
                string action = script[0].Value;
                script.RemoveAt(0);
                Act(client, cfg, mon, outDir, action, background);
            }
            if (now >= nextStat)
            {
                nextStat += statEvery;
                st = client.Status;
                Say("STAT t={0:F0}s rec={1} frames={2} lagged={3} skipped={4} buffered={5:F0}s ring={6} files {7:F1} MB pruneErr={8} helper={9:F1} MB pid={10} restarts={11} tickMax={13}ms err={12}",
                    now, st.Recording, st.Frames, st.LaggedFrames, st.SkippedFrames, st.BufferedSeconds, st.RingFiles, st.RingBytes / 1048576.0,
                    st.PruneErrors, st.HelperPrivateMB, st.HelperPid, st.Restarts, st.LastError, st.RingTickMsMax);
            }
        }
        foreach (Thread t in background) t.Join(600000);
        sw.Restart();
        client.Stop();
        Say("stopped in {0} ms", sw.ElapsedMilliseconds);
        return 0;
    }

    static void Act(EngineClient client, EngineConfig cfg, EngineMonitor mon, string outDir, string action, List<Thread> background)
    {
        string[] a = action.Split(':');
        try
        {
            if (a[0] == "save" || a[0] == "savekill")
            {
                int secs = int.Parse(a[1]);
                string dest = Path.Combine(outDir, a[2] + ".mp4");
                ThreadStart work = delegate()
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        // progress must only move forward and may say 1 only once the finished file exists
                        int calls = 0, backwards = 0, early = 0;
                        double lastP = -1, beforeDone = 0;
                        SaveResult r = client.SaveLast(secs, dest, delegate(double p)
                        {
                            calls++;
                            if (p < lastP) backwards++;
                            if (p >= 1.0 && !File.Exists(dest)) early++;
                            if (p < 1.0) beforeDone = p;
                            lastP = p;
                        });
                        Say("SAVE {0}: asked {1}s -> {2:F3}s, {3:F1} MB in {4} ms  ({5})", a[2], secs, r.Duration, r.Size / 1048576.0, sw.ElapsedMilliseconds, r.Path);
                        Say("     progress: {0} calls, went backwards {1} times, reached 1.0 before the file existed {2} times, last value before the end {3:F3}, final {4:F2}",
                            calls, backwards, early, beforeDone, lastP);
                        using (Process me = Process.GetCurrentProcess())
                            Say("     this process: peak working set {0:F0} MB, peak private bytes {1:F0} MB", me.PeakWorkingSet64 / 1048576.0, me.PeakPagedMemorySize64 / 1048576.0);
                    }
                    catch (EngineException ex)
                    {
                        Say("SAVE {0} FAILED after {1} ms: {2}: {3}; file exists: {4}; .part exists: {5}", a[2], sw.ElapsedMilliseconds, ex.Code, ex.Message, File.Exists(dest), File.Exists(dest + ".part"));
                    }
                };
                if (a[0] == "save" && !(a.Length > 3 && a[3] == "bg")) work();
                else
                {
                    Thread t = new Thread(work);
                    t.Start();
                    background.Add(t);
                    if (a[0] == "savekill")
                    {
                        Thread.Sleep(int.Parse(a[3]));
                        KillPid(client.Status.HelperPid, "helper (during save)");
                    }
                }
            }
            else if (a[0] == "savekillff")
            {
                // kill the ffmpeg that is writing the MP4: the save must fail and leave nothing behind
                int secs = int.Parse(a[1]);
                string dest = Path.Combine(outDir, a[2] + ".mp4");
                Thread t = new Thread(delegate()
                {
                    Stopwatch sw = Stopwatch.StartNew();
                    try { SaveResult r = client.SaveLast(secs, dest); Say("SAVE {0}: completed although ffmpeg was to be killed: {1:F3}s in {2} ms", a[2], r.Duration, sw.ElapsedMilliseconds); }
                    catch (EngineException ex) { Say("SAVE {0} FAILED after {1} ms: {2}: {3}; file exists: {4}; .part exists: {5}", a[2], sw.ElapsedMilliseconds, ex.Code, ex.Message, File.Exists(dest), File.Exists(dest + ".part")); }
                });
                t.Start();
                background.Add(t);
                Thread.Sleep(int.Parse(a[3]));
                int me = Process.GetCurrentProcess().Id;
                foreach (Process p in Process.GetProcessesByName("ffmpeg"))
                    if (ParentOf(p.Id) == me) KillPid(p.Id, "ffmpeg (during save)");
            }
            else if (a[0] == "freezehelper")
            {
                // a helper that is alive but stuck: suspend all its threads
                int pid = client.Status.HelperPid;
                using (Process p = Process.GetProcessById(pid)) Say("suspended helper pid {0} (NtSuspendProcess rc={1})", pid, NtSuspendProcess(p.Handle));
            }
            else if (a[0] == "killhelper") KillPid(client.Status.HelperPid, "helper");
            else if (a[0] == "join") { client.DebugJoin = a.Length > 1 ? a[1] : ""; Say("join mode: {0}", client.DebugJoin.Length > 0 ? client.DebugJoin : "(default)"); }
            else if (a[0] == "killmux")
            {
                int helper = client.Status.HelperPid;
                foreach (Process p in Process.GetProcessesByName("obs-ffmpeg-mux"))
                    if (ParentOf(p.Id) == helper) KillPid(p.Id, "obs-ffmpeg-mux child of the helper");
            }
            else if (a[0] == "videofile")
            {
                Dictionary<string, object> f = new Dictionary<string, object>();
                f["path"] = Path.GetFullPath(action.Substring("videofile:".Length));
                client.DebugRequest("debug_video_file", f, 5000);
                Thread.Sleep(1500);
                Say("video source switched to {0}; source size now {1}", Path.GetFileName((string)f["path"]), client.DebugRequest("debug_source_size", null, 5000)["size"]);
            }
            else if (a[0] == "list")
            {
                // only the helper's part of a save: how fast is the segment list for this much footage?
                Dictionary<string, object> f = new Dictionary<string, object>();
                f["seconds"] = int.Parse(a[1]);
                Stopwatch sw = Stopwatch.StartNew();
                Dictionary<string, object> r = client.DebugRequest("save", f, 60000);
                long ms = sw.ElapsedMilliseconds;
                int n = 0;
                foreach (object o in (System.Collections.IEnumerable)r["segments"]) n++;
                Dictionary<string, object> rel = new Dictionary<string, object>();
                rel["saveId"] = r["saveId"];
                client.DebugRequest("release", rel, 5000);
                Say("LIST asked {0}s -> {1} segments, request to parsed reply {2} ms", a[1], n, ms);
            }
            else if (a[0] == "tones") Tones();
            else if (a[0] == "flash") Flash(mon);
            else if (a[0] == "stop") { client.Stop(); Say("client.Stop() done"); }
            else if (a[0] == "start") { client.Start(cfg); Say("client.Start() done: {0}", client.Status.Encoder); }
            else Say("unknown action {0}", action);
        }
        catch (EngineException ex) { Say("ACTION {0} FAILED {1}: {2}", action, ex.Code, ex.Message); }
    }

    static void KillPid(int pid, string what)
    {
        if (pid <= 0) { Say("kill {0}: no pid", what); return; }
        try { Process.GetProcessById(pid).Kill(); Say("killed {0} pid {1}", what, pid); }
        catch (Exception ex) { Say("kill {0} pid {1}: {2}", what, pid, ex.Message); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION { public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("ntdll.dll")]
    static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int cls, ref PROCESS_BASIC_INFORMATION info, int len, out int ret);

    static int ParentOf(int pid)
    {
        try
        {
            using (Process p = Process.GetProcessById(pid))
            {
                PROCESS_BASIC_INFORMATION pbi = new PROCESS_BASIC_INFORMATION();
                int ret;
                if (NtQueryInformationProcess(p.Handle, 0, ref pbi, Marshal.SizeOf(typeof(PROCESS_BASIC_INFORMATION)), out ret) == 0)
                    return pbi.InheritedFromUniqueProcessId.ToInt32();
            }
        }
        catch { }
        return -1;
    }

    // ------------------------------------------------------------------ test signals

    static byte[] Wav(short[] samples, int rate)
    {
        MemoryStream ms = new MemoryStream();
        BinaryWriter w = new BinaryWriter(ms);
        int bytes = samples.Length * 2;
        w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
        foreach (short s in samples) w.Write(s);
        return ms.ToArray();
    }

    static readonly List<GCHandle> Pinned = new List<GCHandle>();

    static IntPtr Pin(byte[] wav)
    {
        GCHandle h = GCHandle.Alloc(wav, GCHandleType.Pinned);
        Pinned.Add(h);      // async playback reads the buffer after we return
        return h.AddrOfPinnedObject();
    }

    /// <summary>Three quiet tones (1 kHz, 0.3 s, about -30 dBFS) through the default playback device.</summary>
    static void Tones()
    {
        const int rate = 48000;
        short[] s = new short[rate * 2];
        for (int k = 0; k < 3; k++)
            for (int i = 0; i < rate * 3 / 10; i++)
            {
                double env = Math.Min(1.0, Math.Min(i, rate * 3 / 10 - i) / 480.0);     // 10 ms fades: no clicks
                s[k * rate * 6 / 10 + i] = (short)(Math.Sin(2 * Math.PI * 1000 * i / rate) * 1000 * env);
            }
        IntPtr p = Pin(Wav(s, rate));
        bool ok = PlaySound(p, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
        Say("played three quiet 1 kHz tones (ok={0})", ok);
    }

    sealed class Patch : Form
    {
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000008; return cp; }   // NOACTIVATE | TOOLWINDOW | TOPMOST
        }
    }

    /// <summary>
    /// A 160x160 patch near the top-left corner of the recorded display turns white for 0.2 s five times,
    /// one second apart, and a short quiet click starts at the same moment. Never takes focus.
    /// </summary>
    static void Flash(EngineMonitor mon)
    {
        if (mon == null) { Say("flash: no display"); return; }
        const int rate = 48000;
        short[] click = new short[rate / 20];
        for (int i = 0; i < click.Length; i++) click[i] = (short)(Math.Sin(2 * Math.PI * 2000 * i / rate) * 1600 * Math.Exp(-i / 300.0));
        IntPtr wav = Pin(Wav(click, rate));
        Thread ui = new Thread(delegate()
        {
            Patch f = new Patch();
            f.FormBorderStyle = FormBorderStyle.None; f.ShowInTaskbar = false; f.StartPosition = FormStartPosition.Manual;
            f.BackColor = Color.Black;
            f.Bounds = new Rectangle(mon.X + 40, mon.Y + 40, 160, 160);
            System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
            int step = 0;
            Stopwatch sw = Stopwatch.StartNew();
            timer.Interval = 10;
            timer.Tick += delegate
            {
                double t = sw.Elapsed.TotalSeconds;
                int n = (int)Math.Floor(t);                 // flash n starts at t = n + 0.5
                bool on = t - n >= 0.5 && t - n < 0.7 && n < 5;
                if (on && step == n)
                {
                    step++;
                    PlaySound(wav, IntPtr.Zero, SND_MEMORY | SND_ASYNC | SND_NODEFAULT);
                    f.BackColor = Color.White; f.Refresh();
                }
                else if (!on && f.BackColor != Color.Black) { f.BackColor = Color.Black; f.Refresh(); }
                if (t > 6) { timer.Stop(); f.Close(); }
            };
            f.Shown += delegate { timer.Start(); };
            Application.Run(f);
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        Say("flash: patch at ({0},{1}) 160x160, 5 flashes with clicks over 6 s", mon.X + 40, mon.Y + 40);
    }

    // ------------------------------------------------------------------ synthetic rings for scaling tests

    /// <summary>
    /// Builds a long ring out of a short real one by repeating its segments (and their index lines) with
    /// start times laid out backwards from now. --dummy writes 188-byte placeholder files instead of copies
    /// (index and prune scaling only; such a ring cannot be joined).
    /// </summary>
    static int Fill()
    {
        string from = Path.GetFullPath(O("from", "")), to = Path.GetFullPath(O("to", ""));
        int want = OI("segments", 900);
        bool dummy = O("dummy", "0") == "1";
        int segMs = OI("seg", 2) * 1000;
        List<string[]> lines = new List<string[]>();
        foreach (string line in File.ReadAllLines(Path.Combine(from, "ring.idx")))
        {
            string[] f = line.Split('|');
            if (f.Length >= 13 && f[11] == "0" && File.Exists(Path.Combine(from, f[0]))) lines.Add(f);
        }
        if (lines.Count < 3) { Say("fill: need at least 3 complete segments in {0}", from); return 2; }
        lines.RemoveAt(0);                       // the first segment of a session is a little longer than the rest
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(to, "rcseg-*.ts")) File.Delete(f);
        File.Copy(Path.Combine(from, "ring.json"), Path.Combine(to, "ring.json"), true);
        long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        long start = now - (long)want * segMs - 3000;
        StringBuilder idx = new StringBuilder();
        Stopwatch sw = Stopwatch.StartNew();
        byte[] placeholder = new byte[188];
        placeholder[0] = 0x47; placeholder[1] = 0x1F; placeholder[2] = 0xFF; placeholder[3] = 0x10;
        long bytes = 0;
        string prev = "";
        for (int i = 0; i < want; i++)
        {
            string[] src = lines[i % lines.Count];
            string name = "rcseg-fill0000-" + i.ToString("D8", CultureInfo.InvariantCulture) + ".ts";
            string dst = Path.Combine(to, name);
            if (dummy) File.WriteAllBytes(dst, placeholder);
            else File.Copy(Path.Combine(from, src[0]), dst, true);
            bytes += new FileInfo(dst).Length;
            string[] f = (string[])src.Clone();
            f[0] = name; f[1] = "fill0000"; f[2] = (start + (long)i * segMs).ToString(CultureInfo.InvariantCulture);
            // a copy continues its predecessor only where the source did (not where the source list wraps around)
            f[12] = (i % lines.Count == 0) ? "" : prev;
            idx.Append(string.Join("|", f)).Append('\n');
            prev = name;
        }
        File.WriteAllText(Path.Combine(to, "ring.idx"), idx.ToString(), new UTF8Encoding(false));
        Say("fill: {0} segments ({1:F1} MB) from {2} source segments in {3} ms -> {4}", want, bytes / 1048576.0, lines.Count, sw.ElapsedMilliseconds, to);
        return 0;
    }
}
