using RideLink.Model;

namespace RideLink.Transport;

/// <summary>
/// Addresses one readable/writable/subscribable stream on a device.
/// <para>
/// For BLE this is a (service UUID, characteristic UUID) pair. For ANT+ there is no such
/// addressing — a channel simply broadcasts pages — so ANT endpoints use logical names such
/// as <c>("fec", "general-fe-data")</c>. Keeping both in one string pair means nothing above
/// the transport has to know which radio it is talking to, which is the entire point of the
/// abstraction.
/// </para>
/// </summary>
/// <param name="Service">Canonical service identity: a lowercase UUID, or a logical name.</param>
/// <param name="Characteristic">Canonical stream identity within the service.</param>
public readonly record struct EndpointId(string Service, string Characteristic)
{
    /// <summary>Endpoint for a BLE characteristic given full 128-bit UUIDs.</summary>
    public static EndpointId Ble(Guid service, Guid characteristic) =>
        new(service.ToString("D").ToLowerInvariant(), characteristic.ToString("D").ToLowerInvariant());

    /// <summary>Endpoint for a BLE characteristic given 16-bit assigned numbers.</summary>
    public static EndpointId Ble(ushort service, ushort characteristic) =>
        Ble(RideLink.Ble.BleUuids.FromShort(service), RideLink.Ble.BleUuids.FromShort(characteristic));

    /// <summary>Endpoint named logically, for transports without GATT addressing.</summary>
    public static EndpointId Logical(string service, string characteristic) =>
        new(service, characteristic);

    public override string ToString() => $"{Service}/{Characteristic}";
}

/// <summary>How a characteristic may be used, as reported by discovery.</summary>
[Flags]
public enum EndpointProperties
{
    None = 0,
    Read = 1 << 0,
    Write = 1 << 1,

    /// <summary>Write without response — lower latency, no acknowledgement.</summary>
    WriteWithoutResponse = 1 << 2,

    /// <summary>Unacknowledged server-initiated updates.</summary>
    Notify = 1 << 3,

    /// <summary>Acknowledged server-initiated updates. FTMS Control Point requires these.</summary>
    Indicate = 1 << 4,
}

/// <summary>One endpoint found during discovery.</summary>
/// <param name="Id">Address of the endpoint.</param>
/// <param name="Properties">What the endpoint supports.</param>
public readonly record struct EndpointDescriptor(EndpointId Id, EndpointProperties Properties)
{
    public bool CanSubscribe =>
        Properties.HasFlag(EndpointProperties.Notify) || Properties.HasFlag(EndpointProperties.Indicate);
}

/// <summary>A value received from a subscribed endpoint.</summary>
/// <param name="Endpoint">Where it came from.</param>
/// <param name="Payload">Raw octets, exactly as received.</param>
/// <param name="ReceivedAt">Arrival time.</param>
public readonly record struct DataPacket(EndpointId Endpoint, byte[] Payload, DateTimeOffset ReceivedAt);

/// <summary>How a write should be performed.</summary>
public enum WriteMode
{
    /// <summary>Acknowledged. Required for the FTMS Control Point.</summary>
    WithResponse,

    /// <summary>Unacknowledged.</summary>
    WithoutResponse,
}

/// <summary>Link state of a device.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,

    /// <summary>Link is up but services have not been discovered yet.</summary>
    Connected,

    /// <summary>Discovery finished and subscriptions are active; measurements should flow.</summary>
    Ready,

    /// <summary>Dropped unexpectedly; the reconnect policy is running.</summary>
    Reconnecting,
}

