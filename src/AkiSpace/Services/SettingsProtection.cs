using System.Security.Cryptography;
using System.Text;

namespace AkiSpace.Services;

/// <summary>
/// DPAPI-at-rest protection for the clone session password stored in
/// %APPDATA%\AkiSpace\settings.json. Uses <see cref="DataProtectionScope.CurrentUser"/>
/// so only the same Windows user can decrypt; copying the file to another
/// user account or machine returns the literal fallback string and triggers
/// a warning on the next connect.
///
/// Wire format: <c>DPAPI:&lt;base64&gt;</c>. Plaintext values (older settings.json
/// files) are accepted as-is on read; on next save they are upgraded to the
/// DPAPI form automatically.
/// </summary>
public static class SettingsProtection
{
    public const string Prefix = "DPAPI:";

    /// <summary>Encrypts a plaintext value, returning <c>DPAPI:&lt;base64&gt;</c>.</summary>
    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return plaintext;
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// Decrypts a <c>DPAPI:</c>-prefixed value back to plaintext. If the input
    /// is not prefixed (older plaintext format), returns it unchanged. If DPAPI
    /// decryption fails (different user, machine change, corrupt blob), returns
    /// the literal input and lets the caller log a warning.
    /// </summary>
    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return stored;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        try
        {
            var b64 = stored.Substring(Prefix.Length);
            var protectedBytes = Convert.FromBase64String(b64);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return stored;  // fall back to literal; caller logs
        }
    }
}
