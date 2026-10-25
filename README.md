# FSR 4.1 Lite by ROG Ally Life (Game Bar widget)

A free Xbox Game Bar widget from ROG Ally Life. Open Game Bar over a game on a ROG Ally, Legion Go or any other
AMD Radeon handheld,
press **Install FSR 4.1 Lite**, restart the game, and turn on FSR in its graphics settings.
FSR 4.1 Lite loads automatically, with no OptiScaler menu to open.

Works with games from any store: Steam, Epic, GOG, Ubisoft Connect, the EA app, Battle.net, itch.io or
DRM-free installs. The widget works from the running game's exe, so the launcher doesn't matter.
Xbox app / Game Pass games are experimental, because some of them block injected DLLs.

## How it works

The package holds two programs:

- **RalFsr.Widget**, a UWP Game Bar widget. It is only the interface.
- **RalFsr.Host**, a full-trust helper that starts with the widget and stops when it closes. It finds the game
  under the overlay, downloads OptiScaler, and installs or removes files. It talks to the widget over an
  in-package app service.

When you install, the helper:

1. Finds the game window under Game Bar and the folder holding its exe. For Unreal Engine games that is
   `<Project>\Binaries\Win64`, next to the `-Shipping.exe`.
2. Blocks games with anti-cheat, and leaves alone any game that already has a manual OptiScaler install.
   Detected: Easy Anti-Cheat, BattlEye, EA Javelin, Ricochet (Call of Duty), nProtect GameGuard, XIGNCODE3,
   Anti-Cheat Expert (ACE), EQU8, PunkBuster and HoYoverse's anti-cheat from files in the game folder; plus Riot
   Vanguard (Valorant, League of Legends), Valve Anti-Cheat (Counter-Strike 2, Dota 2), Blizzard (Overwatch 2)
   and HoYoverse games by exe name, since those systems live outside the game folder. The lists are in
   `src\Core\GameAnalyzer.cs`; detection is best effort, so new anti-cheat systems need adding as they appear.
3. Copies the latest OptiScaler nightly (the v10 line, downloaded from OptiScaler's official
   `optiscaler/OptiScaler-nightly` releases on GitHub, checked at most once a day)
   into that folder. `OptiScaler.dll` is renamed to `dxgi.dll`, or to `winmm.dll` and so on if the game already
   ships a file with that name.
