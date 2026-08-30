using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// Unmanaged UTF-8 scratch space for values handed to OS keyring APIs. Always zeroed before free
/// so a freed heap block cannot be scavenged.
/// </summary>
internal sealed class NativeSecretBuffer : IDisposable
{
    private nint _ptr;
    private int _length;

    private NativeSecretBuffer(nint ptr, int length)
    {
        _ptr = ptr;
        _length = length;
    }

    public nint Pointer => _ptr;

    /// <summary>Byte count excluding the NUL terminator.</summary>
    public int Length => _length;

    public static NativeSecretBuffer FromUtf8(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var ptr = Marshal.AllocHGlobal(bytes.Length + 1);
        try
        {
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            Marshal.WriteByte(ptr, bytes.Length, 0);
        }
        catch
        {
            Zero(ptr, bytes.Length + 1);
            Marshal.FreeHGlobal(ptr);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
        return new NativeSecretBuffer(ptr, bytes.Length);
    }

    public static string? ReadUtf8(nint ptr, int length)
    {
        if (ptr == 0 || length < 0) return null;
        if (length == 0) return string.Empty;
        var bytes = new byte[length];
        Marshal.Copy(ptr, bytes, 0, length);
        try
        {
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void Zero(nint ptr, int length)
    {
        for (var i = 0; i < length; i++) Marshal.WriteByte(ptr, i, 0);
    }

    public void Dispose()
    {
        if (_ptr == 0) return;
        Zero(_ptr, _length + 1);
        Marshal.FreeHGlobal(_ptr);
        _ptr = 0;
        _length = 0;
    }
}
