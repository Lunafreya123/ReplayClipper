# Replay Clipper built-in recorder: contract (v1)

Two parts, built by different people against this file:

- **Engine helper** `ReplayClipperEngine.exe`: hosts libobs (OBS 33.0.1, unmodified, version-pinned), no window. GPL-2.0-or-later, source shipped.
- **Engine client** `src\Engine.cs` (class `EngineClient`): part of the app. Starts and supervises the helper, speaks the protocol, turns ring segments into an MP4. It writes the MP4 itself; the app's bundled ffmpeg supplies the header, referees the result and is the fallback (see "Joining").

The app talks to the helper only through the protocol below (separate process, documented protocol).

## Layout in the product

```
<app>\ReplayClipper.exe
<app>\ffmpeg\ffmpeg.exe
<app>\engine\bin\64bit\ReplayClipperEngine.exe   next to obs.dll and the other OBS binaries
<app>\engine\core\<module>\<module>.dll          win-capture, win-wasapi, obs-ffmpeg, obs-x264, obs-nvenc, obs-qsv11
<app>\engine\data\libobs\*.effect
<app>\engine\licenses\                            GPL-2.0.txt, GPL-3.0.txt, THIRD-PARTY-NOTICES.txt, FILES.sha256.txt
<app>\src\engine\*.cs                             helper source (built by build-engine.cmd)
<app>\src\Engine.cs                               the client, compiled into the app
<data>\engine\config\                             helper's private libobs config dir
<data>\engine\logs\engine.log                     helper log, rotated at 4 MB (engine.1.log .. engine.3.log)
<data>\engine\join-header.mp4                     the client's; exists for a fraction of a second during a save (see "Joining")
<data>\buffer\                                    ring (default; the app may pass another folder): rcseg-*.ts, ring.json, ring.idx
```

