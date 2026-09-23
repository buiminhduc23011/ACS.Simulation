#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

Write-Host "========================================================" -ForegroundColor Cyan
Write-Host "|    ACS Simulator - Home Server One-Click Deploy      |" -ForegroundColor Cyan
Write-Host "========================================================" -ForegroundColor Cyan
Write-Host ""

# 1. Build the Simulator
Write-Host "[1/3] Building Simulator for Linux..." -ForegroundColor Yellow
$buildScript = Join-Path $ScriptDir 'build-simulator.ps1'
& $buildScript -SelfContained -Runtime linux-x64 -OutputDir dist-simulator-linux
if ($LASTEXITCODE -ne 0) { throw "Build failed!" }

# 2. SCP to home-server
Write-Host "`n[2/3] Transferring package to home-server..." -ForegroundColor Yellow
$distPath = Join-Path $ScriptDir '..\dist-simulator-linux'
scp -r -o BatchMode=yes "$distPath" home-server:/tmp/simulator-update
if ($LASTEXITCODE -ne 0) { throw "SCP transfer failed!" }

# 3. Apply changes via SSH
Write-Host "`n[3/3] Installing and restarting service on home-server..." -ForegroundColor Yellow
$password = "66668888"
$sshCommand = @"
echo '$password' | sudo -S systemctl stop acs-simulator
echo '$password' | sudo -S cp -r /home/ducne/acs-simulator/dist-simulator/server/data /tmp/simulator-data-backup || true
echo '$password' | sudo -S rm -rf /home/ducne/acs-simulator/dist-simulator
echo '$password' | sudo -S mv /tmp/simulator-update /home/ducne/acs-simulator/dist-simulator
echo '$password' | sudo -S cp -r /tmp/simulator-data-backup /home/ducne/acs-simulator/dist-simulator/server/data || true
echo '$password' | sudo -S rm -rf /tmp/simulator-data-backup
echo '$password' | sudo -S chmod +x /home/ducne/acs-simulator/dist-simulator/server/ACS.Simulator.API
echo '$password' | sudo -S systemctl start acs-simulator
echo '$password' | sudo -S systemctl status acs-simulator --no-pager
"@

& ssh home-server $sshCommand

if ($LASTEXITCODE -eq 0) {
    Write-Host "`nSuccessfully updated and restarted ACS Simulator on home-server!" -ForegroundColor Green
} else {
    Write-Host "`nFailed to restart the simulator on home-server." -ForegroundColor Red
}
