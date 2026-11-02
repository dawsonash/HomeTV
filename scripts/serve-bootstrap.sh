#!/usr/bin/env bash
# Serves bootstrap-ssh.ps1 (with this Mac's public deploy key baked in) on the LAN, so the fresh Windows box
# only needs one short line typed into an admin PowerShell. Only public data is served. Ctrl+C when done.
#
#   scripts/serve-bootstrap.sh            # uses ~/.ssh/hometv.pub
set -euo pipefail

key_file="${HOMETV_KEY:-$HOME/.ssh/hometv}.pub"
port="${PORT:-8899}"
root="$(cd "$(dirname "$0")" && pwd)"
ip="$(ipconfig getifaddr en0 2>/dev/null || ipconfig getifaddr en1)"
[[ -f "$key_file" ]] || { echo "No public key at $key_file" >&2; exit 1; }

serve_dir="$(mktemp -d)"
trap 'rm -rf "$serve_dir"' EXIT
cp "$root/bootstrap-ssh.ps1" "$serve_dir/bootstrap-ssh.txt"
# .txt so Invoke-RestMethod returns a string rather than bytes.
cat > "$serve_dir/go.txt" <<EOF
\$script = Invoke-RestMethod http://$ip:$port/bootstrap-ssh.txt
& ([scriptblock]::Create(\$script)) -PublicKey '$(cat "$key_file")'
EOF

echo "On the TV box, open an *admin* PowerShell (right-click Start > Terminal (Admin)) and type:"
echo
echo "    irm http://$ip:$port/go.txt | iex"
echo
echo "If macOS asks whether python3 may accept incoming connections, click Allow."
cd "$serve_dir" && exec python3 -m http.server "$port" --bind "$ip"
