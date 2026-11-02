<#
.SYNOPSIS
  Builds HomeTV from this checkout and (re)starts it on this PC. The local counterpart of deploy.sh.

.DESCRIPTION
  For when the dev machine IS the TV box. Needs Node and the .NET 10 SDK. No admin rights needed.

    powershell -ExecutionPolicy Bypass -File scripts\deploy-local.ps1
    powershell -ExecutionPolicy Bypass -File scripts\deploy-local.ps1 -SkipTests -NoStart

  Steps: build the web UI, run tests, publish a self-contained win-x64 build to <InstallDir>\staging, stop the
  running HomeTV.exe, swap staging into <InstallDir>\app, and start it. The default InstallDir matches
  setup-box.ps1, so -RegisterShell will find the exe if you provision the box later.

.PARAMETER InstallDir
  Where the published app lives. Default C:\HomeTV (app\HomeTV.exe).

.PARAMETER SkipTests
  Skip dotnet test and the TypeScript typecheck.

.PARAMETER NoStart
  Install but don't start HomeTV.
#>
param(
    [string]$InstallDir = "C:\HomeTV",
    [switch]$SkipTests,
    [switch]$NoStart
)

$ErrorActionPreference = "Stop"
function Step($message) { Write-Host "`n==> $message" -ForegroundColor Cyan }
function Invoke-Checked([scriptblock]$command, [string]$what) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit $LASTEXITCODE)." }
}

$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $InstallDir "staging"
$app = Join-Path $InstallDir "app"
$exe = Join-Path $app "HomeTV.exe"

foreach ($tool in "node", "npm", "dotnet") {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool not found on PATH." }
}

Step "Building web UI"
Push-Location (Join-Path $root "web")
try {
    if (-not (Test-Path "node_modules")) { Invoke-Checked { npm install --no-audit --no-fund } "npm install" }
    Invoke-Checked { npm run --silent build } "Web build"
    if (-not $SkipTests) { Invoke-Checked { npm run --silent typecheck } "Typecheck" }
}
finally { Pop-Location }

if (-not $SkipTests) {
    Step "Running tests"
    Invoke-Checked { dotnet test $root --nologo -v quiet } "dotnet test"
}

Step "Publishing win-x64 to $staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging, $app | Out-Null
Invoke-Checked {
    dotnet publish (Join-Path $root "src\HomeTV.App") -c Release -f net10.0-windows -r win-x64 --self-contained -o $staging --nologo -v quiet
} "dotnet publish"

Step "Stopping HomeTV"
$running = Get-Process HomeTV -ErrorAction SilentlyContinue
if ($running) {
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    Write-Host "Stopped $(@($running).Count) process(es)."
}
else { Write-Host "Not running." }

Step "Installing to $app"
Remove-Item (Join-Path $app "*") -Recurse -Force -ErrorAction SilentlyContinue
Move-Item (Join-Path $staging "*") $app -Force
Remove-Item $staging -Recurse -Force

if (-not (Get-NetFirewallRule -Name "HomeTV-8888" -ErrorAction SilentlyContinue)) {
    Write-Warning @"
No firewall rule for port 8888, so phones may not reach the remote. From an admin PowerShell:
  New-NetFirewallRule -Name HomeTV-8888 -DisplayName 'HomeTV remote (8888)' -Direction Inbound -Protocol TCP -LocalPort 8888 -Action Allow -Profile Private
"@
}

if ($NoStart) { Write-Host "`nInstalled. Start it with: & '$exe'"; return }

Step "Starting HomeTV"
Start-Process $exe -WorkingDirectory $app
# 127.0.0.1, not localhost: the server binds IPv4 only, and localhost may try ::1 first.
# Allow 30s: the first launch of a freshly published exe is slow while Defender scans it.
$process = Get-Process HomeTV -ErrorAction SilentlyContinue | Select-Object -First 1
$up = $false
$deadline = (Get-Date).AddSeconds(30)
while (-not $up -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
    if ($process -and $process.HasExited) { break }
    try { Invoke-WebRequest "http://127.0.0.1:8888/launcher/" -UseBasicParsing -TimeoutSec 2 | Out-Null; $up = $true } catch { }
}
if (-not $up) {
    throw "HomeTV didn't answer on port 8888. See its log: $env:LOCALAPPDATA\HomeTV\logs\hometv.log"
}

$ip = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.PrefixOrigin -in "Dhcp", "Manual" -and $_.IPAddress -notlike "169.254.*" } |
    Select-Object -First 1 -ExpandProperty IPAddress
Write-Host "`nHomeTV is running." -ForegroundColor Green
Write-Host "  Launcher: http://localhost:8888/launcher/"
if ($ip) { Write-Host "  Remote:   http://${ip}:8888 (from your phone)" }
