<#
.SYNOPSIS
    Build and package ACS Simulator (Backend + Web) into a distributable folder.

.DESCRIPTION
    1. Builds the React frontend (acs.simulator.web) with npm.
    2. Publishes the .NET backend (ACS.Simulator.API) in Release mode.
    3. Copies the frontend build output into server/wwwroot.
    4. Copies simulator deployment scripts and compiles Setup.exe.

.PARAMETER OutputDir
    Path for the packaged output. Default: dist-simulator

.PARAMETER Configuration
    .NET build configuration. Default: Release

.PARAMETER SelfContained
    Publish self-contained (default true) so the target does not need .NET installed.
    Dùng -SelfContained:$false nếu muốn framework-dependent.

.PARAMETER Runtime
    RID when publishing self-contained. e.g. win-x64, linux-x64, linux-arm64

.PARAMETER SkipInstaller
    Skip Inno Setup compilation.
#>

param(
    [string]$OutputDir = 'dist-simulator',
    [string]$Configuration = 'Release',
    [bool]$SelfContained = $true,
    [string]$Runtime = 'win-x64',
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'

$ScriptDir = $PSScriptRoot
$SimRoot = $ScriptDir | Split-Path -Parent
$FrontendPath = Join-Path $SimRoot 'acs.simulator.web'
$BackendPath = Join-Path $SimRoot 'ACS.Simulator.API\ACS.Simulator.API.csproj'
$OutPath = if ([System.IO.Path]::IsPathRooted($OutputDir)) { $OutputDir } else { Join-Path $SimRoot $OutputDir }
$ServerOut = Join-Path $OutPath 'server'
$WebDest = Join-Path $ServerOut 'wwwroot'

function Get-MsBuildProperty {
    param([string]$Path, [string]$Name)

    if (-not (Test-Path $Path)) { return $null }
    try {
        [xml]$xml = Get-Content -Path $Path -Raw
        $node = $xml.SelectSingleNode("//PropertyGroup/$Name")
        if ($node -and -not [string]::IsNullOrWhiteSpace($node.InnerText)) { return $node.InnerText.Trim() }
    } catch { }
    return $null
}

# AppVersion.props là nguồn version duy nhất cho app và Setup.exe.
$Version = Get-MsBuildProperty -Path (Join-Path $SimRoot 'AppVersion.props') -Name 'AcsSimulatorVersion'
if ([string]::IsNullOrWhiteSpace($Version)) {
    throw 'Cannot read AcsSimulatorVersion from AppVersion.props.'
}

Write-Host ''
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host '|        ACS Simulator - Build and Package Script      |' -ForegroundColor Cyan
Write-Host '========================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "  Simulation  : $SimRoot"
Write-Host "  Version     : $Version"
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
    # vite ghi cảnh báo ra stderr; hạ EAP để không dừng nhầm, fail-fast qua LASTEXITCODE.
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        if (-not (Test-Path 'node_modules')) {
            Write-Host '      Installing npm dependencies (this may take a while)...'
            npm ci
            if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
        }
        npm run build
        if ($LASTEXITCODE -ne 0) { throw 'npm run build failed' }
    } finally {
        $ErrorActionPreference = $prevEAP
    }
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

# Không đóng gói địa chỉ MQTT/ACS của máy dev; installer sẽ hỏi site.
Write-Host '      Sanitizing packaged appsettings.json...' -ForegroundColor DarkGray
$packagedSettingsPath = Join-Path $ServerOut 'appsettings.json'
if (Test-Path $packagedSettingsPath) {
    $settings = Get-Content -LiteralPath $packagedSettingsPath -Raw | ConvertFrom-Json

    if ($settings.PSObject.Properties['Urls']) { $settings.Urls = 'http://*:9060' }
    else { $settings | Add-Member -NotePropertyName Urls -NotePropertyValue 'http://*:9060' -Force }

    if (-not $settings.PSObject.Properties['Simulator'] -or $null -eq $settings.Simulator) {
        $settings | Add-Member -NotePropertyName Simulator -NotePropertyValue ([pscustomobject]@{}) -Force
    }
    foreach ($property in @(
        @{ Name = 'MqttHost'; Value = '127.0.0.1' },
        @{ Name = 'MqttPort'; Value = 1883 },
        @{ Name = 'AcsApiBaseUrl'; Value = 'http://localhost:9050' }
    )) {
        if ($settings.Simulator.PSObject.Properties[$property.Name]) {
            $settings.Simulator.($property.Name) = $property.Value
        } else {
            $settings.Simulator | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
        }
    }

    $settings | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $packagedSettingsPath -Encoding UTF8
}

$devSettings = Join-Path $ServerOut 'appsettings.Development.json'
if (Test-Path $devSettings) { Remove-Item -LiteralPath $devSettings -Force }

Write-Host ''
Write-Host '[3/4] Embedding simulator web into server/wwwroot...' -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path $WebDest | Out-Null
Copy-Item -Path (Join-Path $FrontendPath 'dist\*') -Destination $WebDest -Recurse -Force
Write-Host '      Frontend embedded.' -ForegroundColor Green

Write-Host ''
Write-Host '[4/4] Copying simulator deployment scripts...' -ForegroundColor Yellow
foreach ($file in @('deploy-simulator.ps1', 'setup-simulator.ps1', 'install-simulator-service.ps1', 'setup-simulator.sh')) {
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

$SetupExe = $null
if (-not $SkipInstaller -and $Runtime -like 'win-*') {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1

    if (-not $iscc) {
        Write-Host '  [SKIP] Inno Setup (ISCC.exe) not found. Install from https://jrsoftware.org/isdown.php' -ForegroundColor Yellow
    } else {
        $iss = Join-Path $ScriptDir 'installer\ACS.Simulator.Setup.iss'
        if (-not (Test-Path $iss)) { throw "Installer script not found: $iss" }

        $InstallerOut = Join-Path $SimRoot 'dist-installer'
        New-Item -ItemType Directory -Force -Path $InstallerOut | Out-Null
        & $iscc /DAppVersion="$Version" /DPackagePath="$OutPath" /O"$InstallerOut" $iss
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE." }

        $SetupExe = Get-ChildItem -Path $InstallerOut -Filter 'ACS-Simulator-*-Setup.exe' |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        Write-Host "  [OK] Installer: $($SetupExe.FullName)" -ForegroundColor Green
    }
}

Write-Host ''
Write-Host '========================================================' -ForegroundColor Green
Write-Host '|               BUILD COMPLETE!                        |' -ForegroundColor Green
Write-Host '========================================================' -ForegroundColor Green
Write-Host ''
Write-Host "  Package location : $OutPath"
if ($SetupExe) { Write-Host "  Installer        : $($SetupExe.FullName)" }
Write-Host ''
Write-Host '  Next steps:' -ForegroundColor Cyan
if ($SetupExe) {
    Write-Host "    Windows : chay `"$($SetupExe.FullName)`""
} else {
    Write-Host "    Windows : cd `"$OutPath`" ; powershell -ExecutionPolicy Bypass -File deploy-simulator.ps1"
}
Write-Host "    Linux   : cd `"$OutPath`" ; chmod +x setup-simulator.sh && sudo ./setup-simulator.sh"
Write-Host ''
