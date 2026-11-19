#!/usr/bin/env bash
# build-ffmpeg.sh - minimal static ffmpeg.exe for Replay Clipper
#
# Builds FFmpeg 9.0.2 (unpatched release tarball) for 64-bit Windows with only what Replay Clipper
# runs ffmpeg for:
#   1. join      -f mpegts -i pipe:0 -map 0:v:0 -map 0:a:0 -c copy [-tag:v hvc1] -moov_size N -f mp4 out
#                (retried with -movflags +faststart instead of -moov_size when the reserve was too small)
#   2. referee   the same for one 2-second segment, with -movflags +faststart
#   3. trim      [-ss a] [-to b] -i in.{mp4,mov,m4v,mkv} -map 0:v:0 -map 0:a? -c copy [-movflags +faststart] out.{same set}
#   4. info      -hide_banner -nostdin -i file            (stderr text is parsed by the app)
#   5. thumbnail [-noaccurate_seek] -ss t -i file -frames:v 1 -an -sn -vf scale=-2:h -q:v 5 -f image2pipe -c:v mjpeg pipe:1
# The remux output must be byte-identical to the gyan.dev "full" build of the same version, which is why
# FFmpeg's native decoders and parsers for every codec the inputs can carry are kept: even with -c copy,
# avformat_find_stream_info() runs them to fill in codec parameters, and the MP4 muxer writes boxes from those.
#
# Result: LGPL-2.1-or-later (no --enable-gpl, no --enable-version3, no external libraries).
#
# Host: Ubuntu 24.04 (WSL2 is fine), run as root. Cross-compiles with mingw-w64 (gcc, win32 thread model)
# and nasm. Builds under $WORK (default /root/ffbuild), copies ffmpeg.exe to the output directory
# (first argument, default: the current directory).
#
#   sudo bash build-ffmpeg.sh /mnt/c/some/folder
#
# Environment knobs:
#   WORK=/path        build tree (default /root/ffbuild); delete it afterwards if you like
#   APT_MIRROR=URL    use this Ubuntu mirror for the package step only (e.g. https://mirrors.tuna.tsinghua.edu.cn/ubuntu),
#                     without touching /etc/apt
#   SKIP_APT=1        do not check/install packages
#   JOBS=N            parallel make jobs (default: nproc)
set -euo pipefail

FF_VERSION=9.0.2
FF_TARBALL=ffmpeg-${FF_VERSION}.tar.xz
FF_URL=https://ffmpeg.org/releases/${FF_TARBALL}
# SHA-256 of ffmpeg-9.0.2.tar.xz as downloaded on 2026-11-20; its signature verified with the key below and its
# tree compared with the GitHub mirror's tag n9.0.2 (identical apart from .gitignore/.gitattributes and VERSION).
FF_SHA256=8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e
# "FFmpeg release signing key <ffmpeg-devel@ffmpeg.org>"
FF_KEY_URL=https://ffmpeg.org/ffmpeg-devel.asc
FF_KEY_FPR=FCF986EA15E6E293A5644F10B4322F04D67658D8

WORK=${WORK:-/root/ffbuild}
OUT=$(realpath -m "${1:-$PWD}")
JOBS=${JOBS:-$(nproc)}
CROSS=x86_64-w64-mingw32-

# Never go through a proxy: downloads are direct.
unset http_proxy https_proxy HTTP_PROXY HTTPS_PROXY all_proxy ALL_PROXY ftp_proxy FTP_PROXY no_proxy NO_PROXY

log() { printf '\n=== %s\n' "$*"; }

# ---------------------------------------------------------------------------------------------- 1. packages
# gcc-mingw-w64-x86-64-win32  cross gcc 13 with the win32 thread model (no libwinpthread needed)
# binutils-mingw-w64-x86-64   cross ld/strip/objdump
# mingw-w64-x86-64-dev        Windows headers and import libraries (msvcrt CRT)
# nasm                        FFmpeg's x86 assembly (kept on: speed, and the same swscale code as the gyan build)
# make pkg-config xz-utils gnupg curl ca-certificates   build/download/verify tools
PKGS=(gcc-mingw-w64-x86-64-win32 binutils-mingw-w64-x86-64 mingw-w64-x86-64-dev nasm
      make pkg-config xz-utils gnupg curl ca-certificates)
