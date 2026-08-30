namespace Mailcoded.Integration.Support;

/// <summary>Trait keys CI selects on; every test carries <c>integration=docker</c>.</summary>
public static class IntegrationTraits
{
    public const string Category = "integration";
    public const string Docker = "docker";

    public const string Scenario = "scenario";
    public const string EndToEnd = "end-to-end";
    public const string UidValidity = "uidvalidity";
    public const string CrashRecovery = "crash-recovery";
    public const string Idle = "idle";
}
