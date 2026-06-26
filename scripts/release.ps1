# Build a self-contained release and (optionally) publish it as a GitHub release.
#
#   scripts\release.ps1 -Version v0.1.0                 # build + zip only
#   scripts\release.ps1 -Version v0.1.0 -Publish        # build + zip + create GitHub release
#   scripts\release.ps1 -Version v0.1.0 -Publish -Draft # ...as a draft
#
# Pushing a v* tag also triggers the Release workflow on GitHub Actions, which does this
# automatically. This script is for cutting a release from your machine instead.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$NotesFile,
    [switch]$Publish,
    [switch]$Draft
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

# 1) build the self-contained app
& (Join-Path $PSScriptRoot "publish.ps1")

# 2) zip it
$zip = Join-Path $root ("model-codex-{0}-win-x64.zip" -f $Version)
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $root "publish\*") -DestinationPath $zip
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Packaged $zip ($mb MB)" -ForegroundColor Green

# 3) optionally cut the GitHub release
if ($Publish) {
    $gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
    if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
    if (-not (Test-Path $gh)) { throw "gh CLI not found; install it or run without -Publish." }

    $relArgs = @("release", "create", $Version, $zip, "--title", "Model Codex $Version")
    if ($NotesFile) { $relArgs += @("--notes-file", $NotesFile) }
    elseif ($Notes) { $relArgs += @("--notes", $Notes) }
    else            { $relArgs += "--generate-notes" }
    if ($Draft) { $relArgs += "--draft" }

    & $gh @relArgs
    Write-Host "Release $Version created." -ForegroundColor Green
} else {
    Write-Host "Built $zip. Re-run with -Publish to create the GitHub release (or push a v* tag)." -ForegroundColor Cyan
}
