namespace Mailcoded.Core.Secrets;

/// <summary>Backends selectable through <see cref="SecretStoreFactory.BackendEnvVar"/>.</summary>
public enum SecretBackend
{
    /// <summary>OS keyring first, encrypted file as fallback.</summary>
    Auto,
    /// <summary>Encrypted file only - the headless and container choice.</summary>
    File,
    WindowsCredentialManager,
    MacKeychain,
    LibSecret
}

/// <summary>
/// Chooses the best secret store for the current OS and session. Honours
/// <c>MAILCODED_SECRET_BACKEND=file</c> as an explicit headless override.
/// </summary>
public static class SecretStoreFactory
{
    public const string BackendEnvVar = "MAILCODED_SECRET_BACKEND";

    /// <param name="dataDirectory">Directory holding the store database; the vault lives beside it.</param>
    public static ISecretStore Create(string dataDirectory) =>
        Create(dataDirectory, ReadBackendFromEnvironment());

    public static ISecretStore Create(string dataDirectory, SecretBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var file = new EncryptedFileStore(dataDirectory);

        switch (backend)
        {
            case SecretBackend.File:
                return file;
            case SecretBackend.WindowsCredentialManager:
                return Require(new WindowsCredentialManagerStore());
            case SecretBackend.MacKeychain:
                return Require(new MacKeychainStore());
            case SecretBackend.LibSecret:
                return Require(new LibSecretStore());
            default:
                return new ChainedSecretStore(OsKeyring(), file);
        }
    }

    /// <summary>The OS keyring store for this platform, which may report itself unavailable.</summary>
    public static ISecretStore OsKeyring()
    {
        if (OperatingSystem.IsWindows()) return new WindowsCredentialManagerStore();
        if (OperatingSystem.IsMacOS()) return new MacKeychainStore();
        return new LibSecretStore();
    }

    public static SecretBackend ReadBackendFromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable(BackendEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return SecretBackend.Auto;

        return raw.Trim().ToLowerInvariant() switch
        {
            "auto" => SecretBackend.Auto,
            "file" or "encrypted-file" => SecretBackend.File,
            "wincred" or "windows" => SecretBackend.WindowsCredentialManager,
            "keychain" or "macos" => SecretBackend.MacKeychain,
            "libsecret" or "keyring" => SecretBackend.LibSecret,
            // The value is never echoed: an operator could have pasted a credential into it.
            _ => throw new SecretStoreException($"{BackendEnvVar} is set to an unsupported backend name.")
        };
    }

    private static ISecretStore Require(ISecretStore store)
    {
        if (!store.IsAvailable)
            throw new SecretStoreException(
                $"{BackendEnvVar} requested the '{store.BackendName}' backend, which is not available on this machine or session.");
        return store;
    }
}