No plugin data folders are shipped (that also keeps win-capture's updater from contacting the network). The helper must make no network requests. The helper only ever creates or deletes `rcseg-*.ts`, `ring.json` and `ring.idx` in the ring folder.

The bundled FFmpeg DLLs are built as "GPL version 3 or later", so the engine folder as a whole is distributed under GPLv3; the helper's own source is GPL-2.0-or-later, which is compatible.

## Process and transport

- The client creates a duplex named pipe `\\.\pipe\ReplayClipper.Engine.<pid>.<random>` (current user only) and starts the helper:
  `ReplayClipperEngine.exe --pipe <name> --parent <pid> --parent-start <FILETIME> --config-dir <dir> --log-dir <dir>`
  (`--debug` additionally enables the test-only requests and start fields used by the harness; the app never passes it.)
- Both ends open the pipe for overlapped I/O (`PipeOptions.Asynchronous`); without it a blocked read holds up writes.
- Messages: UTF-8 without BOM, one JSON object per line (`\n`), non-ASCII characters escaped as `\uXXXX`. There is no line-length limit: a `save` reply for a 12-hour ring is about 2 MB.
- The helper's stdout/stderr are not part of the protocol. Logs go to a rotating file in `--log-dir`.
- Lifetime: the client puts the helper in a job object with kill-on-close. The helper also watches the parent process handle (pid + start time) and the pipe; when either goes away it stops outputs, shuts libobs down within a deadline and exits.
- Exit codes: 0 normal, 2 bad arguments, 3 OBS version mismatch, 4 engine init failed, 5 parent gone / pipe lost.

## Messages

Requests carry `id`; the reply echoes it: `{"id":7,"ok":true,...}` or `{"id":7,"ok":false,"error":"<code>","message":"<english text for the log>"}`. Match replies by `id` (this helper answers in request order, but a client must not rely on it). Events have no `id`.

Events from the helper:
- `{"event":"ready","protocol":1,"obs":"33.0.1"}`: sent once after libobs and modules are up. Before it, the only possible message is `{"event":"fatal","error":"<code>","message":"..."}` followed by exit.
- `{"event":"stopped","error":"<code>","obsCode":-8,"message":"..."}`: recording ended without a `stop` request. `error` is `encode_error` (OBS code -8: encoder or mux helper failed), `no_space` (-7) or `output_error`. The helper is idle afterwards and accepts `start` again.
- `{"event":"stats", ...}`: same fields as the `status` reply, every 2 s while recording.

Requests (handled one at a time, in order):

| cmd | fields | reply |
|---|---|---|
| `probe` | none | `encoders`: the registered H.264/HEVC/AV1 video encoders (deprecated ids left out) in OBS registration order, each `{id, codec ("hevc"/"h264"/"av1"), vendor ("nvidia"/"amd"/"intel"/"software"), hardware}`. Ranking is the app's job. Registered means the vendor's probe passed, not that a session will open; `start` finds that out. Also `monitors`: `{id, device, name, x, y, width, height, primary}` (`id` is the OBS `monitor_id`), `obs` version, `protocol` |
| `start` | config, below | after the output has really started: `{encoder, codec, width, height, fps}`. Errors: `already_running`, `monitor_not_found`, `encoder_failed` (all candidates failed; message says why each failed), `output_failed`, `bad_config` |
| `stop` | none | after outputs stopped and sources/encoders released; ring files stay |
| `status` | none | `recording`, `encoder`, `codec`, `width`, `height`, `fps`, `since` (unix ms), `bufferedSeconds`, `frames` (total output frames, for stall detection), `laggedFrames`, `skippedFrames`, `ringFiles`, `ringBytes`, `pruneErrors`, `privateMB` (helper's private working set), `tickMsMax` |
| `save` | `seconds` (0 = everything buffered) | at once, no heavy work: `saveId`, `dir`, `timeBase` (90000), `fps`, `codec`, `segments`: ordered list of `{file, bytes, v, a, ticks}` covering at least the requested time, including the usable part of the file still being written. See "Joining" below. The helper does not delete these files until `release`, or until 5 minutes pass without a `hold`. Works whenever a ring is open (also after an unexpected stop). Errors: `not_recording` (no ring opened by a `start` yet), `nothing_buffered` |
| `hold` | `saveId` | ok; keeps the save's files for another 5 minutes (send about once a minute during a long save). Error: `unknown_save` |
| `release` | `saveId` | ok |
| `quit` | none | ok, then clean shutdown and exit 0 |

Unknown requests get `bad_request`.

`start` config:

```json
{
  "monitorId": "\\\\?\\DISPLAY#...",      // OBS monitor_id as the app lists it; \\.\DISPLAYn also accepted. Validated: unknown id = monitor_not_found
  "width": 1920, "height": 1080, "fps": 60,   // output size; the display is scaled to fit (aspect kept), also if its mode changes later
  "cursor": true,
  "encoders": [                                 // candidates in order; the first that really starts is used
    { "id": "h265_texture_amf", "settings": { "rate_control": "VBR", "bitrate": 16000, "keyint_sec": 2 } },
    { "id": "obs_x264", "settings": { } }
  ],
  "audioBitrateKbps": 160,
  "desktopAudio": true, "mic": false,           // default devices
  "bufferSeconds": 1800,                        // up to 43200 (12 hours)
  "segmentSeconds": 2,                          // 1..10; keyframe interval must equal this
  "ringDir": "D:\\...\\data\\buffer",
  "method": "auto"                              // optional: "auto" (default), "dxgi", "wgc"
}
```

Encoder `settings` are OBS encoder settings passed through unchanged; the helper forces the keyframe interval (`keyint_sec`) to `segmentSeconds`. Width and height must be even; fps is a whole number. The canvas has the output size and the display is fitted into it by a scene item ("scale to inner bounds", centred), so a display of another size or shape, or a later mode change, still fills the frame with the aspect kept (bars where the shapes differ). Audio is always AAC-LC 48 kHz stereo.

## Ring

- Continuous recording into keyframe-aligned MPEG-TS segment files of `segmentSeconds` each, all in `ringDir` (flat: one folder holds a 12-hour ring of 21,600 files without trouble; see the measurements). Names: `rcseg-<session>-first-<yyyymmdd>-<hhmmss>.ts` for the first file of a recording session, `rcseg-<session>-<yyyymmdd>-<hhmmss>.ts` after that.
- The helper keeps an in-memory index of the segments (file, wall-clock start, exact video and audio lengths). It is appended to `ring.idx` as files are completed and rebuilt once at `start` from `ring.idx` plus one directory listing; nothing enumerates the folder afterwards. A file without an index line (the one being written when a helper died) is scanned once.
- Old files are pruned from the index by wall-clock age (`bufferSeconds` + `segmentSeconds` + 2 s), never while pinned by a save.
- On `start`, files older than `bufferSeconds` are deleted; younger ones from a previous run stay usable (a clip may span a gap).
- The helper keeps a small `ring.json` in `ringDir` describing what the segments contain: codec, encoder id, the encoder settings actually used, width, height, fps, audio format and bitrate, segment length. On `start`, if the new recording would differ in any of these, all old segments are deleted first, so a clip never mixes formats. (Encoder settings are included because a different bitrate or profile changes the stream headers.) A different display with the same output size does not clear the ring.
- "Last N seconds" means wall-clock: segments that overlap [now − N, now]. A clip starts on a segment boundary, so it can be up to one segment longer than asked; if recording was interrupted inside that window it is shorter. Segments are `segmentSeconds` long, except that the first one of an x264 session was twice that in tests.
- `stop` keeps the files; they age out at the next `start` (the client deletes the folder when the app turns recording off for good).

## Joining (what `save` returns and what the client does with it)

Every segment restarts its timestamps at zero, so files cannot simply be appended, and ffmpeg's concat demuxer stretches the timeline (it offsets each file by its container duration, which is the longer of video and audio). The helper therefore reports, for each listed file, how far to shift its timestamps, and the client applies the shifts itself:

- `file`: name inside `dir`. `bytes`: how much of the file to read: a multiple of 188 that ends on a frame boundary. For the file still being written (and for a file cut off by a crash) this is less than the file size; never read beyond it.
- `v`, `a`: 90 kHz ticks to add to every video / audio timestamp in the file (PTS and DTS in PES headers; `v` also for the PCR). `ticks`: video length of the listed part.
- **Direct join (what normally happens).** The client writes the MP4 itself. Worker threads (up to four, at most half the cores) unpack one segment each: PES timestamps get the shifts, video frames go from Annex B start codes to length-prefixed NAL units with the same result as the MP4 muxer of FFmpeg 9 (zero bytes after a NAL unit dropped; HEVC VPS/SPS/PPS left out of the samples, because the sample entry is `hvc1`), AAC frames lose their ADTS header. One thread writes the converted segments in order and builds the sample tables. Encoded frames are passed through byte for byte. File layout: `ftyp`, `moov`, padding, `mdat`: index first, like faststart. The padding is what was reserved for the index and not needed (64 KB plus about 2 MB per hour at 60 fps).
- **ffmpeg supplies the header and is the referee.** While the footage is being written, the client pipes the first listed segment alone (timestamps patched as in the fallback below) into `ffmpeg -f mpegts -i pipe:0 -map 0:v:0 -map 0:a:0 -c copy [-tag:v hvc1] -movflags +faststart -f mp4 <data>\engine\join-header.mp4`, reads that small MP4 and deletes it. From it come `ftyp` and the skeleton of `moov`: sample entries (hvcC / avcC / esds), track and media headers, user data. And it is compared with the client's own work: every sample of the first segment must be byte-identical to the client's conversion, with the same sizes, durations, composition offsets and keyframes, and the edit lists and durations the client computes for that segment (the rules of FFmpeg 9's `mov_write_edts_tag` / `mov_write_tkhd_tag`) must be exactly ffmpeg's. Only then is the direct file kept. The check runs within the first 0.1 to 0.2 s of a save, so a disagreement costs almost nothing.
- **Fallback: the ffmpeg join of version 2.0.** Used when the direct join declines or fails: a codec other than H.264/HEVC, a stream structure it does not expect (more than one video or audio stream, audio that is not ADTS AAC), any difference from ffmpeg in the first segment, HEVC parameter sets that change inside the clip, a clip longer than 6 h 10 min (from 6 h 37 min FFmpeg writes 64-bit time fields in the header, which the direct join does not), or any error. The client then reads the files in order, patches the timestamps in the TS packets, renumbers the per-PID continuity counters across files and pipes the result, one continuous MPEG-TS stream, into `ffmpeg -f mpegts -i pipe:0 -map 0:v:0 -map 0:a:0 -c copy [-tag:v hvc1] -moov_size <n> -f mp4 <temp>` (`-moov_size` reserves room for the index at the front: 32 bytes per frame plus 64 KB); if that run fails it joins once more with `-movflags +faststart`. After changed HEVC parameter sets `-tag:v hvc1` is left out, so ffmpeg writes `hev1` and keeps the parameter sets in the stream. The client's log says which join made each file (`saved … (direct)` or `(ffmpeg)`) and why a fallback happened.
- Both joins give the same file content: the same samples byte for byte, the same timestamps, keyframes, edit lists and durations (compared on HEVC, x264 and AMF H.264 clips, see the measurements). They differ in how samples are grouped into chunks (the direct join writes about half a second of video, then half a second of audio; ffmpeg alternates frame by frame), in the amount of padding, and in one hint: the audio "maximum bitrate" in the sample entry, which ffmpeg estimates while probing.
- Within a recording session the shifts reproduce the timeline of one continuous recording exactly: frame n at n/fps, audio frame m at m·1024/48000, and the true audio-to-video offset (the helper tracks it across files, including the AAC priming frame at the start of a session). Across a break (helper restart, missing file) the next file starts where neither stream overlaps the previous one; that is the only place with an irregular step.
- A session begins with the AAC priming frame one audio frame (21.3 ms) before the first video frame. In a clip that starts at a session start ffmpeg therefore puts either the audio at −21.3 ms with the video at 0 (seen with HEVC; it writes an edit list) or the audio at 0 with the video at +21.3 ms (seen with x264 and AMF H.264, whose frames are reordered). Other clips have audio starting 0–21.3 ms after the video. The direct join does the same in each case: it works out both variants and takes the one ffmpeg's header for the first segment shows.

## Client (`EngineClient`, C# 5, .NET Framework 4.x, one file `src\Engine.cs`, namespace `ReplayClipper`)

Needs `System.Core.dll` and `System.Web.Extensions.dll` (both already referenced by the app's build.cmd). All helper classes are nested inside `EngineClient`; the only namespace-level names are the ones below. Note that the app already has nested `Program.EncoderInfo` and `Program.MonitorInfo`: inside `Program`, the unqualified name `EncoderInfo` means the app's own class, so refer to this one as `ReplayClipper.EncoderInfo` there (or rename the app's).

```csharp
public sealed class EngineException : Exception { public string Code; }
public sealed class EncoderInfo { public string Id, Codec, Vendor; public bool Hardware; }
public sealed class EncoderChoice { public string Id; public string SettingsJson; }          // also EncoderChoice(id, settingsJson)
public sealed class EngineMonitor { public string Id, Device, Name; public int X, Y, Width, Height; public bool Primary; }
public sealed class EngineConfig { public string MonitorId; public int Width, Height, Fps; public bool Cursor;
                                   public List<EncoderChoice> Encoders; public int AudioBitrateKbps; public bool DesktopAudio, Mic;
                                   public int BufferSeconds, SegmentSeconds; public string RingDir;
                                   public string Method;                          // null = auto
                                   public Dictionary<string, object> Extra; }     // tests only
public sealed class EngineStatus { public bool HelperRunning, Recording; public string Encoder, Codec; public int Width, Height, Fps;
                                   public DateTime SinceUtc; public double BufferedSeconds; public long Frames, LaggedFrames, SkippedFrames;
                                   public string LastError, LastErrorMessage;
                                   public int RingFiles; public long RingBytes; public int PruneErrors;      // additions
                                   public double HelperPrivateMB; public int HelperPid; public int Restarts; public long RingTickMsMax; }
public sealed class SaveResult { public string Path; public double Duration; public long Size; }

public sealed class EngineClient : IDisposable {
    public EngineClient(string engineExe, string ffmpegExe, string dataDir, Action<string> log);
    public List<EncoderInfo> Probe();                      // starts the helper if needed
    public List<EngineMonitor> Monitors();                 // displays as the helper sees them (addition)
    public void Start(EngineConfig config);                // returns when recording runs; throws EngineException
    public void Stop();                                    // stops recording and ends the helper
    public EngineStatus Status { get; }                    // cached snapshot (a new object per change), never blocks
    public SaveResult SaveLast(int seconds, string destPath);   // blocks until the MP4 is complete; throws EngineException
    public SaveResult SaveLast(int seconds, string destPath, Action<double> progress);   // progress: see below (addition)
    public event Action Changed;                           // status changed; raised on a thread-pool thread, also on every stats message (every 2 s)
    public bool Debug;                                     // tests only: start the helper with --debug
    public long DebugIndexReserve;                         // tests only: override the MP4 index reserve
    public string DebugJoin;                               // tests only: "ffmpeg" (always the ffmpeg join), "direct" (never fall back), "both"
    public Dictionary<string, object> DebugRequest(string cmd, Dictionary<string, object> fields, int timeoutMs);   // tests only: one raw request
    public void Dispose();
}
```

- `EngineException.Code` is a helper error code (see the requests) or one of the client's own: `busy`, `helper_missing`, `helper_failed` (did not start or sent `fatal`), `helper_lost` (not running / died during the request), `timeout`, `ffmpeg_missing`, `save_failed`, `bad_config`, `disposed`.
- `Start` on a client that is already recording stops first, then starts with the new config. If `Start` throws, nothing is wanted any more and the supervisor does not retry.
- Supervision: if the helper exits, stops sending messages for 15 s, stops producing frames for 20 s, or reports `stopped` while recording is wanted, the client restarts what is needed and starts recording again with the last config. The first retry is immediate, later ones wait 1, 2, 5, 15, 60 s; the back-off resets after 60 s of healthy recording. `Status` shows what is happening (`Recording`, `LastError`, `Restarts`). A helper left idle by `Probe` is ended after 60 s.
- `SaveLast`: `save` → join as described under "Joining" into `<destPath>.part` → the client checks that the MP4's duration covers the footage (after the ffmpeg join also ffmpeg's exit code) → rename to `destPath` → `release`. On any failure the `.part` file is deleted and an exception is thrown; a path is only ever returned for a complete file. If the app itself is killed during a save, `<destPath>.part` stays behind (the final name never exists); the next save to the same name replaces it. One save at a time (`busy` otherwise). `hold` is sent once a minute during long saves; in the ffmpeg join, an ffmpeg that stops consuming for 3 minutes is killed.
- `progress` is called on the thread that called `SaveLast`, with the share of the footage that is in the file so far. It only moves forward, in steps of at least half a percent, stays at or below 0.99 while the file is being written, and is called with exactly 1.0 once: after the rename, when the finished file exists under its final name. A failed save never reports 1.0. If the direct join gives up part-way and ffmpeg takes over, the value stands still until the second pass has caught up. Exceptions thrown by the callback are swallowed.
- The clip starts at the first listed segment (no tail cut is needed: stream copy can only start on a keyframe, and segments are exactly one keyframe interval).
- Save time is proportional to the footage, so `progress` is an honest bar. On the test machine (Ryzen 7 7800X3D, NVMe SSD; 16 Mbps HEVC 1080p60) the direct join takes about 0.19 s for 5 minutes, 0.85 s for 30 minutes and 2.0 s for 60 minutes (7 GB); with the ring read from the disk instead of the file cache, 1.1 s and 2.5 s for 30 and 60 minutes. That is what copying the same bytes takes. The ffmpeg join takes 0.8 s, 4.4 s and 9.2 s for the same clips. During a direct join the client uses up to four threads and, for a long clip, about 250 MB of memory.
- The MP4 (index at the front, video + AAC, HEVC tagged `hvc1`) has an exact timeline: constant frame cadence with no accumulated stretch across segment boundaries, the same A/V offset as a continuous recording, no missing or duplicated packets, timestamps starting at 0 (give or take the one priming frame described under "Joining").
