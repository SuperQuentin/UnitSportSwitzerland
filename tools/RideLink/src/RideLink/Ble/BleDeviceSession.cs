using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Ble.Ftms;
using RideLink.Ble.Parsers;
using RideLink.Capabilities;
using RideLink.Diagnostics;
using RideLink.Model;
using RideLink.Transport;

namespace RideLink.Ble;

/// <summary>Metadata read from Device Information and Battery.</summary>
/// <param name="Manufacturer">Manufacturer Name String, when exposed.</param>
/// <param name="Model">Model Number String, when exposed.</param>
/// <param name="Serial">Serial Number String, when exposed.</param>
/// <param name="Firmware">Firmware Revision String, when exposed.</param>
/// <param name="BatteryPercent">Battery Level at connect time, when exposed.</param>
public readonly record struct DeviceMetadata(
    string? Manufacturer, string? Model, string? Serial, string? Firmware, int? BatteryPercent);

/// <summary>
/// Turns a connected BLE device into a set of capabilities.
/// <para>
/// Discovery is entirely dynamic: the session enumerates what the device actually exposes and
/// builds a capability for each service it recognises. No handle is ever hardcoded, and a
/// device offering an unexpected combination — a trainer with FTMS *and* Cycling Power, say —
/// yields both, leaving the router to decide which watts win.
/// </para>
/// </summary>
public sealed class BleDeviceSession : IAsyncDisposable
{
    private readonly IDeviceConnection _connection;
    private readonly ILogger _logger;
    private readonly IPacketLog? _packetLog;
    private readonly DeviceQuirks? _quirks;
    private readonly TimeProvider _clock;

    private readonly List<ICapability> _capabilities = [];
    private readonly List<IAsyncDisposable> _subscriptions = [];
    private FtmsControlSession? _controlSession;

    private BleDeviceSession(
        IDeviceConnection connection,
        ILogger logger,
        IPacketLog? packetLog,
        DeviceQuirks? quirks,
        TimeProvider clock)
    {
        _connection = connection;
        _logger = logger;
        _packetLog = packetLog;
        _quirks = quirks;
        _clock = clock;
    }

    /// <summary>Capabilities this device provides. Register them with the router.</summary>
    public IReadOnlyList<ICapability> Capabilities => _capabilities;

    /// <summary>Device Information and Battery, read once at attach time.</summary>
    public DeviceMetadata Metadata { get; private set; }

    /// <summary>Underlying link, for state changes and teardown.</summary>
    public IDeviceConnection Connection => _connection;

