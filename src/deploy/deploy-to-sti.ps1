#Requires -Version 5.1
<#
.SYNOPSIS
    Deploy ACS Simulator lên máy STI (Windows) qua SSH + SCP.

.DESCRIPTION
    1. Build package win-x64 (trừ khi -SkipBuild).
    2. Copy thư mục server lên staging trên STI.
    3. Trên STI: stop service -> backup data + appsettings.Production.json ->
       đổi server cũ thành server.old-<timestamp> -> đưa bản mới vào ->
       restore backup -> start service -> verify.

    Giữ nguyên appsettings.Production.json và server/data của máy STI.
    Chạy từ máy dev có SSH alias 'sti' (BatchMode, key auth).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy-to-sti.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy-to-sti.ps1 -SkipBuild
#>

param(
    [string]$SshHost = 'sti',
    [string]$InstallDir = 'C:\ACS.Simulator',
    [string]$PackageDir = '',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$SimRoot = $ScriptDir | Split-Path -Parent
if ([string]::IsNullOrWhiteSpace($PackageDir)) {
    $PackageDir = Join-Path $SimRoot 'dist-simulator'
}
$ServerLocal = Join-Path $PackageDir 'server'

function Invoke-Ssh([string]$RemoteCommand) {
    & ssh -o BatchMode=yes $SshHost $RemoteCommand
    if ($LASTEXITCODE -ne 0) { throw "SSH failed (exit $LASTEXITCODE): $RemoteCommand" }
}

if (-not $SkipBuild) {
    Write-Host '[1/4] Building simulator package (win-x64)...' -ForegroundColor Yellow
    $buildScript = Join-Path $ScriptDir 'build-simulator.ps1'
    & $buildScript -SkipInstaller
    if ($LASTEXITCODE -ne 0) { throw 'Build failed!' }
} else {
    Write-Host '[1/4] Skipping build (-SkipBuild).' -ForegroundColor DarkGray
}
if (-not (Test-Path (Join-Path $ServerLocal 'ACS.Simulator.API.exe'))) {
    throw "Package server not found: $ServerLocal (run build first)"
}

# 2. Transfer package lên staging trên STI.
Write-Host "`n[2/4] Transferring package to ${SshHost}..." -ForegroundColor Yellow
$staging = 'C:/Temp/simulator-update'
& ssh -o BatchMode=yes $SshHost "cmd /c if exist C:\Temp\simulator-update rmdir /s /q C:\Temp\simulator-update"
if ($LASTEXITCODE -ne 0) { throw 'Could not clean remote staging directory!' }
& scp -r -o BatchMode=yes "$ServerLocal" "${SshHost}:${staging}"
if ($LASTEXITCODE -ne 0) { throw 'SCP transfer failed!' }

# 3. Remote update script: tránh quoting lồng nhau bằng cách copy file script lên rồi chạy.
Write-Host "`n[3/4] Installing and restarting service on ${SshHost}..." -ForegroundColor Yellow
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$remoteScript = @"
`$ErrorActionPreference = 'Stop'
`$InstallDir = '$InstallDir'
`$ServiceName = 'ACS.Simulator'
`$stamp = '$stamp'
`$ServerDir = Join-Path `$InstallDir 'server'
`$StagingDir = 'C:\Temp\simulator-update'
`$BackupDir = Join-Path `$InstallDir "backup-`$stamp"
Write-Host "Stopping service `$ServiceName..."
& sc.exe stop `$ServiceName | Out-Null
`$waited = 0
while (`$waited -lt 60) {
    `$svc = Get-Service -Name `$ServiceName -ErrorAction SilentlyContinue
    if (`$null -eq `$svc -or `$svc.Status -eq 'Stopped') { break }
    Start-Sleep -Seconds 2
    `$waited += 2
}
New-Item -ItemType Directory -Force -Path `$BackupDir | Out-Null
if (Test-Path (Join-Path `$ServerDir 'data')) {
    Copy-Item (Join-Path `$ServerDir 'data') (Join-Path `$BackupDir 'data') -Recurse -Force
    Write-Host 'Backed up server/data.'
}
foreach (`$cfg in @('appsettings.Production.json')) {
    `$p = Join-Path `$ServerDir `$cfg
    if (Test-Path `$p) { Copy-Item `$p (Join-Path `$BackupDir `$cfg) -Force; Write-Host "Backed up `$(`$cfg)." }
}
if (Test-Path `$ServerDir) {
    Rename-Item -LiteralPath `$ServerDir -NewName "server.old-`$stamp" -Force
    Write-Host 'Moved old server aside.'
}
Move-Item -LiteralPath `$StagingDir -Destination `$ServerDir -Force
Write-Host 'New server in place.'
if (Test-Path (Join-Path `$BackupDir 'data')) {
    Copy-Item (Join-Path `$BackupDir 'data') (Join-Path `$ServerDir 'data') -Recurse -Force
    Write-Host 'Restored server/data.'
}
foreach (`$cfg in @('appsettings.Production.json')) {
    `$p = Join-Path `$BackupDir `$cfg
    if (Test-Path `$p) { Copy-Item `$p (Join-Path `$ServerDir `$cfg) -Force; Write-Host "Restored `$(`$cfg)." }
}
Write-Host "Starting service `$ServiceName..."
& sc.exe start `$ServiceName | Out-Null
Start-Sleep -Seconds 8
`$svc = Get-Service -Name `$ServiceName -ErrorAction SilentlyContinue
if (`$null -eq `$svc -or `$svc.Status -ne 'Running') { throw "Service not running after start (status: `$(if (`$svc) { `$svc.Status } else { 'Missing' }))" }
Write-Host 'Service is RUNNING.'
`$port = 9060
try {
    `$prod = Get-Content -LiteralPath (Join-Path `$ServerDir 'appsettings.Production.json') -Raw | ConvertFrom-Json
    if (`$prod.Urls -match ':(\d+)') { `$port = [int]`$Matches[1] }
} catch { }
`$ok = `$false
for (`$i = 0; `$i -lt 12; `$i++) {
    try {
        `$r = Invoke-WebRequest -Uri "http://127.0.0.1:`$port/api/pump/state" -TimeoutSec 5 -UseBasicParsing
        if (`$r.StatusCode -eq 200) { `$ok = `$true; break }
    } catch { Start-Sleep -Seconds 5 }
}
if (-not `$ok) { throw "API did not respond on port `$port (/api/pump/state)." }
Write-Host "API verified on port `$port (/api/pump/state -> 200)."
"@

$localRemote = Join-Path ([System.IO.Path]::GetTempPath()) "sti-update-$stamp.ps1"
$remoteScript | Set-Content -LiteralPath $localRemote -Encoding UTF8
try {
    & scp -o BatchMode=yes "$localRemote" "${SshHost}:C:/Temp/sti-apply-update.ps1"
    if ($LASTEXITCODE -ne 0) { throw 'SCP of remote script failed!' }
    Invoke-Ssh "powershell -NoProfile -ExecutionPolicy Bypass -File C:\Temp\sti-apply-update.ps1"
} finally {
    Remove-Item -LiteralPath $localRemote -Force -ErrorAction SilentlyContinue
    & ssh -o BatchMode=yes $SshHost "cmd /c del C:\Temp\sti-apply-update.ps1" | Out-Null
}

Write-Host "`n[4/4] Done." -ForegroundColor Green
Write-Host "Deployed to ${SshHost}:${InstallDir} (backup: backup-$stamp, old: server.old-$stamp)." -ForegroundColor Green
