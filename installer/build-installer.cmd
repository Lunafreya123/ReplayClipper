@echo off
rem Builds ..\dist\ReplayClipper-Setup-<version>.exe from the program in the repository root.
rem
rem Before running this:
rem   1. build the app (..\build.cmd; the app must not be running from this folder)
rem   2. make sure the version numbers match: "AppVersion" in setup.iss and in ..\src\Program.cs
rem
rem The installer is made with Inno Setup 6 (https://jrsoftware.org/isinfo.php). Put a portable copy in
rem ..\tools\innosetup, or set ISCC to the path of ISCC.exe. It is only needed to build the installer.
setlocal
if not defined ISCC set ISCC=%~dp0..\tools\innosetup\ISCC.exe
if not exist "%ISCC%" (
  echo Inno Setup compiler not found: %ISCC%
  exit /b 1
)
"%ISCC%" /Qp "%~dp0setup.iss"
exit /b %ERRORLEVEL%
