# FSR 4.1 Lite by ROG Ally Life (Game Bar widget)

A free Xbox Game Bar widget from ROG Ally Life. Open Game Bar over a game on a ROG Ally, Legion Go or any other
AMD Radeon handheld,
press **Install FSR 4.1 Lite**, restart the game, and turn on FSR in its graphics settings.
FSR 4.1 Lite loads automatically, with no OptiScaler menu to open.

Works with games from any store: Steam, Epic, GOG, Ubisoft Connect, the EA app, Battle.net, itch.io or
DRM-free installs. The widget works from the running game's exe, so the launcher doesn't matter.
Xbox app / Game Pass games work too (confirmed with Valor Mortis). If a game ignores OptiScaler's loader, the
widget offers **Try another loader**.

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
   `Fsr4Model=3` (INT8 Lite), OptiScaler frame generation off, and nothing on screen: no OptiScaler startup message
   (`DisableSplash=true`) and no AMD FSR 4 watermark (`Fsr4EnableWatermark=false`).
6. Records every file it added or replaced in `.ralfsr\manifest.json`, with backups of anything replaced.
   **Uninstall from this game** uses that record to put the folder back exactly as it was.

All of these values live in `src\Core\InstallConfig.cs`.

### Game compatibility notes

Each game card shows one line from OptiScaler's community
[Compatibility List](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List) when the game is in it: the
upscalers that work, for example "Works with: DLSS, FSR3.1, XeSS", or a warning for the few games marked as not
working. The widget downloads the list's raw Markdown once a day and caches it (`compat-list.md`), matching games by
name with edition words removed, so "Ghost of Tsushima" finds the DIRECTOR'S CUT entry but "Control" never matches
"CONTROL Resonant".

### Updating OptiScaler in installed games

New OptiScaler nightlies download automatically, but games keep the build they were installed with until they're
updated. When installed games are on an older build, the Installed section shows **Update N games**, and a
notification says so once per new build. Updating copies the new package over the old files, keeps each game's
loader name, and never replaces `OptiScaler.ini`, so each game keeps its own settings. Running games are skipped.
Uninstall still restores the folder byte for byte.

### FSR 2 games

OptiScaler upgrades FSR 2 to FSR 4.1.2 Lite as well. At install the widget reads the running game's graphics API
and sets what OptiScaler needs: `UseFsr2Dx11Inputs` for DX11 games and `UseFsr2VulkanInputs` for Vulkan games that
ship an FSR 2 DLL. When no upscaler files are found at all (FSR 2 or 3 built into the exe), it turns on
`Fsr2Pattern` and `Fsr3Pattern` so OptiScaler searches the exe; only those games load a little slower. FSR 1 can't
be upgraded: it has no motion vectors or depth for FSR 4 to use. If a game offers DLSS or XeSS as well, those
inputs are the most reliable choice.

### Render resolution

Installed games get a **Render resolution** picker on the game card: Game decides (default), Native AA, Quality,
Balanced or Performance. Anything but "Game decides" sets OptiScaler's upscale ratio override, so every
render-resolution request from the game gets that ratio whichever preset the game asks for. It works for games
that ask the upscaler for their render resolution. The choice is saved per game and applies from the next launch.

Some games only apply their upscaler preset after their display resolution changes. Forza Horizon 6 is one: after
launch it kept the full-size upscaler it created at startup ("color_in extents (0,0,1600,900)", ratio 1) whichever
preset was picked, then upscaled properly once the resolution was switched to 1080p and back to 900p. The widget reads
the size a game really hands over (Streamline's color_in extents when present) and, when a game renders at full
resolution, suggests changing the resolution once and back.

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

FSR 4 is by AMD. Thanks to **TheRazerMD** for sharing the FSR 4.1.2 Lite driver files, to the OptiScaler
community for the [Compatibility List](https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List) shown on
game cards, and to everyone OptiScaler itself thanks in its README.

FSR 4.1 Lite is free. If it saves you time, you can
[support ROG Ally Life on Ko-fi](https://ko-fi.com/rogallylife).

## Installing (users)

1. Download **FSR4Lite_x64.zip** from the Releases page.
2. Unzip it.
3. Double-click **Install.cmd**. Windows asks for administrator permission once, to trust the ROG Ally Life
   certificate.
4. Open Game Bar (the **Armoury Crate** button on ROG handhelds, **Win + G** on others), open the Widgets menu and
   pick **FSR 4.1 Lite**. Pin it to keep it handy.

In the widget, OptiScaler and the FSR 4.1 Lite files download by themselves the first time it opens. Then launch
a game, open the widget over it and press **Install FSR 4.1 Lite**. **Import files manually** only appears if that
download fails (for example offline).

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

## Current limitations

- FSR 4.1.2 Lite costs more GPU time than FSR 3, so at the same preset it can be slower; the gain is image quality,
  or a lower preset that still looks good.
- Some games only apply their upscaler preset after a display resolution change (Forza Horizon 6 needed this once).
  The card says when a game renders at full resolution.
- Needs a game with FSR 2, FSR 3, DLSS or XeSS. FSR 1 can't be upgraded (no motion vectors or depth).
- Single-player only. Anti-cheat detection is best effort.
- Some Game Pass games need **Try another loader**; games in the protected WindowsApps folder can't be changed.
- Tested on the ROG Ally X. Other AMD handhelds are classified by chip; RDNA 2 handhelds are experimental.
- OptiScaler nightlies update automatically and can occasionally break a game.
- Install asks for administrator permission once (self-signed certificate), and Windows holds notifications during
  fullscreen games unless the widget is a priority app.

## Device support

Support is decided by GPU family (`src\Core\DeviceCheck.cs`), so any AMD Radeon handheld works:

- **Supported (RDNA 3 / 3.5):** ROG Ally and ROG Ally X (Z1, Z1 Extreme), Legion Go, Legion Go 2, MSI Claw A8, and
  GPD, AYANEO and OneXPlayer handhelds with Ryzen 7040/8040 or Ryzen AI 300 chips. Unknown Radeon chips are allowed
  too, with a note.
- **Supported with a note:** ROG Xbox Ally X / X20. ASUS ships FSR 4.1 in Armoury Crate SE, but its toggle only
  upgrades games with FSR 3.1 or newer; the widget also covers games that only offer DLSS, XeSS or FSR 2.
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
