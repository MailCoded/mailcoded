using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Mailcoded.Core.Secrets;

/// <summary>
/// The always-available fallback store: an AEAD-encrypted file beside the store database, required
/// because headless and container hosts have no keyring.
/// <para>
/// Key material: on Windows the file key is wrapped with DPAPI (CurrentUser). Elsewhere it is
/// derived with PBKDF2-SHA256 from a passphrase taken from <c>MAILCODED_SECRET_KEY</c>, or the file
/// named by <c>MAILCODED_SECRET_KEY_FILE</c>, or - when neither is set - from a machine-local key
/// file this store creates next to the vault with mode 0600. Setting either environment variable
/// also overrides DPAPI on Windows.
/// </para>
/// <para>
/// Records are individually sealed with ChaCha20-Poly1305 where supported and AES-GCM otherwise,
/// with a fresh 96-bit nonce per write and the secret reference as associated data. Writes go to a
/// temp file that is fsynced and renamed, so a crash cannot truncate the vault.
/// </para>
/// </summary>
public sealed class EncryptedFileStore : ISecretStore
{
    public const string PassphraseEnvVar = "MAILCODED_SECRET_KEY";
    public const string PassphraseFileEnvVar = "MAILCODED_SECRET_KEY_FILE";
    public const string VaultFileName = "secrets.enc";
    public const string MachineKeyFileName = "secrets.key";

    private const string Magic = "mailcoded-secrets-1";
    private const string KdfPbkdf2 = "pbkdf2-sha256";
    private const string KdfDpapi = "dpapi";
    private const string AlgAesGcm = "aesgcm";
    private const string AlgChaCha = "chacha20poly1305";
    private const int Pbkdf2Iterations = 600_000;
    private const int KeyBytes = 32;
    private const int SaltBytes = 16;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private static readonly byte[] DpapiEntropy = "mailcoded-secret-store-v1"u8.ToArray();
    private static readonly UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly object _gate = new();
    private readonly string _directory;

    private byte[]? _cachedKey;
    private string? _cachedKeyId;

