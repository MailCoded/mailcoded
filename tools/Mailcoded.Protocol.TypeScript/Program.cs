using Mailcoded.Protocol;
using Mailcoded.Protocol.TypeScript;

string? outPath = null;
string? checkPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out" when i + 1 < args.Length:
            outPath = args[++i];
            break;

        case "--check" when i + 1 < args.Length:
            checkPath = args[++i];
            break;

        default:
            Console.Error.WriteLine("usage: Mailcoded.Protocol.TypeScript [--out <file> | --check <file>]");
            return 2;
    }
}

var protocol = typeof(ProtocolJsonContext).Assembly;
var generated = TypeScriptEmitter.Emit(protocol, XmlDocs.Load(protocol));

if (checkPath is not null)
{
    if (!File.Exists(checkPath))
    {
        Console.Error.WriteLine($"{checkPath} does not exist; run with --out to create it.");
        return 1;
    }

    var current = XmlDocs.NormalizeNewlines(File.ReadAllText(checkPath));
    if (current == generated)
    {
        Console.WriteLine($"{checkPath} is up to date.");
        return 0;
    }

    Console.Error.WriteLine($"{checkPath} is stale. {TypeScriptEmitter.FirstDifference(current, generated)}");
    Console.Error.WriteLine("Regenerate with: npm run gen:types");
    return 1;
}

if (outPath is not null)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    File.WriteAllText(outPath, generated);
    Console.WriteLine($"wrote {outPath}");
    return 0;
}

Console.Out.Write(generated);
return 0;
