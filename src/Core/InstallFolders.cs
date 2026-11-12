namespace RalFsrHelper.Core;

/// <summary>
/// Folders that are never a game's own folder, so OptiScaler is never put there, and a program running from one is
/// never taken for the game: Windows, Windows' shared program files (the touch keyboard, TabTip.exe, runs from
/// Common Files\microsoft shared\ink and can fill a handheld's screen), and the top of Program Files itself.
/// Everywhere else is allowed, because players keep games in all sorts of places: the Desktop, Downloads, the top of
/// a microSD card. Launchers that sit in such places (Content Manager on the Desktop) are skipped by name instead.
/// </summary>
public static class InstallFolders
{
    public static bool IsUnsafe(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return true;
        string full;
        try { full = Norm(Path.GetFullPath(dir)); }
        catch { return true; }

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

    /// <summary>Folders that hold games in subfolders but never have a game's exe directly inside.</summary>
    private static IEnumerable<string> Exactly() => Clean(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

    private static IEnumerable<string> Clean(params string?[] dirs) =>
        dirs.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => Norm(d!));

    private static string Norm(string d) => d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
