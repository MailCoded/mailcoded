using System.Globalization;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Mailcoded.Integration.Support;

/// <summary>An smtp4dev sink: a real SMTP submission endpoint plus an HTTP API to inspect it.</summary>
public sealed class Smtp4DevServer : IAsyncDisposable
{
    public const string ImageEnvVar = "MAILCODED_IT_SMTP4DEV_IMAGE";
    public const string DefaultImage = "rnwood/smtp4dev:3.6.1";

    private const int SmtpPort = 25;
    private const int WebPort = 80;

    private readonly IContainer _container;
    private readonly HttpClient _http;

    private Smtp4DevServer(IContainer container, HttpClient http)
    {
        _container = container;
        _http = http;
    }

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(SmtpPort);

    public int ApiPort => _container.GetMappedPublicPort(WebPort);

    public static async Task<Smtp4DevServer> StartAsync(TestCredentials credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var container = new ContainerBuilder(Environment.GetEnvironmentVariable(ImageEnvVar) ?? DefaultImage)
            .WithPortBinding(SmtpPort, true)
            .WithPortBinding(WebPort, true)
            .WithEnvironment("ServerOptions__Urls", "http://*:80")
            .WithEnvironment("ServerOptions__HostName", "smtp4dev.mailcoded.test")
            .WithEnvironment("ServerOptions__AllowRemoteConnections", "true")
            .WithEnvironment("ServerOptions__Port", SmtpPort.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("ServerOptions__NumberOfMessagesToKeep", "500")
            .WithEnvironment("ServerOptions__NumberOfSessionsToKeep", "500")
            .WithEnvironment("ServerOptions__AuthenticationRequired", "true")
            .WithEnvironment("ServerOptions__SecureConnectionRequired", "false")
            .WithEnvironment("ServerOptions__Users__0__Username", credentials.SmtpUser)
            .WithEnvironment("ServerOptions__Users__0__Password", credentials.SmtpPassword)
            .WithEnvironment("ServerOptions__Users__0__DefaultMailbox", "Default")
            .Build();

        await container.StartAsync(ct).ConfigureAwait(false);

        var server = new Smtp4DevServer(container, new HttpClient { Timeout = TimeSpan.FromSeconds(20) });
        await PortProbe.WaitForOpenAsync(server.Host, server.Port, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        await PortProbe.WaitForOpenAsync(server.Host, server.ApiPort, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return server;
    }

    /// <summary>How many accepted messages carry this exact subject. The gate against double-send.</summary>
    public async Task<int> CountBySubjectAsync(string subject, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        var uri = new Uri($"http://{Host}:{ApiPort.ToString(CultureInfo.InvariantCulture)}/api/Messages");
        using var response = await _http.GetAsync(uri, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var count = 0;
        foreach (var element in EnumerateMessages(document.RootElement))
        {
            if (!element.TryGetProperty("subject", out var value)) continue;
            if (value.ValueKind != JsonValueKind.String) continue;
            if (string.Equals(value.GetString(), subject, StringComparison.Ordinal)) count++;
        }

        return count;
    }

    public async Task<int> WaitForSubjectAsync(string subject, int expected, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var seen = 0;

        while (Environment.TickCount64 < deadline)
        {
            seen = await CountBySubjectAsync(subject, ct).ConfigureAwait(false);
            if (seen >= expected) return seen;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return seen;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        if (_container is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The list endpoint has shipped both as a bare array and as a paged envelope.</summary>
    private static IEnumerable<JsonElement> EnumerateMessages(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object) yield break;

        foreach (var name in new[] { "results", "items", "value" })
        {
            if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in array.EnumerateArray()) yield return item;
            yield break;
        }
    }
}
