#!/usr/bin/env bash
# Builds HomeTV for Windows on the Mac and ships it to the TV box over SSH.
#
#   scripts/deploy.sh                 # uses the "hometv" host alias from ~/.ssh/config
#   HOMETV_SSH=admin@10.0.0.50 scripts/deploy.sh
#
# The box needs no .NET SDK: this publishes a self-contained win-x64 build, copies it to C:\HomeTV\staging,
# and runs the HomeTV-Restart scheduled task (created by setup-box.ps1), which swaps it into place and
# restarts HomeTV inside the tv user's desktop session.
set -euo pipefail

HOMETV_SSH="${HOMETV_SSH:-hometv}"
root="$(cd "$(dirname "$0")/.." && pwd)"
out="$root/artifacts/publish"

echo "==> Building web UI"
(cd "$root/web" && npm run --silent build)

echo "==> Publishing win-x64"
rm -rf "$out"
dotnet publish "$root/src/HomeTV.App" -c Release -f net10.0-windows -r win-x64 --self-contained -o "$out" --nologo -v quiet

echo "==> Copying to $HOMETV_SSH"
ssh "$HOMETV_SSH" "powershell -NoProfile -Command \"Remove-Item C:\\HomeTV\\staging\\* -Recurse -Force -ErrorAction SilentlyContinue\""
scp -rq "$out"/* "$HOMETV_SSH:C:/HomeTV/staging/"

echo "==> Restarting HomeTV on the box"
ssh "$HOMETV_SSH" "schtasks /run /tn HomeTV-Restart"
echo "Done. Remote: http://hometv.local:8888"
