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
src/HomeTV.App               web server; net10.0 = Mac/fakes, net10.0-windows10.0.19041.0 = TV box
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
dotnet run --project src/HomeTV.App -f net10.0-windows10.0.19041.0

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

**Background photos**: drop JPEG, PNG, WebP, GIF or AVIF files into `Pictures\HomeTV` of the user running HomeTV
(`C:\Users\tv\Pictures\HomeTV` on the box; override with `HomeTV:PhotosDir`). They crossfade behind the launcher in
shuffled order, 30s each, and the folder is re-read every 2 minutes, so no restart is needed. Settings shows the folder
and how many photos it found. iPhone HEIC files won't draw in the WebView: export them as JPEG. From the Mac:
`scp *.jpg hometv:Pictures/HomeTV/` (if you SSH in as the tv user; otherwise give the full path).

**Ambient mode**: after 5 minutes without remote, keyboard, controller or mouse input, the tiles, pairing QR and
settings button fade away and the widget cards lose their backgrounds, leaving the photo, clock and widgets. The first
press (or click) only wakes it, so it never launches anything by accident.

**Continue watching**: a row under the tiles (down from the tiles), and a list on the phone's Apps tab, of the last
videos you played in YouTube and Prime Video. Opening one goes straight back to that page; YouTube also starts at
where you stopped. While an Edge app with a `resumePattern` in `config/apps.json` is open, HomeTV reads its page's
address, title, picture and video position every 5 seconds through Edge's DevTools port (`--remote-debugging-port=0`,
loopback only; `src/HomeTV.Platform.Windows/EdgeDevTools.cs`). A page joins the row after 10 seconds of playback and
leaves it once you reach the credits. Edge only opens that port for a separate profile, so those apps need an
`edgeProfile` (Prime has its own `prime` profile for this, so it needs one sign-in there). Saved in
`%LOCALAPPDATA%\HomeTV\continue.json`. Fubo has no `resumePattern` yet: add one once you know which of its URLs are
worth reopening.

**Now playing** (phone remote): the Remote tab shows what Windows' media controls report (title, artist, artwork
and position) for Edge apps, Spotify and most players, with a seek bar and ±10s buttons when the player accepts seeks.
This is why the Windows target is `net10.0-windows10.0.19041.0`: it needs the WinRT media APIs.

**Sound output**: the pill in the launcher's top-left corner (up from the left half of the tiles) has an icon per
output in `config/audio.json`: the soundbar and the Echo Studio. The one playing is lit, and a press (OK or a click)
switches to the other; a dimmed one isn't connected. Both are Windows playback devices, so switching just changes
Windows' default device (`WindowsAudioDevices.cs`):
- **Soundbar**: the HDMI audio to the TV, which passes it to the soundbar over ARC.
- **Echo Studio**: paired to the PC over Bluetooth. Pair it once as the tv user: Settings → Bluetooth & devices → Add
  device → Bluetooth, and say "Alexa, pair Bluetooth". If it drops off later, "Alexa, connect to <PC name>" brings it
  back, and its icon lights up again within 5 seconds. Bluetooth adds some delay, so lip sync can be slightly off.

Each output's `device` is part of its Windows playback device name. The log lists the connected ones at startup
("Playback device: ..."); the HDMI one is named after the TV, e.g. `HISENSE (NVIDIA High Definition Audio)`. Leave
`outputs` empty (or delete the file) to hide the button.

There's no TV speakers option. Switching the Hisense (a Fire TV) between its speakers and the soundbar would mean
telling the TV itself, since both play what comes down the same HDMI cable, and its menus have no such switch. HomeTV
can run a command on the TV over ADB for an output (`tvCommand`, plus `tv.host` in `audio.json`; see `AdbTvControl.cs`),
but none of the settings tried worked: `amazon_settings_speaker_status` only reports the state, and
`hdmi_system_audio_control_enabled` doesn't exist on this TV. `adb shell dumpsys audio` shows where it's routing
(`Devices: hmdi_arc` is the soundbar), and `scripts\find-tv-audio-setting.ps1` compares the TV's settings before and
after a change.

**Ad blocking**: `setup-box.ps1` force-installs **uBlock Origin Lite** and **SponsorBlock** into Edge via the
`ExtensionInstallForcelist` policy, so every HomeTV Edge profile has them (check `edge://policy`). It's Lite because
Edge is switching off Manifest V2 extensions like classic uBlock Origin. Lite blocks less on YouTube than classic uBO
did, and YouTube keeps fighting blockers, so expect some ads. `-SkipAdBlock` leaves them out. The policy covers
the whole PC, so the admin account's own Edge gets them too.

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
