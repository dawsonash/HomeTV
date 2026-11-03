# HomeTV

An ad-free TV home screen for an old gaming PC, with a phone remote at `http://hometv.local:8888`.

- **Launcher**: full-screen tiles (Prime Video, Fubo, YouTube, Steam, Spotify) that replace the Windows shell,
  with weather and sports widgets above them.
- **Remote**: phone web app with a D-pad, touchpad, keyboard and app list. Phones pair with a PIN shown on the TV.
- **Server**: .NET 10 + WebSockets. It injects input with Win32 `SendInput`, launches Edge app windows and Steam Big Picture, and puts the PC to sleep.

## Layout
```
src/HomeTV.Core              platform interfaces, pairing, command routing, app registry (cross-platform)
src/HomeTV.Platform.Windows  SendInput, sleep, app launching (the real thing)
src/HomeTV.Platform.Fake     logs instead of acting (Mac development)
src/HomeTV.App               web server; net10.0 = Mac/fakes, net10.0-windows = TV box
web/                         launcher + remote (TypeScript, bundled into src/HomeTV.App/wwwroot)
config/apps.json             launcher tiles
scripts/                     setup-box.ps1 (provision the box), deploy.sh (Mac → box)
docs/box-setup.md            BIOS, Windows install, provisioning checklist
```

## Develop on the Mac
```bash
cd web && npm install && npm run build && cd ..
dotnet run --project src/HomeTV.App -f net10.0
```
- TV launcher: http://localhost:8888/launcher/
- Remote: open `http://<mac-lan-ip>:8888` on your phone. Requests from localhost skip pairing, so use the LAN IP to test pairing.
- Fake platform actions (keys, launches, sleep) are printed to the console.
- `npm run watch` in `web/` rebuilds the UI on save.

## Develop on the box itself (Windows)
When the TV PC is also the dev machine (needs Node and the .NET 10 SDK):
```powershell
# iterate from source with the real Windows platform (keys, launches and sleep really happen)
cd web; npm install; npm run build; cd ..
dotnet run --project src/HomeTV.App -f net10.0-windows

# or build, test and (re)start an installed copy at C:\HomeTV\app
powershell -ExecutionPolicy Bypass -File scripts\deploy-local.ps1   # -SkipTests, -NoStart
```
Phones need inbound TCP 8888 open; the script prints the firewall command if the rule is missing.

The launcher opens in a WebView2 window. `HomeTV:Launcher` picks how: `Fullscreen` (default; the installed copy,
Alt+F4 can't close it), `Windowed` (what `dotnet run` uses; closing it stops HomeTV, dev tools enabled), or `None`
(no window; browse to `/launcher/` yourself). Override with e.g. `$env:HomeTV__Launcher = "None"`.

Each launched app's window is tracked: Home closes it (or runs its `closeTarget`), and when it closes on its own
the launcher comes back. Edge apps are found as a new `msedge` window; shell apps need `windowProcess`, and
optionally `windowTitle`, in `config/apps.json`.

**Watchdog**: the installed `HomeTV.exe` is a small supervisor that runs the real app as a child (`--worker`).
It restarts the app when it exits (backing off from 1s to 60s if it keeps crashing) and kills/restarts it when
`/healthz` fails 3 checks in a row, which includes the launcher window freezing. Killing the supervisor kills the
app too. Logs: `%LOCALAPPDATA%\HomeTV\logs\hometv.log`. `dotnet run` skips the watchdog (`HomeTV:Watchdog=false`).

**Settings**: the round gear button in the launcher's top-right corner (press up from the tiles). Shows the address
phones should open, pairs a new phone, lists and removes paired phones (each with its own touchpad speed), and has
Sleep, Restart HomeTV, Restart PC and Desktop mode. Destructive actions need OK twice. Its endpoints (`/api/system*`, removing a device, `/ws/launcher`) only answer the launcher page itself:
loopback connection, loopback `Host`, same-origin `Origin`, so other web pages open on the box can't use them.

**Widgets**: `config/widgets.json` sets the weather location and up to 4 teams (ESPN slugs, e.g.
`football / nfl / den`; college teams use ESPN's numeric id). Weather is from Open-Meteo, sports from ESPN's
public but undocumented API, all fetched and cached by the box (`src/HomeTV.Core/Widgets`). Scores refresh every 30s
during a game, otherwise every 10 minutes. Leave a section out to hide that card; changes need a restart.

Home without the phone, from inside any app: **Ctrl+Alt+H**, the keyboard's **Home** (Browser Home) key, or
**hold Back+Start** (View+Menu) on an Xbox-compatible controller for 1 second.

## Test
```bash
dotnet test
cd web && npm run typecheck
```

## Deploy to the box
Set up the box first (`docs/box-setup.md`), then:
```bash
scripts/deploy.sh   # uses the hometv alias in ~/.ssh/config
```

## Status
Phases 2 (server, pairing, remote) and 3 (WebView2 launcher window, Home closes the app, exit detection for
Edge apps and Steam Big Picture) are done. Next up:
- **Phase 4**: CEC, HDR toggle.
- **Phase 5**: settings screen ✅, watchdog ✅.

See the plan for details.
