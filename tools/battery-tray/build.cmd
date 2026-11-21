@echo off
rem Builds BatteryTray.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo .NET Framework 4.x compiler not found.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /codepage:65001 /win32icon:"%~dp0BatteryTray.ico" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /out:"%~dp0BatteryTray.exe" "%~dp0BatteryTray.cs"
if errorlevel 1 (
  echo Build failed.
  if not defined CI pause
  exit /b 1
)
echo Built: %~dp0BatteryTray.exe
if not defined CI pause
