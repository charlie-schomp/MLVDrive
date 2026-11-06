# SPDX-License-Identifier: GPL-2.0-or-later
# Copyright (C) 2026 charlie-schomp
# MLV Drive installer.
#
# Runs as the normal user. The only step that needs administrator rights is installing
# WinFsp (the virtual-drive driver), and Windows asks for that once, for that installer
# only. Nothing is compiled and nothing is downloaded except that one pinned, verified
# WinFsp release:
#   - via winget (which checks the package hash), or
#   - from WinFsp's GitHub release, accepted only if its SHA-256 matches the value
#     pinned below AND it carries a valid signature from WinFsp's publisher.
#
# MLV Drive itself goes to %LOCALAPPDATA%\Programs\MLV Drive (only you can write there),
# with a Start menu shortcut and a "start with Windows" entry for your account.

[CmdletBinding()]
param([switch]$NoAutostart, [switch]$NoLaunch)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 3.0

$WinFspVersion   = '2.1.25156'
$WinFspMsiUrl    = 'https://github.com/winfsp/winfsp/releases/download/v2.1/winfsp-2.1.25156.msi'
$WinFspMsiSha256 = '073a70e00f77423e34bed98b86e600def93393ba5822204fac57a29324db9f7a'
$WinFspSigner    = 'CN=NAVIMATICS LLC'

$PackageDir = Split-Path -Parent $PSScriptRoot
$InstallDir = Join-Path $env:LOCALAPPDATA 'Programs\MLV Drive'
$Exe        = Join-Path $InstallDir 'MLVDrive.exe'

function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

function Test-WinFsp {
    # (Strict mode: a missing key or value must be tested for, not read and compared.)
    foreach ($key in 'HKLM:\SOFTWARE\WOW6432Node\WinFsp', 'HKLM:\SOFTWARE\WinFsp') {
        if (-not (Test-Path $key)) { continue }
        $props = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -eq $props -or -not ($props.PSObject.Properties.Name -contains 'InstallDir')) { continue }
        $dir = $props.InstallDir
        if ($dir -and (Test-Path (Join-Path $dir 'bin\winfsp-*.dll'))) { return $true }
    }
    return $false
}

function Install-WinFspWithWinget {
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if (-not $winget) { return $false }
    Say "Installing WinFsp $WinFspVersion with winget (Windows will ask for permission)..." Cyan
    & $winget.Source install --id WinFsp.WinFsp --exact --version $WinFspVersion --source winget `
        --accept-source-agreements --accept-package-agreements | Out-Host
    return (Test-WinFsp)
}

function Install-WinFspFromRelease {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('mlvdrive-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $msi = Join-Path $dir "winfsp-$WinFspVersion.msi"
    try {
        Say "Downloading WinFsp $WinFspVersion from its official release..." Cyan
        Invoke-WebRequest -UseBasicParsing -Uri $WinFspMsiUrl -OutFile $msi

        $hash = (Get-FileHash -Algorithm SHA256 -Path $msi).Hash.ToLowerInvariant()
        if ($hash -ne $WinFspMsiSha256) { throw "WinFsp download does not match the expected checksum ($hash). Not installing it." }
        $sig = Get-AuthenticodeSignature -FilePath $msi
        $signer = if ($null -ne $sig.SignerCertificate) { $sig.SignerCertificate.Subject } else { '' }
        if ($sig.Status -ne 'Valid' -or -not $signer.StartsWith($WinFspSigner)) {
            throw "WinFsp download is not validly signed by its publisher ($($sig.Status)). Not installing it."
        }
        Say 'Checksum and signature verified. Windows will now ask for permission to install WinFsp.' Green
        $p = Start-Process -FilePath 'msiexec.exe' -ArgumentList @('/i', "`"$msi`"", '/passive', '/norestart') -Verb RunAs -Wait -PassThru
        if ($p.ExitCode -notin 0, 3010, 1641) { throw "WinFsp installer exited with code $($p.ExitCode)." }
    }
    finally {
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }
    return (Test-WinFsp)
}

try {
    Say 'MLV Drive setup' White
    Say '---------------'

    $source = Join-Path $PackageDir 'MLVDrive.exe'
    foreach ($f in 'MLVDrive.exe', 'winfsp-msil.dll') {
        if (-not (Test-Path (Join-Path $PackageDir $f))) { throw "$f was not found next to this installer ($PackageDir)." }
    }

    if (Test-WinFsp) {
        Say 'WinFsp is already installed.' Green
    }
    else {
        Say 'MLV Drive needs WinFsp, a free, signed virtual-drive driver (https://winfsp.dev).'
        $ok = $false
        try { $ok = Install-WinFspWithWinget } catch { Say "winget could not install it: $($_.Exception.Message)" Yellow }
        if (-not $ok) { $ok = Install-WinFspFromRelease }
        if (-not $ok) { throw 'WinFsp is still not installed. If you declined the permission prompt, run this installer again.' }
        Say 'WinFsp installed.' Green
    }

    # stop a running copy so the exe can be replaced
    # (a tray app has no main window to close, so it is stopped; WinFsp unmounts its drive)
    Get-Process -Name 'MLVDrive' -ErrorAction SilentlyContinue | ForEach-Object {
        Say 'Stopping the running MLV Drive...'
        $_.Kill()
        if (-not $_.WaitForExit(10000)) { throw 'MLV Drive is still running; quit it from its tray icon and run setup again.' }
    }
    Start-Sleep -Milliseconds 500   # let Windows release the file

    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Force $source $Exe
    Copy-Item -Force (Join-Path $PackageDir 'winfsp-msil.dll') (Join-Path $InstallDir 'winfsp-msil.dll')
    foreach ($doc in 'README.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md') {
        $p = Join-Path $PackageDir $doc
        if (Test-Path $p) { Copy-Item -Force $p (Join-Path $InstallDir $doc) }
    }
    $lic = Join-Path $PackageDir 'licenses'
    if (Test-Path $lic) { Copy-Item -Recurse -Force $lic $InstallDir }
    Copy-Item -Force (Join-Path $PSScriptRoot 'Uninstall.ps1') (Join-Path $InstallDir 'Uninstall.ps1')
    Say "Installed to $InstallDir" Green

    $shell = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'MLV Drive.lnk'
    $lnk = $shell.CreateShortcut($startMenu)
    $lnk.TargetPath = $Exe
    $lnk.WorkingDirectory = $InstallDir
    $lnk.Description = 'Magic Lantern MLV clips as CinemaDNG for DaVinci Resolve'
    $lnk.Save()

    $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if ($NoAutostart) { Remove-ItemProperty -Path $run -Name 'MLV Drive' -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $run -Name 'MLV Drive' -Value "`"$Exe`""; Say 'MLV Drive will start with Windows (change this from its tray menu).' }

    if (-not $NoLaunch) { Start-Process -FilePath $Exe -WorkingDirectory $InstallDir }

    Say ''
    Say 'Done. MLV Drive is in the system tray (bottom right, next to the clock).' Green
    Say 'Connect the drive with your MLV clips, then in Resolve drag clips in from the M: drive.' Green
}
catch {
    Say ''
    Say ('Setup did not finish: ' + $_.Exception.Message) Red
    exit 1
}
