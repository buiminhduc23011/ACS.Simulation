#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

Write-Host "Connecting to home-server to update the simulator..." -ForegroundColor Cyan

$password = "66668888"
$sshCommand = @"
echo '$password' | sudo -S systemctl stop acs-simulator
echo '$password' | sudo -S rm -rf /home/ducne/acs-simulator/dist-simulator
echo '$password' | sudo -S mv /tmp/simulator-update /home/ducne/acs-simulator/dist-simulator
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
