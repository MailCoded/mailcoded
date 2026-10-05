using System.Globalization;
using System.Text;
using Mailcoded.Core.Domain.Identities;
using Mailcoded.Core.Providers;

namespace Mailcoded.Cli.Commands;

/// <summary>Named plaintext signatures per Account, and which one a new message and a reply start with.</summary>
internal static class AccountSignatureCommand
{
    private const int MaxInputBytes = 64 * 1024;

    public static async Task<int> RunAsync(CliHost host, CommandLine line, CliOutput output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(output);

        var action = line.RequirePositional(0, "an action: list, set, forget or default");
        var account = host.RequireAccount(line, ct);
        var changed = false;

        switch (action)
        {
            case "list":
                line.RejectExtraPositional(1);
                break;

            case "set":
            {
                line.RejectExtraPositional(2);
                var name = line.RequirePositional(1, "a signature name");
                var text = await ReadTextAsync(line, ct).ConfigureAwait(false);
                changed = await host.Accounts.SetSignatureAsync(account.Id, name, text, host.Caller, ct).ConfigureAwait(false);
                break;
            }

            case "forget":
                line.RejectExtraPositional(2);
                changed = await host.Accounts
                    .ForgetSignatureAsync(account.Id, line.RequirePositional(1, "a signature name"), host.Caller, ct)
                    .ConfigureAwait(false);
                break;

            case "default":
            {
                line.RejectExtraPositional(3);
                var use = line.RequirePositional(1, "'new' or 'reply'") switch
                {
                    "new" => SignatureUse.New,
                    "reply" => SignatureUse.Reply,
                    _ => throw new CliUsageException("The default is for 'new' or for 'reply'."),
                };

                string? name = null;
                if (line.Flag("none"))
                {
                    line.RejectExtraPositional(2);
                }
                else
                {
                    name = line.RequirePositional(2, "a signature name, or --none");
                }

                changed = await host.Accounts.SetDefaultSignatureAsync(account.Id, use, name, host.Caller, ct).ConfigureAwait(false);
                break;
            }

            default:
                throw new CliUsageException($"'account signature {SafeText.Line(action, 40)}' does not exist. Actions: list, set, forget, default.");
        }

        account = host.Store.GetAccount(account.Id, ct) ?? account;
        return Write(output, account, action, changed);
    }

    private static async Task<string> ReadTextAsync(CommandLine line, CancellationToken ct)
    {
        var path = line.Value("file");
        var stdin = line.Flag("stdin");

        if (path is not null && stdin) throw new CliUsageException("Pass either --file or --stdin, not both.");

        if (path is not null)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException($"No signature file at '{path}'.", path);
            if (info.Length > MaxInputBytes)
                throw new CliUsageException($"--file is {info.Length} bytes; a signature is at most {SignatureText.MaxChars} characters.");

            return await File.ReadAllTextAsync(path, Encoding.UTF8, ct).ConfigureAwait(false);
        }

        if (!stdin) throw new CliUsageException("A signature needs its text: pass --file <path> or --stdin.");

        using var reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8);
        var buffer = new char[MaxInputBytes + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
        if (read > MaxInputBytes)
            throw new CliUsageException($"The signature read from stdin is far longer than {SignatureText.MaxChars} characters.");

        return new string(buffer, 0, read);
    }

    private static int Write(CliOutput output, AccountConfig account, string action, bool changed)
    {
        var book = account.Signatures;

        if (output.Json)
        {
            var writer = output.BeginJson();
            writer.WriteNumber("account_id", account.Id.Value);
            writer.WriteString("email", account.Email);
            JsonFields.WriteText(writer, "default_new", book.DefaultForNew);
            JsonFields.WriteText(writer, "default_reply", book.DefaultForReply);
            writer.WriteStartArray("signatures");
            foreach (var signature in book.Items)
            {
                writer.WriteStartObject();
                writer.WriteString("name", signature.Name);
                writer.WriteString("text", signature.Text);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("changed", changed);
            output.EndJson(writer);
            return ExitCodes.Ok;
        }

        output.Line($"account {account.Id.Value.ToString(CultureInfo.InvariantCulture)}  {account.Email}");
        output.Line($"  new messages start with    {book.DefaultForNew ?? "(no signature)"}");
        output.Line($"  replies start with         {book.DefaultForReply ?? "(no signature)"}");

        foreach (var signature in book.Items)
        {
            output.Blank();
            output.Line($"  {signature.Name}");
            foreach (var text in signature.Text.Split('\n')) output.Line("    " + text);
        }

        if (action != "list")
        {
            output.Blank();
            output.Line(changed ? "Saved." : "Nothing changed.");
        }
        else if (book.IsEmpty)
        {
            output.Blank();
            output.Line("No signatures. Add one with: mailcoded account signature set <name> --file <path>");
        }

        return ExitCodes.Ok;
    }
}
