using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

// Usage: GameBarProxyStub <Microsoft.Gaming.XboxGameBar.Private.winmd> <Package.appxmanifest>
//
// Game Bar talks to a widget through the SDK's private interfaces, and Windows can only marshal those calls if
// every interface is registered in the package manifest's ProxyStub block. Each SDK version adds interfaces, so a
// hand-copied list goes stale and the widget crashes on open ("something isn't playing nice"). This reads the exact
// list from the SDK the build restored and rewrites the block.

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: GameBarProxyStub <Private.winmd> <Package.appxmanifest>");
    return 2;
}

var interfaces = ReadInterfaces(args[0]);
if (interfaces.Count == 0)
{
    Console.Error.WriteLine($"No interfaces found in {args[0]}");
    return 1;
}

var manifestPath = args[1];
var manifest = File.ReadAllText(manifestPath);
var block = Regex.Match(manifest,
    @"(?<open><ProxyStub ClassId=""00000355-0000-0000-C000-000000000046"">)(?<body>.*?)(?<indent>[ \t]*)(?<close></ProxyStub>)",
    RegexOptions.Singleline);
if (!block.Success)
{
    Console.Error.WriteLine("ProxyStub block not found in the manifest.");
    return 1;
}

var inner = block.Groups["indent"].Value + "  ";
var sb = new StringBuilder();
sb.Append('\n').Append(inner).Append("<Path>Microsoft.Gaming.XboxGameBar.winmd</Path>\n");
foreach (var (name, guid) in interfaces)
    sb.Append(inner).Append($"<Interface Name=\"{name}\" InterfaceId=\"{guid}\" />\n");

var updated = manifest[..block.Index]
              + block.Groups["open"].Value + sb + block.Groups["indent"].Value + block.Groups["close"].Value
              + manifest[(block.Index + block.Length)..];

File.WriteAllText(manifestPath, updated, new UTF8Encoding(false));
Console.WriteLine($"Registered {interfaces.Count} Game Bar interfaces in {Path.GetFileName(manifestPath)}");
return 0;

static List<(string Name, string Guid)> ReadInterfaces(string winmdPath)
{
    var result = new List<(string, string)>();
    using var stream = File.OpenRead(winmdPath);
    using var pe = new PEReader(stream);
    var md = pe.GetMetadataReader();

    foreach (var handle in md.TypeDefinitions)
    {
        var type = md.GetTypeDefinition(handle);
        if ((type.Attributes & TypeAttributes.Interface) == 0) continue;
        var ns = md.GetString(type.Namespace);
        if (!ns.EndsWith(".Private", StringComparison.Ordinal)) continue;

        foreach (var attrHandle in type.GetCustomAttributes())
        {
            var attr = md.GetCustomAttribute(attrHandle);
            if (attr.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            if (md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name) != "GuidAttribute") continue;

            // WinRT GuidAttribute(uint, ushort, ushort, byte x8)
            var blob = md.GetBlobReader(attr.Value);
            blob.ReadUInt16(); // prolog
            var a = blob.ReadUInt32();
            var b = blob.ReadUInt16();
            var c = blob.ReadUInt16();
            var d = new byte[8];
            for (var i = 0; i < 8; i++) d[i] = blob.ReadByte();
            var guid = new Guid((int)a, (short)b, (short)c, d).ToString().ToUpperInvariant();
            result.Add(($"{ns}.{md.GetString(type.Name)}", guid));
        }
    }

    return result.OrderBy(r => r.Item1, StringComparer.Ordinal).ToList();
}
