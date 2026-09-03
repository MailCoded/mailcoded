using System.Reflection;
using NetArchTest.Rules;
using Xunit;

namespace Mailcoded.Core.Tests.Architecture;

/// <summary>ARCHITECTURE §12.1 rules 1-3, enforced (CLAUDE invariant 11: never weaken these).</summary>
public sealed class LayeringTests
{
    private const string MailKit = "MailKit";
    private const string MimeKit = "MimeKit";
    private const string Sqlite = "Microsoft.Data.Sqlite";
    private const string SqlitePcl = "SQLitePCL";

    [Fact]
    public void Domain_depends_on_nothing_outside_the_bcl()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.DomainNamespace,
            [
                MailKit,
                MimeKit,
                Sqlite,
                SqlitePcl,
                ArchitectureAssemblies.ProvidersNamespace,
                ArchitectureAssemblies.StoreNamespace,
                ArchitectureAssemblies.ParsingNamespace,
                ArchitectureAssemblies.SecretsNamespace,
                ArchitectureAssemblies.ProtocolNamespace,
                ArchitectureAssemblies.ApplicationNamespace,
            ],
            "RULE (ARCHITECTURE §12.1.1): Mailcoded.Core.Domain references the BCL and itself, nothing else. "
            + "The sync planner, the tag/flag map, the threader and the outbox aggregate must be unit-testable "
            + "with no SQLite, no sockets and no mocks. If you need a fact from a library or an adapter, pass it "
            + "in as a parameter or a Domain record instead of reaching for it.");
    }

    [Fact]
    public void Application_never_references_a_mailkit_mimekit_or_sqlite_type()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.ApplicationNamespace,
            [MailKit, MimeKit, Sqlite, SqlitePcl],
            "RULE (ARCHITECTURE §12.1.2): Application orchestrates through ports (IMailProvider, ISecretStore, "
            + "IThreader, IClock) and the SqliteStore facade. A MimeMessage, an ImapClient or a SqliteConnection "
            + "in Application means an adapter concern leaked upwards: push it back into Providers, Parsing or Store "
            + "and hand Application a Domain type.");
    }

    [Fact]
    public void Parsing_references_no_other_adapter()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.ParsingNamespace,
            [
                ArchitectureAssemblies.ProvidersNamespace,
                ArchitectureAssemblies.StoreNamespace,
                ArchitectureAssemblies.SecretsNamespace,
                ArchitectureAssemblies.ApplicationNamespace,
                Sqlite,
                MailKit,
            ],
            "RULE (ARCHITECTURE §12.1.3): adapters talk to Domain, never to each other. Parsing owns MimeKit and "
            + "turns raw bytes into Domain records; it must not know that a database, a socket or a keychain exists.");
    }

    [Fact]
    public void Secrets_references_no_other_adapter()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.SecretsNamespace,
            [
                ArchitectureAssemblies.ProvidersNamespace,
                ArchitectureAssemblies.StoreNamespace,
                ArchitectureAssemblies.ParsingNamespace,
                ArchitectureAssemblies.ApplicationNamespace,
                Sqlite,
                MailKit,
                MimeKit,
            ],
            "RULE (ARCHITECTURE §12.1.3 + SPEC invariant 3): Secrets is a leaf. A credential must never be able to "
            + "reach the database, a log or an RPC response, and the cheapest way to guarantee that is for the secret "
            + "stores to have no way of naming those things.");
    }

    [Fact]
    public void Providers_references_no_other_adapter()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.ProvidersNamespace,
            [
                ArchitectureAssemblies.StoreNamespace,
                ArchitectureAssemblies.ParsingNamespace,
                ArchitectureAssemblies.ApplicationNamespace,
                Sqlite,
                SqlitePcl,
            ],
            "RULE (ARCHITECTURE §12.1.3): Providers owns MailKit and emits Domain SyncEvents. It must not persist "
            + "anything itself, and it must not parse MIME on Parsing's behalf. Sequencing a fetch against a write "
            + "is the Application layer's job.");
    }

    [Fact]
    public void Providers_depends_on_the_secret_port_only_never_on_a_concrete_secret_store()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.ProvidersNamespace,
            [
                "Mailcoded.Core.Secrets.ChainedSecretStore",
                "Mailcoded.Core.Secrets.EncryptedFileStore",
                "Mailcoded.Core.Secrets.LibSecretStore",
                "Mailcoded.Core.Secrets.MacKeychainStore",
                "Mailcoded.Core.Secrets.WindowsCredentialManagerStore",
                "Mailcoded.Core.Secrets.SecretStoreFactory",
            ],
            "RULE (ARCHITECTURE §12.1.3-4): Providers is allowed exactly one name from Secrets — the ISecretStore "
            + "port — because IMAP and SMTP need a password at connect time. Naming a concrete store here would make "
            + "the provider pick a backend, which is the composition root's decision.");
    }

    [Fact]
    public void Store_references_no_other_adapter_and_no_mail_library()
    {
        AssertNoDependency(
            ArchitectureAssemblies.Core,
            ArchitectureAssemblies.StoreNamespace,
            [
                ArchitectureAssemblies.ParsingNamespace,
                ArchitectureAssemblies.SecretsNamespace,
                ArchitectureAssemblies.ApplicationNamespace,
                MailKit,
                MimeKit,
                "Mailcoded.Core.Providers.ImapProvider",
                "Mailcoded.Core.Providers.SmtpSender",
                "Mailcoded.Core.Providers.IMailProvider",
                "Mailcoded.Core.Providers.IMailSender",
                "Mailcoded.Core.Providers.ImapCommandQueue",
                "Mailcoded.Core.Providers.ImapConnection",
                "Mailcoded.Core.Providers.ReconnectPolicy",
            ],
            "RULE (ARCHITECTURE §12.1.3): Store owns SQL and nothing else. Its one permitted link to Providers is the "
            + "shared account-configuration vocabulary (AccountConfig, ProviderKind, SecureSocket, StoreException) — "
            + "plain records with no MailKit inside. A live connection, a sender or the reconnect policy in Store "
            + "means the writer thread has started doing network work.");
    }

    [Fact]
    public void The_namespaces_the_rules_quote_all_exist()
    {
        string[] inCore =
        [
            ArchitectureAssemblies.DomainNamespace,
            ArchitectureAssemblies.ApplicationNamespace,
            ArchitectureAssemblies.ProvidersNamespace,
            ArchitectureAssemblies.StoreNamespace,
            ArchitectureAssemblies.ParsingNamespace,
            ArchitectureAssemblies.SecretsNamespace,
        ];

        foreach (var ns in inCore)
        {
            Assert.True(
                TypesIn(ArchitectureAssemblies.Core, ns).Any(),
                $"No type resides in '{ns}'. A renamed namespace turns every rule quoting it into a rule that "
                + "silently passes over an empty set, so the rename must update this list too.");
        }

        Assert.True(
            TypesIn(ArchitectureAssemblies.Protocol, ArchitectureAssemblies.ProtocolNamespace).Any(),
            $"No type resides in '{ArchitectureAssemblies.ProtocolNamespace}'. The wire DTOs are harvested "
            + "into AgentSurfaceTypes() from this assembly, so an empty set would silently stop the "
            + "removal-verb rules covering them.");
    }

    [Fact]
    public void Domain_folders_named_pure_by_claude_invariant_13_hold_no_io_types()
    {
        string[] pure =
        [
            "Mailcoded.Core.Domain.Sync",
            "Mailcoded.Core.Domain.Tags",
            "Mailcoded.Core.Domain.Threading",
        ];

        foreach (var ns in pure)
        {
            AssertNoDependency(
                ArchitectureAssemblies.Core,
                ns,
                ["System.IO", "System.Net", "System.Data", "System.Diagnostics.Stopwatch"],
                $"RULE (CLAUDE invariant 13): '{ns}' is the pure sync core. No I/O, no ambient clock, no logging. "
                + "A new edge case extends SyncPlanner/Apply plus a table-driven row — it never becomes a branch "
                + "that reads a file, opens a socket or asks the machine what time it is.");
        }
    }

    private static IEnumerable<Type> TypesIn(Assembly assembly, string ns) =>
        Types.InAssembly(assembly).That().ResideInNamespace(ns).GetTypes();

    private static void AssertNoDependency(Assembly assembly, string ns, string[] forbidden, string rule)
    {
        Assert.True(
            TypesIn(assembly, ns).Any(),
            $"No types found in '{ns}'; this rule would pass vacuously. {rule}");

        var result = Types.InAssembly(assembly)
            .That().ResideInNamespace(ns)
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, $"{rule}{Environment.NewLine}Offending types: {Offenders(result)}");
    }

    private static string Offenders(NetArchTest.Rules.TestResult result)
    {
        var failing = result.FailingTypes ?? Enumerable.Empty<Type>();
        var names = failing.Select(t => t.FullName ?? t.Name).ToArray();
        return names.Length == 0 ? "(none reported)" : string.Join(", ", names);
    }
}
