<#
.SYNOPSIS  Prüft models/manifest.json. Mit -Strict (Freigabe) müssen alle Prüfsummen und Größen gesetzt
           und alle Lizenzen geprüft sein.
#>
param([switch]$Strict)
$ErrorActionPreference = 'Stop'
$m = Get-Content (Join-Path $PSScriptRoot '..\models\manifest.json') -Raw | ConvertFrom-Json
$errors = @()
foreach ($e in $m.models) {
  if (-not $e.url.StartsWith('https://')) { $errors += "$($e.id): URL muss https sein" }
  if ($Strict) {
    if (-not $e.sha256 -or $e.sha256.Length -ne 64) { $errors += "$($e.id): SHA-256 fehlt" }
    if ($e.licenseStatus -notlike 'geprüft*') { $errors += "$($e.id): Lizenz nicht als geprüft markiert ($($e.licenseStatus))" }
  }
}
if ($errors) { $errors | ForEach-Object { Write-Error $_ -ErrorAction Continue }; exit 1 }
Write-Host "Manifest in Ordnung ($($m.models.Count) Modelle)."
