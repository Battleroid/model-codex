# Fresh-machine setup: ensure the .NET 8 SDK is present, then build Release.
#   powershell -ExecutionPolicy Bypass -File scripts\setup.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

function Test-Dotnet8 {
  try { return (dotnet --list-sdks 2>$null | Select-String '^8\.') -ne $null } catch { return $false }
}

if (-not (Test-Dotnet8)) {
  Write-Host "Installing .NET 8 SDK via winget..." -ForegroundColor Cyan
  winget install --id Microsoft.DotNet.SDK.8 --silent --accept-source-agreements --accept-package-agreements
  if ($LASTEXITCODE -ne 0) { throw "winget install failed; install the .NET 8 SDK manually from https://dotnet.microsoft.com/download" }
}

Write-Host "Building Release..." -ForegroundColor Cyan
dotnet build (Join-Path $root "src\App\ModelCodex.App.csproj") -c Release
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Write-Host ""
Write-Host "Done. Launch with Run.bat (or scripts\publish.ps1 for a standalone build)." -ForegroundColor Green
