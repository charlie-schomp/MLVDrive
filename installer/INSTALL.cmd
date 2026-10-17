@echo off
rem Installs MLV Drive for this Windows user. installer\Install.ps1 says exactly what it does.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0installer\Install.ps1" %*
if errorlevel 1 pause
