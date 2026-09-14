namespace AkiSpace.Common;

/// <summary>
/// Centralized registry path constants. Previously repeated as inline strings
/// in EnvironmentVerifier (4x) and ChildSessionManager (2x).
/// </summary>
internal static class RegistryKeys
{
    public const string TerminalServer = @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    public const string RdpTcp = TerminalServer + @"\WinStations\RDP-Tcp";
    public const string TermServiceParameters = @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";

    public const string FDenyTSConnections = "fDenyTSConnections";
    public const string FSingleSessionPerUser = "fSingleSessionPerUser";
    public const string StartRCM = "StartRCM";
    public const string SecurityLayer = "SecurityLayer";
    public const string MinEncryptionLevel = "MinEncryptionLevel";
    public const string UserAuthentication = "UserAuthentication";
    public const string PortNumber = "PortNumber";
    public const string ServiceDll = "ServiceDll";
}
