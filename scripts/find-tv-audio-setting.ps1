<#
.SYNOPSIS
  Finds which Android setting the TV flips when you change its audio output, for tvCommand in config/audio.json.

.DESCRIPTION
  Snapshots the TV's settings (global, secure, system) with the output on TV speakers, again on the soundbar,
  and prints what changed. Each changed line becomes a command, e.g. a change in global "foo" from 0 to 1 means
    soundbar:    "tvCommand": "settings put global foo 1"
    TV speakers: "tvCommand": "settings put global foo 0"
  Needs adb (winget install Google.PlatformTools) and ADB debugging on in the TV's Developer options.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\find-tv-audio-setting.ps1 -TvIp 192.168.1.50
#>
param(
    [Parameter(Mandatory)][string]$TvIp,
    [int]$Port = 5555,
    [string]$Adb = "adb"
)
$ErrorActionPreference = "Stop"
$serial = "${TvIp}:$Port"

& $Adb connect $serial
Write-Host "If the TV asks to allow USB debugging, tick 'Always allow from this computer' and choose OK." -ForegroundColor Yellow
& $Adb -s $serial wait-for-device

function Get-TvSettings {
    foreach ($namespace in "global", "secure", "system") {
        & $Adb -s $serial shell settings list $namespace | ForEach-Object { "$namespace $_" }
    }
}

Read-Host "On the TV, set Audio output to TV speakers, then press Enter here"
$speakers = Get-TvSettings
Read-Host "Now set it to the soundbar (HDMI ARC), then press Enter here"
$soundbar = Get-TvSettings

$before = @{}; foreach ($line in $speakers) { $k, $v = $line -split "=", 2; $before[$k] = $v }
$after = @{};  foreach ($line in $soundbar) { $k, $v = $line -split "=", 2; $after[$k] = $v }
$changed = @($before.Keys) + @($after.Keys) | Sort-Object -Unique | Where-Object { $before[$_] -ne $after[$_] }

if (-not $changed) {
    Write-Warning "No setting changed. This TV keeps its audio output somewhere adb's settings can't see."
    Write-Host "Check what HDMI-CEC thinks instead: $Adb -s $serial shell dumpsys hdmi_control"
    exit 1
}

Write-Host "`nChanged (TV speakers -> soundbar):" -ForegroundColor Cyan
foreach ($key in $changed) {
    $namespace, $name = $key -split " ", 2
    Write-Host "  $key : $($before[$key]) -> $($after[$key])"
    Write-Host "    soundbar:    settings put $namespace $name $($after[$key])"
    Write-Host "    TV speakers: settings put $namespace $name $($before[$key])"
}
Write-Host "`nIgnore anything that changes by itself (timestamps, counters). Try a candidate by running the soundbar"
Write-Host "command with the TV on speakers:  $Adb -s $serial shell settings put <namespace> <name> <value>"
