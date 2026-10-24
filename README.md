# FSR 4 Lite for ROG Ally (Game Bar widget)

A free Xbox Game Bar widget from ROG Ally Life. Open Game Bar over a game on a ROG Ally or ROG Ally X,
press **Install FSR 4 Lite**, restart the game, and turn on FSR in its graphics settings.
FSR 4.1.2 Lite loads automatically, with no OptiScaler menu to open.

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
3. Copies the latest OptiScaler nightly (the v10 line, downloaded from OptiScaler's official GitHub release)
   into that folder. `OptiScaler.dll` is renamed to `dxgi.dll`, or to `winmm.dll` and so on if the game already
   ships a file with that name.
4. Copies the FSR 4.1.2 files you imported into the `OptiScaler` subfolder.
5. Writes the settings that replace the in-game menu steps into `OptiScaler.ini`:
   `Dx12Upscaler=ffx`, `Dx11Upscaler=ffx_12`, `VulkanUpscaler=ffx` (native Vulkan, when the Vulkan file is imported; `ffx_12` otherwise), `Fsr4GpuSupport=4` (INT8 Lite),
   `Fsr4Model=3` (INT8 Lite), and OptiScaler frame generation off.
6. Records every file it added or replaced in `.ralfsr\manifest.json`, with backups of anything replaced.
   **Uninstall from this game** uses that record to put the folder back exactly as it was.

All of these values live in `src\Core\InstallConfig.cs`.

### Is it active?

The game card has a live status badge, refreshed every 2 seconds while the widget is open. The helper reads which
DLLs the running game has loaded:

- **Amber, "Restart the game":** installed, but the game started before the install, so OptiScaler isn't loaded.
- **Amber, "Turn on FSR in the game's settings":** OptiScaler is loaded, but no FSR 4 library yet.
- **Green, "FSR 4 Lite active (DX12 / Vulkan)":** the FSR 4.1.2 library from the game's `OptiScaler` folder is
  loaded.

Folder scans are cached for 2 minutes so the polling stays light on battery; only the loaded-DLL check runs each time.

For on-screen proof, the **Show the FSR 4 watermark in game** switch sets OptiScaler's `Fsr4EnableWatermark`, so
the game shows the FSR 4 watermark while it runs.

### What the widget never does

- It doesn't bundle or download AMD's FSR 4 files. Users import them once from the OptiScaler Discord post,
  the same way OptiScaler itself handles them. The widget checks on import that each file is a signed AMD driver file (WHQL or AMD signature, unmodified) and at least version 2.4.0.4370.
- It doesn't bundle OptiScaler. It downloads it from OptiScaler's GitHub at runtime.
- The only network access is the OptiScaler download from GitHub.

## Installing (users)

1. Download **FSR4Lite_x64.zip** from the Releases page.
2. Unzip it.
3. Double-click **Install.cmd**. Windows asks for administrator permission once, to trust the ROG Ally Life
   certificate.
4. Press Win + G, open the Widgets menu and pick **FSR 4 Lite**. Pin it to keep it handy.

In the widget, OptiScaler downloads by itself. Press **Import FSR 4 files** and pick the two files from the
OptiScaler Discord: `Official_RDNA2_and_APUs_amdxcffx64.7z` and `FSR4_Vulkan_amdvkffx64.7z`. Then launch a game,
open the widget over it and press **Install FSR 4 Lite**.

To update, download the new zip and run its Install.cmd. Settings and installed games carry over.

To uninstall, press **Uninstall FSR 4 Lite** at the bottom of the widget. It removes FSR 4 Lite from every game it
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

- **ROG Ally / ROG Ally X (Ryzen Z1, Z1 Extreme):** supported.
- **ROG Xbox Ally X / X20 (Ryzen AI Z2 Extreme):** installs are turned off. ASUS ships FSR 4.1 for these in
  Armoury Crate SE (Update Center > Preview Updates), and the widget points there.
- **Anything else:** installs are turned off.

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
src\Core\                    install engine (shared, no UI)
src\RalFsr.Host\             full-trust helper: app service, game detection, commands
src\RalFsr.Widget\           Game Bar widget (UWP)
```
