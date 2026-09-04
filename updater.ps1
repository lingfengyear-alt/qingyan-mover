param(
  [Parameter(Mandatory=$true)][string]$ZipPath,
  [Parameter(Mandatory=$true)][string]$TargetDir,
  [Parameter(Mandatory=$true)][int]$ProcessId
)
$ErrorActionPreference = 'Stop'
try {
  Wait-Process -Id $ProcessId -Timeout 60 -ErrorAction SilentlyContinue
  $stage = Join-Path $env:TEMP ("qingyan-update-" + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Force $stage | Out-Null
  Expand-Archive -LiteralPath $ZipPath -DestinationPath $stage -Force
  Get-ChildItem -LiteralPath $stage -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($stage.Length).TrimStart('\')
    if ($relative -notin @('config.json','accounts.csv') -and
        $relative -notlike 'data\*' -and
        $relative -notlike 'chrome-profile\*' -and
        $relative -notlike 'edge-profile\*') {
      $destination = Join-Path $TargetDir $relative
      New-Item -ItemType Directory -Force (Split-Path $destination) | Out-Null
      Copy-Item -LiteralPath $_.FullName -Destination $destination -Force
    }
  }
  Remove-Item -LiteralPath $stage -Recurse -Force
  Remove-Item -LiteralPath $ZipPath -Force -ErrorAction SilentlyContinue
  Start-Process -FilePath (Join-Path $TargetDir 'QingyanMover.exe') -WorkingDirectory $TargetDir
} catch {
  Add-Content -LiteralPath (Join-Path $TargetDir 'update-error.log') -Value ("$(Get-Date -Format o) $($_.Exception.Message)")
}
