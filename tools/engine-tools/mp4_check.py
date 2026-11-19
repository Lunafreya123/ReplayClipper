# Structure check of an MP4 written by the client (no ffmpeg involved).
#
#   python -I mp4_check.py <clip.mp4>                 dump the header and check it against the file itself
#   python -I mp4_check.py <clip.mp4> <other.mp4>     also compare the two headers field by field
#
# Checks on one file: every table agrees with the others (sample counts in stts / stsz / stsc+stco / ctts /
# sbgp), every sample lies inside mdat, no two samples overlap, the samples cover mdat completely, sync
# samples are in range, durations agree (mdhd = sum of stts; tkhd = sum of the edit list; mvhd = longest
# track). Comparing two files: everything that does not depend on how the samples are grouped into chunks
# must be the same (ftyp, mvhd, tkhd, mdhd, hdlr, edit lists, sample entries, stts, ctts, stss, stsz, sample
# groups) and every sample must have the same bytes. One field may differ and is reported: the "maximum
# bitrate" hint in the sample entry (btrt / esds), which in ffmpeg's file is an estimate from probing.
import hashlib
import struct
import sys

CONTAINERS = {b"moov", b"trak", b"mdia", b"minf", b"stbl", b"edts", b"dinf", b"udta"}


def top_boxes(f, size):
    pos, out = 0, []
    while pos + 8 <= size:
        f.seek(pos)
        h = f.read(16)
        n, typ = struct.unpack(">I4s", h[:8])
        hdr = 8
        if n == 1:
            n = struct.unpack(">Q", h[8:16])[0]
            hdr = 16
        elif n == 0:
            n = size - pos
        if n < hdr:
            raise ValueError("bad box at %d" % pos)
        out.append((typ, pos, n, hdr))
        pos += n
    return out, pos


def parse(buf, start, end, path, found):
    p = start
    while p + 8 <= end:
        n, typ = struct.unpack(">I4s", buf[p:p + 8])
        hdr = 8
        if n == 1:
            n = struct.unpack(">Q", buf[p + 8:p + 16])[0]
            hdr = 16
        elif n == 0:
            n = end - p
        if n < hdr or p + n > end:
            raise ValueError("bad box %r in %s" % (typ, path))
        name = path + "/" + typ.decode("latin1")
        if typ in CONTAINERS:
            found.append((name, None))
            parse(buf, p + hdr, p + n, name, found)
        else:
            found.append((name, buf[p + hdr:p + n]))
        p += n


def u32(b, i):
    return struct.unpack(">I", b[i:i + 4])[0]


def u64(b, i):
    return struct.unpack(">Q", b[i:i + 8])[0]


def s32(b, i):
    return struct.unpack(">i", b[i:i + 4])[0]


def s64(b, i):
    return struct.unpack(">q", b[i:i + 8])[0]


class Track:
    pass


