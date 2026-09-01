using Mailcoded.Core.Tests.Support;
using Mailcoded.Daemon;
using Xunit;

namespace Mailcoded.Core.Tests.Daemon;

/// <summary>
/// The exclusion is an OS advisory lock, so a second acquire in this process is refused exactly as
/// a second daemon process is (flock conflicts across file descriptors, not just across processes).
/// A genuinely cross-process check needs a second daemon binary and lives in the integration suite.
/// </summary>
public sealed class SingleInstanceLockTests
{
    private static StderrLog Log() => new(TextWriter.Null, DaemonLogLevel.Off, timestamps: false);

    [Fact]
    public void TheSecondAcquireOnOneStoreIsASecondary()
    {
        using var workspace = TempWorkspace.Create("instance-lock");

        using var first = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.True(first.IsPrimary);
        Assert.Null(first.Detail);
        Assert.Equal(Environment.ProcessId, first.OwnerPid);

        using var second = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.False(second.IsPrimary);
        Assert.Equal("another-daemon-owns-store", second.Detail);
    }

    [Fact]
    public void ASecondaryNamesTheLiveOwnerFromTheDiagnosticStamp()
    {
        using var workspace = TempWorkspace.Create("instance-lock-owner");

        using var first = SingleInstanceLock.Acquire(workspace.Root, Log());
        using var second = SingleInstanceLock.Acquire(workspace.Root, Log());

        Assert.False(second.IsPrimary);
        Assert.Equal(Environment.ProcessId, second.OwnerPid);

        var stamp = File.ReadAllText(Path.Combine(workspace.Root, SingleInstanceLock.OwnerFileName));
        Assert.Contains("pid=" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), stamp, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleasingTheLockHandsPrimaryOwnershipToTheNextAcquire()
    {
        using var workspace = TempWorkspace.Create("instance-lock-handoff");

        var first = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.True(first.IsPrimary);
        first.Dispose();

        using var next = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.True(next.IsPrimary);
        Assert.Null(next.Detail);
    }

    [Fact]
    public void ThePidStampIsNotTheLock()
    {
        using var workspace = TempWorkspace.Create("instance-lock-stamp");

        // A stale stamp naming a dead process must not hand out primary ownership.
        File.WriteAllText(Path.Combine(workspace.Root, SingleInstanceLock.OwnerFileName), "pid=999999999\n");

        using var first = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.True(first.IsPrimary);

        using var second = SingleInstanceLock.Acquire(workspace.Root, Log());
        Assert.False(second.IsPrimary);
    }
}
