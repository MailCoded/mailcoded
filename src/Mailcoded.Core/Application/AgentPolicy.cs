using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Core.Application;

/// <summary>The capabilities the posture matrix covers. There is deliberately no delete member.</summary>
public enum AgentCapability
{
    Read,
    Search,
    Thread,
    Tag,
    Draft,
    Send,
    RawSql,
    Move,
    HtmlBody,
}

/// <summary>The outcome of the send gate, including the label the audit row records.</summary>
public sealed record SendGateDecision
{
    public required bool Allowed { get; init; }
    public PolicyDenialReason Reason { get; init; } = PolicyDenialReason.None;
    public string? Detail { get; init; }
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>allowed | denied | rate-limited</summary>
    public string Label => Allowed
        ? "allowed"
        : Reason == PolicyDenialReason.RateLimited ? "rate-limited" : "denied";

    public static readonly SendGateDecision Allow = new() { Allowed = true };

    public static SendGateDecision Deny(PolicyDenialReason reason, string detail, TimeSpan? retryAfter = null) =>
        new() { Allowed = false, Reason = reason, Detail = detail, RetryAfter = retryAfter };

    public void ThrowIfDenied()
    {
        if (Allowed) return;
        throw new PolicyDeniedException(Reason, Detail ?? "The request was denied by policy.", RetryAfter);
    }
}

/// <summary>The outcome of the gated read-only SQL surface, carrying the row cap actually applied.</summary>
public sealed record SqlGateDecision
{
    public required bool Allowed { get; init; }
    public PolicyDenialReason Reason { get; init; } = PolicyDenialReason.None;
    public string? Detail { get; init; }
    public int RowCap { get; init; }

    public string Label => Allowed ? "allowed" : "denied";

    public void ThrowIfDenied()
    {
        if (Allowed) return;
        throw new PolicyDeniedException(Reason, Detail ?? "Raw SQL is not available.");
    }
}

/// <summary>The AGENT-INTERFACE §13.6 posture matrix as one class. Gates live here, never in an adapter.</summary>
public sealed class AgentPolicy
{
    public const int SendWindowMs = 3_600_000;

    private readonly IClock _clock;
    private readonly Lock _gate = new();
    private readonly Queue<long> _sendTicks = new();

    public AgentPolicy(AgentPolicyOptions options, IClock clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);

