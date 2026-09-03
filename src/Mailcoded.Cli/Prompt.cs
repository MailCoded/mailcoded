using System.Text;

namespace Mailcoded.Cli;

/// <summary>
/// Interactive prompts for the onboarding wizard. Everything is written to STDERR so that stdout
/// stays a clean data channel — piping <c>mailcoded setup --json</c> must not interleave prompts
/// with the result.
/// </summary>
internal static class Prompt
{
    /// <summary>
    /// True when there is a human to talk to. A wizard that blocks on a closed stdin in a script
    /// is worse than one that refuses to start, so every caller checks this first.
    /// </summary>
    public static bool IsInteractive => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    public static void Say(string text = "") => Console.Error.WriteLine(text);

    public static void Heading(string text)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine(text);
        Console.Error.WriteLine(new string('-', text.Length));
    }

    /// <summary>Reads a line. Returns <paramref name="fallback"/> when the human just hits enter.</summary>
    public static string Ask(string label, string? fallback = null)
    {
        while (true)
        {
            Console.Error.Write(fallback is null ? $"{label}: " : $"{label} [{fallback}]: ");
            var answer = Console.ReadLine();

            if (answer is null) throw new CliUsageException("Input ended before setup finished.");

            answer = answer.Trim();
            if (answer.Length > 0) return answer;
            if (fallback is not null) return fallback;

            Console.Error.WriteLine("  This one is required.");
        }
    }

    public static bool Confirm(string label, bool fallback)
    {
        var hint = fallback ? "Y/n" : "y/N";
        while (true)
        {
            Console.Error.Write($"{label} [{hint}]: ");
            var answer = Console.ReadLine();
            if (answer is null) throw new CliUsageException("Input ended before setup finished.");

            answer = answer.Trim();
            if (answer.Length == 0) return fallback;
            if (answer is "y" or "Y" or "yes" or "Yes" or "YES") return true;
            if (answer is "n" or "N" or "no" or "No" or "NO") return false;

            Console.Error.WriteLine("  Answer y or n.");
        }
    }

    /// <summary>Presents a numbered menu and returns the chosen index.</summary>
    public static int Choose(string label, IReadOnlyList<string> options, int fallback = 0)
    {
        Console.Error.WriteLine(label);
        for (var i = 0; i < options.Count; i++)
            Console.Error.WriteLine($"  {i + 1}. {options[i]}");

        while (true)
        {
            Console.Error.Write($"Choice [{fallback + 1}]: ");
            var answer = Console.ReadLine();
            if (answer is null) throw new CliUsageException("Input ended before setup finished.");

            answer = answer.Trim();
            if (answer.Length == 0) return fallback;
            if (int.TryParse(answer, out var picked) && picked >= 1 && picked <= options.Count)
                return picked - 1;

            Console.Error.WriteLine($"  Enter a number from 1 to {options.Count}.");
        }
    }

    /// <summary>
    /// Reads a credential without echoing it. The characters never reach the terminal, the
    /// scrollback, or the shell history — which is the whole reason a credential is not an argument.
    /// </summary>
    public static string AskSecret(string label)
    {
        Console.Error.Write($"{label}: ");
        var buffer = new StringBuilder();

        try
        {
            while (true)
            {
                var key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter) break;

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0) buffer.Length--;
                    continue;
                }

                // Ctrl+C arrives here as a key rather than a signal because of intercept:true.
                if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    throw new OperationCanceledException();

                if (!char.IsControl(key.KeyChar)) buffer.Append(key.KeyChar);
            }
        }
        catch (InvalidOperationException)
        {
            // No console to read keys from; the caller should not have prompted.
            throw new CliUsageException(
                "A credential cannot be read here because there is no terminal. Use "
                + "'mailcoded account add --password-stdin' and pipe it in instead.");
        }

        Console.Error.WriteLine();
        return buffer.ToString();
    }
}
