using System.Reflection;
using System.Text;
using System.Xml.Linq;

namespace Mailcoded.Protocol.TypeScript;

/// <summary>The XML documentation of the protocol assembly, as one-line summaries keyed the way
/// the compiler keys them (<c>T:</c>, <c>P:</c>, <c>F:</c>), so they can become JSDoc.</summary>
internal sealed class XmlDocs
{
    private readonly Dictionary<string, string> _summaries;

    private XmlDocs(Dictionary<string, string> summaries) => _summaries = summaries;

    public static XmlDocs Load(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"No documentation file beside {assembly.GetName().Name}; it is required so the generated "
                + "TypeScript carries the same comments as the C#.",
                path);
        }

        return Parse(File.ReadAllText(path));
    }

    public static XmlDocs Parse(string xml)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = XDocument.Parse(xml);

        foreach (var member in document.Descendants("member"))
        {
            var name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null) continue;

            var text = Flatten(summary);
            if (text.Length > 0) summaries[name] = text;
        }

        return new XmlDocs(summaries);
    }

    public string? ForType(Type type) => Lookup("T:" + type.FullName);

    public string? ForProperty(PropertyInfo property) =>
        Lookup($"P:{property.DeclaringType!.FullName}.{property.Name}");

    public string? ForField(FieldInfo field) =>
        Lookup($"F:{field.DeclaringType!.FullName}.{field.Name}");

    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private string? Lookup(string key) => _summaries.GetValueOrDefault(key);

    private static string Flatten(XElement element)
    {
        var builder = new StringBuilder();

        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value);
                    break;

                case XElement { Name.LocalName: "c" } code:
                    builder.Append('`').Append(code.Value).Append('`');
                    break;

                case XElement { Name.LocalName: "see" or "seealso" } see:
                    builder.Append(CrefName(see.Attribute("cref")?.Value ?? see.Value));
                    break;

                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                    builder.Append(reference.Attribute("name")?.Value);
                    break;

                case XElement other:
                    builder.Append(Flatten(other));
                    break;
            }
        }

        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary><c>P:Mailcoded.Protocol.EnvelopeDto.Date</c> reads as <c>EnvelopeDto.Date</c>.</summary>
    private static string CrefName(string cref)
    {
        var body = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var parts = body.Split('.');
        var isMember = cref.StartsWith("P:", StringComparison.Ordinal) || cref.StartsWith("F:", StringComparison.Ordinal);

        return isMember && parts.Length >= 2 ? string.Join('.', parts[^2..]) : parts[^1];
    }
}
