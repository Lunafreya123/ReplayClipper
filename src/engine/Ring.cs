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
using System.Globalization;
using System.IO;
using System.Text;

namespace ReplayClipperEngine
{
    /// <summary>One segment file of the ring. Times are 90 kHz ticks on the file's own clock.</summary>
    sealed class Seg
    {
        public long Seq;            // order in this helper process (pins refer to it)
        public string File;         // name only
        public string Session;      // recording session that wrote it
        public string Prev = "";    // name of the file it continues, "" at a session start
        public long StartMs;        // wall clock (unix ms) when the muxer switched to this file
        public long Bytes;          // usable length
        public long Vp, Vd, VLastDts, Ap, AEnd;
        public int VFrames;
        public long Av;             // true (audio start - video start) of this file in its session
        public bool Cont;           // continues the previous entry without a gap
        public bool Ready;          // metadata known
        public bool Open;           // the muxer is still writing it
        public bool Truncated;      // cut off by a crash; usable part ends at Bytes
        public bool First;          // first file of a recording session
        public long ClosingSinceMs;
        public TsScan.Head Head;

        public long ADur { get { return AEnd - Ap; } }
    }

    /// <summary>What the client needs to place one segment on the joined timeline.</summary>
    sealed class SaveEntry
    {
        public string File;
        public long Bytes;
        public long V, A;           // add to every video / audio timestamp of the file
        public long Ticks;          // video length of the usable part
    }

    /// <summary>
    /// The on-disk ring: an in-memory index of the segment files (rebuilt once at start from ring.idx
    /// and one directory listing), pruning by wall-clock age, pins for running saves, and the
    /// arithmetic that turns "these files" into exact per-file timestamp shifts.
    /// Only the main thread touches it.
    /// </summary>
    static class Ring
    {
        public const string Prefix = "rcseg-";
        const string IdxName = "ring.idx";
        const string FormatName = "ring.json";
        public const long AudioFrameTicks = 1920;   // 1024 samples at 48 kHz

        public static string Dir;
        public static bool Loaded;
        static readonly List<Seg> Segs = new List<Seg>();          // oldest first; an open segment is last
        static readonly List<Seg> Closing = new List<Seg>();       // muxer has moved on; waiting for it to close the file
        static readonly Dictionary<long, long[]> Pins = new Dictionary<long, long[]>();   // saveId -> { first seq, expiry ms }
        static Seg _open;
        static long _nextSeq = 1, _nextSaveId = 1;
        static long _fpsNum = 60, _fpsDen = 1;
        static int _bufferSeconds = 1800, _segmentSeconds = 2;
        static StreamWriter _idx;
        static int _idxLines;
        // Load found segments of another format: they are kept out of the ring but stay on disk until the
        // new recording has really started (CommitFormat). An encoder that fails to start must not cost the
        // footage: the next candidate, or the next start with the original encoder, may still use it.
        static string _pendingFormat;
        static List<string> _pendingDelete;
        static string _session = "";
        public static string Session { get { return _session; } }
        static bool _sessionBroken;
        const string FirstMark = "-first-";

        /// <summary>
        /// Encoder delay of the audio codec in ticks (AAC: 1024 samples). A session's first audio packet is
        /// the priming packet, one frame BEFORE the first video frame; the muxer stamps both with the same
        /// time when it writes a split file, so the true offset has to be put back here.
        /// </summary>
        public static long PrimingTicks = AudioFrameTicks;

        /// <summary>True audio-minus-video start of a file that begins a chain (nothing to continue from).</summary>
        static long ChainStartAv(Seg s)
        {
            return s.Ap - s.Vp - (s.First ? PrimingTicks : 0);
        }

        public static long TotalBytes;
        static long _totalFrames;
        public static int PruneErrors;
        static string _lastPruneError = "";

        public static int Count { get { return Segs.Count; } }

        public static double BufferedSeconds(long nowMs)
        {
            double s = _totalFrames * (double)_fpsDen / _fpsNum;
            if (_open != null) s += Math.Max(0, nowMs - _open.StartMs) / 1000.0;
            return s;
        }

