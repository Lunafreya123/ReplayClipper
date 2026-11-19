@echo off
rem Rebuild everything with the C# compiler that ships with Windows (.NET Framework 4.x, C# 5 syntax):
rem
rem   ReplayClipper.exe                          the app (src\*.cs)
rem   Uninstall.exe                              only launches "ReplayClipper.exe --uninstall" (src\UninstallStub.cs)
rem   engine\bin\64bit\ReplayClipperEngine.exe   the recording engine helper (src\engine\*.cs, GPL-2.0-or-later).
rem                                              It must sit next to obs.dll.
rem
rem Close Replay Clipper first: the files cannot be replaced while the app or its helper is running.
rem The three WebView2 files next to this script (Microsoft.Web.WebView2.*.dll, WebView2Loader.dll) are needed
rem both to compile and to run; they come from Microsoft's "Microsoft.Web.WebView2" NuGet package.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set ICON=
set ICONRES=
set MANIFEST=
if exist "%~dp0src\app.ico" set ICON=/win32icon:"%~dp0src\app.ico"
if exist "%~dp0src\app.ico" set ICONRES=/resource:"%~dp0src\app.ico",app.ico
if exist "%~dp0src\app.manifest" set MANIFEST=/win32manifest:"%~dp0src\app.manifest"
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ ^
  /out:"%~dp0ReplayClipper.exe" %ICON% %ICONRES% %MANIFEST% ^
  /r:System.Core.dll /r:System.Web.Extensions.dll /r:Microsoft.VisualBasic.dll /r:System.Management.dll ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:"%~dp0Microsoft.Web.WebView2.Core.dll" /r:"%~dp0Microsoft.Web.WebView2.WinForms.dll" ^
  "%~dp0src\Program.cs" "%~dp0src\Host.cs" "%~dp0src\Background.cs" "%~dp0src\Recorder.cs" ^
  "%~dp0src\Engine.cs" "%~dp0src\LegacyObs.cs" "%~dp0src\Uninstall.cs"
if errorlevel 1 exit /b %ERRORLEVEL%
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ ^
  /out:"%~dp0Uninstall.exe" %ICON% %MANIFEST% "%~dp0src\UninstallStub.cs"
if errorlevel 1 exit /b %ERRORLEVEL%
"%CSC%" /nologo /codepage:65001 /target:winexe /platform:x64 /optimize+ ^
  /out:"%~dp0engine\bin\64bit\ReplayClipperEngine.exe" ^
  /r:System.Core.dll /r:System.Web.Extensions.dll ^
  "%~dp0src\engine\Native.cs" "%~dp0src\engine\Log.cs" "%~dp0src\engine\TsScan.cs" ^
  "%~dp0src\engine\Ring.cs" "%~dp0src\engine\Recorder.cs" "%~dp0src\engine\Program.cs"
exit /b %ERRORLEVEL%
