@echo off
rem Builds the test tools into this folder with the C# compiler that ships with Windows
rem (.NET Framework 4.x, C# 5 syntax):
rem
rem   EngineHarness.exe   console test driver for the app-side client ..\..\src\Engine.cs
rem   JoinBench.exe       profiling tool for the save path
rem
rem The recorder helper itself (engine\bin\64bit\ReplayClipperEngine.exe) is built by ..\..\build.cmd.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /codepage:65001 /target:exe /platform:x64 /optimize+ ^
  /out:"%~dp0EngineHarness.exe" ^
  /r:System.Core.dll /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0..\..\src\Engine.cs" "%~dp0Harness.cs"
if errorlevel 1 exit /b %ERRORLEVEL%
"%CSC%" /nologo /codepage:65001 /target:exe /platform:x64 /optimize+ ^
  /out:"%~dp0JoinBench.exe" /r:System.Core.dll /r:System.Web.Extensions.dll "%~dp0JoinBench.cs"
exit /b %ERRORLEVEL%
