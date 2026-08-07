# Builds every release artifact for CustomVoicedDialogue:
#   dist\CustomVoicedDialogue-Mod-<version>.zip   - FOMOD, drop into MO2/Vortex
#   dist\CustomVoicedDialogue-App-<version>.zip   - companion app (self-contained)
#
# Usage:  powershell -File build-package.ps1 [-Version 0.1.0] [-SkipPluginBuild]

param(
    [string]$Version = "0.1.0",
    [switch]$SkipPluginBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root "packaging\dist"
$staging = Join-Path $env:TEMP ("cvd-package-" + [Guid]::NewGuid().ToString("N"))

New-Item -ItemType Directory -Force $dist | Out-Null

# --- 1. Plugin build + guard verification -----------------------------------

if (-not $SkipPluginBuild) {
    Write-Host "== Building F4SE plugin (xmake releasedbg)" -ForegroundColor Cyan
    Push-Location (Join-Path $root "plugin")
    try {
        xmake f -p windows -a x64 -m releasedbg -y
        if ($LASTEXITCODE -ne 0) { throw "xmake configure failed" }
        xmake build
        if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
    }
    finally { Pop-Location }

    Write-Host "== Verifying hook sites (GuardCheck)" -ForegroundColor Cyan
    Push-Location (Join-Path $root "tools")
    try {
        dotnet run -c Release --project CvdTools -- guardcheck guardcheck.manifest.json
        if ($LASTEXITCODE -ne 0) { throw "GuardCheck FAILED - do not release" }
    }
    finally { Pop-Location }
}

# --- 2. Silence assets -------------------------------------------------------

Write-Host "== Generating silence assets" -ForegroundColor Cyan
& powershell -ExecutionPolicy Bypass -File (Join-Path $root "tools\SilenceGen\generate.ps1")

# --- 3. Mod zip (FOMOD) ------------------------------------------------------

Write-Host "== Assembling mod package" -ForegroundColor Cyan
$modStage = Join-Path $staging "mod"
New-Item -ItemType Directory -Force (Join-Path $modStage "fomod") | Out-Null
New-Item -ItemType Directory -Force (Join-Path $modStage "Data") | Out-Null

Copy-Item (Join-Path $root "packaging\fomod\*") (Join-Path $modStage "fomod") -Recurse
Copy-Item (Join-Path $root "Compile\*") (Join-Path $modStage "Data") -Recurse

$modZip = Join-Path $dist "CustomVoicedDialogue-Mod-$Version.zip"
if (Test-Path $modZip) { Remove-Item $modZip }
Compress-Archive -Path (Join-Path $modStage "*") -DestinationPath $modZip
Write-Host "   -> $modZip"

# --- 4. Companion app zip ----------------------------------------------------

Write-Host "== Publishing companion app (self-contained win-x64)" -ForegroundColor Cyan
$appStage = Join-Path $staging "app"
dotnet publish (Join-Path $root "server\CustomVoicedDialogue.App") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version `
    -o $appStage
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

dotnet publish (Join-Path $root "server\CustomVoicedDialogue.Updater") `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:Version=$Version `
    -o $appStage
if ($LASTEXITCODE -ne 0) { throw "updater publish failed" }

# Trim publish debris the user does not need.
Get-ChildItem $appStage -Filter *.pdb | Remove-Item

$appZip = Join-Path $dist "CustomVoicedDialogue-App-$Version.zip"
if (Test-Path $appZip) { Remove-Item $appZip }
Compress-Archive -Path (Join-Path $appStage "*") -DestinationPath $appZip
Write-Host "   -> $appZip"

Remove-Item $staging -Recurse -Force
Write-Host "== Done" -ForegroundColor Green
