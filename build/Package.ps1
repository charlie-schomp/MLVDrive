# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 charlie-schomp
# Builds the release zip: tests, publishes one self-contained MLVDrive.exe, and lays out
#   MLV-Drive-<version>-windows-<arch>\  INSTALL.cmd  UNINSTALL.cmd  MLVDrive.exe  mlvdrive-cli.exe
#                         README.md  LICENSE  installer\Install.ps1  installer\Uninstall.ps1  SHA256SUMS.txt
# Usage: powershell -File build\Package.ps1 [-Rid win-arm64]

param([string]$Rid = 'win-x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$props = Get-Content 'Directory.Build.props'
$version = $props.Project.PropertyGroup.Version
$name = "MLV-Drive-$version-windows-$($Rid -replace '^win-', '')"
$out = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

dotnet test tests\MlvDrive.Tests -c Release
if ($LASTEXITCODE) { throw 'tests failed' }

Remove-Item -Recurse -Force $out, $zip -ErrorAction SilentlyContinue
dotnet publish src\MlvDrive.Windows -c Release -r $Rid -o "$out\publish"
if ($LASTEXITCODE) { throw 'publish failed' }
dotnet publish src\MlvDrive.Cli -c Release -r $Rid -o "$out\cli"
if ($LASTEXITCODE) { throw 'publish failed' }
Move-Item "$out\cli\mlvdrive-cli.exe" "$out\mlvdrive-cli.exe"
Remove-Item -Recurse -Force "$out\cli"

Move-Item "$out\publish\MLVDrive.exe" "$out\MLVDrive.exe"
Move-Item "$out\publish\winfsp-msil.dll" "$out\winfsp-msil.dll"      # must sit beside the exe (see csproj)
$left = Get-ChildItem "$out\publish" -File | Where-Object { $_.Extension -ne '.pdb' }
if ($left) { throw "unexpected published files: $($left.Name -join ', ')" }
Remove-Item -Recurse -Force "$out\publish"
New-Item -ItemType Directory -Force "$out\installer" | Out-Null
Copy-Item installer\INSTALL.cmd, installer\UNINSTALL.cmd $out
Copy-Item installer\Install.ps1, installer\Uninstall.ps1 "$out\installer"
Copy-Item README.md, LICENSE, THIRD-PARTY-NOTICES.md $out
New-Item -ItemType Directory -Force "$out\licenses" | Out-Null
Copy-Item licenses\* "$out\licenses"

$sums = Get-ChildItem -Recurse -File $out | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.FullName.Substring($out.Length + 1).Replace('\', '/')
}
Set-Content -Encoding ascii "$out\SHA256SUMS.txt" $sums

Compress-Archive -Path $out -DestinationPath $zip
Write-Host "$zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Get-Content "$out\SHA256SUMS.txt"