def load(path):
    import os
    size = os.path.getsize(path)
    f = open(path, "rb")
    tops, covered = top_boxes(f, size)
    info = {"path": path, "size": size, "top": [(t.decode("latin1"), p, n) for t, p, n, h in tops], "covered": covered, "f": f}
    moov = [x for x in tops if x[0] == b"moov"]
    mdat = [x for x in tops if x[0] == b"mdat"]
    ftyp = [x for x in tops if x[0] == b"ftyp"]
    if len(moov) != 1 or len(mdat) != 1 or len(ftyp) != 1:
        raise ValueError("expected one ftyp, one moov and one mdat")
    f.seek(ftyp[0][1])
    info["ftyp"] = f.read(ftyp[0][2])
    f.seek(moov[0][1] + moov[0][3])
    buf = f.read(moov[0][2] - moov[0][3])
    found = []
    parse(buf, 0, len(buf), "moov", found)
    info["mdat"] = (mdat[0][1] + mdat[0][3], mdat[0][1] + mdat[0][2])
    info["moov_first"] = moov[0][1] < mdat[0][1]
    info["moov_size"] = moov[0][2]
    tracks, cur = [], None
    for name, data in found:
        if name == "moov/mvhd":
            v = data[0]
            info["mvhd_raw"] = data
            info["movie_scale"], info["movie_dur"] = (u32(data, 20), u64(data, 24)) if v == 1 else (u32(data, 12), u32(data, 16))
        elif name == "moov/trak":
            cur = Track()
            cur.boxes = {}
            cur.other = []
            tracks.append(cur)
        elif name.startswith("moov/trak/") and data is not None:
            cur.boxes[name[len("moov/trak/"):]] = data
        elif data is not None:
            info.setdefault("moov_other", []).append((name, data))
    for t in tracks:
        b = t.boxes
        d = b["tkhd"]
        t.tkhd_dur = u64(d, 28) if d[0] == 1 else u32(d, 20)
        d = b["mdia/mdhd"]
        t.scale, t.mdhd_dur = (u32(d, 20), u64(d, 24)) if d[0] == 1 else (u32(d, 12), u32(d, 16))
        t.kind = b["mdia/hdlr"][8:12].decode("latin1")
        st = "mdia/minf/stbl/"
        t.entry = b[st + "stsd"][12:16].decode("latin1")
        d = b[st + "stts"]
        t.stts = [(u32(d, 8 + 8 * i), u32(d, 12 + 8 * i)) for i in range(u32(d, 4))]
        d = b.get(st + "ctts")
        t.ctts = [(u32(d, 8 + 8 * i), s32(d, 12 + 8 * i)) for i in range(u32(d, 4))] if d else None
        d = b.get(st + "stss")
        t.stss = [u32(d, 8 + 4 * i) for i in range(u32(d, 4))] if d else None
        d = b[st + "stsz"]
        if u32(d, 4) != 0:
            t.sizes = [u32(d, 4)] * u32(d, 8)
        else:
            t.sizes = list(struct.unpack(">%dI" % u32(d, 8), d[12:12 + 4 * u32(d, 8)]))
        d = b[st + "stsc"]
        t.stsc = [(u32(d, 8 + 12 * i), u32(d, 12 + 12 * i), u32(d, 16 + 12 * i)) for i in range(u32(d, 4))]
        if st + "co64" in b:
            d = b[st + "co64"]
            t.chunks = list(struct.unpack(">%dQ" % u32(d, 4), d[8:8 + 8 * u32(d, 4)]))
            t.co = "co64"
        else:
            d = b[st + "stco"]
            t.chunks = list(struct.unpack(">%dI" % u32(d, 4), d[8:8 + 4 * u32(d, 4)]))
            t.co = "stco"
        d = b.get("edts/elst")
        t.elst = []
        if d:
            for i in range(u32(d, 4)):
                if d[0] == 1:
                    t.elst.append((u64(d, 8 + 20 * i), s64(d, 16 + 20 * i), u32(d, 24 + 20 * i)))
                else:
                    t.elst.append((u32(d, 8 + 12 * i), s32(d, 12 + 12 * i), u32(d, 16 + 12 * i)))
        d = b.get(st + "sbgp")
        t.sbgp = None
        if d:
            at = 12 if d[0] == 1 else 8
            t.sbgp = [(u32(d, at + 4 + 8 * i), u32(d, at + 8 + 8 * i)) for i in range(u32(d, at))]
        # sample offsets
        t.offsets = []
        si = 0
        for ci, off in enumerate(t.chunks):
            per = 0
            for first, n, _ in t.stsc:
                if first <= ci + 1:
                    per = n
            for _ in range(per):
                if si >= len(t.sizes):
                    break
                t.offsets.append(off)
                off += t.sizes[si]
                si += 1
        t.placed = si
    info["tracks"] = tracks
    return info


