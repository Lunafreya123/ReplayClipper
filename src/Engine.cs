// Replay Clipper: client for the built-in recorder (ReplayClipperEngine.exe).
//
// This file is part of the app, not of the engine. It contains no code from OBS or from the
// engine helper: it starts the helper as a separate process and talks to it only through the
// documented protocol in engine-contract.md (JSON lines over a named pipe), then turns the
// helper's segment files into an MP4 with the app's bundled ffmpeg.
//
// One file, C# 5, .NET Framework 4.x; needs System.Core.dll and System.Web.Extensions.dll.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReplayClipper
{
    public sealed class EngineException : Exception
    {
        public string Code;
        public EngineException(string code, string message) : base(message) { Code = code; }
    }

    public sealed class EncoderInfo { public string Id, Codec, Vendor; public bool Hardware; }

    public sealed class EncoderChoice
    {
        public string Id;
        public string SettingsJson;   // OBS encoder settings as a JSON object, passed through unchanged
        public EncoderChoice() { }
        public EncoderChoice(string id, string settingsJson) { Id = id; SettingsJson = settingsJson; }
    }

    public sealed class EngineMonitor
    {
        public string Id, Device, Name;   // Id = OBS monitor_id (device path); Device = \\.\DISPLAYn
        public int X, Y, Width, Height;
        public bool Primary;
    }

    public sealed class EngineConfig
    {
        public string MonitorId;                      // OBS monitor_id, or \\.\DISPLAYn
        public int Width = 1920, Height = 1080, Fps = 60;
        public bool Cursor = true;
        public List<EncoderChoice> Encoders = new List<EncoderChoice>();   // tried in order
        public int AudioBitrateKbps = 160;
        public bool DesktopAudio = true, Mic;
        public int BufferSeconds = 1800;              // up to 43200 (12 h)
        public int SegmentSeconds = 2;
        public string RingDir;
        public string Method;                         // null/"auto", "dxgi", "wgc"
        public Dictionary<string, object> Extra;      // extra fields for the start request (tests only)
    }

    public sealed class EngineStatus
    {
        public bool HelperRunning, Recording;
        public string Encoder = "", Codec = "";
        public int Width, Height, Fps;
        public DateTime SinceUtc;
        public double BufferedSeconds;
        public long Frames, LaggedFrames, SkippedFrames;
        public string LastError = "", LastErrorMessage = "";
        // additions to the contract's list
        public int RingFiles;
        public long RingBytes;
        public int PruneErrors;
        public double HelperPrivateMB;
        public int HelperPid;
        public int Restarts;          // times the helper or the recording had to be started again
        public long RingTickMsMax;    // helper's longest ring housekeeping pass in the last 2 s
    }

    public sealed class SaveResult { public string Path; public double Duration; public long Size; }

    public sealed class EngineClient : IDisposable
    {
        const int ReplyTimeoutMs = 15000;
        static readonly int[] BackoffSeconds = { 1, 2, 5, 15, 60 };

        readonly string _exe, _ffmpeg, _dataDir;
        readonly Action<string> _log;
        readonly object _op = new object();          // one control operation at a time
        readonly object _state = new object();
        Link _link;
        IntPtr _job = IntPtr.Zero;
        EngineConfig _wanted;
        volatile EngineStatus _status = new EngineStatus();
        volatile bool _disposed;
        int _saving;
        Process _ffmpegProc;
        Thread _supervisor;
        int _backoff;
        DateTime _nextAttemptUtc = DateTime.MinValue, _healthySinceUtc = DateTime.MinValue, _lastUseUtc = DateTime.UtcNow;
        long _lastFrames = -1;
        DateTime _lastFramesChangeUtc = DateTime.UtcNow;
        List<EngineMonitor> _monitors = new List<EngineMonitor>();

        /// <summary>Test switch: start the helper with --debug (enables its debug requests).</summary>
        public bool Debug;
        /// <summary>Test switch: bytes to reserve for the MP4 index instead of the computed amount.</summary>
        public long DebugIndexReserve;
        /// <summary>Test switch: "ffmpeg" = always join with ffmpeg (as 2.0 did), "direct" = never fall back to it,
        /// "both" = after a direct join also write <c>&lt;destPath&gt;.ffmpeg.mp4</c> from the same footage.</summary>
        public string DebugJoin = "";

        public event Action Changed;

        public EngineClient(string engineExe, string ffmpegExe, string dataDir, Action<string> log)
        {
            _exe = engineExe; _ffmpeg = ffmpegExe; _dataDir = dataDir; _log = log;
            _supervisor = new Thread(Supervise);
            _supervisor.IsBackground = true;
            _supervisor.Name = "EngineClient.Supervise";
            _supervisor.Start();
        }

        public EngineStatus Status { get { return _status; } }

        // ------------------------------------------------------------------ public operations

        /// <summary>Usable video encoders, in the helper's (OBS registration) order. Starts the helper if needed.</summary>
        public List<EncoderInfo> Probe()
        {
            lock (_op)
            {
                Dictionary<string, object> r = Request(EnsureLink(), Msg("probe"), ReplyTimeoutMs);
                List<EncoderInfo> list = new List<EncoderInfo>();
                foreach (Dictionary<string, object> d in Items(r, "encoders"))
                {
                    EncoderInfo e = new EncoderInfo();
                    e.Id = Str(d, "id"); e.Codec = Str(d, "codec"); e.Vendor = Str(d, "vendor"); e.Hardware = Flag(d, "hardware");
                    list.Add(e);
                }
                List<EngineMonitor> mons = new List<EngineMonitor>();
                foreach (Dictionary<string, object> d in Items(r, "monitors"))
                {
                    EngineMonitor m = new EngineMonitor();
                    m.Id = Str(d, "id"); m.Device = Str(d, "device"); m.Name = Str(d, "name");
                    m.X = (int)Num(d, "x"); m.Y = (int)Num(d, "y"); m.Width = (int)Num(d, "width"); m.Height = (int)Num(d, "height");
                    m.Primary = Flag(d, "primary");
                    mons.Add(m);
                }
                _monitors = mons;
                return list;
            }
        }

        /// <summary>Displays as the helper sees them (ids are valid for <see cref="EngineConfig.MonitorId"/>).</summary>
        public List<EngineMonitor> Monitors()
        {
            Probe();
            return _monitors;
        }

        /// <summary>Starts recording; returns when it really runs. From then on the client keeps it running.</summary>
        public void Start(EngineConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            lock (_op)
            {
                _wanted = null;
                Link link = EnsureLink();
                if (_status.Recording) { try { Request(link, Msg("stop"), ReplyTimeoutMs); } catch (EngineException) { } }
                DoStart(link, config);
                _wanted = config;
                _backoff = 0;
                _healthySinceUtc = DateTime.UtcNow;
            }
        }

        /// <summary>Stops recording and ends the helper. The ring files stay on disk.</summary>
        public void Stop()
        {
            lock (_op)
            {
                _wanted = null;
                EndLink(true);
            }
        }

        /// <summary>Test hook: sends one raw request to a running helper and returns its reply.</summary>
        public Dictionary<string, object> DebugRequest(string cmd, Dictionary<string, object> fields, int timeoutMs)
        {
            Dictionary<string, object> m = Msg(cmd);
            if (fields != null) foreach (KeyValuePair<string, object> kv in fields) m[kv.Key] = kv.Value;
            return Request(_link, m, timeoutMs);
        }

        public SaveResult SaveLast(int seconds, string destPath)
        {
            return SaveLast(seconds, destPath, null);
        }

        /// <summary>
        /// Writes the last <paramref name="seconds"/> of footage (0 = everything buffered) to an MP4 and returns
        /// when the file is complete. The clip starts on the keyframe at or before the requested point, so it
        /// can be up to one segment longer. <paramref name="progress"/> gets the share of the footage that is in
        /// the file so far: it only moves forward (in steps of at least half a percent), stays below 1 while the
        /// file is being written, and is called with exactly 1 once, after the finished file has its final name.
        /// It is called on the thread that called SaveLast.
        /// </summary>
        public SaveResult SaveLast(int seconds, string destPath, Action<double> progress)
        {
            if (Interlocked.Exchange(ref _saving, 1) == 1) throw new EngineException("busy", "another save is still running");
            try { return DoSave(seconds, destPath, progress); }
            finally { Interlocked.Exchange(ref _saving, 0); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { Process f = _ffmpegProc; if (f != null && !f.HasExited) f.Kill(); } catch { }
            lock (_op)
            {
                _wanted = null;
                EndLink(true);
                if (_job != IntPtr.Zero) { CloseHandle(_job); _job = IntPtr.Zero; }   // kill-on-close: takes any stray helper with it
            }
        }

        // ------------------------------------------------------------------ helper process + pipe

        sealed class Waiter
        {
            public readonly ManualResetEvent Done = new ManualResetEvent(false);
            public Dictionary<string, object> Reply;
        }

        sealed class Link
        {
            public Process Proc;
            public NamedPipeServerStream Pipe;
            public readonly object WriteGate = new object();
            public readonly Dictionary<long, Waiter> Pending = new Dictionary<long, Waiter>();
            public readonly ManualResetEvent Ready = new ManualResetEvent(false);
            public volatile bool Dead;
            public volatile bool Closing;     // we asked it to quit
            public string Fatal;
            public DateTime LastMessageUtc = DateTime.UtcNow;
            public long NextId = 1;
        }

        void Log(string text)
        {
            try { if (_log != null) _log("engine: " + text); } catch { }
        }

        Link EnsureLink()
        {
            if (_disposed) throw new EngineException("disposed", "the engine client was disposed");
            Link l = _link;
            _lastUseUtc = DateTime.UtcNow;
            if (l != null && !l.Dead) return l;
            l = StartHelper();
            _link = l;
            return l;
        }

        Link StartHelper()
        {
            if (!File.Exists(_exe)) throw new EngineException("helper_missing", "engine helper not found: " + _exe);
            Link l = new Link();
            string name = "ReplayClipper.Engine." + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N").Substring(0, 8);
            PipeSecurity sec = new PipeSecurity();
            sec.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User, PipeAccessRights.FullControl, AccessControlType.Allow));
            // Asynchronous = overlapped handle: a blocked read must not hold up writes from another thread.
            l.Pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 16, 1 << 16, sec);
            try
            {
                string engineData = Path.Combine(_dataDir, "engine");
                ProcessStartInfo si = new ProcessStartInfo(_exe);
                si.Arguments = "--pipe " + name + " --parent " + Process.GetCurrentProcess().Id
                               + " --parent-start " + Process.GetCurrentProcess().StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture)
                               + " --config-dir " + Quote(Path.Combine(engineData, "config")) + " --log-dir " + Quote(Path.Combine(engineData, "logs"))
                               + (Debug ? " --debug" : "");
                si.WorkingDirectory = Path.GetDirectoryName(_exe);
                si.UseShellExecute = false;
                si.CreateNoWindow = true;
                // A proxy must never matter to a recorder; the helper makes no network requests anyway.
                foreach (string v in new string[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
                    if (si.EnvironmentVariables.ContainsKey(v)) si.EnvironmentVariables.Remove(v);
                l.Proc = Process.Start(si);
                AssignToJob(l.Proc);

                IAsyncResult ar = l.Pipe.BeginWaitForConnection(null, null);
                Stopwatch sw = Stopwatch.StartNew();
                while (!ar.AsyncWaitHandle.WaitOne(100))
                {
                    if (l.Proc.HasExited) throw new EngineException("helper_failed", "engine helper exited with code " + l.Proc.ExitCode + " before connecting");
                    if (sw.ElapsedMilliseconds > 15000) throw new EngineException("helper_failed", "engine helper did not connect");
                }
                l.Pipe.EndWaitForConnection(ar);

                Thread t = new Thread(delegate() { ReadLoop(l); });
                t.IsBackground = true;
                t.Name = "EngineClient.Read";
                t.Start();

                sw.Restart();
                while (!l.Ready.WaitOne(100))
                {
                    if (l.Dead || l.Fatal != null)
                        throw new EngineException("helper_failed", l.Fatal ?? ("engine helper exited" + ExitText(l)));
                    if (sw.ElapsedMilliseconds > 30000) throw new EngineException("helper_failed", "engine helper did not become ready");
                }
                Log("helper pid " + l.Proc.Id + " ready");
                SetStatus(delegate(EngineStatus s) { s.HelperRunning = true; s.HelperPid = l.Proc.Id; });
                return l;
            }
            catch
            {
                Kill(l);
                throw;
            }
        }

        static string ExitText(Link l)
        {
            try { return l.Proc.WaitForExit(1000) ? " with code " + l.Proc.ExitCode : ""; } catch { return ""; }
        }

        void Kill(Link l)
        {
            l.Dead = true;
            try { if (l.Proc != null && !l.Proc.HasExited) l.Proc.Kill(); } catch { }
            try { l.Pipe.Dispose(); } catch { }
            FailPending(l);
        }

        void FailPending(Link l)
        {
            lock (l.Pending)
            {
                foreach (Waiter w in l.Pending.Values) w.Done.Set();
                l.Pending.Clear();
            }
        }

        void EndLink(bool graceful)
        {
            Link l = _link;
            _link = null;
            if (l == null) return;
            if (!l.Dead && graceful)
            {
                l.Closing = true;
                try { Request(l, Msg("stop"), ReplyTimeoutMs); } catch (EngineException) { }
                try { Request(l, Msg("quit"), 3000); } catch (EngineException) { }
                try { l.Proc.WaitForExit(8000); } catch { }
            }
            Kill(l);
            SetStatus(delegate(EngineStatus s) { s.HelperRunning = false; s.Recording = false; s.HelperPid = 0; });
        }

        void ReadLoop(Link l)
        {
            try
            {
                StreamReader r = new StreamReader(l.Pipe, new UTF8Encoding(false), false, 1 << 16);
                JavaScriptSerializer js = NewJs();
                for (;;)
                {
                    string line = r.ReadLine();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    Dictionary<string, object> d;
                    try { d = js.Deserialize<Dictionary<string, object>>(line); }
                    catch (Exception ex) { Log("unreadable message from helper: " + ex.Message); continue; }
                    l.LastMessageUtc = DateTime.UtcNow;
                    if (d.ContainsKey("id"))
                    {
                        Waiter w = null;
                        lock (l.Pending)
                        {
                            long id = Num(d, "id");
                            if (l.Pending.TryGetValue(id, out w)) l.Pending.Remove(id);
                        }
                        if (w != null) { w.Reply = d; w.Done.Set(); }
                    }
                    else OnEvent(l, d);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            catch (Exception ex) { Log("pipe reader: " + ex.Message); }
            bool wasDead = l.Dead;
            l.Dead = true;
            FailPending(l);
            if (!wasDead && !l.Closing)
            {
                string exit = ExitText(l);
                Log("helper connection lost" + exit);
                SetStatus(delegate(EngineStatus s)
                {
                    s.HelperRunning = false; s.Recording = false; s.HelperPid = 0;
                    if (_wanted != null) { s.LastError = "helper_lost"; s.LastErrorMessage = "the recording engine stopped unexpectedly" + exit; }
                });
            }
        }

        void OnEvent(Link l, Dictionary<string, object> d)
        {
            string ev = Str(d, "event");
            if (ev == "ready") l.Ready.Set();
            else if (ev == "fatal") { l.Fatal = Str(d, "error") + ": " + Str(d, "message"); Log("helper fatal: " + l.Fatal); }
            else if (ev == "stats") SetStatus(delegate(EngineStatus s) { ApplyStatus(s, d); });
            else if (ev == "stopped")
            {
                Log("recording stopped by itself: " + Str(d, "error") + " " + Str(d, "message"));
                SetStatus(delegate(EngineStatus s) { s.Recording = false; s.LastError = Str(d, "error"); s.LastErrorMessage = Str(d, "message"); });
            }
        }

        static void ApplyStatus(EngineStatus s, Dictionary<string, object> d)
        {
            s.Recording = Flag(d, "recording");
            s.Encoder = Str(d, "encoder"); s.Codec = Str(d, "codec");
            s.Width = (int)Num(d, "width"); s.Height = (int)Num(d, "height"); s.Fps = (int)Num(d, "fps");
            long since = Num(d, "since");
            s.SinceUtc = since > 0 ? new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(since) : DateTime.MinValue;
            s.BufferedSeconds = Dbl(d, "bufferedSeconds");
            s.Frames = Num(d, "frames"); s.LaggedFrames = Num(d, "laggedFrames"); s.SkippedFrames = Num(d, "skippedFrames");
            s.RingFiles = (int)Num(d, "ringFiles"); s.RingBytes = Num(d, "ringBytes"); s.PruneErrors = (int)Num(d, "pruneErrors");
            s.HelperPrivateMB = Dbl(d, "privateMB");
            s.RingTickMsMax = Num(d, "tickMsMax");
        }

        void SetStatus(Action<EngineStatus> change)
        {
            lock (_state)
            {
                EngineStatus n = (EngineStatus)CloneStatus(_status);
                change(n);
                _status = n;
            }
            // Raised on a pool thread: a handler may call back into this client (Stop, SaveLast) without
            // blocking the pipe reader that those calls depend on.
            Action h = Changed;
            if (h != null) ThreadPool.QueueUserWorkItem(delegate { try { h(); } catch { } });
        }

        static EngineStatus CloneStatus(EngineStatus o)
        {
            EngineStatus n = new EngineStatus();
            n.HelperRunning = o.HelperRunning; n.Recording = o.Recording; n.Encoder = o.Encoder; n.Codec = o.Codec;
            n.Width = o.Width; n.Height = o.Height; n.Fps = o.Fps; n.SinceUtc = o.SinceUtc; n.BufferedSeconds = o.BufferedSeconds;
            n.Frames = o.Frames; n.LaggedFrames = o.LaggedFrames; n.SkippedFrames = o.SkippedFrames;
            n.LastError = o.LastError; n.LastErrorMessage = o.LastErrorMessage;
            n.RingFiles = o.RingFiles; n.RingBytes = o.RingBytes; n.PruneErrors = o.PruneErrors;
            n.HelperPrivateMB = o.HelperPrivateMB; n.HelperPid = o.HelperPid; n.Restarts = o.Restarts; n.RingTickMsMax = o.RingTickMsMax;
            return n;
        }

        // ------------------------------------------------------------------ requests

        static Dictionary<string, object> Msg(string cmd)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["cmd"] = cmd;
            return d;
        }

        static JavaScriptSerializer NewJs()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;     // a save reply for a 12-hour ring is a couple of megabytes
            js.RecursionLimit = 64;
            return js;
        }

        /// <summary>Sends one request and waits for its reply. Throws EngineException on error replies and timeouts.</summary>
        Dictionary<string, object> Request(Link l, Dictionary<string, object> msg, int timeoutMs)
        {
            if (l == null || l.Dead) throw new EngineException("helper_lost", "the recording engine is not running");
            Waiter w = new Waiter();
            long id;
            lock (l.Pending) { id = l.NextId++; l.Pending[id] = w; }
            msg["id"] = id;
            string json = NewJs().Serialize(msg);
            StringBuilder sb = new StringBuilder(json.Length + 16);
            foreach (char c in json)
            {
                if (c > 0x7E || c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            sb.Append('\n');
            byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());
            try
            {
                lock (l.WriteGate) { l.Pipe.Write(bytes, 0, bytes.Length); l.Pipe.Flush(); }
            }
            catch (Exception ex)
            {
                lock (l.Pending) l.Pending.Remove(id);
                throw new EngineException("helper_lost", "cannot reach the recording engine: " + ex.Message);
            }
            if (!w.Done.WaitOne(timeoutMs))
            {
                lock (l.Pending) l.Pending.Remove(id);
                throw new EngineException("timeout", "the recording engine did not answer \"" + msg["cmd"] + "\" in time");
            }
            Dictionary<string, object> r = w.Reply;
            if (r == null) throw new EngineException("helper_lost", "the recording engine stopped while handling \"" + msg["cmd"] + "\"");
            if (!Flag(r, "ok")) throw new EngineException(Str(r, "error"), Str(r, "message"));
            return r;
        }

        void DoStart(Link link, EngineConfig c)
        {
            Dictionary<string, object> m = Msg("start");
            m["monitorId"] = c.MonitorId ?? "";
            m["width"] = c.Width; m["height"] = c.Height; m["fps"] = c.Fps;
            m["cursor"] = c.Cursor;
            if (!string.IsNullOrEmpty(c.Method)) m["method"] = c.Method;
            JavaScriptSerializer js = NewJs();
            ArrayList encs = new ArrayList();
            foreach (EncoderChoice e in c.Encoders)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["id"] = e.Id;
                object settings = null;
                if (!string.IsNullOrEmpty(e.SettingsJson))
                {
                    try { settings = js.DeserializeObject(e.SettingsJson); }
                    catch (Exception ex) { throw new EngineException("bad_config", "settings of " + e.Id + " are not valid JSON: " + ex.Message); }
                }
                d["settings"] = settings ?? new Dictionary<string, object>();
                encs.Add(d);
            }
            m["encoders"] = encs;
            m["audioBitrateKbps"] = c.AudioBitrateKbps;
            m["desktopAudio"] = c.DesktopAudio; m["mic"] = c.Mic;
            m["bufferSeconds"] = c.BufferSeconds; m["segmentSeconds"] = c.SegmentSeconds;
            m["ringDir"] = c.RingDir ?? "";
            if (c.Extra != null) foreach (KeyValuePair<string, object> kv in c.Extra) m[kv.Key] = kv.Value;

            // a format change makes the helper delete the old ring first; with a 12-hour ring that takes a while
            Dictionary<string, object> r = Request(link, m, 120000);
            _lastFrames = -1; _lastFramesChangeUtc = DateTime.UtcNow;
            Log("recording with " + Str(r, "encoder") + " " + Num(r, "width") + "x" + Num(r, "height") + "@" + Num(r, "fps"));
            SetStatus(delegate(EngineStatus s)
            {
                s.Recording = true; s.Encoder = Str(r, "encoder"); s.Codec = Str(r, "codec");
                s.Width = (int)Num(r, "width"); s.Height = (int)Num(r, "height"); s.Fps = (int)Num(r, "fps");
                s.SinceUtc = DateTime.UtcNow; s.LastError = ""; s.LastErrorMessage = "";
            });
        }

        // ------------------------------------------------------------------ supervision

        void Supervise()
        {
            while (!_disposed)
            {
                Thread.Sleep(500);
                if (!Monitor.TryEnter(_op)) continue;       // a public call is in progress; look again later
                try
                {
                    if (_disposed) return;
                    Link l = _link;
                    DateTime now = DateTime.UtcNow;
                    if (_wanted == null)
                    {
                        // nothing wanted: do not leave an idle helper around after a Probe
                        if (l != null && !l.Dead && _saving == 0 && (now - _lastUseUtc).TotalSeconds > 60) EndLink(true);
                        else if (l != null && l.Dead) EndLink(false);
                        continue;
                    }
                    bool alive = l != null && !l.Dead;
                    EngineStatus st = _status;
                    if (alive && st.Recording)
                    {
                        // The helper sends stats every 2 s; frames must keep counting.
                        if (st.Frames != _lastFrames) { _lastFrames = st.Frames; _lastFramesChangeUtc = now; }
                        bool silent = (now - l.LastMessageUtc).TotalSeconds > 15;
                        bool stalled = (now - _lastFramesChangeUtc).TotalSeconds > 20;
                        if (silent || stalled)
                        {
                            Log(silent ? "helper stopped answering; restarting it" : "no new frames for 20 s; restarting the helper");
                            Kill(l);
                            SetStatus(delegate(EngineStatus s) { s.HelperRunning = false; s.Recording = false; s.LastError = "helper_hung"; s.LastErrorMessage = silent ? "the recording engine stopped answering" : "the recording engine stopped producing frames"; });
                            continue;
                        }
                        if (_backoff > 0 && (now - _healthySinceUtc).TotalSeconds > 60) _backoff = 0;
                        continue;
                    }
                    if (now < _nextAttemptUtc) continue;
                    int wait = BackoffSeconds[Math.Min(_backoff, BackoffSeconds.Length - 1)];
                    _backoff++;
                    _nextAttemptUtc = now.AddSeconds(wait);
                    try
                    {
                        if (!alive) { EndLink(false); l = EnsureLink(); }
                        DoStart(l, _wanted);
                        _healthySinceUtc = DateTime.UtcNow;
                        SetStatus(delegate(EngineStatus s) { s.Restarts++; });
                        Log("recording resumed");
                    }
                    catch (EngineException ex)
                    {
                        Log("restart failed (" + ex.Code + "): " + ex.Message + "; next try in " + wait + " s");
                        SetStatus(delegate(EngineStatus s) { s.LastError = ex.Code; s.LastErrorMessage = ex.Message; });
                    }
                }
                catch (Exception ex) { Log("supervisor: " + ex.Message); }
                finally { Monitor.Exit(_op); }
            }
        }

        // ------------------------------------------------------------------ save

        sealed class SegRef { public string Path; public long Bytes, V, A, Ticks; }

        /// <summary>
        /// Longest clip (in 90 kHz ticks, 6 h 10 min) the direct join takes on. From 2^31 ticks (6 h 37 min)
        /// FFmpeg writes the MP4 time fields with 64 bits; this stays clear of that.
        /// </summary>
        const long MaxDirectTicks = 2000000000;

        SaveResult DoSave(int seconds, string destPath, Action<double> progress)
        {
            if (string.IsNullOrEmpty(_ffmpeg) || !File.Exists(_ffmpeg)) throw new EngineException("ffmpeg_missing", "ffmpeg not found: " + _ffmpeg);
            Link link = _link;
            _lastUseUtc = DateTime.UtcNow;
            Dictionary<string, object> m = Msg("save");
            m["seconds"] = seconds;
            Dictionary<string, object> r = Request(link, m, 30000);
            long saveId = Num(r, "saveId");
            string dir = Str(r, "dir");
            string codec = Str(r, "codec");
            List<SegRef> segs = new List<SegRef>();
            long totalBytes = 0;
            foreach (Dictionary<string, object> d in Items(r, "segments"))
            {
                SegRef s = new SegRef();
                s.Path = Path.Combine(dir, Str(d, "file")); s.Bytes = Num(d, "bytes"); s.V = Num(d, "v"); s.A = Num(d, "a"); s.Ticks = Num(d, "ticks");
                segs.Add(s); totalBytes += s.Bytes;
            }
            if (segs.Count == 0) throw new EngineException("nothing_buffered", "the recording engine listed no footage");
            SegRef last = segs[segs.Count - 1];
            double expected = (last.V + last.Ticks - segs[0].V) / 90000.0;

            string tmp = destPath + ".part";
            Stopwatch sw = Stopwatch.StartNew();
            // Progress only ever moves forward and stays below 1 until the finished file is in place.
            double shown = 0;
            Action<double> report = delegate(double p)
            {
                if (progress == null) return;
                p = Math.Min(p, 0.99);
                if (p < shown + 0.005) return;      // forward only, in steps of half a percent
                shown = p;
                try { progress(p); } catch { }
            };
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destPath)));
                long fps = Math.Max(1, Num(r, "fps"));
                string how = "direct";
                double duration = 0;
                bool done = false, inBandPs = false;
                // First choice: write the MP4 ourselves (Mp4Join). ffmpeg reading the footage spends most of its
                // time in its H.264/H.265 stream parser; the direct join needs no parser and uses several cores.
                // Not for clips of about six hours and more: their MP4 header needs 64-bit time fields, which
                // only ffmpeg writes here.
                bool direct = (codec == "hevc" || codec == "h264") && last.V + last.Ticks - segs[0].V < MaxDirectTicks;
                if (DebugJoin == "ffmpeg") direct = false;
                if (direct)
                {
                    try
                    {
                        if (File.Exists(tmp)) File.Delete(tmp);
                        new Mp4Join(this, segs, totalBytes, codec, (int)fps, last.V + last.Ticks - segs[0].V, saveId, report).Run(tmp);
                        duration = Mp4Duration(tmp);
                        if (duration < expected - 0.25)
                            throw new InvalidDataException("the MP4 is " + duration.ToString("F2", CultureInfo.InvariantCulture) + " s of "
                                                           + expected.ToString("F2", CultureInfo.InvariantCulture) + " s");
                        done = true;
                    }
                    catch (Exception ex)
                    {
                        if (_disposed || DebugJoin == "direct") throw;
                        // anything unexpected in the footage: the slower, proven way still applies
                        inBandPs = ex is Mp4Join.ParameterSetsChanged;
                        Log("save: direct join failed (" + ex.Message.Trim() + "); joining with ffmpeg instead");
                    }
                }
                if (!done)
                {
                    how = "ffmpeg";
                    duration = FfmpegJoin(segs, totalBytes, codec, inBandPs, fps, expected, tmp, saveId, report);
                }
                else if (DebugJoin == "both")
                {
                    // test only: the same footage joined by ffmpeg as well, for comparison
                    string other = destPath + ".ffmpeg.mp4";
                    FfmpegJoin(segs, totalBytes, codec, false, fps, expected, other + ".part", saveId, delegate(double p) { });
                    if (File.Exists(other)) File.Delete(other);
                    File.Move(other + ".part", other);
                }
                if (File.Exists(destPath)) File.Delete(destPath);
                File.Move(tmp, destPath);
                SaveResult res = new SaveResult();
                res.Path = destPath; res.Duration = duration; res.Size = new FileInfo(destPath).Length;
                Log("saved " + destPath + ": " + duration.ToString("F2", CultureInfo.InvariantCulture) + " s, " + segs.Count + " segment(s), "
                    + (res.Size / 1048576.0).ToString("F1", CultureInfo.InvariantCulture) + " MB in " + sw.ElapsedMilliseconds + " ms (" + how + ")");
                if (progress != null) { try { progress(1.0); } catch { } }
                return res;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                Log("save failed: " + ex.Message);
                if (ex is EngineException) throw;
                throw new EngineException("save_failed", ex.Message);
            }
            finally
            {
                Dictionary<string, object> rel = Msg("release");
                rel["saveId"] = saveId;
                try { Request(_link, rel, 5000); } catch (EngineException) { }
            }
        }

        /// <summary>
        /// The join as version 2.0 did it: the segments go to ffmpeg as one MPEG-TS stream and ffmpeg writes the
        /// MP4. Used when the direct join declines the footage. Returns the MP4's duration.
        /// </summary>
        double FfmpegJoin(List<SegRef> segs, long totalBytes, string codec, bool inBandPs, long fps, double expected, string tmp, long saveId, Action<double> report)
        {
            // Room for the index is reserved at the front so ffmpeg does not rewrite the whole file at the end;
            // should the reserve be too small, ffmpeg fails at the very end and the join is repeated the slow way.
            long samples = (long)(expected * fps) + (long)(expected * 48000 / 1024) + 2;
            long reserve = DebugIndexReserve > 0 ? DebugIndexReserve : 65536 + 32 * samples;
            string[] index = { "-moov_size " + reserve.ToString(CultureInfo.InvariantCulture), "-movflags +faststart" };
            string failure = "";
            for (int attempt = 0; attempt < index.Length; attempt++)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                JoinRun run = RunJoin(segs, totalBytes, codec, inBandPs, index[attempt], tmp, saveId, report);
                if (run.InputError != null) throw new EngineException("save_failed", "cannot read the footage: " + run.InputError);
                double duration = run.ExitCode == 0 && run.PipeError == null ? Mp4Duration(tmp) : 0;
                if (run.ExitCode == 0 && run.PipeError == null && duration >= expected - 0.25)
                {
                    if (run.Err.Length > 0) Log("save: ffmpeg said: " + run.Err);
                    return duration;
                }
                failure = "ffmpeg exit code " + run.ExitCode + (run.PipeError != null ? ", stopped reading (" + run.PipeError + ")" : "")
                          + (run.ExitCode == 0 ? ", MP4 is " + duration.ToString("F2", CultureInfo.InvariantCulture) + " s of "
                                                 + expected.ToString("F2", CultureInfo.InvariantCulture) + " s" : "")
                          + (run.Err.Length > 0 ? ": " + run.Err : "");
                if (attempt + 1 < index.Length) Log("save: first attempt failed (" + failure + "); joining again");
            }
            throw new EngineException("save_failed", failure);
        }

        /// <summary>
        /// Writes the MP4 directly from the ring segments (MPEG-TS as obs-ffmpeg-mux writes it: one video frame
        /// per PES, AAC as ADTS), without sending the footage through ffmpeg.
        ///   - Worker threads unpack one segment each: PES timestamps get the shift the helper computed, video
        ///     frames go from Annex B start codes to length-prefixed NAL units exactly as FFmpeg's MP4 muxer does
        ///     it, AAC frames lose their ADTS header.
        ///   - This thread writes the converted segments in order and collects the sample tables.
        ///   - ffmpeg still makes a small MP4 from the clip's first segment alone, in parallel. Everything
        ///     codec-specific (sample entries with hvcC/avcC/esds, track and media headers) is taken from it,
        ///     and it is the referee: the first segment as converted here must agree with ffmpeg's version in
        ///     every sample (bytes, size, timing, keyframes) and in the header fields computed here (edit lists,
        ///     durations). Only then is the direct file kept; on any disagreement the caller joins with ffmpeg.
        /// The file is laid out index first: [ftyp][moov][padding][mdat].
        /// </summary>
        sealed class Mp4Join
        {
            const int Packet = 188;
            const int AudioBatch = 24;          // audio frames written together (about half a second)
            // What ffmpeg uses for footage that comes from MPEG-TS; checked against the header it writes.
            const long VideoScale = 90000, AudioScale = 48000, AudioFrame = 1024;

            readonly EngineClient _owner;
            readonly List<SegRef> _segs;
            readonly long _totalBytes, _ticks, _saveId;
            readonly string _codec;
            readonly int _fps;
            readonly Action<double> _report;
            readonly bool _dropHevcPs;
            readonly Track _v, _a;

            public Mp4Join(EngineClient owner, List<SegRef> segs, long totalBytes, string codec, int fps, long ticks, long saveId, Action<double> report)
            {
                _owner = owner; _segs = segs; _totalBytes = totalBytes; _codec = codec; _fps = fps; _ticks = ticks; _saveId = saveId; _report = report;
                // ffmpeg is asked for the "hvc1" sample entry, which keeps parameter sets out of the samples
                _dropHevcPs = codec == "hevc";
                _v = new Track(VideoScale, RoundNear(VideoScale, fps));
                _a = new Track(AudioScale, AudioFrame);
            }

            /// <summary>Thrown when HEVC parameter sets differ inside the clip ("hvc1" cannot carry that).</summary>
            public sealed class ParameterSetsChanged : Exception
            {
                public ParameterSetsChanged() : base("the HEVC parameter sets change inside the clip") { }
            }

            static InvalidDataException Bad(string what) { return new InvalidDataException(what); }

            // a / b rounded as FFmpeg's av_rescale does it: to nearest (halves away from zero), away from zero
            static long RoundNear(long a, long b) { return a >= 0 ? (a + b / 2) / b : -((-a + b / 2) / b); }
            static long RoundUp(long a, long b) { return a >= 0 ? (a + b - 1) / b : -((-a + b - 1) / b); }

            // ---------------------------------------------------------------- HEVC parameter sets

            // Every distinct VPS/SPS/PPS that was left out of the samples. "hvc1" keeps them in the sample
            // entry only, so each one must be in the entry ffmpeg wrote.
            readonly List<byte[]> _paramSets = new List<byte[]>();
            bool _tooManyParamSets;

            void SeenParameterSet(byte[] b, int off, int len)
            {
                lock (_paramSets)
                {
                    foreach (byte[] p in _paramSets)
                    {
                        if (p.Length != len) continue;
                        int i = 0;
                        while (i < len && p[i] == b[off + i]) i++;
                        if (i == len) return;
                    }
                    if (_paramSets.Count >= 64) { _tooManyParamSets = true; return; }
                    byte[] n = new byte[len];
                    Buffer.BlockCopy(b, off, n, 0, len);
                    _paramSets.Add(n);
                }
            }

            bool ParameterSetsVary(byte[] sampleEntry)
            {
                lock (_paramSets)
                {
                    if (_tooManyParamSets) return true;
                    foreach (byte[] p in _paramSets)
                    {
                        bool found = false;
                        for (int at = 0; at + p.Length <= sampleEntry.Length && !found; at++)
                        {
                            int i = 0;
                            while (i < p.Length && sampleEntry[at + i] == p[i]) i++;
                            found = i == p.Length;
                        }
                        if (!found) return true;
                    }
                    return false;
                }
            }

            // ---------------------------------------------------------------- one converted segment

            sealed class Part
            {
                public byte[] Data;
                public int Len;
                public int VCount, ACount;
                public int[] VSize = new int[160], VCts = new int[160];
                public long[] VDts = new long[160];
                public bool[] VKey = new bool[160];
                public int[] ASize = new int[128];
                public long[] ADts = new long[128];
                public readonly List<int> Chunks = new List<int>();     // triples: track (0 video, 1 audio), samples, offset in Data
                public Exception Error;

                public void Need(int more)
                {
                    if (Len + more > Data.Length) Array.Resize(ref Data, Math.Max(Data.Length + (Data.Length >> 2), Len + more + 65536));
                }

                public void AddVideo(int size, long dts, long pts, bool key)
                {
                    if (VCount == VSize.Length)
                    {
                        int n = VCount * 2;
                        Array.Resize(ref VSize, n); Array.Resize(ref VCts, n); Array.Resize(ref VDts, n); Array.Resize(ref VKey, n);
                    }
                    if (pts < dts || pts - dts > int.MaxValue) throw Bad("frame shown before it is decoded");
                    VSize[VCount] = size; VDts[VCount] = dts; VCts[VCount] = (int)(pts - dts); VKey[VCount] = key;
                    VCount++;
                }

                public void AddAudio(int size, long dts)
                {
                    if (ACount == ASize.Length) { int n = ACount * 2; Array.Resize(ref ASize, n); Array.Resize(ref ADts, n); }
                    ASize[ACount] = size; ADts[ACount] = dts;
                    ACount++;
                }
            }

            // ---------------------------------------------------------------- segment -> Part (one per worker thread)

            sealed class Converter
            {
                readonly bool _dropHevcPs;
                readonly Mp4Join _join;
                byte[] _in = new byte[6 << 20];
                byte[] _vbuf = new byte[1 << 20], _abuf = new byte[8 << 10], _batch = new byte[32 << 10];
                int _vlen, _alen, _batchLen, _batchSamples, _vpid, _apid, _lastTrack;
                bool _vopen, _aopen, _vkey;
                long _vpts, _vdts, _apts;
                Part _part;

                public Converter(Mp4Join join) { _join = join; _dropHevcPs = join._dropHevcPs; }

                public Part Convert(SegRef s, Func<int, byte[]> rent)
                {
                    long want = s.Bytes / Packet * Packet;
                    if (want > 1 << 30) throw Bad("segment too large: " + s.Path);
                    int len = (int)want;
                    if (_in.Length < len) _in = new byte[len + (len >> 3)];
                    // ReadWrite|Delete sharing: the newest file is still being written by the muxer
                    using (FileStream fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
                    {
                        int got = 0;
                        while (got < len)
                        {
                            int n = fs.Read(_in, got, len - got);
                            if (n <= 0) throw new IOException("segment is shorter than listed: " + s.Path);
                            got += n;
                        }
                    }
                    _part = new Part();
                    _part.Data = rent(len + (len >> 6) + 65536);
                    _vopen = _aopen = false; _vlen = _alen = 0; _vpid = _apid = -1; _lastTrack = -1; _batchLen = 0; _batchSamples = 0;
                    Unpack(_in, len, s.V, s.A);
                    // a listed segment always ends on a PES boundary
                    FinishVideo(); FinishAudio(); FlushAudioBatch();
                    Part p = _part;
                    _part = null;
                    return p;
                }

                static long ReadTs(byte[] b, int i)
                {
                    return ((long)(uint)((b[i] >> 1) & 0x07) << 30) | ((long)b[i + 1] << 22) | ((long)(uint)((b[i + 2] >> 1) & 0x7F) << 15)
                           | ((long)b[i + 3] << 7) | (long)(uint)((b[i + 4] >> 1) & 0x7F);
                }

                void Unpack(byte[] b, int len, long vShift, long aShift)
                {
                    for (int o = 0; o < len; o += Packet)
                    {
                        if (b[o] != 0x47) throw Bad("not an MPEG-TS packet");
                        int afc = b[o + 3] & 0x30;
                        if ((afc & 0x10) == 0) continue;                            // no payload
                        int pid = ((b[o + 1] & 0x1F) << 8) | b[o + 2];
                        int p = o + 4;
                        if (afc == 0x30) p += 1 + b[o + 4];
                        int end = o + Packet;
                        if (p >= end) continue;
                        if ((b[o + 1] & 0x40) != 0)
                        {
                            if (p + 9 > end || b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) continue;     // a table, not a PES
                            int sid = b[p + 3];
                            bool video = sid >= 0xE0 && sid <= 0xEF;
                            if (!video && (sid < 0xC0 || sid > 0xDF)) continue;
                            int flags = b[p + 7] >> 6;
                            if ((flags & 2) == 0 || p + 14 > end) throw Bad("PES without a timestamp");
                            long pts = ReadTs(b, p + 9);
                            if (video)
                            {
                                if (_vpid >= 0 && pid != _vpid) throw Bad("more than one video stream");
                                FinishVideo();
                                _vpts = pts + vShift;
                                _vdts = (flags == 3 && p + 19 <= end) ? ReadTs(b, p + 14) + vShift : _vpts;
                                // the muxer sets random_access_indicator on exactly the keyframes
                                _vkey = afc == 0x30 && b[o + 4] > 0 && (b[o + 5] & 0x40) != 0;
                                _vopen = true; _vpid = pid;
                            }
                            else
                            {
                                if (_apid >= 0 && pid != _apid) throw Bad("more than one audio stream");
                                FinishAudio();
                                _apts = pts + aShift;
                                _aopen = true; _apid = pid;
                            }
                            p += 9 + b[p + 8];
                            if (p > end) throw Bad("PES header too long");
                        }
                        int n = end - p;
                        if (pid == _vpid && _vopen)
                        {
                            if (_vlen + n > _vbuf.Length) Array.Resize(ref _vbuf, _vbuf.Length * 2);
                            Buffer.BlockCopy(b, p, _vbuf, _vlen, n);
                            _vlen += n;
                        }
                        else if (pid == _apid && _aopen)
                        {
                            if (_alen + n > _abuf.Length) Array.Resize(ref _abuf, _abuf.Length * 2);
                            Buffer.BlockCopy(b, p, _abuf, _alen, n);
                            _alen += n;
                        }
                    }
                }

                void Chunk(int track, int samples)
                {
                    List<int> c = _part.Chunks;
                    if (_lastTrack == track) c[c.Count - 2] += samples;
                    else { c.Add(track); c.Add(samples); c.Add(_part.Len); _lastTrack = track; }
                }

                /// <summary>First "00 00 01" in [p, end), backed up by one when a zero precedes it; end when there is
                /// none. Same contract as FFmpeg's ff_nal_find_startcode.</summary>
                static int FindStartCode(byte[] b, int p, int end)
                {
                    int i = p + 2;
                    while (i < end)
                    {
                        byte c = b[i];
                        if (c > 1) { i += 3; continue; }
                        if (c == 1)
                        {
                            if (b[i - 1] == 0 && b[i - 2] == 0)
                            {
                                int s = i - 2;
                                return (s > p && b[s - 1] == 0) ? s - 1 : s;
                            }
                            i += 3;
                        }
                        else i++;       // a zero: the 01 may be one or two bytes further on
                    }
                    return end;
                }

                void FinishVideo()
                {
                    if (!_vopen) return;
                    _vopen = false;
                    if (_vlen == 0) return;
                    // Annex B -> 4-byte length + NAL unit, with the same result as FFmpeg 9's MP4 muxer
                    byte[] b = _vbuf;
                    int end = _vlen, total = 0;
                    _vlen = 0;
                    Part part = _part;
                    part.Need(end + 64);
                    Chunk(0, 1);
                    int nal = FindStartCode(b, 0, end);
                    for (;;)
                    {
                        while (nal < end && b[nal++] == 0) { }
                        if (nal >= end) break;
                        int next = FindStartCode(b, nal, end);
                        int len = next - nal;
                        // zero bytes after a NAL unit are padding of the byte stream, not part of the unit
                        while (len > 0 && b[nal + len - 1] == 0) len--;
                        if (len == 0) throw Bad("empty NAL unit");
                        // "hvc1" keeps parameter sets in the sample entry only: FFmpeg drops VPS/SPS/PPS from the samples
                        if (_dropHevcPs)
                        {
                            int type = (b[nal] >> 1) & 0x3F;
                            if (type >= 32 && type <= 34) { _join.SeenParameterSet(b, nal, len); nal = next; continue; }
                        }
                        part.Need(len + 4);
                        byte[] d = part.Data;
                        int at = part.Len;
                        d[at] = (byte)(len >> 24); d[at + 1] = (byte)(len >> 16); d[at + 2] = (byte)(len >> 8); d[at + 3] = (byte)len;
                        Buffer.BlockCopy(b, nal, d, at + 4, len);
                        part.Len = at + 4 + len;
                        total += len + 4;
                        nal = next;
                    }
                    part.AddVideo(total, _vdts, _vpts, _vkey);
                }

                void FinishAudio()
                {
                    if (!_aopen) return;
                    _aopen = false;
                    int end = _alen;
                    _alen = 0;
                    if (end == 0) return;
                    // one or more ADTS frames; each becomes one sample without its header
                    byte[] b = _abuf;
                    int i = 0, n = 0;
                    while (i + 7 <= end)
                    {
                        if (b[i] != 0xFF || (b[i + 1] & 0xF0) != 0xF0) throw Bad("audio is not ADTS AAC");
                        int frameLen = ((b[i + 3] & 0x03) << 11) | (b[i + 4] << 3) | (b[i + 5] >> 5);
                        int header = (b[i + 1] & 1) != 0 ? 7 : 9;
                        if ((b[i + 6] & 0x03) != 0) throw Bad("ADTS frame with several data blocks");
                        if (frameLen <= header || i + frameLen > end) throw Bad("broken ADTS frame");
                        int size = frameLen - header;
                        if (_batchLen + size > _batch.Length) Array.Resize(ref _batch, Math.Max(_batch.Length * 2, _batchLen + size));
                        Buffer.BlockCopy(b, i + header, _batch, _batchLen, size);
                        _batchLen += size; _batchSamples++;
                        _part.AddAudio(size, _apts + n * 1920L);
                        i += frameLen; n++;
                    }
                    if (n == 0 || i != end) throw Bad("audio packet is not whole ADTS frames");
                    if (_batchSamples >= AudioBatch) FlushAudioBatch();
                }

                void FlushAudioBatch()
                {
                    if (_batchSamples == 0) return;
                    _part.Need(_batchLen);
                    Chunk(1, _batchSamples);
                    Buffer.BlockCopy(_batch, 0, _part.Data, _part.Len, _batchLen);
                    _part.Len += _batchLen;
                    _batchLen = 0; _batchSamples = 0;
                }
            }

            // ---------------------------------------------------------------- sample tables of one track

            /// <summary>
            /// Sample times as ffmpeg derives them: every timestamp is converted to the track's timescale on its
            /// own (rounded to nearest), counted from <see cref="Zero"/>; durations are the differences.
            /// </summary>
            sealed class Track
            {
                public readonly long Scale, Nominal;        // timescale; length of one frame in it
                public long Zero;                           // the 90 kHz time that counts as 0
                public int Count;
                public int[] Sizes = new int[1 << 12];
                public long StartDts, PrevDts, EndPts = long.MinValue, TotalSize;
                readonly long[] _headDts = new long[64], _headCts = new long[64];       // the first samples
                int _head;
                public readonly List<long> SttsRuns = new List<long>();     // (count << 32) | duration; the last sample is not in here
                public readonly List<long> CttsRuns = new List<long>();     // (count << 32) | offset
                public bool AnyCtts;
                public readonly List<int> Sync = new List<int>();
                public readonly List<long> ChunkOffset = new List<long>();
                public readonly List<int> ChunkSamples = new List<int>();

                public Track(long scale, long nominal) { Scale = scale; Nominal = nominal; }

                static void AddRun(List<long> runs, long value)
                {
                    int n = runs.Count;
                    if (n > 0 && (int)runs[n - 1] == (int)value) runs[n - 1] += 1L << 32;
                    else runs.Add((1L << 32) | (uint)(int)value);
                }

                long Conv(long ticks) { return RoundNear((ticks - Zero) * Scale, 90000); }

                public void Add(int size, long dts, int cts, bool key)
                {
                    long td = Conv(dts), tp = cts == 0 ? td : Conv(dts + cts);
                    if (_head < _headDts.Length) { _headDts[_head] = td; _headCts[_head] = tp - td; _head++; }
                    if (Count == 0) StartDts = td;
                    else
                    {
                        long d = td - PrevDts;
                        if (d <= 0 || d > int.MaxValue) throw Bad("timestamps do not increase");
                        AddRun(SttsRuns, d);
                    }
                    PrevDts = td;
                    if (tp - td > int.MaxValue) throw Bad("composition offset too large");
                    if (tp != td) AnyCtts = true;
                    AddRun(CttsRuns, tp - td);
                    if (tp + Nominal > EndPts) EndPts = tp + Nominal;
                    if (Count == Sizes.Length) Array.Resize(ref Sizes, Sizes.Length * 2);
                    Sizes[Count++] = size;
                    TotalSize += size;
                    if (key) Sync.Add(Count);
                }

                /// <summary>Bits per second over the whole track, as FFmpeg's compute_avg_bitrate() has it.</summary>
                public long AverageBitrate { get { return TotalSize * 8 * Scale / MediaDuration; } }

                /// <summary>
                /// Composition offset FFmpeg takes as the track's first: that of the first sample, or a smaller one
                /// among the samples decoded at or before time zero (<paramref name="shift"/> moves the track).
                /// </summary>
                public long StartCts(long shift)
                {
                    long cts = _headCts[0];
                    int i = 1;
                    for (; i < _head && _headDts[i] + shift <= 0; i++) if (_headCts[i] < cts) cts = _headCts[i];
                    if (i == _headDts.Length) throw Bad("too many frames before time zero");
                    return cts;
                }

                /// <summary>Sum of all sample durations; the last sample lasts one nominal frame.</summary>
                public long MediaDuration { get { return PrevDts - StartDts + Nominal; } }

                public List<long> Stts()
                {
                    List<long> runs = new List<long>(SttsRuns);
                    AddRun(runs, Nominal);
                    return runs;
                }
            }

            /// <summary>
            /// A track's edit list as FFmpeg's MP4 muxer computes it (mov_write_edts_tag): an empty edit when the
            /// track starts late, then one edit that plays the media from its first presented sample.
            /// </summary>
            sealed class Edits
            {
                public long Delay, Duration, MediaTime;
                public long TrackDuration;          // for tkhd: where the track ends on the movie's timeline

                public Edits(Track t, long shift, long movieScale)
                {
                    long startDts = t.StartDts + shift, startCts = t.StartCts(shift), first = startDts + startCts;
                    Delay = first * movieScale / t.Scale;                       // towards zero
                    Duration = RoundUp((t.EndPts + shift - first) * movieScale, t.Scale);
                    if (Delay > 0) MediaTime = startCts;
                    else { MediaTime = -Math.Min(startDts, 0); Duration += Delay; }
                    TrackDuration = RoundUp((t.EndPts + shift) * movieScale, t.Scale);
                }

                public byte[] Box()
                {
                    if (Delay >= int.MaxValue || Duration >= int.MaxValue || Duration < 0 || MediaTime >= int.MaxValue)
                        throw Bad("the clip is too long for the direct join");
                    int n = Delay > 0 ? 2 : 1;
                    byte[] d = new byte[8 + n * 12];
                    P32(d, 4, (uint)n);
                    int at = 8;
                    if (Delay > 0) { P32(d, at, (uint)Delay); P32(d, at + 4, 0xFFFFFFFF); P32(d, at + 8, 0x00010000); at += 12; }
                    P32(d, at, (uint)Duration); P32(d, at + 4, (uint)MediaTime); P32(d, at + 8, 0x00010000);
                    return d;
                }
            }

            // ---------------------------------------------------------------- the run

            readonly object _gate = new object();
            Part[] _done;
            int _next, _written;
            long _claimedBytes, _writtenBytes;
            const long MaxAhead = 96L << 20;     // converted footage waiting for the writer (memory bound)
            volatile bool _abort;
            readonly Stack<byte[]> _pool = new Stack<byte[]>();

            byte[] Rent(int size)
            {
                lock (_pool)
                {
                    if (_pool.Count > 0 && _pool.Peek().Length >= size) return _pool.Pop();
                }
                return new byte[size + (size >> 3)];
            }

            void Work()
            {
                Converter conv = new Converter(this);
                for (;;)
                {
                    int i;
                    lock (_gate)
                    {
                        // do not run far ahead of the writer: converted segments wait in memory
                        while (!_abort && _next < _segs.Count && _next > _written && _claimedBytes - _writtenBytes >= MaxAhead) Monitor.Wait(_gate);
                        if (_abort || _next >= _segs.Count) return;
                        i = _next++;
                        _claimedBytes += _segs[i].Bytes;
                    }
                    Part part;
                    try { part = conv.Convert(_segs[i], Rent); }
                    catch (Exception ex) { part = new Part(); part.Error = ex; }
                    lock (_gate) { _done[i] = part; Monitor.PulseAll(_gate); }
                }
            }

            // what ffmpeg wrote for the first segment
            byte[] _template, _ftyp;
            Box _moov, _vTrak, _aTrak;
            long _movieScale, _shiftV, _shiftA, _firstBitrateV, _firstBitrateA;
            volatile Exception _templateError;
            // the first segment as converted here (kept for the comparison)
            Part _first;
            readonly ManualResetEvent _firstReady = new ManualResetEvent(false);

            public void Run(string tmp)
            {
                // Next to the helper's own files, not in the folder the clip goes to: nothing new ever shows up there.
                string header = string.IsNullOrEmpty(_owner._dataDir) ? tmp + ".hdr" : Path.Combine(Path.Combine(_owner._dataDir, "engine"), "join-header.mp4");
                // The template is made while the footage is being written; it is needed at the end, but a
                // disagreement with ffmpeg should stop the work early.
                Thread template = new Thread(delegate()
                {
                    try
                    {
                        MakeTemplate(header);
                        _firstReady.WaitOne();
                        Part first = _first;
                        if (first != null) CheckFirstSegment(first);
                    }
                    catch (Exception ex) { _templateError = ex; }
                });
                template.IsBackground = true;
                template.Name = "EngineClient.JoinHeader";
                template.Start();

                // unpacking is the only real CPU work and splits cleanly by segment; four threads keep up with the
                // writer on any disk, and half the cores stay free for whatever is in the foreground
                int workers = Math.Max(1, Math.Min(Math.Min(4, Environment.ProcessorCount / 2), _segs.Count));
                _done = new Part[_segs.Count];
                List<Thread> threads = new List<Thread>();
                for (int k = 0; k < workers; k++)
                {
                    Thread t = new Thread(Work);
                    t.IsBackground = true;
                    t.Name = "EngineClient.Join";
                    t.Start();
                    threads.Add(t);
                }
                try
                {
                    // Room for ftyp + moov in front of the footage. The tables need 4 bytes per frame for the sizes,
                    // up to 8 more per video frame when frames are reordered, and 20 per chunk.
                    long nv = _ticks * _fps / 90000 + _segs.Count + 64, na = _ticks * 48000 / 1024 / 90000 + 2L * _segs.Count + 64;
                    long chunks = 2 * (na / AudioBatch + 2L * _segs.Count + 4);
                    long reserve = _owner.DebugIndexReserve > 0 ? _owner.DebugIndexReserve : (64 << 10) + nv * 12 + na * 4 + chunks * 20;
                    reserve = (reserve + 4095) / 4096 * 4096;

                    using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.SequentialScan))
                    {
                        fs.SetLength(reserve + 16);
                        fs.Position = reserve + 16;
                        long pos = reserve + 16, fed = 0;
                        DateTime lastHold = DateTime.UtcNow;
                        for (int i = 0; i < _segs.Count; i++)
                        {
                            Part part;
                            lock (_gate)
                            {
                                while (_done[i] == null) Monitor.Wait(_gate);
                                part = _done[i];
                                _done[i] = null;
                            }
                            if (part.Error != null) throw part.Error;
                            if (_templateError != null) throw _templateError;      // no point in writing on
                            if (_owner._disposed) throw new ObjectDisposedException("EngineClient");
                            if (i == 0)
                            {
                                if (part.VCount == 0 || part.ACount == 0) throw Bad("no video or no audio at the start of the footage");
                                // ffmpeg counts from the first video frame it shows (see CheckFirstSegment)
                                _v.Zero = _a.Zero = part.VDts[0] + part.VCts[0];
                            }
                            fs.Write(part.Data, 0, part.Len);
                            Merge(part, _v, _a, pos);
                            pos += part.Len;
                            if (i == 0) { _first = part; _firstReady.Set(); }
                            else lock (_pool) _pool.Push(part.Data);
                            lock (_gate) { _written = i + 1; _writtenBytes += _segs[i].Bytes; Monitor.PulseAll(_gate); }
                            fed += _segs[i].Bytes;
                            _report(_totalBytes > 0 ? (double)fed / _totalBytes : 1);
                            if ((DateTime.UtcNow - lastHold).TotalSeconds > 60)
                            {
                                lastHold = DateTime.UtcNow;
                                Dictionary<string, object> h = Msg("hold");
                                h["saveId"] = _saveId;
                                try { _owner.Request(_owner._link, h, 5000); } catch (EngineException) { }
                            }
                        }

                        // mdat header, 64-bit size
                        byte[] mh = new byte[16];
                        P32(mh, 0, 1); mh[4] = (byte)'m'; mh[5] = (byte)'d'; mh[6] = (byte)'a'; mh[7] = (byte)'t';
                        P64(mh, 8, (ulong)(pos - reserve));
                        fs.Position = reserve;
                        fs.Write(mh, 0, 16);

                        if (!template.Join(30000)) throw new TimeoutException("ffmpeg did not deliver the MP4 header in time");
                        if (_templateError != null) throw _templateError;
                        if (_dropHevcPs && ParameterSetsVary(_vTrak.Find("mdia").Find("minf").Find("stbl").Find("stsd").Data)) throw new ParameterSetsChanged();
                        MemoryStream moov = BuildMoov();
                        long head = _ftyp.Length + moov.Length;
                        fs.Position = 0;
                        fs.Write(_ftyp, 0, _ftyp.Length);
                        byte[] fh = new byte[8];
                        fh[4] = (byte)'f'; fh[5] = (byte)'r'; fh[6] = (byte)'e'; fh[7] = (byte)'e';
                        if (head + 8 <= reserve)
                        {
                            // index first, the rest of the reserve is padding
                            fs.Write(moov.GetBuffer(), 0, (int)moov.Length);
                            P32(fh, 0, (uint)(reserve - head));
                            fs.Write(fh, 0, 8);
                        }
                        else
                        {
                            // more tables than there was room for (does not happen with ordinary footage):
                            // still a correct MP4, with the index after the footage
                            P32(fh, 0, (uint)(reserve - _ftyp.Length));
                            fs.Write(fh, 0, 8);
                            fs.Position = pos;
                            fs.Write(moov.GetBuffer(), 0, (int)moov.Length);
                        }
                        fs.Flush();
                    }
                }
                finally
                {
                    _abort = true;
                    lock (_gate) Monitor.PulseAll(_gate);
                    _firstReady.Set();
                    foreach (Thread t in threads) t.Join(10000);
                    try { Process f = _owner._ffmpegProc; if (template.IsAlive && f != null && !f.HasExited) f.Kill(); } catch { }
                    template.Join(5000);
                    try { if (File.Exists(header)) File.Delete(header); } catch { }
                }
            }

            /// <summary>Appends one converted segment's samples and chunks to the tables.</summary>
            static void Merge(Part p, Track v, Track a, long fileOffset)
            {
                for (int i = 0; i < p.VCount; i++) v.Add(p.VSize[i], p.VDts[i], p.VCts[i], p.VKey[i]);
                for (int i = 0; i < p.ACount; i++) a.Add(p.ASize[i], p.ADts[i], 0, false);
                List<int> c = p.Chunks;
                for (int i = 0; i + 2 < c.Count; i += 3)
                {
                    Track t = c[i] == 0 ? v : a;
                    t.ChunkOffset.Add(fileOffset + c[i + 2]);
                    t.ChunkSamples.Add(c[i + 1]);
                }
            }

            /// <summary>
            /// ffmpeg makes a small MP4 from the first segment alone, with the same shifts and the same options
            /// as for a whole clip.
            /// </summary>
            void MakeTemplate(string path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                ProcessStartInfo si = new ProcessStartInfo(_owner._ffmpeg);
                si.Arguments = "-hide_banner -loglevel error -nostdin -y -f mpegts -i pipe:0 -map 0:v:0 -map 0:a:0 -c copy"
                               + (_codec == "hevc" ? " -tag:v hvc1" : "") + " -movflags +faststart -f mp4 " + Quote(path);
                si.UseShellExecute = false; si.CreateNoWindow = true;
                si.RedirectStandardInput = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
                StringBuilder err = new StringBuilder();
                using (Process ff = Process.Start(si))
                {
                    _owner._ffmpegProc = ff;
                    try
                    {
                        ff.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (err) { if (err.Length < 2000) err.AppendLine(e.Data); } };
                        ff.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { };
                        ff.BeginErrorReadLine();
                        ff.BeginOutputReadLine();
                        Stream stdin = ff.StandardInput.BaseStream;
                        string pipeError, problem;
                        try { problem = new TsJoin().Feed(_segs[0], stdin, out pipeError); }
                        finally { try { stdin.Close(); } catch { } }
                        if (!ff.WaitForExit(20000)) { try { ff.Kill(); } catch { } throw new TimeoutException("ffmpeg did not finish the MP4 header"); }
                        ff.WaitForExit();
                        string said;
                        lock (err) said = err.ToString().Trim();
                        if (problem != null) throw new IOException(problem);
                        if (ff.ExitCode != 0 || pipeError != null) throw Bad("ffmpeg could not make the MP4 header: " + (pipeError ?? "") + " " + said);
                    }
                    finally { _owner._ffmpegProc = null; }
                }
                if (new FileInfo(path).Length > 1 << 30) throw Bad("MP4 header unexpectedly large");
                _template = File.ReadAllBytes(path);
                try { File.Delete(path); } catch { }
                byte[] b = _template;
                long pos = 0;
                while (pos + 8 <= b.Length)
                {
                    long size = R32(b, (int)pos);
                    string type = Encoding.ASCII.GetString(b, (int)pos + 4, 4);
                    int head = 8;
                    if (size == 1 && pos + 16 <= b.Length) { size = R64(b, (int)pos + 8); head = 16; }
                    else if (size == 0) size = b.Length - pos;
                    if (size < head || pos + size > b.Length) throw Bad("bad MP4 box " + type);
                    if (type == "ftyp") { _ftyp = new byte[size]; Buffer.BlockCopy(b, (int)pos, _ftyp, 0, (int)size); }
                    else if (type == "moov") { _moov = new Box(); _moov.Type = "moov"; _moov.Kids = Parse(b, (int)pos + head, (int)(pos + size)); }
                    pos += size;
                }
                if (_ftyp == null || _moov == null) throw Bad("ffmpeg wrote no MP4 header");
            }

            // ---------------------------------------------------------------- ffmpeg as the referee

            /// <summary>One track of the MP4 ffmpeg made from the first segment.</summary>
            sealed class Reference
            {
                public long Scale, MediaDuration, TrackDuration;
                public int[] Sizes;
                public long[] Offsets;
                public List<long> Stts, Ctts;
                public List<int> Sync;
                public byte[] Elst;
            }

            static List<long> ReadRuns(byte[] d)
            {
                int n = (int)R32(d, 4);
                if (n < 0 || d.Length < 8 + 8L * n) throw Bad("bad table in the MP4 header");
                List<long> runs = new List<long>(n);
                for (int i = 0; i < n; i++) runs.Add(((long)R32(d, 8 + 8 * i) << 32) | R32(d, 12 + 8 * i));
                return runs;
            }

            static bool Same(List<long> x, List<long> y)
            {
                if (x.Count != y.Count) return false;
                for (int i = 0; i < x.Count; i++) if (x[i] != y[i]) return false;
                return true;
            }

            static bool Same(byte[] x, byte[] y)
            {
                if (x.Length != y.Length) return false;
                for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
                return true;
            }

            static Reference ReadReference(Box trak)
            {
                Reference r = new Reference();
                Box tkhd = trak.Find("tkhd"), mdia = trak.Find("mdia"), mdhd = mdia.Find("mdhd");
                if (tkhd.Data[0] != 0 || mdhd.Data[0] != 0) throw Bad("unexpected version of the MP4 track header");
                r.TrackDuration = R32(tkhd.Data, 20);
                r.Scale = R32(mdhd.Data, 12);
                r.MediaDuration = R32(mdhd.Data, 16);
                Box edts = trak.Maybe("edts");
                Box elst = edts != null ? edts.Maybe("elst") : null;
                if (elst == null) throw Bad("MP4 header without an edit list");
                r.Elst = elst.Data;
                Box stbl = mdia.Find("minf").Find("stbl");
                foreach (Box k in stbl.Kids)
                    if (k.Type != "stsd" && k.Type != "stts" && k.Type != "stss" && k.Type != "ctts" && k.Type != "stsc" && k.Type != "stsz"
                        && k.Type != "stco" && k.Type != "co64" && k.Type != "sgpd" && k.Type != "sbgp")
                        throw Bad("unexpected table " + k.Type + " in the MP4 header");
                byte[] d = stbl.Find("stsz").Data;
                long fixedSize = R32(d, 4);
                int n = (int)R32(d, 8);
                if (n < 0 || (fixedSize == 0 && d.Length < 12 + 4L * n)) throw Bad("bad sample size table");
                r.Sizes = new int[n];
                for (int i = 0; i < n; i++) r.Sizes[i] = (int)(fixedSize != 0 ? fixedSize : R32(d, 12 + 4 * i));
                r.Stts = ReadRuns(stbl.Find("stts").Data);
                Box ctts = stbl.Maybe("ctts");
                if (ctts != null)
                {
                    if (ctts.Data[0] != 0) throw Bad("unexpected version of the composition offsets");
                    r.Ctts = ReadRuns(ctts.Data);
                }
                Box stss = stbl.Maybe("stss");
                if (stss != null)
                {
                    int m = (int)R32(stss.Data, 4);
                    if (m < 0 || stss.Data.Length < 8 + 4L * m) throw Bad("bad sync sample table");
                    r.Sync = new List<int>(m);
                    for (int i = 0; i < m; i++) r.Sync.Add((int)R32(stss.Data, 8 + 4 * i));
                }
                // where each sample is
                d = stbl.Find("stsc").Data;
                int runs = (int)R32(d, 4);
                Box co = stbl.Maybe("stco");
                bool big = co == null;
                if (big) co = stbl.Find("co64");
                int chunkCount = (int)R32(co.Data, 4);
                if (runs < 1 || d.Length < 8 + 12L * runs || chunkCount < 0 || co.Data.Length < 8 + (big ? 8L : 4L) * chunkCount) throw Bad("bad chunk tables");
                r.Offsets = new long[n];
                int sample = 0, run = 0;
                for (int ch = 0; ch < chunkCount; ch++)
                {
                    while (run + 1 < runs && R32(d, 8 + 12 * (run + 1)) <= ch + 1) run++;
                    long per = R32(d, 12 + 12 * run);
                    long off = big ? R64(co.Data, 8 + 8 * ch) : R32(co.Data, 8 + 4 * ch);
                    for (long k = 0; k < per && sample < n; k++) { r.Offsets[sample] = off; off += r.Sizes[sample]; sample++; }
                }
                if (sample != n) throw Bad("the MP4 header places " + sample + " of " + n + " samples");
                return r;
            }

            /// <summary>The first segment's samples of one track: ffmpeg's and this code's must be the same.</summary>
            void CompareSamples(string what, Reference r, Track mine, Part p, int track)
            {
                if (r.Scale != mine.Scale) throw Bad(what + ": ffmpeg uses timescale " + r.Scale);
                if (r.Sizes.Length != mine.Count) throw Bad(what + ": ffmpeg found " + r.Sizes.Length + " frames in the first segment, the direct join " + mine.Count);
                if (!Same(r.Stts, mine.Stts())) throw Bad(what + ": frame durations differ from ffmpeg's");
                if (mine.AnyCtts != (r.Ctts != null) || (r.Ctts != null && !Same(r.Ctts, mine.CttsRuns))) throw Bad(what + ": frame order differs from ffmpeg's");
                if (track == 0)
                {
                    bool same = r.Sync == null ? mine.Sync.Count == mine.Count : r.Sync.Count == mine.Sync.Count;
                    for (int i = 0; same && r.Sync != null && i < r.Sync.Count; i++) same = r.Sync[i] == mine.Sync[i];
                    if (!same) throw Bad(what + ": keyframes differ from ffmpeg's");
                }
                byte[] tpl = _template, own = p.Data;
                int s = 0;
                List<int> c = p.Chunks;
                for (int i = 0; i + 2 < c.Count; i += 3)
                {
                    if (c[i] != track) continue;
                    int off = c[i + 2];
                    for (int k = 0; k < c[i + 1]; k++, s++)
                    {
                        if (s >= mine.Count) throw Bad(what + ": sample count");
                        int size = mine.Sizes[s];
                        long at = r.Offsets[s];
                        if (r.Sizes[s] != size) throw Bad(what + ": frame " + (s + 1) + " is " + size + " bytes, ffmpeg's " + r.Sizes[s]);
                        if (at < 0 || at + size > tpl.Length || off + size > p.Len) throw Bad(what + ": frame " + (s + 1) + " is outside the file");
                        for (int j = 0; j < size; j++)
                            if (tpl[at + j] != own[off + j]) throw Bad(what + ": frame " + (s + 1) + " differs from ffmpeg's at byte " + j);
                        off += size;
                    }
                }
                if (s != mine.Count) throw Bad(what + ": sample count");
            }

            /// <summary>
            /// Compares the first segment as converted here with the MP4 ffmpeg made from it, and finds out where
            /// ffmpeg put time zero. Throws when anything differs: this code and ffmpeg then do not understand
            /// the footage the same way, and the direct file must not be used.
            /// </summary>
            void CheckFirstSegment(Part p)
            {
                Box mvhd = _moov.Find("mvhd");
                if (mvhd.Data[0] != 0) throw Bad("unexpected version of the MP4 movie header");
                _movieScale = R32(mvhd.Data, 12);
                long movieDuration = R32(mvhd.Data, 16);
                foreach (Box k in _moov.Kids)
                {
                    if (k.Type != "trak") continue;
                    string kind = Encoding.ASCII.GetString(k.Find("mdia").Find("hdlr").Data, 8, 4);
                    if (kind == "vide" && _vTrak == null) _vTrak = k;
                    else if (kind == "soun" && _aTrak == null) _aTrak = k;
                    else throw Bad("unexpected track " + kind);
                }
                if (_vTrak == null || _aTrak == null) throw Bad("expected one video and one audio track");
                byte[] sd = _vTrak.Find("mdia").Find("minf").Find("stbl").Find("stsd").Data;
                string entry = sd.Length >= 16 ? Encoding.ASCII.GetString(sd, 12, 4) : "";
                if (entry != (_codec == "hevc" ? "hvc1" : "avc1")) throw Bad("unexpected sample entry " + entry);

                Track v = new Track(_v.Scale, _v.Nominal), a = new Track(_a.Scale, _a.Nominal);
                v.Zero = a.Zero = p.VDts[0] + p.VCts[0];
                Merge(p, v, a, 0);
                Reference rv = ReadReference(_vTrak), ra = ReadReference(_aTrak);
                CompareSamples("video", rv, v, p, 0);
                CompareSamples("audio", ra, a, p, 1);
                Box sbgp = _aTrak.Find("mdia").Find("minf").Find("stbl").Maybe("sbgp");
                if (sbgp != null)
                {
                    int at = sbgp.Data[0] == 1 ? 12 : 8;
                    if (sbgp.Data.Length != at + 12 || R32(sbgp.Data, at) != 1 || R32(sbgp.Data, at + 4) != a.Count || R32(sbgp.Data, at + 8) != 1)
                        throw Bad("unexpected sample groups in the MP4 header");
                }

                // Time zero. ffmpeg counts from the first video frame it shows; when audio that begins before
                // that frame is the first thing its muxer sees (the start of a recording, with reordered video),
                // it moves everything so that the audio begins at 0 instead.
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    long shiftA = 0, shiftV = 0;
                    if (attempt == 1)
                    {
                        if (a.StartDts >= 0) break;
                        shiftA = -a.StartDts;
                        shiftV = RoundUp(shiftA * v.Scale, a.Scale);
                    }
                    Edits ev = new Edits(v, shiftV, _movieScale), ea = new Edits(a, shiftA, _movieScale);
                    if (Same(ev.Box(), rv.Elst) && Same(ea.Box(), ra.Elst) && ev.TrackDuration == rv.TrackDuration && ea.TrackDuration == ra.TrackDuration
                        && v.MediaDuration == rv.MediaDuration && a.MediaDuration == ra.MediaDuration
                        && Math.Max(ev.TrackDuration, ea.TrackDuration) == movieDuration)
                    {
                        _shiftV = shiftV; _shiftA = shiftA;
                        _firstBitrateV = v.AverageBitrate; _firstBitrateA = a.AverageBitrate;
                        return;
                    }
                }
                throw Bad("ffmpeg's edit lists and durations for the first segment are not the expected ones");
            }

            // ---------------------------------------------------------------- MP4 boxes

            sealed class Box
            {
                public string Type;
                public byte[] Data;             // payload of a leaf
                public List<Box> Kids;          // children of a container

                public Box Find(string type)
                {
                    if (Kids != null) foreach (Box k in Kids) if (k.Type == type) return k;
                    throw Bad("MP4 header without " + type);
                }

                public Box Maybe(string type)
                {
                    if (Kids != null) foreach (Box k in Kids) if (k.Type == type) return k;
                    return null;
                }

                public long Size()
                {
                    long n = 8;
                    if (Kids != null) foreach (Box k in Kids) n += k.Size(); else n += Data.Length;
                    return n;
                }

                public void Write(Stream s)
                {
                    long size = Size();
                    if (size > uint.MaxValue) throw Bad("MP4 index too large");
                    byte[] h = new byte[8];
                    P32(h, 0, (uint)size);
                    for (int i = 0; i < 4; i++) h[4 + i] = (byte)Type[i];
                    s.Write(h, 0, 8);
                    if (Kids != null) foreach (Box k in Kids) k.Write(s); else s.Write(Data, 0, Data.Length);
                }
            }

            static uint R32(byte[] b, int i) { return ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3]; }
            static void P32(byte[] b, int i, uint v) { b[i] = (byte)(v >> 24); b[i + 1] = (byte)(v >> 16); b[i + 2] = (byte)(v >> 8); b[i + 3] = (byte)v; }
            static void P64(byte[] b, int i, ulong v) { P32(b, i, (uint)(v >> 32)); P32(b, i + 4, (uint)v); }
            static long R64(byte[] b, int i) { return (long)(((ulong)R32(b, i) << 32) | R32(b, i + 4)); }

            static bool IsContainer(string type)
            {
                return type == "moov" || type == "trak" || type == "mdia" || type == "minf" || type == "stbl" || type == "edts";
            }

            static List<Box> Parse(byte[] b, int from, int to)
            {
                List<Box> list = new List<Box>();
                int p = from;
                while (p + 8 <= to)
                {
                    long size = R32(b, p);
                    string type = Encoding.ASCII.GetString(b, p + 4, 4);
                    int header = 8;
                    if (size == 1 && p + 16 <= to) { size = R64(b, p + 8); header = 16; }
                    else if (size == 0) size = to - p;
                    if (size < header || p + size > to) throw Bad("bad MP4 box " + type);
                    Box x = new Box();
                    x.Type = type;
                    if (IsContainer(type)) x.Kids = Parse(b, p + header, (int)(p + size));
                    else { x.Data = new byte[size - header]; Buffer.BlockCopy(b, p + header, x.Data, 0, x.Data.Length); }
                    list.Add(x);
                    p += (int)size;
                }
                return list;
            }

            static Box Leaf(string type, byte[] data) { Box x = new Box(); x.Type = type; x.Data = data; return x; }

            static byte[] Runs(List<long> runs)
            {
                byte[] d = new byte[8 + runs.Count * 8];
                P32(d, 4, (uint)runs.Count);
                for (int i = 0; i < runs.Count; i++) { P32(d, 8 + i * 8, (uint)(runs[i] >> 32)); P32(d, 12 + i * 8, (uint)(int)runs[i]); }
                return d;
            }

            /// <summary>
            /// Duration field of a version-0 mvhd / tkhd / mdhd. FFmpeg switches to 64-bit fields from 2^31 on
            /// (6.6 hours at 90 kHz); such a clip is left to ffmpeg.
            /// </summary>
            static void SetDuration(Box x, int offset, long value)
            {
                if (x.Data[0] != 0 || value < 0 || value >= int.MaxValue) throw Bad("the clip is too long for the direct join");
                P32(x.Data, offset, (uint)value);
            }

            MemoryStream BuildMoov()
            {
                Edits ev = new Edits(_v, _shiftV, _movieScale), ea = new Edits(_a, _shiftA, _movieScale);
                FillTrack(_vTrak, _v, ev, true);
                FillTrack(_aTrak, _a, ea, false);
                SetBitrate(_vTrak.Find("mdia").Find("minf").Find("stbl").Find("stsd").Data, true, _firstBitrateV, _v.AverageBitrate);
                SetBitrate(_aTrak.Find("mdia").Find("minf").Find("stbl").Find("stsd").Data, false, _firstBitrateA, _a.AverageBitrate);
                SetDuration(_moov.Find("mvhd"), 16, Math.Max(ev.TrackDuration, ea.TrackDuration));
                MemoryStream ms = new MemoryStream((int)_moov.Size());
                _moov.Write(ms);
                return ms;
            }

            /// <summary>
            /// ffmpeg notes each track's average bitrate, and a maximum that is never below it, in the sample entry
            /// ("btrt", and "esds" for AAC). Its header has the first segment's values; this puts in the whole
            /// clip's, computed the way FFmpeg does. Information for players only: where the entry does not look
            /// as expected it is left alone.
            /// </summary>
            static void SetBitrate(byte[] stsd, bool video, long firstSegment, long wholeClip)
            {
                if (stsd.Length < 16 || wholeClip <= 0 || wholeClip > int.MaxValue) return;
                int p = 8 + (video ? 86 : 36), end = 8 + (int)R32(stsd, 8);
                if (end > stsd.Length || (!video && (stsd.Length < 26 || stsd[24] != 0 || stsd[25] != 0))) return;    // sound entry version 0 only
                while (p + 8 <= end)
                {
                    long size = R32(stsd, p);
                    if (size < 8 || p + size > end) return;
                    string type = Encoding.ASCII.GetString(stsd, p + 4, 4);
                    int at = -1;                                    // where "maximum, average" are
                    if (type == "btrt" && size == 20) at = p + 12;
                    else if (type == "esds") at = EsdsBitrates(stsd, p + 8, p + (int)size);
                    if (at >= 0 && R32(stsd, at + 4) == firstSegment)
                    {
                        long max = R32(stsd, at);
                        P32(stsd, at, (uint)(max == firstSegment ? wholeClip : Math.Max(max, wholeClip)));
                        P32(stsd, at + 4, (uint)wholeClip);
                    }
                    p += (int)size;
                }
            }

            /// <summary>Position of maxBitrate, avgBitrate in an esds box as ffmpeg writes it, or -1.</summary>
            static int EsdsBitrates(byte[] b, int p, int end)
            {
                p += 4;                                             // version, flags
                if (p >= end || b[p++] != 0x03) return -1;          // ES_Descriptor
                p = SkipLength(b, p, end);
                if (p < 0 || p + 3 > end) return -1;
                p += 2;                                             // ES_ID
                if ((b[p++] & 0xE0) != 0) return -1;                // optional fields follow: not what ffmpeg writes
                if (p >= end || b[p++] != 0x04) return -1;          // DecoderConfigDescriptor
                p = SkipLength(b, p, end);
                if (p < 0) return -1;
                p += 5;                                             // object type, stream type, buffer size
                return p + 8 <= end ? p : -1;
            }

            static int SkipLength(byte[] b, int p, int end)
            {
                for (int i = 0; i < 4 && p < end; i++) if ((b[p++] & 0x80) == 0) return p;
                return -1;
            }

            /// <summary>Puts the sample tables, edit list and durations of one track into ffmpeg's trak.</summary>
            static void FillTrack(Box trak, Track t, Edits e, bool video)
            {
                Box mdia = trak.Find("mdia");
                Box stbl = mdia.Find("minf").Find("stbl");
                List<Box> kids = new List<Box>();
                kids.Add(stbl.Find("stsd"));
                kids.Add(Leaf("stts", Runs(t.Stts())));
                if (video && t.Sync.Count != t.Count)
                {
                    byte[] ss = new byte[8 + t.Sync.Count * 4];
                    P32(ss, 4, (uint)t.Sync.Count);
                    for (int i = 0; i < t.Sync.Count; i++) P32(ss, 8 + i * 4, (uint)t.Sync[i]);
                    kids.Add(Leaf("stss", ss));
                }
                if (t.AnyCtts) kids.Add(Leaf("ctts", Runs(t.CttsRuns)));
                // stsc: one entry per change in samples-per-chunk
                List<int> firstChunk = new List<int>(), perChunk = new List<int>();
                for (int i = 0; i < t.ChunkSamples.Count; i++)
                    if (i == 0 || t.ChunkSamples[i] != t.ChunkSamples[i - 1]) { firstChunk.Add(i + 1); perChunk.Add(t.ChunkSamples[i]); }
                byte[] sc = new byte[8 + firstChunk.Count * 12];
                P32(sc, 4, (uint)firstChunk.Count);
                for (int i = 0; i < firstChunk.Count; i++) { P32(sc, 8 + i * 12, (uint)firstChunk[i]); P32(sc, 12 + i * 12, (uint)perChunk[i]); P32(sc, 16 + i * 12, 1); }
                kids.Add(Leaf("stsc", sc));
                byte[] sz = new byte[12 + (long)t.Count * 4];
                P32(sz, 8, (uint)t.Count);
                for (int i = 0; i < t.Count; i++) P32(sz, 12 + i * 4, (uint)t.Sizes[i]);
                kids.Add(Leaf("stsz", sz));
                bool big = t.ChunkOffset.Count > 0 && t.ChunkOffset[t.ChunkOffset.Count - 1] > uint.MaxValue;
                byte[] co = new byte[8 + t.ChunkOffset.Count * (big ? 8 : 4)];
                P32(co, 4, (uint)t.ChunkOffset.Count);
                for (int i = 0; i < t.ChunkOffset.Count; i++)
                {
                    if (big) P64(co, 8 + i * 8, (ulong)t.ChunkOffset[i]); else P32(co, 8 + i * 4, (uint)t.ChunkOffset[i]);
                }
                kids.Add(Leaf(big ? "co64" : "stco", co));
                // ffmpeg marks AAC with a "roll" sample group that covers every sample (checked in CheckFirstSegment)
                Box sgpd = stbl.Maybe("sgpd"), sbgp = stbl.Maybe("sbgp");
                if (sgpd != null && sbgp != null)
                {
                    int at = sbgp.Data[0] == 1 ? 12 : 8;
                    P32(sbgp.Data, at + 4, (uint)t.Count);
                    kids.Add(sgpd);
                    kids.Add(sbgp);
                }
                stbl.Kids = kids;

                SetDuration(mdia.Find("mdhd"), 16, t.MediaDuration);
                SetDuration(trak.Find("tkhd"), 20, e.TrackDuration);
                trak.Find("edts").Find("elst").Data = e.Box();
            }
        }

        sealed class JoinRun { public int ExitCode = -1; public string Err = "", InputError, PipeError; }

        /// <summary>One ffmpeg run: feeds it the joined stream on stdin and waits for it to finish the file.</summary>
        JoinRun RunJoin(List<SegRef> segs, long totalBytes, string codec, bool inBandPs, string indexArgs, string tmp, long saveId, Action<double> progress)
        {
            JoinRun run = new JoinRun();
            StringBuilder err = new StringBuilder();
            // The segments arrive as ONE continuous MPEG-TS stream on stdin (see TsJoin), so ffmpeg only
            // remuxes; -f mp4 because the temporary name has no .mp4 extension.
            ProcessStartInfo si = new ProcessStartInfo(_ffmpeg);
            // "hvc1" is the HEVC flavour every player takes, but it keeps parameter sets out of the samples;
            // footage whose parameter sets change needs ffmpeg's default "hev1", which leaves them in.
            si.Arguments = "-hide_banner -loglevel error -nostdin -y -f mpegts -i pipe:0 -map 0:v:0 -map 0:a:0 -c copy"
                           + (codec == "hevc" && !inBandPs ? " -tag:v hvc1" : "") + " " + indexArgs + " -f mp4 " + Quote(tmp);
            si.UseShellExecute = false; si.CreateNoWindow = true;
            si.RedirectStandardInput = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
            Process ff = Process.Start(si);
            _ffmpegProc = ff;
            try
            {
                ff.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null) lock (err) { if (err.Length < 4000) err.AppendLine(e.Data); } };
                ff.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { };
                ff.BeginErrorReadLine();
                ff.BeginOutputReadLine();

                long fed = 0;
                DateTime lastHold = DateTime.UtcNow;
                Stream stdin = ff.StandardInput.BaseStream;
                TsJoin join = new TsJoin();
                // a write to a stuck ffmpeg would block for ever: kill it when nothing moves for 3 minutes
                long lastMove = Stopwatch.GetTimestamp();
                Timer stall = new Timer(delegate
                {
                    if ((Stopwatch.GetTimestamp() - Interlocked.Read(ref lastMove)) / (double)Stopwatch.Frequency > 180)
                    {
                        try { if (!ff.HasExited) ff.Kill(); } catch { }
                    }
                }, null, 5000, 5000);
                try
                {
                    foreach (SegRef s in segs)
                    {
                        string problem = join.Feed(s, stdin, out run.PipeError);
                        if (problem != null) { run.InputError = problem; break; }
                        if (run.PipeError != null) break;
                        Interlocked.Exchange(ref lastMove, Stopwatch.GetTimestamp());
                        fed += s.Bytes;
                        if (progress != null) { try { progress(totalBytes > 0 ? (double)fed / totalBytes : 1); } catch { } }
                        if ((DateTime.UtcNow - lastHold).TotalSeconds > 60)
                        {
                            lastHold = DateTime.UtcNow;
                            Dictionary<string, object> h = Msg("hold");
                            h["saveId"] = saveId;
                            try { Request(_link, h, 5000); } catch (EngineException) { }
                        }
                    }
                }
                finally
                {
                    stall.Dispose();
                    try { stdin.Close(); } catch { }
                }
                if (run.InputError != null) { try { if (!ff.HasExited) ff.Kill(); } catch { } }
                int finishMs = (int)Math.Min(int.MaxValue, 120000 + totalBytes / 20000);
                if (!ff.WaitForExit(finishMs)) { try { ff.Kill(); } catch { } run.PipeError = "ffmpeg did not finish in time"; }
                ff.WaitForExit();
                run.ExitCode = ff.ExitCode;
                lock (err) run.Err = err.ToString().Trim();
                return run;
            }
            finally
            {
                _ffmpegProc = null;
                try { if (!ff.HasExited) { ff.Kill(); ff.WaitForExit(5000); } } catch { }
                try { ff.Dispose(); } catch { }
            }
        }

        /// <summary>
        /// Joins the segment files into one continuous MPEG-TS stream. Every segment restarts its
        /// timestamps, so each file's video and audio timestamps (and its clock reference) are shifted by the
        /// amounts the helper computed, and the per-stream packet counters are renumbered. Nothing else
        /// changes: the encoded frames are passed through byte for byte.
        /// </summary>
        sealed class TsJoin
        {
            const int Packet = 188;
            readonly byte[] _buf = new byte[Packet * 5578];      // about 1 MB
            readonly int[] _cc = new int[8192];

            public TsJoin() { for (int i = 0; i < _cc.Length; i++) _cc[i] = -1; }

            /// <summary>
            /// Copies one segment. Returns null, or what is wrong with the segment; <paramref name="pipeError"/>
            /// is set instead when the reader (ffmpeg) went away.
            /// </summary>
            public string Feed(SegRef s, Stream dst, out string pipeError)
            {
                pipeError = null;
                long left = s.Bytes / Packet * Packet;
                FileStream fs;
                // ReadWrite|Delete sharing: the newest file is still being written by the muxer
                try { fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan); }
                catch (Exception ex) { return ex.Message; }
                using (fs)
                {
                    while (left > 0)
                    {
                        int want = (int)Math.Min(_buf.Length, left);
                        int got = 0;
                        try
                        {
                            while (got < want)
                            {
                                int n = fs.Read(_buf, got, want - got);
                                if (n <= 0) return "segment is shorter than listed: " + s.Path;
                                got += n;
                            }
                        }
                        catch (IOException ex) { return ex.Message; }
                        for (int o = 0; o < got; o += Packet)
                            if (!Patch(_buf, o, s.V, s.A)) return "not an MPEG-TS file: " + s.Path;
                        try { dst.Write(_buf, 0, got); }
                        catch (IOException ex) { pipeError = ex.Message.Trim(); return null; }
                        left -= got;
                    }
                }
                return null;
            }

            bool Patch(byte[] b, int o, long v, long a)
            {
                if (b[o] != 0x47) return false;
                int pid = ((b[o + 1] & 0x1F) << 8) | b[o + 2];
                int afc = (b[o + 3] >> 4) & 3;
                bool payload = (afc & 1) != 0;
                if (pid != 0x1FFF)
                {
                    // continuity counter: counts packets that carry payload, per PID, across file boundaries
                    int c = _cc[pid];
                    if (c < 0) c = b[o + 3] & 0x0F; else if (payload) c = (c + 1) & 0x0F;
                    _cc[pid] = c;
                    b[o + 3] = (byte)((b[o + 3] & 0xF0) | c);
                }
                int p = o + 4;
                if ((afc & 2) != 0)
                {
                    int len = b[o + 4];
                    if (len >= 7 && (b[o + 5] & 0x10) != 0 && v != 0)
                    {
                        // program clock reference: 33-bit base in 90 kHz, runs with the video
                        long pcr = ((long)b[o + 6] << 25) | ((long)b[o + 7] << 17) | ((long)b[o + 8] << 9) | ((long)b[o + 9] << 1) | (long)(uint)(b[o + 10] >> 7);
                        pcr = (pcr + v) & 0x1FFFFFFFFL;
                        b[o + 6] = (byte)(pcr >> 25); b[o + 7] = (byte)(pcr >> 17); b[o + 8] = (byte)(pcr >> 9); b[o + 9] = (byte)(pcr >> 1);
                        b[o + 10] = (byte)((b[o + 10] & 0x7F) | ((int)(pcr & 1) << 7));
                    }
                    p += 1 + len;
                }
                if (!payload || (b[o + 1] & 0x40) == 0 || p + 14 > o + Packet) return true;
                if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) return true;
                int sid = b[p + 3];
                long shift = sid >= 0xE0 && sid <= 0xEF ? v : sid >= 0xC0 && sid <= 0xDF ? a : 0;
                if (shift == 0) return true;
                int flags = b[p + 7] >> 6;
                if ((flags & 2) != 0) Shift(b, p + 9, shift);
                if (flags == 3 && p + 19 <= o + Packet) Shift(b, p + 14, shift);
                return true;
            }

            // PTS/DTS: 33 bits in 5 bytes as 3|8|7|8|7, each group followed by a marker bit
            static void Shift(byte[] b, int i, long shift)
            {
                long t = ((long)(uint)((b[i] >> 1) & 0x07) << 30) | ((long)b[i + 1] << 22) | ((long)(uint)((b[i + 2] >> 1) & 0x7F) << 15)
                         | ((long)b[i + 3] << 7) | (long)(uint)((b[i + 4] >> 1) & 0x7F);
                t = (t + shift) & 0x1FFFFFFFFL;
                b[i] = (byte)((b[i] & 0xF1) | (int)((t >> 30) & 0x07) << 1);
                b[i + 1] = (byte)(t >> 22);
                b[i + 2] = (byte)((b[i + 2] & 0x01) | (int)((t >> 15) & 0x7F) << 1);
                b[i + 3] = (byte)(t >> 7);
                b[i + 4] = (byte)((b[i + 4] & 0x01) | (int)(t & 0x7F) << 1);
            }
        }

        /// <summary>Duration from the MP4's movie header, or 0 when the file has none (not a finished MP4).</summary>
        static double Mp4Duration(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] h = new byte[16];
                long pos = 0, len = fs.Length;
                while (pos + 8 <= len)
                {
                    fs.Position = pos;
                    if (fs.Read(h, 0, 16) < 8) break;
                    long size = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
                    string type = Encoding.ASCII.GetString(h, 4, 4);
                    int header = 8;
                    if (size == 1) { size = 0; for (int i = 8; i < 16; i++) size = (size << 8) | h[i]; header = 16; }
                    else if (size == 0) size = len - pos;
                    if (size < header) break;
                    if (type == "moov")
                    {
                        byte[] m = new byte[(int)Math.Min(size - header, 4096)];
                        fs.Position = pos + header;
                        int n = fs.Read(m, 0, m.Length);
                        for (int i = 0; i + 32 <= n; i++)
                        {
                            if (m[i + 4] != 'm' || m[i + 5] != 'v' || m[i + 6] != 'h' || m[i + 7] != 'd') continue;
                            int q = i + 8;
                            long timescale, duration;
                            if (m[q] == 1)
                            {
                                timescale = Be(m, q + 20, 4); duration = Be(m, q + 24, 8);
                            }
                            else
                            {
                                timescale = Be(m, q + 12, 4); duration = Be(m, q + 16, 4);
                            }
                            return timescale > 0 ? (double)duration / timescale : 0;
                        }
                        return 0;
                    }
                    pos += size;
                }
            }
            return 0;
        }

        static long Be(byte[] b, int i, int n)
        {
            long v = 0;
            for (int k = 0; k < n; k++) v = (v << 8) | b[i + k];
            return v;
        }

        // ------------------------------------------------------------------ small helpers

        static string Quote(string s) { return "\"" + s.TrimEnd('\\') + "\""; }

        static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : "";
        }

        static long Num(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static double Dbl(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static bool Flag(Dictionary<string, object> d, string key)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool && (bool)v;
        }

        static IEnumerable<Dictionary<string, object>> Items(Dictionary<string, object> d, string key)
        {
            object v;
            if (!d.TryGetValue(key, out v) || !(v is IEnumerable) || v is string) yield break;
            foreach (object o in (IEnumerable)v)
            {
                Dictionary<string, object> e = o as Dictionary<string, object>;
                if (e != null) yield return e;
            }
        }

        // ------------------------------------------------------------------ job object (helper dies with the app)

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
            public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        static extern bool CloseHandle(IntPtr handle);

        void AssignToJob(Process p)
        {
            try
            {
                if (_job == IntPtr.Zero)
                {
                    _job = CreateJobObject(IntPtr.Zero, null);
                    JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.Basic.LimitFlags = 0x2000;   // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                    if (!SetInformationJobObject(_job, 9, ref info, (uint)Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION))))
                        Log("job object: cannot set kill-on-close (" + Marshal.GetLastWin32Error() + ")");
                }
                if (!AssignProcessToJobObject(_job, p.Handle))
                    Log("job object: cannot assign the helper (" + Marshal.GetLastWin32Error() + "); it still watches this process itself");
            }
            catch (Exception ex) { Log("job object: " + ex.Message); }
        }
    }
}
