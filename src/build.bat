@echo off
chcp 65001 >nul
rem Build with the C# compiler that ships with Windows (.NET Framework 4.x). No SDK needed.
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist app.ico powershell -NoProfile -ExecutionPolicy Bypass -File make_icon.ps1
"%CSC%" /nologo /codepage:65001 /optimize+ /target:winexe /platform:anycpu /win32icon:app.ico /out:"..\SHP转KMZ.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll Program.cs MainForm.cs Core.cs AppInfo.cs
if errorlevel 1 (
  echo BUILD FAILED
  pause
  exit /b 1
)
echo Build OK