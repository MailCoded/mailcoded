using Mailcoded.Core.Domain.Identities;

namespace Mailcoded.Cli.Commands;

internal static class SignatureOptions
{
    public static SignatureChoice Read(CommandLine line)
    {
        var name = line.Value("signature");
        var none = line.Flag("no-signature");

        if (name is not null && none)
            throw new CliUsageException("Pass either --signature or --no-signature, not both.");

        if (none) return SignatureChoice.None;
        return name is null ? SignatureChoice.UseDefault : SignatureChoice.Named(name);
    }
}
