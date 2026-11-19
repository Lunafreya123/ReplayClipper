# Engine test tools

Test harness and checks for the recorder (`src/engine/`) and the save path (`src/Engine.cs`). Not part of the app.

Run `build-engine.cmd` here first (it builds `EngineHarness.exe` and `JoinBench.exe` from this folder and `..\..\src\Engine.cs`). The scripts then expect a separate work folder laid out like this:

```
<work folder>\
  ReplayClipper\    engine\ and ffmpeg\ of a built copy of the app
  engine-tools\     a copy of this folder, including the two .exe files
  bench\media\      pattern840s.mp4: any 1280x720 60 fps H.264 + AAC video of a few minutes
  bench\            everything else is created by the scripts (rings, clips, logs)
```

Example: `powershell -ExecutionPolicy Bypass -File <work folder>\engine-tools\both_test.ps1 -Root <work folder>`

| File | Purpose |
| --- | --- |
| `Harness.cs`, `build-engine.cmd` | `EngineHarness.exe`: drives `EngineClient` from the console (record, save, kill the helper on a schedule) |
| `JoinBench.cs` | profiles the save path step by step on an existing ring |
| `both_test.ps1` | saves the same footage with the app's own MP4 writer and with ffmpeg and compares the two; **run this after replacing `ffmpeg.exe`** |
| `proof_run.ps1`, `measure.ps1` | long proof recordings, save timings with warm or cold file cache |
| `mp4_check.py`, `exact_check.py`, `av_check.py`, `play_check.py` | MP4 structure, timeline exactness against a continuous reference, A/V sync, playback in headless Edge |
| `netwatch.ps1` | confirms the helper makes no network connections |

The helper records a test video file instead of the screen in these runs (`--videofile`). Python scripts: `python -I <script> ...`.
