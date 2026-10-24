namespace RalFsrHelper.Core;

public enum GameSource { Steam, Epic, Gog, Ubisoft, Xbox, Manual }

/// <summary>One installed game, from any launcher. The installer only needs InstallRoot.</summary>
public sealed record GameEntry(GameSource Source, string Key, string Name, string InstallRoot)
{
    /// <summary>Steam App ID when the game came from Steam.</summary>
    public int? SteamAppId { get; init; }

    /// <summary>Manually added games point at a specific exe, which skips folder detection.</summary>
    public string? ExePath { get; init; }

    public string SourceLabel => Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic",
        GameSource.Gog => "GOG",
        GameSource.Ubisoft => "Ubisoft Connect",
        GameSource.Xbox => "Xbox app",
        _ => "Added by you",
    };
}
