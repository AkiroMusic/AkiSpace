using System.Runtime.InteropServices;
using AkiSpace.Services;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Regression tests for the firewall rule attribute validators: AkiSpace blocks
/// ONLY the configured RDP port, so a Correct rule must carry an EXACT single-port
/// scope — substring matching (round 1) blessed a "13389" rule for port 3389, and
/// containment matching (round 3) blessed wildcard/range/mixed rules that block
/// far more than the design intends. Also covers the COM failure classification:
/// only FWP_E_NOT_FOUND may read as Missing; everything else must fail closed.
/// </summary>
public sealed class FirewallRuleValidationTests
{
    // ---- LocalPortsExactly ----

    [Theory]
    [InlineData("3389", 3389, true)]
    [InlineData(" 3389 ", 3389, true)]      // whitespace tolerated, scope unchanged
    [InlineData("13389", 3389, false)]      // the original substring false-positive
    [InlineData("33890", 3389, false)]
    [InlineData("3389,3390", 3389, false)]  // blocks MORE than the design intends
    [InlineData("13389,3389", 3389, false)]
    [InlineData("3300-3500", 3389, false)]  // a range blocks unrelated ports
    [InlineData("*", 3389, false)]          // wildcard blocks every inbound TCP port
    [InlineData("", 3389, false)]
    [InlineData(null, 3389, false)]
    [InlineData("abc", 3389, false)]        // malformed scope is not "correct"
    [InlineData("3389,abc", 3389, false)]   // malformed sibling poisons the scope
    [InlineData("3389-3300", 3389, false)]  // inverted range is malformed
    [InlineData("-3389", 3389, false)]
    [InlineData("3389-", 3389, false)]
    [InlineData(" , ,", 3389, false)]       // token list that parses to nothing
    [InlineData("03389", 3389, true)]       // numeric equality, not string equality
    public void LocalPortsExactly_RequiresExactSinglePortScope(string? ports, int port, bool expected)
        => Assert.Equal(expected, EnvironmentVerifier.LocalPortsExactly(ports, port));

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

    // ---- COM failure classification ----

    [Fact]
    public void RuleLookup_NotFoundHresult_ClassifiedMissing()
    {
        var ex = new COMException("The object does not exist", EnvironmentVerifier.FwpENotFound);
        Assert.Equal(EnvironmentVerifier.FirewallRuleState.Missing, EnvironmentVerifier.ClassifyRuleLookupFailure(ex));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005))]  // E_ACCESSDENIED
    [InlineData(unchecked((int)0x800706BA))]  // RPC_S_SERVER_UNAVAILABLE
    [InlineData(unchecked((int)0x80004005))]  // E_FAIL
    public void RuleLookup_AnyOtherComFailure_ClassifiedUnknown_FailClosed(int hresult)
    {
        var ex = new COMException("boom", hresult);
        Assert.Equal(EnvironmentVerifier.FirewallRuleState.Unknown, EnvironmentVerifier.ClassifyRuleLookupFailure(ex));
    }

    [Fact]
    public void FwpENotFound_IsTheWindowsSdkValue()
        => Assert.Equal(unchecked((int)0x8032000F), EnvironmentVerifier.FwpENotFound);

    // ---- FirewallRuleState / profile constant ----

    [Fact]
    public void NetFwProfile2All_IsWindowsSdkValue()
        => Assert.Equal(unchecked((int)0x7FFFFFFF), EnvironmentVerifier.NetFwProfile2All);
}
