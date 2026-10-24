namespace RalFsrHelper.Core;

/// <summary>Where the app keeps its own files. Nothing here is ever written into a game folder.</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RAL FSR Helper");

    /// <summary>Downloaded OptiScaler nightly packages, one subfolder per build.</summary>
    public static string OptiCache => Path.Combine(Root, "optiscaler");

    /// <summary>The FSR 4 driver files the user imported (amdxcffx64.dll / amdvkffx64.dll).</summary>
    public static string Fsr4Dir => Path.Combine(Root, "fsr4");

    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogFile => Path.Combine(Root, "helper.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(OptiCache);
        Directory.CreateDirectory(Fsr4Dir);
    }
}
