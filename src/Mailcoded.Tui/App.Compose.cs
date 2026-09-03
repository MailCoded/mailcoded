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

    private void ComposeNew() => Compose(new DraftBuffer());

    private void ComposeReply(bool all)
    {
        if (_state.Open is not { } open) return;

        var envelope = open.Envelope;
        var to = new List<string>();
        var cc = new List<string>();

        if (!string.IsNullOrWhiteSpace(envelope.From)) to.Add(envelope.From);

        if (all)
        {
            var mine = _state.Account?.Email;
            foreach (var address in DraftBuffer.SplitAddresses(envelope.To ?? string.Empty))
                if (!string.Equals(address, mine, StringComparison.OrdinalIgnoreCase)) cc.Add(address);

            foreach (var address in DraftBuffer.SplitAddresses(envelope.Cc ?? string.Empty))
                if (!string.Equals(address, mine, StringComparison.OrdinalIgnoreCase)) cc.Add(address);
        }

        var draft = new DraftBuffer
        {
            To = string.Join(", ", to),
            Cc = string.Join(", ", cc),
            Subject = Prefixed(envelope.Subject),
            ReplyToMessageId = envelope.Id,
            InReplyTo = envelope.MessageId,
        };

        draft.SetBody(DraftBuffer.Quote(envelope.From, open.BodyText, 200));
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
