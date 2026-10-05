using System.Globalization;
using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;

namespace Mailcoded.Cli;

/// <summary>One self-overwriting progress line on stderr; stdout and every JSON document stay untouched.</summary>
internal sealed class SyncProgressLine
{
    private const int MinWidth = 20;
    private const int MaxWidth = 400;
    private const int FolderNameWidth = 40;

    private readonly SyncProgressRun run;
    private readonly TextWriter error;
    private readonly int width;
    private readonly List<SyncProgress> open = [];

    private int drawn;
    private bool everDrawn;
    private bool disabled;

    internal SyncProgressLine(IClock clock, TextWriter error, int width)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(error);

        this.error = error;
        this.width = width;
        run = new SyncProgressThrottle(clock).Begin(Show);
    }

    public IProgress<SyncProgress> Progress => run;

    public static SyncProgressLine? Start(IClock clock, bool json, bool quiet) =>
        Enabled(json, quiet, !Console.IsErrorRedirected) ? new SyncProgressLine(clock, Console.Error, TerminalWidth()) : null;

    internal static bool Enabled(bool json, bool quiet, bool stderrIsTerminal) => !json && !quiet && stderrIsTerminal;

    public void Finish()
    {
        run.Complete();

        if (!everDrawn) return;

        Write("\r" + new string(' ', drawn) + "\r");
        drawn = 0;
    }

    internal static string Format(IReadOnlyList<SyncProgress> open, int width)
    {
        ArgumentNullException.ThrowIfNull(open);

        if (open.Count == 0 || width < MinWidth) return string.Empty;

        var parts = new List<string>(open.Count);
        foreach (var progress in open)
        {
            var count = progress.Total is { } total
                ? string.Create(CultureInfo.InvariantCulture, $"{progress.Done}/{total}")
                : progress.Done.ToString(CultureInfo.InvariantCulture);

            parts.Add(progress.Phase switch
            {
                SyncPhase.Folders => $"{count} folders",
                SyncPhase.Flags => $"{SafeText.Line(progress.FolderPath, FolderNameWidth)} flags {count}",
                _ => $"{SafeText.Line(progress.FolderPath, FolderNameWidth)} {count}",
            });
        }

        var line = "sync " + string.Join(", ", parts);
        var limit = width - 1;
        if (line.Length <= limit) return line;

        var cut = limit;
        if (char.IsHighSurrogate(line[cut - 1])) cut--;
        return line[..cut];
    }

    private static int TerminalWidth()
    {
        try
        {
            var columns = Console.WindowWidth;
            return columns <= 0 ? 80 : Math.Clamp(columns, MinWidth, MaxWidth);
        }
        catch (Exception ex) when (ex is IOException or PlatformNotSupportedException)
        {
            return 80;
        }
    }

    private void Show(SyncProgress progress)
    {
        var index = open.FindIndex(o => o.FolderId == progress.FolderId && o.Phase == progress.Phase);

        if (progress.Final)
        {
            if (index >= 0) open.RemoveAt(index);
        }
        else if (index >= 0)
        {
            open[index] = progress;
        }
        else
        {
            open.Add(progress);
        }

        var line = Format(open, width);
        if (line.Length == 0) return;

        var builder = new StringBuilder(1 + Math.Max(line.Length, drawn));
        builder.Append('\r').Append(line);
        if (drawn > line.Length) builder.Append(' ', drawn - line.Length);

        Write(builder.ToString());
        drawn = line.Length;
        everDrawn = true;
    }

    private void Write(string text)
    {
        if (disabled) return;

        try
        {
            error.Write(text);
            error.Flush();
        }
        catch (IOException)
        {
            disabled = true;
        }
    }
}
