<#
.SYNOPSIS
  Turns the RAM sticks' RGB off with OpenRGB. Run by the HomeTV-RGB-Off scheduled task (setup-box.ps1) as SYSTEM
  at boot, at the tv user's sign-in, and on wake from sleep, because RAM lighting comes back on after each.

.DESCRIPTION
  OpenRGB needs admin rights (and, from OpenRGB 1.0, the PawnIO driver) to reach RAM over the SMBus, which is why
  this runs as SYSTEM rather than as the tv user. Only devices OpenRGB lists as "Type: DRAM" are touched; pass
  -AllDevices to darken everything it finds (GPU, motherboard, fans) too. Logs to rgb-off.log next to this script.
#>
param(
    [string]$OpenRgb,
    [switch]$AllDevices,
    [int]$StartDelaySeconds = 15
)

$log = Join-Path $PSScriptRoot "rgb-off.log"
function Log($message) { "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss') $message" | Add-Content -Path $log }

if (-not $OpenRgb -or -not (Test-Path $OpenRgb)) {
    $OpenRgb = Get-ChildItem -Path "$env:ProgramFiles\OpenRGB", "$env:ProgramFiles\WinGet\Packages", "${env:ProgramFiles(x86)}\OpenRGB" `
        -Filter OpenRGB.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $OpenRgb) { Log "OpenRGB.exe not found; install it (winget install OpenRGB.OpenRGB)"; exit 1 }

# At boot the SMBus drivers may not be ready straight away.
Start-Sleep -Seconds $StartDelaySeconds

# The device list. OpenRGB's Windows build prints to a console window only, so a script can't capture
# --list-devices; instead, read the log that same run writes, which names each controller in the order it was
# registered (that order is the --device index). Device types aren't in the log, so RAM is told by its name.
$logDir = Join-Path ([Environment]::GetFolderPath("ApplicationData")) "OpenRGB\logs"
$started = Get-Date
$listing = & $OpenRgb --noautoconnect --list-devices 2>&1 | Out-String
$runLog = Get-ChildItem $logDir -Filter *.log -ErrorAction SilentlyContinue |
    Where-Object LastWriteTime -ge $started.AddSeconds(-2) | Sort-Object LastWriteTime | Select-Object -Last 1

$devices = @()
if ($listing.Trim()) {
    # A console was there to print to (run by hand): use the real list, with types.
    $index = $null
    foreach ($line in $listing -split "`r?`n") {
        if ($line -match '^(\d+): (.+)$') { $index = [int]$Matches[1]; $name = $Matches[2].Trim(); continue }
        if ($null -ne $index -and $line -match '^\s+Type:\s+(.+)$') {
            if ($AllDevices -or $Matches[1].Trim() -eq "DRAM") { $devices += [pscustomobject]@{ Index = $index; Name = $name } }
            $index = $null
        }
    }
}
elseif ($runLog) {
    $names = Select-String -Path $runLog.FullName -Pattern 'Registering RGB controller (.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() }
    for ($i = 0; $i -lt @($names).Count; $i++) {
        $name = @($names)[$i]
        if ($AllDevices -or $name -match 'DRAM|DDR\d|Vengeance|Trident|Fury|Dominator|T-Force|Delta|XPG|Ballistix|Viper') {
            $devices += [pscustomobject]@{ Index = $i; Name = $name }
        }
    }
    $listing = "(from $($runLog.Name)) " + (@($names) -join ', ')
}
else { $listing = "(no output, and no OpenRGB log in $logDir)" }

if ($devices.Count -eq 0) {
    Log "No $(if ($AllDevices) { 'RGB' } else { 'DRAM' }) devices found. OpenRGB said:`n$listing"
    exit 2
}

$arguments = @("--noautoconnect")
foreach ($device in $devices) { $arguments += @("--device", $device.Index, "--mode", "static", "--color", "000000") }
& $OpenRgb @arguments 2>&1 | Out-Null
Log "Turned off: $(($devices | ForEach-Object { "$($_.Index) $($_.Name)" }) -join ', ') (exit $LASTEXITCODE)"
