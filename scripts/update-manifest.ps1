<#
.SYNOPSIS
  Lädt alle Modelle aus models/manifest.json, trägt exakte Größe und SHA-256 ein und kann
  optional ein Offline-Modellpaket (ZIP) erzeugen bzw. die Modelle lokal installieren.
.EXAMPLE
  ./scripts/update-manifest.ps1                 # nur Prüfsummen/Größen eintragen
  ./scripts/update-manifest.ps1 -Pack           # zusätzlich artifacts/HILDE-Modelle-<stufe>.zip
#>
param([switch]$Pack, [string]$Cache = "$PSScriptRoot\..\artifacts\model-cache")
$ErrorActionPreference = 'Stop'
$path = Join-Path $PSScriptRoot '..\models\manifest.json'
$m = Get-Content $path -Raw | ConvertFrom-Json
New-Item -ItemType Directory -Force $Cache | Out-Null
foreach ($e in $m.models) {
  $file = Join-Path $Cache $e.fileName
  if (-not (Test-Path $file)) {
    Write-Host "Lade $($e.displayName) …"
    Invoke-WebRequest -Uri $e.url -OutFile "$file.part"
    Move-Item "$file.part" $file -Force
  }
  $e.sha256 = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
  $e.sizeBytes = (Get-Item $file).Length
  Write-Host ("{0,-34} {1,14:N0} Bytes  {2}" -f $e.id, $e.sizeBytes, $e.sha256)
}
$m.updated = (Get-Date -Format 'yyyy-MM-dd')
$m.PSObject.Properties.Remove('note')
$m | ConvertTo-Json -Depth 6 | Set-Content $path -Encoding utf8
Write-Host "Manifest aktualisiert. Lizenzstatus bitte manuell auf 'geprüft …' setzen."

if ($Pack) {
  foreach ($tier in 'light', 'standard') {
    $files = $m.models | Where-Object { $_.tiers.Count -eq 0 -or $_.tiers -contains $tier } | ForEach-Object { Join-Path $Cache $_.fileName }
    $zip = Join-Path $PSScriptRoot "..\artifacts\HILDE-Modelle-$tier.zip"
    Compress-Archive -Path $files -DestinationPath $zip -CompressionLevel NoCompression -Force
    Write-Host "Offline-Paket: $zip"
  }
}
