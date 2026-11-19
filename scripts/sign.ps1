<#
.SYNOPSIS  Optionale Authenticode-Signatur (Zertifikat als Base64-Secret in der CI).
#>
param([string[]]$Files)
$ErrorActionPreference = 'Stop'
$pfx = Join-Path $env:RUNNER_TEMP 'hilde-sign.pfx'
[IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:SIGN_CERT))
$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter signtool.exe | Where-Object FullName -like '*x64*' | Select-Object -Last 1
foreach ($f in $Files) {
  & $signtool.FullName sign /f $pfx /p $env:SIGN_PASS /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $f
  if ($LASTEXITCODE -ne 0) { throw "Signatur fehlgeschlagen: $f" }
}
Remove-Item $pfx
