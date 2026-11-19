Replay Clipper
==============

Replay Clipper saves the last few minutes of gameplay as a clip, the way a
console does. It records the chosen display continuously in the background,
with system audio and, if you want, the microphone. When something worth
keeping happens, choose a length (3, 5, 15 or 30 minutes, or your own) or
press its hotkey, and the clip is saved. Clips can be played and trimmed in
the app.

Version 2.0 records by itself. OBS Studio is no longer needed. If you used
version 1.0, see "Upgrading from version 1.0" below.

中文说明见 README.zh-CN.txt。界面语言可在窗口右上角切换。


Requirements
------------
- Windows 10 or 11, 64-bit
- Free disk space for the replay buffer. Recording settings shows how much
  the chosen settings need.
- Nothing else to install. The recorder and ffmpeg are included in this
  folder.

Recording uses the graphics card's video encoder (NVIDIA, AMD or Intel). If
none is available, software encoding is used instead. Recording settings
says so when that is the case, because software encoding lowers game frame
rates noticeably.


Getting started
---------------
1. Start Replay Clipper from the Start menu or the desktop shortcut.
   (If you received the app as a folder rather than a setup program, put the
   folder somewhere permanent and run ReplayClipper.exe.)
2. The first time, Recording settings opens by itself. Check the display to
   record and the other settings (see "Recording settings" below), then
   select Start recording.
   If you close the dialog without starting, the home page shows
   "Recording isn't set up yet" and a Recording settings button.
3. From then on the app records in the background. The home page shows the
   recording status, for example
   "Recording in background · keeping the last 30 min", and the save
   lengths.


Everyday use
------------
- Save: select a length on the home page, or press its hotkey. The most
  recent footage of that length is saved as a clip. If less than that has
  been recorded so far, everything in the replay buffer is saved.
  The clip is cut from the replay buffer without re-encoding. It starts on
  a two-second boundary of the recording, so it can be up to two seconds
  longer than the length you asked for. Saving takes longer the longer the
  clip is. As an indication, not a promise: on the development computer,
  with an NVMe SSD and the default quality, saving 5 minutes took about
  0.2 seconds and saving 30 minutes about 1 second.
- Play and trim: open a clip under Clips. Select Trim, drag the handles at
  either end of the timeline, then select Export clip. Export is lossless
  (no re-encoding).
- Stop: select Stop next to the recording status on the home page. Footage
  that has not been saved is discarded, and the disk space used by the
  replay buffer is freed. To record again, select Start recording.
- The app remembers whether recording is on. Once started, recording
  resumes every time the app starts, until you select Stop.


Save lengths and hotkeys
------------------------
The home page shows up to five save lengths as tiles. Selecting a tile and
pressing its hotkey do the same thing. The defaults:

  3 minutes     Alt+F7
  5 minutes     Alt+F8
  15 minutes    Alt+F9
  30 minutes    Alt+F10

Hotkeys are global. They work in games and in other programs as long as
Replay Clipper is running, including when it runs in the background without
a window. A save by hotkey, or from the menu of the app's icon in the
notification area, shows a small notification at the top right of the
screen and plays the system sound when it is done.

To change lengths and hotkeys, select Edit after the save tiles on the home
page, or Edit… under "Save lengths and hotkeys" in Recording settings:
- Length: from 15 seconds up to the replay length.
- Hotkey: select the hotkey field and press the new combination. Function
  keys can be used on their own; other keys need Ctrl or Alt. The Windows
  key and combinations reserved by Windows (such as Alt+F4 or Alt+Tab)
  cannot be used. A length can also have no hotkey.
- Add adds a length, up to five. Restore defaults brings back the four
  lengths and hotkeys listed above.
- Select Save. Nothing changes until then.

A hotkey that another program already uses cannot be saved; the dialog marks
it "Already in use by another program". If a hotkey becomes unavailable
later, its tile shows "Hotkey unavailable". Selecting the tile still works.

If the replay length is shorter than a tile's length, that tile saves
everything in the replay buffer.


Running in the background
-------------------------
While it runs, Replay Clipper keeps an icon in the taskbar's notification
area. Recording and hotkeys do not depend on the window.

- When you close the window, the app asks whether to keep running in the
  background or to exit. With "Don't ask again" selected, the choice is
  remembered; you can change it later under "When closing the window" in
  Recording settings. When the app exits, recording stops, footage that
  has not been saved is discarded, and hotkeys no longer work.
- Select the icon to open the window. Right-click it for a menu with
  Open Replay Clipper, a save command for each length (such as
  "Save the last 3 min"), Start recording or Stop recording, and Exit.
