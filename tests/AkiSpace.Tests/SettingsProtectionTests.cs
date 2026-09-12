using AkiSpace.Services;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Covers the DPAPI-at-rest helpers backing settings.json password protection.
/// These are the PUBLIC members of the persistence path; SettingsService
/// itself is only reachable through its %APPDATA% constructor from an external
/// assembly (the temp-directory seam is `internal` and InternalsVisibleTo is
/// limited to AkiSpace.SelfTest), so the file-level round-trip is covered by
/// these helpers instead. See the report for the seam gap.
/// </summary>
public sealed class SettingsProtectionTests
{
    [Fact]
    public void Protect_Produces_DpapiPrefixed_Ciphertext()
    {
        var protectedValue = SettingsProtection.Protect("s3cret-pw");

        Assert.StartsWith(SettingsProtection.Prefix, protectedValue);
        Assert.NotEqual("s3cret-pw", protectedValue);
        // The base64 body must decode.
        Convert.FromBase64String(protectedValue.Substring(SettingsProtection.Prefix.Length));
    }

    [Fact]
    public void Protect_Then_Unprotect_RoundTrips()
    {
        const string plaintext = "Pa$$w0rd-äöü-123";

        var restored = SettingsProtection.Unprotect(SettingsProtection.Protect(plaintext));

        Assert.Equal(plaintext, restored);
    }

    [Fact]
    public void Unprotect_PassesThrough_Plaintext()
    {
        // Older settings.json files store the password in plaintext; read must
        // accept it unchanged so the next save can upgrade it.
        Assert.Equal("legacy-plain", SettingsProtection.Unprotect("legacy-plain"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("DPAPI:")]                    // prefix + empty base64 body
    [InlineData("DPAPI:not-valid-base64!!!")] // prefix + non-base64 garbage
    [InlineData("DPAPI:AAECAw==fake")]        // plausible-but-invalid blob
    public void Unprotect_ReturnsLiteral_OnUndecryptableInput(string stored)
    {
        // Never throws: corrupt / foreign-machine blobs fall back to the literal
        // so the caller can log the warning.
        Assert.Equal(stored, SettingsProtection.Unprotect(stored));
    }

    [Fact]
    public void Protect_EmptyString_IsNoOp()
    {
        Assert.Equal("", SettingsProtection.Protect(""));
    }
}
