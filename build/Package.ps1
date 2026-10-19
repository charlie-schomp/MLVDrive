# Builds the release zip: tests, publishes one self-contained MLVDrive.exe, and lays out
#   MLV-Drive-<version>\  INSTALL.cmd  UNINSTALL.cmd  MLVDrive.exe  README.md  LICENSE
#                         installer\Install.ps1  installer\Uninstall.ps1  SHA256SUMS.txt
# Usage: powershell -File build\Package.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$proj = Get-Content 'src\MlvDrive.Windows\MlvDrive.Windows.csproj'
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "MLV-Drive-$version"
$out = Join-Path $root "artifacts\$name"
$zip = Join-Path $root "artifacts\$name.zip"

dotnet test tests\MlvDrive.Tests -c Release
if ($LASTEXITCODE) { throw 'tests failed' }

Remove-Item -Recurse -Force $out, $zip -ErrorAction SilentlyContinue
dotnet publish src\MlvDrive.Windows -c Release -o "$out\publish"
if ($LASTEXITCODE) { throw 'publish failed' }

Move-Item "$out\publish\MLVDrive.exe" "$out\MLVDrive.exe"
Move-Item "$out\publish\winfsp-msil.dll" "$out\winfsp-msil.dll"      # must sit beside the exe (see csproj)
$left = Get-ChildItem "$out\publish" -File | Where-Object { $_.Extension -ne '.pdb' }
if ($left) { throw "unexpected published files: $($left.Name -join ', ')" }
Remove-Item -Recurse -Force "$out\publish"
New-Item -ItemType Directory -Force "$out\installer" | Out-Null
Copy-Item installer\INSTALL.cmd, installer\UNINSTALL.cmd $out
Copy-Item installer\Install.ps1, installer\Uninstall.ps1 "$out\installer"
Copy-Item README.md, LICENSE $out

$sums = Get-ChildItem -Recurse -File $out | Sort-Object FullName | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant(), $_.FullName.Substring($out.Length + 1).Replace('\', '/')
}
Set-Content -Encoding ascii "$out\SHA256SUMS.txt" $sums

Compress-Archive -Path $out -DestinationPath $zip
Write-Host "$zip  ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
Get-Content "$out\SHA256SUMS.txt"
