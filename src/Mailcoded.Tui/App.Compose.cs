using Mailcoded.Protocol;
using Mailcoded.Protocol.Client;
using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    /// <summary>The one-time token, held here and nowhere else. AppState has no field for it, so
    /// no view can render it and no status line can leak it.</summary>
    private string? _confirmToken;

    private void Compose(DraftBuffer draft)
    {
        if (!_client.Supports(RpcMethods.SendPreview) || !_client.Supports(RpcMethods.Send))
        {
            _state.Complain("This daemon does not advertise send.");
            return;
        }

        _state.Draft = draft;
        _state.Focus = Pane.Compose;
        _state.Open = null;
        _state.BodyLines = null;
        _state.Say("tab moves field, ctrl-s previews, esc discards");
    }

    /// <summary>Where Ctrl-N is in cycling through what it offered, so another press shows the next one.</summary>
    private RecipientCycle? _cycle;

    private sealed record RecipientCycle(DraftField Field, int Start, int Length, IReadOnlyList<string> Candidates, int Index);

    private void CompleteRecipient()
    {
        if (_state.Draft is not { } draft) return;

        if (draft.Field is not (DraftField.To or DraftField.Cc or DraftField.Bcc))
        {
            _state.Say("Completion works in To, Cc and Bcc.");
            return;
        }

        if (!_client.Supports(RpcMethods.CorrespondentSuggest))
        {
            _state.Complain("This daemon does not offer correspondent suggestions.");
            return;
        }

        if (_cycle is { } cycle && cycle.Field == draft.Field)
        {
            var next = (cycle.Index + 1) % cycle.Candidates.Count;
            if (!draft.ReplaceInHeader(cycle.Field, cycle.Start, cycle.Length, cycle.Candidates[next]))
            {
                _cycle = null;
                return;
            }

            _cycle = cycle with { Length = cycle.Candidates[next].Length, Index = next };
            _state.Say($"{next + 1} of {cycle.Candidates.Count}: ctrl-n for the next");
            return;
        }

        if (_state.Account is not { } account)
        {
            _state.Complain("No account to complete from.");
            return;
        }

        var field = draft.Field;
        var typed = draft.Header(field);
        var token = RecipientCompletion.TokenAt(typed, draft.HeaderCaret);

        Start("suggesting", async cancellation =>
        {
            var result = await _client
                .SuggestCorrespondentsAsync(account.Id, token.Prefix, RecipientCompletion.MaxSuggestions, cancellation)
                .ConfigureAwait(false);

            return () =>
            {
                if (!ReferenceEquals(_state.Draft, draft) || draft.Field != field || draft.Header(field) != typed) return;

                var candidates = RecipientCompletion.Unlisted(typed, token, result.Correspondents.Select(c => c.Address));
                if (candidates.Count == 0)
                {
                    _state.Say("No one you have written to matches.");
                    return;
                }

                if (!draft.ReplaceInHeader(field, token.Start, token.Length, candidates[0])) return;

                _cycle = new RecipientCycle(field, token.Start, candidates[0].Length, candidates, 0);
                _state.Say(candidates.Count == 1 ? "Completed." : $"1 of {candidates.Count}: ctrl-n for the next");
            };
        });
    }

    private void ComposeNew()
    {
        var draft = new DraftBuffer();
        if (SignatureBlock.DefaultText(_state.Account, reply: false) is { } signature)
            draft.SetBody(SignatureBlock.Body(signature, null));

        Compose(draft);
    }

    private void ComposeReply(bool all)
    {
        if (_state.Open is not { } open) return;

        var envelope = open.Envelope;
        var to = new List<string>();
        var cc = new List<string>();

        if (!string.IsNullOrWhiteSpace(envelope.From)) to.Add(envelope.From);

        if (all)
        {
            var mine = new List<string>();
            if (_state.Account is { } own)
            {
                mine.Add(own.Email);
                foreach (var identity in own.Identities) mine.Add(identity.Address);
            }

            foreach (var address in DraftBuffer.SplitAddresses(envelope.To ?? string.Empty))
                if (!mine.Contains(address, StringComparer.OrdinalIgnoreCase)) cc.Add(address);

            foreach (var address in DraftBuffer.SplitAddresses(envelope.Cc ?? string.Empty))
                if (!mine.Contains(address, StringComparer.OrdinalIgnoreCase)) cc.Add(address);
        }

        var draft = new DraftBuffer
        {
            To = string.Join(", ", to),
            Cc = string.Join(", ", cc),
            Subject = Prefixed(envelope.Subject),
            ReplyToMessageId = envelope.Id,
            InReplyTo = envelope.MessageId,
        };

        draft.SetBody(SignatureBlock.Body(
            SignatureBlock.DefaultText(_state.Account, reply: true),
            DraftBuffer.Quote(envelope.From, open.BodyText, 200)));
        draft.Field = DraftField.Body;
        draft.BodyLine = 0;
        draft.BodyColumn = 0;

        Compose(draft);
    }

    private static string Prefixed(string? subject)
    {
        var text = subject?.Trim() ?? string.Empty;
        return text.StartsWith("Re:", StringComparison.OrdinalIgnoreCase) ? text : "Re: " + text;
    }

    private void Discard()
    {
        _state.Draft = null;
        _state.Pending = null;
        _confirmToken = null;
        _state.Focus = Pane.Messages;
        _state.Say("Draft discarded.");
    }

    private void PreviewSend()
    {
        if (_state.Draft is not { } draft) return;

        if (_state.Account is not { } account)
        {
            _state.Complain("No account to send from.");
            return;
        }

        if (DraftBuffer.SplitAddresses(draft.To).Count == 0)
        {
            _state.Complain("A message needs at least one recipient.");
            return;
        }

        var dto = new DraftDto
        {
            To = DraftBuffer.SplitAddresses(draft.To),
            Cc = DraftBuffer.SplitAddresses(draft.Cc),
            Bcc = DraftBuffer.SplitAddresses(draft.Bcc),
            Subject = draft.Subject,
            BodyText = draft.BodyText,
            InReplyTo = draft.InReplyTo,
            References = draft.References,
            ReplyToMessageId = draft.ReplyToMessageId,
        };

        Start("previewing", async token =>
        {
            var result = await _client.PreviewSendAsync(account.Id, dto, token).ConfigureAwait(false);

            return () =>
            {
                _confirmToken = result.ConfirmToken;
                _state.Pending = new PendingSend
                {
                    AccountId = account.Id,
                    DraftId = result.DraftId,
                    Preview = result.Preview,
                    ExpiresUtc = Expiry(result.ConfirmTokenExpiresUtc),
                };

                _state.Focus = Pane.Confirm;
                _state.Say(string.Empty);
            };
        });
    }

    private static DateTimeOffset? Expiry(string? iso) =>
        DateTimeOffset.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private void ConfirmedSend()
    {
        if (_state.Pending is not { } pending || _confirmToken is not { } token)
        {
            _state.Complain("There is nothing confirmed to send.");
            return;
        }

        if (pending.ExpiresUtc is { } deadline && deadline <= DateTimeOffset.UtcNow)
        {
            RePreview("That confirmation expired. Preview again.");
            return;
        }

        // Consumed here whatever the outcome: a one-time token is not retried.
        _confirmToken = null;

        Start("sending", async cancellation =>
        {
            try
            {
                var result = await _client
                    .SendAsync(pending.AccountId, pending.DraftId, token, cancellation)
                    .ConfigureAwait(false);

                return () =>
                {
                    _state.Pending = null;
                    _state.Draft = null;
                    _state.Focus = Pane.Messages;
                    _state.Say($"Sent: {result.State}.");
                };
            }
            catch (RpcException ex)
            {
                var message = ExplainSend(ex);
                return () => RePreview(message);
            }
        });
    }

    private void RePreview(string why)
    {
        _confirmToken = null;
        _state.Pending = null;
        _state.Focus = Pane.Compose;
        _state.Complain(why);
    }

    private void CancelConfirmation()
    {
        _confirmToken = null;
        _state.Pending = null;
        _state.Focus = Pane.Compose;
        _state.Say("Not sent. The draft is still here.");
    }

    /// <summary>Send failures are the ones worth naming precisely: docs/rpc.md §8.</summary>
    private static string ExplainSend(RpcException ex) => ex.Code switch
    {
        (int)RpcErrorCode.ConfirmRequired => "That confirmation is spent or expired. Preview again.",
        (int)RpcErrorCode.Forbidden =>
            $"{ex.Message}  The send gate is closed on purpose; this client will not reopen it.",
        (int)RpcErrorCode.RateLimited when ex.RetryAfterMs is { } wait =>
            $"{ex.Message}  The hourly send budget reopens in {wait / 1000}s.",
        (int)RpcErrorCode.Auth => $"{ex.Message}  Run 'mailcoded account reauth'.",
        _ => ex.Message,
    };
}
