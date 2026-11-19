; Replay Clipper - installer script (Inno Setup 6.7).
;
; Build:  build-installer.cmd   (or: ..\tools\innosetup\ISCC.exe setup.iss)
; Result: ..\dist\ReplayClipper-Setup-<version>.exe
;
; The program files are taken from the repository root (..). Build the app first (..\build.cmd). The
; binary dependencies (engine\, ffmpeg\ffmpeg.exe, the WebView2 files) must be in place as well, see
; "Building" in README.en.md: the build stops if one is missing.
; Keep AppVersion below in step with "AppVersion" in ..\src\Program.cs.

#define AppName "Replay Clipper"
#define AppVersion "2.0.1"
#define AppExe "ReplayClipper.exe"
#define AppDir ".."
; The app holds this mutex for as long as it runs, also while it is only an icon in the notification area.
#define RunningMutex "ReplayClipper.Running"
; The recorder's helper program, relative to the app folder. Version 1.0 did not have it.
#define EngineExe "engine\bin\64bit\ReplayClipperEngine.exe"
; How long to wait for the running app to be gone after it was asked to exit, in milliseconds.
#define QuitWaitMs 15000

[Setup]
AppId={{6F0B8C0E-51C4-4B7D-9E2A-3D5A7C1E8F42}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Per-user install: no administrator prompt. The default location is
; %LOCALAPPDATA%\Programs\ReplayClipper, or the place of an existing installation (see [Code]).
PrivilegesRequired=lowest
DefaultDirName={code:GetDefaultDir}
DisableDirPage=no
DisableProgramGroupPage=yes
DirExistsWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Uninstalling is done by the app's own Uninstall.exe, which also stops the recorder, removes the replay
; buffer and the autostart entry, and cleans up what version 1.0 left in OBS.
; The app registers that uninstaller under "Installed apps" itself (see [Run]), so Setup must not add a second entry.
Uninstallable=no

; The app holds this mutex while it runs. It keeps running in the notification area after its window is
; closed, so Setup closes an installed copy itself before this check is made (InitializeSetup in [Code]).
; The standard "please close the app" prompt remains for what that does not cover: version 1.0, which exits
; when its window is closed, and a copy started from another folder.
AppMutex={#RunningMutex}
CloseApplications=yes
RestartApplications=no

WizardStyle=modern dynamic windows11
SetupIconFile={#AppDir}\src\app.ico
WizardSmallImageFile=images\small-*.png
WizardImageFile=images\large-*.png
ShowLanguageDialog=yes
UsePreviousLanguage=no

Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir=..\dist
OutputBaseFilename=ReplayClipper-Setup-{#AppVersion}

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zh"; MessagesFile: "languages\ChineseSimplified.isl"

[CustomMessages]
en.FolderNotEmpty=This folder is not empty:%n%n%1%n%nInstall here anyway?
zh.FolderNotEmpty=该文件夹不为空：%n%n%1%n%n是否仍要安装到此位置？
en.Finishing=Finishing installation...
zh.Finishing=正在完成安装...
en.CloseRunningApp={#AppName} is running.%n%nClick OK to let Setup close it and continue, or Cancel to exit. Background recording stops and unsaved footage is discarded.
zh.CloseRunningApp={#AppName} 正在运行。%n%n点击“确定”，安装程序将关闭它并继续；点击“取消”退出安装。后台录制将停止，未保存的画面会丢弃。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[InstallDelete]
; Version 1.0 kept the script that OBS loaded in {app}\obs. Version 2.0 does not use OBS.
Type: filesandordirs; Name: "{app}\obs"

[Files]
; The program, the recorder (engine\) and the source code (src\, build.cmd). Personal data (data\) is not installed.
Source: "{#AppDir}\ReplayClipper.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\Uninstall.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\Microsoft.Web.WebView2.Core.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\Microsoft.Web.WebView2.WinForms.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\WebView2Loader.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README.zh-CN.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\ui\*"; DestDir: "{app}\ui"; Flags: ignoreversion recursesubdirs
Source: "{#AppDir}\ffmpeg\*"; DestDir: "{app}\ffmpeg"; Flags: ignoreversion recursesubdirs
Source: "{#AppDir}\engine\*"; DestDir: "{app}\engine"; Flags: ignoreversion recursesubdirs
; Source code: the app (src\*.cs, with the icon and the manifest that build.cmd uses) and the recorder's helper
; program (src\engine\). The complete project is at https://github.com/Lunafreya123/ReplayClipper
Source: "{#AppDir}\build.cmd"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#AppDir}\src\*.cs"; DestDir: "{app}\src"; Flags: ignoreversion
Source: "{#AppDir}\src\app.ico"; DestDir: "{app}\src"; Flags: ignoreversion
Source: "{#AppDir}\src\app.manifest"; DestDir: "{app}\src"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#AppDir}\src\engine\*"; DestDir: "{app}\src\engine"; Flags: ignoreversion recursesubdirs

[Icons]
; One Start menu entry (no program group) and an optional desktop shortcut.
; The app's uninstaller removes shortcuts that point into the install folder.
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; Registers the app under "Installed apps" and stores the language chosen here as the app's language
; (only if no language has been chosen in the app before). Opens no window.
Filename: "{app}\{#AppExe}"; Parameters: "--register {language}"; StatusMsg: "{cm:Finishing}"; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// The app records where it is installed (it has no Inno Setup uninstall entry).
// Returns that folder if the app is still there, otherwise an empty string.
function GetInstalledDir(): String;
var
  S: String;
begin
  Result := '';
  if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\ReplayClipper', 'InstallLocation', S)
     and (S <> '') and FileExists(AddBackslash(S) + '{#AppExe}') then
    Result := RemoveBackslashUnlessRoot(S);
end;

// When updating, offer the existing location again.
function GetDefaultDir(Param: String): String;
begin
  Result := GetInstalledDir();
  if Result = '' then
    Result := ExpandConstant('{autopf}\ReplayClipper');
end;

// Runs before Setup's AppMutex check. If the installed copy of the app is running, ask it to exit:
// "ReplayClipper.exe --quit" ends the app that runs from its own folder, together with the recorder, and
// returns when both are gone (it waits a few seconds, then ends them forcibly).
// Only version 2.0 and later understand --quit; they are recognized by the recorder in their folder.
// Version 1.0 would merely bring its window to the front, so it is left to the AppMutex prompt.
// If the app is still running afterwards, the AppMutex prompt follows as before.
function InitializeSetup(): Boolean;
var
  Dir, Exe: String;
  ResultCode, Waited: Integer;
begin
  Result := True;
  if not CheckForMutexes('{#RunningMutex}') then
    Exit;
  Dir := GetInstalledDir();
  if (Dir = '') or not FileExists(AddBackslash(Dir) + '{#EngineExe}') then
    Exit;

  // Silent installs proceed without asking. Cancel leaves the app running and exits Setup.
  if not WizardSilent() then
    if SuppressibleMsgBox(CustomMessage('CloseRunningApp'), mbInformation, MB_OKCANCEL, IDOK) <> IDOK then
    begin
      Result := False;
      Exit;
    end;

  Exe := AddBackslash(Dir) + '{#AppExe}';
  Log('Closing the running app: "' + Exe + '" --quit');
  if not Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Log('Could not start it: ' + SysErrorMessage(ResultCode));
    Exit;
  end;

  Waited := 0;
  while CheckForMutexes('{#RunningMutex}') and (Waited < {#QuitWaitMs}) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
  if CheckForMutexes('{#RunningMutex}') then
    Log('The app is still running.');
end;

// True if the folder contains files that are not an existing installation of this app.
function FolderHasOtherFiles(Dir: String): Boolean;
var
  F: TFindRec;
begin
  Result := False;
  if FileExists(AddBackslash(Dir) + '{#AppExe}') then
    Exit;
  if FindFirst(AddBackslash(Dir) + '*', F) then
  try
    repeat
      if (F.Name <> '.') and (F.Name <> '..') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpSelectDir) and FolderHasOtherFiles(WizardDirValue) then
    Result := SuppressibleMsgBox(FmtMessage(CustomMessage('FolderNotEmpty'), [WizardDirValue]),
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDYES) = IDYES;
end;
