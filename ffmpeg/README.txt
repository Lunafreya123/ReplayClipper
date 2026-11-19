ffmpeg.exe in this folder
=========================

FFmpeg 9.0.2 (https://ffmpeg.org), built for Replay Clipper with only the parts the app uses:
joining the replay buffer into MP4 files, lossless trimming (MP4, MOV, M4V, MKV), reading a video's
length and format, and making thumbnails (H.264 / H.265 decoding, JPEG encoding).

Licence:  GNU Lesser General Public License, version 2.1 or later (text: LICENSE.txt).
          The build uses no GPL parts and no external libraries.
Source:   the unmodified release tarball https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz
          (SHA-256 8c3850283eb25fa026482078a04051e0be17347b09ef81a0849bec15a96e002e),
          also the tag n9.0.2 of https://github.com/FFmpeg/FFmpeg
Build:    tools/ffmpeg/build-ffmpeg.sh in https://github.com/Lunafreya123/ReplayClipper
          (cross-compiled with mingw-w64 on Ubuntu 24.04; the script downloads and verifies the
          source, configures and builds). The configuration is printed by "ffmpeg -version".

Replacing this file: the app compares its own MP4 output with ffmpeg's on every save, so a different
FFmpeg version or configuration can make every save fall back to the slower ffmpeg join. After a
change, run tools/engine-tools/both_test.ps1 from the repository.

The test mode REPLAYCLIP_FAKE_ENGINE generates its videos with lavfi and libx264, which this build
does not contain; use a full FFmpeg build for that mode.