def check(info):
    ok = True

    def bad(msg):
        nonlocal ok
        ok = False
        print("   PROBLEM: " + msg)

    print("== %s" % info["path"])
    print("   boxes: %s" % " ".join("%s(%d)" % (t, n) for t, p, n in info["top"]))
    if info["covered"] != info["size"]:
        bad("top-level boxes cover %d of %d bytes" % (info["covered"], info["size"]))
    print("   index %s the footage; moov %d bytes; movie timescale %d, duration %d (%.6f s)" % (
        "before" if info["moov_first"] else "AFTER", info["moov_size"], info["movie_scale"], info["movie_dur"], info["movie_dur"] / info["movie_scale"]))
    lo, hi = info["mdat"]
    spans = []
    longest = 0
    for t in info["tracks"]:
        n = len(t.sizes)
        total = sum(c * d for c, d in t.stts)
        print("   %s %s: %d samples, %d chunks (%s, %d stsc runs), timescale %d, mdhd %d, tkhd %d, elst %s" % (
            t.kind, t.entry, n, len(t.chunks), t.co, len(t.stsc), t.scale, t.mdhd_dur, t.tkhd_dur, t.elst))
        print("      stts %s%s; ctts %s; sync %s; sbgp %s" % (
            t.stts[:4], " ..." if len(t.stts) > 4 else "",
            (str(t.ctts[:6]) + (" ..." if len(t.ctts) > 6 else "")) if t.ctts else "none",
            ("%d (first %s)" % (len(t.stss), t.stss[:3])) if t.stss is not None else "none", t.sbgp))
        if sum(c for c, d in t.stts) != n:
            bad("%s: stts covers %d samples, stsz has %d" % (t.kind, sum(c for c, d in t.stts), n))
        if t.ctts is not None and sum(c for c, d in t.ctts) != n:
            bad("%s: ctts covers %d samples, stsz has %d" % (t.kind, sum(c for c, d in t.ctts), n))
        if t.placed != n or len(t.offsets) != n:
            bad("%s: chunks place %d samples, stsz has %d" % (t.kind, t.placed, n))
        if t.sbgp is not None and sum(c for c, g in t.sbgp) != n:
            bad("%s: sbgp covers %d samples, stsz has %d" % (t.kind, sum(c for c, g in t.sbgp), n))
        if t.stss is not None and (not t.stss or t.stss[0] != 1 or t.stss[-1] > n or any(b <= a for a, b in zip(t.stss, t.stss[1:]))):
            bad("%s: sync sample table is not 1.., increasing, within range" % t.kind)
        if total != t.mdhd_dur:
            bad("%s: mdhd duration %d, sum of stts %d" % (t.kind, t.mdhd_dur, total))
        if any(d <= 0 for c, d in t.stts):
            bad("%s: a sample duration is not positive" % t.kind)
        # ffmpeg rounds the track's end and the edits separately: they may differ by one unit
        if t.elst and abs(sum(e[0] for e in t.elst) - t.tkhd_dur) > 1:
            bad("%s: tkhd duration %d, edit list adds up to %d" % (t.kind, t.tkhd_dur, sum(e[0] for e in t.elst)))
        longest = max(longest, t.tkhd_dur)
        for off, sz in zip(t.offsets, t.sizes):
            spans.append((off, off + sz))
    if longest != info["movie_dur"]:
        bad("mvhd duration %d, longest track %d" % (info["movie_dur"], longest))
    spans.sort()
    gaps = overlaps = outside = 0
    pos = lo
    for a, b in spans:
        if a < lo or b > hi:
            outside += 1
        if a > pos:
            gaps += 1
        elif a < pos:
            overlaps += 1
        pos = max(pos, b)
    if pos != hi:
        gaps += 1
    print("   samples in mdat [%d, %d): %d outside, %d overlapping, %d gap(s) -> %s" % (
        lo, hi, outside, overlaps, gaps, "every byte of mdat belongs to exactly one sample" if not (outside or overlaps or gaps) else "see above"))
    if outside or overlaps:
        bad("samples outside mdat or overlapping")
    info["gaps"] = gaps
    return ok


def sample_hashes(info, t):
    f = info["f"]
    out = []
    for off, sz in zip(t.offsets, t.sizes):
        f.seek(off)
        out.append(hashlib.md5(f.read(sz)).digest())
    return out


