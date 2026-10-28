namespace RalFsrHelper.Core;

/// <summary>
/// Installs OptiScaler plus the FSR 4 files into a game's exe folder with the Lite settings pre-set,
/// and removes them again. Every change is recorded in the manifest before the next one is made,
/// so a failed install can always be rolled back.
/// </summary>
public static class Installer
{
    public static InstallManifest Install(GameScan scan, OptiPackage opti, Fsr4Set fsr4, AppSettings settings,
        string? overlayScale = null)
    {
        var exeDir = scan.ExeDir ?? throw new InvalidOperationException("No exe folder for this game.");
        if (!fsr4.IsUsable) throw new InvalidOperationException("Import the FSR 4 files first.");
        if (InstallManifest.TryLoad(exeDir) is not null) throw new InvalidOperationException("Already installed.");

        var proxy = PickProxyName(exeDir);
        var manifest = new InstallManifest
        {
            GameKey = scan.Game.Key,
            Source = scan.Game.Source.ToString(),
            GameName = scan.Game.Name,
            ProxyName = proxy,
            OptiBuild = opti.Build,
            Fsr4Version = fsr4.VersionLabel,
        };

        try
        {
            Directory.CreateDirectory(InstallManifest.MetaDir(exeDir));
            manifest.Save(exeDir);

            // 1. OptiScaler package, with OptiScaler.dll renamed to the proxy name.
            foreach (var source in Directory.GetFiles(opti.Dir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(opti.Dir, source);
                if (InstallConfig.SkipPackageFiles.Contains(Path.GetFileName(rel), StringComparer.OrdinalIgnoreCase)) continue;
                if (Path.GetFileName(rel).StartsWith("!!", StringComparison.Ordinal)) continue; // readme stubs

                if (rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) rel = proxy;
                PlaceFile(source, exeDir, rel, manifest);
            }

            // 2. FSR 4 driver files into the OptiScaler subfolder (v10 layout).
            var sub = InstallConfig.OptiSubfolder;
            if (fsr4.Dx12 is not null)
                PlaceFile(fsr4.Dx12.Path, exeDir, Path.Combine(sub, InstallConfig.Dx12Fsr4File), manifest);
            if (fsr4.Vulkan is not null)
                PlaceFile(fsr4.Vulkan.Path, exeDir, Path.Combine(sub, InstallConfig.VulkanFsr4File), manifest);

            // 3. Settings that replace the in-game menu steps.
            var ini = Path.Combine(exeDir, "OptiScaler.ini");
            if (!manifest.CreatedFiles.Contains("OptiScaler.ini", StringComparer.OrdinalIgnoreCase))
            {
                // Package had no ini: track the one we are about to create.
                RecordNewFile(exeDir, "OptiScaler.ini", manifest);
            }
            IniFile.SetValues(ini, InstallConfig.Base(fsr4.Vulkan is not null)
                .Concat(InstallConfig.Overlay(settings.ShowResolutionOverlay, overlayScale ?? "0.8", settings.OverlayPosition))
                .Concat(InstallConfig.Watermark(settings.ShowFsr4Watermark)));

            manifest.InstalledAt = DateTime.Now;
            manifest.Save(exeDir);
            Log.Info($"Installed into {exeDir} as {proxy} (OptiScaler {opti.Build}, FSR 4 {manifest.Fsr4Version})");
            return manifest;
        }
        catch (Exception ex)
        {
            Log.Error($"Install failed for {scan.Game.Name}, rolling back", ex);
            try { Uninstall(exeDir, manifest); }
            catch (Exception rollbackEx) { Log.Error("Rollback failed", rollbackEx); }
            throw;
        }
    }

    public static void Uninstall(string exeDir, InstallManifest? manifest = null)
    {
        manifest ??= InstallManifest.TryLoad(exeDir) ?? throw new InvalidOperationException("No install record found.");

        foreach (var rel in manifest.CreatedFiles)
        {
            DeleteFile(Path.Combine(exeDir, rel));
        }

        // Files OptiScaler writes while running.
        DeleteFile(Path.Combine(exeDir, "OptiScaler.log"));

        // Files and folders REFramework creates while running (config, log, its reframework folder), unless the game
        // already had them before FSR 4.1 Lite added REFramework.
        if (manifest.REFrameworkVersion.Length > 0)
        {
            foreach (var path in REFrameworkArtifacts(exeDir))
            {
                if (manifest.REFrameworkPreexisting.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else DeleteFile(path);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Couldn't remove REFramework file {path}: {ex.Message}");
                }
            }
        }

        foreach (var rel in manifest.CreatedDirs.OrderByDescending(d => d.Length))
        {
            var dir = Path.Combine(exeDir, rel);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }

        var backupRoot = InstallManifest.BackupDir(exeDir);
        foreach (var rel in manifest.BackedUpFiles)
        {
            var backup = Path.Combine(backupRoot, rel);
            if (!File.Exists(backup)) continue;
            var target = Path.Combine(exeDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(backup, target, overwrite: true);
        }

        var meta = InstallManifest.MetaDir(exeDir);
        if (Directory.Exists(meta)) Directory.Delete(meta, recursive: true);
        Log.Info($"Removed install from {exeDir}");
    }

    /// <summary>
    /// Copies newly imported FSR 4 files over the ones in an installed game. Only touches files this app created.
    /// Returns false if the game has the files locked (it's running).
    /// </summary>
    public static bool RefreshFsr4(string exeDir, Fsr4Set fsr4)
    {
        var manifest = InstallManifest.TryLoad(exeDir);
        if (manifest is null) return true;
        try
        {
            foreach (var (file, name) in new[] { (fsr4.Dx12, InstallConfig.Dx12Fsr4File), (fsr4.Vulkan, InstallConfig.VulkanFsr4File) })
            {
                if (file is null) continue;
                var rel = Path.Combine(InstallConfig.OptiSubfolder, name);
                Directory.CreateDirectory(Path.Combine(exeDir, InstallConfig.OptiSubfolder));
                if (!File.Exists(Path.Combine(exeDir, rel))) RecordNewFile(exeDir, rel, manifest);
                File.Copy(file.Path, Path.Combine(exeDir, rel), overwrite: true);
            }
            // A newly imported Vulkan file switches Vulkan games to the native path.
            var ini = Path.Combine(exeDir, "OptiScaler.ini");
            if (File.Exists(ini)) IniFile.SetValues(ini, new[] { InstallConfig.VulkanUpscaler(fsr4.Vulkan is not null) });

            manifest.Fsr4Version = fsr4.VersionLabel;
            manifest.Save(exeDir);
            return true;
        }
        catch (IOException ex)
        {
            Log.Warn($"FSR 4 refresh skipped for {exeDir}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Changes only the overlay keys in an installed game's ini.</summary>
    public static void ApplyOverlay(string exeDir, bool on, string scale = "0.8", string position = "topleft")
    {
        var ini = Path.Combine(exeDir, "OptiScaler.ini");
        if (File.Exists(ini)) IniFile.SetValues(ini, InstallConfig.Overlay(on, scale, position));
    }

    /// <summary>Sets this game's render resolution (OptiScaler's upscale ratio override) and remembers it.</summary>
    public static void ApplyRenderScale(string exeDir, string key)
    {
        if (InstallConfig.RenderScales.All(r => r.Key != key)) key = "game";
        var manifest = InstallManifest.TryLoad(exeDir);
        if (manifest is null) return;

        var ini = Path.Combine(exeDir, "OptiScaler.ini");
        if (File.Exists(ini)) IniFile.SetValues(ini, InstallConfig.RenderScale(key));

        if (manifest.RenderScale != key)
        {
            manifest.RenderScale = key;
            manifest.Save(exeDir);
        }
    }

    /// <summary>Changes only the watermark key in an installed game's ini.</summary>
    public static void ApplyWatermark(string exeDir, bool on)
    {
        var ini = Path.Combine(exeDir, "OptiScaler.ini");
        if (File.Exists(ini)) IniFile.SetValues(ini, InstallConfig.Watermark(on));
    }

    /// <summary>
    /// Renames OptiScaler's loader to the next name the game doesn't already use (dxgi.dll, winmm.dll, version.dll, ...).
    /// Some games, often Game Pass ones, ignore one name and load another. Returns the new name.
    /// </summary>
    public static string SwitchProxy(string exeDir)
    {
        var manifest = InstallManifest.TryLoad(exeDir) ?? throw new InvalidOperationException("FSR 4.1 Lite isn't installed here.");
        var names = InstallConfig.ProxyNames;
        var start = Array.FindIndex(names, n => n.Equals(manifest.ProxyName, StringComparison.OrdinalIgnoreCase));
        string? next = null;
        for (var i = 1; i <= names.Length; i++)
        {
            var candidate = names[(start + i + names.Length) % names.Length];
            if (candidate.Equals(manifest.ProxyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(Path.Combine(exeDir, candidate))) { next = candidate; break; }
        }
        if (next is null) throw new InvalidOperationException("Every loader name is already used by the game.");

        var from = Path.Combine(exeDir, manifest.ProxyName);
        if (File.Exists(from)) File.Move(from, Path.Combine(exeDir, next));

        var index = manifest.CreatedFiles.FindIndex(f => f.Equals(manifest.ProxyName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) manifest.CreatedFiles[index] = next; else manifest.CreatedFiles.Add(next);
        manifest.ProxyName = next;
        manifest.InstalledAt = DateTime.Now; // the next launch counts as "after the change"
        manifest.Save(exeDir);
        Log.Info($"Switched OptiScaler loader in {exeDir} to {next}");
        return next;
    }

    /// <summary>
    /// Brings an installed game up to a newer OptiScaler build: copies the package's files over the old ones, keeping the
    /// game's loader name. OptiScaler.ini is never replaced, so the game keeps its settings (render resolution, loader,
    /// FSR 2 inputs); the FSR 4 files aren't part of the package and stay as they are. Files the game shipped itself are
    /// backed up first, exactly like a fresh install, so Uninstall still restores the folder byte for byte.
    /// The game must not be running. Returns false when it was already on this build.
    /// </summary>
    public static bool UpdateOpti(string exeDir, OptiPackage opti)
    {
        var manifest = InstallManifest.TryLoad(exeDir) ?? throw new InvalidOperationException("FSR 4.1 Lite isn't installed here.");
        if (manifest.OptiBuild.Equals(opti.Build, StringComparison.OrdinalIgnoreCase)) return false;

        foreach (var source in Directory.GetFiles(opti.Dir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(opti.Dir, source);
            var name = Path.GetFileName(rel);
            if (InstallConfig.SkipPackageFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (name.StartsWith("!!", StringComparison.Ordinal)) continue;
            if (rel.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase)) continue; // keep the game's settings

            if (rel.Equals("OptiScaler.dll", StringComparison.OrdinalIgnoreCase)) rel = manifest.ProxyName;
            PlaceFile(source, exeDir, rel, manifest);
        }

        var previous = manifest.OptiBuild;
        manifest.OptiBuild = opti.Build;
        manifest.Save(exeDir);
        Log.Info($"Updated OptiScaler in {exeDir}: {previous} -> {opti.Build}");
        return true;
    }

    public enum RefResult { Installed, Updated, AlreadyCurrent, GameHasOwnDinput8 }

    /// <summary>
    /// Adds (or refreshes) REFramework's dinput8.dll next to the game exe, tracked in the manifest like every other file.
    /// If the game already has a dinput8.dll that FSR 4.1 Lite didn't put there (the player's own REFramework or another
    /// mod), it's left alone.
    /// </summary>
    public static RefResult AddREFramework(string exeDir, RefPackage pkg)
    {
        var manifest = InstallManifest.TryLoad(exeDir) ?? throw new InvalidOperationException("FSR 4.1 Lite isn't installed here.");
        const string name = "dinput8.dll";
        var ours = manifest.CreatedFiles.Contains(name, StringComparer.OrdinalIgnoreCase);
        if (!ours && File.Exists(Path.Combine(exeDir, name))) return RefResult.GameHasOwnDinput8;
        if (ours && manifest.REFrameworkVersion.Equals(pkg.Version, StringComparison.OrdinalIgnoreCase))
        {
            HideREFrameworkMenuAtStartup(exeDir, manifest);
            return RefResult.AlreadyCurrent;
        }

        if (!ours)
            manifest.REFrameworkPreexisting = REFrameworkArtifacts(exeDir).Select(p => Path.GetFileName(p)).ToList();
        PlaceFile(pkg.DllPath, exeDir, name, manifest);
        var result = manifest.REFrameworkVersion.Length == 0 ? RefResult.Installed : RefResult.Updated;
        manifest.REFrameworkVersion = pkg.Version;
        manifest.Save(exeDir);
        HideREFrameworkMenuAtStartup(exeDir, manifest);
        Log.Info($"REFramework {pkg.Version} {(result == RefResult.Installed ? "added to" : "updated in")} {exeDir}");
        return result;
    }

    /// <summary>
    /// REFramework opens its menu on every launch by default. Its config (re2_fw_config.txt next to the exe, the same name
    /// for every game) can make it start closed: with RememberMenuState on it restores the saved MenuOpen state at launch.
    /// Only written for configs FSR 4.1 Lite owns, never one the player already had; Insert still opens the menu.
    /// Call only while the game isn't running (REFramework saves its own config while it runs).
    /// </summary>
    private static void HideREFrameworkMenuAtStartup(string exeDir, InstallManifest manifest)
    {
        const string configName = "re2_fw_config.txt";
        if (manifest.REFrameworkPreexisting.Contains(configName, StringComparer.OrdinalIgnoreCase)) return;
        var path = Path.Combine(exeDir, configName);
        try
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            var changed = false;
            foreach (var (key, value) in new[] { ("REFrameworkConfig_RememberMenuState", "true"), ("REFrameworkConfig_MenuOpen", "false") })
            {
                var i = lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
                var line = $"{key}={value}";
                if (i < 0) { lines.Add(line); changed = true; }
                else if (lines[i] != line) { lines[i] = line; changed = true; }
            }
            if (changed) File.WriteAllLines(path, lines);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't set REFramework's menu to start closed in {exeDir}: {ex.Message}");
        }
    }

    /// <summary>REFramework's runtime files next to the exe: the reframework folder, *_fw_config/log files, its log.</summary>
    private static IEnumerable<string> REFrameworkArtifacts(string exeDir)
    {
        if (!Directory.Exists(exeDir)) yield break;
        var dir = Path.Combine(exeDir, "reframework");
        if (Directory.Exists(dir)) yield return dir;
        foreach (var file in Directory.GetFiles(exeDir))
        {
            var n = Path.GetFileName(file);
            if (System.Text.RegularExpressions.Regex.IsMatch(n, @"^(re\d*_?framework.*|[a-z0-9]+_fw_(config|log).*)\.(txt|log|json)$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                yield return file;
        }
    }

    /// <summary>First proxy name that doesn't clash with a file the game already ships.</summary>
    public static string PickProxyName(string exeDir) =>
        InstallConfig.ProxyNames.FirstOrDefault(n => !File.Exists(Path.Combine(exeDir, n)))
        ?? throw new InvalidOperationException("Every OptiScaler file name is already taken in this folder.");

    private static void PlaceFile(string source, string exeDir, string rel, InstallManifest manifest)
    {
        var target = Path.Combine(exeDir, rel);
        EnsureDirs(exeDir, Path.GetDirectoryName(rel), manifest);

        if (File.Exists(target))
        {
            if (!manifest.CreatedFiles.Contains(rel, StringComparer.OrdinalIgnoreCase) &&
                !manifest.BackedUpFiles.Contains(rel, StringComparer.OrdinalIgnoreCase))
            {
                var backup = Path.Combine(InstallManifest.BackupDir(exeDir), rel);
                Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
                File.Copy(target, backup, overwrite: true);
                manifest.BackedUpFiles.Add(rel);
                manifest.Save(exeDir);
            }
        }
        else
        {
            RecordNewFile(exeDir, rel, manifest);
        }

        File.Copy(source, target, overwrite: true);
    }

    private static void RecordNewFile(string exeDir, string rel, InstallManifest manifest)
    {
        if (manifest.CreatedFiles.Contains(rel, StringComparer.OrdinalIgnoreCase)) return;
        manifest.CreatedFiles.Add(rel);
        manifest.Save(exeDir);
    }

    private static void EnsureDirs(string exeDir, string? relDir, InstallManifest manifest)
    {
        if (string.IsNullOrEmpty(relDir)) return;
        var parts = relDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var current = "";
        foreach (var part in parts)
        {
            current = current.Length == 0 ? part : Path.Combine(current, part);
            var full = Path.Combine(exeDir, current);
            if (Directory.Exists(full)) continue;
            Directory.CreateDirectory(full);
            manifest.CreatedDirs.Add(current);
            manifest.Save(exeDir);
        }
    }

    private static void DeleteFile(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }
}
