using System.Reflection;
using Mailcoded.Core.Domain.Sync;

namespace Mailcoded.Core.Tests.Architecture;

/// <summary>Hosts load by AssemblyName: every type in them is internal, and must stay that way.</summary>
internal static class ArchitectureAssemblies
{
    public const string DomainNamespace = "Mailcoded.Core.Domain";
    public const string ApplicationNamespace = "Mailcoded.Core.Application";
    public const string ProtocolNamespace = "Mailcoded.Protocol";
    public const string ProvidersNamespace = "Mailcoded.Core.Providers";
    public const string StoreNamespace = "Mailcoded.Core.Store";
    public const string ParsingNamespace = "Mailcoded.Core.Parsing";
    public const string SecretsNamespace = "Mailcoded.Core.Secrets";
    public const string EmbeddingNamespace = "Mailcoded.Core.Embedding";

    public const string DaemonAssemblyName = "mailcoded-daemon";
    public const string CliAssemblyName = "mailcoded";
    public const string McpAssemblyName = "mailcoded-mcp";
    public const string TuiAssemblyName = "mailcoded-tui";

    public static Assembly Core => typeof(SyncPlanner).Assembly;

    public static Assembly Protocol => typeof(Mailcoded.Protocol.RpcMethods).Assembly;

    public static Assembly? Daemon { get; } = TryLoad(DaemonAssemblyName);

    public static Assembly? Cli { get; } = TryLoad(CliAssemblyName);

    public static Assembly? Mcp { get; } = TryLoad(McpAssemblyName);

    public static Assembly? Tui { get; } = TryLoad(TuiAssemblyName);

    /// <summary>Host assemblies this test project can see. The daemon is always one of them.</summary>
    public static IReadOnlyList<Assembly> ReachableHosts
    {
        get
        {
            var hosts = new List<Assembly>(4);
            if (Daemon is { } daemon) hosts.Add(daemon);
            if (Cli is { } cli) hosts.Add(cli);
            if (Mcp is { } mcp) hosts.Add(mcp);
            if (Tui is { } tui) hosts.Add(tui);
            return hosts;
        }
    }

    /// <summary>What the CLI and MCP surfaces reach through: Application, Protocol, and the hosts.</summary>
    public static IReadOnlyList<Type> AgentSurfaceTypes()
    {
        var types = new List<Type>();

        foreach (var type in SafeTypes(Core))
        {
            var ns = type.Namespace;
            if (ns is null) continue;
            if (ns.StartsWith(ApplicationNamespace, StringComparison.Ordinal)) types.Add(type);
        }

        // Protocol moved to its own assembly; harvesting it by namespace from Core would silently
        // shrink the set these rules cover.
        foreach (var type in SafeTypes(Protocol))
        {
            var ns = type.Namespace;
            if (ns is not null && ns.StartsWith(ProtocolNamespace, StringComparison.Ordinal)) types.Add(type);
        }

        foreach (var host in ReachableHosts)
            types.AddRange(SafeTypes(host));

        return types;
    }

    public static bool IsAuthored(Type type) =>
        !type.Name.StartsWith('<') && !type.Name.Contains("__", StringComparison.Ordinal);

    public static IEnumerable<Type> SafeTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            var loaded = new List<Type>();
            foreach (var type in ex.Types)
                if (type is not null) loaded.Add(type);
            return loaded;
        }
    }

    private static Assembly? TryLoad(string simpleName)
    {
        try
        {
            return Assembly.Load(new AssemblyName(simpleName));
        }
        catch (Exception)
        {
            // Not project-referenced; probe the test output directory instead.
        }

        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, simpleName + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
