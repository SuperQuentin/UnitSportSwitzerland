using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Capabilities;
using RideLink.Transport;

namespace RideLink.Ble.Ftms;

/// <summary>
/// Serializes Fitness Machine Control Point transactions.
/// <para>
/// The Control Point is transactional (FTMS v1.0, §4.16): a write is not "applied" until the
/// machine indicates a Response Code echoing the op code with a result. Two overlapping
/// writes cannot be told apart, because the response carries only the op code and no
/// correlation id — so writes are strictly one at a time behind a semaphore, and the
/// response the machine sends back is matched against the op code that is currently in flight.
/// </para>
/// <para>
/// The handshake is Request Control (0x00) then Reset (0x01) before any Set*. Machines reject
/// targets with Control Not Permitted until that is done, and revoke control again whenever a
/// user touches the machine's own buttons — which arrives as a Fitness Machine Status
/// notification, not as a failed write, so the session re-arms itself on that signal.
/// </para>
/// </summary>
public sealed class FtmsControlSession : IAsyncDisposable
{
    private readonly IDeviceConnection _connection;
    private readonly EndpointId _controlPoint;
    private readonly ILogger _logger;

    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _pendingGate = new();

    private IAsyncDisposable? _subscription;
    private TaskCompletionSource<FtmsControlResponse>? _pending;
    private FtmsOpCode _pendingOpCode;
    private bool _hasControl;

    public FtmsControlSession(
        IDeviceConnection connection,
        EndpointId controlPoint,
        ILogger? logger = null)
    {
        _connection = connection;
        _controlPoint = controlPoint;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>How long to wait for the machine's indication before giving up.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>True once Request Control has succeeded and control has not been revoked.</summary>
    public bool HasControl
    {
        get { lock (_pendingGate) return _hasControl; }
    }

    /// <summary>Subscribes to the Control Point's indications. Call before any write.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _subscription ??= await _connection
            .SubscribeAsync(_controlPoint, OnIndication, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Called by the owning device when Fitness Machine Status reports that control was lost,
    /// so the next command re-runs the handshake instead of failing.
    /// </summary>
    public void NotifyControlLost()
    {
        lock (_pendingGate) _hasControl = false;
        _logger.LogWarning("FTMS control permission lost; handshake will be repeated");
    }

    private void OnIndication(DataPacket packet)
    {
        FtmsControlResponse response;
        try
        {
            response = FtmsControlPoint.ParseResponse(packet.Payload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unparseable FTMS Control Point indication {Hex}",
                Convert.ToHexString(packet.Payload));
            return;
        }

        lock (_pendingGate)
        {
            if (_pending is null) return;

            // The response carries no correlation id, only the op code being answered. An
            // indication for a different op code is a stale or unsolicited one and is ignored
            // rather than resolving the wrong transaction.
            if (response.RequestOpCode != _pendingOpCode)
            {
                _logger.LogDebug(
                    "Ignoring FTMS response for {Actual} while awaiting {Expected}",
                    response.RequestOpCode, _pendingOpCode);
                return;
            }

            if (response.Result == FtmsResultCode.ControlNotPermitted) _hasControl = false;

            _pending.TrySetResult(response);
            _pending = null;
        }
    }

    /// <summary>
    /// Runs Request Control then Reset. Idempotent — returns success immediately when control
    /// is already held.
    /// </summary>
    public async Task<ControlResult> EnsureControlAsync(CancellationToken cancellationToken = default)
    {
        if (HasControl) return ControlResult.Ok();

        await StartAsync(cancellationToken).ConfigureAwait(false);

        var request = await ExecuteAsync(
            FtmsOpCode.RequestControl, FtmsControlPoint.RequestControl(), cancellationToken)
            .ConfigureAwait(false);
        if (!request.Success) return request;

        lock (_pendingGate) _hasControl = true;

        // Reset clears any target the previous session left behind. A machine still holding
        // an old ERG target would otherwise fight the first SIM command.
        var reset = await ExecuteAsync(
            FtmsOpCode.Reset, FtmsControlPoint.Reset(), cancellationToken).ConfigureAwait(false);

        // Some machines answer Reset with Op Code Not Supported while still accepting targets,
        // so a failed Reset is logged but does not fail the handshake.
        if (!reset.Success)
            _logger.LogDebug("FTMS Reset was refused ({Failure}); continuing", reset.Failure);

        return ControlResult.Ok(message: "control acquired");
    }

    /// <summary>
    /// Writes one command and waits for its indicated response.
    /// </summary>
    /// <param name="opCode">Op code being written, used to match the response.</param>
    /// <param name="payload">Complete Control Point value including the op code octet.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="appliedValue">Value actually sent, echoed back in the result for the UI.</param>
    public async Task<ControlResult> ExecuteAsync(
        FtmsOpCode opCode,
        byte[] payload,
        CancellationToken cancellationToken = default,
        double? appliedValue = null)
    {
        if (_connection.State is ConnectionState.Disconnected or ConnectionState.Reconnecting)
            return ControlResult.Fail(ControlFailure.NotConnected, "Device is not connected");

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<FtmsControlResponse>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_pendingGate)
            {
                _pending = completion;
                _pendingOpCode = opCode;
            }

            _logger.LogDebug("FTMS -> {OpCode} {Hex}", opCode, Convert.ToHexString(payload));

            try
            {
                await _connection
                    .WriteAsync(_controlPoint, payload, WriteMode.WithResponse, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                lock (_pendingGate) _pending = null;
                return ControlResult.Fail(ControlFailure.NotConnected, $"Write failed: {ex.Message}");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ResponseTimeout);

            FtmsControlResponse response;
            try
            {
                response = await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                lock (_pendingGate) _pending = null;
                return ControlResult.Fail(
                    ControlFailure.Timeout,
                    $"{opCode} was written but the machine did not indicate a response within "
                    + $"{ResponseTimeout.TotalSeconds:0.#} s");
            }

            _logger.LogDebug("FTMS <- {OpCode} {Result}", response.RequestOpCode, response.Result);

            return response.Result switch
            {
                FtmsResultCode.Success => ControlResult.Ok(appliedValue),
                FtmsResultCode.OpCodeNotSupported => ControlResult.Fail(
                    ControlFailure.NotSupported, $"{opCode} is not supported by this machine"),
                FtmsResultCode.InvalidParameter => ControlResult.Fail(
                    ControlFailure.OutOfRange, $"{opCode} rejected the parameter as invalid"),
                FtmsResultCode.ControlNotPermitted => ControlResult.Fail(
                    ControlFailure.ControlNotPermitted,
                    "The machine has not granted control; Request Control must be repeated"),
                _ => ControlResult.Fail(
                    ControlFailure.Rejected, $"{opCode} failed with {response.Result}"),
            };
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null) await _subscription.DisposeAsync().ConfigureAwait(false);
        _subscription = null;
        _writeGate.Dispose();
    }
}
