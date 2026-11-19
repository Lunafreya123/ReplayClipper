// Profiling tool for the save path (not part of the app). Works on a ring folder directly: the
// per-file timestamp shifts are computed from ring.idx the same way the helper does for a save.
//
//   JoinBench read    <ring> <segments>                 read the bytes, nothing else
//   JoinBench copy    <ring> <segments> <out>           read + write them unchanged (the I/O floor)
//   JoinBench patch   <ring> <segments>                 read + the client's timestamp patch
//   JoinBench tofile  <ring> <segments> <out.ts>        read + patch + write one continuous TS file
//   JoinBench pipe    <ring> <segments> <exe> <args..>  read + patch + feed to a child's stdin, wait for it
//   JoinBench sink                                      (child) read stdin to the end and discard
//   JoinBench evict   <ring> <segments>                 drop these files from the Windows file cache (for cold-cache runs)
// <segments> = how many of the newest closed segments to use (0 = all).
// The whole save (helper + client, either join) is timed with EngineHarness: run ... --join ffmpeg|direct.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

static class JoinBench
{
    public sealed class Seg { public string Path; public long Bytes, V, A, Ticks; }

    public static List<Seg> Load(string ring, int want)
    {
        List<string[]> lines = new List<string[]>();
        foreach (string line in File.ReadAllLines(Path.Combine(ring, "ring.idx")))
        {
            string[] f = line.Split('|');
            if (f.Length >= 13 && File.Exists(Path.Combine(ring, f[0]))) lines.Add(f);
        }
        lines.Sort(delegate(string[] x, string[] y) { return long.Parse(x[2]).CompareTo(long.Parse(y[2])); });
        if (want > 0 && lines.Count > want) lines.RemoveRange(0, lines.Count - want);
        List<Seg> list = new List<Seg>();
        long tv = 0, ta = 0, origin = 0;
        string[] p = null;
        foreach (string[] f in lines)
        {
            long vp = long.Parse(f[4]), ap = long.Parse(f[8]), av = long.Parse(f[10]);
            if (p == null) { ta = av; origin = vp; }
            else
            {
                long pv = long.Parse(p[7]) * 1500, pa = long.Parse(p[9]) - long.Parse(p[8]);
                if (f[12] == p[0] && p[11] == "0") { tv += pv; ta += pa; }
                else { long start = Math.Max(tv + pv, ta + pa - av); tv = start; ta = start + av; }
            }
            Seg s = new Seg();
            s.Path = Path.Combine(ring, f[0]); s.Bytes = long.Parse(f[3]); s.Ticks = long.Parse(f[7]) * 1500;
            s.V = origin + tv - vp; s.A = origin + ta - ap;
            list.Add(s);
            p = f;
        }
        return list;
    }

    // ---- the client's current patch (copied from Engine.cs TsJoin) ----
    const int Packet = 188;
    static readonly int[] Cc = new int[8192];

    static void Patch(byte[] b, int o, long v, long a)
    {
        int pid = ((b[o + 1] & 0x1F) << 8) | b[o + 2];
        int afc = (b[o + 3] >> 4) & 3;
        bool payload = (afc & 1) != 0;
        if (pid != 0x1FFF)
        {
            int c = Cc[pid];
            if (c < 0) c = b[o + 3] & 0x0F; else if (payload) c = (c + 1) & 0x0F;
            Cc[pid] = c;
            b[o + 3] = (byte)((b[o + 3] & 0xF0) | c);
        }
        int p = o + 4;
        if ((afc & 2) != 0)
        {
            int len = b[o + 4];
            if (len >= 7 && (b[o + 5] & 0x10) != 0 && v != 0)
            {
                long pcr = ((long)b[o + 6] << 25) | ((long)b[o + 7] << 17) | ((long)b[o + 8] << 9) | ((long)b[o + 9] << 1) | (long)(uint)(b[o + 10] >> 7);
                pcr = (pcr + v) & 0x1FFFFFFFFL;
                b[o + 6] = (byte)(pcr >> 25); b[o + 7] = (byte)(pcr >> 17); b[o + 8] = (byte)(pcr >> 9); b[o + 9] = (byte)(pcr >> 1);
                b[o + 10] = (byte)((b[o + 10] & 0x7F) | ((int)(pcr & 1) << 7));
            }
            p += 1 + len;
        }
        if (!payload || (b[o + 1] & 0x40) == 0 || p + 14 > o + Packet) return;
        if (b[p] != 0 || b[p + 1] != 0 || b[p + 2] != 1) return;
        int sid = b[p + 3];
        long shift = sid >= 0xE0 && sid <= 0xEF ? v : sid >= 0xC0 && sid <= 0xDF ? a : 0;
        if (shift == 0) return;
        int flags = b[p + 7] >> 6;
        if ((flags & 2) != 0) Shift(b, p + 9, shift);
        if (flags == 3 && p + 19 <= o + Packet) Shift(b, p + 14, shift);
    }

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

