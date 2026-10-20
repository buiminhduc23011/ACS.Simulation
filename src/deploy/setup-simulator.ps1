#Requires -Version 5.1
param(
    [string]$InstallDir = 'C:\ACS.Simulator'
)

$ErrorActionPreference = 'Stop'
$Host.UI.RawUI.WindowTitle = 'ACS Simulator Setup'

function Read-Input {
    param([string]$Prompt, [string]$Default = '')
    $display = if ($Default) { "$Prompt [$Default]" } else { $Prompt }
    Write-Host "  $display : " -NoNewline -ForegroundColor White
    $val = Read-Host
    if ([string]::IsNullOrWhiteSpace($val)) { return $Default }
    return $val.Trim()
}

function Get-PrimaryIPv4 {
    try {
        $defaultRoute = Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue |
            Sort-Object RouteMetric |
            Select-Object -First 1
        if ($defaultRoute) {
            $ip = Get-NetIPAddress -InterfaceIndex $defaultRoute.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
                Where-Object { $_.IPAddress -and $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
                Select-Object -ExpandProperty IPAddress -First 1
            if ($ip) { return $ip }
        }
    } catch {
    }

    return '127.0.0.1'
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
             [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host '  [FAIL] This script must be run as Administrator.' -ForegroundColor Red
    exit 1
}

$SourceDir = $PSScriptRoot
$serviceName = 'ACS.Simulator'
$existingService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existingService -and $existingService.Status -eq 'Running') {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
}

$resolvedSource = (Resolve-Path $SourceDir).Path.TrimEnd('\')
$resolvedInstall = $InstallDir.TrimEnd('\')
if ($resolvedSource -ne $resolvedInstall) {
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force $InstallDir | Out-Null
    }
    robocopy $resolvedSource $resolvedInstall /E /IS /IT /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -gt 7) {
        Write-Host '  [FAIL] Copy failed. Check folder permissions.' -ForegroundColor Red
        exit 1
    }
}

$SetupDir = $InstallDir
$ServerDir = Join-Path $SetupDir 'server'
$AppSettings = Join-Path $ServerDir 'appsettings.Production.json'

if (-not (Test-Path $ServerDir)) {
    Write-Host "  [FAIL] Cannot find 'server' folder at: $ServerDir" -ForegroundColor Red
    exit 1
}

$defPort = '9060'
$defMqttHost = '127.0.0.1'
$defMqttPort = '1883'
$defAcsApi = 'http://localhost:9050'

if (Test-Path $AppSettings) {
    try {
        $existing = Get-Content $AppSettings -Raw | ConvertFrom-Json
        if ($existing.Urls) { $defPort = ($existing.Urls -replace 'http://\*:', '') }
        if ($existing.Simulator.MqttHost) { $defMqttHost = $existing.Simulator.MqttHost }
        if ($existing.Simulator.MqttPort) { $defMqttPort = [string]$existing.Simulator.MqttPort }
        if ($existing.Simulator.AcsApiBaseUrl) { $defAcsApi = $existing.Simulator.AcsApiBaseUrl }
    } catch {
    }
}

Write-Host ''
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host '|           ACS Simulator - Setup and Configuration            |' -ForegroundColor Cyan
Write-Host '================================================================' -ForegroundColor Cyan
Write-Host ''
Write-Host "  Install Dir : $InstallDir" -ForegroundColor Gray
Write-Host ''

$simPort = Read-Input 'Simulator Port' $defPort
$mqttHost = Read-Input 'MQTT Broker Address' $defMqttHost
$mqttPort = Read-Input 'MQTT Broker Port' $defMqttPort
$acsApiBaseUrl = Read-Input 'ACS API Base URL' $defAcsApi

$installScript = Join-Path $SetupDir 'install-simulator-service.ps1'
if (-not (Test-Path -LiteralPath $installScript)) {
    Write-Host "  [FAIL] install-simulator-service.ps1 not found at: $installScript" -ForegroundColor Red
    exit 1
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $installScript `
    -InstallDir $InstallDir `
    -SimPort $simPort `
    -MqttHost $mqttHost `
    -MqttPort $mqttPort `
    -AcsApiBaseUrl $acsApiBaseUrl
if ($LASTEXITCODE -ne 0) {
    Write-Host "  [FAIL] Simulator service setup failed with exit code $LASTEXITCODE." -ForegroundColor Red
    exit $LASTEXITCODE
}

$hostIp = Get-PrimaryIPv4
Write-Host ''
Write-Host "  Simulator URL : http://${hostIp}:$simPort" -ForegroundColor Cyan
Write-Host "  Swagger UI    : http://${hostIp}:$simPort/swagger" -ForegroundColor Cyan
Write-Host "  Config file   : $AppSettings" -ForegroundColor Gray
Write-Host ''
