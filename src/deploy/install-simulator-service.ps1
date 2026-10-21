#Requires -Version 5.1
<#
.SYNOPSIS
    Ghi cấu hình và cài ACS Simulator thành Windows Service.

.DESCRIPTION
    Dùng cho Setup.exe và wizard setup-simulator.ps1.
    Không hỏi tương tác; mọi giá trị nhận qua tham số.
    Viết theo mẫu Z129 (deploy/windows/install-service.ps1): log file thay vì
    Start-Transcript, New-Service trước + fallback sc.exe quote chuẩn,
    mọi bước phụ đều try/catch để không làm fail oan.
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
$ServiceDisplayName = 'ACS Simulator'
$ServerDir = Join-Path $InstallDir 'server'
$AppSettings = Join-Path $ServerDir 'appsettings.Production.json'
$PackagedSettings = Join-Path $ServerDir 'appsettings.json'
$Executable = Join-Path $ServerDir 'ACS.Simulator.API.exe'
$LogDir = Join-Path $InstallDir 'logs'
$InstallLogPath = Join-Path $LogDir 'install-service.log'

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-InstallLog([string]$Message) {
    $timestamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $line = "[$timestamp] $Message"
    Write-Host $line
    try {
        Add-Content -LiteralPath $InstallLogPath -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue
    } catch { }
}

Write-InstallLog '=========================================================='
Write-InstallLog ' Installing ACS Simulator Windows Service'
Write-InstallLog " Time: $(Get-Date)"
Write-InstallLog " InstallDir: $InstallDir"
Write-InstallLog " ServiceName: $ServiceName"
Write-InstallLog " SimPort: $SimPort"
Write-InstallLog " MqttHost: $MqttHost"
Write-InstallLog " MqttPort: $MqttPort"
Write-InstallLog " ServerExe: $Executable"
Write-InstallLog '=========================================================='

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
            Write-InstallLog "WARNING: Could not parse $path; trying the next config source."
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
    Write-InstallLog "Wrote simulator config: $AppSettings"
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

    # 1. Stop existing service if present (do not fail if it is stuck).
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -ne $existing) {
        Write-InstallLog "Existing service '$ServiceName' found. Status: $($existing.Status). Stopping..."
        try {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        } catch {
            Write-InstallLog "WARNING stopping service: $_"
        }

        # Update ImagePath directly (giống Z129), fallback sc config quote chuẩn.
        try {
            Set-ItemProperty -Path "HKLM:\System\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value "`"$Executable`"" -Type ExpandString
            Set-ItemProperty -Path "HKLM:\System\CurrentControlSet\Services\$ServiceName" -Name Description -Value 'ACS Simulator API and web UI' -ErrorAction SilentlyContinue
            Write-InstallLog 'Updated service binary path in registry.'
        } catch {
            Write-InstallLog "Registry update failed ($_); falling back to sc config..."
            cmd.exe /c "sc config `"$ServiceName`" binPath= `"\`"$Executable\`"`" start= auto DisplayName= `"$ServiceDisplayName`""
        }
    } else {
        # 2. Register new service: New-Service trước, fallback sc create (DisplayName có quote).
        Write-InstallLog "Registering new Windows Service '$ServiceName'..."
        try {
            New-Service -Name $ServiceName `
                -BinaryPathName "`"$Executable`"" `
                -DisplayName $ServiceDisplayName `
                -Description 'ACS Simulator API and web UI' `
                -StartupType Automatic | Out-Null
            Write-InstallLog 'New-Service registered successfully.'
        } catch {
            Write-InstallLog "New-Service failed ($_); falling back to sc create..."
            cmd.exe /c "sc create `"$ServiceName`" binPath= `"\`"$Executable\`"`" start= auto DisplayName= `"$ServiceDisplayName`""
            if ($LASTEXITCODE -ne 0) { throw "sc create failed with exit code $LASTEXITCODE." }
        }
    }

    # 3. Description + recovery actions (không fail installer nếu lỗi).
    try {
        cmd.exe /c "sc description `"$ServiceName`" `"ACS Simulator API and web UI`"" | Out-Null
    } catch {
        Write-InstallLog "WARNING setting description: $_"
    }
    try {
        cmd.exe /c "sc failure `"$ServiceName`" reset= 86400 actions= restart/5000/restart/10000/restart/60000" | Out-Null
        Write-InstallLog 'Configured service failure recovery actions.'
    } catch {
        Write-InstallLog "WARNING setting failure actions: $_"
    }

    # 4. Firewall (parity với Z129, không fail installer).
    try {
        netsh advfirewall firewall delete rule name="$ServiceDisplayName HTTP" 2>$null | Out-Null
        netsh advfirewall firewall add rule name="$ServiceDisplayName HTTP" dir=in action=allow protocol=TCP localport=$SimPort | Out-Null
        Write-InstallLog "Configured Windows Firewall rule for TCP port $SimPort."
    } catch {
        Write-InstallLog "WARNING adding firewall rule: $_"
    }

    # 5. Start service (warn-only để installer không lỗi oan khi cổng bận).
    Write-InstallLog "Starting service '$ServiceName'..."
    try {
        Start-Service -Name $ServiceName -ErrorAction Stop
        Start-Sleep -Seconds 2
        $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($service -and $service.Status -eq 'Running') {
            Write-InstallLog "Service '$ServiceName' is running."
        } else {
            $status = if ($service) { $service.Status } else { 'Missing' }
            Write-InstallLog "WARNING: Service status after start: $status. Check $InstallLogPath and $ServerDir\logs."
        }
    } catch {
        Write-InstallLog "WARNING: Could not start service (installer continues): $_"
        Write-InstallLog "Start it manually: Start-Service -Name '$ServiceName'. Check $ServerDir\logs."
    }

    Write-InstallLog "Simulator URL: http://localhost:$SimPort"
    Write-InstallLog 'Installation completed successfully.'
    exit 0
} catch {
    Write-InstallLog "FATAL ERROR during service installation: $_"
    Write-InstallLog "Stack trace: $($_.ScriptStackTrace)"
    exit 1
}