        static long FrameTicks(long frames)
        {
            return (frames * 90000 * _fpsDen + _fpsNum / 2) / _fpsNum;
        }

        static long VDur(Seg s) { return FrameTicks(s.VFrames); }

        static long EndMs(Seg s) { return s.StartMs + s.VFrames * 1000L * _fpsDen / _fpsNum; }

        // ------------------------------------------------------------------ load / wipe

        /// <summary>
        /// Adopts what is in the folder. <paramref name="format"/> describes what the new recording will
        /// write; if the segments already there were written with anything else they are deleted first,
        /// so a clip never mixes formats.
        /// </summary>
        public static void Load(string dir, string format, int bufferSeconds, int segmentSeconds, long fpsNum, long fpsDen, long nowMs)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            CloseIndex();
            Segs.Clear(); Closing.Clear(); Pins.Clear();
            _open = null; TotalBytes = 0; _totalFrames = 0;
            Dir = dir; _bufferSeconds = bufferSeconds; _segmentSeconds = segmentSeconds; _fpsNum = fpsNum; _fpsDen = fpsDen;
            Directory.CreateDirectory(dir);

            string formatPath = Path.Combine(dir, FormatName);
            string old = null;
            try { if (System.IO.File.Exists(formatPath)) old = System.IO.File.ReadAllText(formatPath, Encoding.UTF8); } catch { }
            FileInfo[] files = new DirectoryInfo(dir).GetFiles(Prefix + "*.ts");
            _pendingFormat = null; _pendingDelete = null;
            if (old != format)
            {
                if (files.Length > 0) Log.Info("ring: format changed, {0} old segment(s) set aside until recording starts", files.Length);
                _pendingFormat = format;
                _pendingDelete = new List<string>();
                foreach (FileInfo f in files) _pendingDelete.Add(f.Name);
                files = new FileInfo[0];
            }

            Dictionary<string, Seg> known = _pendingFormat != null ? new Dictionary<string, Seg>() : ReadIndex(Path.Combine(dir, IdxName));
            List<Seg> list = new List<Seg>();
            List<FileInfo> unknown = new List<FileInfo>();
            foreach (FileInfo f in files)
            {
                Seg s;
                if (known.TryGetValue(f.Name, out s)) list.Add(s);
                else unknown.Add(f);
            }
            // Files without an index line: normally just the one that was open when a previous helper died.
            foreach (FileInfo f in unknown)
            {
                Seg s = new Seg();
                s.File = f.Name; s.Session = SessionOf(f.Name); s.StartMs = ToUnixMs(f.CreationTimeUtc); s.Truncated = true;
                s.First = f.Name.IndexOf(FirstMark, StringComparison.Ordinal) >= 0;
                if (ScanInto(s, false)) list.Add(s);
                else { try { f.Delete(); } catch { } }
            }
            list.Sort(delegate(Seg a, Seg b) { int c = a.StartMs.CompareTo(b.StartMs); return c != 0 ? c : string.CompareOrdinal(a.File, b.File); });

            long cutoff = nowMs - (long)bufferSeconds * 1000;
            Seg prev = null;
            foreach (Seg s in list)
            {
                if (EndMs(s) < cutoff) { TryDelete(s); continue; }
                if (!known.ContainsKey(s.File))
                {
                    // the crash-cut file continues its predecessor of the same session
                    if (prev != null && prev.Session == s.Session && !prev.Truncated)
                    {
                        s.Prev = prev.File; s.Cont = true; s.Av = prev.Av + prev.ADur - VDur(prev);
                    }
                    else { s.Prev = ""; s.Cont = false; s.Av = ChainStartAv(s); }
                }
                else s.Cont = prev != null && s.Prev.Length > 0 && s.Prev == prev.File && !prev.Truncated;   // Av comes from the index
                s.Seq = _nextSeq++; s.Ready = true;
                Segs.Add(s); TotalBytes += s.Bytes; _totalFrames += s.VFrames;
                prev = s;
            }
            if (_pendingFormat == null) RewriteIndex();     // otherwise the old index stays as it is until CommitFormat
            Loaded = true;
            Log.Info("ring: {0} segment(s) adopted, {1:F1} MB, {2:F1} s of footage, index built in {3} ms", Segs.Count, TotalBytes / 1048576.0,
                     BufferedSeconds(nowMs), sw.ElapsedMilliseconds);
        }

