using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Ble;
using RideLink.Capabilities;
using RideLink.Diagnostics;
using RideLink.Model;
using RideLink.Threading;
using RideLink.Transport;

namespace RideLink;

/// <summary>Tuning for <see cref="RideLinkEngine"/>.</summary>
public sealed record RideLinkOptions
{
    /// <summary>Wheel rollout for wheel-based speed and distance, metres.</summary>
    public double WheelCircumferenceMeters { get; init; } = 2.096;

    /// <summary>Where measurement callbacks are raised. Defaults to inline.</summary>
    public IEventDispatcher Dispatcher { get; init; } = ImmediateDispatcher.Instance;

    /// <summary>Source-ranking rules for the router.</summary>
    public SourcePriorityPolicy PriorityPolicy { get; init; } = SourcePriorityPolicy.Default;

    /// <summary>Reconnect after an unexpected drop.</summary>
    public bool AutoReconnect { get; init; } = true;

    /// <summary>First reconnect delay; doubles up to <see cref="MaxReconnectDelay"/>.</summary>
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Ceiling on the reconnect backoff.</summary>
    public TimeSpan MaxReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Give up after this many consecutive failures. Zero means never give up.</summary>
    public int MaxReconnectAttempts { get; init; } = 0;

    /// <summary>Time source, injectable for tests.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>A connection lifecycle event, for the UI's device list.</summary>
/// <param name="DeviceId">Which device.</param>
/// <param name="DeviceName">Its display name.</param>
/// <param name="Transport">Which radio.</param>
/// <param name="State">New state.</param>
/// <param name="Detail">Free text, e.g. the reconnect attempt number.</param>
public readonly record struct DeviceConnectionEvent(
    string DeviceId, string DeviceName, TransportKind Transport, ConnectionState State, string? Detail);

/// <summary>A device the engine is managing.</summary>
public sealed class ManagedDevice
{
    internal ManagedDevice(DiscoveredDevice descriptor, IDeviceTransport transport)
    {
        Descriptor = descriptor;
        Transport = transport;
    }

    /// <summary>What the scan reported.</summary>
    public DiscoveredDevice Descriptor { get; }

    /// <summary>Transport it was found on, used to reconnect.</summary>
    public IDeviceTransport Transport { get; }

    /// <summary>Current session, replaced on each reconnect.</summary>
    public BleDeviceSession? Session { get; internal set; }

    /// <summary>Device Information, once attached.</summary>
    public DeviceMetadata Metadata => Session?.Metadata ?? default;

    /// <summary>Capabilities currently registered with the router.</summary>
    public IReadOnlyList<ICapability> Capabilities => Session?.Capabilities ?? [];

    public override string ToString() => Descriptor.ToString();
}

/// <summary>
/// The engine: scan across every configured transport, connect devices, and expose one merged
/// set of capability streams.
/// <para>
/// This is the whole surface a consumer needs. A Godot host constructs it with a
/// <see cref="QueuedDispatcher"/>, subscribes to <see cref="Router"/>, and drains the
/// dispatcher once a frame; nothing else in the game touches BLE, ANT+ or GATT.
/// </para>
/// </summary>
public sealed class RideLinkEngine : IAsyncDisposable
{
    private readonly IReadOnlyList<IDeviceTransport> _transports;
    private readonly RideLinkOptions _options;
    private readonly ILogger _logger;
    private readonly ILoggerFactory? _loggerFactory;

    private readonly Subject<DeviceConnectionEvent> _connectionEvents = new();
    private readonly Dictionary<string, ManagedDevice> _devices = [];
    private readonly Dictionary<string, List<IDisposable>> _registrations = [];
    private readonly Dictionary<string, CancellationTokenSource> _reconnectLoops = [];
    private readonly object _gate = new();

