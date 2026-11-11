using AkiSpace.Controls;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Regression tests for the connect-attempt ownership gate (round-3 review P1-01):
/// a superseded attempt's cleanup (its finally block) must never clear the
/// connecting flag a NEWER attempt has already set — otherwise a third Connect
/// passes the re-entrancy guard and two connect flows drive the same ActiveX.
/// RdpActiveXHost itself cannot be instantiated in unit tests (AxHost pulls in
/// the MSTSC COM control), so the ownership rule lives in ConnectAttemptGate.
/// </summary>
public sealed class ConnectAttemptGateTests
{
    [Fact]
    public void NormalLifecycle_BeginEnd_ClearsFlag()
    {
        var gate = new ConnectAttemptGate();
        Assert.False(gate.IsConnecting);

        var gen = gate.Begin();
        Assert.True(gate.IsConnecting);

        gate.End(gen);
        Assert.False(gate.IsConnecting);
    }

    [Fact]
    public void StaleAttemptCleanup_DoesNotClearNewerAttempt()
    {
        var gate = new ConnectAttemptGate();

        // Connect A begins; user disconnects (Invalidate) and immediately reconnects
        // (B begins) before A's task observes the cancellation.
        var genA = gate.Begin();
        Assert.True(gate.IsConnecting);
        gate.Invalidate();          // DisconnectSession
        var genB = gate.Begin();    // Connect B

        // A's finally runs late — it must NOT clear B's in-flight flag.
        gate.End(genA);
        Assert.True(gate.IsConnecting,
            "a superseded attempt's cleanup cleared the NEW attempt's connecting flag");

        gate.End(genB);
        Assert.False(gate.IsConnecting);
    }

    [Fact]
    public void Invalidate_AloneClearsFlag_AndRevokesOwnership()
    {
        var gate = new ConnectAttemptGate();
        var gen = gate.Begin();
        gate.Invalidate();

        Assert.False(gate.IsConnecting);
        gate.End(gen); // late cleanup after invalidation — still a no-op
        Assert.False(gate.IsConnecting);
    }

    [Fact]
    public void SuccessiveConnects_EachOwnsItsGeneration()
    {
        var gate = new ConnectAttemptGate();

        var gen1 = gate.Begin();
        gate.End(gen1);
        Assert.False(gate.IsConnecting);

        var gen2 = gate.Begin();
        Assert.True(gate.IsConnecting);
        gate.End(gen2);
        Assert.False(gate.IsConnecting);

        // A stale End for an old generation after a fresh Begin stays a no-op.
        var gen3 = gate.Begin();
        gate.End(gen1);
        Assert.True(gate.IsConnecting);
        gate.End(gen3);
        Assert.False(gate.IsConnecting);
    }
}
