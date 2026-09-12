using System.Text;
using AkiSpace.Ipc;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Covers the locked behaviors of the binary IPC framing protocol
/// (src/AkiSpace/Ipc/IpcProtocol.cs) found untested by the audit (#15).
/// </summary>
public sealed class IpcProtocolTests
{
    // ---- 1. Batch round-trip ----

    [Fact]
    public void BatchRoundTrip_PreservesSequence_BaseTicks_And_Every_Sample()
    {
        const long baseTicks = 638_000_000_000_000_000L;
        var batch = new RelativeMouseBatch
        {
            FirstSequence = 42,
            BaseTicks = baseTicks,
            Samples =
            [
                new RelativeMouseSample(3, -5, baseTicks + 1_000),
                new RelativeMouseSample(-120, 77, baseTicks + 10_000),
                // Timestamp below BaseTicks must survive the delta-from-base encoding.
                new RelativeMouseSample(0, 0, baseTicks - 500),
                new RelativeMouseSample(int.MinValue, int.MaxValue, baseTicks + long.MaxValue / 2),
            ],
        };

        var payload = IpcProtocol.SerializeBatch(batch);
        var restored = IpcProtocol.DeserializeBatch(payload);

        Assert.Equal(batch.FirstSequence, restored.FirstSequence);
        Assert.Equal(batch.BaseTicks, restored.BaseTicks);
        Assert.Equal(batch.Samples.Length, restored.Samples.Length);
        for (var i = 0; i < batch.Samples.Length; i++)
        {
            Assert.Equal(batch.Samples[i].DeltaX, restored.Samples[i].DeltaX);
            Assert.Equal(batch.Samples[i].DeltaY, restored.Samples[i].DeltaY);
            // TimestampTicks is stored as delta-from-BaseTicks and restored as
            // BaseTicks + rel — the reconstructed absolute ticks must equal the originals.
            Assert.Equal(batch.Samples[i].TimestampTicks, restored.Samples[i].TimestampTicks);
        }
    }

    [Fact]
    public void BatchRoundTrip_EmptySamples_PreservesHeaderFields()
    {
        var batch = new RelativeMouseBatch { FirstSequence = ulong.MaxValue, BaseTicks = -7, Samples = [] };

        var restored = IpcProtocol.DeserializeBatch(IpcProtocol.SerializeBatch(batch));

        Assert.Equal(batch.FirstSequence, restored.FirstSequence);
        Assert.Equal(batch.BaseTicks, restored.BaseTicks);
        Assert.Empty(restored.Samples);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(0)]
    public void DeserializeBatch_RejectsPayloadShorterThanHeader(int length)
    {
        Assert.Throws<InvalidOperationException>(() => IpcProtocol.DeserializeBatch(new byte[length]));
    }

    [Fact]
    public void DeserializeBatch_RejectsTruncatedSampleSection()
    {
        // Valid 18-byte header claiming 2 samples but carrying no sample bytes.
        var payload = new byte[18];
        payload[0] = 2;
        Assert.Throws<InvalidOperationException>(() => IpcProtocol.DeserializeBatch(payload));
    }

    // ---- 2. Result round-trip ----

    [Theory]
    [InlineData(0UL, true)]
    [InlineData(42UL, false)]
    [InlineData(ulong.MaxValue, true)]
    public void ResultRoundTrip_PreservesLastSequence_And_Handled(ulong lastSequence, bool handled)
    {
        var result = new RelativeMouseResult(lastSequence, handled);

        var restored = IpcProtocol.DeserializeResult(IpcProtocol.SerializeResult(result));

        Assert.Equal(lastSequence, restored.LastSequence);
        Assert.Equal(handled, restored.Handled);
    }

    [Fact]
    public void DeserializeResult_RejectsShortPayload()
    {
        Assert.Throws<InvalidOperationException>(() => IpcProtocol.DeserializeResult(new byte[8]));
    }

    // ---- 3. Frame round-trip ----

    [Theory]
    [InlineData(IpcPayloadType.Utf8Json)]
    [InlineData(IpcPayloadType.RelativeMouseBatch)]
    [InlineData(IpcPayloadType.RelativeMouseResult)]
    [InlineData(IpcPayloadType.Handshake)]
    public async Task FrameRoundTrip_PreservesType_And_Payload(IpcPayloadType type)
    {
        var payload = Encoding.UTF8.GetBytes("frame-payload-for-test");
        var stream = new MemoryStream();

        await IpcProtocol.WriteFrameAsync(stream, type, payload);
        stream.Position = 0;
        var frame = await IpcProtocol.ReadFrameAsync(stream);

        Assert.NotNull(frame);
        Assert.Equal(type, frame.Value.Type);
        Assert.Equal(payload, frame.Value.Payload);
    }

