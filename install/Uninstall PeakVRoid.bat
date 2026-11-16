@echo off
rem Double-click to remove PeakVRoid from PEAK (keeps BepInEx and your avatars).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PeakVRoid.ps1" -Uninstall %*
pause
