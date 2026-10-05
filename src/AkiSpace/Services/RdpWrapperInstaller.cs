using System.Diagnostics;
using System.Security.Cryptography;
using AkiSpace.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace AkiSpace.Services;

/// <summary>
/// One-click installer for the RDP unlock layer on Home editions: downloads the
/// pinned sergiye/rdpWrapper build from its official GitHub release, verifies the
/// SHA-256 recorded at pin time, then launches it elevated with `-install -offline`.
/// The upstream installer is fully self-contained (stops TermService, installs
/// TermWrap, sets ServiceDll, restarts the service, adds the Defender folder
/// exclusion) — one UAC prompt, no extra logic on our side.
///
/// Pinning: the URL and hash below are pinned to release 2.15; a hash mismatch
/// aborts before anything is executed. Updating the pin = new URL + new hash.
/// </summary>
public sealed class RdpWrapperInstaller
{
    public const string PinnedVersion = "2.15";
    public const string DownloadUrl =
        "https://github.com/sergiye/rdpWrapper/releases/download/2.15/rdpWrapper_x64.exe";
    public const string PinnedSha256 =
        "bcd0286dbf22bf38fa18010598a19fff476b82150c0199741eb3af37b699e048";
    public const string DownloadedFileName = "rdpWrapper_x64.exe";

    private static readonly HashSet<string> HomeEditionIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "Core", "CoreCountrySpecific", "CoreSingleLanguage", "CoreN", "Starter",
    };

    private readonly ILogger<RdpWrapperInstaller> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>Test seam: overrides where the edition id is read from.</summary>
    internal static Func<string?> EditionIdProvider { get; set; } = () =>
        Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
            "EditionID", null) as string;

    public RdpWrapperInstaller(ILogger<RdpWrapperInstaller> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Windows edition id (e.g. CoreCountrySpecific), or null if unreadable.</summary>
    public static string? GetEditionId() => EditionIdProvider();

    /// <summary>True for Home-family editions where standard RDP needs an unlock layer.</summary>
    public static bool IsHomeEdition(string? editionId) =>
        editionId is not null && HomeEditionIds.Contains(editionId);

    /// <summary>Home edition of THIS machine (registry), false when unknown — fail toward
    /// "not Home" so Pro users are never blocked by Home-only guidance.</summary>
    public static bool IsThisMachineHomeEdition() => IsHomeEdition(GetEditionId());

    /// <summary>Instant registry probe: is a wrapper hook (TermWrap/rdpwrap) set as TermService's ServiceDll?</summary>
    public static bool IsWrapperHookInstalled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = baseKey.OpenSubKey(RegistryKeys.TermServiceParameters);
            var serviceDll = k?.GetValue(RegistryKeys.ServiceDll) as string;
            return !string.IsNullOrEmpty(serviceDll)
                   && (serviceDll.IndexOf("TermWrap", StringComparison.OrdinalIgnoreCase) >= 0
                       || serviceDll.IndexOf("rdpwrap", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns a locally-cached, hash-verified wrapper executable. Reuses a previous
    /// download when its hash still matches; re-downloads otherwise. Throws on any
    /// failure (network/hash) — the caller surfaces the message.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(CancellationToken ct = default)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AkiSpace", "downloads");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, DownloadedFileName);

        if (File.Exists(path) && await HashMatchesAsync(path, ct).ConfigureAwait(false))
        {
            _logger.LogInformation("Reusing cached rdpWrapper {Version} at {Path}", PinnedVersion, path);
            return path;
        }

        _logger.LogInformation("Downloading rdpWrapper {Version} from {Url}", PinnedVersion, DownloadUrl);
        using (var response = await _httpClient.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, ct).ConfigureAwait(false);
        }

        if (!await HashMatchesAsync(path, ct).ConfigureAwait(false))
        {
            try { File.Delete(path); } catch { /* best-effort */ }
            throw new InvalidOperationException("Downloaded rdpWrapper failed SHA-256 verification — aborted, nothing was executed");
        }
        _logger.LogInformation("rdpWrapper downloaded and verified ({Path})", path);
        return path;
    }

    private static async Task<bool> HashMatchesAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var hash = await sha.ComputeHashAsync(fs, ct).ConfigureAwait(false);
            var hex = Convert.ToHexString(hash);
            return hex.Equals(PinnedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The elevated install launch: upstream console mode (-install) with the update
    /// check disabled (-offline) for deterministic, pinned behavior. The upstream
    /// installer itself stops/starts TermService — one UAC prompt covers everything.
    /// Public so SelfTest can assert the shape.
    /// </summary>
    public static ProcessStartInfo BuildInstallStartInfo(string exePath) =>
        new(exePath, "-install -offline")
        {
            UseShellExecute = true,
            Verb = "runas",
        };
}
