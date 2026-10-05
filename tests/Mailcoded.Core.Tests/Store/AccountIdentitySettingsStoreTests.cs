using System.Text.Json;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Store;

public sealed class AccountIdentitySettingsStoreTests
{
    private static SenderIdentity Identity(string address, string? name = null, string? replyTo = null)
    {
        Assert.True(SenderIdentity.TryCreate(address, name, replyTo, out var identity, out var error), error);
        return identity;
    }

    private static AccountConfig Stored(TempStore temp, AccountId id) =>
        Require.Ref(temp.Store.GetAccount(id, TestContext.Current.CancellationToken), "the account");

    private static void WriteRawConfig(TempStore temp, AccountId id, string json)
    {
        using var connection = LegacyV1Database.OpenRaw(temp.Workspace.DatabasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE accounts SET config_json = $json WHERE id = $id";
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$id", id.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    [Fact]
    public async Task A_row_written_before_identities_and_signatures_existed_reads_as_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        WriteRawConfig(temp, id, """{"version":1,"secretRef":"mailcoded:imap:tester@example.com","auth":"password","imap":{"host":"imap.example.com"}}""");

        var account = Stored(temp, id);
        Assert.Empty(account.Identities);
        Assert.True(account.Signatures.IsEmpty);
        Assert.Null(account.Signatures.DefaultForNew);
        Assert.Null(account.Signatures.DefaultForReply);
    }

    [Fact]
    public async Task Identities_round_trip_with_their_name_and_reply_to()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        Assert.True(await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("sales@example.com", "Sales", "help@example.com")), ct));
        Assert.True(await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("plain@example.com")), ct));

        var identities = Stored(temp, id).Identities;
        Assert.Equal(["sales@example.com", "plain@example.com"], identities.Select(i => i.Address.Value));
        Assert.Equal("Sales", identities[0].DisplayName);
        Assert.Equal("help@example.com", identities[0].ReplyTo?.Value);
        Assert.Null(identities[1].DisplayName);
        Assert.Null(identities[1].ReplyTo);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_reports_false_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        Assert.False(await temp.Store.EditIdentitiesAsync(id, list => list, ct));
        Assert.True(await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("a@example.com")), ct));
        Assert.False(await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("a@example.com")), ct));
        Assert.False(await temp.Store.EditSignaturesAsync(id, book => book, ct));
        Assert.True(await temp.Store.EditSignaturesAsync(id, book => book.Set("work", "Alex"), ct));
        Assert.False(await temp.Store.EditSignaturesAsync(id, book => book.Set("WORK", "Alex"), ct));
    }

    [Fact]
    public async Task A_failing_edit_leaves_the_stored_settings_alone()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        await temp.Store.EditSignaturesAsync(id, book => book.Set("work", "Alex"), ct);

        await Assert.ThrowsAsync<ArgumentException>(() => temp.Store.EditSignaturesAsync(id, book => book.Set("work", new string('x', 5_000)), ct));
        await Assert.ThrowsAsync<ArgumentException>(() => temp.Store.EditSignaturesAsync(id, book => book.WithDefault(SignatureUse.New, "missing"), ct));

        var book = Stored(temp, id).Signatures;
        Assert.Equal("Alex", book.Find("work")?.Text);
        Assert.Null(book.DefaultForNew);
    }

    [Fact]
    public async Task Editing_an_account_that_does_not_exist_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();

        var failure = await Assert.ThrowsAsync<StoreException>(() => temp.Store.EditIdentitiesAsync(new AccountId(999), list => list, ct));
        Assert.Equal(FailureCategory.NotFound, failure.Category);
    }

    [Fact]
    public async Task Signatures_and_both_defaults_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        await temp.Store.EditSignaturesAsync(
            id,
            book => book.Set("work", "Alex\nExample Pty").Set("home", "Alex").WithDefault(SignatureUse.New, "work").WithDefault(SignatureUse.Reply, "home"),
            ct);

        var book = Stored(temp, id).Signatures;
        Assert.Equal(["work", "home"], book.Items.Select(i => i.Name));
        Assert.Equal("Alex\nExample Pty", book.Find("work")?.Text);
        Assert.Equal("work", book.DefaultForNew);
        Assert.Equal("home", book.DefaultForReply);

        await temp.Store.EditSignaturesAsync(id, b => b.Forget("work", out _), ct);
        Assert.Null(Stored(temp, id).Signatures.DefaultForNew);
        Assert.Equal("home", Stored(temp, id).Signatures.DefaultForReply);
    }

    [Fact]
    public async Task Re_registering_the_account_keeps_identities_and_signatures()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("sales@example.com")), ct);
        await temp.Store.EditSignaturesAsync(id, book => book.Set("work", "Alex").WithDefault(SignatureUse.New, "work"), ct);

        await temp.Store.AddAccountAsync(StoreSeed.AccountConfigFor("tester@example.com") with { DisplayName = "Renamed" }, ct);

        var account = Stored(temp, id);
        Assert.Equal("Renamed", account.DisplayName);
        Assert.Equal(["sales@example.com"], account.Identities.Select(i => i.Address.Value));
        Assert.Equal("work", account.Signatures.DefaultForNew);
    }

    [Fact]
    public async Task A_config_update_from_stale_memory_cannot_overwrite_them()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        var stale = Stored(temp, id);

        await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("sales@example.com")), ct);
        await temp.Store.EditSignaturesAsync(id, book => book.Set("work", "Alex"), ct);

        await temp.Store.UpdateAccountAsync(
            stale with
            {
                Identities = [Identity("attacker@example.org")],
                Signatures = SignatureBook.Empty.Set("evil", "Buy gift cards"),
            },
            ct);

        var account = Stored(temp, id);
        Assert.Equal(["sales@example.com"], account.Identities.Select(i => i.Address.Value));
        Assert.Equal(["work"], account.Signatures.Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Changing_the_trusted_servers_does_not_disturb_them()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);
        await temp.Store.EditIdentitiesAsync(id, list => SenderIdentities.With(list, Identity("sales@example.com")), ct);

        await temp.Store.SetTrustedAuthservIdsAsync(id, ["mx.example.net"], ct);

        Assert.Equal(["sales@example.com"], Stored(temp, id).Identities.Select(i => i.Address.Value));
    }

    [Fact]
    public async Task A_hand_edited_row_cannot_smuggle_in_a_bad_address_or_hostile_signature_text()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        var config = new
        {
            version = 1,
            secretRef = "x",
            auth = "password",
            imap = new { host = "imap.example.com" },
            identities = new object[]
            {
                new { address = "good@example.com", displayName = "Good" },
                new { address = "bad\r\nBcc: victim@example.org" },
                new { address = "not an address" },
                new { address = "named@example.com", displayName = "Evil\r\nBcc: victim@example.org" },
                new { address = "ok@example.com", replyTo = "nope" },
                "a string",
                42,
            },
            signatures = new object[]
            {
                new { name = "fine", text = "Alex‮\u0000\r\nExample" },
                new { name = "bad name", text = "x" },
                new { name = "empty", text = "  " },
                new { name = "long", text = new string('x', 5_000) },
                new { name = "fine2", text = "-- \nAlex" },
            },
            defaultSignature = new { @new = "FINE", reply = "missing" },
        };

        WriteRawConfig(temp, id, JsonSerializer.Serialize(config));

        var account = Stored(temp, id);
        Assert.Equal(["good@example.com"], account.Identities.Select(i => i.Address.Value));
        Assert.Equal(["fine", "fine2"], account.Signatures.Items.Select(i => i.Name));
        Assert.Equal("Alex\nExample", account.Signatures.Find("fine")?.Text);
        Assert.Equal("Alex", account.Signatures.Find("fine2")?.Text);
        Assert.Equal("fine", account.Signatures.DefaultForNew);
        Assert.Null(account.Signatures.DefaultForReply);
    }

    [Fact]
    public async Task A_hand_edited_row_with_the_wrong_json_shape_reads_as_none()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        WriteRawConfig(temp, id, """{"version":1,"identities":"all","signatures":{"a":"b"},"defaultSignature":"x"}""");

        var account = Stored(temp, id);
        Assert.Empty(account.Identities);
        Assert.True(account.Signatures.IsEmpty);
    }

    [Fact]
    public async Task A_row_with_too_many_identities_keeps_only_the_cap()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = TempStore.Create();
        var id = await StoreSeed.AccountAsync(temp.Store, ct);

        var identities = Enumerable.Range(0, SenderIdentities.MaxIdentities + 10).Select(i => new { address = $"a{i}@example.com" }).ToArray();
        WriteRawConfig(temp, id, JsonSerializer.Serialize(new { version = 1, identities }));

        Assert.Equal(SenderIdentities.MaxIdentities, Stored(temp, id).Identities.Count);
    }
}
