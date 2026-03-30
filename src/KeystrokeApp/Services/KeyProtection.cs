using System.Security.Cryptography;
using System.Text;

namespace KeystrokeApp.Services;

/// <summary>
/// Encrypts and decrypts API keys using Windows DPAPI (Data Protection API).
/// Keys are scoped to the current Windows user — only the same user account
/// on the same machine can decrypt them.
/// </summary>
public static class KeyProtection
{
    private const string EncryptedPrefix = "enc:";

    /// <summary>
    /// Encrypt a plaintext API key for storage on disk.
    /// Returns a string prefixed with "enc:" followed by base64-encoded ciphertext.
    /// Returns null/empty inputs unchanged.
    /// </summary>
    public static string? Encrypt(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
            return plaintext;

        // Already encrypted — don't double-encrypt
        if (plaintext.StartsWith(EncryptedPrefix))
            return plaintext;

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherBytes = ProtectedData.Protect(plaintextBytes, null, DataProtectionScope.CurrentUser);
        return EncryptedPrefix + Convert.ToBase64String(cipherBytes);
    }

    /// <summary>
    /// Decrypt an API key loaded from disk.
    /// Handles both encrypted ("enc:...") and legacy plaintext keys transparently.
    /// Returns null/empty inputs unchanged.
    /// </summary>
    public static string? Decrypt(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
            return stored;

        // Legacy plaintext key — return as-is (will be encrypted on next save)
        if (!stored.StartsWith(EncryptedPrefix))
            return stored;

        try
        {
            var cipherBytes = Convert.FromBase64String(stored[EncryptedPrefix.Length..]);
            var plaintextBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plaintextBytes);
        }
        catch (Exception)
        {
            // Decryption failed (e.g., different user account, corrupted data, malformed base64).
            // Return null so the user is prompted to re-enter the key.
            return null;
        }
    }

    /// <summary>
    /// Returns true if the stored value is already encrypted.
    /// </summary>
    public static bool IsEncrypted(string? stored) =>
        stored != null && stored.StartsWith(EncryptedPrefix);
}