        Options = options;
        _clock = clock;
    }

    public static AgentPolicy FromEnvironment(IClock clock) => new(AgentPolicyOptions.FromEnvironment(), clock);

    public AgentPolicyOptions Options { get; }

    /// <summary>The posture matrix. Read, search, thread, tag, and draft are on for every caller.</summary>
    public bool IsEnabled(AgentCapability capability, CallerKind caller) => capability switch
    {
        AgentCapability.Read or AgentCapability.Search or AgentCapability.Thread
            or AgentCapability.Tag or AgentCapability.Draft => true,
        AgentCapability.Send => !IsAgent(caller) || Options.SendEnabled,
        AgentCapability.RawSql => Options.RawSqlEnabled,
        AgentCapability.Move or AgentCapability.HtmlBody => !IsAgent(caller),
        _ => false,
    };

    /// <summary>Agents receive plaintext bodies only, which kills the markdown and image exfil vectors.</summary>
    public bool AllowsHtmlBody(CallerKind caller) => !IsAgent(caller);

    public bool AllowsMove(CallerKind caller) => !IsAgent(caller);

    public void RequireCapability(AgentCapability capability, CallerContext caller)
    {
        if (IsEnabled(capability, caller.Kind)) return;

        var reason = capability switch
        {
            AgentCapability.Send => PolicyDenialReason.SendDisabled,
            AgentCapability.RawSql => PolicyDenialReason.RawSqlDisabled,
            AgentCapability.HtmlBody => PolicyDenialReason.HtmlBodyDenied,
            _ => PolicyDenialReason.OperationNotAvailable,
        };

        throw new PolicyDeniedException(
            reason,
            $"'{capability}' is not available to a '{caller.InterfaceName}' caller under the current policy.");
    }

    /// <summary>Evaluates the send gate without consuming budget, so a preview can report the posture.</summary>
    public SendGateDecision EvaluateSend(CallerContext caller, IReadOnlyList<EmailAddress> recipients)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        if (caller.Kind == CallerKind.Internal) return SendGateDecision.Allow;
        if (!caller.IsAgentSurface) return SendGateDecision.Allow;

        if (!Options.SendEnabled)
        {
            return SendGateDecision.Deny(
                PolicyDenialReason.SendDisabled,
                $"Sending from the agent surface requires {AgentPolicyOptions.SendEnvVar}=1.");
        }

        foreach (var recipient in recipients)
        {
            if (IsRecipientApproved(recipient, Options.ApprovedRecipients)) continue;

            return SendGateDecision.Deny(
                PolicyDenialReason.RecipientNotApproved,
                $"Recipient '{recipient.Value}' is not in {AgentPolicyOptions.ApprovedRecipientsEnvVar}.");
        }

        lock (_gate)
        {
            Evict();
            if (_sendTicks.Count < Options.MaxSendsPerHour) return SendGateDecision.Allow;

            var oldest = _sendTicks.Peek();
            var elapsed = _clock.Ticks - oldest;
            var remaining = SendWindowMs - elapsed;
            if (remaining < 0) remaining = 0;

            return SendGateDecision.Deny(
                PolicyDenialReason.RateLimited,
                $"The agent send budget of {Options.MaxSendsPerHour} per hour is spent.",
                TimeSpan.FromMilliseconds(remaining));
        }
    }

    /// <summary>Consumes one slot of the sliding window. Called only after the send was dispatched.</summary>
    public void RecordSend(CallerContext caller)
    {
        if (!caller.IsAgentSurface) return;

        lock (_gate)
        {
            Evict();
            _sendTicks.Enqueue(_clock.Ticks);
        }
    }

    public int RemainingSendsInWindow()
    {
        lock (_gate)
        {
            Evict();
            var remaining = Options.MaxSendsPerHour - _sendTicks.Count;
            return remaining < 0 ? 0 : remaining;
        }
    }

    /// <summary>Exact address or <c>@domain</c> / <c>*@domain</c>. An empty allowlist approves nothing.</summary>
    public static bool IsRecipientApproved(EmailAddress address, IReadOnlyList<string> patterns)
    {
        ArgumentNullException.ThrowIfNull(patterns);
        if (patterns.Count == 0) return false;

        var value = address.Value;
        if (string.IsNullOrEmpty(value)) return false;
        var domain = address.Domain;

        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern)) continue;

            if (pattern[0] == '@')
            {
                if (DomainMatches(domain, pattern.AsSpan(1))) return true;
                continue;
            }

            if (pattern.Length > 2 && pattern[0] == '*' && pattern[1] == '@')
            {
                if (DomainMatches(domain, pattern.AsSpan(2))) return true;
                continue;
            }

            if (string.Equals(value, pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    /// <summary>The raw-SQL read gate: environment flag, single read-only statement, hard row cap.</summary>
    public SqlGateDecision EvaluateRawSql(CallerContext caller, string? sql, int requestedRows)
    {
        if (!Options.RawSqlEnabled)
        {
            return new SqlGateDecision
            {
                Allowed = false,
                Reason = PolicyDenialReason.RawSqlDisabled,
                Detail = $"Raw SQL reads require {AgentPolicyOptions.EnableSqlEnvVar}=1.",
            };
        }

        if (!IsSingleReadOnlyStatement(sql))
        {
            return new SqlGateDecision
            {
                Allowed = false,
                Reason = PolicyDenialReason.RawSqlNotReadOnly,
                Detail = "Only a single SELECT or WITH statement is accepted.",
            };
        }

        var hardCap = Options.SqlRowHardCap < 1 ? AgentPolicyOptions.MaxSqlRowCap : Options.SqlRowHardCap;
        var cap = requestedRows <= 0 ? Options.SqlRowCap : requestedRows;
        if (cap > hardCap) cap = hardCap;
        if (cap < 1) cap = 1;

        return new SqlGateDecision { Allowed = true, RowCap = cap };
    }

    /// <summary>Defence in depth over <c>PRAGMA query_only</c>: nothing but one SELECT or WITH runs.</summary>
    public static bool IsSingleReadOnlyStatement(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return false;
        if (sql.Length > 8192) return false;

        var span = sql.AsSpan();
        var start = SkipTrivia(span);
        if (start >= span.Length) return false;

        var body = span[start..];
        if (!StartsWithKeyword(body, "select") && !StartsWithKeyword(body, "with")) return false;

        var semicolon = sql.IndexOf(';');
        if (semicolon >= 0 && !sql.AsSpan(semicolon + 1).IsWhiteSpace()) return false;

        return true;
    }

    private static bool DomainMatches(string domain, ReadOnlySpan<char> pattern)
    {
        if (pattern.Length == 0) return false;
        return domain.AsSpan().Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAgent(CallerKind caller) => caller is CallerKind.Cli or CallerKind.Mcp;

    private void Evict()
    {
        var now = _clock.Ticks;
        while (_sendTicks.Count > 0 && now - _sendTicks.Peek() >= SendWindowMs) _sendTicks.Dequeue();
    }

    private static int SkipTrivia(ReadOnlySpan<char> span)
    {
        var i = 0;
        while (i < span.Length)
        {
            if (char.IsWhiteSpace(span[i])) { i++; continue; }

            if (span[i] == '-' && i + 1 < span.Length && span[i + 1] == '-')
            {
                while (i < span.Length && span[i] is not ('\n' or '\r')) i++;
                continue;
            }

            if (span[i] == '/' && i + 1 < span.Length && span[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < span.Length && !(span[i] == '*' && span[i + 1] == '/')) i++;
                i = i + 1 < span.Length ? i + 2 : span.Length;
                continue;
            }

            break;
        }

        return i;
    }

    private static bool StartsWithKeyword(ReadOnlySpan<char> body, string keyword)
    {
        if (!body.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
        if (body.Length == keyword.Length) return true;

        var next = body[keyword.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }
}
