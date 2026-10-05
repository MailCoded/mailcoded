using Mailcoded.Core.Application;

namespace Mailcoded.Core.Tests.Support;

internal sealed class ProgressRecorder : IProgress<SyncProgress>
{
    public List<SyncProgress> Reports { get; } = [];

    public void Report(SyncProgress value) => Reports.Add(value);
}
