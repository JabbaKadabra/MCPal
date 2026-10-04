<#
.SYNOPSIS
Installs the MCPal bridge as a Windows service. Run from the unpacked release archive in an elevated PowerShell.

.EXAMPLE
.\install.ps1 -ApiKey mcpal_xxxxxxxx_...

.DESCRIPTION
Files: <InstallDir>\mcpal-bridge.exe (binary), <DataDir>\mcpal.json (config, never overwritten), <DataDir>\mcp.json (your local
MCP servers in the .mcp.json format of Claude Code, never overwritten). The files can hold secrets,
so <DataDir> is readable only by Administrators, SYSTEM and the service account. The API key is stored in the service's
environment (registry key of the service, Administrators only). The service starts only when "mcpal-bridge check" passes.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'MCPal'),
    [string]$DataDir = (Join-Path $env:ProgramData 'MCPal'),
    [string]$ApiKey = $env:MCPAL_API_KEY,
    # The account the service runs as. Local stdio servers run as this account too and need access to what they use.
    [string]$ServiceAccount = 'LocalSystem',
    [string]$ServiceName = 'MCPalBridge'
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell (Run as administrator).'
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
foreach ($file in 'mcpal-bridge.exe', 'mcpal.example.json', 'mcp.example.json') {
    if (-not (Test-Path (Join-Path $here $file))) {
        throw "Missing $file next to the install script. Run it from the unpacked archive."
    }
}

$exe = Join-Path $InstallDir 'mcpal-bridge.exe'
$config = Join-Path $DataDir 'mcpal.json'
$servers = Join-Path $DataDir 'mcp.json'

# Stop a running bridge first, so the binary can be replaced.
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
Copy-Item -Force (Join-Path $here 'mcpal-bridge.exe') $exe
Write-Host "Installed $exe"

if (Test-Path $config) {
    Write-Host "Kept the existing $config."
} else {
    Copy-Item (Join-Path $here 'mcpal.example.json') $config
    Write-Host "Created $config from the example. Edit it: the MCPal server URL."
}

if (Test-Path $servers) {
    Write-Host "Kept the existing $servers."
} else {
    Copy-Item (Join-Path $here 'mcp.example.json') $servers
    Write-Host "Created $servers from the example. Replace it with your own MCP config (the mcpServers block, as in Claude Code's .mcp.json)."
}

# The config files may contain secrets: only Administrators, SYSTEM and the service account may read the data directory.
$grants = @('*S-1-5-32-544:(OI)(CI)F', '*S-1-5-18:(OI)(CI)F')
if ($ServiceAccount -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) {
    $grants += "${ServiceAccount}:(OI)(CI)RX"
}
& icacls.exe $DataDir /inheritance:r /grant:r @grants | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed for $DataDir" }

$binaryPath = "`"$exe`" run --config `"$config`""
if ($existing) {
    & sc.exe config $ServiceName binPath= $binaryPath | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed for $ServiceName" }
} else {
    $arguments = @{
        Name           = $ServiceName
        BinaryPathName = $binaryPath
        DisplayName    = 'MCPal Bridge'
        Description    = 'Outbound tunnel from this network to the MCPal server.'
        StartupType    = 'Automatic'
    }
    New-Service @arguments | Out-Null
}
if ($ServiceAccount -ne 'LocalSystem') {
    & sc.exe config $ServiceName obj= $ServiceAccount | Out-Null
}
# Restart after a crash: after 5 s, 5 s, then 60 s; the failure count resets after a day.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

if ($ApiKey) {
    # REG_MULTI_SZ "Environment" of the service: the service (not other users) sees the key.
    $key = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    Set-ItemProperty -Path $key -Name Environment -Type MultiString -Value @("MCPAL_API_KEY=$ApiKey")
    Write-Host 'Stored the API key in the service environment.'
} else {
    Write-Warning 'No API key given. Run the script again with -ApiKey mcpal_..., or set MCPAL_API_KEY for the service yourself.'
}

Write-Host 'Checking the configuration and the local servers ...'
$env:MCPAL_API_KEY = $ApiKey
& $exe check --config $config
if ($LASTEXITCODE -ne 0) {
    Write-Error "The check failed, so the service was not started. Fix $config, then run: Start-Service $ServiceName"
    exit 1
}

Start-Service -Name $ServiceName
Write-Host "MCPal bridge started. Logs: Event Viewer > Windows Logs > Application (source '$ServiceName')."