if [ "${SKIP_APT:-0}" != 1 ]; then
    missing=()
    for p in "${PKGS[@]}"; do dpkg -s "$p" >/dev/null 2>&1 || missing+=("$p"); done
    if [ ${#missing[@]} -gt 0 ]; then
        log "installing: ${missing[*]}"
        APT_OPTS=(-o Acquire::http::Proxy=false -o Acquire::https::Proxy=false)
        if [ -n "${APT_MIRROR:-}" ]; then
            # a one-off source list; /etc/apt stays as it is
            tmpl=$(mktemp)
            . /etc/os-release
            printf 'deb %s %s main universe\ndeb %s %s-updates main universe\n' \
                "$APT_MIRROR" "$VERSION_CODENAME" "$APT_MIRROR" "$VERSION_CODENAME" > "$tmpl"
            APT_OPTS+=(-o Dir::Etc::SourceList="$tmpl" -o Dir::Etc::SourceParts=/dev/null)
        fi
        export DEBIAN_FRONTEND=noninteractive
        apt-get "${APT_OPTS[@]}" update -q
        # --no-upgrade: install what is missing, leave already-installed packages at their version
        apt-get "${APT_OPTS[@]}" install -y -q --no-install-recommends --no-upgrade "${missing[@]}"
    fi
fi
for t in ${CROSS}gcc ${CROSS}strip ${CROSS}objdump nasm make curl gpg xz sha256sum; do
    command -v "$t" >/dev/null || { echo "missing tool: $t" >&2; exit 1; }
done

# ---------------------------------------------------------------------------------------------- 2. source
mkdir -p "$WORK/dl"
cd "$WORK/dl"
fetch() {   # url file
    [ -f "$2" ] && return 0
    curl -fSL --retry 3 --max-time 900 -o "$2.part" "$1"
    mv "$2.part" "$2"
}
fetch "$FF_URL"     "$FF_TARBALL"
fetch "$FF_URL.asc" "$FF_TARBALL.asc"
fetch "$FF_KEY_URL" ffmpeg-devel.asc

log "verifying $FF_TARBALL"
echo "$FF_SHA256  $FF_TARBALL" | sha256sum -c -
# signature check with a throwaway keyring (root's own keyring is not touched); the key must be the pinned one
export GNUPGHOME="$WORK/gnupg"
rm -rf "$GNUPGHOME"; mkdir -m 700 "$GNUPGHOME"
gpg --batch --quiet --import ffmpeg-devel.asc
gpg --batch --with-colons --fingerprint | grep -q "^fpr:.*:${FF_KEY_FPR}:" \
    || { echo "release key fingerprint mismatch" >&2; exit 1; }
gpg --batch --status-fd 1 --verify "$FF_TARBALL.asc" "$FF_TARBALL" 2>/dev/null | grep -q "VALIDSIG ${FF_KEY_FPR}" \
    || { echo "bad signature on $FF_TARBALL" >&2; exit 1; }
echo "signature OK (key $FF_KEY_FPR)"

SRC="$WORK/ffmpeg-$FF_VERSION"
rm -rf "$SRC"
tar -xJf "$FF_TARBALL" -C "$WORK"

# ---------------------------------------------------------------------------------------------- 3. configure
# Everything off, then only what the five command shapes need.
#  - --disable-autodetect also turns off threading (it is in the autodetect list), and the ffmpeg CLI needs
#    threads: hence --enable-w32threads (native Win32 threads; the gyan build uses winpthreads, which makes
#    no difference to any output).
#  - decoders h264,hevc,aac,opus and the matching parsers: needed for stream info even with -c copy (see top);
#    these are the decoders the full build picks as well (its native ones come first in libavcodec's list).
#  - bitstream filters: the ones libavformat looks up by name for these demuxers/muxers, plus a few generic ones:
#      extract_extradata  avformat_find_stream_info() uses it to pull the parameter sets out of the first
#                         keyframe when the input (MPEG-TS) has no extradata. configure does NOT select it on
#                         its own. Without it the MP4 muxer builds hvcC from the first packet itself, and
#                         for some streams that differs: tested, an x265 10-bit stream got its first-frame
#                         SEI into hvcC (2448 instead of 122 bytes); the real OBS/AMF footage happened to
#                         come out the same either way.
#      aac_adtstoasc      inserted by the MP4/MOV/Matroska muxers for ADTS audio (AAC from MPEG-TS)
#      vp9_superframe, pgs_frame_merge   inserted by the muxers for those codecs (selected by configure anyway)
#      h264_mp4toannexb, hevc_mp4toannexb, dump_extradata, remove_extradata, setts, null   generic, for -bsf use
#    (--enable-bsfs, i.e. all of them, works too and adds about 0.75 MB, mostly the CBS parsers for
#    AV1/H.266/H.265/H.264/... that only the *_metadata/trace_headers/filter_units/dovi_rpu filters use.)
#  - filters: scale (the -vf, and auto-inserted for pixel format conversion), plus everything the ffmpeg CLI
#    and libavfilter insert on their own: format/aformat/null/anull, trim/atrim (accurate seek, -to),
#    crop (container cropping), transpose/hflip/vflip/rotate (autorotate), aresample; setpts/asetpts for
#    manual use. (configure selects most of these for the ffmpeg program anyway.)
#  - not --enable-small: it shortens log texts the app parses.
#  - -static: no DLLs besides Windows system ones (libgcc linked in).
#  - --no-insert-timestamp (ld) and SOURCE_DATE_EPOCH=0 (strip would otherwise stamp the current time):
#    PE time stamp 0, so that two runs of this script give the same bytes (together with the fresh build
#    directory below). The C runtime is msvcrt.dll (Ubuntu's mingw-w64 default; the gyan build uses UCRT).
CONFIGURE_FLAGS=(
    --target-os=mingw32 --arch=x86_64 --cross-prefix=$CROSS
    --enable-static --disable-shared
    --disable-everything --disable-autodetect
    --disable-network --disable-doc --disable-ffplay --disable-ffprobe --disable-debug
    --disable-avdevice
    --enable-w32threads
    --enable-protocol=file,pipe
    --enable-demuxer=mpegts,mov,matroska
    --enable-muxer=mp4,mov,ipod,matroska,image2pipe
    --enable-decoder=h264,hevc,aac,opus
    --enable-parser=h264,hevc,aac,opus
    --enable-encoder=mjpeg
    --enable-bsf=extract_extradata,aac_adtstoasc,vp9_superframe,pgs_frame_merge,h264_mp4toannexb,hevc_mp4toannexb,dump_extradata,remove_extradata,setts,null
    --enable-filter=scale,format,null,anull,trim,atrim,aformat,aresample,setpts,asetpts,crop,transpose,hflip,vflip,rotate
    --extra-ldflags='-static -Wl,--no-insert-timestamp'
)
log "configure ${CONFIGURE_FLAGS[*]}"
# always a fresh build directory: a reused one makes configure switch to a "./src" source path, which ends
# up in the assert/log file names inside the exe (harmless, but the bytes then differ from a clean run)
rm -rf "$WORK/build"
mkdir -p "$WORK/build"
cd "$WORK/build"
"$SRC/configure" "${CONFIGURE_FLAGS[@]}" | tee "$WORK/configure.out"

# ---------------------------------------------------------------------------------------------- 4. build
log "make -j$JOBS"
export SOURCE_DATE_EPOCH=0
make -j"$JOBS" ffmpeg.exe
# the Makefile makes ffmpeg_g.exe (with symbols) and a stripped copy ffmpeg.exe; strip once more to be sure
${CROSS}strip --strip-all ffmpeg.exe

# ---------------------------------------------------------------------------------------------- 5. check, copy
log "imported DLLs"
${CROSS}objdump -p ffmpeg.exe | sed -n 's/^\s*DLL Name: //p' | sort -u
mkdir -p "$OUT"
cp ffmpeg.exe "$OUT/ffmpeg.exe"
log "done: $OUT/ffmpeg.exe"
ls -l "$OUT/ffmpeg.exe"
sha256sum "$OUT/ffmpeg.exe"
