using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Mailcoded.Core.Providers;
using Mailcoded.Core.Store;

namespace Mailcoded.Daemon;

/// <summary>
/// Entry point and process lifecycle. stdout carries protocol frames and nothing else; every
/// diagnostic goes to stderr, and <c>Console.Out</c> is redirected there so a stray write from any
/// library cannot corrupt a frame.
/// </summary>
internal static class Program
{
    private const string DataDirEnvVar = "MAILCODED_DATA_DIR";

    private const int ExitOk = 0;
    private const int ExitUsage = 1;
    private const int ExitStartupFailed = 2;
    private const int ExitStoreUnsupported = 3;
    private const int ExitRequestFailed = 4;

    private static readonly TimeSpan HandlerDrainTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriterDrainTimeout = TimeSpan.FromSeconds(5);

    public static async Task<int> Main(string[] args)
    {
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true };

        // Anything that reaches Console.Out would land in the middle of a frame; send it to stderr.
        Console.SetOut(stderr);
        Console.SetError(stderr);

        var options = DaemonOptions.Parse(args);

        if (options.UsageError is { } usage)
        {
            stderr.WriteLine(usage);
            stderr.WriteLine("Run with --help for usage.");
            return ExitUsage;
        }

        if (options.Transcript) DaemonInfo.EnableTranscriptMode();

        switch (options.Mode)
        {
            case DaemonMode.Version:
                await WriteToStdoutAsync(DaemonInfo.Version).ConfigureAwait(false);
                return ExitOk;

            case DaemonMode.Help:
                await WriteToStdoutAsync(DaemonOptions.HelpText).ConfigureAwait(false);
                return ExitOk;
        }

        var log = new StderrLog(stderr, options.LogLevel, timestamps: !DaemonInfo.Deterministic);

        SingleInstanceLock? instance = null;
        DaemonHost? host = null;

        try
        {
            var dataDirectory = options.StorePath is null ? DataDirectoryFromEnvironment() : null;
            instance = SingleInstanceLock.Acquire(ResolveDataDirectory(options.StorePath, dataDirectory), log);
            host = DaemonHost.Create(options.StorePath, dataDirectory, log, instance.IsPrimary);
        }
        catch (StoreException ex) when (ex.Category == FailureCategory.Unsupported)
        {
            // M0 acceptance criterion: FTS5 and WAL are hard requirements, not degradable features.
            stderr.WriteLine("mailcoded cannot start:");
            stderr.WriteLine(ex.Message);
            instance?.Dispose();
            return ExitStoreUnsupported;
        }
        catch (Exception ex)
        {
            log.Exception("The daemon could not open its store.", ex);
            instance?.Dispose();
            return ExitStartupFailed;
        }

        if (instance is null || host is null)
        {
            instance?.Dispose();
            return ExitStartupFailed;
        }

        try
        {
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);