        /// <summary>The recording started: if Load set segments of another format aside, delete them now.</summary>
        public static void CommitFormat()
        {
            if (_pendingFormat == null) return;
            if (_pendingDelete.Count > 0) Log.Info("ring: format changed, deleting {0} old segment(s)", _pendingDelete.Count);
            foreach (string name in _pendingDelete)
            {
                try { System.IO.File.Delete(Path.Combine(Dir, name)); }
                catch (Exception ex) { Log.Info("ring: cannot delete {0}: {1}", name, ex.Message); }
            }
            try { System.IO.File.Delete(Path.Combine(Dir, IdxName)); } catch { }
            WriteAtomic(Path.Combine(Dir, FormatName), _pendingFormat);
            _pendingFormat = null; _pendingDelete = null;
            RewriteIndex();
        }

        /// <summary>
        /// The output of an encoder that failed to start: forget the file(s) of this session and delete them, so
        /// that they are never adopted later as segments of whatever format ring.json names.
        /// </summary>
        public static void DiscardSession()
        {
            string session = _session;
            if (session.Length == 0) return;
            _open = null;
            Closing.RemoveAll(x => x.Session == session);
            List<Seg> mine = Segs.FindAll(x => x.Session == session);
            foreach (Seg s in mine)
            {
                Segs.Remove(s);
                if (s.Ready) { TotalBytes -= s.Bytes; _totalFrames -= s.VFrames; }
                string path = Path.Combine(Dir, s.File);
                // the muxer process may need a moment to let go of the file
                for (int i = 0; i < 20 && System.IO.File.Exists(path); i++)
                {
                    try { System.IO.File.Delete(path); }
                    catch (IOException) { System.Threading.Thread.Sleep(100); }
                    catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(100); }
                }
                if (System.IO.File.Exists(path)) Log.Info("ring: cannot delete {0} of a failed start", s.File);
            }
        }

        static string SessionOf(string name)
        {
            // rcseg-<session>-<yyyymmdd>-<hhmmss>.ts
            string[] p = name.Split('-');
            return p.Length >= 2 ? p[1] : "";
        }

        static void WriteAtomic(string path, string text)
        {
            string tmp = path + ".tmp";
            System.IO.File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            System.IO.File.Move(tmp, path);
        }

        // ------------------------------------------------------------------ index file

        static string Line(Seg s)
        {
            return string.Join("|", new string[] {
                s.File, s.Session, s.StartMs.ToString(CultureInfo.InvariantCulture), s.Bytes.ToString(CultureInfo.InvariantCulture),
                s.Vp.ToString(CultureInfo.InvariantCulture), s.Vd.ToString(CultureInfo.InvariantCulture), s.VLastDts.ToString(CultureInfo.InvariantCulture),
                s.VFrames.ToString(CultureInfo.InvariantCulture), s.Ap.ToString(CultureInfo.InvariantCulture), s.AEnd.ToString(CultureInfo.InvariantCulture),
                s.Av.ToString(CultureInfo.InvariantCulture), s.Truncated ? "1" : "0", s.Prev });
        }

        static Dictionary<string, Seg> ReadIndex(string path)
        {
            Dictionary<string, Seg> d = new Dictionary<string, Seg>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!System.IO.File.Exists(path)) return d;
                foreach (string line in System.IO.File.ReadAllLines(path, Encoding.UTF8))
                {
                    string[] f = line.Split('|');
                    if (f.Length < 13) continue;
                    try
                    {
                        Seg s = new Seg();
                        s.File = f[0]; s.Session = f[1];
                        s.StartMs = long.Parse(f[2], CultureInfo.InvariantCulture); s.Bytes = long.Parse(f[3], CultureInfo.InvariantCulture);
                        s.Vp = long.Parse(f[4], CultureInfo.InvariantCulture); s.Vd = long.Parse(f[5], CultureInfo.InvariantCulture);
                        s.VLastDts = long.Parse(f[6], CultureInfo.InvariantCulture); s.VFrames = int.Parse(f[7], CultureInfo.InvariantCulture);
                        s.Ap = long.Parse(f[8], CultureInfo.InvariantCulture); s.AEnd = long.Parse(f[9], CultureInfo.InvariantCulture);
                        s.Av = long.Parse(f[10], CultureInfo.InvariantCulture); s.Truncated = f[11] == "1"; s.Prev = f[12];
                        s.First = s.File.IndexOf(FirstMark, StringComparison.Ordinal) >= 0;
                        d[s.File] = s;
                    }
                    catch (FormatException) { }
                }
            }
            catch (IOException) { }
            return d;
        }

        static void RewriteIndex()
        {
            CloseIndex();
            string path = Path.Combine(Dir, IdxName);
            StringBuilder sb = new StringBuilder(Segs.Count * 110);
            int n = 0;
            foreach (Seg s in Segs)
                if (s.Ready && !s.Open) { sb.Append(Line(s)).Append('\n'); n++; }
            WriteAtomic(path, sb.ToString());
            _idx = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false));
            _idx.NewLine = "\n";
            _idxLines = n;
        }

        static void AppendIndex(Seg s)
        {
            if (_idx == null) return;
            try
            {
                _idx.WriteLine(Line(s));
                _idx.Flush();
                _idxLines++;
                // pruned files leave dead lines behind; rewrite now and then
                if (_idxLines > Segs.Count * 2 + 2000) RewriteIndex();
            }
            catch (IOException ex) { Log.Info("ring: index write failed: {0}", ex.Message); }
        }

        public static void CloseIndex()
        {
            if (_idx != null) { try { _idx.Dispose(); } catch { } _idx = null; }
        }

        // ------------------------------------------------------------------ recording session

        static string Stamp(DateTime local)
        {
            return local.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        }

        /// <summary>Starts a session; returns the full path of its first file.</summary>
        public static string BeginSession(long nowMs, out string nameFormat)
        {
            _session = Guid.NewGuid().ToString("N").Substring(0, 8);
            _sessionBroken = false;
            // later files are named by obs-ffmpeg-mux from this pattern (strftime-like, local time)
            nameFormat = Prefix + _session + "-%CCYY%MM%DD-%hh%mm%ss";
            string first = Prefix + _session + FirstMark + Stamp(DateTime.Now) + ".ts";
            Seg s = new Seg();
            s.Seq = _nextSeq++; s.File = first; s.Session = _session; s.StartMs = nowMs; s.Open = true; s.First = true;
            Segs.Add(s); _open = s;
            return Path.Combine(Dir, first);
        }

        /// <summary>The muxer switched to a new file (file_changed signal).</summary>
        public static void OnFileChanged(string nextPath, long nowMs)
        {
            Seg old = _open;
            Seg s = new Seg();
            s.Seq = _nextSeq++; s.File = Path.GetFileName(nextPath.Replace('/', '\\')); s.Session = _session; s.StartMs = nowMs; s.Open = true;
            if (old != null)
            {
                old.Open = false; old.ClosingSinceMs = nowMs;
                Closing.Add(old);
                s.Prev = old.File;
            }
            Segs.Add(s); _open = s;
        }

        /// <summary>The output stopped. <paramref name="clean"/> = the muxer wrote its last file in full.</summary>
        public static void EndSession(bool clean, long nowMs)
        {
            if (_open == null) return;
            _open.Open = false; _open.ClosingSinceMs = nowMs; _open.Truncated = !clean;
            Closing.Add(_open);
            _open = null;
        }

        /// <summary>True once no process has the file open for writing (readers do not matter).</summary>
        static bool IsClosed(Seg s)
        {
            try
            {
                using (new FileStream(Path.Combine(Dir, s.File), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete)) return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>Fills the timing fields from the file. False when the file holds nothing usable.</summary>
        static bool ScanInto(Seg s, bool complete)
        {
            string path = Path.Combine(Dir, s.File);
            TsScan.Head h = s.Head ?? TsScan.ScanHead(path);
            if (h == null) return false;
            TsScan.Tail t = TsScan.ScanTail(path, complete, h.Vp - h.Vd, AudioFrameTicks);
            if (t == null) return false;
            s.Head = h; s.Vp = h.Vp; s.Vd = h.Vd; s.Ap = h.Ap;
            s.Bytes = t.Bytes; s.VLastDts = t.VLastDts; s.AEnd = t.AEnd;
            long span = s.VLastDts - s.Vd;
            s.VFrames = (int)((span * _fpsNum + 45000 * _fpsDen) / (90000 * _fpsDen)) + 1;
            return s.VFrames > 0 && s.AEnd >= s.Ap;
        }

        static Seg Before(Seg s)
        {
            int i = Segs.IndexOf(s);
            return i > 0 ? Segs[i - 1] : null;
        }

        static void Finish(Seg s)
        {
            Closing.Remove(s);
            bool ok = ScanInto(s, !s.Truncated);
            if (!ok)
            {
                // nothing usable (e.g. the recording died before the first frames reached the disk)
                Segs.Remove(s);
                TryDelete(s);
                _sessionBroken = true;
                return;
            }
            Seg prev = Before(s);
            s.Cont = !_sessionBroken && prev != null && prev.Ready && !prev.Truncated && prev.File == s.Prev && prev.Session == s.Session;
            s.Av = s.Cont ? prev.Av + prev.ADur - VDur(prev) : ChainStartAv(s);
            // The muxer starts every later file with the first audio frame at or after the keyframe, so a
            // continued file must land within one audio frame; anything else means the bookkeeping is off.
            if (s.Cont && (s.Av < 0 || s.Av >= AudioFrameTicks))
            {
                Log.Info("ring: {0}: audio offset {1} ticks out of range, treating as a break", s.File, s.Av);
                s.Cont = false; s.Av = ChainStartAv(s);
            }
            _sessionBroken = false;
            if (!s.Cont) s.Prev = "";
            s.Ready = true;
            TotalBytes += s.Bytes; _totalFrames += s.VFrames;
            AppendIndex(s);
        }

        /// <summary>Housekeeping from the main loop: index files the muxer has closed, then prune.</summary>
        public static void Tick(long nowMs)
        {
            if (!Loaded) return;
            for (int i = 0; i < Closing.Count; )
            {
                Seg s = Closing[i];
                if (IsClosed(s)) { Finish(s); continue; }
                if (nowMs - s.ClosingSinceMs > 5000)
                {
                    Log.Info("ring: {0} still open after 5 s; indexing what is on disk", s.File);
                    s.Truncated = true; Finish(s); continue;
                }
                i++;
            }
            Prune(nowMs);
        }

        // ------------------------------------------------------------------ prune / pins

        static void TryDelete(Seg s)
        {
            try { System.IO.File.Delete(Path.Combine(Dir, s.File)); }
            catch (Exception ex) { Log.Info("ring: cannot delete {0}: {1}", s.File, ex.Message); }
        }

        static long PinFloor(long nowMs)
        {
            long floor = long.MaxValue;
            List<long> dead = null;
            foreach (KeyValuePair<long, long[]> p in Pins)
            {
                if (p.Value[1] < nowMs) { if (dead == null) dead = new List<long>(); dead.Add(p.Key); }
                else floor = Math.Min(floor, p.Value[0]);
            }
            if (dead != null) foreach (long id in dead) Pins.Remove(id);
            return floor;
        }

        static void Prune(long nowMs)
        {
            if (Segs.Count == 0) return;
            long cutoff = nowMs - ((long)_bufferSeconds + _segmentSeconds + 2) * 1000;
            long floor = PinFloor(nowMs);
            int n = 0;
            while (n < Segs.Count)
            {
                Seg s = Segs[n];
                if (!s.Ready || s.Open || s.Seq >= floor || EndMs(s) >= cutoff) break;
                try { System.IO.File.Delete(Path.Combine(Dir, s.File)); }
                catch (Exception ex)
                {
                    if (_lastPruneError != s.File) { _lastPruneError = s.File; PruneErrors++; Log.Info("ring: prune failed for {0}: {1}", s.File, ex.Message); }
                    break;
                }
                TotalBytes -= s.Bytes; _totalFrames -= s.VFrames;
                n++;
            }
            if (n > 0) Segs.RemoveRange(0, n);
        }

        public static bool Release(long saveId) { return Pins.Remove(saveId); }

        public static bool Hold(long saveId, long nowMs)
        {
            long[] p;
            if (!Pins.TryGetValue(saveId, out p)) return false;
            p[1] = nowMs + 300000;
            return true;
        }

        // ------------------------------------------------------------------ save

        /// <summary>
        /// The segments that cover the last <paramref name="seconds"/> of wall-clock time (0 = everything),
        /// including the usable part of the file still being written, with the shift to apply to each
        /// file's video and audio timestamps so the joined stream has the timeline of one continuous
        /// recording. Pins the files until released.
        /// </summary>
        public static List<SaveEntry> BuildSave(int seconds, long nowMs, out long saveId)
        {
            saveId = 0;
            // a file the muxer left a moment ago must be indexed before it can be listed
            long deadline = Environment.TickCount + 400;
            while (Closing.Count > 0 && Environment.TickCount < deadline)
            {
                Tick(nowMs);
                if (Closing.Count > 0) System.Threading.Thread.Sleep(5);
            }
            foreach (Seg c in Closing.ToArray()) { c.Truncated = true; Finish(c); }

            long from = seconds <= 0 ? long.MinValue : nowMs - (long)seconds * 1000;
            List<Seg> pick = new List<Seg>();
            foreach (Seg s in Segs)
            {
                if (s.Open || !s.Ready) continue;
                if (EndMs(s) > from) pick.Add(s);
            }
            Seg tail = null;
            if (_open != null)
            {
                // a view of the growing file: complete frames only
                Seg v = new Seg();
                v.File = _open.File; v.Session = _open.Session; v.Prev = _open.Prev; v.Seq = _open.Seq; v.Head = _open.Head; v.First = _open.First;
                if (ScanInto(v, false))
                {
                    _open.Head = v.Head;
                    Seg prev = pick.Count > 0 ? pick[pick.Count - 1] : null;
                    v.Cont = !_sessionBroken && prev != null && !prev.Truncated && prev.File == v.Prev && prev.Session == v.Session;
                    v.Av = v.Cont ? prev.Av + prev.ADur - VDur(prev) : ChainStartAv(v);
                    tail = v;
                    pick.Add(v);
                }
            }
            if (pick.Count == 0) return null;

            List<SaveEntry> list = new List<SaveEntry>(pick.Count);
            long tv = 0, ta = pick[0].Av, origin = pick[0].Vp;
            for (int k = 0; k < pick.Count; k++)
            {
                Seg s = pick[k];
                if (k > 0)
                {
                    Seg p = pick[k - 1];
                    if (s.Cont) { tv += VDur(p); ta += p.ADur; }
                    else
                    {
                        // A break (helper restart, missing file): the next file starts where neither stream
                        // would overlap what came before. Its audio may begin before its video (the priming
                        // packet of a new session), hence the second term.
                        long start = Math.Max(tv + VDur(p), ta + p.ADur - s.Av);
                        tv = start; ta = start + s.Av;
                    }
                }
                SaveEntry e = new SaveEntry();
                e.File = s.File; e.Bytes = s.Bytes; e.Ticks = VDur(s);
                e.V = origin + tv - s.Vp;
                e.A = origin + ta - s.Ap;
                list.Add(e);
            }
            saveId = _nextSaveId++;
            Pins[saveId] = new long[] { pick[0].Seq, nowMs + 300000 };
            return list;
        }

        public static long ToUnixMs(DateTime utc)
        {
            return (long)(utc - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
        }
    }
}