    [Fact]
    public async Task FrameRoundTrip_EmptyPayload_Works()
    {
        var stream = new MemoryStream();
        await IpcProtocol.WriteFrameAsync(stream, IpcPayloadType.Utf8Json, ReadOnlyMemory<byte>.Empty);
        stream.Position = 0;
        var frame = await IpcProtocol.ReadFrameAsync(stream);

        Assert.NotNull(frame);
        Assert.Equal(IpcPayloadType.Utf8Json, frame.Value.Type);
        Assert.Empty(frame.Value.Payload);
    }

    [Fact]
    public async Task WriteFrameAsync_RejectsOversizedPayload()
    {
        var tooBig = new byte[IpcProtocol.MaxPayloadLength + 1];
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => IpcProtocol.WriteFrameAsync(new MemoryStream(), IpcPayloadType.Utf8Json, tooBig));
    }

    // ---- 4. Clean EOF ----

    [Fact]
    public async Task ReadFrameAsync_ReturnsNull_OnEmptyStream()
    {
        var frame = await IpcProtocol.ReadFrameAsync(new MemoryStream());

        Assert.Null(frame);
    }

    [Fact]
    public async Task ReadFrameAsync_ReturnsNull_AfterLastFrameConsumed()
    {
        var stream = new MemoryStream();
        await IpcProtocol.WriteFrameAsync(stream, IpcPayloadType.Utf8Json, new byte[] { 1, 2, 3 });
        stream.Position = 0;

        Assert.NotNull(await IpcProtocol.ReadFrameAsync(stream));   // the written frame
        Assert.Null(await IpcProtocol.ReadFrameAsync(stream));      // then clean EOF
    }

    // ---- 5. Truncation ----

    [Fact]
    public async Task ReadFrameAsync_Throws_OnTruncatedHeader()
    {
        var stream = new MemoryStream(new byte[] { 9, 9, 9 }); // only 3 of 5 header bytes

        await Assert.ThrowsAsync<EndOfStreamException>(() => IpcProtocol.ReadFrameAsync(stream));
    }

    [Fact]
    public async Task ReadFrameAsync_Throws_OnTruncatedPayload()
    {
        // Header claims a 10-byte payload; only 4 bytes follow.
        var bytes = new byte[IpcProtocol.FrameHeaderLength + 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), 10);
        bytes[4] = (byte)IpcPayloadType.RelativeMouseBatch;

        await Assert.ThrowsAsync<EndOfStreamException>(
            () => IpcProtocol.ReadFrameAsync(new MemoryStream(bytes)));
    }

    // ---- 6. Invalid lengths ----

    [Fact]
    public async Task ReadFrameAsync_Throws_OnOversizedLengthClaim()
    {
        var bytes = new byte[IpcProtocol.FrameHeaderLength];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), IpcProtocol.MaxPayloadLength + 1);
        bytes[4] = (byte)IpcPayloadType.Utf8Json;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => IpcProtocol.ReadFrameAsync(new MemoryStream(bytes)));
    }

    [Fact]
    public async Task ReadFrameAsync_Throws_OnNegativeLengthClaim()
    {
        var bytes = new byte[IpcProtocol.FrameHeaderLength];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), -1);
        bytes[4] = (byte)IpcPayloadType.Utf8Json;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => IpcProtocol.ReadFrameAsync(new MemoryStream(bytes)));
    }

    // ---- 7. Nonce validation ----

    [Fact]
    public void DeserializeNonce_ReturnsArray_OnLength32()
    {
        var nonce = new byte[32];
        new Random(1234).NextBytes(nonce);

        Assert.Equal(nonce, IpcProtocol.DeserializeNonce(nonce));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void DeserializeNonce_Throws_OnWrongLength(int length)
    {
        Assert.Throws<InvalidOperationException>(() => IpcProtocol.DeserializeNonce(new byte[length]));
    }

    // ---- JSON helpers (DeserializeJson null-hardening, post-fix state) ----

    [Fact]
    public void JsonRoundTrip_PreservesValue()
    {
        var payload = IpcProtocol.SerializeJson(new ProbeDto { Name = "probe", Count = 7 });

        var restored = IpcProtocol.DeserializeJson<ProbeDto>(payload);

        Assert.Equal("probe", restored.Name);
        Assert.Equal(7, restored.Count);
    }

    [Fact]
    public void DeserializeJson_Throws_OnJsonNull()
    {
        // The old code returned null via `!`; post-fix it must throw instead.
        Assert.Throws<InvalidOperationException>(
            () => IpcProtocol.DeserializeJson<ProbeDto>("null"u8.ToArray()));
    }

    private sealed class ProbeDto
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }
}
