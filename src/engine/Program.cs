// ReplayClipperEngine - headless libobs recording helper for Replay Clipper.
// Copyright (C) 2026 the Replay Clipper authors
//
// This program is free software; you can redistribute it and/or modify it under the terms of the
// GNU General Public License as published by the Free Software Foundation; either version 2 of the
// License, or (at your option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without
// even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
// General Public License for more details.
//
// You should have received a copy of the GNU General Public License along with this program; if not,
// see <https://www.gnu.org/licenses/>.
// SPDX-License-Identifier: GPL-2.0-or-later

// Process entry, the named-pipe protocol (engine-contract.md) and the main loop.
//
//   ReplayClipperEngine.exe --pipe <name> --parent <pid> --parent-start <FILETIME>
//                           --config-dir <dir> --log-dir <dir> [--debug]
//
// Must sit next to obs.dll (<engine>\bin\64bit). Exit codes: 0 normal, 2 bad arguments,
// 3 OBS version mismatch, 4 engine init failed, 5 parent gone / pipe lost.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace ReplayClipperEngine
{
    static class Json
    {
        public static string Quote(string s)
        {
            StringBuilder sb = new StringBuilder((s ?? "").Length + 8);
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7E) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        static JavaScriptSerializer New()
        {
            JavaScriptSerializer js = new JavaScriptSerializer();
            js.MaxJsonLength = int.MaxValue;
            return js;
        }

        /// <summary>One line of JSON, ASCII only (non-ASCII as \uXXXX).</summary>
        public static string Write(object o)
        {
            string s = New().Serialize(o);
            StringBuilder sb = null;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c > 0x7E || c < 0x20)
                {
                    if (sb == null) sb = new StringBuilder(s, 0, i, s.Length + 16);
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                }
                else if (sb != null) sb.Append(c);
            }
            return sb == null ? s : sb.ToString();
        }

        public static Dictionary<string, object> Read(string line)
        {
            return New().Deserialize<Dictionary<string, object>>(line);
        }

        public static string Str(Dictionary<string, object> d, string key, string def)
        {
            object v;
            return d.TryGetValue(key, out v) && v is string ? (string)v : def;
        }

        public static long Num(Dictionary<string, object> d, string key, long def)
        {
            object v;
            if (!d.TryGetValue(key, out v) || v == null) return def;
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); } catch { return def; }
        }

        public static bool Flag(Dictionary<string, object> d, string key, bool def)
        {
            object v;
            return d.TryGetValue(key, out v) && v is bool ? (bool)v : def;
        }
    }

    static class Program
    {
        const int Protocol = 1;
        static NamedPipeClientStream _pipe;
        static readonly object WriteGate = new object();
        static readonly Queue<string> Requests = new Queue<string>();
        static volatile bool _quit, _parentGone, _pipeLost;
        static bool _debug;
        static long _tickMsMax;

        [STAThread]
        static int Main(string[] args)
        {
            Dictionary<string, string> opt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--")) continue;
                string key = args[i].Substring(2);
                opt[key] = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "1";
            }
            string pipeName, configDir, logDir, parentText, parentStartText;
            int parentPid;
            long parentStart;
            if (!opt.TryGetValue("pipe", out pipeName) || !opt.TryGetValue("config-dir", out configDir) || !opt.TryGetValue("log-dir", out logDir)
                || !opt.TryGetValue("parent", out parentText) || !int.TryParse(parentText, out parentPid)
                || !opt.TryGetValue("parent-start", out parentStartText) || !long.TryParse(parentStartText, out parentStart))
                return 2;
            _debug = opt.ContainsKey("debug");

            // monitor rectangles and the cursor position are virtualised for DPI-unaware processes
            Win.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Win.SetErrorMode(0x0001 | 0x0002 | 0x8000);   // no critical-error, GP-fault or open-file dialogs
            try { Log.Open(logDir); } catch { return 2; }
            Log.Info("start pid={0} parent={1} pipe={2}{3}", Process.GetCurrentProcess().Id, parentPid, pipeName, _debug ? " (debug)" : "");

            // The parent is identified by pid AND start time, so a recycled pid is never mistaken for it.
            IntPtr parent = Win.OpenProcess(Win.SYNCHRONIZE | Win.PROCESS_QUERY_LIMITED_INFORMATION, false, parentPid);
            long created, t1, t2, t3;
            if (parent == IntPtr.Zero || !Win.GetProcessTimes(parent, out created, out t1, out t2, out t3) || Math.Abs(created - parentStart) > 20000000)
            {
                Log.Info("parent process not found");
                return 5;
            }
            Thread watch = new Thread(delegate() { Win.WaitForSingleObject(parent, 0xFFFFFFFF); _parentGone = true; });
            watch.IsBackground = true;
            watch.Start();

            try
            {
                _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                _pipe.Connect(10000);
            }
            catch (Exception ex)
            {
                Log.Info("cannot connect to pipe: {0}", ex.Message);
                return 5;
            }
            Thread reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Start();

            Stopwatch up = Stopwatch.StartNew();
            string err = null;
            try { err = Engine.Init(configDir); }
            catch (Exception ex) { err = "init_failed|" + ex.GetType().Name + ": " + ex.Message; }
            if (err != null)
            {
                string[] p = err.Split(new char[] { '|' }, 2);
                Log.Info("fatal: {0}", err);
                Send("{\"event\":\"fatal\",\"error\":" + Json.Quote(p[0]) + ",\"message\":" + Json.Quote(p[1]) + "}");
                Thread.Sleep(200);
                return p[0] == "version_mismatch" ? 3 : 4;
            }
            Log.Info("libobs {0} up in {1} ms", Engine.PinnedObsVersion, up.ElapsedMilliseconds);
            Send("{\"event\":\"ready\",\"protocol\":" + Protocol + ",\"obs\":" + Json.Quote(Engine.PinnedObsVersion) + "}");

            int code = Loop();

            // Whatever happens next, do not outlive the deadline (a driver may hang in shutdown).
            Thread deadline = new Thread(delegate() { Thread.Sleep(6000); Log.Info("shutdown deadline hit"); Process.GetCurrentProcess().Kill(); });
            deadline.IsBackground = true;
            deadline.Start();
            Log.Info("leaving: {0}", _quit ? "quit" : _parentGone ? "parent gone" : "pipe lost");
            try { Engine.Stop(); } catch (Exception ex) { Log.Info("stop: {0}", ex.Message); }
            try { Ring.Tick(NowMs()); Ring.CloseIndex(); } catch { }
            try { Engine.Shutdown(); } catch (Exception ex) { Log.Info("shutdown: {0}", ex.Message); }
            Log.Info("exit {0}", code);
            return code;
        }

        static long NowMs() { return Ring.ToUnixMs(DateTime.UtcNow); }

        static void ReadLoop()
        {
            try
            {
                StreamReader r = new StreamReader(_pipe, new UTF8Encoding(false), false, 1 << 16);
                for (;;)
                {
                    string line = r.ReadLine();
                    if (line == null) break;
                    if (line.Length == 0) continue;
                    lock (Requests) Requests.Enqueue(line);
                }
            }
            catch { }
            _pipeLost = true;
        }

        static void Send(string json)
        {
            byte[] b = Encoding.ASCII.GetBytes(json + "\n");
            try
            {
                lock (WriteGate)
                {
                    _pipe.Write(b, 0, b.Length);
                    _pipe.Flush();
                }
            }
            catch { _pipeLost = true; }
        }

        // ------------------------------------------------------------------ main loop

        static int Loop()
        {
            Stopwatch clock = Stopwatch.StartNew();
            long nextTick = 0, nextStats = 2000;
            for (;;)
            {
                if (_quit) return 0;
                if (_parentGone || _pipeLost) return 5;

                for (;;)
                {
                    string line;
                    lock (Requests) line = Requests.Count > 0 ? Requests.Dequeue() : null;
                    if (line == null) break;
                    try { Handle(line); }
                    catch (Exception ex) { Log.Info("request failed: {0}", ex); }
                    if (_quit) return 0;
                }

                ObsEvent e;
                while ((e = Engine.NextEvent()) != null)
                {
                    if (e.Kind == 3) Ring.OnFileChanged(e.Text, e.Ms);
                    else if (e.Kind == 2 && Engine.Recording)
                    {
                        string msg = Engine.LastOutputError();
                        Log.Info("recording stopped by itself: code {0} {1}", e.Code, msg);
                        Engine.OnUnexpectedStop();
                        string name = e.Code == -7 ? "no_space" : e.Code == -8 ? "encode_error" : "output_error";
                        Send("{\"event\":\"stopped\",\"error\":" + Json.Quote(name) + ",\"obsCode\":" + e.Code + ",\"message\":" + Json.Quote(msg) + "}");
                    }
                }
                Engine.PumpUiTasks();

                long t = clock.ElapsedMilliseconds;
                if (t >= nextTick)
                {
                    nextTick = t + 200;
                    try { Ring.Tick(NowMs()); } catch (Exception ex) { Log.Info("ring tick: {0}", ex.Message); }
                    _tickMsMax = Math.Max(_tickMsMax, clock.ElapsedMilliseconds - t);
                }
                if (t >= nextStats)
                {
                    nextStats = t + 2000;
                    if (Engine.Recording) Send("{\"event\":\"stats\"," + StatusFields() + "}");
                    _tickMsMax = 0;
                }
                Thread.Sleep(15);
            }
        }

        static string StatusFields()
        {
            long now = NowMs();
            Win.PMC_EX2 pm = new Win.PMC_EX2();
            pm.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Win.PMC_EX2));
            Win.GetProcessMemoryInfo(Win.GetCurrentProcess(), ref pm, pm.cb);
            StringBuilder sb = new StringBuilder(256);
            sb.Append("\"recording\":").Append(Engine.Recording ? "true" : "false");
            sb.Append(",\"encoder\":").Append(Json.Quote(Engine.Recording ? Engine.EncoderId : ""));
            sb.Append(",\"codec\":").Append(Json.Quote(Engine.Recording ? Engine.Codec : ""));
            sb.Append(",\"width\":").Append(Engine.Width).Append(",\"height\":").Append(Engine.Height).Append(",\"fps\":").Append(Engine.Fps);
            sb.Append(",\"since\":").Append(Engine.Recording ? Engine.SinceMs : 0);
            sb.Append(",\"bufferedSeconds\":").Append((Ring.Loaded ? Ring.BufferedSeconds(now) : 0).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(",\"frames\":").Append(Engine.Frames());
            sb.Append(",\"laggedFrames\":").Append(Engine.LaggedFrames());
            sb.Append(",\"skippedFrames\":").Append(Engine.SkippedFrames());
            sb.Append(",\"ringFiles\":").Append(Ring.Count).Append(",\"ringBytes\":").Append(Ring.TotalBytes);
            sb.Append(",\"pruneErrors\":").Append(Ring.PruneErrors);
            sb.Append(",\"privateMB\":").Append((pm.PrivateWorkingSetSize / 1048576.0).ToString("F1", CultureInfo.InvariantCulture));
            sb.Append(",\"tickMsMax\":").Append(_tickMsMax);   // longest ring housekeeping pass since the last stats event
            return sb.ToString();
        }

        // ------------------------------------------------------------------ requests

        static void Ok(long id, string extra)
        {
            Send("{\"id\":" + id + ",\"ok\":true" + (string.IsNullOrEmpty(extra) ? "" : "," + extra) + "}");
        }

        static void Fail(long id, string error, string message)
        {
            Send("{\"id\":" + id + ",\"ok\":false,\"error\":" + Json.Quote(error) + ",\"message\":" + Json.Quote(message) + "}");
        }

        static void Handle(string line)
        {
            Dictionary<string, object> req;
            try { req = Json.Read(line); }
            catch (Exception ex) { Log.Info("unreadable request: {0}", ex.Message); return; }
            long id = Json.Num(req, "id", 0);
            string cmd = Json.Str(req, "cmd", "");
            switch (cmd)
            {
                case "probe":
                {
                    Dictionary<string, object> r = new Dictionary<string, object>();
                    r["id"] = id; r["ok"] = true; r["obs"] = Engine.PinnedObsVersion; r["protocol"] = Protocol;
                    r["encoders"] = Engine.ProbeEncoders();
                    List<Dictionary<string, object>> mons = new List<Dictionary<string, object>>();
                    foreach (MonitorDesc m in Engine.Monitors())
                    {
                        Dictionary<string, object> d = new Dictionary<string, object>();
                        d["id"] = m.Id; d["device"] = m.Device; d["name"] = m.Name; d["x"] = m.X; d["y"] = m.Y;
                        d["width"] = m.Width; d["height"] = m.Height; d["primary"] = m.Primary;
                        mons.Add(d);
                    }
                    r["monitors"] = mons;
                    Send(Json.Write(r));
                    break;
                }
                case "start":
                {
                    StartConfig cfg;
                    try { cfg = ReadConfig(req); }
                    catch (Exception ex) { Fail(id, "bad_config", ex.Message); break; }
                    Stopwatch sw = Stopwatch.StartNew();
                    string err = Engine.Start(cfg);
                    if (err != null)
                    {
                        string[] p = err.Split(new char[] { '|' }, 2);
                        Log.Info("start failed: {0}", err);
                        Fail(id, p[0], p[1]);
                        break;
                    }
                    Log.Info("recording: {0} {1}x{2}@{3} segment {4}s buffer {5}s ring {6}  [{7} ms]", Engine.EncoderId, cfg.Width, cfg.Height, cfg.Fps,
                             cfg.SegmentSeconds, cfg.BufferSeconds, cfg.RingDir, sw.ElapsedMilliseconds);
                    Ok(id, "\"encoder\":" + Json.Quote(Engine.EncoderId) + ",\"codec\":" + Json.Quote(Engine.Codec) + ",\"width\":" + Engine.Width
                           + ",\"height\":" + Engine.Height + ",\"fps\":" + Engine.Fps);
                    break;
                }
                case "stop":
                    Engine.Stop();
                    Log.Info("recording stopped on request");
                    Ok(id, null);
                    break;
                case "status":
                    Ok(id, StatusFields());
                    break;
                case "save":
                {
                    if (!Ring.Loaded) { Fail(id, "not_recording", "no ring is open; start recording first"); break; }
                    Stopwatch sw = Stopwatch.StartNew();
                    long saveId;
                    List<SaveEntry> list = Ring.BuildSave((int)Json.Num(req, "seconds", 0), NowMs(), out saveId);
                    if (list == null) { Fail(id, "nothing_buffered", "the ring holds no complete footage yet"); break; }
                    StringBuilder sb = new StringBuilder(list.Count * 96 + 256);
                    sb.Append("{\"id\":").Append(id).Append(",\"ok\":true,\"saveId\":").Append(saveId);
                    sb.Append(",\"dir\":").Append(Json.Quote(Ring.Dir)).Append(",\"timeBase\":90000");
                    sb.Append(",\"fps\":").Append(Engine.Fps).Append(",\"codec\":").Append(Json.Quote(Engine.Codec));
                    sb.Append(",\"segments\":[");
                    for (int i = 0; i < list.Count; i++)
                    {
                        SaveEntry s = list[i];
                        if (i > 0) sb.Append(',');
                        sb.Append("{\"file\":").Append(Json.Quote(s.File)).Append(",\"bytes\":").Append(s.Bytes)
                          .Append(",\"v\":").Append(s.V).Append(",\"a\":").Append(s.A).Append(",\"ticks\":").Append(s.Ticks).Append('}');
                    }
                    sb.Append("]}");
                    Send(sb.ToString());
                    Log.Info("save {0}: {1} segment(s) listed in {2} ms", saveId, list.Count, sw.ElapsedMilliseconds);
                    break;
                }
                case "hold":
                    if (Ring.Hold(Json.Num(req, "saveId", 0), NowMs())) Ok(id, null);
                    else Fail(id, "unknown_save", "no such saveId");
                    break;
                case "release":
                    Ring.Release(Json.Num(req, "saveId", 0));
                    Ok(id, null);
                    break;
                case "quit":
                    Ok(id, null);
                    _quit = true;
                    break;
                case "debug_video_file":
                    if (_debug && Engine.DebugSetVideoFile(Json.Str(req, "path", ""))) Ok(id, null);
                    else Fail(id, "bad_request", "not available");
                    break;
                case "debug_source_size":
                    if (_debug) Ok(id, "\"size\":" + Json.Quote(Engine.SourceSize()));
                    else Fail(id, "bad_request", "not available");
                    break;
                default:
                    Fail(id, "bad_request", "unknown cmd " + cmd);
                    break;
            }
        }

        static StartConfig ReadConfig(Dictionary<string, object> r)
        {
            StartConfig c = new StartConfig();
            c.MonitorId = Json.Str(r, "monitorId", "");
            c.Width = (int)Json.Num(r, "width", 1920); c.Height = (int)Json.Num(r, "height", 1080); c.Fps = (int)Json.Num(r, "fps", 60);
            c.Cursor = Json.Flag(r, "cursor", true);
            c.Method = Json.Str(r, "method", "auto");
            c.AudioBitrateKbps = (int)Json.Num(r, "audioBitrateKbps", 160);
            c.DesktopAudio = Json.Flag(r, "desktopAudio", true); c.Mic = Json.Flag(r, "mic", false);
            c.BufferSeconds = (int)Json.Num(r, "bufferSeconds", 1800); c.SegmentSeconds = (int)Json.Num(r, "segmentSeconds", 2);
            c.RingDir = Json.Str(r, "ringDir", "");
            if (_debug)
            {
                c.DebugReference = Json.Str(r, "debugReference", "");
                c.DebugVideoFile = Json.Str(r, "debugVideoFile", "");
            }
            object list;
            if (r.TryGetValue("encoders", out list) && list is IEnumerable && !(list is string))
            {
                JavaScriptSerializer js = new JavaScriptSerializer();
                foreach (object o in (IEnumerable)list)
                {
                    Dictionary<string, object> d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    EncoderCandidate e = new EncoderCandidate();
                    e.Id = Json.Str(d, "id", "");
                    object s;
                    if (d.TryGetValue("settings", out s) && s is Dictionary<string, object>) e.SettingsJson = js.Serialize(s);
                    if (e.Id.Length > 0) c.Encoders.Add(e);
                }
            }
            return c;
        }
    }
}
