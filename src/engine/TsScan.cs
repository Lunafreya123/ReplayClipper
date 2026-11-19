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
using System.IO;

namespace ReplayClipperEngine
{
    /// <summary>
    /// Reads just enough of an MPEG-TS segment (as written by obs-ffmpeg-mux) to know where its video
    /// and audio begin and end on the file's own clock. That is what makes an exact join possible:
    /// every segment restarts its timestamps, so the join needs each file's true stream lengths.
    ///
    /// Facts relied on (FFmpeg mpegts muxer): one PES per video frame; the TS packets of one PES are
    /// not interleaved with payload of another stream; AAC is carried as ADTS, several frames per PES.
    /// All times are 90 kHz ticks.
    /// </summary>
    static class TsScan
    {
        public const int Packet = 188;

        public sealed class Head
        {
            public long Vp;   // PTS of the first video frame (the keyframe)
            public long Vd;   // DTS of the first video frame
            public long Ap;   // PTS of the first audio frame
        }

        public sealed class Tail
        {
            public long Bytes;      // usable length: a multiple of 188, ends on a PES boundary
            public long VLastDts;   // DTS of the last usable video frame (decode order)
            public long AEnd;       // end of the last usable audio frame
        }

        sealed class Pes
        {
            public long Off;
            public bool Video;
            public long Pts, Dts;
            public long AEnd = -1;  // audio only, known once the PES is complete
        }

        static long ReadTs(byte[] b, int i)
        {
            // 33 bits spread over 5 bytes: 3 | 8 | 7 | 8 | 7, each group followed by a marker bit
            ulong v = (ulong)(uint)((b[i] >> 1) & 0x07) << 30;
            v |= (ulong)b[i + 1] << 22;
            v |= (ulong)(uint)(b[i + 2] >> 1) << 15;
            v |= (ulong)b[i + 3] << 7;
            v |= (ulong)(uint)(b[i + 4] >> 1);
            return (long)v;
        }

        /// <summary>Index of the first payload byte of the packet at <paramref name="o"/>, or -1.</summary>
        static int PayloadOffset(byte[] b, int o)
        {
            int afc = (b[o + 3] >> 4) & 3;
            if ((afc & 1) == 0) return -1;
            int p = o + 4;
            if ((afc & 2) != 0) p += 1 + b[o + 4];
            return p < o + Packet ? p : -1;
        }

        static bool PesStart(byte[] b, int o, int p, out bool video, out long pts, out long dts, out int dataOff)
        {
            video = false; pts = -1; dts = -1; dataOff = p;
            if ((b[o + 1] & 0x40) == 0) return false;             // payload_unit_start_indicator
            if (p + 9 > o + Packet) return false;
            if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) return false;
            int sid = b[p + 3];
            if (sid < 0xC0 || sid > 0xEF) return false;           // 0xC0-0xDF audio, 0xE0-0xEF video
            video = sid >= 0xE0;
            int flags = b[p + 7] >> 6;
            int headerLen = b[p + 8];
            if ((flags & 2) == 0 || p + 14 > o + Packet) return false;
            pts = ReadTs(b, p + 9);
            dts = (flags == 3 && p + 19 <= o + Packet) ? ReadTs(b, p + 14) : pts;
            dataOff = p + 9 + headerLen;
            return true;
        }

