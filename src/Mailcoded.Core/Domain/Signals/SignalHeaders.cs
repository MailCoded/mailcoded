namespace Mailcoded.Core.Domain.Signals;

/// <summary>Raw, bounded header values: null means absent, empty means present with no value.</summary>
public sealed record SignalHeaders
{
    public string? ListId { get; init; }
    public string? ListUnsubscribe { get; init; }
    public string? ListUnsubscribePost { get; init; }
    public string? Precedence { get; init; }
    public string? AutoSubmitted { get; init; }
    public string? XAutoreply { get; init; }
    public string? XAutorespond { get; init; }
    public string? XFailedRecipients { get; init; }

    /// <summary>Every Authentication-Results instance in message order, topmost (most recently added) first.</summary>
    public IReadOnlyList<string> AuthenticationResults { get; init; } = [];

    public static SignalHeaders None { get; } = new();
}

public sealed class SignalHeaderCollector
{
    public const int MaxValueChars = 2_000;
    public const int MaxAuthenticationResultChars = 4_000;
    public const int MaxAuthenticationResults = 8;
    public const int MaxTotalChars = 16_000;

    private enum Slot
    {
        ListId,
        ListUnsubscribe,
        ListUnsubscribePost,
        Precedence,
        AutoSubmitted,
        XAutoreply,
        XAutorespond,
        XFailedRecipients,
        AuthenticationResults,
    }

    private static readonly Dictionary<string, Slot> Slots = new(StringComparer.OrdinalIgnoreCase)
    {
        ["List-Id"] = Slot.ListId,
        ["List-Unsubscribe"] = Slot.ListUnsubscribe,
        ["List-Unsubscribe-Post"] = Slot.ListUnsubscribePost,
        ["Precedence"] = Slot.Precedence,
        ["Auto-Submitted"] = Slot.AutoSubmitted,
        ["X-Autoreply"] = Slot.XAutoreply,
        ["X-Autorespond"] = Slot.XAutorespond,
        ["X-Failed-Recipients"] = Slot.XFailedRecipients,
        ["Authentication-Results"] = Slot.AuthenticationResults,
    };

    private readonly string?[] _single = new string?[(int)Slot.AuthenticationResults];
    private readonly List<string> _authentication = [];
    private int _total;
    private bool _authenticationClosed;

    public static IReadOnlyList<string> FetchNames { get; } = [.. Slots.Keys];

    public static bool Wants(string? field) => field is not null && Slots.ContainsKey(field);

    public void Add(string? field, string? value)
    {
        if (field is null || !Slots.TryGetValue(field, out var slot)) return;

        if (slot == Slot.AuthenticationResults)
        {
            AddAuthentication(value);
            return;
        }

        var index = (int)slot;
        var cleaned = Clean(value, MaxValueChars);
        var previous = _single[index];

        if (previous is null)
        {
            _single[index] = Spend(cleaned);
            return;
        }

        // Duplicates of a single-valued header are joined, so no instance can hide another.
        var joined = string.Concat(previous, ", ", cleaned);
        if (joined.Length > MaxValueChars || _total + cleaned.Length + 2 > MaxTotalChars) return;

        _total += cleaned.Length + 2;
        _single[index] = joined;
    }

    public SignalHeaders Build() => new()
    {
        ListId = _single[(int)Slot.ListId],
        ListUnsubscribe = _single[(int)Slot.ListUnsubscribe],
        ListUnsubscribePost = _single[(int)Slot.ListUnsubscribePost],
        Precedence = _single[(int)Slot.Precedence],
        AutoSubmitted = _single[(int)Slot.AutoSubmitted],
        XAutoreply = _single[(int)Slot.XAutoreply],
        XAutorespond = _single[(int)Slot.XAutorespond],
        XFailedRecipients = _single[(int)Slot.XFailedRecipients],
        AuthenticationResults = _authentication.Count == 0 ? [] : [.. _authentication],
    };

    private void AddAuthentication(string? value)
    {
        if (_authenticationClosed || _authentication.Count >= MaxAuthenticationResults) return;

        var cleaned = Clean(value, MaxAuthenticationResultChars);
        if (_total + cleaned.Length > MaxTotalChars)
        {
            // Keeping a smaller instance from below a dropped one would let a lower header stand in for it.
            _authenticationClosed = true;
            return;
        }

        _total += cleaned.Length;
        _authentication.Add(cleaned);
    }

    // Presence is the signal for most headers, so an over-budget value is kept as empty rather than dropped.
    private string Spend(string cleaned)
    {
        if (_total + cleaned.Length > MaxTotalChars) return string.Empty;

        _total += cleaned.Length;
        return cleaned;
    }

    private static string Clean(string? value, int maxChars)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var length = Math.Min(value.Length, maxChars);
        return string.Create(length, value, static (span, source) =>
        {
            for (var i = 0; i < span.Length; i++)
                span[i] = char.IsControl(source[i]) ? ' ' : source[i];
        }).Trim();
    }
}
