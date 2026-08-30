using System.Globalization;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

namespace Mailcoded.Integration.Support;

/// <summary>A Dovecot IMAP server in a container, configured from a generated self-contained conf.</summary>
public sealed class DovecotServer : IAsyncDisposable
{
    public const string ImageEnvVar = "MAILCODED_IT_DOVECOT_IMAGE";
    public const string DefaultImage = "dovecot/dovecot:2.3.21";

    private const int ImapPort = 143;
    private const string ConfigPath = "/etc/dovecot/dovecot.conf";
    private const string UsersPath = "/etc/dovecot/users";
    private const string MailRoot = "/tmp/vmail";

    private static readonly UnixFileModes ReadableFile =
        UnixFileModes.UserRead | UnixFileModes.UserWrite | UnixFileModes.GroupRead | UnixFileModes.OtherRead;

    private readonly IContainer _container;
    private readonly TestCredentials _credentials;

    private DovecotServer(IContainer container, TestCredentials credentials)
    {
        _container = container;
        _credentials = credentials;
    }

    public string Host => _container.Hostname;

    public int Port => _container.GetMappedPublicPort(ImapPort);

    public string Mailbox => _credentials.Mailbox;

    public static async Task<DovecotServer> StartAsync(TestCredentials credentials, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        var container = new ContainerBuilder(Environment.GetEnvironmentVariable(ImageEnvVar) ?? DefaultImage)
            .WithPortBinding(ImapPort, true)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BuildConfig()), ConfigPath, 0, 0, ReadableFile)
            .WithResourceMapping(Encoding.UTF8.GetBytes(BuildUsers(credentials)), UsersPath, 0, 0, ReadableFile)
            .Build();

        await container.StartAsync(ct).ConfigureAwait(false);

        var server = new DovecotServer(container, credentials);
        await PortProbe.WaitForOpenAsync(server.Host, server.Port, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return server;
    }

    /// <summary>Drops the maildir UID bookkeeping; the pause is because Dovecot derives the
    /// replacement UIDVALIDITY from wall-clock seconds.</summary>
    public async Task ForceUidValidityChangeAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1_200), ct).ConfigureAwait(false);

        var home = HomeDirectory();
        var script =
            $"pkill -f 'dovecot/imap' || true; "
            + $"find {home} -name 'dovecot-uidlist' -delete || true; "
            + $"find {home} -name 'dovecot.index*' -delete || true; "
            + $"find {home} -name 'dovecot.list.index*' -delete || true; "
            + $"find {home} -name 'dovecot-uidvalidity*' -delete || true";

        await ExecAsync(["sh", "-c", script], ct).ConfigureAwait(false);
    }

    public async Task<string> ExecAsync(IList<string> command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);

        var result = await _container.ExecAsync(command, ct).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var exitCode = result.ExitCode ?? -1;
            throw new InvalidOperationException(
                $"Command exited {exitCode.ToString(CultureInfo.InvariantCulture)}: {_credentials.Redact(result.Stderr)}");
        }

        return result.Stdout;
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
    }

    private string HomeDirectory() => MailRoot + "/" + _credentials.Mailbox;

    private static string BuildUsers(TestCredentials credentials) =>
        credentials.Mailbox + ":{PLAIN}" + credentials.ImapPassword + "\n";

    private static string BuildConfig() =>
        $$"""
        protocols = imap
        listen = *
        ssl = no
        disable_plaintext_auth = no
        auth_mechanisms = plain login
        auth_verbose = yes
        log_path = /dev/stderr
        info_log_path = /dev/stderr

        first_valid_uid = 1000
        mail_home = {{MailRoot}}/%u
        mail_location = maildir:~/Maildir

        namespace inbox {
          inbox = yes
          separator = /
          mailbox Sent {
            special_use = \Sent
            auto = subscribe
          }
          mailbox Drafts {
            special_use = \Drafts
            auto = subscribe
          }
          mailbox Trash {
            special_use = \Trash
            auto = subscribe
          }
          mailbox Archive {
            special_use = \Archive
            auto = subscribe
          }
        }

        passdb {
          driver = passwd-file
          args = scheme=PLAIN username_format=%u {{UsersPath}}
        }

        userdb {
          driver = static
          args = uid=1000 gid=1000 home={{MailRoot}}/%u
        }

        service imap-login {
          inet_listener imap {
            port = {{ImapPort.ToString(CultureInfo.InvariantCulture)}}
          }
          inet_listener imaps {
            port = 0
          }
        }

        protocol imap {
          mail_max_userip_connections = 20
        }
        """;
}
