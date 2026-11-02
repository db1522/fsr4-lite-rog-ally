using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RalFsrHelper.Core;

/// <summary>Recommended frame generation settings for one game (data/framegen-presets.json).</summary>
public sealed class FrameGenPreset
{
    public string Name { get; set; } = "";
    public List<string> Aliases { get; set; } = new();
    public string Output { get; set; } = FrameGen.DefaultOutput;
    public bool HudFix { get; set; } = true;
    public int HudLimit { get; set; } = FrameGen.DefaultHudLimit;
    public bool Immediate { get; set; }

    /// <summary>Overrides the automatic input choice (dlssg, nukems or optifg); XboxInput for the Xbox app version only.</summary>
    public string? Input { get; set; }
    public string? XboxInput { get; set; }

    public string Note { get; set; } = "";

    /// <summary>rogallylife (tested on a ROG Ally X) or wiki (OptiScaler's game page).</summary>
    public string Source { get; set; } = "";

    /// <summary>The input this preset asks for in this copy of the game, or null to keep the automatic choice.</summary>
    public string? InputFor(GameSource source)
    {
        var input = source == GameSource.Xbox && !string.IsNullOrEmpty(XboxInput) ? XboxInput : Input;
        return input is FrameGen.Dlssg or FrameGen.Nukems or FrameGen.OptiFg ? input : null;
    }

    public string SourceText => Source == "rogallylife" ? "Recommended settings from ROG Ally Life's testing." : "Recommended settings from OptiScaler's wiki.";
}

/// <summary>
/// Per-game frame generation presets: ROG Ally Life's own list (data/framegen-presets.json in this repository, built in and
/// fetched from GitHub every 6 hours, so games can be added without a widget update) plus OptiScaler's wiki list of games
/// where HUDFix doesn't work (fetched daily; the copy in the preset file is the fallback).
/// </summary>
public static class FrameGenPresets
{
    public const string RawUrl = "https://raw.githubusercontent.com/db1522/fsr4-lite-rog-ally/main/data/framegen-presets.json";
    public const string HudFixWikiUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Hudfix-incompatible.md";

    private static string PresetCache => Path.Combine(AppPaths.Root, "framegen-presets.json");
    private static string HudFixCache => Path.Combine(AppPaths.Root, "hudfix-incompatible.md");
    private static readonly TimeSpan PresetEvery = TimeSpan.FromHours(6);
    private static readonly TimeSpan WikiEvery = TimeSpan.FromHours(24);

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/1.0");
        return c;
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly object Gate = new();
    private static Dictionary<string, FrameGenPreset>? _presets;
    private static HashSet<string>? _hudFixIncompatible;

    private sealed class PresetFile
    {
        /// <summary>Raised with every change to the list. The saved online copy and the built-in copy: the higher wins.</summary>
        public int Version { get; set; }
        public List<FrameGenPreset> Games { get; set; } = new();
        public List<string> HudFixIncompatible { get; set; } = new();
    }

    /// <summary>Fetches newer copies of both lists when the cached ones are old. Never throws.</summary>
    public static async Task RefreshAsync()
    {
        AppPaths.Ensure();
        await RefreshFileAsync(RawUrl, PresetCache, PresetEvery, text => Parse(text)?.Games.Count > 0);
        await RefreshFileAsync(HudFixWikiUrl, HudFixCache, WikiEvery, text => ParseWikiList(text).Count > 0);
    }

    private static async Task RefreshFileAsync(string url, string cache, TimeSpan every, Func<string, bool> looksRight)
    {
        try
        {
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < every) return;
            var text = await Http.GetStringAsync(url);
            if (!looksRight(text)) return;
            await File.WriteAllTextAsync(cache, text, Encoding.UTF8);
            lock (Gate) { _presets = null; _hudFixIncompatible = null; }
            Log.Info($"Frame generation list updated: {Path.GetFileName(cache)}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't refresh {Path.GetFileName(cache)}: {ex.Message}");
        }
    }

    /// <summary>The preset for a game by its name (or an alias), or null when the list has none.</summary>
    public static FrameGenPreset? Find(string gameName)
    {
        lock (Gate)
        {
            Load();
            return _presets!.TryGetValue(CompatList.Key(gameName), out var exact) ? exact
                : _presets.TryGetValue(CompatList.BaseKey(gameName), out var loose) ? loose : null;
        }
    }

    /// <summary>OptiScaler's wiki lists this game as one where HUDFix can't find the HUD, so OptiFG ghosts menus.</summary>
    public static bool HudFixIncompatible(string gameName)
    {
        lock (Gate)
        {
            Load();
            return _hudFixIncompatible!.Contains(CompatList.Key(gameName)) || _hudFixIncompatible.Contains(CompatList.BaseKey(gameName));
        }
    }

    private static void Load()
    {
        if (_presets is not null && _hudFixIncompatible is not null) return;
        // The saved online copy, unless the copy built into this widget version is newer (a release can carry a list
        // change before the 6-hourly check fetches it).
        var saved = ReadCached(PresetCache) is { } cached && Parse(cached) is { Games.Count: > 0 } fromCache ? fromCache : null;
        var builtIn = Parse(BuiltIn());
        var file = saved is not null && (builtIn is null || saved.Version >= builtIn.Version) ? saved : builtIn ?? new PresetFile();

        _presets = new Dictionary<string, FrameGenPreset>();
        foreach (var preset in file.Games)
            foreach (var name in preset.Aliases.Prepend(preset.Name))
            {
                _presets.TryAdd(CompatList.Key(name), preset);
                _presets.TryAdd(CompatList.BaseKey(name), preset);
            }

        var wiki = ReadCached(HudFixCache) is { } md ? ParseWikiList(md) : new List<string>();
        _hudFixIncompatible = new HashSet<string>();
        foreach (var name in (wiki.Count > 0 ? wiki : file.HudFixIncompatible))
        {
            _hudFixIncompatible.Add(CompatList.Key(name));
            _hudFixIncompatible.Add(CompatList.BaseKey(name));
        }
    }

    private static PresetFile? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<PresetFile>(text, Json); }
        catch (Exception ex) { Log.Warn($"Frame generation presets unreadable: {ex.Message}"); return null; }
    }

    /// <summary>The bullet list under "Games known to be incompatible with HUDfix" on OptiScaler's wiki page.</summary>
    public static List<string> ParseWikiList(string markdown)
    {
        var names = new List<string>();
        var inList = false;
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#", StringComparison.Ordinal))
            {
                inList = line.Contains("incompatible", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inList) continue;
            if (line.StartsWith("---", StringComparison.Ordinal)) break;
            if (!line.StartsWith("* ", StringComparison.Ordinal)) continue;
            var name = Regex.Replace(line[2..], @"\[([^\]]*)\]\([^)]*\)", "$1").Replace("*", "").Replace("_", "").Trim();
            if (name.Length > 0) names.Add(name);
        }
        return names;
    }

    private static string? ReadCached(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static string? BuiltIn()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RalFsrHelper.framegen-presets.json");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