    public EncryptedFileStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        VaultPath = Path.Combine(_directory, VaultFileName);
        MachineKeyPath = Path.Combine(_directory, MachineKeyFileName);
    }

    public string VaultPath { get; }

    public string MachineKeyPath { get; }

    public string BackendName => "file";

    public bool IsAvailable => true;

    public Task SetAsync(string secretRef, string value, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);
        SecretRefGuard.ValidateValue(value);

        lock (_gate)
        {
            var vault = LoadOrCreate();
            var key = ResolveKey(vault.Header);
            var algorithm = ChaCha20Poly1305.IsSupported ? AlgChaCha : AlgAesGcm;
            var plaintext = Encoding.UTF8.GetBytes(value);
            try
            {
                var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                var sealedBytes = Seal(algorithm, key, nonce, plaintext, Encoding.UTF8.GetBytes(reference));
                vault.Records[reference] = new VaultRecord(algorithm, nonce, sealedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
            Save(vault);
        }
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<string?>(ct);
        var reference = SecretRefGuard.Validate(secretRef);

        lock (_gate)
        {
            var vault = Load();
            if (vault is null || !vault.Records.TryGetValue(reference, out var record)) return Task.FromResult<string?>(null);

            var key = ResolveKey(vault.Header);
            var plaintext = Open(record, key, Encoding.UTF8.GetBytes(reference));
            try
            {
                return Task.FromResult<string?>(Encoding.UTF8.GetString(plaintext));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    public Task DeleteAsync(string secretRef, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
        var reference = SecretRefGuard.Validate(secretRef);

        lock (_gate)
        {
            var vault = Load();
            if (vault is null || !vault.Records.Remove(reference)) return Task.CompletedTask;
            Save(vault);
        }
        return Task.CompletedTask;
    }

    private static byte[] Seal(string algorithm, byte[] key, byte[] nonce, byte[] plaintext, byte[] associatedData)
    {
        var output = new byte[plaintext.Length + TagBytes];
        var ciphertext = output.AsSpan(0, plaintext.Length);
        var tag = output.AsSpan(plaintext.Length, TagBytes);

        if (algorithm == AlgChaCha)
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        else
        {
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        return output;
    }

    private static byte[] Open(VaultRecord record, byte[] key, byte[] associatedData)
    {
        if (record.Sealed.Length < TagBytes)
            throw new SecretStoreException("The secret file contains a truncated record.");

        var plaintext = new byte[record.Sealed.Length - TagBytes];
        var ciphertext = record.Sealed.AsSpan(0, plaintext.Length);
        var tag = record.Sealed.AsSpan(plaintext.Length, TagBytes);

        try
        {
            if (record.Algorithm == AlgChaCha)
            {
                if (!ChaCha20Poly1305.IsSupported)
                    throw new SecretStoreException("This secret file needs ChaCha20-Poly1305, which this platform does not provide.");
                using var cipher = new ChaCha20Poly1305(key);
                cipher.Decrypt(record.Nonce, ciphertext, tag, plaintext, associatedData);
            }
            else
            {
                using var cipher = new AesGcm(key, TagBytes);
                cipher.Decrypt(record.Nonce, ciphertext, tag, plaintext, associatedData);
            }
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new SecretStoreException(
                "A record in the secret file failed authentication: the key material does not match, " +
                "or the file was modified. Check " + PassphraseEnvVar + " / " + PassphraseFileEnvVar + ".");
        }
        return plaintext;
    }

    private byte[] ResolveKey(VaultHeader header)
    {
        var id = header.Kdf + ":" + Convert.ToBase64String(header.Material) + ":" + header.Iterations.ToString(CultureInfo.InvariantCulture);
        if (_cachedKey is not null && _cachedKeyId == id) return _cachedKey;

        byte[] key;
        if (header.Kdf == KdfDpapi)
        {
            if (!OperatingSystem.IsWindows())
                throw new SecretStoreException("This secret file is DPAPI-protected and can only be opened on the Windows account that created it.");
            key = Dpapi.Unprotect(header.Material);
        }
        else
        {
            var passphrase = ResolvePassphrase(createIfMissing: false)
                ?? throw new SecretStoreException(
                    $"No passphrase available: set {PassphraseEnvVar} or {PassphraseFileEnvVar}, or restore {MachineKeyFileName}.");
            key = Rfc2898DeriveBytes.Pbkdf2(passphrase, header.Material, header.Iterations, HashAlgorithmName.SHA256, KeyBytes);
        }

        _cachedKey = key;
        _cachedKeyId = id;
        return key;
    }

    private VaultHeader CreateHeader()
    {
        var explicitPassphrase = ReadConfiguredPassphrase();
        if (explicitPassphrase is null && OperatingSystem.IsWindows())
        {
            var key = RandomNumberGenerator.GetBytes(KeyBytes);
            try
            {
                return new VaultHeader(KdfDpapi, Dpapi.Protect(key), 0);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        _ = ResolvePassphrase(createIfMissing: true);
        return new VaultHeader(KdfPbkdf2, RandomNumberGenerator.GetBytes(SaltBytes), Pbkdf2Iterations);
    }

    private string? ReadConfiguredPassphrase()
    {
        var inline = Environment.GetEnvironmentVariable(PassphraseEnvVar);
        if (!string.IsNullOrEmpty(inline)) return inline;

        var path = Environment.GetEnvironmentVariable(PassphraseFileEnvVar);
        if (string.IsNullOrEmpty(path)) return null;

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new SecretStoreException($"The key file named by {PassphraseFileEnvVar} could not be read.");
        }

        var trimmed = text.TrimEnd('\r', '\n');
        if (trimmed.Length == 0)
            throw new SecretStoreException($"The key file named by {PassphraseFileEnvVar} is empty.");
        return trimmed;
    }

    private string? ResolvePassphrase(bool createIfMissing)
    {
        var configured = ReadConfiguredPassphrase();
        if (configured is not null) return configured;

        if (File.Exists(MachineKeyPath))
        {
            var existing = File.ReadAllText(MachineKeyPath).TrimEnd('\r', '\n');
            if (existing.Length > 0) return existing;
        }
        if (!createIfMissing) return null;

        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(KeyBytes));
        WriteFileAtomically(MachineKeyPath, Encoding.UTF8.GetBytes(generated + "\n"));
        return generated;
    }

    private Vault LoadOrCreate() => Load() ?? new Vault(CreateHeader(), new Dictionary<string, VaultRecord>(StringComparer.Ordinal));

    private Vault? Load()
    {
        string[] lines;
        try
        {
            if (!File.Exists(VaultPath)) return null;
            lines = File.ReadAllLines(VaultPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SecretStoreException("The secret file could not be read.", ex);
        }

        if (lines.Length == 0 || lines[0].Trim() != Magic)
            throw new SecretStoreException("The secret file has an unrecognized format.");

        VaultHeader? header = null;
        var records = new Dictionary<string, VaultRecord>(StringComparer.Ordinal);

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(' ');

            if (parts[0] == "kdf" && parts.Length >= 4)
            {
                header = new VaultHeader(parts[1], Decode(parts[2]), int.Parse(parts[3], CultureInfo.InvariantCulture));
            }
            else if (parts[0] == "s" && parts.Length >= 5)
            {
                var reference = Encoding.UTF8.GetString(Decode(parts[1]));
                records[reference] = new VaultRecord(parts[2], Decode(parts[3]), Decode(parts[4]));
            }
            else
            {
                throw new SecretStoreException("The secret file has an unrecognized format.");
            }
        }

        if (header is null) throw new SecretStoreException("The secret file has no key header.");
        return new Vault(header, records);
    }

    private static byte[] Decode(string value)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException ex)
        {
            throw new SecretStoreException("The secret file has an unrecognized format.", ex);
        }
    }

    private void Save(Vault vault)
    {
        var sb = new StringBuilder();
        sb.Append(Magic).Append('\n');
        sb.Append("kdf ").Append(vault.Header.Kdf).Append(' ')
          .Append(Convert.ToBase64String(vault.Header.Material)).Append(' ')
          .Append(vault.Header.Iterations.ToString(CultureInfo.InvariantCulture)).Append('\n');

        foreach (var (reference, record) in vault.Records.OrderBy(static r => r.Key, StringComparer.Ordinal))
        {
            sb.Append("s ")
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(reference))).Append(' ')
              .Append(record.Algorithm).Append(' ')
              .Append(Convert.ToBase64String(record.Nonce)).Append(' ')
              .Append(Convert.ToBase64String(record.Sealed)).Append('\n');
        }

        WriteFileAtomically(VaultPath, Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private void WriteFileAtomically(string path, byte[] content)
    {
        Directory.CreateDirectory(_directory);
        var temp = path + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None
            };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(content, 0, content.Length);
                stream.Flush(true);
            }

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, OwnerOnly);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            throw new SecretStoreException("The secret file could not be written.", ex);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record VaultHeader(string Kdf, byte[] Material, int Iterations);

    private sealed record VaultRecord(string Algorithm, byte[] Nonce, byte[] Sealed);

    private sealed record Vault(VaultHeader Header, Dictionary<string, VaultRecord> Records);

    /// <summary>CryptProtectData/CryptUnprotectData, CurrentUser scope, UI suppressed.</summary>
    private static class Dpapi
    {
        private const uint CryptProtectUiForbidden = 0x1;

        public static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

        public static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inputPtr = Marshal.AllocHGlobal(input.Length);
            var entropyPtr = Marshal.AllocHGlobal(DpapiEntropy.Length);
            var output = new DataBlob();
            try
            {
                Marshal.Copy(input, 0, inputPtr, input.Length);
                Marshal.Copy(DpapiEntropy, 0, entropyPtr, DpapiEntropy.Length);
                var inBlob = new DataBlob { Size = (uint)input.Length, Data = inputPtr };
                var entropy = new DataBlob { Size = (uint)DpapiEntropy.Length, Data = entropyPtr };

                var ok = protect
                    ? CryptProtectData(ref inBlob, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out output)
                    : CryptUnprotectData(ref inBlob, 0, ref entropy, 0, 0, CryptProtectUiForbidden, out output);

                if (ok == 0)
                    throw new SecretStoreException(
                        $"DPAPI could not {(protect ? "protect" : "unprotect")} the secret file key (win32 {Marshal.GetLastWin32Error()}).");

                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                for (var i = 0; i < input.Length; i++) Marshal.WriteByte(inputPtr, i, 0);
                Marshal.FreeHGlobal(inputPtr);
                Marshal.FreeHGlobal(entropyPtr);
                if (output.Data != 0)
                {
                    for (var i = 0; i < output.Size; i++) Marshal.WriteByte(output.Data, i, 0);
                    LocalFree(output.Data);
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public uint Size;
            public nint Data;
        }

        [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", ExactSpelling = true, SetLastError = true)]
        private static extern int CryptProtectData(
            ref DataBlob input, nint description, ref DataBlob entropy,
            nint reserved, nint prompt, uint flags, out DataBlob output);

        [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", ExactSpelling = true, SetLastError = true)]
        private static extern int CryptUnprotectData(
            ref DataBlob input, nint description, ref DataBlob entropy,
            nint reserved, nint prompt, uint flags, out DataBlob description2);

        [DllImport("kernel32.dll", EntryPoint = "LocalFree", ExactSpelling = true)]
        private static extern nint LocalFree(nint handle);
    }
}