4. Copies the FSR 4.1.2 files you imported into the `OptiScaler` subfolder.
5. Writes the settings that replace the in-game menu steps into `OptiScaler.ini`:
   `Dx12Upscaler=ffx`, `Dx11Upscaler=ffx_12`, `VulkanUpscaler=ffx_12` (Vulkan through the DX12 bridge, where FSR 4.1.2
   is listed first; OptiScaler's native Vulkan path lists FSR 3 first and would pick it), `Fsr4GpuSupport=4` (INT8 Lite),
   `Fsr4Model=3` (INT8 Lite), and OptiScaler frame generation off.
6. Records every file it added or replaced in `.ralfsr\manifest.json`, with backups of anything replaced.
   **Uninstall from this game** uses that record to put the folder back exactly as it was.

All of these values live in `src\Core\InstallConfig.cs`.

### Render resolution

Installed games get a **Render resolution** picker on the game card: Game decides (default), Native AA, Quality,
Balanced or Performance. Anything but "Game decides" sets OptiScaler's upscale ratio override, so every
render-resolution request from the game gets that ratio whichever preset the game asks for. This fixes games that
stay on Native AA, such as Forza Horizon 6. The choice is saved per game and applies from the next launch.

### Automatic fixes

- **Stuck at full resolution:** if a game on "Game decides" renders at its display resolution (OptiScaler's log
  shows render size equal to display size), FSR 4 isn't upscaling. The widget switches that game to Quality (1.5x)
  once and says so on the card. An explicit choice in the picker, including Native AA, is never overridden.
- **FSR 3 instead of FSR 4:** if the game's upscaler started but no FSR 4 file loaded, the badge says FSR 3 is
  running and how to fix it.
- The card shows the live render resolution, e.g. "Rendering 1280x720 → 1920x1080".

### Is it active?

The game card has a live status badge, refreshed every 2 seconds while the widget is open. The helper reads which
DLLs the running game has loaded:

- **Amber, "Restart the game":** installed, but the game started before the install, so OptiScaler isn't loaded.
- **Amber, "Turn on FSR in the game's settings":** OptiScaler is loaded, but no FSR 4 library yet.
- **Green, "FSR 4.1 Lite active (DX12 / Vulkan)":** the FSR 4.1.2 file from the game's `OptiScaler` folder is
  loaded, and OptiScaler's log shows the Lite model being hooked. "Lite loaded" means the file is loaded but the log
  hasn't confirmed it yet.
- **Amber, "running, but not Lite":** FSR 4 came from another copy of the file, usually the AMD driver's own, which
  has no Lite model. OptiScaler uses a copy that's already in memory before its own.

Installs turn on OptiScaler's Info-level log (`OptiScaler.log` next to the game exe, fresh each session) so the
widget can read which file was loaded and which model was hooked.

Folder scans are cached for 2 minutes so the polling stays light on battery; only the loaded-DLL check runs each time.

### Notifications

With **Notify me when FSR 4.1 Lite is active** on (the default), a Windows notification confirms each game launch,
for example "FSR 4.1 Lite active / Forza Horizon 6 · DX12 / Rendering 1280x720 → 1920x1080". If FSR 4.1 Lite isn't
running (FSR 3 fallback, or the AMD driver's own file loaded), the notification says so and how to fix it.

A small watcher does this in the background: the helper starts with Windows (startup task "FSR 4.1 Lite
notifications") and, after the widget closes, keeps watching instead of exiting. Every 5 seconds it checks whether a
game with FSR 4.1 Lite installed is running, then reads that game's loaded DLLs and OptiScaler.log. Turning the
switch off stops the watcher and removes it from startup.

Windows 11 turns on Do not disturb during fullscreen games and holds notifications until you exit. To see them in
game, add **FSR 4.1 Lite by ROG Ally Life** under Settings > System > Notifications > Set priority notifications.

AMD's on-screen FSR 4 watermark (`Fsr4EnableWatermark`) is no longer offered in the widget; notifications replace it.

### What the widget never does

- AMD's FSR 4 files aren't bundled in the package. The helper downloads `amdxcffx64.dll` and `amdvkffx64.dll` from
  the `fsr4/` folder of ROG Ally Life's R2 bucket through its r2.dev address (URL in `src\Core\Fsr4Download.cs`).
  Every file must be a signed AMD driver file (WHQL or AMD signature, unmodified) and at least version 2.4.0.4370,
  or the download is discarded. Once a day the helper compares the files' ETags with two HEAD requests and downloads
  only when they changed, so uploading new DLLs over the old ones in `fsr4/` updates every user and every installed
  game.
- It doesn't bundle OptiScaler. It downloads it from OptiScaler's GitHub at runtime.
- Network access: OptiScaler from its GitHub releases, and the FSR 4 files from ROG Ally Life's R2 bucket.

## Credits

FSR 4.1 Lite is a front end. The upscaling is done by **[OptiScaler](https://github.com/optiscaler/OptiScaler)**,
the free, open-source tool created by **cdozdil** and the OptiScaler contributors, released under GPL-3.0. The
widget downloads OptiScaler's official nightly builds from
[optiscaler/OptiScaler-nightly](https://github.com/optiscaler/OptiScaler-nightly) unmodified, along with their
licence files. If FSR 4.1 Lite helps you, consider
[supporting the OptiScaler developer](https://github.com/sponsors/cdozdil).

FSR 4 is by AMD. Thanks to **TheRazerMD** for sharing the FSR 4.1 Lite driver files, and to everyone OptiScaler
itself thanks in its README.

## Installing (users)

1. Download **FSR4Lite_x64.zip** from the Releases page.
2. Unzip it.
3. Double-click **Install.cmd**. Windows asks for administrator permission once, to trust the ROG Ally Life
   certificate.
4. Open Game Bar (the **Armoury Crate** button on ROG handhelds, **Win + G** on others), open the Widgets menu and
   pick **FSR 4.1 Lite**. Pin it to keep it handy.

In the widget, OptiScaler and the FSR 4.1 Lite files download by themselves the first time it opens. Then launch
a game, open the widget over it and press **Install FSR 4.1 Lite**. **Import files manually** is only a fallback for
offline setups.

To update, download the new zip and run its Install.cmd. Settings and installed games carry over.

To uninstall, press **Uninstall FSR 4.1 Lite** at the bottom of the widget. It removes FSR 4.1 Lite from every game it
installed into (restoring each folder), then opens the widget's page in Windows Settings for the final Uninstall
click. Running games are skipped with a note to close them first. Uninstalling from Settings directly would leave
OptiScaler in game folders, so use the button.

## Publishing a release (you)

GitHub builds the widget, so you don't need Visual Studio or git, and there's nothing to set up for signing.

**Once:** create a public repository on github.com, click "uploading an existing file", and drag in everything
inside this folder, including `.github`. Commit.

**Each release:** Releases > Draft a new release > Choose a tag > type a new tag such as `v0.1.0` > Create new tag
> Publish release. A few minutes later **FSR4Lite_x64.zip** is attached to the release. Share that link. Progress
and any errors show in the Actions tab.

To change code later, edit or upload the changed files in the browser, then publish a release with the next tag
(`v0.1.1`, and so on).

Signing is automatic: each build is signed with a fresh self-signed "CN=ROG Ally Life" certificate, and Install.cmd
asks users to approve it. Updates still install over older versions, because Windows matches them by the publisher
name, which never changes. Users just approve the prompt again for each new version.

**Building on your own PC instead** (optional): install Visual Studio 2022 with the Universal Windows Platform
development workload and the .NET 8 SDK, then run `powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1`.
The output is `dist\FSR4Lite_x64.zip`.

## Device support

Support is decided by GPU family (`src\Core\DeviceCheck.cs`), so any AMD Radeon handheld works:

- **Supported (RDNA 3 / 3.5):** ROG Ally and ROG Ally X (Z1, Z1 Extreme), Legion Go, Legion Go 2, MSI Claw A8, and
  GPD, AYANEO and OneXPlayer handhelds with Ryzen 7040/8040 or Ryzen AI 300 chips. Unknown Radeon chips are allowed
  too, with a note.
- **Supported with a note:** ROG Xbox Ally X / X20. ASUS ships FSR 4.1 in Armoury Crate SE, but only for games with
  FSR 3.1 or newer; the widget also covers DLSS and XeSS games.
- **Experimental (RDNA 2):** ROG Xbox Ally (Z2 A), Legion Go S (Z2 Go), Steam Deck on Windows, Ryzen 6000 handhelds.
  Lite runs, but it costs more GPU time than FSR 3 on these chips, so some games may run slower.
- **Not supported:** handhelds without AMD graphics (Intel MSI Claw models). FSR 4's INT8 models need an AMD GPU.

## If the widget won't open

Errors while the widget starts are shown inside the widget and logged to `widget.log` in
`%LOCALAPPDATA%\Packages\ROGAllyLife.FSR4Lite_<id>\LocalState`. The helper logs to
`...\LocalCache\Local\RAL FSR Helper\helper.log` in the same package folder.

The build registers the Game Bar SDK's private interfaces in the package manifest from the SDK it restores
(`tools\GameBarProxyStub`). Without every one of them registered, Game Bar can't talk to the widget and shows
"something isn't playing nice".

## Tested so far

Tested in a Linux build environment against fake game folders: folder detection (Unreal and flat layouts),
anti-cheat blocking, proxy-name choice when a game ships its own `dxgi.dll`, ini editing, and a byte-for-byte
restore on Remove. The helper's logic and the widget's C# 7.3 syntax were compile-checked.

Not yet tested, because it needs Windows: the UWP build itself, Game Bar registration, the helper/widget
connection, foreground game detection, the live status badge (loaded-DLL check), the launcher readers (Epic, GOG, Ubisoft, Xbox) and the signature check.
The first build and a run on an Ally will show whether anything needs fixing.

## Layout

```
RALFsr4Lite.sln
.github\workflows\release.yml   GitHub build: publish a v* release, get the zip
scripts\build.ps1              build, sign and zip a release (GitHub runs this)
src\Installer\                 user installer, copied into the release zip as Install.cmd
tools\GameBarProxyStub\        build step: registers the Game Bar SDK's interfaces in the manifest
src\Core\                    install engine (shared, no UI)
src\RalFsr.Host\             full-trust helper: app service, game detection, commands
src\RalFsr.Widget\           Game Bar widget (UWP)
```
