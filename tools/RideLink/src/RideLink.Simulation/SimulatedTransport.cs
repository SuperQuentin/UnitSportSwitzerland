using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using RideLink.Model;
using RideLink.Transport;

namespace RideLink.Simulation;

/// <summary>
/// A transport backed by virtual devices.
/// <para>
/// It is a full <see cref="IDeviceTransport"/>, so everything above it — device sessions,
/// parsers, capability router, control modes — runs unchanged. That is the point: the engine
/// under test is the real engine, and the only substitution is the radio.
/// </para>
/// </summary>
public sealed class SimulatedTransport : IDeviceTransport
{
    private readonly List<SimulatedDevice> _devices = [];
    private readonly List<SimulatedConnection> _connections = [];
    private readonly object _gate = new();

    public TransportKind Kind => TransportKind.Simulated;

    public string Name => "Simulated";

    /// <summary>How often connected devices notify. Real FTMS trainers sit around 4 Hz.</summary>
    public TimeSpan NotificationInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Artificial delay on connect, so reconnect and cancellation paths are realistic.</summary>
    public TimeSpan ConnectLatency { get; init; } = TimeSpan.FromMilliseconds(120);

    /// <summary>Time source, injectable so tests can run without waiting.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Adds a virtual device to the simulated airwaves.</summary>
    public SimulatedTransport Add(SimulatedDevice device)
    {
        lock (_gate) _devices.Add(device);
        return this;
    }

    /// <summary>Devices currently visible.</summary>
    public IReadOnlyList<SimulatedDevice> Devices
    {
        get { lock (_gate) return _devices.ToArray(); }
    }

    /// <summary>
    /// A trainer, a chest strap and a separate power meter — the arrangement the router's
    /// priority rules exist for, since two devices then both claim to know the watts.
    /// </summary>
    public static SimulatedTransport CreateDefaultRig(IRideProfile? profile = null)
    {
        profile ??= new SteadyRideProfile();
        return new SimulatedTransport()
            .Add(new SimulatedTrainer(profile: profile))
            .Add(new SimulatedHeartRateStrap(profile: profile))
            .Add(new SimulatedPowerMeter(profile: profile));
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        ScanFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        SimulatedDevice[] devices;
        lock (_gate) devices = _devices.ToArray();

        foreach (var device in devices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (filter.Services.Count > 0
                && !filter.Services.Any(s => device.AdvertisedServices.Contains(s)))
                continue;

            if (filter.NameContains is { } needle
                && !device.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                continue;

            if (filter.MinimumRssi is { } min && device.Rssi < min) continue;

            // Spread the results out a little so the console demo behaves like a real scan.
            await Task.Delay(TimeSpan.FromMilliseconds(60), Clock, cancellationToken)
                .ConfigureAwait(false);

            yield return new DiscoveredDevice(
                device.Id,
                device.Name,
                TransportKind.Simulated,
                device.Rssi,
                device.AdvertisedServices,
                new Dictionary<string, string> { ["simulated"] = "true" });
        }
    }

    public async Task<IDeviceConnection> ConnectAsync(
        DiscoveredDevice device, CancellationToken cancellationToken = default)
    {
        SimulatedDevice? target;
        lock (_gate) target = _devices.FirstOrDefault(d => d.Id == device.Id);

        if (target is null)
            throw new InvalidOperationException($"No simulated device with id '{device.Id}'");

        await Task.Delay(ConnectLatency, Clock, cancellationToken).ConfigureAwait(false);

        var connection = new SimulatedConnection(target, NotificationInterval, Clock);
        lock (_gate) _connections.Add(connection);
        connection.Start();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        SimulatedConnection[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
            _connections.Clear();
        }

        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A link to one virtual device. Runs the notification loop on a background task, so consumers
/// receive callbacks off the calling thread exactly as they would from a real radio — which is
/// what makes the dispatcher meaningful to test.
/// </summary>
public sealed class SimulatedConnection : IDeviceConnection
{
    private readonly SimulatedDevice _device;
    private readonly TimeSpan _interval;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly BehaviorSubject<ConnectionState> _state = new(ConnectionState.Connected);
    private readonly Dictionary<EndpointId, List<Action<DataPacket>>> _subscribers = [];
    private readonly object _gate = new();

    private Task? _loop;
    private DateTimeOffset _startedAt;

    internal SimulatedConnection(SimulatedDevice device, TimeSpan interval, TimeProvider clock)
    {
        _device = device;
        _interval = interval;
        _clock = clock;
    }

    public string DeviceId => _device.Id;
    public string Name => _device.Name;
    public TransportKind Transport => TransportKind.Simulated;
    public ConnectionState State => _state.Value;
    public IObservable<ConnectionState> StateChanges => _state;

    internal void Start()
    {
        _startedAt = _clock.GetUtcNow();
        _loop = Task.Run(() => RunAsync(_shutdown.Token));
    }

    /// <summary>
    /// Simulates a dropout, so reconnect handling can be tested without unplugging anything.
    /// </summary>
    public void SimulateDisconnect()
    {
        _state.OnNext(ConnectionState.Disconnected);
    }

    /// <summary>Brings a simulated dropout back up.</summary>
    public void SimulateReconnect()
    {
        _state.OnNext(ConnectionState.Ready);
    }

    public Task<IReadOnlyList<EndpointDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        _state.OnNext(ConnectionState.Ready);
        return Task.FromResult(_device.Endpoints);
    }

    public Task<byte[]> ReadAsync(EndpointId endpoint, CancellationToken cancellationToken = default) =>
        Task.FromResult(_device.Read(endpoint));

    public Task WriteAsync(
        EndpointId endpoint,
        ReadOnlyMemory<byte> value,
        WriteMode mode = WriteMode.WithResponse,
        CancellationToken cancellationToken = default)
    {
        _device.Write(endpoint, value.ToArray(), Deliver);
        return Task.CompletedTask;
    }

    public Task<IAsyncDisposable> SubscribeAsync(
        EndpointId endpoint,
        Action<DataPacket> onPacket,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(endpoint, out var list))
                _subscribers[endpoint] = list = [];
            list.Add(onPacket);
        }

        return Task.FromResult<IAsyncDisposable>(new Unsubscriber(this, endpoint, onPacket));
    }

    private void Deliver(EndpointId endpoint, byte[] payload)
    {
        Action<DataPacket>[] handlers;
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(endpoint, out var list) || list.Count == 0) return;
            handlers = list.ToArray();
        }

        var packet = new DataPacket(endpoint, payload, _clock.GetUtcNow());
        foreach (var handler in handlers) handler(packet);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var previous = _clock.GetUtcNow();
        using var timer = new PeriodicTimer(_interval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_state.Value == ConnectionState.Disconnected) continue;

                var now = _clock.GetUtcNow();
                var delta = now - previous;
                previous = now;

                _device.Tick(now - _startedAt, delta, Deliver);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _state.OnNext(ConnectionState.Disconnected);
        _state.Dispose();
        _shutdown.Dispose();

        lock (_gate) _subscribers.Clear();
    }

    private sealed class Unsubscriber(
        SimulatedConnection connection, EndpointId endpoint, Action<DataPacket> handler)
        : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (connection._gate)
            {
                if (connection._subscribers.TryGetValue(endpoint, out var list))
                    list.Remove(handler);
            }
            return ValueTask.CompletedTask;
        }
    }
}
