using Mailcoded.Core.Domain.Signals;
using Mailcoded.Core.Parsing;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Core.Application;

public static class ParsedSignals
{
    public static SignalValues ValuesOf(ParsedMessage parsed, AccountConfig? account)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        return new SignalValues(
            parsed.Automated,
            parsed.Encrypted,
            SenderAuthentication.Dmarc(parsed.SignalHeaders, parsed.FromHeader, account?.EffectiveAuthservTrust() ?? AuthservTrust.None),
            UnsubscribeStorage.Encode(parsed.SignalHeaders));
    }

    /// <summary>A file the user supplied carries no proof that a trusted server wrote its Authentication-Results, so it is never verified.</summary>
    public static SignalValues ImportedValuesOf(ParsedMessage parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        return new SignalValues(parsed.Automated, parsed.Encrypted, DmarcResult.Imported, UnsubscribeStorage.Encode(parsed.SignalHeaders));
    }
}
