using System.Reflection;
using Mailcoded.Core.Application;
using Mailcoded.Core.Tests.Backup;
using Mailcoded.Core.Tests.Support;
using Mailcoded.Core.Tests.Surface;
using Mailcoded.Mcp;
using Xunit;

namespace Mailcoded.Core.Tests.Safety;

public sealed class BackupGateTests
{
    private static readonly AgentPolicyOptions Unlocked = new() { BackupEnabled = true };

    [Theory]
    [InlineData(CallerKind.Rpc, false, true)]
    [InlineData(CallerKind.Rpc, true, true)]
    [InlineData(CallerKind.Internal, false, true)]
    [InlineData(CallerKind.Cli, false, false)]
    [InlineData(CallerKind.Cli, true, true)]
    [InlineData(CallerKind.Mcp, false, false)]
    [InlineData(CallerKind.Mcp, true, false)]
    public void The_posture_matrix_for_backup(CallerKind caller, bool unlocked, bool expected)
    {
        var policy = new AgentPolicy(new AgentPolicyOptions { BackupEnabled = unlocked }, new TestClock());

        Assert.Equal(expected, policy.IsEnabled(AgentCapability.Backup, caller));
        Assert.Equal(expected, policy.AllowsBackup(caller));
    }

    [Fact]
    public void The_unlock_is_read_from_the_environment_and_defaults_to_off()
    {
        using (EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, null)))
            Assert.False(AgentPolicyOptions.FromEnvironment().BackupEnabled);

        foreach (var truthy in new[] { "1", "true", "TRUE", "yes", " 1 " })
        {
            using var scope = EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, truthy));
            Assert.True(AgentPolicyOptions.FromEnvironment().BackupEnabled, truthy);
        }

        foreach (var falsy in new[] { "0", "false", "no", "", "  ", "2", "on" })
        {
            using var scope = EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, falsy));
            Assert.False(AgentPolicyOptions.FromEnvironment().BackupEnabled, falsy);
        }

        Assert.Equal("MAILCODED_ALLOW_BACKUP", AgentPolicyOptions.AllowBackupEnvVar);
    }

    [Fact]
    public async Task A_cli_caller_without_the_unlock_is_refused_audited_and_nothing_is_written()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kit = BackupKit.Create();
        await kit.SeedAsync(ct);
        var agent = CallerContext.For(CallerKind.Cli, "xunit");

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(() => kit.Service.CreateAsync(agent, kit.Dest(), ct));

        Assert.Equal(PolicyDenialReason.BackupDisabled, denial.Reason);
        Assert.Contains(AgentPolicyOptions.AllowBackupEnvVar, denial.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(kit.Dest()));

        var row = Assert.Single(kit.Store.ReadSyncLog(limit: 100, ct: ct).Items, static r => r.Event == AuditEvents.BackupDenied);
        Assert.Equal("cli", row.Interface);
        Assert.Equal("xunit", row.AgentHost);
        Assert.Equal("warn", row.Level);
        Assert.DoesNotContain(kit.Store.ReadSyncLog(limit: 100, ct: ct).Items, static r => r.Event == AuditEvents.BackupStarted);
    }

    [Fact]
    public async Task Verification_is_gated_the_same_way_so_an_agent_cannot_probe_arbitrary_folders()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kit = BackupKit.Create();
        var human = await MakeBackupAsync(kit, ct);
        var agent = CallerContext.For(CallerKind.Cli);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(() => kit.Service.VerifyAsync(agent, human, ct));

        Assert.Equal(PolicyDenialReason.BackupDisabled, denial.Reason);
        Assert.Contains(kit.Store.ReadSyncLog(limit: 100, ct: ct).Items, static r => r.Event == AuditEvents.BackupDenied);
    }

    [Fact]
    public async Task A_cli_caller_with_the_unlock_may_back_up_and_the_rows_say_cli()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kit = BackupKit.Create(Unlocked);
        await kit.SeedAsync(ct);
        var agent = CallerContext.For(CallerKind.Cli, "xunit");

        var result = await kit.Service.CreateAsync(agent, kit.Dest(), ct);
        await kit.Service.VerifyAsync(agent, result.Directory, ct);

        var rows = kit.Store.ReadSyncLog(limit: 100, ct: ct).Items.Where(static r => r.Event.StartsWith("backup_", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, static r => Assert.Equal("cli", r.Interface));
    }

    [Fact]
    public async Task An_mcp_caller_is_refused_even_with_the_unlock_set()
    {
        var ct = TestContext.Current.CancellationToken;
        using var kit = BackupKit.Create(Unlocked);

        var denial = await Assert.ThrowsAsync<PolicyDeniedException>(
            () => kit.Service.CreateAsync(CallerContext.For(CallerKind.Mcp), kit.Dest(), ct));

        Assert.Equal(PolicyDenialReason.BackupDisabled, denial.Reason);
        Assert.False(Directory.Exists(kit.Dest()));
    }

    [Fact]
    public async Task The_mcp_catalog_has_no_backup_tool_whatever_the_environment_says()
    {
        using var workspace = TempWorkspace.Create("mcp-backup");
        using var scope = EnvironmentScope.Set((AgentPolicyOptions.AllowBackupEnvVar, "1"));
        await using var host = McpHost.Create(workspace.DatabasePath);

        var tools = ToolCatalog.Build(new MailTools(host)).ToArray();

        Assert.DoesNotContain(tools, static t => Mentions(t.ProtocolTool.Name));
        Assert.DoesNotContain(tools, static t => Mentions(t.ProtocolTool.Description ?? string.Empty));
    }

    [Fact]
    public void The_mcp_adapter_cannot_reach_the_backup_service_at_all()
    {
        var mcp = typeof(McpHost).Assembly;
        var service = typeof(BackupService);

        foreach (var type in mcp.GetTypes())
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var field in type.GetFields(all)) Assert.NotEqual(service, field.FieldType);
            foreach (var property in type.GetProperties(all)) Assert.NotEqual(service, property.PropertyType);
            foreach (var method in type.GetMethods(all))
            {
                Assert.NotEqual(service, method.ReturnType);
                foreach (var parameter in method.GetParameters()) Assert.NotEqual(service, parameter.ParameterType);
            }
        }
    }

    [Fact]
    public void No_capability_or_method_name_in_the_surface_carries_a_removal_verb_because_of_backup()
    {
        foreach (var name in Enum.GetNames<AgentCapability>().Append(Mailcoded.Protocol.RpcMethods.StoreBackup).Append(Mailcoded.Protocol.RpcMethods.StoreBackupVerify))
        {
            foreach (var word in new[] { "delete", "expunge", "trash", "purge", "remove" })
                Assert.DoesNotContain(word, name, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool Mentions(string text) => text.Contains("backup", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> MakeBackupAsync(BackupKit kit, CancellationToken ct)
    {
        var result = await kit.Service.CreateAsync(BackupKit.Human, kit.Dest(), ct);
        return result.Directory;
    }
}
