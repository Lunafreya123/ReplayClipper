# Proof that a saved clip has the timeline of a continuous recording.
#
#   python -I exact_check.py <ffmpeg.exe> <clip.mp4> <reference.ts> [<reference2.ts> ...]
#
# Each reference is ONE continuous file written by a second output on the same encoders while the
# ring was recording (the helper's --debug "debugReference"). A clip that spans a helper restart is
# checked against the references of both sessions, in order.
#
# For video and audio separately:
#   - every packet of the clip must be a packet of the reference, in order, none missing, none twice
#     (compared by MD5 of the payload after remuxing the reference with the same ffmpeg);
#   - clip timestamp minus reference timestamp must be the same for every packet of a session
#     (no stretch at segment boundaries);
#   - every packet must carry the same keyframe flag as in the reference;
# and across streams: that constant must be the same for video and audio (A/V offset unchanged).
# Also reports the clip's own cadence (distinct steps between consecutive DTS).
import os
import subprocess
import sys
import tempfile
from collections import Counter
from fractions import Fraction


def run(cmd):
    r = subprocess.run(cmd, capture_output=True, text=True)
    if r.returncode != 0:
        print("command failed:", " ".join(cmd), "\n", r.stderr[-2000:])
        sys.exit(2)
    return r.stdout


def packets(path, stream):
    tb = run(["ffprobe", "-v", "error", "-select_streams", stream, "-show_entries", "stream=time_base", "-of", "csv=p=0", path]).strip()
    num, den = tb.split("/")
    tbf = Fraction(int(num), int(den))
    out = run(["ffprobe", "-v", "error", "-select_streams", stream, "-show_packets", "-show_data_hash", "md5",
               "-show_entries", "packet=pts,dts,duration,size,flags,data_hash", "-of", "csv=p=0", path])
    rows = []
    for line in out.splitlines():
        f = line.strip().split(",")
        if len(f) < 6 or f[0] == "N/A":
            continue
        rows.append((Fraction(int(f[0])) * tbf, Fraction(int(f[1])) * tbf, f[5], int(f[3]), "K" in f[4]))
    return rows   # (pts, dts, hash, size, key) in file (decode) order


def index_of(ref):
    idx = {}
    for i, r in enumerate(ref):
        idx.setdefault(r[2], []).append(i)
    return idx


def best_start(ref, ref_idx, clip, pos):
    """Index in ref where clip[pos:] begins, scored over the whole overlap (static content repeats)."""
    best, best_run = -1, 0
    for i in ref_idx.get(clip[pos][2], ()):
        n = 0
        while pos + n < len(clip) and i + n < len(ref) and clip[pos + n][2] == ref[i + n][2]:
            n += 1
        if n > best_run:
            best, best_run = i, n
        if pos + n == len(clip):
            break
    return best, best_run


