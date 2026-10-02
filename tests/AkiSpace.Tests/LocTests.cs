using System.Text.RegularExpressions;
using AkiSpace.Common;
using AkiSpace.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Localization catalog integrity: the two catalogs must stay key-identical, and
/// every key referenced from XAML ({loc:Loc Key}) or code (Loc.T/Loc.F) must exist.
/// A missing key renders as the raw key string in the UI — these tests make that
/// a build failure instead of a visual one.
/// </summary>
public sealed partial class LocTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AkiSpace.slnx")))
            dir = dir.Parent;
        return dir?.FullName;
    }

    private static IEnumerable<string> SourceFiles(string extension)
    {
        var root = FindRepoRoot() ?? throw new InvalidOperationException("repo root (AkiSpace.slnx) not found");
        return Directory.EnumerateFiles(Path.Combine(root, "src", "AkiSpace"), "*" + extension, SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void EnglishAndChineseCatalogsHaveIdenticalKeys()
    {
        var missingInZh = Loc.EnglishCatalog.Keys.Except(Loc.ChineseCatalog.Keys).ToList();
        var missingInEn = Loc.ChineseCatalog.Keys.Except(Loc.EnglishCatalog.Keys).ToList();
        Assert.True(missingInZh.Count == 0 && missingInEn.Count == 0,
            $"catalog keys diverge — missing in zh: [{string.Join(", ", missingInZh)}]; missing in en: [{string.Join(", ", missingInEn)}]");
    }

    [Fact]
    public void EveryXamlLocKeyExistsInBothCatalogs()
    {
        var regex = XamlLocKeyRegex();
        var unknown = new List<string>();
        foreach (var file in SourceFiles(".xaml"))
        {
            foreach (Match m in regex.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (!Loc.EnglishCatalog.ContainsKey(key) || !Loc.ChineseCatalog.ContainsKey(key))
                    unknown.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(unknown.Count == 0, $"unknown XAML loc keys: {string.Join("; ", unknown)}");
    }

    [Fact]
    public void EveryCodeLocKeyExistsInBothCatalogs()
    {
        var regex = CodeLocKeyRegex();
        var unknown = new List<string>();
        foreach (var file in SourceFiles(".cs"))
        {
            foreach (Match m in regex.Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (!Loc.EnglishCatalog.ContainsKey(key) || !Loc.ChineseCatalog.ContainsKey(key))
                    unknown.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(unknown.Count == 0, $"unknown code loc keys: {string.Join("; ", unknown)}");
    }

    [Fact]
    public void DefaultsToEnglishAndFallsBackForMissingKeys()
    {
        Loc.SetLanguage("en", persist: false);
        Assert.Equal("en", Loc.Language);
        Assert.Equal("Connect", Loc.T("Main_Connect"));
        // Unknown values fall back to English…
        Loc.SetLanguage("fr", persist: false);
        Assert.Equal("en", Loc.Language);
        // …and a genuinely missing key echoes the key instead of crashing.
        Assert.Equal("No_Such_Key", Loc.T("No_Such_Key"));
    }

    [Fact]
    public void ChineseSwitchResolvesChineseAndPersistsThroughSettings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "AkiSpaceLocTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var settings = new SettingsService(NullLogger<SettingsService>.Instance, dir);
            Loc.Initialize(settings);
            Loc.SetLanguage("zh", persist: true);
            try
            {
                Assert.Equal("zh", Loc.Language);
                Assert.Equal("连接", Loc.T("Main_Connect"));
                // Reordered placeholders work in both languages.
                Assert.Equal("连接: 已在分身（会话 7）中启动 game.exe",
                    Loc.F("Conn_Launched", "game.exe", 7U));
                var onDisk = File.ReadAllText(Path.Combine(dir, "settings.json"));
                Assert.Contains("\"Language\": \"zh\"", onDisk);
                // A fresh service instance restores the persisted language.
                Loc.Initialize(new SettingsService(NullLogger<SettingsService>.Instance, dir));
                Assert.Equal("zh", Loc.Language);
            }
            finally
            {
                Loc.SetLanguage("en", persist: false);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [GeneratedRegex(@"\{loc:Loc\s+([A-Za-z0-9_]+)")]
    private static partial Regex XamlLocKeyRegex();

    // Literal keys passed to the status-key plumbing (resolved later via Loc.T(_statusKey)).
    [GeneratedRegex(@"(?:Loc\.[TF]|SetStatusText|ResetConnectUi)\(\s*""([A-Za-z0-9_]+)""")]
    private static partial Regex CodeLocKeyRegex();
}
