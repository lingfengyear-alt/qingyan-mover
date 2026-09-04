$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$staging = Join-Path $PSScriptRoot 'staging'
$dist = Join-Path $PSScriptRoot 'dist'
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item $staging -ItemType Directory -Force | Out-Null
New-Item $dist -ItemType Directory -Force | Out-Null

dotnet restore (Join-Path $root 'QingyanMover.csproj')
if ($LASTEXITCODE -ne 0) { throw 'NuGet restore failed; installer was not created.' }
dotnet publish (Join-Path $root 'QingyanMover.csproj') -c Release -r win-x64 --self-contained true `
  /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $staging --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed; installer was not created.' }

Copy-Item (Join-Path $root 'config.example.json') (Join-Path $staging 'config.json')
Copy-Item (Join-Path $root 'accounts.example.csv') (Join-Path $staging 'accounts.csv')
Copy-Item (Join-Path $root 'README.md') (Join-Path $staging 'README.txt')
Copy-Item (Join-Path $root 'updater.ps1') (Join-Path $staging 'updater.ps1')
Remove-Item (Join-Path $staging '*.pdb') -Force -ErrorAction SilentlyContinue

$iscc = @(
  (Get-Command iscc -ErrorAction SilentlyContinue).Source,
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($iscc) {
  & $iscc (Join-Path $PSScriptRoot 'qingyan-mover.iss')
  Write-Host "Installer created in $dist"
} else {
  $zip = Join-Path $dist 'QingyanMover-portable.zip'
  Remove-Item $zip -Force -ErrorAction SilentlyContinue
  Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip
  Write-Warning 'Inno Setup not found; portable ZIP created. Install Inno Setup 6 and rerun this script for Setup.exe.'
  Write-Host "Portable package created: $zip"
}
