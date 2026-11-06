# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 charlie-schomp
# Removes MLV Drive for this user. Leaves WinFsp installed, since other programs may use
# it; remove it from Settings > Apps if you no longer want it. Your footage is never
# touched: MLV Drive only ever read it.

$ErrorActionPreference = 'Stop'

$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\MLV Drive'
$DataDir    = Join-Path $env:LOCALAPPDATA 'MLV Drive'

Get-Process -Name 'MLVDrive' -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill(); $_.WaitForExit(3000) | Out-Null }
Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'MLV Drive' -ErrorAction SilentlyContinue
Remove-Item -Force (Join-Path ([Environment]::GetFolderPath('Programs')) 'MLV Drive.lnk') -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $InstallDir -ErrorAction SilentlyContinue
Remove-Item -Recurse -Force $DataDir -ErrorAction SilentlyContinue

Write-Host 'MLV Drive removed. WinFsp was left installed (remove it from Settings > Apps if you like).' -ForegroundColor Green