/// <summary>A device seen during a scan, before any connection is attempted.</summary>
/// <param name="Id">Transport-scoped identity, stable enough to reconnect with.</param>
/// <param name="Name">Advertised name, may be empty.</param>
/// <param name="Transport">Radio it was seen on.</param>
/// <param name="Rssi">Signal strength in dBm, null when the transport does not report it.</param>
/// <param name="AdvertisedServices">Service identities from the advertisement, used for filtering.</param>
/// <param name="Metadata">Anything else the transport wants to carry, e.g. ANT device type.</param>
public sealed record DiscoveredDevice(
    string Id,
    string Name,
    TransportKind Transport,
    int? Rssi,
    IReadOnlyList<string> AdvertisedServices,
    IReadOnlyDictionary<string, string> Metadata)
{
    public override string ToString() =>
        $"{(string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name)} [{Transport}:{Id}]"
        + (Rssi is { } r ? $" {r} dBm" : string.Empty);
}

/// <summary>Narrows a scan. An empty filter accepts everything the transport can see.</summary>
public sealed record ScanFilter
{
    /// <summary>
    /// Service identities to require in the advertisement. For BLE these are the
    /// <see cref="EndpointId.Service"/> strings, i.e. lowercase UUIDs.
    /// </summary>
    public IReadOnlyList<string> Services { get; init; } = Array.Empty<string>();

    /// <summary>Case-insensitive substring the device name must contain.</summary>
    public string? NameContains { get; init; }

    /// <summary>Drop advertisements weaker than this, in dBm.</summary>
    public int? MinimumRssi { get; init; }

    /// <summary>Accepts everything.</summary>
    public static ScanFilter All { get; } = new();
}

/// <summary>
/// A radio the engine can talk over. Implemented by the BLE, ANT+ and simulated backends;
/// nothing above this interface knows which it is holding.
/// </summary>
public interface IDeviceTransport : IAsyncDisposable
{
    /// <summary>Which radio this is.</summary>
    TransportKind Kind { get; }

    /// <summary>Human-readable backend name, for logs and the device picker.</summary>
    string Name { get; }

    /// <summary>
    /// Whether the transport can be used right now — adapter present and powered, ANT stick
    /// plugged in, permissions granted. Checked before scanning so the consumer can explain
    /// the failure instead of timing out.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams devices as they are seen. The same device may be yielded more than once as
    /// its RSSI or advertised name changes; consumers key on <see cref="DiscoveredDevice.Id"/>.
    /// Runs until the token is cancelled.
    /// </summary>
    IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        ScanFilter filter, CancellationToken cancellationToken = default);

    /// <summary>Opens a link. The returned connection owns its native handles.</summary>
    Task<IDeviceConnection> ConnectAsync(
        DiscoveredDevice device, CancellationToken cancellationToken = default);
}

/// <summary>An open link to one device.</summary>
public interface IDeviceConnection : IAsyncDisposable
{
    string DeviceId { get; }
    string Name { get; }
    TransportKind Transport { get; }

    /// <summary>Current link state.</summary>
    ConnectionState State { get; }

    /// <summary>Link state changes, including reconnect attempts.</summary>
    IObservable<ConnectionState> StateChanges { get; }

    /// <summary>
    /// Enumerates every endpoint the device exposes. Handles are resolved here and cached by
    /// the implementation; callers address endpoints by UUID and never by handle.
    /// </summary>
    Task<IReadOnlyList<EndpointDescriptor>> DiscoverAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads a value.</summary>
    Task<byte[]> ReadAsync(EndpointId endpoint, CancellationToken cancellationToken = default);

    /// <summary>Writes a value.</summary>
    Task WriteAsync(
        EndpointId endpoint,
        ReadOnlyMemory<byte> value,
        WriteMode mode = WriteMode.WithResponse,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Subscribes to server-initiated updates. For BLE this writes the CCCD
    /// (Core Specification, Vol 3, Part G, §3.3.3.3) with 0x0001 for notify or 0x0002 for
    /// indicate, chosen from the endpoint's properties. Disposing the returned handle
    /// unsubscribes.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(
        EndpointId endpoint,
        Action<DataPacket> onPacket,
        CancellationToken cancellationToken = default);
}
