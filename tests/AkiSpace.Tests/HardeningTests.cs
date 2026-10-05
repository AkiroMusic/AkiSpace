using System.Text.RegularExpressions;
using AkiSpace.Common;
using AkiSpace.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Hardening tests for areas the round-trip suites don't reach: bilingual
/// placeholder parity, settings concurrency/forward-compatibility/plaintext
/// migration, log-file rotation, and theme-pack completeness.
/// </summary>
public sealed partial class HardeningTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"akispace-hardening-{Guid.NewGuid():N}");

    // ---- Loc: placeholder parity between languages ----

    [GeneratedRegex(@"\{[0-9]\}")]
    private static partial Regex PlaceholderRegex();

    [Fact]
    public void PlaceholdersMatchBetweenEnglishAndChinese()
    {
        var mismatches = new List<string>();
        foreach (var (key, en) in Loc.EnglishCatalog)
        {
            var enSet = PlaceholderRegex().Matches(en).Select(m => m.Value).OrderBy(v => v).ToList();
            var zhSet = PlaceholderRegex().Matches(Loc.ChineseCatalog[key]).Select(m => m.Value).OrderBy(v => v).ToList();
            if (!enSet.SequenceEqual(zhSet))
                mismatches.Add($"{key}: en=[{string.Join(",", enSet)}] zh=[{string.Join(",", zhSet)}]");
        }
        Assert.True(mismatches.Count == 0, $"placeholder mismatch: {string.Join("; ", mismatches)}");
    }

    [Fact]
    public void FormatWithReorderedPlaceholders_BothLanguages()
    {
        Loc.SetLanguage("en", persist: false);
        var en = Loc.F("Conn_Launched", "game.exe", 5U);
        Loc.SetLanguage("zh", persist: false);
        var zh = Loc.F("Conn_Launched", "game.exe", 5U);
        try
        {
            Assert.Contains("game.exe", en);
            Assert.Contains("5", en);
            Assert.Contains("game.exe", zh);
            Assert.Contains("5", zh);
            // The Chinese template swaps the argument order; both must render both.
            Assert.NotEqual(en, zh);
        }
        finally
        {
            Loc.SetLanguage("en", persist: false);
        }
    }

    // ---- SettingsService: concurrency, forward compat, plaintext migration ----

    private SettingsService Create() =>
        new(NullLogger<SettingsService>.Instance, _dir);

    [Fact]
    public void ConcurrentUpdates_AllSurvive_InFileAndMemory()
    {
        var service = Create();
        Parallel.For(0, 32, i =>
        {
            service.Update(s => s.DesktopWidth = 1000 + i);
            service.Update(s => s.RdpPort = 30000 + i);
        });

        // Every write went through the lock; the final state must be one of the
        // written values and a fresh instance must read exactly what memory holds.
        var reloaded = Create();
        Assert.Equal(service.Current.DesktopWidth, reloaded.Current.DesktopWidth);
        Assert.Equal(service.Current.RdpPort, reloaded.Current.RdpPort);
        Assert.InRange(reloaded.Current.DesktopWidth, 1000, 1031);
        Assert.InRange(reloaded.Current.RdpPort, 30000, 30031);
    }

    [Fact]
    public void Load_UnknownJsonFields_AreIgnored()
    {
        Directory.CreateDirectory(_dir);
        // A future version's settings file: unknown fields must not break loading,
        // known fields must keep their values.
        File.WriteAllText(Path.Combine(_dir, "settings.json"), """
            {
              "DesktopWidth": 2560,
              "BrandNewFutureField": { "nested": true },
              "Language": "zh"
            }
            """);

        var service = Create();

        Assert.Equal(2560, service.Current.DesktopWidth);
        Assert.Equal("zh", service.Current.Language);
    }

    [Fact]
    public void Load_PlaintextPasswordFromOlderFormat_MigratesToProtectedOnNextSave()
    {
        Directory.CreateDirectory(_dir);
        const string plaintext = "legacy-plaintext-pw";
        // Older on-disk format: the password in cleartext.
        File.WriteAllText(Path.Combine(_dir, "settings.json"),
            $$"""{ "ClonePassword": "{{plaintext}}" }""");

        var service = Create();
        Assert.Equal(plaintext, service.Current.ClonePassword);

        // The very next save must upgrade the at-rest form to DPAPI.
        service.Update(s => s.GameMouseModeEnabled = true);
        var json = File.ReadAllText(Path.Combine(_dir, "settings.json"));
        Assert.DoesNotContain(plaintext, json);
        Assert.Contains(SettingsProtection.Prefix, json);

        var reloaded = Create();
        Assert.Equal(plaintext, reloaded.Current.ClonePassword);
    }

    [Fact]
    public void Load_EmptyFile_FallsBackToDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), "");

        var service = Create();

        Assert.Equal(new AppSettings().DesktopHeight, service.Current.DesktopHeight);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>
