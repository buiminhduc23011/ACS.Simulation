<#
.SYNOPSIS
    Build and package ACS Simulator (Backend + Web) into a distributable folder.

.DESCRIPTION
    1. Builds the React frontend (acs.simulator.web) with npm.
    2. Publishes the .NET backend (ACS.Simulator.API) in Release mode.
    3. Copies the frontend build output into server/wwwroot.
    4. Copies simulator deployment scripts into the output folder.

.PARAMETER OutputDir
    Path for the packaged output. Default: dist-simulator

.PARAMETER Configuration
    .NET build configuration. Default: Release

.PARAMETER SelfContained
    Publish as a self-contained executable (no .NET runtime required on target).

.PARAMETER Runtime
    RID when publishing self-contained. e.g. win-x64, linux-x64, linux-arm64
#>

param(
    [string]$OutputDir = 'dist-simulator',
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$SimRoot = $ScriptDir | Split-Path -Parent
$FrontendPath = Join-Path $SimRoot 'acs.simulator.web'
$BackendPath = Join-Path $SimRoot 'ACS.Simulator.API\ACS.Simulator.API.csproj'
$OutPath = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $SimRoot $OutputDir }
$ServerOut = Join-Path $OutPath 'server'
$WebDest = Join-Path $ServerOut 'wwwroot'

Write-Host ''
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host '|        ACS Simulator - Build and Package Script      |' -ForegroundColor Cyan
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "  Simulation  : $SimRoot"
Write-Host "  Output      : $OutPath"
Write-Host "  Config      : $Configuration"
if ($SelfContained) {
    Write-Host "  Mode        : Self-contained ($Runtime)"
} else {
    Write-Host '  Mode        : Framework-dependent'
}
Write-Host ''

Write-Host '[1/4] Building simulator web...' -ForegroundColor Yellow
Push-Location $FrontendPath
try {
    if (-not (Test-Path 'node_modules')) {
        Write-Host '      Installing npm dependencies (this may take a while)...'
        npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }
    Write-Host '      Frontend build complete.' -ForegroundColor Green
} finally {
    Pop-Location
}

Write-Host ''
Write-Host '[2/4] Publishing simulator backend...' -ForegroundColor Yellow

if (Test-Path $ServerOut) { Remove-Item $ServerOut -Recurse -Force }

$publishArgs = @(
    'publish', $BackendPath,
    '-c', $Configuration,
    '-o', $ServerOut
)

if ($SelfContained) {
    $publishArgs += '--self-contained', 'true', '-r', $Runtime
} else {
    $publishArgs += '--self-contained', 'false'
}

dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Write-Host '      Backend publish complete.' -ForegroundColor Green

Write-Host ''
Write-Host '[3/4] Embedding simulator web into server/wwwroot...' -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $WebDest | Out-Null
Copy-Item -Path (Join-Path $FrontendPath 'dist\*') -Destination $WebDest -Recurse -Force
Write-Host '      Frontend embedded.' -ForegroundColor Green

Write-Host ''
Write-Host '[4/4] Copying simulator deployment scripts...' -ForegroundColor Yellow
foreach ($file in @('deploy-simulator.ps1', 'setup-simulator.ps1', 'setup-simulator.sh')) {
    $src = Join-Path $ScriptDir $file
    if (Test-Path $src) {
        Copy-Item $src -Destination $OutPath -Force
        Write-Host "      Copied $file" -ForegroundColor DarkGray
    } else {
        Write-Warning "Script not found, skipping: $src"
    }
}

if ($env:OS -ne 'Windows_NT') {
    chmod +x (Join-Path $OutPath 'setup-simulator.sh')
}

Write-Host ''
Write-Host '========================================================' -ForegroundColor Green
Write-Host '|               BUILD COMPLETE!                        |' -ForegroundColor Green
Write-Host '========================================================' -ForegroundColor Green
Write-Host ''
Write-Host "  Package location : $OutPath"
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Cyan
Write-Host "    Windows : cd `"$OutPath`""
Write-Host '              powershell -ExecutionPolicy Bypass -File deploy-simulator.ps1'
Write-Host "    Linux   : cd `"$OutPath`""
Write-Host '              chmod +x setup-simulator.sh && sudo ./setup-simulator.sh'
Write-Host ''
