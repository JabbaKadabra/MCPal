<#
.SYNOPSIS
Removes the MCPal bridge service and binary. The configuration in the data directory stays unless -RemoveData is given.
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'MCPal'),
    [string]$DataDir = (Join-Path $env:ProgramData 'MCPal'),
    [string]$ServiceName = 'MCPalBridge',
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script in an elevated PowerShell (Run as administrator).'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $ServiceName | Out-Null
}

Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $InstallDir
if ($RemoveData) {
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $DataDir
    Write-Host 'Removed the bridge and its configuration.'
} else {
    Write-Host "Removed the bridge. Kept $DataDir (use -RemoveData to delete it)."
}