        static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }

        static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        }

        /// <summary>First video and audio timestamps of a segment, or null when they are not on disk yet.</summary>
        public static Head ScanHead(string path)
        {
            try
            {
                using (FileStream fs = OpenShared(path))
                {
                    byte[] buf = new byte[Packet * 4096];
                    long vp = -1, vd = -1, ap = -1;
                    long done = 0;
                    while (done < 64L * 1024 * 1024)
                    {
                        int n = ReadFull(fs, buf, buf.Length) / Packet * Packet;
                        if (n == 0) break;
                        for (int o = 0; o < n; o += Packet)
                        {
                            if (buf[o] != 0x47) return null;
                            int p = PayloadOffset(buf, o);
                            if (p < 0) continue;
                            bool video; long pts, dts; int dataOff;
                            if (!PesStart(buf, o, p, out video, out pts, out dts, out dataOff)) continue;
                            if (video && vp < 0) { vp = pts; vd = dts; }
                            else if (!video && ap < 0) ap = pts;
                            if (vp >= 0 && ap >= 0)
                            {
                                Head h = new Head();
                                h.Vp = vp; h.Vd = vd; h.Ap = ap;
                                return h;
                            }
                        }
                        done += n;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        static int CountAdts(byte[] b, int len)
        {
            int i = 0, n = 0;
            while (i + 7 <= len)
            {
                if (b[i] != 0xFF || (b[i + 1] & 0xF0) != 0xF0) break;
                int frameLen = ((b[i + 3] & 0x03) << 11) | (b[i + 4] << 3) | (b[i + 5] >> 5);
                if (frameLen < 7 || i + frameLen > len) break;
                i += frameLen;
                n++;
            }
            return n;
        }

        /// <summary>
        /// End of a segment. <paramref name="complete"/> = the muxer has closed the file: everything in it
        /// counts. Otherwise (file still being written, or cut off by a crash) the result stops at the last
        /// point where the video is whole in display order: with B-frames a frame can be on disk before the
        /// frames shown ahead of it, and ending there would leave a hole. <paramref name="reorder"/> is
        /// PTS - DTS of the segment's first frame; a prefix is whole exactly when
        /// max(PTS so far) - DTS(last frame) equals it.
        /// </summary>
        public static Tail ScanTail(string path, bool complete, long reorder, long audioFrameTicks)
        {
            try
            {
                using (FileStream fs = OpenShared(path))
                {
                    long len = fs.Length / Packet * Packet;
                    if (len == 0) return null;
                    long[] windows = { 2L << 20, 16L << 20, 256L << 20 };
                    foreach (long w in windows)
                    {
                        long start = Math.Max(0, len - w) / Packet * Packet;
                        Tail t = ScanWindow(fs, start, len, complete, reorder, audioFrameTicks);
                        if (t != null || start == 0) return t;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        static Tail ScanWindow(FileStream fs, long start, long len, bool complete, long reorder, long audioFrameTicks)
        {
            int size = (int)(len - start);
            byte[] buf = new byte[size];
            fs.Position = start;
            if (ReadFull(fs, buf, size) != size) return null;

            List<Pes> all = new List<Pes>();
            List<Pes> vids = new List<Pes>();
            Pes curAudio = null;
            int audioPid = -1;
            byte[] apay = new byte[8192];
            int alen = 0;

            for (int o = 0; o < size; o += Packet)
            {
                if (buf[o] != 0x47) return null;
                int p = PayloadOffset(buf, o);
                if (p < 0) continue;
                int pid = ((buf[o + 1] & 0x1F) << 8) | buf[o + 2];
                bool video; long pts, dts; int dataOff;
                if (PesStart(buf, o, p, out video, out pts, out dts, out dataOff))
                {
                    // PES packets are contiguous, so any new PES start ends the audio PES in progress
                    if (curAudio != null) { curAudio.AEnd = curAudio.Pts + CountAdts(apay, alen) * audioFrameTicks; curAudio = null; }
                    Pes e = new Pes();
                    e.Off = start + o; e.Video = video; e.Pts = pts; e.Dts = dts;
                    all.Add(e);
                    if (video) vids.Add(e);
                    else
                    {
                        curAudio = e; audioPid = pid; alen = 0;
                        Append(ref apay, ref alen, buf, dataOff, o + Packet);
                    }
                }
                else if (curAudio != null && pid == audioPid)
                {
                    Append(ref apay, ref alen, buf, p, o + Packet);
                }
            }
            if (complete && curAudio != null) curAudio.AEnd = curAudio.Pts + CountAdts(apay, alen) * audioFrameTicks;

            if (vids.Count == 0) return null;
            Tail t = new Tail();
            if (complete)
            {
                long aend = LastAudioEnd(all, long.MaxValue);
                if (aend < 0) return null;
                t.Bytes = len; t.VLastDts = vids[vids.Count - 1].Dts; t.AEnd = aend;
                return t;
            }

            long max = long.MinValue;
            long[] prefixMax = new long[vids.Count];
            for (int i = 0; i < vids.Count; i++) { max = Math.Max(max, vids[i].Pts); prefixMax[i] = max; }
            for (int m = vids.Count - 2; m >= 0; m--)
            {
                if (start != 0 && m < 8) break;                    // too close to the window edge to trust max(PTS)
                if (prefixMax[m] - vids[m].Dts != reorder) continue;
                long cut = vids[m + 1].Off;                       // frame m and all audio before the cut are complete
                long aend = LastAudioEnd(all, cut);
                if (aend < 0) return null;
                t.Bytes = cut; t.VLastDts = vids[m].Dts; t.AEnd = aend;
                return t;
            }
            return null;
        }

        static long LastAudioEnd(List<Pes> all, long before)
        {
            for (int i = all.Count - 1; i >= 0; i--)
                if (!all[i].Video && all[i].Off < before && all[i].AEnd >= 0) return all[i].AEnd;
            return -1;
        }

        static void Append(ref byte[] dst, ref int len, byte[] src, int from, int to)
        {
            int n = to - from;
            if (n <= 0) return;
            if (len + n > dst.Length) Array.Resize(ref dst, Math.Max(dst.Length * 2, len + n));
            Buffer.BlockCopy(src, from, dst, len, n);
            len += n;
        }
    }
}
