using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Microsoft.Data.Sqlite;

namespace Mailcoded.Core.Store;

public sealed partial class SqliteStore
{
    private const string SelectAccount =
        "SELECT id, email, display_name, provider, config_json FROM accounts";

    /// <summary>
    /// Inserts an account and returns its id. <paramref name="config"/> is written through
    /// <see cref="AccountConfigJson"/>, which has no field capable of carrying a credential.
    /// </summary>
    public Task<AccountId> AddAccountAsync(AccountConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        return WriteAsync(context =>
        {
            var existing = context.Session
                .Prepare("SELECT id FROM accounts WHERE email = $email", "$email")
                .SetText(0, config.Email)
                .ExecuteNullableInt64();

            if (existing is { } id)
            {
                var json = AccountConfigJson.Write(AccountConfigJson.CarryOver(config, StoredConfigJson(context.Session, id)));

                context.Session
                    .Prepare(
                        "UPDATE accounts SET display_name = $name, provider = $provider, config_json = $config WHERE id = $id",
                        "$name", "$provider", "$config", "$id")
                    .SetText(0, config.DisplayName)
                    .SetText(1, config.Provider.ToWireValue())
                    .SetText(2, json)
                    .SetInt(3, id)
                    .Execute();
                return new AccountId(id);
            }

            var inserted = context.Session
                .Prepare(
                    "INSERT INTO accounts (email, display_name, provider, config_json) VALUES ($email,$name,$provider,$config) RETURNING id",
                    "$email", "$name", "$provider", "$config")
                .SetText(0, config.Email)
                .SetText(1, config.DisplayName)
                .SetText(2, config.Provider.ToWireValue())
                .SetText(3, AccountConfigJson.Write(config))
                .ExecuteInt64();

            return new AccountId(inserted);
        }, ct);
    }

    public Task UpdateAccountAsync(AccountConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Id.IsNone)
            throw new ArgumentException("Updating an account requires its id.", nameof(config));