            return options.Mode == DaemonMode.OneShot
                ? await RunOneShotAsync(host, options, log).ConfigureAwait(false)
                : await RunServerAsync(host, log).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Exception("The daemon terminated abnormally.", ex);
            return ExitStartupFailed;
        }
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);
            instance.Dispose();
        }
    }

    private static async Task<int> RunServerAsync(DaemonHost host, StderrLog log)
    {
        var stdout = Console.OpenStandardOutput();
        var stdin = Console.OpenStandardInput();

        await using var channel = new RpcChannel(new FrameWriter(stdout), log);
        host.AttachWatch(channel);

        var dispatcher = new RpcDispatcher(host, log);
        var reader = new FrameReader(PipeReader.Create(stdin, new StreamPipeReaderOptions(leaveOpen: false)));
        var server = new JsonRpcServer(reader, channel, dispatcher, log);

        using var lifetime = new CancellationTokenSource();
        // Separate from `lifetime` so shutdown can stop reading stdin while requests already in
        // flight still get to answer.
        using var handlers = new CancellationTokenSource();
        using var sigterm = Register(PosixSignal.SIGTERM, lifetime, log);
        using var sigint = Register(PosixSignal.SIGINT, lifetime, log);

        var watchdog = ParentWatchdog.Create(log);
        if (watchdog is not null) log.Debug($"Orphan watchdog is tracking pid {watchdog.ParentPid}.");

        var reading = Guarded(server.RunAsync(lifetime.Token, handlers.Token), log, "The stdio read loop failed.");
        var watching = Guarded(
            watchdog?.RunAsync(lifetime.Token) ?? Task.Delay(Timeout.Infinite, lifetime.Token),
            log,
            "The orphan watchdog failed.");

        log.Info($"mailcoded-daemon {DaemonInfo.Version} ready (protocol {Mailcoded.Protocol.ProtocolConstants.Version}).");

        await Task.WhenAny(reading, watching, server.ShutdownRequested).ConfigureAwait(false);

        await lifetime.CancelAsync().ConfigureAwait(false);
        await server.DrainAsync(HandlerDrainTimeout, CancellationToken.None).ConfigureAwait(false);
        await handlers.CancelAsync().ConfigureAwait(false);
        await channel.DrainAsync(WriterDrainTimeout, CancellationToken.None).ConfigureAwait(false);

        // A console read parked in the OS is not always cancellable, so the loop is given a bounded
        // grace period and then abandoned; returning from Main tears the process down either way.
        try
        {
            await reading.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log.Debug("The stdio read loop was still parked on stdin at shutdown.");
        }

        log.Info("Shutdown complete.");
        return ExitOk;
    }

    private static async Task<int> RunOneShotAsync(DaemonHost host, DaemonOptions options, StderrLog log)
    {
        // No stream to delimit, so a one-shot answer is a bare JSON line rather than a framed message.
        await using var sink = new RpcChannel(new FrameWriter(Stream.Null, framed: false), log);
        host.AttachWatch(sink);

        var dispatcher = new RpcDispatcher(host, log);
        var method = options.Method ?? string.Empty;

        using var id = JsonDocument.Parse("1");
        JsonDocument? parameters = null;
        byte[] payload;
        var failed = false;

        try
        {
            if (!string.IsNullOrWhiteSpace(options.Params)) parameters = JsonDocument.Parse(options.Params);

            var result = await dispatcher
                .DispatchAsync(method, parameters?.RootElement, CancellationToken.None)
                .ConfigureAwait(false);

            payload = RpcPayloads.Result(id.RootElement, result);
        }
        catch (Exception ex)
        {
            payload = RpcPayloads.Error(id.RootElement, RpcErrorMapper.Map(ex, log));
            failed = true;
        }
        finally
        {
            parameters?.Dispose();
        }

        await new FrameWriter(Console.OpenStandardOutput(), framed: false)
            .WriteAsync(payload, CancellationToken.None)
            .ConfigureAwait(false);

        return failed ? ExitRequestFailed : ExitOk;
    }

    /// <summary>The lock file lives beside the store, so the directory has to be known before it opens.</summary>
    /// <summary>The CLI honours MAILCODED_DATA_DIR; a daemon that ignored it would open a
    /// different store than the CLI that configured the account.</summary>
    private static string? DataDirectoryFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(DataDirEnvVar);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string ResolveDataDirectory(string? storePath, string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(storePath))
        {
            var fallback = dataDirectory ?? StorePaths.DefaultDataDirectory();
            Directory.CreateDirectory(fallback);
            return fallback;
        }

        var full = Path.GetFullPath(storePath);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory)) directory = dataDirectory ?? StorePaths.DefaultDataDirectory();

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static PosixSignalRegistration? Register(PosixSignal signal, CancellationTokenSource lifetime, StderrLog log)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                log.Info($"Received {signal}; shutting down.");
                lifetime.Cancel();
            });
        }
        catch (Exception ex) when (ex is PlatformNotSupportedException or ArgumentException)
        {
            log.Debug($"{signal} is not available on this platform.");
            return null;
        }
    }

    /// <summary>Keeps a background task from ending as an unobserved fault.</summary>
    private static async Task Guarded(Task task, StderrLog log, string message)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
        catch (Exception ex)
        {
            log.Exception(message, ex);
        }
    }

    private static async Task WriteToStdoutAsync(string text)
    {
        await using var stdout = Console.OpenStandardOutput();
        var bytes = Encoding.UTF8.GetBytes(text + Environment.NewLine);
        await stdout.WriteAsync(bytes).ConfigureAwait(false);
        await stdout.FlushAsync().ConfigureAwait(false);
    }
}
