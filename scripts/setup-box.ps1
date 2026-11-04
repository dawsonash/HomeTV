#Requires -RunAsAdministrator
<#
.SYNOPSIS
  Provisions the TV box for HomeTV. Safe to re-run.

.DESCRIPTION
  Non-interactive, so it can run over SSH as the admin account (after bootstrap-ssh.ps1):
    powershell -ExecutionPolicy Bypass -File setup-box.ps1

  The tv account has no password: it's a non-admin couch account, and Windows refuses network logons to
  blank-password accounts, so this exposes nothing over the LAN. It also lets auto-login be set without
  storing a secret.

  Does NOT: debloat Windows (see docs/box-setup.md), change BIOS settings, or replace the tv user's shell
  (that's -RegisterShell, once HomeTV has been deployed and tested).

.PARAMETER RegisterShell
  Makes HomeTV.exe the tv user's shell instead of Explorer. Only use after a deploy works.

.PARAMETER UnregisterShell
  Restores Explorer as the tv user's shell.

.PARAMETER SkipAdBlock
  Don't force-install uBlock Origin Lite and SponsorBlock into Edge. Remove them later by deleting their values
  under HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist.
#>
param(
    [string]$TvUser = "tv",
    [string]$ComputerName = "hometv",
    [string]$InstallDir = "C:\HomeTV",
    [switch]$RegisterShell,
    [switch]$UnregisterShell,
    [switch]$SkipAdBlock
)

$ErrorActionPreference = "Stop"
function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

$exe = Join-Path $InstallDir "app\HomeTV.exe"
$shellKey = "Software\Microsoft\Windows NT\CurrentVersion\Winlogon"

# Per-user shell lives in the tv user's own registry hive (HKCU while they're logged in).
function Set-TvUserShell([string]$value) {
    $sid = (New-Object System.Security.Principal.NTAccount($TvUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $loaded = Test-Path "Registry::HKEY_USERS\$sid"
    if (-not $loaded) {
        $profile = Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid
        if (-not $profile) { throw "'$TvUser' has never signed in, so there's no profile yet. Reboot (auto-login) first." }
        $hive = Join-Path $profile.LocalPath "NTUSER.DAT"
        reg load "HKU\$sid" $hive | Out-Null
    }
    try {
        $path = "Registry::HKEY_USERS\$sid\$shellKey"
        if ($value) { New-ItemProperty -Path $path -Name Shell -Value $value -PropertyType String -Force | Out-Null }
        else { Remove-ItemProperty -Path $path -Name Shell -ErrorAction SilentlyContinue }
    }
    finally {
        if (-not $loaded) { [gc]::Collect(); reg unload "HKU\$sid" | Out-Null }
    }
}

if ($RegisterShell) {
    if (-not (Test-Path $exe)) { throw "$exe not found. Deploy HomeTV first (scripts/deploy.sh)." }
    Step "Making HomeTV the shell for '$TvUser'"
    Set-TvUserShell $exe
    Write-Host "Done. Sign out/in as $TvUser to take effect. Undo with -UnregisterShell."
    return
}
if ($UnregisterShell) {
    Step "Restoring Explorer as the shell for '$TvUser'"
    Set-TvUserShell $null
    return
}

Step "License status"
$oemKey = (Get-CimInstance SoftwareLicensingService).OA3xOriginalProductKey
if ($oemKey) { Write-Host "Found a key embedded in the BIOS: Windows may activate with it (Settings > System > Activation)." -ForegroundColor Green }
else { Write-Host "No BIOS key. Running unactivated is fine for now (watermark + locked personalization)." }

Step "Computer name -> $ComputerName (phones reach http://$ComputerName.local:8888)"
if ($env:COMPUTERNAME -ne $ComputerName.ToUpper()) {
    Rename-Computer -NewName $ComputerName -Force
    Write-Host "Renamed. Takes effect after reboot." -ForegroundColor Yellow
}

Step "Users"
if (-not (Get-LocalUser -Name $TvUser -ErrorAction SilentlyContinue)) {
    New-LocalUser -Name $TvUser -NoPassword -AccountNeverExpires -Description "HomeTV couch account" | Out-Null
    Set-LocalUser -Name $TvUser -PasswordNeverExpires $true
    Add-LocalGroupMember -Group "Users" -Member $TvUser
    Write-Host "Created standard (non-admin) user '$TvUser' with no password."
}
else { Write-Host "'$TvUser' already exists." }

Step "Auto-login as '$TvUser'"
$winlogon = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"
Set-ItemProperty $winlogon -Name AutoAdminLogon -Value "1"
Set-ItemProperty $winlogon -Name DefaultUserName -Value $TvUser
Set-ItemProperty $winlogon -Name DefaultDomainName -Value $env:COMPUTERNAME
Set-ItemProperty $winlogon -Name DefaultPassword -Value ""
# "Require Windows Hello sign-in for Microsoft accounts" blocks auto-login when on.
$passwordless = "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device"
New-Item -Path $passwordless -Force | Out-Null
Set-ItemProperty $passwordless -Name DevicePasswordLessBuildVersion -Value 0 -Type DWord

Step "Install directory $InstallDir"
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir "app"), (Join-Path $InstallDir "staging") | Out-Null
# The restart task runs as the tv user and swaps staging into app, so it needs modify rights.
icacls $InstallDir /grant "${TvUser}:(OI)(CI)M" | Out-Null

Step "Firewall: allow HomeTV on port 8888 (private networks only)"
if (-not (Get-NetFirewallRule -Name "HomeTV-8888" -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -Name "HomeTV-8888" -DisplayName "HomeTV remote (8888)" -Direction Inbound -Protocol TCP -LocalPort 8888 -Action Allow -Profile Private | Out-Null
}
Get-NetConnectionProfile | Where-Object NetworkCategory -eq "Public" | Set-NetConnectionProfile -NetworkCategory Private

Step "Power: HomeTV controls sleep, never hibernate"
powercfg /hibernate off
powercfg /change standby-timeout-ac 0
powercfg /change monitor-timeout-ac 0
Write-Host "Sleep states available:"
powercfg /a

Step "Apps via winget"
$packages = @(
    "Valve.Steam",
    "9N4WGH0Z6VHQ"   # HEVC Video Extensions from Device Manufacturer (free), needed for 4K/HDR streams
)
# winget is a per-user Store app and is sometimes missing or flaky in SSH sessions: warn, don't fail.
if (Get-Command winget -ErrorAction SilentlyContinue) {
    foreach ($id in $packages) {
        winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
        if ($LASTEXITCODE -ne 0) { Write-Warning "winget install $id exited with $LASTEXITCODE; install it manually." }
    }
}
else { Write-Warning "winget not available in this session; install manually: $($packages -join ', ')" }

if (-not $SkipAdBlock) {
    # Force-installed through Edge policy, so they're in every HomeTV Edge profile (YouTube, Prime, Fubo) without
    # anyone clicking through a store page, and can't be switched off from inside an app window. uBlock Origin Lite,
    # not classic uBlock Origin: Edge is switching off Manifest V2 extensions like classic uBO during 2026.
    # Chrome Web Store ids: Edge installs from there when given Google's update URL.
    Step "Ad blocking in Edge: uBlock Origin Lite + SponsorBlock"
    $chromeStore = "https://clients2.google.com/service/update2/crx"
    $extensions = @{
        "ddkjiahejlhfcafbddmgiahcphecmpfh" = "uBlock Origin Lite"
        "mnjggcdmjocbbbhaepdhchncahnbgone" = "SponsorBlock for YouTube"
    }
    $forcelist = "HKLM:\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist"
    New-Item -Path $forcelist -Force | Out-Null
    # Keep any entries already there (numbered values "1", "2", ...); add ours once.
    $current = (Get-Item $forcelist).Property | ForEach-Object { (Get-ItemProperty $forcelist -Name $_).$_ }
    $next = 1 + ((Get-Item $forcelist).Property | Where-Object { $_ -match '^\d+$' } | ForEach-Object { [int]$_ } | Measure-Object -Maximum).Maximum
    foreach ($id in $extensions.Keys) {
        if ($current | Where-Object { $_ -like "$id;*" }) { Write-Host "$($extensions[$id]) already set"; continue }
        New-ItemProperty -Path $forcelist -Name "$next" -Value "$id;$chromeStore" -PropertyType String -Force | Out-Null
        Write-Host "Added $($extensions[$id])"
        $next++
    }
    Write-Host "Edge picks these up within a few minutes, or at its next start (edge://policy shows them)."
}

Step "Restart task (lets deploys restart HomeTV inside the $TvUser desktop session)"
$restartScript = Join-Path $InstallDir "restart-hometv.ps1"
@"
`$staging = '$InstallDir\staging'
`$app = '$InstallDir\app'
Get-Process HomeTV -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
if (Test-Path "`$staging\HomeTV.exe") {
    Remove-Item "`$app\*" -Recurse -Force -ErrorAction SilentlyContinue
    Move-Item "`$staging\*" `$app -Force
}
Start-Process "`$app\HomeTV.exe" -WorkingDirectory `$app
"@ | Set-Content -Path $restartScript -Encoding UTF8

$action = New-ScheduledTaskAction -Execute "powershell.exe" -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$restartScript`""
$principal = New-ScheduledTaskPrincipal -UserId $TvUser -LogonType Interactive
Register-ScheduledTask -TaskName "HomeTV-Restart" -Action $action -Principal $principal -Force | Out-Null

Step "Next"
Write-Host @"
  1. Reboot (applies the computer name and auto-login as '$TvUser').
  2. Deploy from the Mac: scripts/deploy.sh
  3. Once HomeTV works, make it the shell: setup-box.ps1 -RegisterShell
"@
