using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace RalFsrHelper.Core;

public enum CompatStatus { Working, Partial, NotWorking, Mixed }

/// <summary>One row of OptiScaler's community Compatibility List.</summary>
public sealed record CompatEntry(string Name, CompatStatus Status, IReadOnlyList<string> Inputs, bool OptiPatcher,
    string Notes, string? WikiUrl)
{
    /// <summary>The community notes say OptiScaler needs REFramework in this game ("Requires REFramework").</summary>
    public bool NeedsREFramework => Regex.IsMatch(Notes, @"requires\W+(\[)?REFramework", RegexOptions.IgnoreCase);
}

/// <summary>
/// OptiScaler's community Compatibility List (github.com/optiscaler/OptiScaler/wiki/Compatibility-List): tested games,
/// the upscaler inputs that work, and notes. Downloaded from the wiki's raw Markdown at most once a day and cached, so it
/// stays current without a widget update. All credit to the OptiScaler community who maintain it.
/// </summary>
public static class CompatList
{
    public const string RawUrl = "https://raw.githubusercontent.com/wiki/optiscaler/OptiScaler/Compatibility-List.md";
    public const string PageUrl = "https://github.com/optiscaler/OptiScaler/wiki/Compatibility-List";
    private const string WikiBase = "https://github.com/optiscaler/OptiScaler/wiki/";

    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(24);
    private static string CachePath => Path.Combine(AppPaths.Root, "compat-list.md");

