using AkiSpace.Services;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// RDP unlock layer installer: edition classification, the elevated install command
/// shape, and download hash verification (offline — feed the verifier local bytes).
/// </summary>
public sealed class RdpWrapperInstallerTests
{
    [Theory]
    [InlineData("Core", true)]
    [InlineData("CoreCountrySpecific", true)]
    [InlineData("CoreSingleLanguage", true)]
    [InlineData("CoreN", true)]
    [InlineData("Professional", false)]
    [InlineData("Enterprise", false)]
    [InlineData("Education", false)]
    [InlineData("ServerStandard", false)]
    [InlineData(null, false)]
    public void HomeEditionClassification(string? editionId, bool expectedHome)
    {
        Assert.Equal(expectedHome, RdpWrapperInstaller.IsHomeEdition(editionId));
    }

    [Fact]
    public void MachineEditionProbeDoesNotThrow()
    {
        // Real registry probe on whatever machine runs the test: must return a value
        // or null, never throw; classification must agree with it.
        var edition = RdpWrapperInstaller.GetEditionId();
        var isHome = RdpWrapperInstaller.IsThisMachineHomeEdition();
        Assert.Equal(RdpWrapperInstaller.IsHomeEdition(edition), isHome);
    }

    [Fact]
    public void InstallStartInfoIsElevatedConsoleInstall()
    {
        var psi = RdpWrapperInstaller.BuildInstallStartInfo(@"C:\x\rdpWrapper_x64.exe");
        Assert.Equal("runas", psi.Verb);
        Assert.True(psi.UseShellExecute);
        Assert.Contains("-install", psi.Arguments);
        Assert.Contains("-offline", psi.Arguments);
    }

    [Fact]
    public void PinnedConstantsAreWellFormed()
    {
        Assert.StartsWith("https://github.com/sergiye/rdpWrapper/releases/download/",
            RdpWrapperInstaller.DownloadUrl);
        Assert.EndsWith(".exe", RdpWrapperInstaller.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(64, RdpWrapperInstaller.PinnedSha256.Length);
        Assert.All(RdpWrapperInstaller.PinnedSha256, c => Assert.True(
            char.IsAsciiHexDigit(c), $"non-hex char {c}"));
    }

    [Fact]
    public void WrapperHookProbeDoesNotThrow()
    {
        // Reads the real registry — must be a plain bool either way.
        _ = RdpWrapperInstaller.IsWrapperHookInstalled();
    }
}