- Starting Replay Clipper again while it is running opens its window.
- To start the app together with Windows, turn on
  "Start with Windows and record in the background" in Recording settings.
  The app then starts in the notification area without opening a window,
  and continues recording if recording was on.


Recording settings
------------------
Select Recording settings in the title bar.

- Display to record. With two or more displays, they are drawn in their
  actual arrangement; select the one your games run on. Select Identify to
  show each display's number on screen.
- Resolution: 1080p, 1440p or the display's native resolution. Sizes larger
  than the display are not offered. Higher resolution means larger files
  and more disk use.
- Frame rate: 60 fps or 30 fps.
- Replay length: how far back you can save. Choose 15 min, 30 min, 1 h or
  2 h, or select Custom and enter 1 to 720 minutes (12 hours). The replay
  buffer is kept on disk; the dialog shows the estimated disk space and the
  free space on the drive that holds the buffer.
- Encoder: Automatic, or one of the encoders available on this computer.
- Save location: the folder for saved clips. Enter a path, or select
  Browse… to choose a folder. Clips cannot be saved inside the app's own
  folders or inside the buffer folder.
- Buffer location: where the replay buffer, the rolling recording that
  saved clips are cut from, is kept. The default is the app's data folder.
  In any other location the app keeps the buffer in a subfolder of its
  own, named "Replay Clipper buffer"; the dialog shows which folder is
  used. Network locations cannot be used. Enter a path, or select Browse…
  to choose a folder; Use default returns to the default location.
- Record microphone: when off, only system audio is recorded.
- Start with Windows and record in the background.
- When closing the window: Ask every time, Run in background, or Exit.

Select Save. While recording is on, some changes restart it, and a change
of display, resolution, frame rate, encoder or buffer location also clears
the footage already in the replay buffer. The dialog says so before you
save.


Trimming precisely
------------------
  Mouse wheel on the timeline     zoom in or out around the pointer
  Shift + mouse wheel             pan the timeline
  + / - / 0                       zoom in / zoom out / show the whole clip
  Left / Right                    1 second (5 seconds with Shift)
  Left / Right while trimming     1 frame (1 second with Shift)
  - and + next to Start and End   1 frame per click (1 second with Shift);
                                  hold to repeat
  I / O                           set the start / end at the current position
  Home / End                      jump to the start / end of the clip
  Space                           play or pause
  Esc                             leave trimming; when not trimming, go
                                  back to the home page

When zoomed in, the bar under the timeline shows which part of the clip is
visible; drag it to pan.

The Speed buttons under the video set the playback speed: 0.5×, 1×, 2× or
4×. While trimming, Preview plays only the selected range. With
"Delete original after export" selected, the original video is moved to the
Recycle Bin after a successful export.

Clips are cut without re-encoding, so a clip starts on the exact frame you
chose in players that honor MP4 edit lists (most do). A few players start
from the preceding keyframe instead, up to 2 seconds earlier.


Good to know
------------
- Language: use the button at the top right of the window (中文 / English).
- Clips are MP4 files in the save location. Unless you chose another folder,
  that is the Replay Clipper folder in your Videos folder. Open folder in
  the title bar opens it.
- Disk space: by default the replay buffer is stored in data\buffer inside
  this folder, so it uses the drive the app is installed on. To keep it on
  another drive, change Buffer location in Recording settings. Recording
  does not start if the buffer's drive has too little free space for the
  buffer, and it stops when less than 1 GB is left there, rather than
  filling the disk. The home page then shows "Not enough disk space". Free
  up space and select Retry, or shorten the replay length in Recording
  settings.
- The replay buffer is discarded when you select Stop and when the app
  exits: through Exit in the icon's menu, by choosing to exit when closing
  the window, or when the setup program closes the app for an upgrade.
  After a Windows shutdown, a sign-out or a crash, the buffer files are
  still on disk; they are cleaned up the next time recording starts
  (anything older than the replay length is deleted).
- If the display chosen for recording is not connected, the home page shows
  "The selected display isn't connected" and nothing new can be recorded.
  Footage already recorded can still be saved. Connect the display again,
  or choose another one in Recording settings.
- The app's own data (settings, log, cache and, unless you chose another
  buffer location, the replay buffer) is stored in the data folder inside
  this folder. If this folder is not writable (for example under
  Program Files), %LOCALAPPDATA%\ReplayClipper is used instead.
- If you move or rename this folder, start the app once from its new
  location. If you use
  "Start with Windows and record in the background", turn it on again in
  Recording settings.
- To share the app, pass on the setup program (ReplayClipper-Setup-x.y.z.exe).
  If you share this folder instead, exit the app and delete the data folder
  first. It holds your own settings and is recreated automatically.
