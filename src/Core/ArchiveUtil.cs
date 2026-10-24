using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace RalFsrHelper.Core;

public static class ArchiveUtil
{
    /// <summary>Extracts a .7z or .zip without needing 7-Zip installed.</summary>
    public static void ExtractAll(string archivePath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ArchiveFactory.Open(archivePath);
        using var reader = archive.ExtractAllEntries();
        reader.WriteAllToDirectory(destination, new ExtractionOptions
        {
            ExtractFullPath = true,
            Overwrite = true,
        });
    }

    public static bool IsArchive(string path) =>
        path.EndsWith(".7z", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
}
