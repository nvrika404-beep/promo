@echo off
rem Prints the devices BatteryTray can see, then waits for a key.
chcp 65001 >nul
"%WINDIR%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0BatteryTray.ps1" -List
pause
