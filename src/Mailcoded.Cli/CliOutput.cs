using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace Mailcoded.Cli;

/// <summary>
/// The output contract. Every JSON document opens with <c>schema_version</c>; every error goes to
/// stderr with an <c>error.code</c> matching the RPC numeric table.
/// </summary>
internal sealed class CliOutput : IDisposable
{
    /// <summary>Bump only on a breaking change to the shape of a document this program prints.</summary>
    public const int SchemaVersion = 1;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        // Control characters stay \u-escaped so a body can never carry an escape sequence into a
        // terminal; the full Unicode range is otherwise emitted verbatim.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly Stream _stdout;
    private readonly Stream _stderr;
    private readonly StreamWriter _text;
    private readonly StreamWriter _errorText;
    private bool _disposed;

    public CliOutput(bool json)
    {
        Json = json;
        _stdout = Console.OpenStandardOutput();
        _stderr = Console.OpenStandardError();

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _text = new StreamWriter(_stdout, encoding) { AutoFlush = false };
        _errorText = new StreamWriter(_stderr, encoding) { AutoFlush = false };
    }

    public bool Json { get; private set; }

    /// <summary>Echoed into every document so a batch of piped invocations stays attributable.</summary>
    public string Command { get; private set; } = string.Empty;

    public void Configure(string command, bool json)
    {
        Command = command;
        Json = json;
    }

    public Utf8JsonWriter BeginJson()
    {
        _text.Flush();
        var writer = new Utf8JsonWriter(_stdout, WriterOptions);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteBoolean("ok", true);
        writer.WriteString("command", Command);
        return writer;
    }

    public void EndJson(Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteEndObject();
        writer.Flush();
        writer.Dispose();
        _stdout.Write("\n"u8);
        _stdout.Flush();
    }

    public void Line(string text)
    {
        _text.WriteLine(text);
    }

    public void Blank() => _text.WriteLine();

    public int WriteError(CliError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        _text.Flush();

        if (Json)
        {
            _errorText.Flush();
            var writer = new Utf8JsonWriter(_stderr, WriterOptions);
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteBoolean("ok", false);
            writer.WriteString("command", Command);
            writer.WriteStartObject("error");
            writer.WriteNumber("code", (int)error.Code);
            writer.WriteString("name", error.Name);
            writer.WriteNumber("exit_code", error.ExitCode);
            writer.WriteString("message", error.Message);
            if (error.RetryAfterMs is { } retry) writer.WriteNumber("retry_after_ms", retry);
            else writer.WriteNull("retry_after_ms");
            if (error.Hint is { } hint) writer.WriteString("hint", hint);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.Flush();
            writer.Dispose();
            _stderr.Write("\n"u8);
            _stderr.Flush();
        }
        else
        {
            _errorText.WriteLine($"mailcoded: {error.Name} ({(int)error.Code}): {error.Message}");
            if (error.Hint is { } hint) _errorText.WriteLine($"  hint: {hint}");
            _errorText.Flush();
        }

        return error.ExitCode;
    }

    public void WriteHelp(string text)
    {
        _text.WriteLine(text);
        _text.Flush();
    }

    public void Flush()
    {
        _text.Flush();
        _errorText.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _text.Flush();
        _errorText.Flush();
    }
}
