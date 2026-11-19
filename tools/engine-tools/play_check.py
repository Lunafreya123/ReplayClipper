# Plays a saved clip in a Chromium-based browser (Microsoft Edge, headless, muted, its own throw-away
# profile, no network: name resolution is switched off and only 127.0.0.1 is reachable).
#
#   python -I play_check.py <msedge.exe> <profile dir> <clip.mp4> [<clip2.mp4> ...]
#
# A tiny local server hands the file to the page with HTTP range requests, the way a player reads a file.
# The page loads the clip in a <video> element, plays the start, seeks to 50 %, 90 %, the last seconds and
# back to 25 %, plays a second at each place, then plays into the end. It reports duration, size, decoded
# frames, where each seek landed and whether time advanced, and any media error.
import http.server
import json
import os
import socketserver
import subprocess
import sys
import threading
import urllib.parse

PAGE = r"""<!doctype html><meta charset="utf-8"><title>play check</title>
<video id="v" muted playsinline preload="auto"></video>
<script>
const q = new URLSearchParams(location.search);
const v = document.getElementById('v');
const out = {file: q.get('file'), seeks: [], canHevc: v.canPlayType('video/mp4; codecs="hvc1.1.6.L120.90"'), ua: navigator.userAgent};
const sleep = ms => new Promise(r => setTimeout(r, ms));
function once(ev, ms) {
  return new Promise((res, rej) => {
    const t = setTimeout(() => rej(new Error('timeout waiting for ' + ev)), ms);
    v.addEventListener(ev, () => { clearTimeout(t); res(); }, {once: true});
  });
}
v.addEventListener('error', () => { out.mediaError = v.error ? (v.error.code + ' ' + v.error.message) : 'error'; });
(async () => {
  try {
    v.src = '/media/' + encodeURIComponent(out.file);
    await once('loadedmetadata', 30000);
    out.duration = v.duration; out.width = v.videoWidth; out.height = v.videoHeight;
    await v.play();
    await sleep(2500);
    out.playedFromStart = v.currentTime;
    for (const f of [0.5, 0.9, 1.0, 0.25]) {
      const target = f >= 1.0 ? Math.max(0, v.duration - 4) : v.duration * f;
      v.pause();
      v.currentTime = target;
      await once('seeked', 30000);
      const landed = v.currentTime;
      await v.play();
      await sleep(1500);
      out.seeks.push({target: +target.toFixed(3), landed: +landed.toFixed(3), advanced: +(v.currentTime - landed).toFixed(3), readyState: v.readyState});
    }
    v.pause();
    v.currentTime = Math.max(0, v.duration - 1.5);
    await once('seeked', 30000);
    const ended = once('ended', 20000);
    await v.play();
    try { await ended; out.reachedEnd = true; } catch (e) { out.reachedEnd = false; }
    out.endTime = v.currentTime;
    const pq = v.getVideoPlaybackQuality ? v.getVideoPlaybackQuality() : null;
    if (pq) { out.framesShown = pq.totalVideoFrames; out.framesDropped = pq.droppedVideoFrames; out.framesCorrupted = pq.corruptedVideoFrames; }
    out.videoBytesDecoded = v.webkitVideoDecodedByteCount; out.audioBytesDecoded = v.webkitAudioDecodedByteCount;
  } catch (e) { out.exception = String(e); }
  await fetch('/result', {method: 'POST', body: JSON.stringify(out)});
})();
</script>"""