        return WriteAsync(context =>
        {
            var json = AccountConfigJson.Write(AccountConfigJson.CarryOver(config, StoredConfigJson(context.Session, config.Id.Value)));

            var affected = context.Session
                .Prepare(
                    "UPDATE accounts SET email = $email, display_name = $name, provider = $provider, config_json = $config WHERE id = $id",
                    "$email", "$name", "$provider", "$config", "$id")
                .SetText(0, config.Email)
                .SetText(1, config.DisplayName)
                .SetText(2, config.Provider.ToWireValue())
                .SetText(3, json)
                .SetInt(4, config.Id.Value)
                .Execute();

            if (affected == 0)
                throw new StoreException(FailureCategory.NotFound, $"No account with id {config.Id.Value}.");
        }, ct);
    }

    /// <summary>The only path that changes trust; it clears the Account's evaluated DMARC results (never imported ones) so none outlives its list.</summary>
    public Task<bool> SetTrustedAuthservIdsAsync(AccountId accountId, IReadOnlyList<string> ids, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var wanted = Domain.Signals.AuthservTrust.From(ids).Patterns;

        return WriteAsync(context =>
        {
            AccountConfig current;
            using (var stored = context.Session
                .Prepare(SelectAccount + " WHERE id = $id", "$id")
                .SetInt(0, accountId.Value)
                .ExecuteReader())
            {
                if (!stored.Read())
                    throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

                current = MapAccount(stored);
            }

            if (current.TrustedAuthservIds.SequenceEqual(wanted, StringComparer.Ordinal)) return false;

            context.Session
                .Prepare("UPDATE accounts SET config_json = $config WHERE id = $id", "$config", "$id")
                .SetText(0, AccountConfigJson.Write(current with { TrustedAuthservIds = wanted }))
                .SetInt(1, accountId.Value)
                .Execute();

            context.Session
                .Prepare("UPDATE messages SET dmarc = NULL WHERE account_id = $id AND dmarc IS NOT NULL AND dmarc <> 'imported'", "$id")
                .SetInt(0, accountId.Value)
                .Execute();

            return true;
        }, ct);
    }

    /// <summary>Replaces the Account's identity list with what <paramref name="edit"/> returns; false when nothing changed.</summary>
    public Task<bool> EditIdentitiesAsync(
        AccountId accountId,
        Func<IReadOnlyList<SenderIdentity>, IReadOnlyList<SenderIdentity>> edit,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return EditAccountSettingAsync(
            accountId,
            current =>
            {
                var edited = edit(current.Identities);
                return IdentitiesEqual(current.Identities, edited) ? null : current with { Identities = edited };
            },
            ct);
    }

    public Task<bool> EditSignaturesAsync(AccountId accountId, Func<SignatureBook, SignatureBook> edit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(edit);

        return EditAccountSettingAsync(
            accountId,
            current =>
            {
                var edited = edit(current.Signatures);
                return SignaturesEqual(current.Signatures, edited) ? null : current with { Signatures = edited };
            },
            ct);
    }

    public Task<bool> SetSaveDraftsToServerAsync(AccountId accountId, bool enabled, CancellationToken ct) =>
        EditAccountSettingAsync(
            accountId,
            current => current.SaveDraftsToServer == enabled ? null : current with { SaveDraftsToServer = enabled },
            ct);

    private Task<bool> EditAccountSettingAsync(AccountId accountId, Func<AccountConfig, AccountConfig?> change, CancellationToken ct) =>
        WriteAsync(context =>
        {
            AccountConfig current;
            using (var stored = context.Session
                .Prepare(SelectAccount + " WHERE id = $id", "$id")
                .SetInt(0, accountId.Value)
                .ExecuteReader())
            {
                if (!stored.Read())
                    throw new StoreException(FailureCategory.NotFound, $"No account with id {accountId.Value}.");

                current = MapAccount(stored);
            }

            if (change(current) is not { } edited) return false;

            context.Session
                .Prepare("UPDATE accounts SET config_json = $config WHERE id = $id", "$config", "$id")
                .SetText(0, AccountConfigJson.Write(edited))
                .SetInt(1, accountId.Value)
                .Execute();

            return true;
        }, ct);

    private static bool IdentitiesEqual(IReadOnlyList<SenderIdentity> a, IReadOnlyList<SenderIdentity> b) =>
        a.SequenceEqual(b);

    private static bool SignaturesEqual(SignatureBook a, SignatureBook b) =>
        a.Items.SequenceEqual(b.Items) && a.DefaultForNew == b.DefaultForNew && a.DefaultForReply == b.DefaultForReply;

    private static string? StoredConfigJson(DbSession session, long accountId) =>
        session
            .Prepare("SELECT config_json FROM accounts WHERE id = $id", "$id")
            .SetInt(0, accountId)
            .ExecuteString();

    public IReadOnlyList<AccountConfig> ListAccounts(CancellationToken ct = default) =>
        Read(session =>
        {
            var accounts = new List<AccountConfig>();
            using var reader = session.Prepare(SelectAccount + " ORDER BY id").ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                accounts.Add(MapAccount(reader));
            }
            return (IReadOnlyList<AccountConfig>)accounts;
        }, ct);

    public AccountConfig? GetAccount(AccountId id, CancellationToken ct = default) =>
        Read<AccountConfig?>(session =>
        {
            using var reader = session
                .Prepare(SelectAccount + " WHERE id = $id", "$id")
                .SetInt(0, id.Value)
                .ExecuteReader();
            return reader.Read() ? MapAccount(reader) : null;
        }, ct);

    public AccountConfig? FindAccountByEmail(string email, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        return Read<AccountConfig?>(session =>
        {
            using var reader = session
                .Prepare(SelectAccount + " WHERE email = $email", "$email")
                .SetText(0, email)
                .ExecuteReader();
            return reader.Read() ? MapAccount(reader) : null;
        }, ct);
    }

    private static AccountConfig MapAccount(SqliteDataReader reader)
    {
        var id = new AccountId(reader.GetInt64(0));
        var email = reader.GetString(1);
        var displayName = Db.Str(reader, 2);
        var provider = ProviderKindExtensions.FromWireValue(reader.GetString(3));
        return AccountConfigJson.Read(reader.GetString(4), id, email, displayName, provider);
    }

    /// <summary>
    /// Removes an account and everything derived from it. Foreign keys cascade folders, messages,
    /// tags and body_text, but the FTS indexes are contentless — nothing cascades into them — so
    /// their rows are deleted explicitly first, or every future search would return phantom hits
    /// pointing at rows that no longer exist. Blobs left unreferenced afterwards are swept too.
    /// </summary>
    public Task<ForgottenAccount> ForgetAccountAsync(AccountId accountId, CancellationToken ct) =>
        WriteAsync(context =>
        {
            var folders = new List<long>();
            using (var reader = context.Session
                .Prepare("SELECT id FROM folders WHERE account_id = $account", "$account")
                .SetInt(0, accountId.Value)
                .ExecuteReader())
            {
                while (reader.Read()) folders.Add(reader.GetInt64(0));
            }

            foreach (var folder in folders)
            {
                ct.ThrowIfCancellationRequested();
                RemoveFolderFtsRows(context.Session, folder);
            }

            var messages = context.Session
                .Prepare("SELECT count(*) FROM messages WHERE account_id = $account", "$account")
                .SetInt(0, accountId.Value)
                .ExecuteInt64();

            var removed = context.Session
                .Prepare("DELETE FROM accounts WHERE id = $account", "$account")
                .SetInt(0, accountId.Value)
                .Execute();

            var blobs = context.Session.Exec(
                "DELETE FROM blobs WHERE id NOT IN (SELECT blob_id FROM messages WHERE blob_id IS NOT NULL)");

            return new ForgottenAccount
            {
                Existed = removed > 0,
                Folders = folders.Count,
                Messages = (int)messages,
                Blobs = blobs,
            };
        }, ct);
}

/// <summary>What <see cref="SqliteStore.ForgetAccountAsync"/> removed.</summary>
public sealed record ForgottenAccount
{
    public bool Existed { get; init; }
    public int Folders { get; init; }
    public int Messages { get; init; }
    public int Blobs { get; init; }

}
