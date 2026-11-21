@echo off
rem Starts BatteryTray in the background, without a console window.
start "" "%WINDIR%\System32\conhost.exe" --headless "%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0BatteryTray.ps1"
