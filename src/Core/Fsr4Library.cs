using System.Diagnostics;

namespace RalFsrHelper.Core;

public sealed record Fsr4File(string Path, string Version, SignatureCheck Signature);

public sealed record Fsr4Set(Fsr4File? Dx12, Fsr4File? Vulkan)
{
    public bool IsUsable => Dx12 is not null;
    public string VersionLabel => Dx12?.Version ?? Vulkan?.Version ?? "none";
}

public sealed record Fsr4Candidate(string SourcePath, string FileName, string Version, SignatureCheck Signature);

/// <summary>
/// Holds the FSR 4 driver files the user imports once (from the OptiScaler Discord post).
/// AMD's files are never bundled with or downloaded by this app.
/// </summary>
public static class Fsr4Library
{
    /// <summary>FSR 4.1.2 driver library version from driver 26.10.1, the first with INT8 Lite for APUs.</summary>
    public static readonly Version MinimumDx12Version = new(2, 4, 0, 4370);

    public static Fsr4Set Load()
    {
        AppPaths.Ensure();
        return new Fsr4Set(Describe(Path.Combine(AppPaths.Fsr4Dir, InstallConfig.Dx12Fsr4File)),
                           Describe(Path.Combine(AppPaths.Fsr4Dir, InstallConfig.VulkanFsr4File)));
    }

    /// <summary>Finds FSR 4 DLLs in the picked files, unpacking any .7z / .zip into a temp folder first.</summary>
    public static List<Fsr4Candidate> FindCandidates(IEnumerable<string> pickedPaths, string tempDir)
    {
        var found = new List<Fsr4Candidate>();
        Directory.CreateDirectory(tempDir);

        foreach (var picked in pickedPaths)
        {
            IEnumerable<string> dlls;
            if (ArchiveUtil.IsArchive(picked))
            {
                var dest = Path.Combine(tempDir, Guid.NewGuid().ToString("N"));
                ArchiveUtil.ExtractAll(picked, dest);
                dlls = Directory.GetFiles(dest, "*.dll", SearchOption.AllDirectories);
            }
            else
            {
                dlls = new[] { picked };
            }

            foreach (var dll in dlls)
            {
                var name = Path.GetFileName(dll);
                if (!name.Equals(InstallConfig.Dx12Fsr4File, StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(InstallConfig.VulkanFsr4File, StringComparison.OrdinalIgnoreCase)) continue;

                found.Add(new Fsr4Candidate(dll, name.ToLowerInvariant(), ReadVersion(dll), WinTrust.Check(dll)));
            }
        }

        return found;
    }

    public static void Store(IEnumerable<Fsr4Candidate> accepted)
    {
        AppPaths.Ensure();
        foreach (var c in accepted)
        {
            File.Copy(c.SourcePath, Path.Combine(AppPaths.Fsr4Dir, c.FileName), overwrite: true);
            Log.Info($"Imported {c.FileName} {c.Version} ({c.Signature})");
        }
    }

    public static bool IsOlderThanMinimum(Fsr4Candidate c) =>
        c.FileName.Equals(InstallConfig.Dx12Fsr4File, StringComparison.OrdinalIgnoreCase) &&
        Version.TryParse(c.Version, out var v) && v < MinimumDx12Version;

    private static Fsr4File? Describe(string path) =>
        File.Exists(path) ? new Fsr4File(path, ReadVersion(path), WinTrust.Check(path)) : null;

    private static string ReadVersion(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (info.FileMajorPart + info.FileMinorPart + info.FileBuildPart + info.FilePrivatePart > 0)
                return $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            return info.FileVersion?.Trim() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
