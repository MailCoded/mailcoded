using Mailcoded.Protocol;

namespace Mailcoded.Tui;

/// <summary>Lays the account's default signature into a compose buffer, where the user can see and edit it.</summary>
internal static class SignatureBlock
{
    private const string Separator = "-- ";

    public static string? DefaultText(AccountDto? account, bool reply)
    {
        if (account is null) return null;

        var name = reply ? account.DefaultSignatureReply : account.DefaultSignatureNew;
        if (name is null) return null;

        foreach (var signature in account.Signatures)
            if (string.Equals(signature.Name, name, StringComparison.Ordinal)) return signature.Text;

        return null;
    }

    /// <summary>Two blank lines for the new text, then the separator and the signature, then the quote when there is one.</summary>
    public static string Body(string? signature, string? quote)
    {
        if (signature is null) return quote ?? string.Empty;

        var body = "\n\n" + Separator + "\n" + signature;
        return string.IsNullOrEmpty(quote) ? body : body + "\n\n" + quote;
    }
}
