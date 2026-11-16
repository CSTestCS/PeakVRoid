@echo off
rem Double-click to install PeakVRoid (and BepInEx if needed) into PEAK.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-PeakVRoid.ps1" %*
pause
