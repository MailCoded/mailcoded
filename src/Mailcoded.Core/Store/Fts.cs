namespace Mailcoded.Core.Store;

/// <summary>
/// Writes to the two contentless FTS5 indexes. Contentless tables cannot look up their own old
/// values, so a replace must hand back exactly what was indexed — every call therefore normalizes
/// NULL to the empty string and reads its "old" values from the same columns the insert used.
/// </summary>
internal static class Fts
{
    private const string InsertMain =
        "INSERT INTO msg_fts(rowid, subject, body_text, from_addr, to_addr) VALUES($rowid,$subject,$body,$from,$to)";

    private const string DeleteMain =
        "INSERT INTO msg_fts(msg_fts, rowid, subject, body_text, from_addr, to_addr) VALUES('delete',$rowid,$subject,$body,$from,$to)";

    private const string InsertCjk =
        "INSERT INTO msg_fts_cjk(rowid, subject, body_text) VALUES($rowid,$subject,$body)";

    private const string DeleteCjk =
        "INSERT INTO msg_fts_cjk(msg_fts_cjk, rowid, subject, body_text) VALUES('delete',$rowid,$subject,$body)";

    public static void Insert(DbSession session, long rowId, string? subject, string? bodyText, string? from, string? to)
    {
        session.Prepare(InsertMain, "$rowid", "$subject", "$body", "$from", "$to")
            .SetInt(0, rowId)
            .SetText(1, subject ?? string.Empty)
            .SetText(2, bodyText ?? string.Empty)
            .SetText(3, from ?? string.Empty)
            .SetText(4, to ?? string.Empty)
            .Execute();

        session.Prepare(InsertCjk, "$rowid", "$subject", "$body")
            .SetInt(0, rowId)
            .SetText(1, subject ?? string.Empty)
            .SetText(2, bodyText ?? string.Empty)
            .Execute();
    }

    public static void Delete(DbSession session, long rowId, string? subject, string? bodyText, string? from, string? to)
    {
        session.Prepare(DeleteMain, "$rowid", "$subject", "$body", "$from", "$to")
            .SetInt(0, rowId)
            .SetText(1, subject ?? string.Empty)
            .SetText(2, bodyText ?? string.Empty)
            .SetText(3, from ?? string.Empty)
            .SetText(4, to ?? string.Empty)
            .Execute();

        session.Prepare(DeleteCjk, "$rowid", "$subject", "$body")
            .SetInt(0, rowId)
            .SetText(1, subject ?? string.Empty)
            .SetText(2, bodyText ?? string.Empty)
            .Execute();
    }

    public static void Replace(
        DbSession session,
        long rowId,
        string? oldSubject, string? oldBody, string? oldFrom, string? oldTo,
        string? newSubject, string? newBody, string? newFrom, string? newTo)
    {
        Delete(session, rowId, oldSubject, oldBody, oldFrom, oldTo);
        Insert(session, rowId, newSubject, newBody, newFrom, newTo);
    }

    public static void Optimize(DbSession session)
    {
        session.Exec("INSERT INTO msg_fts(msg_fts) VALUES('optimize')");
        session.Exec("INSERT INTO msg_fts_cjk(msg_fts_cjk) VALUES('optimize')");
    }
}
