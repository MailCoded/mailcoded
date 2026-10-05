using System.Text;

namespace Mailcoded.Core.Domain.Signals;

public sealed record AuthenticationProperty(string Ptype, string Property, string Value);

public sealed record AuthenticationResult(
    string Method,
    string? MethodVersion,
    string Result,
    IReadOnlyList<AuthenticationProperty> Properties);

public sealed record ParsedAuthenticationResults(string AuthservId, IReadOnlyList<AuthenticationResult> Results);

/// <summary>RFC 8601 reader for one attacker-controlled Authentication-Results value; it never throws.</summary>
public static class AuthenticationResultsParser
{
    public const int MaxLength = 8_000;

    private enum Kind { Atom, Quoted, Equals, Slash }

    private readonly record struct Token(Kind Kind, string Text, bool SpaceBefore);

    public static string? ReadAuthservId(string? header)
    {
        if (!TryLex(header, firstSegmentOnly: true, out var segments) || segments.Count == 0) return null;

        return AuthservIdOf(segments[0]);
    }

    public static ParsedAuthenticationResults? Parse(string? header)
    {
        if (!TryLex(header, firstSegmentOnly: false, out var segments) || segments.Count == 0) return null;

        var id = AuthservIdOf(segments[0]);
        if (id is null) return null;

        var results = new List<AuthenticationResult>();
        for (var i = 1; i < segments.Count; i++)
            if (ReadResult(segments[i]) is { } result) results.Add(result);

        return new ParsedAuthenticationResults(id, results);
    }

    private static string? AuthservIdOf(List<Token> first)
    {
        if (first.Count == 0 || first[0].Kind is not (Kind.Atom or Kind.Quoted)) return null;

        var text = first[0].Text;
        return text.Length == 0 ? null : text;
    }

    private static AuthenticationResult? ReadResult(List<Token> tokens)
    {
        var i = 0;
        if (tokens.Count < 3 || tokens[0].Kind != Kind.Atom) return null;

        var method = tokens[0].Text;
        string? version = null;
        i = 1;

        if (tokens[i].Kind == Kind.Slash)
        {
            if (tokens.Count < 5 || tokens[i + 1].Kind != Kind.Atom) return null;

            version = tokens[i + 1].Text;
            i += 2;
        }

        if (tokens.Count < i + 2 || tokens[i].Kind != Kind.Equals || tokens[i + 1].Kind != Kind.Atom) return null;

        var result = tokens[i + 1].Text;
        i += 2;

        var properties = new List<AuthenticationProperty>();

        while (i < tokens.Count)
        {
            if (tokens[i].Kind != Kind.Atom || i + 1 >= tokens.Count || tokens[i + 1].Kind != Kind.Equals)
            {
                i++;
                continue;
            }

            var key = tokens[i].Text;
            var value = new StringBuilder();
            i += 2;

            if (i < tokens.Count) value.Append(tokens[i++].Text);
            while (i < tokens.Count && !tokens[i].SpaceBefore) value.Append(tokens[i++].Text);

            var dot = key.IndexOf('.');
            if (dot > 0 && dot < key.Length - 1)
                properties.Add(new AuthenticationProperty(key[..dot], key[(dot + 1)..], value.ToString()));
        }

        return new AuthenticationResult(method, version, result, properties);
    }

    private static bool TryLex(string? input, bool firstSegmentOnly, out List<List<Token>> segments)
    {
        segments = [];
        if (string.IsNullOrWhiteSpace(input) || input.Length > MaxLength) return false;

        var current = new List<Token>();
        var space = false;
        var i = 0;

        while (i < input.Length)
        {
            var c = input[i];

            if (char.IsWhiteSpace(c))
            {
                space = true;
                i++;
            }
            else if (c == '(')
            {
                if (!TrySkipComment(input, ref i)) return false;
                space = true;
            }
            else if (c == ')')
            {
                return false;
            }
            else if (c == '"')
            {
                if (!TryReadQuoted(input, ref i, out var text)) return false;
                current.Add(new Token(Kind.Quoted, text, space));
                space = false;
            }
            else if (c == ';')
            {
                segments.Add(current);
                if (firstSegmentOnly) return true;

                current = [];
                space = true;
                i++;
            }
            else if (c == '=')
            {
                current.Add(new Token(Kind.Equals, "=", space));
                space = false;
                i++;
            }
            else if (c == '/')
            {
                current.Add(new Token(Kind.Slash, "/", space));
                space = false;
                i++;
            }
            else
            {
                var start = i;
                while (i < input.Length && !IsDelimiter(input[i])) i++;
                current.Add(new Token(Kind.Atom, input[start..i], space));
                space = false;
            }
        }

        segments.Add(current);
        return true;
    }

    private static bool IsDelimiter(char c) =>
        char.IsWhiteSpace(c) || c is '(' or ')' or '"' or ';' or '=' or '/';

    // A quote mark inside a comment is ordinary text, not the start of a quoted string.
    private static bool TrySkipComment(string input, ref int i)
    {
        var depth = 0;

        while (i < input.Length)
        {
            var c = input[i++];

            if (c == '\\')
            {
                if (i >= input.Length) return false;
                i++;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadQuoted(string input, ref int i, out string text)
    {
        var builder = new StringBuilder();
        i++;

        while (i < input.Length)
        {
            var c = input[i++];

            if (c == '\\')
            {
                if (i >= input.Length) break;
                builder.Append(input[i++]);
            }
            else if (c == '"')
            {
                text = builder.ToString();
                return true;
            }
            else
            {
                builder.Append(c);
            }
        }

        text = string.Empty;
        return false;
    }
}