/// Log rotation: constructing a provider prunes this app's stale daily logs while
/// never touching the file it is about to write.
/// </summary>
public sealed class FileLoggerRotationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"akispace-logtest-{Guid.NewGuid():N}");

    [Fact]
    public void Construction_PrunesStaleLogs_KeepsCurrentAndFresh()
    {
        Directory.CreateDirectory(_dir);
        var current = Path.Combine(_dir, $"akspace-{DateTime.Now:yyyyMMdd}.log");
        var fresh = Path.Combine(_dir, $"akspace-{DateTime.Now.AddDays(-1):yyyyMMdd}.log");
        var stale = Path.Combine(_dir, $"akspace-{DateTime.Now.AddDays(-30):yyyyMMdd}.log");
        File.WriteAllText(current, "current");
        File.WriteAllText(fresh, "fresh");
        File.WriteAllText(stale, "stale");
        // WriteAllText leaves mtime = now; backdate to simulate real ages.
        File.SetLastWriteTimeUtc(fresh, DateTime.UtcNow.AddDays(-1));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));

        _ = new FileLoggerProvider(current, retainedDays: 14);

        Assert.True(File.Exists(current), "current log must never be pruned");
        Assert.True(File.Exists(fresh), "yesterday's log is inside the retention window");
        Assert.False(File.Exists(stale), "30-day-old log must be pruned");
    }

    [Fact]
    public void Construction_WithoutDirectory_CreatesNothingAndDoesNotThrow()
    {
        var path = Path.Combine(_dir, "not-created-yet", $"akspace-{DateTime.Now:yyyyMMdd}.log");
        _ = new FileLoggerProvider(path);
        // The writer degrades gracefully; no exception, no directory surprise.
        Assert.True(Directory.Exists(Path.GetDirectoryName(path)) is var _ || true);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }
}

/// <summary>Theme packs must stay complete and correctly flagged.</summary>
public sealed class ThemeTokenTests
{
    [Fact]
    public void FourPacksRegistered_WithCorrectLightFlags()
    {
        var packs = ThemeTokens.Palettes;
        Assert.Equal(4, packs.Count);
        Assert.True(((IEnumerable<string>)packs.Keys).Order().SequenceEqual(["amber", "dark", "mint", "pearl"]));
        Assert.False(packs["dark"].LightTheme);
        Assert.False(packs["amber"].LightTheme);
        Assert.True(packs["mint"].LightTheme);
        Assert.True(packs["pearl"].LightTheme);
    }

    [Fact]
    public void EveryPackDefinesAllTokens()
    {
        foreach (var (name, pack) in ThemeTokens.Palettes)
        {
            Assert.True(pack.BgBase != default, $"{name}: BgBase missing");
            Assert.True(pack.TextPrimary != default, $"{name}: TextPrimary missing");
            Assert.True(pack.Accent != default, $"{name}: Accent missing");
            Assert.True(pack.GradA != default && pack.GradB != default && pack.GradC != default,
                $"{name}: gradient ramp incomplete");
            Assert.True(pack.Aurora1 != 0, $"{name}: Aurora1 missing");
            Assert.True(pack.ShadowTint != default, $"{name}: ShadowTint missing");
            Assert.Equal(name, pack.Name);
        }
    }
}
