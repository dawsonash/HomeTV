# HomeTV

An ad-free TV home screen for an old gaming PC, with a phone remote at `http://hometv.local:8888`.

- **Launcher**: full-screen tiles (Prime Video, Fubo, YouTube, Games) that replace the Windows shell.
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
Phase 2 (server, pairing, remote) is done. Next up:
- **Phase 3**: WebView2 launcher window, Home closes the app, Steam exit detection.
- **Phase 4**: CEC, HDR toggle.
- **Phase 5**: settings screen, watchdog.

See the plan for details.
