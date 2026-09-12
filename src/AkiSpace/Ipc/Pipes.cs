using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Ipc;

/// <summary>Well-known pipe names for the AkiSpace IPC.</summary>
/// <remarks>
/// These are BARE pipe names, NOT full paths. Both <see cref="NamedPipeServerStream"/>
/// and <see cref="NamedPipeClientStream"/> prepend <c>\\.\pipe\</c> themselves, so the
/// server and client must pass the SAME bare string here to reach each other. (Passing a
/// full path on one side and a bare name on the other registers two different pipes and
/// they never connect.)
/// </remarks>
public static class PipeNames
{
    /// <summary>Primary -> child direction (mouse batches).</summary>
    public const string MouseForward = "AkiSpace_MouseForward";

    /// <summary>Child -> primary direction (results/confirmations).</summary>
    public const string MouseResult = "AkiSpace_MouseResult";

    /// <summary>Control channel (JSON control messages, either direction).</summary>
    public const string Control = "AkiSpace_Control";
}

/// <summary>
/// Named-pipe server (primary session side). Listens for a child-session
/// client connection and exposes typed frame events. Verifies a nonce
/// handshake so only the agent launched with the correct nonce can connect.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly ILogger<PipeServer> _logger;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _generation;

    /// <summary>The nonce the connecting agent must present (set via SetNonce).</summary>
    private byte[]? _expectedNonce;

    /// <summary>
    /// Optional extra SID granted connect access to the pipe — the clone account when the
    /// replay agent runs under a DIFFERENT Windows identity (standard-RDP mode). Without it
    /// the DACL (primary user + SYSTEM) would deny the cross-user agent.
    /// </summary>
    private SecurityIdentifier? _allowedClientSid;

    /// <summary>True when a verified client is connected.</summary>
    public bool IsClientConnected { get; private set; }

    /// <summary>
    /// Bare pipe name this server registers (both NamedPipeServerStream and
    /// NamedPipeClientStream prepend <c>\\.\pipe\</c> themselves). Defaults to the
    /// production well-known name; set to a unique value (before Start) to isolate
    /// concurrent instances — e.g. per-test in the handshake suite, which shares a
    /// machine-global name and otherwise races on asynchronously-released OS pipe
    /// instances between tests.
    /// </summary>
    public string PipeName { get; set; } = PipeNames.MouseForward;

    public PipeServer(ILogger<PipeServer> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised when a client connects and passes the nonce handshake.</summary>
    public event Action<PipeConnection>? ClientConnected;

    /// <summary>
    /// Grants the named-pipe DACL an additional client SID (the clone account). Call with
    /// null (default) when the agent runs as the same user as the server. Must be called
    /// before Start().
    /// </summary>
    public void SetAllowedClientSid(SecurityIdentifier? sid) => _allowedClientSid = sid;

    /// <summary>Sets the expected handshake nonce. Must be called before Start().</summary>
    public void SetNonce(byte[] nonce) => _expectedNonce = nonce;

    /// <summary>Starts listening for a single child-session connection.</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_cts != null) return;  // already started
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var myGen = ++_generation;
            _listenTask = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_lifecycleGate)
                    {
                        if (_generation != myGen) break;
                    }
                    if (token.IsCancellationRequested) break;

                    NamedPipeServerStream? stream = null;
                    try
                    {
                        stream = CreateServerStream();

                        _logger.LogInformation("Pipe server listening on {Pipe}", PipeName);
                        await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
                        _logger.LogInformation("Pipe client connected, verifying handshake");

                        // Verify nonce handshake before accepting the connection.
                        var verified = await VerifyHandshakeAsync(stream, token).ConfigureAwait(false);
                        if (!verified)
                        {
                            _logger.LogWarning("Pipe handshake failed; disconnecting client");
                            stream.Dispose();
                            stream = null;
                            continue;
                        }

                        _logger.LogInformation("Pipe handshake verified — agent connected");
                        IsClientConnected = true;
                        var connection = new PipeConnection(stream, _logger);
                        stream = null;
                        connection.Disconnected += () => IsClientConnected = false;
                        ClientConnected?.Invoke(connection);
                        await connection.RunAsync(token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning(ex, "Pipe server I/O error; continuing to listen");
                        try { await Task.Delay(500, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Pipe server error; continuing to listen");
                        try { await Task.Delay(1000, token).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
                    }
                    finally
                    {
                        stream?.Dispose();
                    }
                }
            });
        }
    }

    /// <summary>
    /// Creates the listening server stream with a restrictive DACL (current user + SYSTEM
    /// only). If custom-ACL construction fails on an unexpected platform/identity shape, we
    /// do NOT fall back to the permissive default DACL (which would silently undo the
    /// hardening) — instead we fall back to <see cref="PipeOptions.CurrentUserOnly"/>, a
    /// safe built-in default that still restricts connects to the same Windows identity, and
    /// log at Error so the degradation is never silent. The fail-closed nonce handshake
    /// gates connections in both paths.
    /// </summary>
    private NamedPipeServerStream CreateServerStream()
    {
        var name = PipeName; // snapshot so the ACL path and fallback path use the same name
        try
        {
            var security = new PipeSecurity();
            // Current interactive user (server process identity) — ReadWrite so it can operate the pipe.
            var userSid = WindowsIdentity.GetCurrent().User!; // non-null for an interactive logon token
            security.AddAccessRule(new PipeAccessRule(
                userSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // LocalSystem — ReadWrite.
            security.AddAccessRule(new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.ReadWrite, AccessControlType.Allow));
            // The clone account (standard-RDP mode): the agent runs under a different user
            // identity, so it needs its own connect grant. Absent for same-user child sessions.
            if (_allowedClientSid is { } clientSid)
            {
                security.AddAccessRule(new PipeAccessRule(
                    clientSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous,
                4096,
                4096,
                security);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build pipe ACL; falling back to CurrentUserOnly (same-user restricted), NOT the open default DACL");
            return new NamedPipeServerStream(
                name,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                4096,
                4096);
        }
    }

    private async Task<bool> VerifyHandshakeAsync(Stream stream, CancellationToken ct)
    {
        if (_expectedNonce is not { Length: 32 })
        {
            _logger.LogWarning("No nonce configured; rejecting connection (fail-closed)");
            return false;
        }

        try
        {
            // Read the first frame — it must be a Handshake frame with the matching nonce.
            var frame = await IpcProtocol.ReadFrameAsync(stream, ct).ConfigureAwait(false);
            if (frame is null) return false;
            var (type, payload) = frame.Value;
            if (type != IpcPayloadType.Handshake) return false;
            var receivedNonce = IpcProtocol.DeserializeNonce(payload);
            if (!CryptographicOperations.FixedTimeEquals(receivedNonce, _expectedNonce))
            {
                _logger.LogWarning("Pipe nonce mismatch — rejecting connection");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Handshake verification failed");
            return false;
        }
    }

    /// <summary>Stops the listener so a fresh Start() can be called again. Safe to call multiple times.</summary>
    public async Task StopAsync()
    {
        Task? waitFor;
        lock (_lifecycleGate)
        {
            if (_cts == null) return;
            _cts.Cancel();
            waitFor = _listenTask;
            _cts = null;
            _listenTask = null;
        }
        if (waitFor != null)
        {
            try { await waitFor.ConfigureAwait(false); } catch { /* ignore */ }
        }
        _logger.LogInformation("Pipe server stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Named-pipe client (child session side). Connects to the primary session's
/// pipe and sends mouse batches / receives results.
/// </summary>
public sealed class PipeClient : IAsyncDisposable
{
    private readonly ILogger<PipeClient> _logger;
    private PipeConnection? _connection;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    /// <summary>The nonce to present during handshake (set via SetNonce).</summary>
    private byte[]? _nonce;

    public PipeClient(ILogger<PipeClient> logger)
    {
        _logger = logger;
    }

    /// <summary>Sets the nonce for handshake authentication.</summary>
    public void SetNonce(byte[] nonce) => _nonce = nonce;

    /// <summary>
    /// Reads the 32-byte handshake nonce from the out-of-band file written by the primary
    /// (passed to the agent as <c>--nonce-file &lt;path&gt;</c> instead of on the command
    /// line, which leaks via Task Scheduler task XML and the PEB), then deletes the file.
    /// Returns null when the file is missing/unreadable or does not hold exactly 32 bytes.
    /// </summary>
    public static byte[]? ReadNonceFromFile(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            return bytes.Length == 32 ? bytes : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>Raised when a batch frame arrives from the primary.</summary>
    public event Action<RelativeMouseBatch>? BatchReceived;

    /// <summary>Raised when a result frame arrives from the child.</summary>
    public event Action<RelativeMouseResult>? ResultReceived;

    /// <summary>Whether the pipe is currently connected.</summary>
    public bool IsConnected => _connection?.IsConnected == true;

    /// <summary>Connects to the primary's pipe, retrying until connected or cancelled.</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _connectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

            while (!linked.IsCancellationRequested)
            {
                try
                {
                    var stream = new NamedPipeClientStream(
                        ".", PipeNames.MouseForward,
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await stream.ConnectAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
                    _logger.LogInformation("Connected to primary pipe, sending handshake");

                    // Send handshake nonce before anything else. The server is fail-closed:
                    // a missing nonce means it will reject us, so log loudly rather than
                    // connecting silently and being dropped.
                    if (_nonce is { Length: 32 })
                    {
                        await IpcProtocol.WriteFrameAsync(stream, IpcPayloadType.Handshake, _nonce, linked.Token).ConfigureAwait(false);
                        _logger.LogInformation("Handshake nonce sent");
                    }
                    else
                    {
                        _logger.LogError("No handshake nonce set; the fail-closed server will reject this connection");
                    }

                    _connection = new PipeConnection(stream, _logger);
                    _connection.BatchReceived += b => BatchReceived?.Invoke(b);
                    _connection.ResultReceived += r => ResultReceived?.Invoke(r);
                    _ = _connection.RunAsync(_cts.Token);
                    return;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Pipe connect retry: {Message}", ex.Message);
                    try { await Task.Delay(1000, linked.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>Sends a batch to the primary (for the child replay path; normally the server sends batches).</summary>
    public async Task SendBatchAsync(RelativeMouseBatch batch, CancellationToken ct = default)
    {
        if (_connection == null || !_connection.IsConnected)
        {
            _logger.LogDebug("SendBatchAsync skipped: pipe not connected");
            return;
        }
        await _connection.SendAsync(IpcPayloadType.RelativeMouseBatch, IpcProtocol.SerializeBatch(batch), ct).ConfigureAwait(false);
    }

    /// <summary>Sends a result back to the primary.</summary>
    public async Task SendResultAsync(RelativeMouseResult result, CancellationToken ct = default)
    {
        if (_connection == null || !_connection.IsConnected) return;
        await _connection.SendAsync(IpcPayloadType.RelativeMouseResult, IpcProtocol.SerializeResult(result), ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_connection != null) await _connection.DisposeAsync().ConfigureAwait(false);
        _connectGate.Dispose();
        // RunAsync may still hold the token briefly after Cancel(); disposing the CTS
        // while a consumer observes the token can race. Dispose defensively.
        try { _cts.Dispose(); } catch (ObjectDisposedException) { /* already disposed */ }
    }
}

/// <summary>
/// A single connected pipe: runs the read loop and dispatches typed frames.
/// </summary>
public sealed class PipeConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    public PipeConnection(Stream stream, ILogger logger)
    {
        _stream = stream;
        _logger = logger;
    }

    public bool IsConnected => Volatile.Read(ref _disposed) == 0;

    public event Action<RelativeMouseBatch>? BatchReceived;
    public event Action<RelativeMouseResult>? ResultReceived;
    public event Action<string>? ControlMessageReceived;
    public event Action? Disconnected;

    public async Task RunAsync(CancellationToken ct = default)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await IpcProtocol.ReadFrameAsync(_stream, ct).ConfigureAwait(false);
                if (frame == null) break; // EOF
                var (type, payload) = frame.Value;
                try
                {
                    switch (type)
                    {
                        case IpcPayloadType.RelativeMouseBatch:
                            BatchReceived?.Invoke(IpcProtocol.DeserializeBatch(payload));
                            break;
                        case IpcPayloadType.RelativeMouseResult:
                            ResultReceived?.Invoke(IpcProtocol.DeserializeResult(payload));
                            break;
                        case IpcPayloadType.Utf8Json:
                            ControlMessageReceived?.Invoke(System.Text.Encoding.UTF8.GetString(payload));
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // A malformed frame must NOT kill the entire read loop.
                    // Log and continue reading; otherwise a single bad payload
                    // from a misbehaving peer would take the whole pipe down.
                    _logger.LogWarning(ex, "Bad frame (type={Type}, {Bytes} bytes); dropping", type, payload.Length);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { _logger.LogDebug("Pipe read ended: {Message}", ex.Message); }
        catch (Exception ex) { _logger.LogWarning(ex, "Pipe read loop error"); }
        finally
        {
            Volatile.Write(ref _disposed, 1);
            Disconnected?.Invoke();
        }
    }

    public async Task SendAsync(IpcPayloadType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await IpcProtocol.WriteFrameAsync(_stream, type, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        _writeGate.Dispose();
    }
}