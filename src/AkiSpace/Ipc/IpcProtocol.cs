using System.Buffers.Binary;
using System.Text;

namespace AkiSpace.Ipc;

/// <summary>Payload type tags for the AkiSpace IPC protocol.</summary>
public enum IpcPayloadType : byte
{
    /// <summary>UTF-8 JSON control message.</summary>
    Utf8Json = 1,

    /// <summary>Binary batch of relative mouse samples (primary -> child).</summary>
    RelativeMouseBatch = 2,

    /// <summary>Child confirmation that it handled a batch (child -> primary).</summary>
    RelativeMouseResult = 3,
}

/// <summary>A single accumulated relative mouse movement sample.</summary>
public readonly record struct RelativeMouseSample(int DeltaX, int DeltaY, long TimestampTicks);

/// <summary>A batch of relative mouse samples sent over the pipe.</summary>
public sealed class RelativeMouseBatch
{
    public required ulong FirstSequence { get; init; }
    public required long BaseTicks { get; init; }
    public required RelativeMouseSample[] Samples { get; init; }
}

/// <summary>Child-session confirmation that a batch was handled.</summary>
public readonly record struct RelativeMouseResult(ulong LastSequence, bool Handled);

/// <summary>
/// Binary framing protocol for the AkiSpace named pipe.
/// Frame = [4-byte little-endian payload length][1-byte payload type][payload bytes]
/// </summary>
public static class IpcProtocol
{
    public const int FrameHeaderLength = 5;
    public const int MaxPayloadLength = 1 << 20; // 1 MB

    /// <summary>Writes a single frame to the stream.</summary>
    public static async Task WriteFrameAsync(Stream stream, IpcPayloadType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        if (payload.Length > MaxPayloadLength)
            throw new InvalidOperationException($"Payload too large: {payload.Length}");

        var header = new byte[FrameHeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        if (payload.Length > 0)
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads exactly one frame, or null on clean EOF.</summary>
    public static async Task<(IpcPayloadType Type, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[FrameHeaderLength];
        var read = await ReadExactlyAsync(stream, header, FrameHeaderLength, ct).ConfigureAwait(false);
        if (read == 0) return null;                 // clean EOF
        if (read < FrameHeaderLength) throw new EndOfStreamException("Truncated frame header");

        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 0 || length > MaxPayloadLength)
            throw new InvalidOperationException($"Invalid payload length {length}");

        var type = (IpcPayloadType)header[4];
        var payload = new byte[length];
        if (length > 0)
        {
            var payloadRead = await ReadExactlyAsync(stream, payload, length, ct).ConfigureAwait(false);
            if (payloadRead < length) throw new EndOfStreamException("Truncated frame payload");
        }
        return (type, payload);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        var total = 0;
        var memory = buffer.AsMemory();
        while (total < count)
        {
            var n = await stream.ReadAsync(memory[total..(count - total)], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    // ---- Payload serialization ----

    public static byte[] SerializeBatch(RelativeMouseBatch batch)
    {
        // [2-byte count][8-byte firstSequence][8-byte baseTicks]
        //   then count * (4-byte deltaX + 4-byte deltaY + 8-byte timestampTicks)
        var size = 2 + 8 + 8 + batch.Samples.Length * 16;
        var buffer = new byte[size];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, (ushort)batch.Samples.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(span[2..], batch.FirstSequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[10..], batch.BaseTicks);

        var offset = 18;
        foreach (var sample in batch.Samples)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], sample.DeltaX);
            BinaryPrimitives.WriteInt32LittleEndian(span[(offset + 4)..], sample.DeltaY);
            BinaryPrimitives.WriteInt64LittleEndian(span[(offset + 8)..], sample.TimestampTicks - batch.BaseTicks);
            offset += 16;
        }
        return buffer;
    }

    public static RelativeMouseBatch DeserializeBatch(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 18)
            throw new InvalidOperationException("Batch payload too short");
        var span = payload;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(payload);
        if (payload.Length < 18 + count * 16)
            throw new InvalidOperationException("Batch payload truncated");

        var firstSeq = BinaryPrimitives.ReadUInt64LittleEndian(payload[2..]);
        var baseTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[10..]);
        var samples = new RelativeMouseSample[count];
        var offset = 18;
        for (var i = 0; i < count; i++)
        {
            var dx = BinaryPrimitives.ReadInt32LittleEndian(payload[offset..]);
            var dy = BinaryPrimitives.ReadInt32LittleEndian(payload[(offset + 4)..]);
            var relTicks = BinaryPrimitives.ReadInt64LittleEndian(payload[(offset + 8)..]);
            samples[i] = new RelativeMouseSample(dx, dy, baseTicks + relTicks);
            offset += 16;
        }
        return new RelativeMouseBatch { FirstSequence = firstSeq, BaseTicks = baseTicks, Samples = samples };
    }

    public static byte[] SerializeResult(RelativeMouseResult result)
    {
        var buffer = new byte[9];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, result.LastSequence);
        buffer[8] = (byte)(result.Handled ? 1 : 0);
        return buffer;
    }

    public static RelativeMouseResult DeserializeResult(byte[] payload)
    {
        if (payload.Length < 9) throw new InvalidOperationException("Result payload too short");
        var lastSeq = BinaryPrimitives.ReadUInt64LittleEndian(payload);
        return new RelativeMouseResult(lastSeq, payload[8] != 0);
    }

    public static byte[] SerializeJson<T>(T obj) => Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(obj));

    public static T DeserializeJson<T>(byte[] payload) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(payload)!;
}