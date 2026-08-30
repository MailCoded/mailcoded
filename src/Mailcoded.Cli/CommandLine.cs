using System.Globalization;

namespace Mailcoded.Cli;

/// <summary>The options one verb accepts. An option absent from the spec is a usage error, never ignored.</summary>
internal sealed class VerbSpec
{
    private static readonly string[] GlobalValues = ["data-dir", "db"];
    private static readonly string[] GlobalFlags = ["json", "help", "quiet"];

    private readonly HashSet<string> _values = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);

    public VerbSpec(string[] valueOptions, string[] boolOptions)
    {
        foreach (var name in GlobalValues) _values.Add(name);
        foreach (var name in GlobalFlags) _flags.Add(name);
        foreach (var name in valueOptions) _values.Add(name);
        foreach (var name in boolOptions) _flags.Add(name);
    }

    public bool TakesValue(string name) => _values.Contains(name);

    public bool IsFlag(string name) => _flags.Contains(name);

    public string Accepted()
    {
        var names = new List<string>(_values.Count + _flags.Count);
        foreach (var name in _values) names.Add("--" + name + " <value>");
        foreach (var name in _flags) names.Add("--" + name);
        names.Sort(StringComparer.Ordinal);
        return string.Join(", ", names);
    }
}

/// <summary>
/// The hand-written parser. Only <c>--long</c> options exist, plus <c>-h</c>; every other token
/// beginning with a single dash stays positional so <c>mailcoded tag 12 -inbox</c> parses.
/// </summary>
internal sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.Ordinal);
    private readonly HashSet<string> _flags = new(StringComparer.Ordinal);
    private readonly List<string> _positional = [];

    private CommandLine(string verb) => Verb = verb;

    public string Verb { get; }

    public IReadOnlyList<string> Positional => _positional;

    public static CommandLine Parse(string verb, IReadOnlyList<string> tokens, VerbSpec spec)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(spec);

        var line = new CommandLine(verb);
        var literal = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (literal)
            {
                line._positional.Add(token);
                continue;
            }

            if (token == "--")
            {
                literal = true;
                continue;
            }

            if (token is "-h")
            {
                line._flags.Add("help");
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                line._positional.Add(token);
                continue;
            }

            var name = token[2..];
            string? inline = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                inline = name[(equals + 1)..];
                name = name[..equals];
            }

            if (name.Length == 0)
                throw new CliUsageException("'--' with no option name is not a valid argument.");

            if (spec.IsFlag(name))
            {
                if (inline is not null && !IsTrue(inline))
                {
                    if (!IsFalse(inline))
                        throw new CliUsageException($"--{name} is a flag; '{inline}' is not one of true, false, 1, 0.");
                    continue;
                }

                line._flags.Add(name);
                continue;
            }

            if (!spec.TakesValue(name))
            {
                throw new CliUsageException(
                    $"'{verb}' has no option --{name}. Accepted: {spec.Accepted()}. Run 'mailcoded help {verb}'.");
            }

            var value = inline;
            if (value is null)
            {
                if (i + 1 >= tokens.Count)
                    throw new CliUsageException($"--{name} needs a value.");
                value = tokens[++i];
            }

            if (!line._options.TryGetValue(name, out var list))
            {
                list = [];
                line._options[name] = list;
            }

            list.Add(value);
        }

        return line;
    }

    public bool Flag(string name) => _flags.Contains(name);

    public string? Value(string name) =>
        _options.TryGetValue(name, out var list) && list.Count > 0 ? list[^1] : null;

    public IReadOnlyList<string> Values(string name) =>
        _options.TryGetValue(name, out var list) ? list : [];

    public string RequireValue(string name) =>
        Value(name) ?? throw new CliUsageException($"--{name} is required. Run 'mailcoded help {Verb}'.");

    public int? Int(string name, int min, int max)
    {
        var raw = Value(name);
        if (raw is null) return null;

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new CliUsageException($"--{name} must be a whole number, not '{raw}'.");

        if (value < min || value > max)
            throw new CliUsageException($"--{name} must be between {min} and {max}.");

        return value;
    }

    public long? Long(string name, long min, long max)
    {
        var raw = Value(name);
        if (raw is null) return null;

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            throw new CliUsageException($"--{name} must be a whole number, not '{raw}'.");

        if (value < min || value > max)
            throw new CliUsageException($"--{name} must be between {min} and {max}.");

        return value;
    }

    public string RequirePositional(int index, string what)
    {
        if (index < _positional.Count) return _positional[index];
        throw new CliUsageException($"'{Verb}' needs {what}. Run 'mailcoded help {Verb}'.");
    }

    public long RequireId(int index, string what)
    {
        var raw = RequirePositional(index, what);
        if (long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0) return id;
        throw new CliUsageException($"'{what}' must be a positive whole number, not '{raw}'.");
    }

    public void RejectExtraPositional(int allowed)
    {
        if (_positional.Count <= allowed) return;
        throw new CliUsageException(
            $"'{Verb}' takes {allowed} positional argument(s); got {_positional.Count}. "
            + "Quote a query that contains spaces.");
    }

    private static bool IsTrue(string value) =>
        value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static bool IsFalse(string value) =>
        value is "0" || string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
}
