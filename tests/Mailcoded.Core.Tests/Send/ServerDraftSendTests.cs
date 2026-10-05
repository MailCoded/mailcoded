using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Outbox;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Domain.Sync;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Secrets;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;
using Xunit;

namespace Mailcoded.Core.Tests.Send;

public sealed class ServerDraftSendTests
{
    private static ServerDraft? StoredDraft(ServerDraftHarness harness, long outbox, CancellationToken ct) =>
        Require.Ref(harness.Inner.Store.GetOutbox(outbox, ct), "the outbox row").ServerDraft;

    private static int AuditCount(ServerDraftHarness harness, string @event, CancellationToken ct) =>
        (int)StoreQuery.Scalar(harness.Inner.Store, $"SELECT COUNT(*) FROM sync_log WHERE event = '{@event}'", ct);

    [Fact]
    public async Task An_account_that_has_not_opted_in_never_asks_for_a_connection()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct, optIn: false);

        var preview = await harness.PreviewAsync(harness.Connect, ct);

        Assert.Equal(ServerDraftSave.Off, preview.ServerDraft);
        Assert.Equal(0, harness.Connects);
        Assert.Empty(harness.Provider.Appends);
        Assert.Null(StoredDraft(harness, preview.OutboxId, ct));
    }

    [Fact]
    public async Task A_caller_with_no_connection_to_offer_makes_a_local_draft_only()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);

        var preview = await harness.PreviewAsync(null, ct);

        Assert.Equal(ServerDraftSave.Off, preview.ServerDraft);
        Assert.Empty(harness.Provider.Appends);
        Assert.NotEqual(0, preview.OutboxId);
    }

    [Fact]
    public async Task An_opted_in_draft_is_appended_to_drafts_with_the_draft_flag_and_not_unread()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);

        var preview = await harness.PreviewAsync(harness.Connect, ct);

        Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);
        var append = Assert.Single(harness.Provider.Appends);
        Assert.Equal("Drafts", append.Folder.Path.Value);
        Assert.Equal(MessageFlags.Draft, append.Flags);
        Assert.False(append.Flags.HasFlag(MessageFlags.Unread));

        var draft = Require.Ref(StoredDraft(harness, preview.OutboxId, ct), "the server draft");
        Assert.Equal("Drafts", draft.FolderPath);
        Assert.Equal(777u, draft.UidValidity.Value);
        Assert.Equal(100u, draft.Uid!.Value.Value);
        Assert.Equal(ServerDraftState.Saved, draft.State);
        Assert.Equal(1, AuditCount(harness, AuditEvents.ServerDraftSaved, ct));
    }

    [Fact]
    public async Task The_server_copy_is_the_very_bytes_that_will_be_sent_and_carries_no_bcc()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct, approved: ["@example.org", "@hidden.example"]);

        var preview = await harness.PreviewAsync(harness.Connect, ct, bcc: "secret@hidden.example");
        await harness.SendAsync(preview, harness.Provider, ct);

        var stored = Require.Ref(harness.Inner.Store.GetOutbox(preview.OutboxId, ct), "the outbox row").Raw;
        var appended = Assert.Single(harness.Provider.Appends).Raw;
        var submitted = Assert.Single(harness.Inner.Sender.Sends).Raw;

        Assert.Equal(stored, appended);
        Assert.Equal(stored, submitted);
        Assert.DoesNotContain("Bcc:", Encoding.UTF8.GetString(appended), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hidden.example", Encoding.UTF8.GetString(appended), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(preview.MessageId.Value, Encoding.UTF8.GetString(appended), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_append_never_blocks_the_local_draft_and_the_draft_can_still_be_sent()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.AppendFailure = new ProviderException(FailureCategory.Full, "over quota");

        var preview = await harness.PreviewAsync(harness.Connect, ct);

        Assert.Equal(ServerDraftSave.Failed, preview.ServerDraft);
        Assert.Equal(ServerDraftReason.AppendFailed, preview.ServerDraftReason);
        Assert.Null(StoredDraft(harness, preview.OutboxId, ct));
        Assert.Equal(1, AuditCount(harness, AuditEvents.ServerDraftSaveFailed, ct));

        harness.Provider.AppendFailure = null;
        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Null(result.ServerDraft);
        Assert.Empty(harness.Provider.Moves);
        Assert.True(result.AppendedToSent);
    }

    [Fact]
    public async Task A_connection_that_cannot_be_made_is_reported_not_thrown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);

        var offline = await harness.PreviewAsync(_ => Task.FromResult<IMailProvider?>(null), ct);
        var refused = await harness.PreviewAsync(
            _ => throw new ProviderException(FailureCategory.Network, "connection refused"),
            ct);
        var locked = await harness.PreviewAsync(
            _ => throw new SecretStoreException("keyring is locked"),
            ct);

        Assert.Equal(ServerDraftReason.NotConnected, offline.ServerDraftReason);
        Assert.Equal(ServerDraftReason.AppendFailed, refused.ServerDraftReason);
        Assert.Equal(ServerDraftReason.AppendFailed, locked.ServerDraftReason);
        Assert.All(new[] { offline, refused, locked }, p => Assert.Equal(ServerDraftSave.Failed, p.ServerDraft));
        Assert.Equal(3, harness.Inner.Store.ListOutbox(null, ct).Count);
    }

    [Fact]
    public async Task A_cancelled_connection_is_not_swallowed()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            harness.PreviewAsync(_ => throw new OperationCanceledException(), ct));
    }

    [Fact]
    public async Task An_account_with_no_drafts_folder_is_reported_without_connecting()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct, draftsFolder: false);

        var preview = await harness.PreviewAsync(harness.Connect, ct);

        Assert.Equal(ServerDraftSave.Failed, preview.ServerDraft);
        Assert.Equal(ServerDraftReason.NoDraftsFolder, preview.ServerDraftReason);
        Assert.Equal(0, harness.Connects);
    }

    [Fact]
    public async Task A_server_that_reports_no_uid_leaves_the_copy_alone_forever()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.AppendReportsNoUid = true;

        var preview = await harness.PreviewAsync(harness.Connect, ct);

        Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);
        var draft = Require.Ref(StoredDraft(harness, preview.OutboxId, ct), "the server draft");
        Assert.Equal(ServerDraftState.Left, draft.State);
        Assert.Equal(ServerDraftReason.UidUnknown, draft.Reason);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Empty(harness.Provider.Moves);
        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.True(result.AppendedToSent);
    }

    [Fact]
    public async Task A_copy_whose_folder_cannot_be_reopened_is_recorded_as_left_so_nothing_moves_it_blind()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.OpenFailure = new ProviderException(FailureCategory.Network, "dropped");

        var preview = await harness.PreviewAsync(harness.Connect, ct);
        var draft = Require.Ref(StoredDraft(harness, preview.OutboxId, ct), "the server draft");

        Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);
        Assert.Equal(ServerDraftState.Left, draft.State);
        Assert.True(draft.UidValidity.IsUnknown);
        Assert.Single(harness.Provider.Appends);
    }

    [Fact]
    public async Task A_server_that_reports_the_validity_with_the_append_is_not_asked_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.AppendReportsValidity = true;
        harness.Provider.OpenFailure = new ProviderException(FailureCategory.Network, "a SELECT here would queue behind a running sync");

        var preview = await harness.PreviewAsync(harness.Connect, ct);
        var draft = Require.Ref(StoredDraft(harness, preview.OutboxId, ct), "the server draft");

        Assert.Equal(ServerDraftState.Saved, draft.State);
        Assert.Equal(harness.Provider.FolderValidity, draft.UidValidity.Value);
        Assert.Empty(harness.Provider.Opened);
    }

    [Fact]
    public async Task A_server_that_reports_no_uid_is_not_selected_either()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.AppendReportsNoUid = true;

        await harness.PreviewAsync(harness.Connect, ct);

        Assert.Empty(harness.Provider.Opened);
    }

    [Fact]
    public async Task Drafting_writes_only_to_the_drafts_folder_and_only_the_one_message()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);

        await harness.PreviewAsync(harness.Connect, ct);

        var append = Assert.Single(harness.Provider.Appends);
        Assert.Equal(harness.Drafts.Path.Value, append.Folder.Path.Value);
        Assert.Empty(harness.Provider.Moves);
        Assert.Empty(harness.Provider.Flags);
    }

    [Fact]
    public async Task With_move_and_a_sent_copy_the_draft_is_moved_into_sent_and_nothing_is_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftState.Moved, result.ServerDraft);
        Assert.False(result.AppendedToSent);

        var move = Assert.Single(harness.Provider.Moves);
        Assert.Equal("Drafts", move.From.Path.Value);
        Assert.Equal("Sent", move.To.Path.Value);
        Assert.Equal(100u, move.Uid.Value);
        Assert.Equal(777u, move.Expected.Value);

        Assert.Single(harness.Provider.Appends);
        Assert.Equal("Drafts", harness.Provider.Appends[0].Folder.Path.Value);

        var flag = Assert.Single(harness.Provider.Flags);
        Assert.Equal("Sent", flag.Folder.Path.Value);
        Assert.Equal(500u, flag.Uid.Value);
        Assert.Equal(MessageFlags.Draft, flag.Delta.Remove);
        Assert.Equal(MessageFlags.None, flag.Delta.Add);

        Assert.Equal(ServerDraftState.Moved, StoredDraft(harness, preview.OutboxId, ct)!.State);
        Assert.Equal(1, AuditCount(harness, AuditEvents.ServerDraftMoved, ct));
    }

    [Fact]
    public async Task A_move_that_reports_no_new_uid_still_counts_and_just_cannot_clear_the_draft_flag()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveReportsNoNewUid = true;
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftState.Moved, result.ServerDraft);
        Assert.Empty(harness.Provider.Flags);
        Assert.False(result.AppendedToSent);
    }

    [Fact]
    public async Task A_failure_clearing_the_draft_flag_does_not_undo_the_move()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.FlagFailure = new ProviderException(FailureCategory.Busy, "try later");
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftState.Moved, result.ServerDraft);
        Assert.Equal(ServerDraftState.Moved, StoredDraft(harness, preview.OutboxId, ct)!.State);
    }

    [Fact]
    public async Task Without_move_the_draft_stays_the_sent_copy_is_appended_and_no_move_is_attempted()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.Capabilities = new ServerCaps { Move = false };
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.NoMoveCapability, result.ServerDraftReason);
        Assert.True(result.AppendedToSent);
        Assert.Empty(harness.Provider.Moves);
        Assert.Equal(["Drafts", "Sent"], harness.Provider.Appends.Select(a => a.Folder.Path.Value));
        Assert.Equal(ServerDraftState.Left, StoredDraft(harness, preview.OutboxId, ct)!.State);
        Assert.Equal(1, AuditCount(harness, AuditEvents.ServerDraftLeft, ct));
    }

    [Fact]
    public async Task A_server_that_saves_sent_mail_itself_keeps_the_draft_in_place_and_gets_no_second_sent_copy()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct, serverAutoSaves: true);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.ServerSavesSent, result.ServerDraftReason);
        Assert.False(result.AppendedToSent);
        Assert.Empty(harness.Provider.Moves);
        Assert.Single(harness.Provider.Appends);
    }

    [Fact]
    public async Task A_gmail_style_server_leaves_the_draft_where_it_is_and_never_moves_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.Capabilities = new ServerCaps { Move = true, GmailExtensions = true };
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.ServerSavesSent, result.ServerDraftReason);
        Assert.Empty(harness.Provider.Moves);
        Assert.Empty(harness.Provider.Flags);
    }

    [Fact]
    public async Task Asking_for_no_sent_copy_leaves_the_draft_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct, appendToSent: false);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.SentCopyNotWanted, result.ServerDraftReason);
        Assert.False(result.AppendedToSent);
        Assert.Empty(harness.Provider.Moves);
        Assert.Single(harness.Provider.Appends);
    }

    [Fact]
    public async Task A_send_with_no_connection_leaves_the_draft_and_is_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, null, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.NotConnected, result.ServerDraftReason);
    }

    [Fact]
    public async Task An_account_with_no_sent_folder_leaves_the_draft()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct, sentFolder: false);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftReason.NoSentFolder, result.ServerDraftReason);
        Assert.Empty(harness.Provider.Moves);
    }

    [Fact]
    public async Task A_renumbered_drafts_folder_leaves_the_draft_and_the_sent_copy_is_appended_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveStatus = AtomicMoveStatus.EpochChanged;
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.UidValidityChanged, result.ServerDraftReason);
        Assert.True(result.AppendedToSent);
        Assert.Empty(harness.Provider.Flags);
        Assert.Equal("Sent", harness.Provider.Appends[^1].Folder.Path.Value);
    }

    [Fact]
    public async Task A_draft_that_another_client_already_replaced_is_left_and_the_sent_copy_is_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveStatus = AtomicMoveStatus.SourceGone;
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftReason.DraftGone, result.ServerDraftReason);
        Assert.True(result.AppendedToSent);
    }

    [Fact]
    public async Task A_provider_that_cannot_move_reports_unsupported_and_the_sent_copy_is_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveStatus = AtomicMoveStatus.Unsupported;
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftReason.NoMoveCapability, result.ServerDraftReason);
        Assert.True(result.AppendedToSent);
    }

    [Fact]
    public async Task A_move_the_server_refused_changed_nothing_so_the_sent_copy_is_appended()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveFailure = new ProviderException(FailureCategory.Protocol, "NO [CANNOT] move");
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftReason.MoveRefused, result.ServerDraftReason);
        Assert.True(result.AppendedToSent);
    }

    [Fact]
    public async Task A_move_cut_off_by_the_network_may_have_happened_so_no_second_copy_is_made()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        harness.Provider.MoveFailure = new ProviderException(FailureCategory.Network, "connection reset");
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.MoveUnconfirmed, result.ServerDraftReason);
        Assert.False(result.AppendedToSent);
        Assert.Single(harness.Provider.Appends);
        Assert.Single(harness.Provider.Moves);
    }

    [Fact]
    public async Task A_send_whose_smtp_step_fails_leaves_the_draft_saved_and_a_later_success_moves_it()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        var flaky = new FlakySender(harness.Inner.Sender);

        var failure = await Assert.ThrowsAsync<SmtpDeliveryException>(() =>
            harness.Inner.Send.SendAsync(
                SendPathHarness.Caller,
                flaky,
                harness.Provider,
                preview.OutboxId,
                preview.ConfirmToken,
                new SendOptions(),
                ct));

        Assert.Equal(451, failure.StatusCode);
        Assert.Equal(ServerDraftState.Saved, StoredDraft(harness, preview.OutboxId, ct)!.State);
        Assert.Empty(harness.Provider.Moves);

        harness.Inner.Clock.Advance(TimeSpan.FromHours(1));
        var results = await harness.Inner.Send.ProcessDueAsync(flaky, harness.Provider, new SendOptions(), ct);

        var retried = Assert.Single(results);
        Assert.Equal(OutboxState.Sent, retried.State);
        Assert.Equal(ServerDraftState.Moved, retried.ServerDraft);
        Assert.Single(harness.Provider.Moves);
    }

    [Fact]
    public async Task A_send_cancelled_in_the_middle_of_the_move_is_never_moved_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        harness.Provider.MoveFailure = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.SendAsync(preview, harness.Provider, ct));
        Assert.Equal(ServerDraftState.Moving, StoredDraft(harness, preview.OutboxId, ct)!.State);
        Assert.Equal(OutboxState.Sent, Require.Ref(harness.Inner.Store.GetOutbox(preview.OutboxId, ct), "the outbox row").State);

        harness.Provider.MoveFailure = null;
        harness.Inner.Clock.Advance(TimeSpan.FromMinutes(11));
        var report = await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct);

        Assert.Equal(1, report.ServerDraftsLeft);
        var draft = StoredDraft(harness, preview.OutboxId, ct)!;
        Assert.Equal(ServerDraftState.Left, draft.State);
        Assert.Equal(ServerDraftReason.Interrupted, draft.Reason);
        Assert.Single(harness.Provider.Moves);
        Assert.Single(harness.Provider.Appends);
    }

    [Fact]
    public async Task A_send_another_process_may_still_be_settling_is_not_swept_until_it_is_old_enough_to_be_a_crash()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        harness.Provider.MoveFailure = new OperationCanceledException();
        await Assert.ThrowsAsync<OperationCanceledException>(() => harness.SendAsync(preview, harness.Provider, ct));

        harness.Inner.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(0, (await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct)).ServerDraftsLeft);
        Assert.Equal(ServerDraftState.Moving, StoredDraft(harness, preview.OutboxId, ct)!.State);

        harness.Inner.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Equal(1, (await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct)).ServerDraftsLeft);
    }

    [Fact]
    public async Task A_settlement_that_lost_its_state_to_another_writer_reports_the_stored_state_and_does_not_claim_a_move()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        harness.Provider.DuringMove = () => harness.Inner.Store.TransitionServerDraftAsync(
            preview.OutboxId, ServerDraftState.Moving, ServerDraftState.Left, ServerDraftReason.Interrupted, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(ServerDraftState.Left, result.ServerDraft);
        Assert.Equal(ServerDraftReason.Interrupted, result.ServerDraftReason);
        Assert.Equal(ServerDraftState.Left, StoredDraft(harness, preview.OutboxId, ct)!.State);
        Assert.Equal(0, AuditCount(harness, AuditEvents.ServerDraftMoved, ct));
    }

    [Fact]
    public async Task A_crash_between_the_send_and_the_move_is_swept_offline_without_a_move_or_an_append()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var message = OutboxMessage.Rehydrate(
            preview.OutboxId, harness.Inner.AccountId, preview.MessageId, OutboxState.Queued, 0,
            harness.Inner.Clock.UtcNow, null, null, null, null, false, OutboxMessage.DefaultMaxAttempts);
        message.BeginSending(harness.Inner.Clock.UtcNow);
        message.MarkSent(SmtpResult.Accepted("250 ok"));
        await harness.Inner.Store.SaveOutboxAsync(ToRecord(message), ct);
        harness.Inner.Clock.Advance(TimeSpan.FromMinutes(11));

        var report = await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct);

        Assert.Equal(1, report.ServerDraftsLeft);
        Assert.Empty(report.Outcomes);
        Assert.Equal(ServerDraftReason.Interrupted, StoredDraft(harness, preview.OutboxId, ct)!.Reason);
        Assert.Empty(harness.Provider.Moves);
        Assert.Single(harness.Provider.Appends);

        var again = await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct);
        Assert.Equal(0, again.ServerDraftsLeft);
        Assert.Same(OutboxReconcileReport.Empty, again);
    }

    [Fact]
    public async Task A_row_another_process_is_already_moving_is_neither_moved_nor_appended_again()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        await harness.Inner.Store.TransitionServerDraftAsync(
            preview.OutboxId, ServerDraftState.Saved, ServerDraftState.Moving, ServerDraftReason.None, ct);

        var result = await harness.SendAsync(preview, harness.Provider, ct);

        Assert.Equal(OutboxState.Sent, result.State);
        Assert.Equal(ServerDraftState.Moving, result.ServerDraft);
        Assert.False(result.AppendedToSent);
        Assert.Empty(harness.Provider.Moves);
        Assert.Single(harness.Provider.Appends);
    }

    [Fact]
    public async Task Sending_an_already_sent_draft_again_does_not_move_or_append_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);
        await harness.SendAsync(preview, harness.Provider, ct);

        await Assert.ThrowsAsync<ConfirmRequiredException>(() => harness.SendAsync(preview, harness.Provider, ct));
        Assert.Single(harness.Provider.Moves);
        Assert.Single(harness.Inner.Sender.Sends);
    }

    [Fact]
    public async Task A_row_still_stuck_in_sending_keeps_its_saved_draft_untouched()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var preview = await harness.PreviewAsync(harness.Connect, ct);

        var message = OutboxMessage.Rehydrate(
            preview.OutboxId, harness.Inner.AccountId, preview.MessageId, OutboxState.Queued, 0,
            harness.Inner.Clock.UtcNow, null, null, null, null, false, OutboxMessage.DefaultMaxAttempts);
        message.BeginSending(harness.Inner.Clock.UtcNow);
        await harness.Inner.Store.SaveOutboxAsync(ToRecord(message), ct);

        var report = await harness.Inner.Send.ReconcileStuckSendsAsync(null, null, null, ct);

        Assert.Equal(1, report.NeedsInvestigation);
        Assert.Equal(0, report.ServerDraftsLeft);
        Assert.Equal(ServerDraftState.Saved, StoredDraft(harness, preview.OutboxId, ct)!.State);
    }

    [Fact]
    public async Task Saving_a_draft_to_the_server_needs_no_send_unlock_because_it_only_reaches_the_users_own_drafts_folder()
    {
        var ct = TestContext.Current.CancellationToken;
        using var harness = await ServerDraftHarness.CreateAsync(ct);
        var agent = CallerContext.For(CallerKind.Mcp, "agent");

        var preview = await harness.Inner.Send.PreviewAsync(
            agent,
            harness.Inner.AccountId,
            new DraftRequest { To = [EmailAddress.Parse("anyone@elsewhere.example")], Subject = "x", BodyText = "y" },
            harness.Connect,
            ct);

        Assert.False(preview.Gate.Allowed);
        Assert.Equal(ServerDraftSave.Saved, preview.ServerDraft);
        Assert.Equal(["Drafts"], harness.Provider.Appends.Select(a => a.Folder.Path.Value));
        Assert.Empty(harness.Inner.Sender.Sends);
    }

    private static OutboxRecord ToRecord(OutboxMessage message) => new()
    {
        Id = message.Id,
        AccountId = message.AccountId,
        MessageId = message.MessageId,
        State = message.State,
        SmtpResponse = message.SmtpResponse,
        EnhancedStatusCode = message.EnhancedStatusCode,
        Attempts = message.Attempts,
        MaxAttempts = message.MaxAttempts,
        PermanentlyFailed = message.PermanentlyFailed,
        NextAttemptUtc = message.NextAttemptUtc,
        LastAttemptUtc = message.LastAttemptUtc,
        CreatedUtc = message.CreatedUtc,
    };

    private sealed class FlakySender(IMailSender inner) : IMailSender
    {
        private int _calls;

        public long? MaxMessageSize => inner.MaxMessageSize;

        public bool SupportsSmtpUtf8 => inner.SupportsSmtpUtf8;

        public Task ConnectAsync(AccountConfig cfg, ISecretStore secrets, CancellationToken ct) => inner.ConnectAsync(cfg, secrets, ct);

        public Task<string> SendAsync(byte[] raw, EmailAddress from, IReadOnlyList<EmailAddress> recipients, CancellationToken ct) =>
            _calls++ == 0
                ? throw SmtpDeliveryException.Create(451, "try again later", "4.3.0")
                : inner.SendAsync(raw, from, recipients, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
