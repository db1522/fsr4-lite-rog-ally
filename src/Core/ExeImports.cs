namespace RalFsrHelper.Core;

/// <summary>
/// Reads a game exe's import table: the DLLs Windows loads for it at startup, and the functions it uses from them.
/// Used to pick OptiScaler's loader name before the first launch. Never throws; an unreadable exe gives empty sets.
/// </summary>
public sealed class ExeImports
{
    /// <summary>DLL names the exe imports directly or delay-loads, lower case.</summary>
    public HashSet<string> Dlls { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Function names the exe imports by name, from any DLL.</summary>
    public HashSet<string> Functions { get; } = new(StringComparer.Ordinal);

    public static ExeImports Read(string exePath)
    {
        var result = new ExeImports();
        try
        {
            var data = File.ReadAllBytes(exePath);
            result.Parse(data);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't read imports of {exePath}: {ex.Message}");
        }
        return result;
    }

    private void Parse(byte[] d)
    {
        if (d.Length < 0x40 || d[0] != 'M' || d[1] != 'Z') return;
        var pe = BitConverter.ToInt32(d, 0x3C);
        if (pe <= 0 || pe + 24 > d.Length || BitConverter.ToUInt32(d, pe) != 0x00004550) return;

        var sections = BitConverter.ToUInt16(d, pe + 6);
        var optSize = BitConverter.ToUInt16(d, pe + 20);
        var opt = pe + 24;
        var is64 = BitConverter.ToUInt16(d, opt) == 0x20B;
        var dirs = opt + (is64 ? 112 : 96);
        var sectionTable = opt + optSize;

        int ToOffset(uint rva)
        {
            for (var i = 0; i < sections; i++)
            {
                var s = sectionTable + i * 40;
                if (s + 40 > d.Length) return -1;
                var va = BitConverter.ToUInt32(d, s + 12);
                var size = Math.Max(BitConverter.ToUInt32(d, s + 8), BitConverter.ToUInt32(d, s + 16));
                if (rva >= va && rva < va + size)
                {
                    var off = (long)rva - va + BitConverter.ToUInt32(d, s + 20);
                    return off < d.Length ? (int)off : -1;
                }
            }
            return -1;
        }

        string? Str(uint rva)
        {
            var off = ToOffset(rva);
            if (off < 0) return null;
            var end = Array.IndexOf(d, (byte)0, off, Math.Min(256, d.Length - off));
            return end < 0 ? null : System.Text.Encoding.ASCII.GetString(d, off, end - off);
        }

        void Names(uint thunkRva)
        {
            var off = ToOffset(thunkRva);
            for (var n = 0; off >= 0 && n < 4096; n++, off += is64 ? 8 : 4)
            {
                if (off + 8 > d.Length) return;
                var entry = is64 ? BitConverter.ToUInt64(d, off) : BitConverter.ToUInt32(d, off);
                if (entry == 0) return;
                var byOrdinal = is64 ? (entry & 0x8000000000000000) != 0 : (entry & 0x80000000) != 0;
                if (!byOrdinal && Str((uint)entry + 2) is { Length: > 0 } fn) Functions.Add(fn);
            }
        }

        // Regular imports: 20-byte descriptors (OriginalFirstThunk, TimeDateStamp, ForwarderChain, Name, FirstThunk).
        var importRva = BitConverter.ToUInt32(d, dirs + 8);
        var off = importRva == 0 ? -1 : ToOffset(importRva);
        for (var n = 0; off >= 0 && off + 20 <= d.Length && n < 512; n++, off += 20)
        {
            var nameRva = BitConverter.ToUInt32(d, off + 12);
            if (nameRva == 0) break;
            if (Str(nameRva) is { Length: > 0 } dll) Dlls.Add(dll.ToLowerInvariant());
            var thunk = BitConverter.ToUInt32(d, off);
            Names(thunk != 0 ? thunk : BitConverter.ToUInt32(d, off + 16));
        }

        // Delay-load imports: 32-byte descriptors (Attributes, Name, ModuleHandle, IAT, INT, ...), RVAs in modern exes.
        var delayRva = BitConverter.ToUInt32(d, dirs + 13 * 8);
        off = delayRva == 0 ? -1 : ToOffset(delayRva);
        for (var n = 0; off >= 0 && off + 32 <= d.Length && n < 512; n++, off += 32)
        {
            var nameRva = BitConverter.ToUInt32(d, off + 4);
            if (nameRva == 0) break;
            if (Str(nameRva) is { Length: > 0 } dll) Dlls.Add(dll.ToLowerInvariant());
        }
    }
}