def check_stream(clip, refs, label):
    print("== %s: clip has %d packets" % (label, len(clip)))
    pos = 0
    parts = []
    ok = True
    unverified = 0
    for ri, ref in enumerate(refs):
        if pos >= len(clip):
            break
        ref_idx = index_of(ref)
        start, n = best_start(ref, ref_idx, clip, pos)
        if n < 30 and ri > 0:
            # A helper that was killed took its reference output down with it; the ring can hold a few
            # packets the cut-off reference never got. Find where this (next) reference begins.
            for skip in range(1, min(1500, len(clip) - pos)):
                s2, n2 = best_start(ref, ref_idx, clip, pos + skip)
                if n2 >= 30:
                    print("   %d clip packet(s) before reference %d begins are in no reference (the previous reference was cut by the kill)" % (skip, ri + 1))
                    unverified += skip
                    pos += skip
                    start, n = s2, n2
                    break
        if start < 0 or n == 0:
            print("   reference %d: clip packet %d not found in it" % (ri + 1, pos))
            continue
        deltas = Counter(clip[pos + k][0] - ref[start + k][0] for k in range(n))
        ddeltas = Counter(clip[pos + k][1] - ref[start + k][1] for k in range(n))
        const = len(deltas) == 1 and len(ddeltas) == 1
        d = next(iter(deltas))
        print("   reference %d: clip packets %d..%d = reference packets %d..%d of %d (%d packets, identical payloads)"
              % (ri + 1, pos, pos + n - 1, start, start + n - 1, len(ref), n))
        print("      clip pts - reference pts: %s  -> %s" % (
            "%.6f s for every packet" % float(d) if const else "NOT constant: %s" % [(float(k), v) for k, v in deltas.most_common(4)],
            "exact" if const else "STRETCHED"))
        ok = ok and const
        keys = sum(1 for k in range(n) if clip[pos + k][4])
        keydiff = sum(1 for k in range(n) if clip[pos + k][4] != ref[start + k][4])
        print("      keyframe flags: %d keyframes in the clip, %d packet(s) flagged differently from the reference" % (keys, keydiff))
        ok = ok and keydiff == 0
        parts.append((pos, n, start, d if const else None))
        pos += n
    if pos != len(clip):
        print("   %d clip packet(s) from index %d match no reference -> MISSING/EXTRA/ALTERED data" % (len(clip) - pos, pos))
        ok = False
    # cadence of the clip itself
    dts = [p[1] for p in clip]
    steps = Counter(dts[i + 1] - dts[i] for i in range(len(dts) - 1))
    print("   clip DTS steps: %s" % ", ".join("%.6f s x%d" % (float(k), v) for k, v in steps.most_common(4)))
    print("   clip first pts %.6f s, first dts %.6f s" % (float(min(p[0] for p in clip)), float(dts[0])))
    for k in range(1, len(parts)):
        j = parts[k][0]
        print("   join before session %d: DTS step %.6f s" % (k + 1, float(dts[j] - dts[j - 1])))
    return ok, parts, steps


def main():
    ffmpeg, clip_path, ref_paths = sys.argv[1], sys.argv[2], sys.argv[3:]
    codec = run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", clip_path]).strip()
    tmp = tempfile.mkdtemp(prefix="exactcheck-")
    ref_mp4 = []
    try:
        for i, r in enumerate(ref_paths):
            out = os.path.join(tmp, "ref%d.mp4" % i)
            cmd = [ffmpeg, "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", r, "-map", "0:v:0", "-map", "0:a:0", "-c", "copy"]
            if codec == "hevc":
                cmd += ["-tag:v", "hvc1"]
            run(cmd + ["-f", "mp4", out])
            ref_mp4.append(out)
        result = {}
        for stream, label in (("v:0", "video"), ("a:0", "audio")):
            clip = packets(clip_path, stream)
            refs = [packets(p, stream) for p in ref_mp4]
            result[label] = check_stream(clip, refs, label)
        vok, vparts, vsteps = result["video"]
        aok, aparts, asteps = result["audio"]
        print("== A/V offset per session (clip minus reference; equal numbers = unchanged)")
        avok = len(vparts) == len(aparts)
        for k in range(min(len(vparts), len(aparts))):
            dv, da = vparts[k][3], aparts[k][3]
            same = dv is not None and da is not None and dv == da
            avok = avok and same
            print("   session %d: video %s, audio %s -> %s" % (k + 1, "%.6f" % float(dv) if dv is not None else "n/a",
                                                             "%.6f" % float(da) if da is not None else "n/a",
                                                             "A/V offset unchanged" if same else "A/V OFFSET CHANGED by %.6f s" % float((dv or 0) - (da or 0))))
        sessions = len(vparts)
        v_irregular = sum(v for k, v in vsteps.items() if k != vsteps.most_common(1)[0][0])
        a_irregular = sum(v for k, v in asteps.items() if k != asteps.most_common(1)[0][0])
        print("== cadence: video %d irregular step(s), audio %d irregular step(s) (expected: %d per stream, at session joins)"
              % (v_irregular, a_irregular, sessions - 1))
        cad_ok = v_irregular <= sessions - 1 and a_irregular <= sessions - 1
        verdict = vok and aok and avok and cad_ok
        print("RESULT: %s" % ("EXACT" if verdict else "NOT EXACT"))
        sys.exit(0 if verdict else 1)
    finally:
        for p in ref_mp4:
            try:
                os.remove(p)
            except OSError:
                pass
        try:
            os.rmdir(tmp)
        except OSError:
            pass


if __name__ == "__main__":
    main()