- The app registers itself under Settings > Apps > Installed apps for the
  current user. No administrator rights are needed.


Upgrading from version 1.0
--------------------------
Version 1.0 recorded through OBS Studio. Version 2.0 does not use OBS.

- Run the setup program of version 2.0 to upgrade. It suggests the folder
  of the existing installation. Your saved clips are kept, and the app goes
  on using the same save folder. If earlier clips are not listed, set
  Save location in Recording settings to the folder that holds them.
- Set up recording once, as described under "Getting started".
- Save lengths and hotkeys are now set in the app (Edit on the home
  page). A custom length from version 1.0 is kept as a fifth tile. Lengths
  and hotkeys that were changed in OBS are not carried over.
- You no longer need to run OBS, or its replay buffer, for Replay Clipper.
  The script that version 1.0 added to OBS is removed automatically the
  next time Replay Clipper starts while OBS is not running.
- The profile and scene collection named "Replay Clipper" that version 1.0
  created in OBS are left in place. You can delete them in OBS; the
  uninstaller also asks whether to delete them.


Uninstalling
------------
Uninstall from Installed apps, or run Uninstall.exe in this folder. The
uninstaller closes the app and stops recording, then deletes the desktop
shortcut, the entry that starts the app with Windows, the program files,
the settings and the replay buffer, also when the buffer is kept in another
location. Your saved clips are not touched. Files in this folder that do
not belong to the app are left in place.

If version 1.0 was used on this computer, the uninstaller also removes the
script it left in OBS and asks whether to delete the "Replay Clipper"
profile and scene collection in OBS. OBS itself and its other settings are
not touched.


Folder contents
---------------
  ReplayClipper.exe     the app
  Uninstall.exe         the uninstaller
  Microsoft.Web.WebView2.*.dll, WebView2Loader.dll
                        Microsoft WebView2 components used for the window.
                        Without them the app opens in a Microsoft Edge
                        window, which has no Browse… buttons and cannot
                        ask what to do when the window is closed.
  ui\                   user interface
  ffmpeg\               video processing (durations, thumbnails, joining
                        and trimming clips)
  engine\               the recorder: the helper program
                        ReplayClipperEngine.exe (in engine\bin\64bit), the
                        OBS Studio core libraries it uses, and their
                        licenses and notices (engine\licenses)
  src\                  source code of the app and, in src\engine, of the
                        recorder's helper program
  build.cmd             build script: rebuilds ReplayClipper.exe,
                        Uninstall.exe and ReplayClipperEngine.exe from src\
  data\                 created at first run: settings, log, cache and, by
                        default, the replay buffer (data\buffer)


Third-party components
----------------------
The recorder in engine\ is built on the core of OBS Studio 33.0.1 (libobs
and plugins, unmodified; https://obsproject.com), which is licensed under
the GNU General Public License, version 2 or later, together with the
libraries OBS Studio ships with, among them FFmpeg and x264. The FFmpeg
libraries are builds under the GPL version 3 or later, so the engine folder
as distributed is under the GPLv3. The helper program that loads these
libraries, ReplayClipperEngine.exe, was written for Replay Clipper; its own
source code, in src\engine, is licensed under the GPL, version 2 or later.

engine\licenses contains:
  GPL-2.0.txt, GPL-3.0.txt   the license texts
  THIRD-PARTY-NOTICES.txt    every bundled binary with its version,
                             license, origin and the location of its
                             upstream source
  FILES.sha256.txt           SHA-256 checksums of the bundled OBS Studio
                             and library files

ffmpeg\ffmpeg.exe is FFmpeg 9.0.2 from the FFmpeg project
(https://ffmpeg.org), built for Replay Clipper with only the parts it uses,
under the GNU Lesser General Public License, version 2.1 or later. See
ffmpeg\LICENSE.txt for the license text and ffmpeg\README.txt for the
source and the build script.

The three WebView2 files come from Microsoft's Microsoft.Web.WebView2 NuGet
package (version 1.0.2903.40) and carry Microsoft's digital signature. The
window itself uses the WebView2 Runtime installed on the system (included
with Windows 11, and normally present on Windows 10 together with Edge).


Source code
-----------
The source code of the app is in src\, and that of the recorder's helper
program in src\engine; build.cmd rebuilds both. The complete project,
including the installer, is published at
https://github.com/Lunafreya123/ReplayClipper . The upstream source of each
component in engine\ is available at the location given for it in
engine\licenses\THIRD-PARTY-NOTICES.txt; for ffmpeg\ffmpeg.exe, see
"Third-party components" above. If one of those locations is no longer
available, a copy of that source can be requested as described in
THIRD-PARTY-NOTICES.txt.
