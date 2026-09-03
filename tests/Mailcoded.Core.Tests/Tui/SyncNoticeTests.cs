using System.Reflection;
using Mailcoded.Protocol;
using Mailcoded.Tui;
using Xunit;

namespace Mailcoded.Core.Tests.Tui;

/// <summary>A second client on one store is normal: only IDLE is exclusive, and saying so in red
/// on every account makes a supported arrangement look broken.</summary>
public sealed class SyncNoticeTests
{
    [Fact]
    public void A_capability_notice_is_not_reported_as_a_failure()
    {
        var state = Report(Notice(RpcErrorCode.Unsupported, requiresUserAction: false));

        Assert.False(state.StatusIsError);
        Assert.Contains("press r", state.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_same_notice_arriving_once_per_account_is_said_once()
    {
        var app = NewApp();
        var state = StateOf(app);

        Invoke(app, Notice(RpcErrorCode.Unsupported, requiresUserAction: false));
        state.Say("37 in INBOX");
        Invoke(app, Notice(RpcErrorCode.Unsupported, requiresUserAction: false));

        Assert.Equal("37 in INBOX", state.Status);
    }

    [Theory]
    [InlineData(RpcErrorCode.Auth, true)]
    [InlineData(RpcErrorCode.Network, false)]
    [InlineData(RpcErrorCode.StoreCorrupt, false)]
    public void A_real_sync_failure_is_still_reported_as_one(RpcErrorCode code, bool requiresUserAction)
    {
        var state = Report(Notice(code, requiresUserAction));

        Assert.True(state.StatusIsError);
        Assert.Contains("sync:", state.Status, StringComparison.Ordinal);
    }

    /// <summary>Unsupported plus requiresUserAction is a gate the human must open, not a notice.</summary>
    [Fact]
    public void An_unsupported_notice_that_needs_the_user_is_still_an_error()
    {
        var state = Report(Notice(RpcErrorCode.Unsupported, requiresUserAction: true));

        Assert.True(state.StatusIsError);
    }

    private static AppState Report(SyncErrorNotification error)
    {
        var app = NewApp();
        Invoke(app, error);
        return StateOf(app);
    }

    private static SyncErrorNotification Notice(RpcErrorCode code, bool requiresUserAction) => new()
    {
        AccountId = 1,
        Code = (int)code,
        Message = "the daemon said something",
        RequiresUserAction = requiresUserAction,
        AtUtc = "2026-09-04T00:00:00.000Z",
    };

    private static object NewApp() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(AppType)
        is { } app && Seed(app) ? app : throw new InvalidOperationException("could not seed an App");

    private static bool Seed(object app)
    {
        Field("_state").SetValue(app, new AppState());
        Field("_notices").SetValue(app, new HashSet<string>(StringComparer.Ordinal));
        return true;
    }

    private static AppState StateOf(object app) => (AppState)Field("_state").GetValue(app)!;

    private static void Invoke(object app, SyncErrorNotification error) =>
        AppType.GetMethod("Report", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(app, [error]);

    private static FieldInfo Field(string name) =>
        AppType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"App.{name} moved; this test asserts nothing.");

    private static readonly Type AppType =
        typeof(AppState).Assembly.GetType("Mailcoded.Tui.App", throwOnError: true)!;
}
