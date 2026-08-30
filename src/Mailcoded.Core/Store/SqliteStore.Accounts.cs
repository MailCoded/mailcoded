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
        var json = AccountConfigJson.Write(config);

        return WriteAsync(context =>
        {
            var existing = context.Session
                .Prepare("SELECT id FROM accounts WHERE email = $email", "$email")
                .SetText(0, config.Email)
                .ExecuteNullableInt64();

            if (existing is { } id)
            {
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
                .SetText(3, json)
                .ExecuteInt64();

            return new AccountId(inserted);
        }, ct);
    }

    public Task UpdateAccountAsync(AccountConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.Id.IsNone)
            throw new ArgumentException("Updating an account requires its id.", nameof(config));

        var json = AccountConfigJson.Write(config);

        return WriteAsync(context =>
        {
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
}
