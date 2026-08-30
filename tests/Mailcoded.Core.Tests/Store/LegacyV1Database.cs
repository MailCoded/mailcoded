using Mailcoded.Core.Store;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Tests.Store;

/// <summary>Builds a database at schema v1 exactly as it shipped, CJK index defect included.</summary>
internal static class LegacyV1Database
{
    /// <summary>The v1 CJK index carried detail='none', which makes FTS5 reject every phrase query.</summary>
    private const string Schema = """
        CREATE TABLE accounts (
          id INTEGER PRIMARY KEY,
          email TEXT NOT NULL UNIQUE,
          display_name TEXT,
          provider TEXT NOT NULL CHECK (provider IN ('imap','graph','jmap','gmail')),
          config_json TEXT NOT NULL
        );

        CREATE TABLE folders (
          id INTEGER PRIMARY KEY,
          account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
          name TEXT NOT NULL,
          role TEXT,
          uidvalidity INTEGER,
          uidnext INTEGER,
          highestmodseq INTEGER,
          delta_token TEXT,
          unread_count INTEGER NOT NULL DEFAULT 0,
          total_count INTEGER NOT NULL DEFAULT 0,
          UNIQUE (account_id, name)
        );

        CREATE TABLE messages (
          id INTEGER PRIMARY KEY,
          account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
          folder_id INTEGER NOT NULL REFERENCES folders(id) ON DELETE CASCADE,
          uid INTEGER,
          message_id TEXT,
          thread_key TEXT,
          date_utc INTEGER,
          from_addr TEXT, to_addrs TEXT, cc_addrs TEXT,
          subject TEXT,
          flags INTEGER NOT NULL DEFAULT 0,
          modseq INTEGER,
          size INTEGER,
          has_attachments INTEGER NOT NULL DEFAULT 0,
          blob_id INTEGER REFERENCES blobs(id),
          body_fetched INTEGER NOT NULL DEFAULT 0,
          UNIQUE (folder_id, uid)
        );

        CREATE TABLE blobs (
          id INTEGER PRIMARY KEY,
          sha256 TEXT NOT NULL UNIQUE,
          bytes BLOB,
          ext_path TEXT
        );

        CREATE TABLE tags (
          message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
          tag TEXT NOT NULL,
          PRIMARY KEY (message_id, tag)
        );

        CREATE TABLE body_text (
          message_id INTEGER PRIMARY KEY REFERENCES messages(id) ON DELETE CASCADE,
          text TEXT NOT NULL
        );

        CREATE TABLE outbox (
          id INTEGER PRIMARY KEY,
          account_id INTEGER NOT NULL REFERENCES accounts(id),
          message_id TEXT NOT NULL,
          state TEXT NOT NULL CHECK (state IN ('queued','sending','sent','failed')),
          raw BLOB NOT NULL,
          smtp_response TEXT,
          attempts INTEGER NOT NULL DEFAULT 0,
          next_attempt_utc INTEGER,
          created_utc INTEGER NOT NULL
        );

        CREATE TABLE sync_log (
          id INTEGER PRIMARY KEY,
          ts INTEGER NOT NULL,
          account_id INTEGER,
          level TEXT, event TEXT, detail TEXT,
          interface TEXT,
          agent_host TEXT
        );

        CREATE TABLE folder_sync_state (
          folder_id INTEGER PRIMARY KEY REFERENCES folders(id) ON DELETE CASCADE,
          backfill_cursor INTEGER,
          accepts_custom_keywords INTEGER NOT NULL DEFAULT 1,
          last_sync_utc INTEGER
        );

        CREATE VIRTUAL TABLE msg_fts USING fts5(
          subject, body_text, from_addr, to_addr,
          content='',
          tokenize='porter unicode61 remove_diacritics 2',
          detail='full',
          prefix='2 3'
        );

        CREATE VIRTUAL TABLE msg_fts_cjk USING fts5(
          subject, body_text,
          content='',
          tokenize='trigram',
          detail='none'
        );

        CREATE INDEX ix_msg_folder_uid ON messages(folder_id, uid);
        CREATE INDEX ix_msg_folder_date ON messages(folder_id, date_utc DESC);
        CREATE INDEX ix_msg_unread ON messages(folder_id) WHERE (flags & 1) = 0;
        CREATE INDEX ix_msg_thread ON messages(thread_key, date_utc DESC, id);
        CREATE INDEX ix_msg_message_id ON messages(message_id);
        CREATE INDEX ix_outbox_state ON outbox(state, next_attempt_utc);
        CREATE INDEX ix_outbox_message_id ON outbox(message_id);
        CREATE INDEX ix_sync_log_account ON sync_log(account_id, id DESC);
        CREATE INDEX ix_tags_tag ON tags(tag, message_id);

        PRAGMA user_version = 1;
        """;

    public static string ConnectionStringFor(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();

    public static SqliteConnection OpenRaw(string databasePath)
    {
        // Touching SqliteStore first runs its static ctor, which loads the bundled native library.
        _ = SqliteStore.ComputeSha256(ReadOnlySpan<byte>.Empty);

        var connection = new SqliteConnection(ConnectionStringFor(databasePath));
        connection.Open();
        return connection;
    }

    public static void Create(string databasePath, string subject, string bodyText, long dateUtcMs)
    {
        using var connection = OpenRaw(databasePath);
        Exec(connection, Schema);

        Exec(
            connection,
            """
            INSERT INTO accounts (id, email, display_name, provider, config_json)
              VALUES (1, 'legacy@example.com', 'Legacy', 'imap', '{"version":1}');
            INSERT INTO folders (id, account_id, name, role, unread_count, total_count)
              VALUES (1, 1, 'INBOX', 'inbox', 1, 1);
            """);

        using (var insertMessage = connection.CreateCommand())
        {
            insertMessage.CommandText =
                "INSERT INTO messages (id, account_id, folder_id, uid, message_id, thread_key, date_utc, "
                + "from_addr, to_addrs, subject, flags, size, body_fetched) "
                + "VALUES (1, 1, 1, 1, 'legacy-1@example.com', 'm:legacy-1@example.com', $date, "
                + "'zhangsan@example.cn', 'bob@example.org', $subject, 1, 2048, 1)";
            insertMessage.Parameters.AddWithValue("$date", dateUtcMs);
            insertMessage.Parameters.AddWithValue("$subject", subject);
            insertMessage.ExecuteNonQuery();
        }

        using (var insertBody = connection.CreateCommand())
        {
            insertBody.CommandText = "INSERT INTO body_text (message_id, text) VALUES (1, $text)";
            insertBody.Parameters.AddWithValue("$text", bodyText);
            insertBody.ExecuteNonQuery();
        }

        using (var indexMain = connection.CreateCommand())
        {
            indexMain.CommandText =
                "INSERT INTO msg_fts(rowid, subject, body_text, from_addr, to_addr) "
                + "VALUES (1, $subject, $body, 'zhangsan@example.cn', 'bob@example.org')";
            indexMain.Parameters.AddWithValue("$subject", subject);
            indexMain.Parameters.AddWithValue("$body", bodyText);
            indexMain.ExecuteNonQuery();
        }

        using (var indexCjk = connection.CreateCommand())
        {
            indexCjk.CommandText = "INSERT INTO msg_fts_cjk(rowid, subject, body_text) VALUES (1, $subject, $body)";
            indexCjk.Parameters.AddWithValue("$subject", subject);
            indexCjk.Parameters.AddWithValue("$body", bodyText);
            indexCjk.ExecuteNonQuery();
        }
    }

    public static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long UserVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
