using System.IO.Pipes;
using Microsoft.Extensions.Logging;

namespace AkiSpace.Ipc;

/// <summary>Well-known pipe names for the AkiSpace IPC.</summary>
public static class PipeNames
{
    /// <summary>Primary -> child direction (mouse batches).</summary>
    public const string MouseForward = @"\\.\pipe\AkiSpace_MouseForward";

    /// <summary>Child -> primary direction (results/confirmations).</summary>
    public const string MouseResult = @"\\.\pipe\AkiSpace_MouseResult";

    /// <summary>Control channel (JSON control messages, either direction).</summary>
    public const string Control = @"\\.\pipe\AkiSpace_Control";
}

/// <summary>
/// Named-pipe server (primary session side). Listens for a child-session
/// client connection and exposes typed frame events.
/// </summary>
public sealed class PipeServer : IAsyncDisposable
{
    private readonly ILogger<PipeServer> _logger;
    private readonly object _lifecycleGate = new();
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public PipeServer(ILogger<PipeServer> logger)
    {
        _logger = logger;
    }

    /// <summary>Raised when a client connects. Fires once per accepted connection.</summary>
    public event Action<PipeConnection>? ClientConnected;

    /// <summary>Starts listening for a single child-session connection.</summary>
    public void Start()
    {
        lock (_lifecycleGate)
        {
            if (_cts != null) return;  // already started
            _cts = new CancellationTokenSource();
        }
        _listenTask = Task.Run(async () =>
        {
            while (true)
            {
                CancellationToken token;
                lock (_lifecycleGate) { token = _cts?.Token ?? default; }
                if (token.IsCancellationRequested) break;

                NamedPipeServerStream? stream = null;
                try
                {
                    stream = new NamedPipeServerStream(
                        PipeNames.MouseForward,
                        PipeDirection.InOut,
                        maxNumberOfServerInstances: 1,
                        PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous);

                    _logger.LogInformation("Pipe server listening on {Pipe}", PipeNames.MouseForward);
                    await stream.WaitForConnectionAsync(token).ConfigureAwait(false);
                    _logger.LogInformation("Child session connected to mouse pipe");

                    // Hand ownership of the stream to PipeConnection; it disposes
                    // when its read loop ends.
                    var connection = new PipeConnection(stream, _logger);
                    stream = null;
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
                    // Dispose the stream if it was never handed off to a connection
                    // (e.g. WaitForConnectionAsync was cancelled or threw).
                    stream?.Dispose();
                }
            }
        });
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

    public PipeClient(ILogger<PipeClient> logger)
    {
        _logger = logger;
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
                        ".", PipeNames.MouseForward.Substring(9),
                        PipeDirection.InOut, PipeOptions.Asynchronous);
                    await stream.ConnectAsync(TimeSpan.FromSeconds(2), linked.Token).ConfigureAwait(false);
                    _logger.LogInformation("Child session connected to primary pipe");
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
        _cts.Dispose();
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