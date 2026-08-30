namespace Mailcoded.Core.Secrets;

/// <summary>
/// Validates the opaque handle every store keys on. A reference reaches OS keyring item names,
/// so it is constrained to a conservative character set.
/// </summary>
internal static class SecretRefGuard
{
    public const int MaxLength = 128;

    public static string Validate(string secretRef)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            throw new SecretStoreException("secretRef must be a non-empty string.");
        if (secretRef.Length > MaxLength)
            throw new SecretStoreException($"secretRef is longer than {MaxLength} characters.");

        foreach (var c in secretRef)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                     || c is '.' or '-' or '_' or ':' or '@' or '+';
            if (!ok)
                throw new SecretStoreException(
                    $"secretRef '{SecretRedactor.SafeRef(secretRef)}' contains an unsupported character.");
        }
        return secretRef;
    }

    public static void ValidateValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
            throw new SecretStoreException("Secret value must not be empty.");
    }
}
