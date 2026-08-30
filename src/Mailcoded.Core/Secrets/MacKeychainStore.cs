using System.Runtime.InteropServices;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// macOS keychain backend, bound directly to Security.framework. The <c>security(1)</c> CLI is
/// deliberately not used: it would place the credential on a process command line.
/// Items are generic passwords with service <c>mailcoded</c> and account <c>&lt;secretRef&gt;</c>.
/// Inert on non-macOS.
/// </summary>
public sealed class MacKeychainStore : ISecretStore
{
    public const string ServiceName = "mailcoded";

    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const int ErrSecSuccess = 0;
    private const int ErrSecDuplicateItem = -25299;
    private const int ErrSecItemNotFound = -25300;

    public string BackendName => "keychain";

    public bool IsAvailable => OperatingSystem.IsMacOS() && Probe();

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        SecretRefGuard.ValidateValue(value);
        RequireMacOS();
        Write(reference, value);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireMacOS();
        return Task.FromResult(Read(reference));
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireMacOS();
        Remove(reference);
        return Task.CompletedTask;
    }

    private static void RequireMacOS()
    {
        if (!OperatingSystem.IsMacOS())
            throw new SecretStoreException("The keychain store is only usable on macOS.");
    }

    private static void Write(string secretRef, string value)
    {
        var service = Marshal.StringToCoTaskMemUTF8(ServiceName);
        var account = Marshal.StringToCoTaskMemUTF8(secretRef);
        using var blob = NativeSecretBuffer.FromUtf8(value);
        try
        {
            var status = SecKeychainAddGenericPassword(
                0, (uint)Utf8Length(ServiceName), service,
                (uint)Utf8Length(secretRef), account,
                (uint)blob.Length, blob.Pointer, out var added);
            if (status == ErrSecSuccess)
            {
                if (added != 0) CFRelease(added);
                return;
            }
            if (status != ErrSecDuplicateItem)
                throw Failure("store", secretRef, status);

            var found = SecKeychainFindGenericPassword(
                0, (uint)Utf8Length(ServiceName), service,
                (uint)Utf8Length(secretRef), account,
                out _, out var existingData, out var itemRef);
            if (found != ErrSecSuccess)
                throw Failure("update", secretRef, found);

            try
            {
                if (existingData != 0) SecKeychainItemFreeContent(0, existingData);
                var modified = SecKeychainItemModifyAttributesAndData(itemRef, 0, (uint)blob.Length, blob.Pointer);
                if (modified != ErrSecSuccess)
                    throw Failure("update", secretRef, modified);
            }
            finally
            {
                if (itemRef != 0) CFRelease(itemRef);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(service);
            Marshal.FreeCoTaskMem(account);
        }
    }

    private static string? Read(string secretRef)
    {
        var service = Marshal.StringToCoTaskMemUTF8(ServiceName);
        var account = Marshal.StringToCoTaskMemUTF8(secretRef);
        try
        {
            var status = SecKeychainFindGenericPassword(
                0, (uint)Utf8Length(ServiceName), service,
                (uint)Utf8Length(secretRef), account,
                out var length, out var data, out var itemRef);

            if (status == ErrSecItemNotFound) return null;
            if (status != ErrSecSuccess) throw Failure("read", secretRef, status);

            try
            {
                return NativeSecretBuffer.ReadUtf8(data, (int)length);
            }
            finally
            {
                if (data != 0) SecKeychainItemFreeContent(0, data);
                if (itemRef != 0) CFRelease(itemRef);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(service);
            Marshal.FreeCoTaskMem(account);
        }
    }

    private static void Remove(string secretRef)
    {
        var service = Marshal.StringToCoTaskMemUTF8(ServiceName);
        var account = Marshal.StringToCoTaskMemUTF8(secretRef);
        try
        {
            var status = SecKeychainFindGenericPassword(
                0, (uint)Utf8Length(ServiceName), service,
                (uint)Utf8Length(secretRef), account,
                out _, out var data, out var itemRef);

            if (status == ErrSecItemNotFound) return;
            if (status != ErrSecSuccess) throw Failure("delete", secretRef, status);

            try
            {
                if (data != 0) SecKeychainItemFreeContent(0, data);
                var deleted = SecKeychainItemDelete(itemRef);
                if (deleted != ErrSecSuccess && deleted != ErrSecItemNotFound)
                    throw Failure("delete", secretRef, deleted);
            }
            finally
            {
                if (itemRef != 0) CFRelease(itemRef);
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(service);
            Marshal.FreeCoTaskMem(account);
        }
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsMacOS()) return false;
        try
        {
            var status = SecKeychainCopyDefault(out var keychain);
            if (keychain != 0) CFRelease(keychain);
            return status == ErrSecSuccess;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static int Utf8Length(string value) => System.Text.Encoding.UTF8.GetByteCount(value);

    private static SecretStoreException Failure(string operation, string secretRef, int status) =>
        new($"Keychain could not {operation} secret '{SecretRedactor.SafeRef(secretRef)}' (OSStatus {status}).");

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainAddGenericPassword(
        nint keychain, uint serviceNameLength, nint serviceName,
        uint accountNameLength, nint accountName,
        uint passwordLength, nint passwordData, out nint itemRef);

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainFindGenericPassword(
        nint keychainOrArray, uint serviceNameLength, nint serviceName,
        uint accountNameLength, nint accountName,
        out uint passwordLength, out nint passwordData, out nint itemRef);

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainItemModifyAttributesAndData(
        nint itemRef, nint attrList, uint length, nint data);

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainItemFreeContent(nint attrList, nint data);

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainItemDelete(nint itemRef);

    [DllImport(SecurityFramework, ExactSpelling = true)]
    private static extern int SecKeychainCopyDefault(out nint keychain);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", ExactSpelling = true)]
    private static extern void CFRelease(nint cf);
}
