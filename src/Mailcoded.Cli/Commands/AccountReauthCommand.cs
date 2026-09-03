using System.Globalization;
using Mailcoded.Core.Auth;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>
/// Replaces the stored credential for an existing account: a fresh Microsoft sign-in for an OAuth
/// account, or a new password for a password account. Settings and cached mail are untouched.
/// </summary>
internal static class AccountReauthCommand
{
    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        line.RejectExtraPositional(0);
        var account = host.RequireAccount(line, ct);

        if (!Prompt.IsInteractive)
        {
            throw new CliUsageException(
                "'account reauth' needs a terminal: it either prompts for a password or runs a "
                + "browser sign-in. For scripts, write the credential with 'mailcoded account add "
                + "--password-stdin' using the same --email.");
        }

        return account.Auth == AuthKind.OAuth2
            ? await ReauthOAuthAsync(host, line, output, account, ct).ConfigureAwait(false)
            : await ReauthPasswordAsync(host, output, account, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReauthOAuthAsync(
        CliHost host,
        CommandLine line,
        CliOutput output,
        AccountConfig account,
        CancellationToken ct)
    {
        var options = OAuthOptions.FromEnvironment();
        if (line.Value("client-id") is { Length: > 0 } id) options = options with { ClientId = id };
        if (line.Value("tenant") is { Length: > 0 } tenant) options = options with { Tenant = tenant };

        Prompt.Heading($"Sign in again: {account.Email}");

        var auth = new MicrosoftOAuth(
            options,
            host.Secrets,
            MicrosoftScopeSet.MailProtocols,
            (prompt, _) =>
            {
                Prompt.Say($"  Open:  {prompt.VerificationUrl}");
                Prompt.Say($"  Code:  {prompt.UserCode}");
                Prompt.Say();
                Prompt.Say("Waiting for you to finish in the browser...");
                return Task.CompletedTask;
            });

        try
        {
            await auth.SignInAsync(account.SecretRef, ct).ConfigureAwait(false);
        }
        catch (ReauthorizationRequiredException ex)
        {
            output.Line(ex.Message);
            return ExitCodes.Auth;
        }

        Prompt.Say("Signed in.");
        return await VerifyAsync(host, output, account, auth, ct).ConfigureAwait(false);
    }

    private static async Task<int> ReauthPasswordAsync(
        CliHost host,
        CliOutput output,
        AccountConfig account,
        CancellationToken ct)
    {
        Prompt.Heading($"New credential: {account.Email}");
        Prompt.Say("Not echoed, and never a command-line argument.");

        var secret = Prompt.AskSecret("Password");
        if (secret.Length == 0) throw new CliUsageException("An empty credential cannot be verified.");

        await host.Accounts.SetSecretAsync(account.SecretRef, secret, ct).ConfigureAwait(false);
        return await VerifyAsync(host, output, account, null, ct).ConfigureAwait(false);
    }

    private static async Task<int> VerifyAsync(
        CliHost host,
        CliOutput output,
        AccountConfig account,
        IAccessTokenSource? tokens,
        CancellationToken ct)
    {
        Prompt.Heading("Verifying");
        try
        {
            await using var provider = new ImapProvider(host.Clock, MailTransportOptions.Default, tokens);
            await provider.ConnectAsync(account, host.Secrets, ct).ConfigureAwait(false);
            await provider.ListFoldersAsync(ct).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            output.Line($"The new credential was stored but the server still refuses it: {ex.Message}");
            return ex.Category == FailureCategory.Auth ? ExitCodes.Auth : ExitCodes.Network;
        }

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            writer.WriteBoolean("verified", true);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"Credential replaced and verified for account "
            + account.Id.Value.ToString(CultureInfo.InvariantCulture) + ".");
        return ExitCodes.Ok;
    }
}
