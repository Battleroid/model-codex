# Produces a self-contained Windows build that needs NO .NET install to run.
# Output folder: <repo>\publish\  (run ModelCodex.App.exe inside it; zip the folder to share).
#
#   scripts\publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root "src\App\ModelCodex.App.csproj"
$out  = Join-Path $root "publish"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Publishing self-contained win-x64 build..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

Write-Host ""
Write-Host "Done. Run: $out\ModelCodex.App.exe" -ForegroundColor Green
Write-Host "Zip the publish\ folder to share (the user still needs a Marathon install for the Oodle DLL)." -ForegroundColor Green
