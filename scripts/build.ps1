<#
.SYNOPSIS  Lokaler Komplett-Build wie in der CI (Windows, .NET 10 SDK, Node 22).
#>
param([string]$Version = '0.2.0', [switch]$SkipTests, [switch]$SkipInstaller)
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
Push-Location ui; npm install --no-audit --no-fund; npx tsc -p tsconfig.json; Pop-Location
dotnet build Hilde.sln -c Release -p:HildeVersion=$Version
if (-not $SkipTests) { dotnet test tests/Unit/Hilde.Tests.Unit.csproj -c Release --no-build }
Remove-Item artifacts/app -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish src/Hilde.EngineHost/Hilde.EngineHost.csproj -c Release -r win-x64 --self-contained -o artifacts/app -p:HildeVersion=$Version
dotnet publish src/Hilde.Desktop/Hilde.Desktop.csproj -c Release -r win-x64 --self-contained -o artifacts/app -p:HildeVersion=$Version
if (-not $SkipInstaller) {
  if (-not (Test-Path installer/Bundle/WebView2Bootstrapper.exe)) {
    Invoke-WebRequest -Uri 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile installer/Bundle/WebView2Bootstrapper.exe
  }
  dotnet build installer/Bundle/Hilde.Bundle.wixproj -c Release -p:HildeVersion=$Version -p:AppDir="$PWD\artifacts\app"
  Get-ChildItem installer/Bundle/bin -Recurse -Filter HILDE-Setup.exe | ForEach-Object { Write-Host "Setup: $($_.FullName)" }
}
