# TV box setup

Hardware: Ryzen 5 3600, GTX 1660, 16 GB DDR4, WiFi. Target: Windows 11 Home (unactivated for now), 4K HDR TV.

## 1. Check for an existing license (before wiping)
On the current Windows 10, in PowerShell:
```powershell
(Get-CimInstance SoftwareLicensingService).OA3xOriginalProductKey
```
If this prints a key, or the PC was ever activated, a clean Windows 11 install may activate on its own.

## 2. BIOS
- [ ] **fTPM** (AMD CPU TPM) → Enabled. Required by Windows 11.
- [ ] **Secure Boot** → Enabled (boot mode UEFI, CSM off).
- [ ] **Fast Boot** → Enabled. **Boot logo / splash** → Disabled.
- [ ] Boot order: the Windows SSD first. Remove other boot entries.
- [ ] **ErP / deep sleep** → Disabled, otherwise USB devices can't wake the PC.
- [ ] **USB wake from S3/S4** → Enabled (CEC adapter + controller wake).
- [ ] Optional: **Restore on AC power loss** → Power On, so a power cut brings the box back.

## 3. Install Windows 11 Home
1. Make the USB installer **on the PC itself while it still runs Windows 10**, using Microsoft's Media Creation Tool (8 GB+ stick).
2. Clean install to the SSD. When asked for a key, choose "I don't have a product key" and pick **Home**.
3. Create your admin account. If setup forces a Microsoft account, that's fine; the bootstrap prints the local username.
4. Connect to WiFi.

## 4. Hand over to SSH (the last step at the PC keyboard)
On the Mac:
```bash
scripts/serve-bootstrap.sh
```
It prints a line like `irm http://10.0.0.138:8899/go.txt | iex`. On the PC, open **Terminal (Admin)** (right-click Start) and type that line. It enables the OpenSSH server, trusts the Mac's `~/.ssh/hometv` key, sets the WiFi network to Private, and prints the username and IP. Then stop the server on the Mac with Ctrl+C.

The Mac's `~/.ssh/config` has a `hometv` alias (`User admin`, `HostName hometv.local`). Until the box is renamed, use the printed IP and username.

## 5. Remote provisioning (done over SSH)
- `setup-box.ps1`: computer name `hometv`, passwordless `tv` couch user + auto-login, firewall, power settings, Steam, HEVC codec, restart task.
- [Win11Debloat](https://github.com/Raphire/Win11Debloat), default mode, keeping **Microsoft Edge** (needed for PlayReady DRM).
- Latest **NVIDIA driver** (Game Ready).
- Reboot, then `scripts/deploy.sh`.

Also reserve a DHCP address for the box on your router, as a backup in case `hometv.local` doesn't resolve.

## 6. Phase 1 hardware tests (do these before building more)
1. **CEC wake**: with the Pulse-Eight adapter on a second TV HDMI input, does turning the TV on wake the sleeping PC? Does `cec-client` "on 0" + "as" switch the TV to the **PC's** input?
2. **Stream quality**: in Edge app mode, do Prime and Fubo reach ≥1080p (4K HDR for Prime)? Check with each player's stats overlay.
3. **YouTube TV UI**: `youtube.com/tv` with the TV user agent in the `youtube` Edge profile. Does sign-in work, do arrow keys work, and does uBlock Origin Lite block ads?
4. **No Explorer**: with HomeTV as the shell, do Edge and Steam Big Picture behave?
5. **Watermark**: does "Activate Windows" show over full-screen apps?

## 7. Make HomeTV the shell
Once a deploy works and HomeTV starts at login:
```powershell
powershell -ExecutionPolicy Bypass -File setup-box.ps1 -RegisterShell
```
To undo it, sign in as admin and run `setup-box.ps1 -UnregisterShell`. You can also use the remote's **Desktop mode** button to open Explorer on top of HomeTV.
