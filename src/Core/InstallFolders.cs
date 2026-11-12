namespace RalFsrHelper.Core;

/// <summary>
/// Folders that are never a game's own folder, so OptiScaler is never put there, and a program running from one is
/// never taken for the game: Windows and its shared program files (the touch keyboard, TabTip.exe, runs from
/// Common Files\microsoft shared\ink and can fill a handheld's screen), the top of Program Files and of a drive, and
/// the user's own folders themselves (Content Manager, Assetto Corsa's launcher, often sits on the Desktop).
/// Games in subfolders of Desktop, Documents or Downloads are still fine.
/// </summary>
public static class InstallFolders
{
    public static bool IsUnsafe(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        string full;
        try { full = Norm(Path.GetFullPath(dir)); }
        catch { return true; }

        if (Path.GetPathRoot(full) is { } root && Norm(root).Equals(full, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var under in Below())
            if (full.Equals(under, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(under + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var exact in Exactly())
            if (full.Equals(exact, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Folders where nothing inside is a game.</summary>
    private static IEnumerable<string> Below() => Clean(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));

    /// <summary>Folders that hold games in subfolders but are never a game's folder themselves.</summary>
    private static IEnumerable<string> Exactly()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var oneDrive = Environment.GetEnvironmentVariable("OneDrive");
        return Clean(
            home,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Join(home, "Desktop"), Join(home, "Documents"), Join(home, "Downloads"),
            oneDrive, Join(oneDrive, "Desktop"), Join(oneDrive, "Documents"));
    }

    private static string? Join(string? a, string b) => string.IsNullOrEmpty(a) ? null : Path.Combine(a, b);

    private static IEnumerable<string> Clean(params string?[] dirs) =>
        dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Norm(d!));

    private static string Norm(string d) => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