def bitrate_fields(stsd, kind):
    """Offsets of (maximum, average) bitrate pairs in a sample entry: btrt, and esds as ffmpeg writes it."""
    out = []
    p, end = 8 + (86 if kind == "vide" else 36), 8 + u32(stsd, 8)
    while p + 8 <= end:
        n, typ = u32(stsd, p), stsd[p + 4:p + 8]
        if n < 8:
            break
        if typ == b"btrt" and n == 20:
            out.append(p + 12)
        elif typ == b"esds":
            q = p + 12
            if stsd[q] == 3:
                q += 1
                while stsd[q] & 0x80:
                    q += 1
                q += 1 + 3
                if stsd[q] == 4:
                    q += 1
                    while stsd[q] & 0x80:
                        q += 1
                    out.append(q + 1 + 5)
        p += n
    return out


def mask_max(stsd, kind):
    b = bytearray(stsd)
    for at in bitrate_fields(stsd, kind):
        b[at:at + 4] = bytes(4)
    return bytes(b)


def max_rates(stsd, kind):
    return "/".join(str(u32(stsd, at)) for at in bitrate_fields(stsd, kind))


def compare(a, b):
    ok = True

    def diff(msg):
        nonlocal ok
        ok = False
        print("   DIFFERENT: " + msg)

    print("== comparing the two headers")
    if a["ftyp"] != b["ftyp"]:
        diff("ftyp")
    if a["mvhd_raw"] != b["mvhd_raw"]:
        diff("mvhd (durations %d vs %d, timescales %d vs %d)" % (a["movie_dur"], b["movie_dur"], a["movie_scale"], b["movie_scale"]))
    if a.get("moov_other") != b.get("moov_other"):
        diff("other moov boxes: %s vs %s" % ([n for n, d in a.get("moov_other", [])], [n for n, d in b.get("moov_other", [])]))
    if len(a["tracks"]) != len(b["tracks"]):
        diff("track count")
        return False
    same = 0
    for ta, tb in zip(a["tracks"], b["tracks"]):
        names = sorted(set(ta.boxes) | set(tb.boxes))
        layout = ("mdia/minf/stbl/stsc", "mdia/minf/stbl/stco", "mdia/minf/stbl/co64")
        for n in names:
            if n in layout:
                continue
            xa, xb = ta.boxes.get(n), tb.boxes.get(n)
            if n.endswith("stsd") and xa is not None and xb is not None and xa != xb and mask_max(xa, ta.kind) == mask_max(xb, tb.kind):
                print("   %s track: sample entry identical except the 'maximum bitrate' hint (%s vs %s)" % (
                    ta.kind, max_rates(xa, ta.kind), max_rates(xb, tb.kind)))
                same += 1
                continue
            if xa != xb:
                extra = ""
                if n == "tkhd":
                    extra = " (durations %d vs %d)" % (ta.tkhd_dur, tb.tkhd_dur)
                if n == "mdia/mdhd":
                    extra = " (durations %d vs %d)" % (ta.mdhd_dur, tb.mdhd_dur)
                if n == "edts/elst":
                    extra = " (%s vs %s)" % (ta.elst, tb.elst)
                if n.endswith("stts"):
                    extra = " (%s vs %s)" % (ta.stts[-3:], tb.stts[-3:])
                diff("%s track: %s%s" % (ta.kind, n, extra))
            else:
                same += 1
        ha, hb = sample_hashes(a, ta), sample_hashes(b, tb)
        if ha != hb:
            n = sum(1 for x, y in zip(ha, hb) if x != y)
            diff("%s track: %d of %d samples differ in content (%d vs %d samples)" % (ta.kind, n, min(len(ha), len(hb)), len(ha), len(hb)))
        else:
            print("   %s track: all %d samples have identical bytes" % (ta.kind, len(ha)))
    print("   %d header boxes identical byte for byte (chunk layout boxes stsc/stco are expected to differ)" % same)
    return ok


def main():
    a = load(sys.argv[1])
    ok = check(a)
    if len(sys.argv) > 2:
        b = load(sys.argv[2])
        ok = check(b) and ok
        ok = compare(a, b) and ok
    print("RESULT: %s" % ("OK" if ok else "PROBLEMS"))
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
