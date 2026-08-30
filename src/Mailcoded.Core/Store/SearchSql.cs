using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Tags;

namespace Mailcoded.Core.Store;

/// <summary>One bound value: text or integer, decided when it is added.</summary>
internal readonly struct SearchArg
{
    private readonly string? _text;
    private readonly long _number;
    private readonly bool _isText;

    private SearchArg(string? text, long number, bool isText)
    {
        _text = text;
        _number = number;
        _isText = isText;
    }

    public static SearchArg Text(string? value) => new(value, 0, true);

    public static SearchArg Int(long value) => new(null, value, false);

    public void Bind(Stmt statement, int ordinal)
    {
        if (_isText) statement.SetText(ordinal, _text);
        else statement.SetInt(ordinal, _number);
    }
}

/// <summary>Only constant fragments reach the SQL text; every query value is a bound parameter.</summary>
internal sealed class SearchSqlBuilder
{
    private const string LikeEscape = " ESCAPE '\\'";

    private readonly StringBuilder _where = new(256);
    private readonly List<string> _names = new(8);
    private readonly List<SearchArg> _args = new(8);

    public bool IsEmpty => _where.Length == 0;

    public string Where => _where.ToString();

    public string[] Names() => _names.ToArray();

    public void Bind(Stmt statement)
    {
        for (var i = 0; i < _args.Count; i++) _args[i].Bind(statement, i);
    }

    public void And(string clause)
    {
        if (clause.Length == 0) return;
        if (_where.Length > 0) _where.Append(" AND ");
        _where.Append(clause);
    }

    public string AddText(string? value)
    {
        var name = NextName();
        _names.Add(name);
        _args.Add(SearchArg.Text(value));
        return name;
    }

    public string AddInt(long value)
    {
        var name = NextName();
        _names.Add(name);
        _args.Add(SearchArg.Int(value));
        return name;
    }

    public void AndIntCompare(string column, string op, long value) =>
        And(string.Concat(column, " ", op, " ", AddInt(value)));

    public void AndContains(string column, string value, bool negated)
    {
        var parameter = AddText(ContainsPattern(value));
        And(string.Concat(negated ? "NOT " : string.Empty, column, " LIKE ", parameter, LikeEscape));
    }

    /// <summary>A term the FTS5 tokenizers cannot index (1-2 CJK runes): subject or body substring.</summary>
    public void AndLikeFallback(string pattern, bool negated)
    {
        var parameter = AddText(pattern);
        var clause = string.Concat(
            "(COALESCE(m.subject, '') LIKE ", parameter, LikeEscape,
            " OR EXISTS (SELECT 1 FROM body_text bt WHERE bt.message_id = m.id AND bt.text LIKE ",
            parameter, LikeEscape, "))");
        And(negated ? string.Concat("NOT ", clause) : clause);
    }

    public void AndFtsMatch(string table, string matchExpression) =>
        And(string.Concat(table, " MATCH ", AddText(matchExpression)));

    public void AndFtsSubquery(string table, string matchExpression, bool negated)
    {
        var parameter = AddText(matchExpression);
        And(string.Concat(
            "m.id ", negated ? "NOT IN" : "IN",
            " (SELECT rowid FROM ", table, " WHERE ", table, " MATCH ", parameter, ")"));
    }

    public void AndTag(Tag tag, bool negated)
    {
        if (TagFlagMap.SystemFlagFor(tag) is { } flag)
        {
            And(FlagClause(flag, negated));
            return;
        }

        var parameter = AddText(tag.Value);
        And(string.Concat(
            negated ? "NOT EXISTS" : "EXISTS",
            " (SELECT 1 FROM tags t WHERE t.message_id = m.id AND t.tag = ", parameter, ")"));
    }

    /// <summary>Matches a folder by full path, leaf name, or role.</summary>
    public void AndFolderName(string name, bool negated)
    {
        var exact = AddText(name);
        var leaf = AddText(string.Concat("%/", EscapeLike(name)));
        var role = AddText(FolderRoleExtensions.FromWireValue(name.ToLowerInvariant()).ToWireValue());

        var clause = string.Concat(
            "EXISTS (SELECT 1 FROM folders f WHERE f.id = m.folder_id AND (f.name = ", exact,
            " COLLATE NOCASE OR f.name LIKE ", leaf, LikeEscape,
            " OR f.role = ", role, "))");

        And(negated ? string.Concat("NOT ", clause) : clause);
    }

    /// <summary>A literal mask, so the clause still matches the ix_msg_unread partial-index predicate.</summary>
    public static string FlagClause(MessageFlags flag, bool negated)
    {
        var mask = flag switch
        {
            MessageFlags.Flagged => "2",
            MessageFlags.Answered => "4",
            MessageFlags.Draft => "8",
            MessageFlags.Deleted => "16",
            _ => "1",
        };

        return negated
            ? string.Concat("(m.flags & ", mask, ") = 0")
            : string.Concat("(m.flags & ", mask, ") = ", mask);
    }

    private static string ContainsPattern(string value) => string.Concat("%", EscapeLike(value), "%");

    private static string EscapeLike(string value)
    {
        var sb = new StringBuilder(value.Length + 4);
        foreach (var c in value)
        {
            if (char.IsControl(c)) continue;
            if (c is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private string NextName() => string.Create(CultureInfo.InvariantCulture, $"$p{_names.Count}");
}
