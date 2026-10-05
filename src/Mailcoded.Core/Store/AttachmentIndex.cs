using Mailcoded.Core.Domain.Search;

namespace Mailcoded.Core.Store;

/// <summary>contentless_delete lets a row go by rowid alone, so removal never needs the text it indexed.</summary>
internal static class AttachmentIndex
{
    private const string InsertText =
        "INSERT INTO attachment_text (message_id, idx, file_name, mime_type, text, truncated) VALUES ($message,$idx,$name,$mime,$text,$truncated)";

    public static void Replace(DbSession session, long messageId, IReadOnlyList<AttachmentTextEntry> entries, int version)
    {
        RemoveFor(session, messageId, keepState: true);

        foreach (var entry in entries)
        {
            session.Prepare(InsertText, "$message", "$idx", "$name", "$mime", "$text", "$truncated")
                .SetInt(0, messageId)
                .SetInt(1, entry.Index)
                .SetText(2, entry.FileName)
                .SetText(3, entry.MimeType)
                .SetText(4, entry.Text)
                .SetBool(5, entry.Truncated)
                .Execute();

            var rowId = session.ExecScalarInt64("SELECT last_insert_rowid()");

            session.Prepare("INSERT INTO att_fts(rowid, text) VALUES($rowid,$text)", "$rowid", "$text")
                .SetInt(0, rowId)
                .SetText(1, entry.Text)
                .Execute();

            if (!HasCjk(entry.Text)) continue;

            session.Prepare("INSERT INTO att_fts_cjk(rowid, text) VALUES($rowid,$text)", "$rowid", "$text")
                .SetInt(0, rowId)
                .SetText(1, entry.Text)
                .Execute();
        }

        session
            .Prepare(
                "INSERT INTO attachment_index (message_id, version) VALUES ($message,$version) "
                + "ON CONFLICT(message_id) DO UPDATE SET version = excluded.version",
                "$message", "$version")
            .SetInt(0, messageId)
            .SetInt(1, version)
            .Execute();
    }

    public static void RemoveFor(DbSession session, long messageId, bool keepState = false)
    {
        const string Rows = "SELECT id FROM attachment_text WHERE message_id = $message";

        session.Prepare("DELETE FROM att_fts WHERE rowid IN (" + Rows + ")", "$message").SetInt(0, messageId).Execute();
        session.Prepare("DELETE FROM att_fts_cjk WHERE rowid IN (" + Rows + ")", "$message").SetInt(0, messageId).Execute();
        session.Prepare("DELETE FROM attachment_text WHERE message_id = $message", "$message").SetInt(0, messageId).Execute();

        if (keepState) return;
        session.Prepare("DELETE FROM attachment_index WHERE message_id = $message", "$message").SetInt(0, messageId).Execute();
    }

    public static void RemoveForFolder(DbSession session, long folderId)
    {
        const string Rows =
            "SELECT a.id FROM attachment_text a JOIN messages m ON m.id = a.message_id WHERE m.folder_id = $folder";

        session.Prepare("DELETE FROM att_fts WHERE rowid IN (" + Rows + ")", "$folder").SetInt(0, folderId).Execute();
        session.Prepare("DELETE FROM att_fts_cjk WHERE rowid IN (" + Rows + ")", "$folder").SetInt(0, folderId).Execute();
    }

    public static void Optimize(DbSession session)
    {
        session.Exec("INSERT INTO att_fts(att_fts) VALUES('optimize')");
        session.Exec("INSERT INTO att_fts_cjk(att_fts_cjk) VALUES('optimize')");
    }

    private static bool HasCjk(string text)
    {
        foreach (var rune in text.AsSpan().EnumerateRunes())
            if (SearchEscaping.IsCjk(rune)) return true;
        return false;
    }
}