    private static readonly HttpClient Http = CreateClient();
    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RALFsr4Lite/1.0");
        return c;
    }

    private static readonly object Gate = new();
    private static Dictionary<string, CompatEntry>? _byKey;
    private static Dictionary<string, CompatEntry>? _byBase;

    /// <summary>Entries whose name has exactly one number, keyed by the loose key without it ("Resident Evil 9 Requiem").</summary>
    private static Dictionary<string, List<(CompatEntry Entry, string Number)>>? _byNoNumber;

    /// <summary>Downloads a fresh copy if the cache is older than a day. Never throws; a stale or missing list is fine.</summary>
    public static async Task RefreshAsync()
    {
        try
        {
            AppPaths.Ensure();
            if (File.Exists(CachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(CachePath) < RefreshEvery) return;
            var text = await Http.GetStringAsync(RawUrl);
            if (!text.Contains("| Game |", StringComparison.Ordinal)) return; // not the page we expect
            await File.WriteAllTextAsync(CachePath, text, Encoding.UTF8);
            lock (Gate) { _byKey = null; _byBase = null; _byNoNumber = null; }
            Log.Info($"OptiScaler compatibility list updated ({text.Length / 1024} KB)");
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't refresh the OptiScaler compatibility list: {ex.Message}");
        }
    }

    /// <summary>
    /// The list's entry for a game, matched by name, or null. <paramref name="exeName"/> enables one extra, careful match:
    /// a store name without a number ("Resident Evil Requiem") finds a list name with one ("Resident Evil 9 Requiem") only
    /// when that number is in the exe name (re9.exe), so "Forza Horizon 5" never matches "Forza Horizon 6".
    /// </summary>
    public static CompatEntry? Find(string gameName, string? exeName = null)
    {
        EnsureLoaded();
        lock (Gate)
        {
            if (_byKey is null || _byBase is null) return null;
            var key = Key(gameName);
            if (key.Length == 0) return null;
            if (_byKey.TryGetValue(key, out var exact)) return exact;
            var baseKey = BaseKey(gameName);
            if (baseKey.Length >= 4 && _byBase.TryGetValue(baseKey, out var loose)) return loose;

            if (string.IsNullOrEmpty(exeName) || _byNoNumber is null || baseKey.Length < 6 || baseKey.Any(char.IsDigit))
                return null;
            if (!_byNoNumber.TryGetValue(baseKey, out var candidates) || candidates.Count != 1) return null;
            var (entry, number) = candidates[0];
            var exeDigits = Regex.Matches(Path.GetFileNameWithoutExtension(exeName), @"\d+").Select(m => m.Value);
            return exeDigits.Contains(number) ? entry : null;
        }
    }

    private static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_byKey is not null) return;
            try
            {
                if (!File.Exists(CachePath)) return;
                var entries = Parse(File.ReadAllText(CachePath));
                _byKey = new Dictionary<string, CompatEntry>();
                _byBase = new Dictionary<string, CompatEntry>();
                _byNoNumber = new Dictionary<string, List<(CompatEntry, string)>>();
                foreach (var e in entries)
                {
                    _byKey.TryAdd(Key(e.Name), e);
                    var b = BaseKey(e.Name);
                    if (b.Length >= 4) _byBase.TryAdd(b, e);

                    // "Resident Evil 9 Requiem" -> "residentevilrequiem" + "9", for names with exactly one number.
                    var numbers = Regex.Matches(e.Name, @"\b\d+\b").Select(m => m.Value).ToList();
                    if (numbers.Count == 1)
                    {
                        var noNumber = BaseKey(Regex.Replace(e.Name, @"\b\d+\b", " "));
                        if (noNumber.Length >= 6)
                        {
                            if (!_byNoNumber.TryGetValue(noNumber, out var list)) _byNoNumber[noNumber] = list = new();
                            list.Add((e, numbers[0]));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't read the OptiScaler compatibility list: {ex.Message}");
            }
        }
    }

    /// <summary>Reads the main table (the first one whose header starts with "| Game |").</summary>
    public static List<CompatEntry> Parse(string markdown)
    {
        var entries = new List<CompatEntry>();
        var lines = markdown.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("| Game |", StringComparison.Ordinal));
        if (start < 0) return entries;

        for (var i = start + 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            // The table runs to the next section heading. Rows may omit the leading pipe, which Markdown allows.
            if (line.StartsWith('#') || line.StartsWith("| Game |", StringComparison.Ordinal)) break;
            if (line.Count(c => c == '|') < 4) continue;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells.Length < 5) continue;

            var (name, page) = NameAndPage(cells[0]);
            if (name.Length == 0 || name.Equals("GAME NAME", StringComparison.OrdinalIgnoreCase)) continue;

            var status = cells[1] switch
            {
                var s when s.Contains('❌') && (s.Contains('✅') || s.Contains('✔')) => CompatStatus.Mixed,
                var s when s.Contains('❌') => CompatStatus.NotWorking,
                var s when s.Contains('➖') => CompatStatus.Partial,
                _ => CompatStatus.Working,
            };
            var inputs = cells[2].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(PlainText).Where(x => x.Length > 0).ToList();
            entries.Add(new CompatEntry(name, status, inputs, cells[3].Contains('✨'), PlainText(cells[4]),
                page is null ? null : WikiBase + page));
        }
        return entries;
    }

    private static (string Name, string? Page) NameAndPage(string cell)
    {
        var m = Regex.Match(cell, @"^\[(?<name>[^\]]+)\]\((?<page>[^)]+)\)");
        if (m.Success)
        {
            var page = m.Groups["page"].Value;
            return (PlainText(m.Groups["name"].Value), page.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? null : page);
        }
        return (PlainText(cell), null);
    }

    /// <summary>Markdown cell to readable text: links to their text, no backticks or bold, line breaks to spaces.</summary>
    private static string PlainText(string s)
    {
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<[^>]+>", "");
        s = s.Replace("**", "").Replace("__", "").Replace("`", "").Replace("_", " ");
        s = WebUtility.HtmlDecode(s);
        return Regex.Replace(s, @"\s+", " ").Trim();
    }

    /// <summary>Exact match key: letters and digits only, lowercased ("No Man’s Sky" and "No Man's Sky" match).</summary>
    public static string Key(string name) =>
        new string(name.ToLowerInvariant().Normalize(NormalizationForm.FormKD).Where(char.IsLetterOrDigit).ToArray());

    private static readonly string[] EditionWords =
    {
        "directors cut", "director's cut", "ultimate edition", "definitive edition", "complete edition", "deluxe edition",
        "game of the year edition", "goty edition", "remastered", "enhanced edition", "anniversary edition",
        "standard edition", "gold edition", "premium edition",
    };

    /// <summary>Loose key: edition suffixes, years in brackets and trademark signs removed, so "Ghost of Tsushima" finds
    /// "Ghost of Tsushima DIRECTOR'S CUT" and "Control" finds "Control Ultimate Edition", never "CONTROL Resonant".</summary>
    public static string BaseKey(string name)
    {
        var s = name.ToLowerInvariant().Replace("’", "'");
        s = Regex.Replace(s, @"\((19|20)\d\d\)", " ");
        foreach (var w in EditionWords) s = s.Replace(w, " ");
        s = Regex.Replace(s, @"[™®©]", " ");
        return Key(s);
    }
}
