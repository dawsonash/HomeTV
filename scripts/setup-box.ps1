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

.PARAMETER SkipRgb
  Don't install OpenRGB or the task that turns the RAM sticks' RGB off at boot, sign-in and wake.

.PARAMETER RgbAllDevices
  Turn off every RGB device OpenRGB finds (GPU, motherboard, fans), not just the RAM.

.PARAMETER SkipEdgeLockdown
  Don't set the tv user's Edge policies (no downloads, no site notifications, SmartScreen can't be clicked past).

.PARAMETER SkipAdBlock
  Don't force-install uBlock Origin Lite and SponsorBlock into Edge. Remove them later by deleting their values
  under HKLM\SOFTWARE\Policies\Microsoft\Edge\ExtensionInstallForcelist.
#>
param(
    [string]$TvUser = "tv",
    [string]$ComputerName = "hometv",
    [string]$InstallDir = "C:\HomeTV",
    # Background photos and videos (uploaded from phones). Keep in step with appsettings.Production.json.
    [string]$PhotosDir = "D:\HomeTV\Photos",
    # The image model that sorts photos into People/Nature/Safe for work (~600 MB, HomeTV downloads it here).
    [string]$ModelsDir = "D:\HomeTV\Models",
    [switch]$RegisterShell,
    [switch]$UnregisterShell,
    [switch]$SkipAdBlock,
    [switch]$SkipRgb,
    [switch]$RgbAllDevices,
    [switch]$SkipEdgeLockdown
)

$ErrorActionPreference = "Stop"
function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }

$exe = Join-Path $InstallDir "app\HomeTV.exe"
$shellKey = "Software\Microsoft\Windows NT\CurrentVersion\Winlogon"

