using AkiSpace.Ipc;
using Xunit;

namespace AkiSpace.Tests;

/// <summary>
/// Covers <see cref="PipeClient.ReadNonceFromFile"/> — the out-of-band 32-byte
/// nonce handoff that must delete the file and never throw on a short/missing file.
/// These tests touch no global pipe state, so they need no collection guard.
/// </summary>
public sealed class PipeNonceFileTests
{
    private static string TempNoncePath()
        => Path.Combine(Path.GetTempPath(), $"akispace-nonce-{Guid.NewGuid():N}.bin");

    [Fact]
    public void ReadNonceFromFile_ReturnsNonce_And_DeletesFile_When32Bytes()
    {
        var path = TempNoncePath();
        var nonce = new byte[32];
        new Random(7).NextBytes(nonce);
        File.WriteAllBytes(path, nonce);

        var result = PipeClient.ReadNonceFromFile(path);

        Assert.NotNull(result);
        Assert.Equal(nonce, result);
        Assert.False(File.Exists(path)); // consumed for one-time use
    }

    [Fact]
    public void ReadNonceFromFile_ReturnsNull_ForMissingFile_WithoutThrowing()
    {
        var path = TempNoncePath(); // never created

        var result = PipeClient.ReadNonceFromFile(path);

        Assert.Null(result);
    }

    [Fact]
    public void ReadNonceFromFile_ReturnsNull_And_DeletesFile_WhenWrongLength()
    {
        var path = TempNoncePath();
        File.WriteAllBytes(path, new byte[31]); // one byte short

        var result = PipeClient.ReadNonceFromFile(path);

        Assert.Null(result);
        Assert.False(File.Exists(path)); // cleanup still runs in finally
    }
}
