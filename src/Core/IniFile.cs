using System.Text;

namespace RalFsrHelper.Core;

/// <summary>
/// Minimal INI editor that changes single values in place and keeps OptiScaler's comments and layout intact.
/// </summary>
public static class IniFile
{
    public static void SetValues(string path, IEnumerable<InstallConfig.IniValue> values)
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
        foreach (var v in values) Set(lines, v.Section, v.Key, v.Value);
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static string? Get(string path, string section, string key)
    {
        if (!File.Exists(path)) return null;
        string? current = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (IsSection(line, out var name)) { current = name; continue; }
            if (current is not null && current.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                TryKey(line, out var k, out var val) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return val;
            }
        }
        return null;
    }

    private static void Set(List<string> lines, string section, string key, string value)
    {
        int sectionStart = -1, sectionEnd = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!IsSection(lines[i].Trim(), out var name)) continue;
            if (sectionStart >= 0) { sectionEnd = i; break; }
            if (name.Equals(section, StringComparison.OrdinalIgnoreCase)) sectionStart = i;
        }

        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0) lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            return;
        }

        for (var i = sectionStart + 1; i < sectionEnd; i++)
        {
            if (TryKey(lines[i].Trim(), out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key}={value}";
                return;
            }
        }

        // Key missing: add it after the last non-blank line of the section.
        var insertAt = sectionEnd;
        while (insertAt - 1 > sectionStart && lines[insertAt - 1].Trim().Length == 0) insertAt--;
        lines.Insert(insertAt, $"{key}={value}");
    }

    private static bool IsSection(string line, out string name)
    {
        name = "";
        if (line.Length < 3 || line[0] != '[' || line[^1] != ']') return false;
        name = line[1..^1].Trim();
        return true;
    }

    private static bool TryKey(string line, out string key, out string value)
    {
        key = value = "";
        if (line.Length == 0 || line[0] == ';' || line[0] == '#') return false;
        var eq = line.IndexOf('=');
        if (eq <= 0) return false;
        key = line[..eq].Trim();
        value = line[(eq + 1)..].Trim();
        return true;
    }
}
