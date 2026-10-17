@echo off
rem Removes MLV Drive for this Windows user (your footage is never touched).
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%LOCALAPPDATA%\Programs\MLV Drive\Uninstall.ps1"
pause
