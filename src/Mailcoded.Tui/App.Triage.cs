using Mailcoded.Protocol;

namespace Mailcoded.Tui;

internal sealed partial class App
{
    private void ToggleTag(string tag)
    {
        if (Current() is not { } envelope) return;

        var present = envelope.Tags.Contains(tag, StringComparer.Ordinal)
            || envelope.Flags.Contains(tag, StringComparer.Ordinal);

        ApplyTags(
            envelope.Id,
            present ? [] : [tag],
            present ? [tag] : [],
            present ? $"removing {tag}" : $"adding {tag}");
    }

    private void ApplyTags(long messageId, IReadOnlyList<string> add, IReadOnlyList<string> remove, string label)
    {
        if (!_client.Supports(RpcMethods.TagsSet))
        {
            _state.Complain("This daemon does not advertise tags.set.");
            return;
        }

        Start(label, async token =>
        {
            var result = await _client.SetTagsAsync(messageId, add, remove, token).ConfigureAwait(false);

            return () =>
            {
                Restamp(messageId, result.Tags);
                _state.Say(Describe(add, remove));
            };
        });
    }

    private static string Describe(IReadOnlyList<string> add, IReadOnlyList<string> remove)
    {
        var parts = new List<string>(add.Count + remove.Count);
        foreach (var tag in add) parts.Add("+" + tag);
        foreach (var tag in remove) parts.Add("-" + tag);
        return string.Join(' ', parts);
    }

    /// <summary>tags.set returns the whole tag set and the daemon projects flags in the same
    /// transaction, so the row is re-derived rather than patched.</summary>
    private void Restamp(long messageId, IReadOnlyList<string> tags)
    {
        var flags = new List<string>(tags.Count);
        foreach (var name in tags)
            if (FlagNames.IsKnown(name)) flags.Add(name);

        for (var i = 0; i < _state.Messages.Count; i++)
        {
            if (_state.Messages[i].Id != messageId) continue;
            _state.Messages[i] = _state.Messages[i] with { Tags = tags, Flags = flags };
        }

        if (_state.Open is { } open && open.Envelope.Id == messageId)
            _state.Open = open with { Envelope = open.Envelope with { Tags = tags, Flags = flags } };
    }

    private void PromptedTags(string line)
    {
        if (Current() is not { } envelope) return;

        var add = new List<string>();
        var remove = new List<string>();

        foreach (var token in line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.StartsWith('-') && token.Length > 1) remove.Add(token[1..]);
            else add.Add(token.StartsWith('+') ? token[1..] : token);
        }

        if (add.Count == 0 && remove.Count == 0) return;
        ApplyTags(envelope.Id, add, remove, "setting tags");
    }

    private void Move(FolderDto destination)
    {
        if (Current() is not { } envelope) return;

        if (!_client.Supports(RpcMethods.MessageMove))
        {
            _state.Complain("This daemon does not advertise message.move.");
            return;
        }

        if (envelope.FolderId == destination.Id)
        {
            _state.Say($"Already in {destination.Name}.");
            return;
        }

        Start($"moving to {destination.Name}", async token =>
        {
            await _client.MoveAsync(envelope.Id, destination.Id, token).ConfigureAwait(false);

            return () =>
            {
                var index = _state.Messages.FindIndex(m => m.Id == envelope.Id);
                if (index >= 0) _state.Messages.RemoveAt(index);
                if (_state.MessageIndex >= _state.Messages.Count)
                    _state.MessageIndex = Math.Max(0, _state.Messages.Count - 1);

                if (_state.Open is { } open && open.Envelope.Id == envelope.Id) CloseReader();
                _state.Say($"Moved to {destination.Name}.");
            };
        });
    }

    private void Archive()
    {
        var archive = _state.Folders.FirstOrDefault(f => f.Role is "archive");

        if (archive is null)
        {
            _state.Complain("This account has no archive folder; use m to pick one.");
            return;
        }

        Move(archive);
    }

    /// <summary>The message a triage key acts on: the open one, else the highlighted row.</summary>
    private EnvelopeDto? Current() => _state.Open?.Envelope ?? _state.Selected;
}
