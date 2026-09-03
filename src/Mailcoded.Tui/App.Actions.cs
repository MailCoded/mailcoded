using Mailcoded.Protocol;
using Mailcoded.Tui.Views;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    private void ShowThread()
    {
        if (Current() is not { ThreadKey: { Length: > 0 } key }) { _state.Complain("No thread for this one."); return; }

        Start("reading the thread", async token =>
        {
            var thread = await _client.GetThreadAsync(key, 200, token).ConfigureAwait(false);

            return () =>
            {
                _state.Messages.Clear();
                _state.Messages.AddRange(thread.Messages);
                _state.MessageIndex = 0;
                _state.NextCursor = null;
                _state.Open = null;
                _state.Focus = Pane.Messages;
                _state.Query = "thread";
                _state.Say($"{thread.Messages.Count} in this conversation - esc to go back");
            };
        });
    }

    /// <summary>Saves beside the store, never to a path from the message: the daemon already
    /// flattened the name, and a client that rebuilt a path from it would undo that.</summary>
    private void SaveAttachment(int index)
    {
        if (_state.Open is not { } open)
        {
            _state.Complain("Open a message first; s saves an attachment from the reader.");
            return;
        }

        if (index < 0 || index >= open.Attachments.Count)
        {
            _state.Complain(open.Attachments.Count == 0
                ? "This message has no attachments."
                : $"This message has {open.Attachments.Count}; press s then a digit.");
            return;
        }

        var attachment = open.Attachments[index];
        var messageId = open.Envelope.Id;

        Start($"saving {attachment.Filename}", async token =>
        {
            var content = await _client.GetAttachmentAsync(messageId, attachment.Index, token).ConfigureAwait(false);
            var bytes = Convert.FromBase64String(content.Base64);

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");

            Directory.CreateDirectory(directory);

            var name = Path.GetFileName(content.Filename);
            if (string.IsNullOrWhiteSpace(name)) name = $"attachment-{attachment.Index}";

            var path = Unused(Path.Combine(directory, name));
            await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);

            return () => _state.Say($"Saved {bytes.Length} bytes to {path}");
        });
    }

    /// <summary>Never overwrite: a second invoice.pdf is a second file, not a lost one.</summary>
    private static string Unused(string path)
    {
        if (!File.Exists(path)) return path;

        var directory = Path.GetDirectoryName(path) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({n}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }

        return path + ".new";
    }

    private void TestAccount()
    {
        if (_state.Account is not { } account) return;

        Start($"testing {account.Email}", async token =>
        {
            var result = await _client.TestAccountAsync(account.Id, token).ConfigureAwait(false);

            return () => _state.Say(
                $"imap {result.Imap} ({result.Folders} folders), smtp {result.Smtp}"
                + (result.SmtpDetail is { Length: > 0 } why ? $" - {why}" : string.Empty));
        });
    }

    private void ShowOutbox()
    {
        Start("reading the outbox", async token =>
        {
            var outbox = await _client.ListOutboxAsync(null, token).ConfigureAwait(false);

            return () =>
            {
                _state.Outbox = outbox.Entries;
                _state.Focus = Pane.Outbox;
                _state.Say(string.Empty);
            };
        });
    }

    private void ShowStatus()
    {
        Start("reading status", async token =>
        {
            var stats = await _client.GetStatsAsync(token).ConfigureAwait(false);
            var health = await _client.GetHealthAsync(token).ConfigureAwait(false);

            return () =>
            {
                _state.Status2 = new StatusReport { Stats = stats, Health = health };
                _state.Focus = Pane.Status;
                _state.Say(string.Empty);
            };
        });
    }
}
