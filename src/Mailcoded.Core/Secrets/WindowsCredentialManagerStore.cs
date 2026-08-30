using System.Runtime.InteropServices;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// Windows Credential Manager backend. Items are generic credentials named
/// <c>mailcoded:&lt;secretRef&gt;</c> under the current user's persisted credential set.
/// Inert (never available, never P/Invokes) on non-Windows.
/// </summary>
public sealed class WindowsCredentialManagerStore : ISecretStore
{
    public const string TargetPrefix = "mailcoded:";

    private const uint CredTypeGeneric = 1;
    private const uint CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorNoSuchLogonSession = 1312;

    public string BackendName => "wincred";

    public bool IsAvailable => OperatingSystem.IsWindows() && Probe();

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        SecretRefGuard.ValidateValue(value);
        RequireWindows();
        Write(reference, value);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireWindows();
        return Task.FromResult(Read(reference));
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireWindows();
        Remove(reference);
        return Task.CompletedTask;
    }

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            throw new SecretStoreException("The Windows Credential Manager store is only usable on Windows.");
    }

    private static string TargetOf(string secretRef) => TargetPrefix + secretRef;

    private static void Write(string secretRef, string value)
    {
        var target = Marshal.StringToHGlobalUni(TargetOf(secretRef));
        var userName = Marshal.StringToHGlobalUni(secretRef);
        using var blob = NativeSecretBuffer.FromUtf8(value);
        try
        {
            var cred = new CredentialW
            {
                Flags = 0,
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = 0,
                LastWrittenLow = 0,
                LastWrittenHigh = 0,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blob.Pointer,
                Persist = CredPersistLocalMachine,
                AttributeCount = 0,
                Attributes = 0,
                TargetAlias = 0,
                UserName = userName
            };

            if (CredWriteW(ref cred, 0) == 0)
                throw Failure("store", secretRef, Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(target);
            Marshal.FreeHGlobal(userName);
        }
    }

    private static string? Read(string secretRef)
    {
        var target = Marshal.StringToHGlobalUni(TargetOf(secretRef));
        try
        {
            if (CredReadW(target, CredTypeGeneric, 0, out var handle) == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorNotFound or ErrorNoSuchLogonSession) return null;
                throw Failure("read", secretRef, error);
            }

            try
            {
                var size = (int)(uint)Marshal.ReadInt32(handle, BlobSizeOffset);
                var data = Marshal.ReadIntPtr(handle, BlobPointerOffset);
                return NativeSecretBuffer.ReadUtf8(data, size);
            }
            finally
            {
                CredFree(handle);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(target);
        }
    }

    private static void Remove(string secretRef)
    {
        var target = Marshal.StringToHGlobalUni(TargetOf(secretRef));
        try
        {
            if (CredDeleteW(target, CredTypeGeneric, 0) != 0) return;
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorNotFound or ErrorNoSuchLogonSession) return;
            throw Failure("delete", secretRef, error);
        }
        finally
        {
            Marshal.FreeHGlobal(target);
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var target = Marshal.StringToHGlobalUni(TargetPrefix + "probe");
        try
        {
            if (CredReadW(target, CredTypeGeneric, 0, out var handle) != 0)
            {
                CredFree(handle);
                return true;
            }
            return Marshal.GetLastWin32Error() != ErrorNoSuchLogonSession;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        finally
        {
            Marshal.FreeHGlobal(target);
        }
    }

    private static SecretStoreException Failure(string operation, string secretRef, int error) =>
        new($"Credential Manager could not {operation} secret '{SecretRedactor.SafeRef(secretRef)}' (win32 {error}).");

    // CREDENTIALW field offsets, read by hand so no runtime struct marshalling is needed on the
    // read path. Pointer-sized alignment: blob size follows two pointers plus FILETIME.
    private static int BlobSizeOffset => 16 + 2 * IntPtr.Size;
    private static int BlobPointerOffset
    {
        get
        {
            var afterSize = BlobSizeOffset + 4;
            var align = IntPtr.Size;
            return (afterSize + align - 1) / align * align;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialW
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public uint LastWrittenLow;
        public uint LastWrittenHigh;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", ExactSpelling = true, SetLastError = true)]
    private static extern int CredWriteW(ref CredentialW credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", ExactSpelling = true, SetLastError = true)]
    private static extern int CredReadW(nint targetName, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", ExactSpelling = true, SetLastError = true)]
    private static extern int CredDeleteW(nint targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree", ExactSpelling = true)]
    private static extern void CredFree(nint buffer);
}