class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    files = {}
    result = None
    done = threading.Event()
    ranges = 0

    def log_message(self, *a):
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length", "0"))
        Handler.result = json.loads(self.rfile.read(n).decode("utf-8"))
        self.send_response(204)
        self.send_header("Content-Length", "0")
        self.end_headers()
        Handler.done.set()

    def do_GET(self):
        u = urllib.parse.urlparse(self.path)
        if u.path == "/play.html":
            b = PAGE.encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Content-Length", str(len(b)))
            self.end_headers()
            self.wfile.write(b)
            return
        name = urllib.parse.unquote(u.path[len("/media/"):]) if u.path.startswith("/media/") else None
        path = Handler.files.get(name)
        if not path:
            self.send_response(404)
            self.send_header("Content-Length", "0")
            self.end_headers()
            return
        size = os.path.getsize(path)
        start, end = 0, size - 1
        rng = self.headers.get("Range")
        if rng and rng.startswith("bytes="):
            a, _, b = rng[6:].partition("-")
            start = int(a) if a else max(0, size - int(b))
            end = min(size - 1, int(b)) if (a and b) else size - 1
            Handler.ranges += 1
            self.send_response(206)
            self.send_header("Content-Range", "bytes %d-%d/%d" % (start, end, size))
        else:
            self.send_response(200)
        self.send_header("Content-Type", "video/mp4")
        self.send_header("Accept-Ranges", "bytes")
        self.send_header("Content-Length", str(end - start + 1))
        self.end_headers()
        try:
            with open(path, "rb") as f:
                f.seek(start)
                left = end - start + 1
                while left > 0:
                    chunk = f.read(min(1 << 20, left))
                    if not chunk:
                        break
                    self.wfile.write(chunk)
                    left -= len(chunk)
        except (ConnectionError, OSError):
            pass


class Server(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True

    def handle_error(self, request, client_address):
        pass        # the browser drops connections when it has enough of a range


def main():
    edge, profile, clips = sys.argv[1], sys.argv[2], sys.argv[3:]
    srv = Server(("127.0.0.1", 0), Handler)
    port = srv.server_address[1]
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    ok = True
    for clip in clips:
        name = os.path.basename(clip)
        Handler.files = {name: clip}
        Handler.result = None
        Handler.done.clear()
        Handler.ranges = 0
        url = "http://127.0.0.1:%d/play.html?file=%s" % (port, urllib.parse.quote(name))
        cmd = [edge, "--headless=new", "--no-first-run", "--no-default-browser-check", "--disable-background-networking",
               "--disable-component-update", "--disable-sync", "--disable-extensions", "--mute-audio",
               "--autoplay-policy=no-user-gesture-required", "--no-proxy-server",
               "--host-resolver-rules=MAP * ~NOTFOUND , EXCLUDE 127.0.0.1", "--user-data-dir=" + profile, url]
        p = subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        got = Handler.done.wait(150)
        subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True)
        try:
            p.wait(10)
        except subprocess.TimeoutExpired:
            pass
        r = Handler.result
        print("== %s (%.1f MB), %d range requests" % (name, os.path.getsize(clip) / 1048576.0, Handler.ranges))
        if not got or r is None:
            print("   no result from the page within 150 s")
            ok = False
            continue
        print("   browser: %s" % r.get("ua", "?"))
        print("   canPlayType(hvc1): '%s'; duration %s s, %sx%s" % (r.get("canHevc"), r.get("duration"), r.get("width"), r.get("height")))
        print("   played from the start: reached %.2f s after 2.5 s" % (r.get("playedFromStart") or 0))
        for s in r.get("seeks", []):
            print("   seek to %9.3f s -> landed %9.3f s, then advanced %.2f s in 1.5 s (readyState %s)" % (s["target"], s["landed"], s["advanced"], s["readyState"]))
        print("   played into the end: %s (time %.3f s of %.3f s)" % (r.get("reachedEnd"), r.get("endTime") or 0, r.get("duration") or 0))
        print("   frames shown %s, dropped %s, corrupted %s; decoded bytes: video %s, audio %s" % (
            r.get("framesShown"), r.get("framesDropped"), r.get("framesCorrupted"), r.get("videoBytesDecoded"), r.get("audioBytesDecoded")))
        bad = r.get("mediaError") or r.get("exception")
        if bad:
            print("   ERROR: %s %s" % (r.get("mediaError", ""), r.get("exception", "")))
        good = (not bad and r.get("reachedEnd") and (r.get("playedFromStart") or 0) > 1.0 and len(r.get("seeks", [])) == 4
                and all(s["advanced"] > 0.5 and abs(s["landed"] - s["target"]) < 0.1 for s in r["seeks"])
                and (r.get("audioBytesDecoded") or 0) > 0 and (r.get("videoBytesDecoded") or 0) > 0 and not r.get("framesCorrupted"))
        print("   RESULT: %s" % ("PLAYS" if good else "PROBLEM"))
        ok = ok and good
    srv.shutdown()
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