# Runs $action with the tv user's own registry hive (their HKCU) as its argument, loading it if they're signed out.
function Invoke-WithTvUserHive([scriptblock]$action) {
    $sid = (New-Object System.Security.Principal.NTAccount($TvUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value
    $loaded = Test-Path "Registry::HKEY_USERS\$sid"
    if (-not $loaded) {
        $profile = Get-CimInstance Win32_UserProfile | Where-Object SID -eq $sid
        if (-not $profile) { throw "'$TvUser' has never signed in, so there's no profile yet. Reboot (auto-login) first." }
        $hive = Join-Path $profile.LocalPath "NTUSER.DAT"
        reg load "HKU\$sid" $hive | Out-Null
    }
    try { & $action "Registry::HKEY_USERS\$sid" }
    finally {
        if (-not $loaded) { [gc]::Collect(); reg unload "HKU\$sid" | Out-Null }
    }
}

# Per-user shell lives in the tv user's own registry hive.
function Set-TvUserShell([string]$value) {
    Invoke-WithTvUserHive {
        param($root)
        $path = "$root\$shellKey"
        if ($value) { New-ItemProperty -Path $path -Name Shell -Value $value -PropertyType String -Force | Out-Null }
        else { Remove-ItemProperty -Path $path -Name Shell -ErrorAction SilentlyContinue }
    }
}

if ($RegisterShell) {
    if (-not (Test-Path $exe)) { throw "$exe not found. Deploy HomeTV first (scripts/deploy.sh)." }
    Step "Making HomeTV the shell for '$TvUser'"
    Set-TvUserShell $exe
    # The shell starts it now; a Run entry too would start a second copy.
    Invoke-WithTvUserHive { param($root) Remove-ItemProperty -Path "$root\Software\Microsoft\Windows\CurrentVersion\Run" -Name "HomeTV" -ErrorAction SilentlyContinue }
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

Step "Background photos folder $PhotosDir"
if (Test-Path (Split-Path -Qualifier $PhotosDir)) {
    New-Item -ItemType Directory -Force -Path $PhotosDir | Out-Null
    # HomeTV runs as the tv user and saves phones' uploads here.
    icacls $PhotosDir /grant "${TvUser}:(OI)(CI)M" | Out-Null
}
else { Write-Warning "$(Split-Path -Qualifier $PhotosDir) doesn't exist; set HomeTV:PhotosDir to a folder that does." }

Step "Photo sorting model folder $ModelsDir"
if (Test-Path (Split-Path -Qualifier $ModelsDir)) {
    New-Item -ItemType Directory -Force -Path $ModelsDir | Out-Null
    # HomeTV (as the tv user) downloads the model here on first start.
    icacls $ModelsDir /grant "${TvUser}:(OI)(CI)M" | Out-Null
}
else { Write-Warning "$(Split-Path -Qualifier $ModelsDir) doesn't exist; set HomeTV:ModelsDir to a folder that does." }

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

if (-not $SkipEdgeLockdown) {
    # Only the tv user's Edge (their HKCU policies), so the admin account can still download things. These cover the
    # free streaming sites in apps.json ("untrusted": true) in particular: no file can be downloaded at all, sites
    # can't ask to send notifications (the usual way they keep spamming after you leave), and a SmartScreen warning
    # about a malicious page or file can't be clicked past. HomeTV itself closes those sites' pop-ups.
    Step "Edge lockdown for '$TvUser': no downloads, no site notifications, strict SmartScreen"
    try {
        Invoke-WithTvUserHive {
            param($root)
            $edgePolicies = "$root\Software\Policies\Microsoft\Edge"
            New-Item -Path $edgePolicies -Force | Out-Null
            $policies = [ordered]@{
                DownloadRestrictions                     = 3   # Block all downloads
                DefaultNotificationsSetting              = 2   # No site may show notifications
                SmartScreenEnabled                       = 1
                SmartScreenPuaEnabled                    = 1   # Also potentially unwanted apps
                PreventSmartScreenPromptOverride         = 1
                PreventSmartScreenPromptOverrideForFiles = 1
                TyposquattingCheckerEnabled              = 1
            }
            foreach ($name in $policies.Keys) {
                New-ItemProperty -Path $edgePolicies -Name $name -Value $policies[$name] -PropertyType DWord -Force | Out-Null
            }
        }
        Write-Host "Set. Check edge://policy in one of the tv user's Edge windows. Undo: delete HKCU\Software\Policies\Microsoft\Edge for '$TvUser'."
    }
    catch { Write-Warning "Couldn't set the tv user's Edge policies: $_" }
}

if (-not $SkipRgb) {
    # The RAM's RGB comes on at every boot (and often after sleep). OpenRGB needs admin rights to reach it over the
    # SMBus, so a SYSTEM task does it, at startup, when the tv user signs in, and on wake. OpenRGB 1.0 reaches the
    # SMBus through the PawnIO driver instead of the old WinRing0 one, so that's installed too.
    Step "RGB: OpenRGB turns the RAM lighting off at boot, sign-in and wake"
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        foreach ($id in @("namazso.PawnIO", "OpenRGB.OpenRGB")) {
            winget install --id $id --exact --silent --scope machine --accept-package-agreements --accept-source-agreements --disable-interactivity
            if ($LASTEXITCODE -ne 0) { Write-Warning "winget install $id exited with $LASTEXITCODE; install it manually." }
        }
    }
    else { Write-Warning "winget not available in this session; install OpenRGB (and PawnIO) manually." }

    $openRgb = Get-ChildItem -Path "$env:ProgramFiles\OpenRGB", "$env:ProgramFiles\WinGet\Packages", "${env:ProgramFiles(x86)}\OpenRGB" `
        -Filter OpenRGB.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
    if (-not $openRgb) { Write-Warning "OpenRGB.exe not found after installing; the task will look for it again each time it runs." }
    else { Write-Host "OpenRGB: $openRgb" }

    $rgbScript = Join-Path $InstallDir "rgb-off.ps1"
    Copy-Item (Join-Path $PSScriptRoot "rgb-off.ps1") $rgbScript -Force
    $rgbArgs = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$rgbScript`""
    if ($openRgb) { $rgbArgs += " -OpenRgb `"$openRgb`"" }
    if ($RgbAllDevices) { $rgbArgs += " -AllDevices" }

    # Waking from sleep: the Power-Troubleshooter "system resumed" event.
    $wake = Get-CimClass -ClassName MSFT_TaskEventTrigger -Namespace Root/Microsoft/Windows/TaskScheduler | New-CimInstance -ClientOnly
    $wake.Enabled = $true
    $wake.Subscription = "<QueryList><Query Id='0' Path='System'><Select Path='System'>*[System[Provider[@Name='Microsoft-Windows-Power-Troubleshooter'] and EventID=1]]</Select></Query></QueryList>"
    $triggers = @((New-ScheduledTaskTrigger -AtStartup), (New-ScheduledTaskTrigger -AtLogOn -User $TvUser), $wake)

    Register-ScheduledTask -TaskName "HomeTV-RGB-Off" -Force `
        -Action (New-ScheduledTaskAction -Execute "powershell.exe" -Argument $rgbArgs) `
        -Trigger $triggers `
        -Principal (New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest) `
        -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew) | Out-Null
    Write-Host "Registered HomeTV-RGB-Off. Try it now: Start-ScheduledTask HomeTV-RGB-Off; then read $InstallDir\rgb-off.log"
}

Step "Start HomeTV when '$TvUser' signs in"
# A Run entry in the tv user's own registry: HomeTV comes up on top of the normal desktop at every boot (auto-login).
# -RegisterShell goes further and replaces Explorer; the shell starts HomeTV itself, so the entry is removed then.
Invoke-WithTvUserHive {
    param($root)
    $run = "$root\Software\Microsoft\Windows\CurrentVersion\Run"
    New-Item -Path $run -Force | Out-Null
    New-ItemProperty -Path $run -Name "HomeTV" -Value "`"$exe`"" -PropertyType String -Force | Out-Null
}
Write-Host "HomeTV starts at sign-in: $exe"

Step "Keep other apps from opening at '$TvUser' sign-in (Spotify)"
# The Store Spotify registers a startup task and turns it on at first run, so it pops up over the launcher at every
# boot. State 1 = "turned off by the user", the same as the switch in Settings > Apps > Startup. Only Spotify's
# Run entry (non-Store installs) is removed; other Run entries are left alone.
Invoke-WithTvUserHive {
    param($root)
    $appData = "$root\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData"
    Get-ChildItem $appData -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -like "SpotifyAB.SpotifyMusic_*" } | ForEach-Object {
        Get-ChildItem $_.PSPath | Where-Object { (Get-ItemProperty $_.PSPath).PSObject.Properties.Name -contains "State" } | ForEach-Object {
            Set-ItemProperty -Path $_.PSPath -Name State -Value 1 -Type DWord
            Write-Host "Turned off startup task $($_.PSChildName)"
        }
    }
    Remove-ItemProperty -Path "$root\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Spotify" -ErrorAction SilentlyContinue
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