    /// <summary>
    /// Discovers services, reads features and ranges, subscribes to every measurement
    /// characteristic and returns the resulting capabilities.
    /// </summary>
    /// <param name="connection">An already-connected device.</param>
    /// <param name="wheelCircumferenceMeters">Rollout for wheel-based speed and distance.</param>
    /// <param name="logger">Optional logger.</param>
    /// <param name="packetLog">Optional raw-packet capture.</param>
    /// <param name="quirks">Optional per-device payload fixups.</param>
    /// <param name="clock">Optional time source, injected by tests.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public static async Task<BleDeviceSession> AttachAsync(
        IDeviceConnection connection,
        double wheelCircumferenceMeters = 2.096,
        ILogger? logger = null,
        IPacketLog? packetLog = null,
        DeviceQuirks? quirks = null,
        TimeProvider? clock = null,
        CancellationToken cancellationToken = default)
    {
        var session = new BleDeviceSession(
            connection, logger ?? NullLogger.Instance, packetLog, quirks, clock ?? TimeProvider.System);

        await session.BuildAsync(wheelCircumferenceMeters, cancellationToken).ConfigureAwait(false);
        return session;
    }

    private async Task BuildAsync(double wheelCircumference, CancellationToken ct)
    {
        var endpoints = await _connection.DiscoverAsync(ct).ConfigureAwait(false);
        var byId = endpoints.ToDictionary(e => e.Id, e => e);

        _logger.LogInformation(
            "{Device}: discovered {Count} characteristic(s)", _connection.Name, endpoints.Count);

        Metadata = await ReadMetadataAsync(byId, ct).ConfigureAwait(false);

        await TryAddFitnessMachineAsync(byId, ct).ConfigureAwait(false);
        await TryAddCyclingPowerAsync(byId, ct).ConfigureAwait(false);
        await TryAddCscAsync(byId, wheelCircumference, ct).ConfigureAwait(false);
        await TryAddHeartRateAsync(byId, ct).ConfigureAwait(false);
        await TryAddRscAsync(byId, ct).ConfigureAwait(false);
        await TryAddBatteryAsync(byId, ct).ConfigureAwait(false);

        if (_capabilities.Count == 0)
            _logger.LogWarning(
                "{Device} exposes no service RideLink understands", _connection.Name);
    }

    // ---- service builders -------------------------------------------------------------

    private async Task TryAddFitnessMachineAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        // The machine type is whichever data characteristic it actually exposes.
        (ushort characteristic, FitnessMachineKind kind, string channel)[] candidates =
        [
            (BleUuids.IndoorBikeData, FitnessMachineKind.IndoorBike, Channels.FtmsIndoorBike),
            (BleUuids.TreadmillData, FitnessMachineKind.Treadmill, Channels.FtmsTreadmill),
            (BleUuids.RowerData, FitnessMachineKind.Rower, Channels.FtmsRower),
            (BleUuids.CrossTrainerData, FitnessMachineKind.CrossTrainer, Channels.FtmsCrossTrainer),
        ];

        var match = candidates.FirstOrDefault(
            c => byId.ContainsKey(Endpoint(BleUuids.FitnessMachineService, c.characteristic)));
        if (match.channel is null) return;

        // Feature is read before anything is subscribed: it decides which control modes exist,
        // so an unsupported mode is reported up front rather than after a rejected write.
        var feature = await TryReadAsync(
            byId, BleUuids.FitnessMachineService, BleUuids.FitnessMachineFeature,
            b => FitnessMachineFeatureParser.Parse(b), ct).ConfigureAwait(false);

        var powerRange = await TryReadAsync(
            byId, BleUuids.FitnessMachineService, BleUuids.SupportedPowerRange,
            b => SupportedRangeParser.ParsePowerRange(b), ct).ConfigureAwait(false);

        var resistanceRange = await TryReadAsync(
            byId, BleUuids.FitnessMachineService, BleUuids.SupportedResistanceLevelRange,
            b => SupportedRangeParser.ParseResistanceRange(b), ct).ConfigureAwait(false);

        var controlPoint = Endpoint(BleUuids.FitnessMachineService, BleUuids.FitnessMachineControlPoint);
        if (byId.ContainsKey(controlPoint))
        {
            _controlSession = new FtmsControlSession(_connection, controlPoint, _logger);
            await _controlSession.StartAsync(ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "{Device}: no Fitness Machine Control Point, measurements only", _connection.Name);
        }

        var capabilities = new TrainerControlCapabilities(
            SupportsSimulation: feature?.SupportsSimulation ?? false,
            SupportsErg: feature?.SupportsErg ?? false,
            SupportsResistance: feature?.SupportsResistance ?? false,
            PowerRange: powerRange,
            ResistanceRange: resistanceRange);

        var machine = new FtmsMachineCapability(
            SourceId(match.channel), _connection.Name, match.kind, capabilities,
            _controlSession, _logger)
        { Clock = _clock };

        Add(machine);

        await SubscribeAsync(
            byId, BleUuids.FitnessMachineService, match.characteristic,
            (payload, at) => machine.Ingest(payload, at), ct).ConfigureAwait(false);

        // Status tells us when a user grabs the machine's own buttons and control is revoked.
        await SubscribeAsync(
            byId, BleUuids.FitnessMachineService, BleUuids.FitnessMachineStatus,
            (payload, _) => machine.IngestStatus(payload), ct, required: false).ConfigureAwait(false);

        _logger.LogInformation(
            "{Device}: FTMS {Kind}; SIM={Sim} ERG={Erg} Resistance={Res} power range {Range}",
            _connection.Name, match.kind, capabilities.SupportsSimulation,
            capabilities.SupportsErg, capabilities.SupportsResistance,
            powerRange is { } r ? $"{r.MinimumWatts}-{r.MaximumWatts} W" : "unreported");
    }

    private async Task TryAddCyclingPowerAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        var measurement = Endpoint(BleUuids.CyclingPowerService, BleUuids.CyclingPowerMeasurement);
        if (!byId.ContainsKey(measurement)) return;

        // A machine that also exposes CPS is still estimating; only a standalone power meter
        // claims direct force measurement, so the router keeps preferring real pedals.
        bool standalone = !byId.Keys.Any(
            e => e.Service == Endpoint(BleUuids.FitnessMachineService, 0).Service);

        var capability = new CyclingPowerCapability(
            SourceId(Channels.CyclingPower), _connection.Name, standalone) { Clock = _clock };

        Add(capability);
        await SubscribeAsync(
            byId, BleUuids.CyclingPowerService, BleUuids.CyclingPowerMeasurement,
            (payload, at) => capability.Ingest(payload, at), ct).ConfigureAwait(false);
    }

    private async Task TryAddCscAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId,
        double wheelCircumference,
        CancellationToken ct)
    {
        if (!byId.ContainsKey(Endpoint(BleUuids.CyclingSpeedAndCadenceService, BleUuids.CscMeasurement)))
            return;

        var capability = new CscCapability(
            SourceId(Channels.SpeedCadence), _connection.Name, wheelCircumference) { Clock = _clock };

        Add(capability);
        await SubscribeAsync(
            byId, BleUuids.CyclingSpeedAndCadenceService, BleUuids.CscMeasurement,
            (payload, at) => capability.Ingest(payload, at), ct).ConfigureAwait(false);
    }

    private async Task TryAddHeartRateAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        if (!byId.ContainsKey(Endpoint(BleUuids.HeartRateService, BleUuids.HeartRateMeasurement)))
            return;

        var capability = new HeartRateCapability(SourceId(Channels.HeartRate), _connection.Name)
        { Clock = _clock };

        Add(capability);
        await SubscribeAsync(
            byId, BleUuids.HeartRateService, BleUuids.HeartRateMeasurement,
            (payload, at) => capability.Ingest(payload, at), ct).ConfigureAwait(false);
    }

    private async Task TryAddRscAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        if (!byId.ContainsKey(Endpoint(BleUuids.RunningSpeedAndCadenceService, BleUuids.RscMeasurement)))
            return;

        var capability = new RscCapability(
            SourceId(Channels.RunningSpeedCadence), _connection.Name) { Clock = _clock };

        Add(capability);
        await SubscribeAsync(
            byId, BleUuids.RunningSpeedAndCadenceService, BleUuids.RscMeasurement,
            (payload, at) => capability.Ingest(payload, at), ct).ConfigureAwait(false);
    }

    private async Task TryAddBatteryAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        var level = Endpoint(BleUuids.BatteryService, BleUuids.BatteryLevel);
        if (!byId.TryGetValue(level, out var descriptor)) return;

        var capability = new BatteryCapability(SourceId(Channels.Battery), _connection.Name)
        { Clock = _clock };
        Add(capability);

        try
        {
            byte[] value = await _connection.ReadAsync(level, ct).ConfigureAwait(false);
            capability.Ingest(value, _clock.GetUtcNow());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Device}: battery level could not be read", _connection.Name);
        }

        // Battery Level notification is optional; many devices only allow a read.
        if (descriptor.CanSubscribe)
            await SubscribeAsync(
                byId, BleUuids.BatteryService, BleUuids.BatteryLevel,
                (payload, at) => capability.Ingest(payload, at), ct, required: false)
                .ConfigureAwait(false);
    }

    private async Task<DeviceMetadata> ReadMetadataAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId, CancellationToken ct)
    {
        return new DeviceMetadata(
            await ReadStringAsync(BleUuids.ManufacturerNameString).ConfigureAwait(false),
            await ReadStringAsync(BleUuids.ModelNumberString).ConfigureAwait(false),
            await ReadStringAsync(BleUuids.SerialNumberString).ConfigureAwait(false),
            await ReadStringAsync(BleUuids.FirmwareRevisionString).ConfigureAwait(false),
            null);

        async Task<string?> ReadStringAsync(ushort characteristic)
        {
            var endpoint = Endpoint(BleUuids.DeviceInformationService, characteristic);
            if (!byId.ContainsKey(endpoint)) return null;
            try
            {
                byte[] value = await _connection.ReadAsync(endpoint, ct).ConfigureAwait(false);
                return Encoding.UTF8.GetString(value).TrimEnd('\0').Trim();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "{Device}: could not read DIS 0x{Uuid:X4}",
                    _connection.Name, characteristic);
                return null;
            }
        }
    }

    // ---- helpers ------------------------------------------------------------------------

    private static EndpointId Endpoint(ushort service, ushort characteristic) =>
        EndpointId.Ble(service, characteristic);

    private SourceId SourceId(string channel) =>
        new(_connection.DeviceId, _connection.Transport, channel);

    private void Add(ICapability capability) => _capabilities.Add(capability);

    /// <summary>Reads and parses a characteristic, returning null when it is absent or unreadable.</summary>
    private async Task<T?> TryReadAsync<T>(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId,
        ushort service,
        ushort characteristic,
        Func<byte[], T> parse,
        CancellationToken ct) where T : struct
    {
        var endpoint = Endpoint(service, characteristic);
        if (!byId.ContainsKey(endpoint)) return null;

        try
        {
            byte[] value = await _connection.ReadAsync(endpoint, ct).ConfigureAwait(false);
            _packetLog?.Record(_connection.DeviceId, endpoint, value, _clock.GetUtcNow());
            return parse(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Device}: failed to read {Endpoint}", _connection.Name, endpoint);
            return null;
        }
    }

    private async Task SubscribeAsync(
        IReadOnlyDictionary<EndpointId, EndpointDescriptor> byId,
        ushort service,
        ushort characteristic,
        Action<byte[], DateTimeOffset> handler,
        CancellationToken ct,
        bool required = true)
    {
        var endpoint = Endpoint(service, characteristic);
        if (!byId.ContainsKey(endpoint))
        {
            if (required)
                _logger.LogWarning("{Device}: expected {Endpoint} but it is absent",
                    _connection.Name, endpoint);
            return;
        }

        var subscription = await _connection.SubscribeAsync(endpoint, packet =>
        {
            byte[] payload = packet.Payload;
            _packetLog?.Record(_connection.DeviceId, endpoint, payload, packet.ReceivedAt);

            if (_quirks is not null)
                payload = _quirks.Rewrite(
                    _connection.Name, Metadata.Manufacturer, endpoint, payload);

            try
            {
                handler(payload, packet.ReceivedAt);
            }
            catch (Exception ex)
            {
                // One malformed packet must never take the session down: log it with the raw
                // hex and carry on, because the next notification is usually fine.
                _logger.LogWarning(ex, "{Device}: {Endpoint} payload {Hex} failed to parse",
                    _connection.Name, endpoint, Convert.ToHexString(payload));
            }
        }, ct).ConfigureAwait(false);

        _subscriptions.Add(subscription);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var subscription in _subscriptions)
        {
            try { await subscription.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogDebug(ex, "Unsubscribe failed during teardown"); }
        }
        _subscriptions.Clear();

        if (_controlSession is not null) await _controlSession.DisposeAsync().ConfigureAwait(false);

        foreach (var capability in _capabilities.OfType<IDisposable>()) capability.Dispose();
        _capabilities.Clear();
    }
}
