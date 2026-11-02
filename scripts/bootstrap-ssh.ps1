<#
.SYNOPSIS
  First thing to run on a fresh Windows install: enables the OpenSSH server and trusts the Mac's deploy key,
  so everything after this can be done remotely. Safe to re-run.

.DESCRIPTION
  Usually run via scripts/serve-bootstrap.sh, which prints a one-liner to type into an admin PowerShell:
    irm http://<mac-ip>:8899/go.txt | iex
#>
param([Parameter(Mandatory)][string]$PublicKey)

$ErrorActionPreference = "Stop"
function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this from an *administrator* PowerShell (right-click Start > Terminal (Admin))." }

Step "Installing OpenSSH server (can take a few minutes)"
$capability = Get-WindowsCapability -Online -Name "OpenSSH.Server*"
if ($capability.State -ne "Installed") { Add-WindowsCapability -Online -Name $capability.Name | Out-Null }
Set-Service sshd -StartupType Automatic
Start-Service sshd

Step "Using PowerShell as the SSH shell"
New-ItemProperty -Path "HKLM:\SOFTWARE\OpenSSH" -Name DefaultShell -PropertyType String -Force `
    -Value "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" | Out-Null

Step "Trusting the deploy key"
# Admin accounts read keys from this shared file, not ~\.ssh\authorized_keys.
$keysFile = Join-Path $env:ProgramData "ssh\administrators_authorized_keys"
if (-not (Test-Path $keysFile) -or -not (Select-String -Path $keysFile -SimpleMatch $PublicKey -Quiet)) {
    Add-Content -Path $keysFile -Value $PublicKey -Encoding ascii
}
# sshd ignores the file unless only Administrators and SYSTEM can access it. SIDs keep this locale-independent.
icacls $keysFile /inheritance:r /grant "*S-1-5-32-544:F" /grant "*S-1-5-18:F" | Out-Null

Step "Network: home WiFi as Private, SSH allowed"
Get-NetConnectionProfile | Where-Object NetworkCategory -ne "DomainAuthenticated" | Set-NetConnectionProfile -NetworkCategory Private
if (-not (Get-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name "OpenSSH-Server-In-TCP" -DisplayName "OpenSSH Server (sshd)" -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Private | Out-Null
}

$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" } | ForEach-Object IPAddress
Write-Host "`nSSH is ready. Tell Claude:" -ForegroundColor Green
Write-Host "  user: $env:USERNAME"
Write-Host "  ip:   $($ips -join ', ')"
Write-Host "  name: $env:COMPUTERNAME"