    static long Run(List<Seg> segs, bool patch, Stream dst)
    {
        for (int i = 0; i < Cc.Length; i++) Cc[i] = -1;
        byte[] buf = new byte[Packet * 5578];
        long total = 0;
        foreach (Seg s in segs)
        {
            long left = s.Bytes / Packet * Packet;
            using (FileStream fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                while (left > 0)
                {
                    int want = (int)Math.Min(buf.Length, left), got = 0;
                    while (got < want) { int n = fs.Read(buf, got, want - got); if (n <= 0) throw new IOException("short " + s.Path); got += n; }
                    if (patch) for (int o = 0; o < got; o += Packet) Patch(buf, o, s.V, s.A);
                    if (dst != null) dst.Write(buf, 0, got);
                    left -= got; total += got;
                }
            }
        }
        return total;
    }

    static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "";
        if (mode == "sink")
        {
            Stream stdin = Console.OpenStandardInput();
            byte[] b = new byte[1 << 20];
            long n = 0;
            for (;;) { int r = stdin.Read(b, 0, b.Length); if (r <= 0) break; n += r; }
            return 0;
        }
        List<Seg> segs = Load(args[1], int.Parse(args[2]));
        long bytes = 0, ticks = 0;
        foreach (Seg s in segs) { bytes += s.Bytes; ticks += s.Ticks; }
        Stopwatch sw = Stopwatch.StartNew();
        string what = mode;
        if (mode == "evict")
        {
            // Opening a file for unbuffered I/O makes Windows drop that file's pages from its cache: the next
            // read really comes from the disk ("cold"). Only these files are affected.
            foreach (Seg s in segs)
                using (FileStream fs = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, (FileOptions)0x20000000)) { }
        }
        else if (mode == "read") Run(segs, false, null);
        else if (mode == "patch") Run(segs, true, null);
        else if (mode == "copy" || mode == "tofile")
        {
            using (FileStream o = new FileStream(args[3], FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)) Run(segs, mode == "tofile", o);
        }
        else if (mode == "pipe")
        {
            ProcessStartInfo si = new ProcessStartInfo(args[3]);
            StringBuilder a = new StringBuilder();
            for (int i = 4; i < args.Length; i++) a.Append(args[i].IndexOf(' ') >= 0 ? "\"" + args[i] + "\"" : args[i]).Append(' ');
            si.Arguments = a.ToString();
            si.UseShellExecute = false; si.CreateNoWindow = true; si.RedirectStandardInput = true; si.RedirectStandardError = true; si.RedirectStandardOutput = true;
            StringBuilder err = new StringBuilder();
            using (Process p = Process.Start(si))
            {
                p.ErrorDataReceived += delegate(object o, DataReceivedEventArgs e) { if (e.Data != null && err.Length < 2000) lock (err) err.AppendLine(e.Data); };
                p.OutputDataReceived += delegate(object o, DataReceivedEventArgs e) { };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                Stream stdin = p.StandardInput.BaseStream;
                try { Run(segs, true, stdin); } catch (IOException ex) { Console.WriteLine("feed stopped: " + ex.Message.Trim()); }
                long fed = sw.ElapsedMilliseconds;
                try { stdin.Close(); } catch { }
                p.WaitForExit();
                what = "pipe -> " + Path.GetFileName(args[3]) + " (fed in " + fed + " ms, exit " + p.ExitCode + ")";
                if (err.Length > 0) Console.WriteLine("child stderr: " + err.ToString().Trim());
            }
        }
        else { Console.WriteLine("unknown mode"); return 2; }
        double sec = sw.Elapsed.TotalSeconds;
        Console.WriteLine(string.Format(CultureInfo.InvariantCulture, "{0,-9} {1,5} segs {2,7:F0} s footage {3,8:F1} MB: {4,7:F0} ms  = {5,6:F0} MB/s  {6,5:F2} ms/MB   [{7}]",
            mode, segs.Count, ticks / 90000.0, bytes / 1048576.0, sec * 1000, bytes / 1048576.0 / sec, sec * 1000 / (bytes / 1048576.0), what));
        return 0;
    }
}