    public RideLinkEngine(
        IEnumerable<IDeviceTransport> transports,
        RideLinkOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IPacketLog? packetLog = null,
        DeviceQuirks? quirks = null)
    {
        _transports = transports.ToArray();
        _options = options ?? new RideLinkOptions();
        _loggerFactory = loggerFactory;
        _logger = loggerFactory?.CreateLogger<RideLinkEngine>() ?? NullLogger<RideLinkEngine>.Instance;

        PacketLog = packetLog ?? new PacketLog(loggerFactory?.CreateLogger<PacketLog>());
        Quirks = quirks ?? new DeviceQuirks();

        Router = new CapabilityRouter(
            _options.Dispatcher,
            _options.PriorityPolicy,
            _options.Clock,
            loggerFactory?.CreateLogger<CapabilityRouter>());
    }

    /// <summary>Merged, prioritized measurement streams. Subscribe here, not to devices.</summary>
    public CapabilityRouter Router { get; }

    /// <summary>Raw-packet capture, off by default.</summary>
    public IPacketLog PacketLog { get; }

    /// <summary>Per-device workarounds.</summary>
    public DeviceQuirks Quirks { get; }

    /// <summary>Connection lifecycle events for every managed device.</summary>
    public IObservable<DeviceConnectionEvent> ConnectionEvents => _connectionEvents;

    /// <summary>Transports the engine was configured with.</summary>
    public IReadOnlyList<IDeviceTransport> Transports => _transports;

    /// <summary>Devices currently managed.</summary>
    public IReadOnlyList<ManagedDevice> Devices
    {
        get { lock (_gate) return _devices.Values.ToArray(); }
    }

    /// <summary>
    /// Scans every available transport at once and yields devices as they appear. The same
    /// device may be yielded more than once as its advertisement changes.
    /// </summary>
    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        ScanFilter? filter = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        filter ??= ScanFilter.All;

        var channel = System.Threading.Channels.Channel.CreateUnbounded<DiscoveredDevice>();
        var workers = new List<Task>();

