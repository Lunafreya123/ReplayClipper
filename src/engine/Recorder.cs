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

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReplayClipperEngine
{
    sealed class MonitorDesc
    {
        public string Id = "";      // device interface path, the form OBS stores as monitor_id
        public string Device = "";  // \\.\DISPLAYn
        public string Name = "";
        public int X, Y, Width, Height;
        public bool Primary;
    }

    sealed class EncoderCandidate
    {
        public string Id = "";
        public string SettingsJson = "{}";
    }

    sealed class StartConfig
    {
        public string MonitorId = "";
        public int Width = 1920, Height = 1080, Fps = 60;
        public bool Cursor = true;
        public string Method = "auto";
        public List<EncoderCandidate> Encoders = new List<EncoderCandidate>();
        public int AudioBitrateKbps = 160;
        public bool DesktopAudio = true, Mic;
        public int BufferSeconds = 1800, SegmentSeconds = 2;
        public string RingDir = "";
        public string DebugReference = "";   // --debug only: also write one continuous file from the same encoders
        public string DebugVideoFile = "";   // --debug only: show a media file instead of the display
    }

    sealed class ObsEvent
    {
        public int Kind;        // 1 start, 2 stop, 3 file_changed
        public string Text;
        public long Code;
        public long Ms;
    }

    /// <summary>Everything that touches libobs. Called from the main thread only, except the callbacks.</summary>
    static class Engine
    {
        public const string PinnedObsVersion = "33.0.1";
        static readonly string[] Modules = { "win-capture", "win-wasapi", "obs-ffmpeg", "obs-x264", "obs-nvenc", "obs-qsv11" };

        // delegates handed to native code must stay reachable
        static readonly Obs.LogHandler LogDelegate = Log.FromObs;
        static readonly Obs.TaskHandler UiTaskDelegate = OnUiTask;
        static readonly Obs.SignalCallback RingStartDelegate = OnRingStart;
        static readonly Obs.SignalCallback RingStopDelegate = OnRingStop;
        static readonly Obs.SignalCallback FileChangedDelegate = OnFileChanged;

        static readonly Queue<ObsEvent> Events = new Queue<ObsEvent>();
        static readonly Queue<KeyValuePair<IntPtr, IntPtr>> UiTasks = new Queue<KeyValuePair<IntPtr, IntPtr>>();

        public static string Root = "";
        static bool _up;
        static int _vidW, _vidH, _vidFps;

        // current recording
        public static bool Recording;
        public static string EncoderId = "", Codec = "";
        public static int Width, Height, Fps;
        public static long SinceMs;
        static IntPtr _scene, _display, _desktop, _mic, _venc, _aenc, _ring, _reference;

        // ------------------------------------------------------------------ bring-up

        /// <summary>Returns null when libobs is up, otherwise "code|message".</summary>
        public static string Init(string configDir)
        {
            string exeDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
            Root = Path.GetFullPath(Path.Combine(exeDir, "..\\..")).Replace('\\', '/').TrimEnd('/');
            // obs.dll resolves "../../data/libobs" and its helper exes relative to this folder
            Directory.SetCurrentDirectory(exeDir);

            Obs.base_set_log_handler(LogDelegate, IntPtr.Zero);
            Directory.CreateDirectory(configDir);
            if (!Obs.obs_startup(Obs.U("en-US"), Obs.U(configDir.Replace('\\', '/')), IntPtr.Zero))
                return "init_failed|obs_startup failed";
            string ver = Obs.S(Obs.obs_get_version_string()) ?? "";
            if (ver != PinnedObsVersion)
                return "version_mismatch|found libobs " + ver + ", this helper is built for " + PinnedObsVersion;
            if (Marshal.SizeOf(typeof(Obs.VideoInfo)) != 56) return "init_failed|obs_video_info layout";
            Obs.obs_set_ui_task_handler(UiTaskDelegate);
            Obs.obs_add_data_path(Obs.U(Root + "/data/libobs/"));

            Obs.AudioInfo ai = new Obs.AudioInfo();
            ai.samples_per_sec = 48000;
            ai.speakers = Obs.SPEAKERS_STEREO;
            if (!Obs.obs_reset_audio(ref ai)) return "init_failed|obs_reset_audio failed";
            string v = ResetVideo(1920, 1080, 60);
            if (v != null) return "init_failed|" + v;

            // Graphics must be up before modules load. Only the modules listed here are loaded, by
            // absolute path, and without data folders (that also keeps win-capture's updater off).
            foreach (string m in Modules)
            {
                string bin = Root + "/core/" + m + "/" + m + ".dll";
                if (!File.Exists(bin)) { Log.Info("module {0}: not shipped", m); continue; }
                IntPtr mod;
                int code = Obs.obs_open_module(out mod, Obs.U(bin), Obs.U(Root + "/core/" + m + "/data"));
                if (code != 0) { Log.Info("module {0}: obs_open_module rc={1}", m, code); continue; }
                bool ok = Obs.obs_init_module(mod);
                Log.Info("module {0}: {1}", m, ok ? "loaded" : "not usable on this machine");
            }
            Obs.obs_post_load_modules();
            _up = true;
            return null;
        }

        static string ResetVideo(int w, int h, int fps)
        {
            if (_vidW == w && _vidH == h && _vidFps == fps) return null;
            Obs.VideoInfo vi = new Obs.VideoInfo();
            vi.graphics_module = Marshal.StringToHGlobalAnsi("libobs-d3d11");   // a few bytes per reset, kept alive on purpose
            vi.fps_num = (uint)fps; vi.fps_den = 1;
            // The canvas has the output size; the display is fitted into it by a scene item (see BuildScene),
            // so neither a different monitor nor a later mode change needs another video reset.
            vi.base_width = (uint)w; vi.base_height = (uint)h;
            vi.output_width = (uint)w; vi.output_height = (uint)h;
            vi.output_format = Obs.VIDEO_FORMAT_NV12;
            vi.adapter = 0;
            vi.gpu_conversion = true;
            vi.colorspace = Obs.VIDEO_CS_709;
            vi.range = Obs.VIDEO_RANGE_PARTIAL;
            vi.scale_type = Obs.OBS_SCALE_BICUBIC;
            int rc = Obs.obs_reset_video(ref vi);
            if (rc != 0) { _vidW = 0; return "obs_reset_video returned " + rc; }
            // libobs leaves both levels at 0 until they are set (the OBS frontend always sets them). Sources
            // that convert HDR to SDR divide by the SDR white level, so with 0 every pixel becomes NaN and the
            // picture is black. 300 / 1000 nits are OBS's defaults.
            Obs.obs_set_video_levels(300f, 1000f);
            _vidW = w; _vidH = h; _vidFps = fps;
            return null;
        }

        public static void Shutdown()
        {
            if (!_up) return;
            _up = false;
            Obs.obs_shutdown();
        }

        // ------------------------------------------------------------------ probe

        public static List<string> EncoderIds()
        {
            List<string> ids = new List<string>();
            for (uint i = 0; ; i++)
            {
                IntPtr id;
                if (!Obs.obs_enum_encoder_types((UIntPtr)i, out id)) break;
                ids.Add(Obs.S(id));
            }
            return ids;
        }

        public static string VendorOf(string id)
        {
            string l = id.ToLowerInvariant();
            return l.Contains("nvenc") ? "nvidia" : l.Contains("amf") ? "amd" : l.Contains("qsv") ? "intel" : "software";
        }

        public static string CodecOf(string id)
        {
            return Obs.S(Obs.obs_get_encoder_codec(Obs.U(id))) ?? "";
        }

        /// <summary>Registered video encoders (H.264 / HEVC / AV1), in OBS registration order.</summary>
        public static List<Dictionary<string, object>> ProbeEncoders()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
            foreach (string id in EncoderIds())
            {
                if (Obs.obs_get_encoder_type(Obs.U(id)) != Obs.OBS_ENCODER_VIDEO) continue;
                uint caps = Obs.obs_get_encoder_caps(Obs.U(id));
                if ((caps & (Obs.OBS_ENCODER_CAP_DEPRECATED | Obs.OBS_ENCODER_CAP_INTERNAL)) != 0) continue;
                string codec = CodecOf(id);
                if (codec != "h264" && codec != "hevc" && codec != "av1") continue;
                Dictionary<string, object> e = new Dictionary<string, object>();
                string vendor = VendorOf(id);
                e["id"] = id; e["codec"] = codec; e["vendor"] = vendor; e["hardware"] = vendor != "software";
                list.Add(e);
            }
            return list;
        }

        public static List<MonitorDesc> Monitors()
        {
            List<MonitorDesc> list = new List<MonitorDesc>();
            Win.MonitorEnumProc cb = delegate(IntPtr h, IntPtr hdc, IntPtr rc, IntPtr lp)
            {
                Win.MONITORINFOEX mi = new Win.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(Win.MONITORINFOEX));
                if (!Win.GetMonitorInfoW(h, ref mi)) return true;
                MonitorDesc m = new MonitorDesc();
                m.Device = mi.szDevice;
                m.X = mi.rcMonitor.left; m.Y = mi.rcMonitor.top;
                m.Width = mi.rcMonitor.right - mi.rcMonitor.left; m.Height = mi.rcMonitor.bottom - mi.rcMonitor.top;
                m.Primary = (mi.dwFlags & 1) != 0;
                Win.DISPLAY_DEVICE dd = new Win.DISPLAY_DEVICE();
                dd.cb = Marshal.SizeOf(typeof(Win.DISPLAY_DEVICE));
                if (Win.EnumDisplayDevicesW(mi.szDevice, 0, ref dd, Win.EDD_GET_DEVICE_INTERFACE_NAME))
                {
                    m.Id = dd.DeviceID ?? "";
                    m.Name = dd.DeviceString ?? "";
                }
                if (m.Id.Length == 0) m.Id = m.Device;
                list.Add(m);
                return true;
            };
            Win.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return list;
        }

        // ------------------------------------------------------------------ start

        static IntPtr Settings(params object[] kv)
        {
            IntPtr d = Obs.obs_data_create();
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                byte[] k = Obs.U((string)kv[i]);
                object v = kv[i + 1];
                if (v is bool) Obs.obs_data_set_bool(d, k, (bool)v);
                else if (v is int) Obs.obs_data_set_int(d, k, (int)v);
                else if (v is long) Obs.obs_data_set_int(d, k, (long)v);
                else Obs.obs_data_set_string(d, k, Obs.U(Convert.ToString(v, CultureInfo.InvariantCulture)));
            }
            return d;
        }

        static IntPtr Source(string id, string name, IntPtr settings)
        {
            IntPtr s = Obs.obs_source_create(Obs.U(id), Obs.U(name), settings, IntPtr.Zero);
            Obs.obs_data_release(settings);
            return s;
        }

        static void BuildScene(StartConfig cfg, string monitorId)
        {
            if (cfg.DebugVideoFile.Length > 0)
                _display = Source("ffmpeg_source", "rc-video", Settings("local_file", cfg.DebugVideoFile.Replace('\\', '/'), "is_local_file", true, "looping", true));
            else
            {
                int method = cfg.Method == "dxgi" ? 1 : cfg.Method == "wgc" ? 2 : 0;
                // The recording is SDR (Rec. 709). force_sdr maps an HDR desktop with the display's own
                // "SDR content brightness" setting, so the desktop and SDR games look as they do with HDR off;
                // HDR highlights above that level are clipped. Without it libobs tone-maps the whole picture,
                // which makes SDR content about one stop darker.
                _display = Source("monitor_capture", "rc-display", Settings("monitor_id", monitorId, "method", method, "capture_cursor", cfg.Cursor, "force_sdr", true));
            }
            // A scene item with "scale to inner bounds" fits whatever size the source has into the canvas,
            // centred, aspect kept. libobs re-evaluates it whenever the source size changes, so a display
            // mode change later still fills the frame.
            _scene = Obs.obs_scene_create(Obs.U("rc-scene"));
            IntPtr item = Obs.obs_scene_add(_scene, _display);
            Obs.Vec2 zero = new Obs.Vec2();
            Obs.Vec2 bounds = new Obs.Vec2();
            bounds.x = cfg.Width; bounds.y = cfg.Height;
            Obs.obs_sceneitem_set_alignment(item, Obs.OBS_ALIGN_LEFT | Obs.OBS_ALIGN_TOP);
            Obs.obs_sceneitem_set_pos(item, ref zero);
            Obs.obs_sceneitem_set_bounds_type(item, Obs.OBS_BOUNDS_SCALE_INNER);
            Obs.obs_sceneitem_set_bounds_alignment(item, Obs.OBS_ALIGN_CENTER);
            Obs.obs_sceneitem_set_bounds(item, ref bounds);
            Obs.obs_sceneitem_set_scale_filter(item, Obs.OBS_SCALE_BICUBIC);
            Obs.obs_set_output_source(0, Obs.obs_scene_get_source(_scene));

            if (cfg.DesktopAudio)
            {
                _desktop = Source("wasapi_output_capture", "rc-desktop-audio", Settings("device_id", "default"));
                if (_desktop != IntPtr.Zero) Obs.obs_set_output_source(1, _desktop);
            }
            if (cfg.Mic)
            {
                _mic = Source("wasapi_input_capture", "rc-mic", Settings("device_id", "default"));
                if (_mic != IntPtr.Zero) Obs.obs_set_output_source(3, _mic);
            }
        }

        /// <summary>--debug only: point the video source at another media file (stands in for a mode change).</summary>
        public static bool DebugSetVideoFile(string path)
        {
            if (_display == IntPtr.Zero) return false;
            IntPtr s = Settings("local_file", path.Replace('\\', '/'), "is_local_file", true, "looping", true);
            Obs.obs_source_update(_display, s);
            Obs.obs_data_release(s);
            return true;
        }

        public static string SourceSize()
        {
            return _display == IntPtr.Zero ? "" : Obs.obs_source_get_width(_display) + "x" + Obs.obs_source_get_height(_display);
        }

        static void ReleaseScene()
        {
            for (uint ch = 0; ch < 6; ch++) Obs.obs_set_output_source(ch, IntPtr.Zero);
            // The main canvas holds its own reference to every scene (SCENE_REF in OBS 31+), so releasing ours
            // alone would keep the scene and the display capture alive until obs_shutdown. Remove it first.
            if (_scene != IntPtr.Zero)
            {
                Obs.obs_source_remove(Obs.obs_scene_get_source(_scene));
                Obs.obs_scene_release(_scene);
                _scene = IntPtr.Zero;
            }
            if (_display != IntPtr.Zero) { Obs.obs_source_release(_display); _display = IntPtr.Zero; }
            if (_desktop != IntPtr.Zero) { Obs.obs_source_release(_desktop); _desktop = IntPtr.Zero; }
            if (_mic != IntPtr.Zero) { Obs.obs_source_release(_mic); _mic = IntPtr.Zero; }
        }

        static void ReleaseOutputs()
        {
            if (_reference != IntPtr.Zero) { Obs.obs_output_release(_reference); _reference = IntPtr.Zero; }
            if (_ring != IntPtr.Zero) { Obs.obs_output_release(_ring); _ring = IntPtr.Zero; }
            if (_venc != IntPtr.Zero) { Obs.obs_encoder_release(_venc); _venc = IntPtr.Zero; }
            if (_aenc != IntPtr.Zero) { Obs.obs_encoder_release(_aenc); _aenc = IntPtr.Zero; }
        }

        /// <summary>What the ring will contain. Two recordings may share a ring only if this text is equal.</summary>
        static string FormatJson(StartConfig cfg, EncoderCandidate c, string codec, string effectiveSettings)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"version\":1,\"codec\":").Append(Json.Quote(codec));
            sb.Append(",\"encoder\":").Append(Json.Quote(c.Id));
            sb.Append(",\"encoderSettings\":").Append(Json.Quote(effectiveSettings));
            sb.Append(",\"width\":").Append(cfg.Width).Append(",\"height\":").Append(cfg.Height).Append(",\"fps\":").Append(cfg.Fps);
            sb.Append(",\"audio\":").Append(Json.Quote("aac/48000/2/" + cfg.AudioBitrateKbps));
            sb.Append(",\"segmentSeconds\":").Append(cfg.SegmentSeconds).Append("}");
            return sb.ToString();
        }

        /// <summary>
        /// Starts recording with the first encoder candidate that really starts.
        /// Returns null on success, otherwise "code|message".
        /// </summary>
        public static string Start(StartConfig cfg)
        {
            if (Recording) return "already_running|recording is already running";
            if (cfg.Width < 16 || cfg.Height < 16 || (cfg.Width & 1) != 0 || (cfg.Height & 1) != 0) return "bad_config|width and height must be even";
            if (cfg.Fps < 1 || cfg.Fps > 240) return "bad_config|fps out of range";
            if (cfg.SegmentSeconds < 1 || cfg.SegmentSeconds > 10) return "bad_config|segmentSeconds must be 1..10";
            if (cfg.BufferSeconds < cfg.SegmentSeconds * 2) return "bad_config|bufferSeconds too small";
            if (cfg.Encoders.Count == 0) return "bad_config|no encoder candidates";
            if (cfg.RingDir.Length == 0) return "bad_config|ringDir missing";

            string monitorId = "";
            if (cfg.DebugVideoFile.Length == 0)
            {
                foreach (MonitorDesc m in Monitors())
                    if (string.Equals(m.Id, cfg.MonitorId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Device, cfg.MonitorId, StringComparison.OrdinalIgnoreCase))
                        monitorId = m.Id;
                if (monitorId.Length == 0) return "monitor_not_found|no connected display has the id " + cfg.MonitorId;
            }
            try { Directory.CreateDirectory(cfg.RingDir); }
            catch (Exception ex) { return "output_failed|cannot create ring folder: " + ex.Message; }

            string v = ResetVideo(cfg.Width, cfg.Height, cfg.Fps);
            if (v != null) return "output_failed|" + v;

            BuildScene(cfg, monitorId);
            if (_display == IntPtr.Zero) { ReleaseScene(); return "output_failed|could not create the capture source"; }

            List<string> registered = EncoderIds();
            StringBuilder why = new StringBuilder();
            foreach (EncoderCandidate c in cfg.Encoders)
            {
                if (!registered.Contains(c.Id)) { why.Append(c.Id).Append(": not available on this machine; "); continue; }
                string err = TryCandidate(cfg, c);
                if (err == null)
                {
                    Recording = true; EncoderId = c.Id; Codec = CodecOf(c.Id);
                    Width = cfg.Width; Height = cfg.Height; Fps = cfg.Fps;
                    SinceMs = Ring.ToUnixMs(DateTime.UtcNow);
                    if (why.Length > 0) Log.Info("start: using {0} after: {1}", c.Id, why);
                    return null;
                }
                Log.Info("start: {0} failed: {1}", c.Id, err);
                why.Append(c.Id).Append(": ").Append(err).Append("; ");
            }
            ReleaseScene();
            return "encoder_failed|" + why.ToString().Trim();
        }

        static string TryCandidate(StartConfig cfg, EncoderCandidate c)
        {
            IntPtr vs = Obs.obs_data_create_from_json(Obs.U(c.SettingsJson));
            if (vs == IntPtr.Zero) return "settings are not valid JSON";
            // one segment = one GOP: the muxer can only switch files on a keyframe
            Obs.obs_data_set_int(vs, Obs.U("keyint_sec"), cfg.SegmentSeconds);
            string effective = Obs.S(Obs.obs_data_get_json(vs)) ?? "";
            _venc = Obs.obs_video_encoder_create(Obs.U(c.Id), Obs.U("rc-video"), vs, IntPtr.Zero);
            Obs.obs_data_release(vs);
            if (_venc == IntPtr.Zero) return "encoder could not be created";
            Obs.obs_encoder_set_video(_venc, Obs.obs_get_video());

            IntPtr s = Settings("bitrate", cfg.AudioBitrateKbps);
            _aenc = Obs.obs_audio_encoder_create(Obs.U("ffmpeg_aac"), Obs.U("rc-audio"), s, UIntPtr.Zero, IntPtr.Zero);
            Obs.obs_data_release(s);
            if (_aenc == IntPtr.Zero) { ReleaseOutputs(); return "AAC encoder could not be created"; }
            Obs.obs_encoder_set_audio(_aenc, Obs.obs_get_audio());

            long now = Ring.ToUnixMs(DateTime.UtcNow);
            try { Ring.Load(cfg.RingDir, FormatJson(cfg, c, CodecOf(c.Id), effective), cfg.BufferSeconds, cfg.SegmentSeconds, cfg.Fps, 1, now); }
            catch (Exception ex) { ReleaseOutputs(); return "ring folder: " + ex.Message; }

            string nameFormat;
            string first = Ring.BeginSession(now, out nameFormat).Replace('\\', '/');
            s = Settings("path", first, "split_file", true, "max_time_sec", cfg.SegmentSeconds, "max_size_mb", 0, "allow_overwrite", false,
                         "directory", cfg.RingDir.Replace('\\', '/'), "format", nameFormat, "extension", "ts", "allow_spaces", false);
            _ring = Obs.obs_output_create(Obs.U("ffmpeg_muxer"), Obs.U("rc-ring"), s, IntPtr.Zero);
            Obs.obs_data_release(s);
            if (_ring == IntPtr.Zero) { Ring.EndSession(false, now); ReleaseOutputs(); Ring.DiscardSession(); return "ring output could not be created"; }
            Obs.obs_output_set_video_encoder(_ring, _venc);
            Obs.obs_output_set_audio_encoder(_ring, _aenc, UIntPtr.Zero);
            IntPtr sh = Obs.obs_output_get_signal_handler(_ring);
            Obs.signal_handler_connect(sh, Obs.U("start"), RingStartDelegate, IntPtr.Zero);
            Obs.signal_handler_connect(sh, Obs.U("stop"), RingStopDelegate, IntPtr.Zero);
            Obs.signal_handler_connect(sh, Obs.U("file_changed"), FileChangedDelegate, IntPtr.Zero);

            lock (Events) Events.Clear();
            string err = null;
            if (!Obs.obs_output_start(_ring))
                err = Obs.S(Obs.obs_output_get_last_error(_ring)) ?? Obs.S(Obs.obs_encoder_get_last_error(_venc)) ?? "the output did not start";
            else
            {
                // a failure after obs_output_start returned is reported through the stop signal
                Stopwatch sw = Stopwatch.StartNew();
                bool started = false;
                while (!started && err == null && sw.ElapsedMilliseconds < 10000)
                {
                    Thread.Sleep(5);
                    PumpUiTasks();
                    lock (Events)
                    {
                        while (Events.Count > 0)
                        {
                            ObsEvent e = Events.Peek();
                            if (e.Kind == 1) { Events.Dequeue(); started = true; break; }
                            if (e.Kind == 2) { Events.Dequeue(); err = "stopped with code " + e.Code + ": " + (Obs.S(Obs.obs_output_get_last_error(_ring)) ?? ""); break; }
                            break;   // a file_changed can only follow a start
                        }
                    }
                }
                if (!started && err == null) err = "no start signal within 10 s";

                // libobs signals "start" as soon as the encoders are hooked up, before anything is encoded. An encoder
                // that fails on its first frames stops the output a moment later; wait for the first packet so that
                // such a failure moves on to the next candidate instead of being retried with the same encoder.
                Stopwatch fw = Stopwatch.StartNew();
                while (err == null && Obs.obs_output_get_total_frames(_ring) <= 0 && fw.ElapsedMilliseconds < 5000)
                {
                    Thread.Sleep(10);
                    PumpUiTasks();
                    ObsEvent e = null;
                    lock (Events) { if (Events.Count > 0) e = Events.Dequeue(); }
                    if (e == null) continue;
                    if (e.Kind == 2)
                        err = "stopped on its first frames with code " + e.Code + ": " + (Obs.S(Obs.obs_output_get_last_error(_ring)) ?? Obs.S(Obs.obs_encoder_get_last_error(_venc)) ?? "");
                    else if (e.Kind == 3) Ring.OnFileChanged(e.Text, e.Ms);
                }
            }
            if (err != null)
            {
                if (Obs.obs_output_active(_ring)) Obs.obs_output_force_stop(_ring);
                Ring.EndSession(false, Ring.ToUnixMs(DateTime.UtcNow));
                ReleaseOutputs();
                Ring.DiscardSession();
                return err.Trim();
            }
            // this encoder really records: only now may footage of another format be deleted
            Ring.CommitFormat();

            // known once the encoder is running; 1024 samples for AAC
            uint priming = Obs.obs_encoder_get_priming_samples(_aenc);
            Ring.PrimingTicks = priming > 0 ? priming * 90000L / 48000 : Ring.AudioFrameTicks;

            if (cfg.DebugReference.Length > 0)
            {
                // one reference file per recording session: <name>-<session>.ts
                string refPath = Path.Combine(Path.GetDirectoryName(cfg.DebugReference),
                                              Path.GetFileNameWithoutExtension(cfg.DebugReference) + "-" + Ring.Session + Path.GetExtension(cfg.DebugReference));
                s = Settings("path", refPath.Replace('\\', '/'));
                _reference = Obs.obs_output_create(Obs.U("ffmpeg_muxer"), Obs.U("rc-reference"), s, IntPtr.Zero);
                Obs.obs_data_release(s);
                Obs.obs_output_set_video_encoder(_reference, _venc);
                Obs.obs_output_set_audio_encoder(_reference, _aenc, UIntPtr.Zero);
                if (!Obs.obs_output_start(_reference)) Log.Info("debug reference output did not start");
            }
            return null;
        }

        // ------------------------------------------------------------------ stop

        /// <summary>Stops recording on request. Ring files stay.</summary>
        public static void Stop()
        {
            if (!Recording) return;
            if (_reference != IntPtr.Zero && Obs.obs_output_active(_reference)) Obs.obs_output_stop(_reference);
            bool clean = false;
            if (_ring != IntPtr.Zero && Obs.obs_output_active(_ring))
            {
                Obs.obs_output_stop(_ring);
                Stopwatch sw = Stopwatch.StartNew();
                bool stopped = false;
                while (sw.ElapsedMilliseconds < 10000 && !stopped)
                {
                    Thread.Sleep(5);
                    PumpUiTasks();
                    ObsEvent e;
                    while ((e = NextEvent()) != null)
                    {
                        if (e.Kind == 3) Ring.OnFileChanged(e.Text, e.Ms);
                        else if (e.Kind == 2) { stopped = true; clean = e.Code == 0; }
                    }
                }
                if (!stopped) Obs.obs_output_force_stop(_ring);
            }
            if (_reference != IntPtr.Zero)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (Obs.obs_output_active(_reference) && sw.ElapsedMilliseconds < 5000) Thread.Sleep(10);
            }
            Finish(clean);
        }

        /// <summary>The output stopped by itself (encoder or muxer failure).</summary>
        public static void OnUnexpectedStop()
        {
            if (!Recording) return;
            if (_reference != IntPtr.Zero && Obs.obs_output_active(_reference)) Obs.obs_output_force_stop(_reference);
            Finish(false);
        }

        static void Finish(bool clean)
        {
            Ring.EndSession(clean, Ring.ToUnixMs(DateTime.UtcNow));
            ReleaseOutputs();
            ReleaseScene();
            Recording = false;
            Ring.Tick(Ring.ToUnixMs(DateTime.UtcNow));
        }

        public static string LastOutputError()
        {
            return _ring == IntPtr.Zero ? "" : (Obs.S(Obs.obs_output_get_last_error(_ring)) ?? "");
        }

        public static long Frames() { return _ring == IntPtr.Zero ? 0 : Obs.obs_output_get_total_frames(_ring); }
        public static long LaggedFrames() { return Obs.obs_get_lagged_frames(); }
        public static long SkippedFrames() { return Obs.video_output_get_skipped_frames(Obs.obs_get_video()); }

        // ------------------------------------------------------------------ callbacks (libobs threads; must not block)

        static void Post(int kind, string text, long code)
        {
            ObsEvent e = new ObsEvent();
            e.Kind = kind; e.Text = text; e.Code = code; e.Ms = Ring.ToUnixMs(DateTime.UtcNow);
            lock (Events) Events.Enqueue(e);
        }

        static void OnRingStart(IntPtr data, IntPtr cd) { Post(1, null, 0); }

        static void OnRingStop(IntPtr data, IntPtr cd)
        {
            long code = 0;
            try { Obs.calldata_get_data(cd, Obs.U("code"), out code, (UIntPtr)8); } catch { }
            Post(2, null, code);
        }

        static void OnFileChanged(IntPtr data, IntPtr cd)
        {
            string next = null;
            try
            {
                IntPtr sp;
                if (Obs.calldata_get_string(cd, Obs.U("next_file"), out sp)) next = Obs.S(sp);
            }
            catch { }
            if (next != null) Post(3, next, 0);
        }

        public static ObsEvent NextEvent()
        {
            lock (Events) return Events.Count > 0 ? Events.Dequeue() : null;
        }

        // libobs asks its "UI thread" to run a task (win-wasapi does on a default-device change)
        static void OnUiTask(IntPtr task, IntPtr param, bool wait)
        {
            if (task == IntPtr.Zero) return;
            if (wait)
            {
                ((Obs.TaskFn)Marshal.GetDelegateForFunctionPointer(task, typeof(Obs.TaskFn)))(param);
                return;
            }
            lock (UiTasks) UiTasks.Enqueue(new KeyValuePair<IntPtr, IntPtr>(task, param));
        }

        public static void PumpUiTasks()
        {
            for (;;)
            {
                KeyValuePair<IntPtr, IntPtr> t;
                lock (UiTasks)
                {
                    if (UiTasks.Count == 0) return;
                    t = UiTasks.Dequeue();
                }
                ((Obs.TaskFn)Marshal.GetDelegateForFunctionPointer(t.Key, typeof(Obs.TaskFn)))(t.Value);
            }
        }
    }
}
