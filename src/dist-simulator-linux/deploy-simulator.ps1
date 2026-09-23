#Requires -Version 5.1
param(
    [string]$InstallDir = 'C:\ACS.Simulator'
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
             [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host ''
    Write-Host '  Error: This script must be run as Administrator.' -ForegroundColor Red
    Write-Host "  Right-click PowerShell and select 'Run as administrator'" -ForegroundColor Yellow
    exit 1
}

$setupScript = Join-Path $ScriptDir 'setup-simulator.ps1'
if (-not (Test-Path $setupScript)) {
    Write-Host "  [FAIL] setup-simulator.ps1 not found at: $setupScript" -ForegroundColor Red
    exit 1
}

& powershell -ExecutionPolicy Bypass -File $setupScript -InstallDir $InstallDir