        foreach (var transport in _transports)
        {
            workers.Add(Task.Run(async () =>
            {
                try
                {
                    if (!await transport.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                    {
                        _logger.LogWarning("{Transport} is not available; skipping", transport.Name);
                        return;
                    }

                    await foreach (var device in transport
                        .ScanAsync(filter, cancellationToken)
                        .WithCancellation(cancellationToken)
                        .ConfigureAwait(false))
                    {
                        await channel.Writer.WriteAsync(device, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{Transport} scan failed", transport.Name);
                }
            }, cancellationToken));
        }

        _ = Task.Run(async () =>
        {
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        await foreach (var device in channel.Reader
            .ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return device;
        }
    }

    /// <summary>
    /// Connects a device, discovers its capabilities and registers them with the router.
    /// Reconnect is armed automatically when enabled.
    /// </summary>
    public async Task<ManagedDevice> ConnectAsync(
        DiscoveredDevice device, CancellationToken cancellationToken = default)
    {
        var transport = _transports.FirstOrDefault(t => t.Kind == device.Transport)
            ?? throw new InvalidOperationException(
                $"No transport configured for {device.Transport}");

        var managed = new ManagedDevice(device, transport);
        lock (_gate) _devices[device.Id] = managed;

        Raise(managed, ConnectionState.Connecting, null);
        await AttachAsync(managed, cancellationToken).ConfigureAwait(false);
        return managed;
    }

    /// <summary>Disconnects a device and removes its capabilities from the router.</summary>
    public async Task DisconnectAsync(string deviceId)
    {
        ManagedDevice? managed;
        CancellationTokenSource? reconnect;

        lock (_gate)
        {
            _devices.Remove(deviceId, out managed);
            _reconnectLoops.Remove(deviceId, out reconnect);
        }

        if (reconnect is not null)
        {
            await reconnect.CancelAsync().ConfigureAwait(false);
            reconnect.Dispose();
        }

        if (managed is null) return;

        Unregister(deviceId);
        if (managed.Session is not null) await managed.Session.DisposeAsync().ConfigureAwait(false);

        Raise(managed, ConnectionState.Disconnected, "disconnected by request");
    }

    private async Task AttachAsync(ManagedDevice managed, CancellationToken cancellationToken)
    {
        var connection = await managed.Transport
            .ConnectAsync(managed.Descriptor, cancellationToken).ConfigureAwait(false);

        var session = await BleDeviceSession.AttachAsync(
            connection,
            _options.WheelCircumferenceMeters,
            _loggerFactory?.CreateLogger<BleDeviceSession>(),
            PacketLog,
            Quirks,
            _options.Clock,
            cancellationToken).ConfigureAwait(false);

        managed.Session = session;

        var registrations = session.Capabilities.Select(Router.Register).ToList();
        lock (_gate) _registrations[managed.Descriptor.Id] = registrations;

        // Watch for drops. The subscription lives as long as the session does; a new one is
        // created by the next successful attach.
        connection.StateChanges.Subscribe(state =>
        {
            if (state is ConnectionState.Disconnected && _options.AutoReconnect)
                ArmReconnect(managed);
        });

        Raise(managed, ConnectionState.Ready,
            $"{session.Capabilities.Count} capability stream(s)");
    }

    private void ArmReconnect(ManagedDevice managed)
    {
        string id = managed.Descriptor.Id;

        lock (_gate)
        {
            if (!_devices.ContainsKey(id)) return;            // deliberately disconnected
            if (_reconnectLoops.ContainsKey(id)) return;      // already retrying
            _reconnectLoops[id] = new CancellationTokenSource();
        }

        _ = Task.Run(() => ReconnectLoopAsync(managed));
    }

    private async Task ReconnectLoopAsync(ManagedDevice managed)
    {
        string id = managed.Descriptor.Id;
        CancellationTokenSource? cts;
        lock (_gate) _reconnectLoops.TryGetValue(id, out cts);
        if (cts is null) return;

        var delay = _options.InitialReconnectDelay;
        int attempt = 0;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                attempt++;
                Raise(managed, ConnectionState.Reconnecting, $"attempt {attempt}");

                // Drop the old session's subscriptions before trying again, or a successful
                // reconnect would register a second copy of every capability.
                Unregister(id);
                if (managed.Session is not null)
                {
                    try { await managed.Session.DisposeAsync().ConfigureAwait(false); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Old session teardown failed"); }
                    managed.Session = null;
                }

                await Task.Delay(delay, _options.Clock, cts.Token).ConfigureAwait(false);

                try
                {
                    await AttachAsync(managed, cts.Token).ConfigureAwait(false);
                    _logger.LogInformation("Reconnected to {Device} after {Attempts} attempt(s)",
                        managed.Descriptor.Name, attempt);
                    return;
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Reconnect attempt {Attempt} to {Device} failed",
                        attempt, managed.Descriptor.Name);
                }

                // Exponential backoff: straps and trainers can be away for minutes and
                // hammering the radio helps nobody.
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxReconnectDelay.Ticks));

                if (_options.MaxReconnectAttempts > 0 && attempt >= _options.MaxReconnectAttempts)
                {
                    Raise(managed, ConnectionState.Disconnected, $"gave up after {attempt} attempts");
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_reconnectLoops.Remove(id, out var finished)) finished.Dispose();
            }
        }
    }

    private void Unregister(string deviceId)
    {
        List<IDisposable>? registrations;
        lock (_gate) _registrations.Remove(deviceId, out registrations);

        if (registrations is null) return;
        foreach (var registration in registrations) registration.Dispose();
    }

    private void Raise(ManagedDevice managed, ConnectionState state, string? detail) =>
        _options.Dispatcher.Post(() => _connectionEvents.OnNext(new DeviceConnectionEvent(
            managed.Descriptor.Id, managed.Descriptor.Name, managed.Descriptor.Transport,
            state, detail)));

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        lock (_gate) ids = _devices.Keys.ToArray();

        foreach (string id in ids) await DisconnectAsync(id).ConfigureAwait(false);

        foreach (var transport in _transports)
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Transport teardown failed"); }
        }

        Router.Dispose();
        _connectionEvents.Dispose();
    }
}
