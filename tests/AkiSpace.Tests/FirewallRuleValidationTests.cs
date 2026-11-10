using AkiSpace.Services;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Regression tests for the firewall rule attribute validators (round-2 review
/// P1-03/P2-03): port scope strings must be parsed numerically (substring
/// matching blessed a "13389" rule for port 3389), remote addresses must be
/// unrestricted, and the state enum must fail closed on Unknown.
/// </summary>
public sealed class FirewallRuleValidationTests
{
    // ---- LocalPortsMatch ----

    [Theory]
    [InlineData("3389", 3389, true)]
    [InlineData("13389", 3389, false)]      // substring false-positive before the fix
    [InlineData("33890", 3389, false)]
    [InlineData("13389,3389", 3389, true)]
    [InlineData("13389, 3389 ,9000", 3389, true)]   // spaces around list items
    [InlineData("13389,9000", 3389, false)]
    [InlineData("3300-3500", 3389, true)]
    [InlineData("3300-3500", 3501, false)]
    [InlineData("3200-3300", 3389, false)]
    [InlineData("13389,3300-3500,9000", 3389, true)]
    [InlineData("*", 3389, true)]
    [InlineData(" * ", 12345, true)]
    [InlineData("", 3389, false)]
    [InlineData(null, 3389, false)]
    [InlineData("abc", 3389, false)]        // malformed token never matches
    [InlineData("3389,abc", 3389, true)]    // malformed sibling doesn't poison a valid one
    [InlineData("3389-3300", 3389, false)]  // inverted range is malformed
    [InlineData("-3389", 3389, false)]      // leading dash: empty low bound
    [InlineData("3389-", 3389, false)]      // empty high bound
    [InlineData("3500-3300,13389", 3389, false)]
    public void LocalPortsMatch_ParsesScopesNumerically(string? ports, int port, bool expected)
        => Assert.Equal(expected, EnvironmentVerifier.LocalPortsMatch(ports, port));

    [Fact]
    public void LocalPortsMatch_BoundaryInclusive()
    {
        Assert.True(EnvironmentVerifier.LocalPortsMatch("3389-4000", 4000));
        Assert.True(EnvironmentVerifier.LocalPortsMatch("3389-4000", 3389));
    }

    // ---- RemoteAddressesAreAny ----

    [Theory]
    [InlineData("*", true)]
    [InlineData(" * ", true)]
    [InlineData("127.0.0.1", false)]        // narrowed scope breaks the boundary
    [InlineData("10.0.0.0/8,192.168.1.0/24", false)]
    [InlineData("LocalSubnet", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void RemoteAddressesAreAny_OnlyWildcardIsUnrestricted(string? remote, bool expected)
        => Assert.Equal(expected, EnvironmentVerifier.RemoteAddressesAreAny(remote));

    // ---- FirewallRuleState / profile constant ----

    [Fact]
    public void NetFwProfile2All_IsWindowsSdkValue()
        => Assert.Equal(unchecked((int)0x7FFFFFFF), EnvironmentVerifier.NetFwProfile2All);
}
