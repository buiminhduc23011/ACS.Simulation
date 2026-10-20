#Requires -Version 5.1
<#
.SYNOPSIS
    Ghi cấu hình và cài ACS Simulator thành Windows Service.

.DESCRIPTION
    Dùng cho Setup.exe và wizard setup-simulator.ps1.
    Không hỏi tương tác; mọi giá trị nhận qua tham số.
#>

param(
    [string]$InstallDir = 'C:\ACS.Simulator',
    [int]$SimPort = 9060,
    [string]$MqttHost = '127.0.0.1',
    [int]$MqttPort = 1883,
    [string]$AcsApiBaseUrl = 'http://localhost:9050'
)

$ErrorActionPreference = 'Stop'

$ServiceName = 'ACS.Simulator'
$ServerDir = Join-Path $InstallDir 'server'
$AppSettings = Join-Path $ServerDir 'appsettings.Production.json'
$PackagedSettings = Join-Path $ServerDir 'appsettings.json'
$Executable = Join-Path $ServerDir 'ACS.Simulator.API.exe'
$LogDir = Join-Path $InstallDir 'logs'
$InstallLogPath = Join-Path $LogDir 'install-service.log'

function Stop-ServiceIfPresent {
    param([string]$Name)

    $service = Get-Service -Name $Name -ErrorAction SilentlyContinue
    if ($null -eq $service -or $service.Status -eq 'Stopped') { return }

    Stop-Service -Name $Name -Force -ErrorAction SilentlyContinue
    try { $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
}

function Invoke-Sc {
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & sc.exe @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($null -ne $output) { $output | ForEach-Object { Write-Host $_ } }
    if ($exitCode -ne 0 -and -not $AllowFailure) {
        throw "sc.exe $($Arguments -join ' ') failed with exit code $exitCode."
    }
}

function Set-ConfigProperty {
    param(
        [Parameter(Mandatory)][psobject]$Object,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Value
    )

    if ($Object.PSObject.Properties[$Name]) {
        $Object.$Name = $Value
    } else {
        $Object | Add-Member -MemberType NoteProperty -Name $Name -Value $Value -Force
    }
}

function Write-ProductionConfig {
    $config = $null
    foreach ($path in @($AppSettings, $PackagedSettings)) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        try {
            $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            break
        } catch {
            Write-Warning "Could not parse $path; trying the next config source."
        }
    }

    if ($null -eq $config) {
        $config = [pscustomobject]@{}
    }

    Set-ConfigProperty -Object $config -Name 'Urls' -Value "http://*:$SimPort"

    if (-not $config.PSObject.Properties['Simulator'] -or $null -eq $config.Simulator) {
        Set-ConfigProperty -Object $config -Name 'Simulator' -Value ([pscustomobject]@{})
    }

    Set-ConfigProperty -Object $config.Simulator -Name 'MqttHost' -Value $MqttHost
    Set-ConfigProperty -Object $config.Simulator -Name 'MqttPort' -Value ([int]$MqttPort)
    Set-ConfigProperty -Object $config.Simulator -Name 'AcsApiBaseUrl' -Value $AcsApiBaseUrl.TrimEnd('/')

    $config | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $AppSettings -Encoding UTF8
    Write-Host "Wrote simulator config: $AppSettings"
}

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$transcriptStarted = $false
try {
    Start-Transcript -Path $InstallLogPath -Force | Out-Null
    $transcriptStarted = $true
} catch {
    Write-Warning "Could not start transcript at '$InstallLogPath': $_"
}

try {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
                 [Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw 'install-simulator-service.ps1 must be run as Administrator.' }

    if ($SimPort -lt 1 -or $SimPort -gt 65535) { throw 'SimPort must be between 1 and 65535.' }
    if ($MqttPort -lt 1 -or $MqttPort -gt 65535) { throw 'MqttPort must be between 1 and 65535.' }
    if ([string]::IsNullOrWhiteSpace($MqttHost)) { throw 'MqttHost is required.' }
    if ([string]::IsNullOrWhiteSpace($AcsApiBaseUrl)) { throw 'AcsApiBaseUrl is required.' }
    if (-not (Test-Path -LiteralPath $ServerDir)) { throw "Server directory not found: $ServerDir" }
    if (-not (Test-Path -LiteralPath $Executable)) { throw "Executable not found: $Executable" }

    New-Item -ItemType Directory -Force -Path (Join-Path $ServerDir 'data') | Out-Null
    New-Item -ItemType Directory -Force -Path (Join-Path $ServerDir 'logs') | Out-Null

    Write-ProductionConfig
    Stop-ServiceIfPresent $ServiceName

    $binPath = "`"$Executable`""
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Invoke-Sc @('config', $ServiceName, 'binPath=', $binPath, 'start=', 'auto') -AllowFailure
    } else {
        Invoke-Sc @('create', $ServiceName, 'binPath=', $binPath, 'start=', 'auto', 'DisplayName=', 'ACS Simulator')
    }

    Invoke-Sc @('description', $ServiceName, 'ACS Simulator API and web UI') -AllowFailure
    Invoke-Sc @('failure', $ServiceName, 'reset=', '86400', 'actions=', 'restart/5000/restart/10000/restart/30000/restart/60000/restart/120000') -AllowFailure
    Invoke-Sc @('failureflag', $ServiceName, '1') -AllowFailure

    Invoke-Sc @('start', $ServiceName) -AllowFailure
    Start-Sleep -Seconds 2
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service -and $service.Status -eq 'Running') {
        Write-Host "Service '$ServiceName' is running."
    } else {
        $status = if ($service) { $service.Status } else { 'Missing' }
        Write-Warning "Service status after start: $status. Check $InstallLogPath and $ServerDir\logs."
    }

    Write-Host "Simulator URL: http://localhost:$SimPort"
    exit 0
} catch {
    Write-Error $_
    exit 1
} finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}
