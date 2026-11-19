# Audio and A/V sync check on a saved clip.
#
#   python -I av_check.py <ffmpeg.exe> <clip.mp4> tones
#   python -I av_check.py <ffmpeg.exe> <clip.mp4> sync <x> <y> <w> <h>
#
# tones: the harness played three quiet 1 kHz tones through the default playback device while the
#        helper recorded desktop audio (WASAPI loopback); find them in the clip's audio track.
# sync:  the harness flashed a small on-screen patch and started a quiet click at the same moment,
#        five times. <x> <y> <w> <h> is a region inside the patch in the clip's pixel coordinates.
#        Reports, per flash, click onset minus the time of the first bright frame.
import array
import math
import re
import subprocess
import sys


def audio(ffmpeg, clip):
    # first_pts=0 lines the samples up with the clip's time zero (pads or trims the start)
    cmd = [ffmpeg, "-v", "error", "-i", clip, "-vn", "-af", "aresample=48000:first_pts=0", "-ac", "1", "-f", "f32le", "-"]
    raw = subprocess.run(cmd, capture_output=True).stdout
    a = array.array("f")
    a.frombytes(raw[: len(raw) // 4 * 4])
    return a


def rms_windows(a, win):
    out = []
    for i in range(0, len(a) - win + 1, win):
        s = 0.0
        for v in a[i:i + win]:
            s += v * v
        out.append(math.sqrt(s / win))
    return out


def db(x):
    return 20 * math.log10(x) if x > 0 else -120.0


def tones(ffmpeg, clip):
    a = audio(ffmpeg, clip)
    win = 480    # 10 ms
    r = rms_windows(a, win)
    peak = max(abs(v) for v in a) if len(a) else 0
    total = math.sqrt(sum(v * v for v in a) / max(1, len(a)))
    print("audio: %.2f s, peak %.1f dBFS, overall RMS %.1f dBFS" % (len(a) / 48000.0, db(peak), db(total)))
    floor = sorted(r)[len(r) // 4]
    thr = max(floor * 6, 10 ** (-60 / 20.0))
    bursts = []
    i = 0
    while i < len(r):
        if r[i] > thr:
            j = i
            while j < len(r) and r[j] > thr:
                j += 1
            if j - i >= 10:      # at least 100 ms
                seg = a[i * win:j * win]
                zc = sum(1 for k in range(1, len(seg)) if seg[k - 1] < 0 <= seg[k])
                level = math.sqrt(sum(v * v for v in seg) / len(seg))
                bursts.append((i * win / 48000.0, (j - i) * win / 48000.0, zc / (len(seg) / 48000.0), db(level)))
            i = j
        else:
            i += 1
    print("noise floor %.1f dBFS; %d burst(s) above it lasting 100 ms or more:" % (db(floor), len(bursts)))
    for t, d, f, lv in bursts:
        print("   at %6.2f s for %.2f s: about %4.0f Hz, %.1f dBFS RMS" % (t, d, f, lv))
    near = [b for b in bursts if 900 <= b[2] <= 1100 and 0.2 <= b[1] <= 0.45]
    print("RESULT: %d burst(s) that look like the 1 kHz / 0.3 s test tone" % len(near))


def sync(ffmpeg, clip, x, y, w, h):
    vf = "crop=%d:%d:%d:%d,signalstats,metadata=print:key=lavfi.signalstats.YAVG:file=-" % (w, h, x, y)
    out = subprocess.run([ffmpeg, "-v", "error", "-i", clip, "-an", "-vf", vf, "-f", "null", "-"], capture_output=True, text=True).stdout
    frames = []
    t = None
    for line in out.splitlines():
        m = re.search(r"pts_time:([0-9.\-]+)", line)
        if m:
            t = float(m.group(1))
            continue
        m = re.search(r"YAVG=([0-9.]+)", line)
        if m and t is not None:
            frames.append((t, float(m.group(1))))
    if not frames:
        print("no video frames read")
        return
    lo, hi = min(f[1] for f in frames), max(f[1] for f in frames)
    print("video: %d frames, patch luma between %.0f and %.0f" % (len(frames), lo, hi))
    if hi - lo < 60:
        print("RESULT: no flash found in that region")
        return
    mid = (lo + hi) / 2
    onsets = [frames[i][0] for i in range(1, len(frames)) if frames[i - 1][1] < mid <= frames[i][1]]
    a = audio(ffmpeg, clip)
    absa = [abs(v) for v in a]
    med = sorted(absa[::97])[len(absa[::97]) // 2] if absa else 0
    thr = max(med * 12, 10 ** (-52 / 20.0))
    diffs = []
    for tv in onsets:
        i0 = max(0, int((tv - 0.25) * 48000))
        i1 = min(len(a), int((tv + 0.35) * 48000))
        ta = None
        for i in range(i0, i1):
            if absa[i] > thr:
                ta = i / 48000.0
                break
        if ta is None:
            print("   flash at %7.3f s: no click within -250..+350 ms" % tv)
            continue
        diffs.append((ta - tv) * 1000)
        print("   flash first seen in the frame at %7.3f s, click onset at %7.3f s: audio - video = %+6.1f ms" % (tv, ta, (ta - tv) * 1000))
    if diffs:
        mean = sum(diffs) / len(diffs)
        print("RESULT: %d flash/click pairs, audio - video = %+.1f ms on average (min %+.1f, max %+.1f)" % (len(diffs), mean, min(diffs), max(diffs)))
        print("        A frame lasts 16.7 ms and shows what happened during it, so anything from about -17 to +35 ms means in sync")
        print("        (the patch needs one or two display refreshes to appear, the click a few ms to reach the mixer).")


if __name__ == "__main__":
    if sys.argv[3] == "tones":
        tones(sys.argv[1], sys.argv[2])
    else:
        sync(sys.argv[1], sys.argv[2], int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]), int(sys.argv[7]))
