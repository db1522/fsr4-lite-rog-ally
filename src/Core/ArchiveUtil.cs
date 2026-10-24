using SharpCompress.Archives;

namespace RalFsrHelper.Core;

public static class ArchiveUtil
{
    /// <summary>
    /// Extracts a .7z or .zip without needing 7-Zip installed. Each entry is written by hand after checking that its
    /// path stays inside the destination, so a crafted archive can't write elsewhere (zip slip). This deliberately
    /// avoids SharpCompress's WriteToDirectory helpers, which were the subject of GHSA-6c8g-7p36-r338.
    /// </summary>
    public static void ExtractAll(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var rootWithSlash = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.IsDirectory || string.IsNullOrEmpty(entry.Key)) continue;

            var target = Path.GetFullPath(Path.Combine(root, entry.Key.Replace('\\', '/').TrimStart('/')));
            if (!target.StartsWith(rootWithSlash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The archive contains an unsafe path: {entry.Key}");

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.OpenEntryStream();
            using var output = File.Create(target);
            input.CopyTo(output);
        }
    }

    public static bool IsArchive(string path) =>
        path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
