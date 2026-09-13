using AkiSpace.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// File-level round-trip coverage for SettingsService via its internal
/// temp-directory constructor. In particular this is the regression suite for
/// the bug where SaveCore DPAPI-wrapped the LIVE settings object in place,
/// making Current.ClonePassword the wrapped blob after any Update — which then
/// broke the next RDP connect in that process (the wrapped string was sent as
/// the literal password).
/// </summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"akispace-settings-{Guid.NewGuid():N}");

    private SettingsService Create() =>
        new(NullLogger<SettingsService>.Instance, _dir);

    [Fact]
    public void Update_KeepsInMemoryPasswordPlaintext_AndWritesProtectedDisk()
    {
        const string plaintext = "s3cret-roundtrip-pw";
        var service = Create();
        service.Update(s => s.ClonePassword = plaintext);

        // In-memory snapshot after the save must still be the plaintext password.
        Assert.Equal(plaintext, service.Current.ClonePassword);

        // On disk it must be DPAPI-wrapped, never plaintext.
        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.Contains(SettingsProtection.Prefix, json);
        Assert.DoesNotContain(plaintext, json);

        // And a fresh service over the same directory reads the plaintext back.
        var reloaded = Create();
        Assert.Equal(plaintext, reloaded.Current.ClonePassword);
    }

    [Fact]
    public void Update_AfterLoad_DoesNotDoubleWrapOrCorruptPassword()
    {
        const string plaintext = "first-password";
        var first = Create();
        first.Update(s => s.ClonePassword = plaintext);

        var second = Create();
        // A second save of an unrelated setting must leave the password usable.
        second.Update(s => s.GameMouseModeEnabled = true);
        Assert.Equal(plaintext, second.Current.ClonePassword);

        var reloaded = Create();
        Assert.Equal(plaintext, reloaded.Current.ClonePassword);
    }

    [Fact]
    public void Load_CorruptFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "{ not valid json !!!");

        var service = Create();

        Assert.Equal(new AppSettings().DesktopWidth, service.Current.DesktopWidth);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}
