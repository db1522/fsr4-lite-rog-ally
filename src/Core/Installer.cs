namespace RalFsrHelper.Core;

/// <summary>
/// Installs OptiScaler plus the FSR 4 files into a game's exe folder with the Lite settings pre-set,
/// and removes them again. Every change is recorded in the manifest before the next one is made,
/// so a failed install can always be rolled back.
/// </summary>
public static class Installer
{
    public static InstallManifest Install(GameScan scan, OptiPackage opti, Fsr4Set fsr4, AppSettings settings)
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
                .Concat(InstallConfig.Overlay(settings.ShowResolutionOverlay))
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
    public static void ApplyOverlay(string exeDir, bool on)
    {
        var ini = Path.Combine(exeDir, "OptiScaler.ini");
        if (File.Exists(ini)) IniFile.SetValues(ini, InstallConfig.Overlay(on));
    }

    /// <summary>Changes only the watermark key in an installed game's ini.</summary>
    public static void ApplyWatermark(string exeDir, bool on)
    {
        var ini = Path.Combine(exeDir, "OptiScaler.ini");
        if (File.Exists(ini)) IniFile.SetValues(ini, InstallConfig.Watermark(on));
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
