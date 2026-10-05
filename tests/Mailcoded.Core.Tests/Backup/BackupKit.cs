using System.Text;
using Mailcoded.Core.Application;
using Mailcoded.Core.Domain.Primitives;
using Mailcoded.Core.Store;
using Mailcoded.Core.Tests.Support;

namespace Mailcoded.Core.Tests.Backup;

internal sealed class BackupKit : IDisposable
{
    public const int ExternalThreshold = 64;

    private BackupKit(TempStore live, TempWorkspace target, BackupService service, AgentPolicy policy, AuditLog audit)
    {
        Live = live;
        Target = target;
        Service = service;
        Policy = policy;
        Audit = audit;
    }

    public TempStore Live { get; }

    public TempWorkspace Target { get; }

    public BackupService Service { get; }

    public AgentPolicy Policy { get; }

    public AuditLog Audit { get; }

    public SqliteStore Store => Live.Store;

    public static BackupKit Create(AgentPolicyOptions? options = null, string? home = null)
    {
        var live = TempStore.Create(configure: o => o with { InlineBlobThresholdBytes = ExternalThreshold });
        var target = TempWorkspace.Create("backup-target");

        try
        {
            var policy = new AgentPolicy(options ?? AgentPolicyOptions.Locked, live.Clock, live.Store);
            var audit = new AuditLog(live.Store, live.Clock);
            var service = new BackupService(live.Store, policy, audit, live.Clock, "9.9.9-test", home ?? target.Root);
            return new BackupKit(live, target, service, policy, audit);
        }
        catch
        {
            target.Dispose();
            live.Dispose();
            throw;
        }
    }

    public string Dest(string name = "backup") => Path.Combine(Target.Root, name);

    public async Task<IReadOnlyList<BlobRef>> SeedAsync(CancellationToken ct, int externalBlobs = 3)
    {
        var account = await StoreSeed.AccountAsync(Store, ct);
        var folder = await StoreSeed.FolderAsync(Store, account, "INBOX", Mailcoded.Core.Domain.Primitives.FolderRole.Inbox, ct);

        for (uint uid = 1; uid <= 4; uid++)
        {
            await StoreSeed.MessageAsync(
                Store,
                folder,
                StoreSeed.Envelope(uid),
                ct,
                bodyText: "Body number " + uid,
                tags: StoreSeed.Tags("work"));
        }

        var blobs = new List<BlobRef>();
        for (var i = 0; i < externalBlobs; i++)
            blobs.Add(await Store.StoreBlobAsync(Payload(1000 + i * 700, i), ct));

        blobs.Add(await Store.StoreBlobAsync(Payload(10, 99), ct));
        return blobs;
    }

    public static byte[] Payload(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    public static CallerContext Human { get; } = CallerContext.Rpc;

    public static string Read(string path) => Encoding.UTF8.GetString(File.ReadAllBytes(path));

    public static void FlipByte(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        stream.Seek(offset, SeekOrigin.Begin);
        var current = stream.ReadByte();
        stream.Seek(offset, SeekOrigin.Begin);
        stream.WriteByte((byte)(current ^ 0x01));
    }

    public static string BlobPath(string backupRoot, string sha) =>
        Path.Combine(backupRoot, "blobs", sha[..2], sha + ".blob");

    public void Dispose()
    {
        Live.Dispose();
        Target.Dispose();
    }
}
