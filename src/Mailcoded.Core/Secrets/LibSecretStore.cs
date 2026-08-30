using System.Runtime.InteropServices;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// Freedesktop secret service backend (gnome-keyring, KWallet's secret service bridge) via
/// libsecret-1. <see cref="IsAvailable"/> returns false rather than throwing when the library is
/// missing or there is no D-Bus session, so headless hosts fall through to the file store.
/// Inert on non-Linux.
/// </summary>
public sealed class LibSecretStore : ISecretStore
{
    public const string SchemaName = "org.mailcoded.Secret";
    private const string LibSecret = "libsecret-1.so.0";
    private const string LibGlib = "libglib-2.0.so.0";
    private const string AttributeKey = "ref";
    private const string ProbeRef = "mailcoded.probe";

    private static readonly Lazy<bool> Availability = new(Probe, LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<nint> Schema = new(CreateSchema, LazyThreadSafetyMode.ExecutionAndPublication);

    public string BackendName => "libsecret";

    public bool IsAvailable => OperatingSystem.IsLinux() && Availability.Value;

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        SecretRefGuard.ValidateValue(value);
        RequireLinux();
        Write(reference, value);
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireLinux();
        return Task.FromResult(Read(reference));
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        RequireLinux();
        Remove(reference);
        return Task.CompletedTask;
    }

    private static void RequireLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new SecretStoreException("The libsecret store is only usable on Linux.");
    }

    private static void Write(string secretRef, string value)
    {
        var collection = Marshal.StringToCoTaskMemUTF8("default");
        var label = Marshal.StringToCoTaskMemUTF8("mailcoded: " + secretRef);
        var attributeKey = Marshal.StringToCoTaskMemUTF8(AttributeKey);
        var attributeValue = Marshal.StringToCoTaskMemUTF8(secretRef);
        using var password = NativeSecretBuffer.FromUtf8(value);
        nint error = 0;
        try
        {
            var stored = secret_password_store_sync(
                Schema.Value, collection, label, password.Pointer, 0, ref error,
                attributeKey, attributeValue, 0);
            if (error != 0 || stored == 0)
                throw Failure("store", secretRef);
        }
        finally
        {
            FreeError(ref error);
            Marshal.FreeCoTaskMem(collection);
            Marshal.FreeCoTaskMem(label);
            Marshal.FreeCoTaskMem(attributeKey);
            Marshal.FreeCoTaskMem(attributeValue);
        }
    }

    private static string? Read(string secretRef)
    {
        var attributeKey = Marshal.StringToCoTaskMemUTF8(AttributeKey);
        var attributeValue = Marshal.StringToCoTaskMemUTF8(secretRef);
        nint error = 0;
        nint password = 0;
        try
        {
            password = secret_password_lookup_sync(Schema.Value, 0, ref error, attributeKey, attributeValue, 0);
            if (error != 0) throw Failure("read", secretRef);
            return password == 0 ? null : Marshal.PtrToStringUTF8(password);
        }
        finally
        {
            if (password != 0) secret_password_free(password);
            FreeError(ref error);
            Marshal.FreeCoTaskMem(attributeKey);
            Marshal.FreeCoTaskMem(attributeValue);
        }
    }

    private static void Remove(string secretRef)
    {
        var attributeKey = Marshal.StringToCoTaskMemUTF8(AttributeKey);
        var attributeValue = Marshal.StringToCoTaskMemUTF8(secretRef);
        nint error = 0;
        try
        {
            _ = secret_password_clear_sync(Schema.Value, 0, ref error, attributeKey, attributeValue, 0);
            if (error != 0) throw Failure("delete", secretRef);
        }
        finally
        {
            FreeError(ref error);
            Marshal.FreeCoTaskMem(attributeKey);
            Marshal.FreeCoTaskMem(attributeValue);
        }
    }

    private static void FreeError(ref nint error)
    {
        if (error == 0) return;
        try { g_error_free(error); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        error = 0;
    }

    private static SecretStoreException Failure(string operation, string secretRef) =>
        new($"libsecret could not {operation} secret '{SecretRedactor.SafeRef(secretRef)}'.");

    private static bool HasSessionBus()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS"))) return true;
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        return !string.IsNullOrEmpty(runtimeDir) && File.Exists(Path.Combine(runtimeDir, "bus"));
    }

    private static bool Probe()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!HasSessionBus()) return false;
        if (!NativeLibrary.TryLoad(LibSecret, out _)) return false;

        var attributeKey = Marshal.StringToCoTaskMemUTF8(AttributeKey);
        var attributeValue = Marshal.StringToCoTaskMemUTF8(ProbeRef);
        nint error = 0;
        nint password = 0;
        try
        {
            password = secret_password_lookup_sync(Schema.Value, 0, ref error, attributeKey, attributeValue, 0);
            return error == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        catch (BadImageFormatException) { return false; }
        finally
        {
            if (password != 0) secret_password_free(password);
            FreeError(ref error);
            Marshal.FreeCoTaskMem(attributeKey);
            Marshal.FreeCoTaskMem(attributeValue);
        }
    }

    /// <summary>
    /// Builds a SecretSchema by hand: its constructor is variadic, and the struct is a stable part
    /// of libsecret's ABI. One string attribute, no flags. Lives for the process lifetime.
    /// </summary>
    private static nint CreateSchema()
    {
        var p = IntPtr.Size;
        const int attributeSlots = 32;
        var size = 2 * p + attributeSlots * 2 * p + 8 * p;
        var block = Marshal.AllocHGlobal(size);
        for (var i = 0; i < size; i++) Marshal.WriteByte(block, i, 0);

        Marshal.WriteIntPtr(block, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(block, p, 0);
        Marshal.WriteIntPtr(block, 2 * p, Marshal.StringToCoTaskMemUTF8(AttributeKey));
        Marshal.WriteInt32(block, 3 * p, 0);
        return block;
    }

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int secret_password_store_sync(
        nint schema, nint collection, nint label, nint password, nint cancellable, ref nint error,
        nint attributeName, nint attributeValue, nint terminator);

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern nint secret_password_lookup_sync(
        nint schema, nint cancellable, ref nint error,
        nint attributeName, nint attributeValue, nint terminator);

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern int secret_password_clear_sync(
        nint schema, nint cancellable, ref nint error,
        nint attributeName, nint attributeValue, nint terminator);

    [DllImport(LibSecret, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void secret_password_free(nint password);

    [DllImport(LibGlib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern void g_error_free(nint error);
}
