using Microsoft.Extensions.Logging;
using RideLink.Transport;

namespace RideLink.Diagnostics;

/// <summary>
/// Raw-packet capture, toggleable per endpoint.
/// <para>
/// Real devices deviate from the spec constantly — a flag bit set that should not be, a field
/// omitted that the flags claim is present, a vendor field appended. When a value looks wrong
/// the only way to find out why is the octets that produced it, so the hex is kept next to
/// the decode.
/// </para>
/// </summary>
public interface IPacketLog
{
    /// <summary>Whether anything is being captured at all.</summary>
    bool IsEnabled { get; }

    /// <summary>Turns capture on or off for one endpoint, or for everything when null.</summary>
    void SetEnabled(EndpointId? endpoint, bool enabled);

    /// <summary>Records one raw payload.</summary>
    void Record(string deviceId, EndpointId endpoint, ReadOnlySpan<byte> payload, DateTimeOffset at);

    /// <summary>Most recent captures, newest last.</summary>
    IReadOnlyList<PacketRecord> Recent(int max = 200);
}

/// <summary>One captured payload.</summary>
public readonly record struct PacketRecord(
    string DeviceId, EndpointId Endpoint, byte[] Payload, DateTimeOffset At)
{
    public string Hex => Convert.ToHexString(Payload);

    public override string ToString() => $"{At:HH:mm:ss.fff} {DeviceId} {Endpoint} {Hex}";
}

/// <summary>
/// Ring-buffered packet log that also mirrors to <see cref="ILogger"/> at Trace level.
/// Thread-safe: BLE and ANT callbacks arrive on arbitrary pool threads.
/// </summary>
public sealed class PacketLog : IPacketLog
{
    private readonly ILogger<PacketLog> _logger;
    private readonly int _capacity;
    private readonly Queue<PacketRecord> _records = new();
    private readonly HashSet<EndpointId> _enabledEndpoints = [];
    private readonly object _gate = new();
    private bool _allEnabled;

    public PacketLog(ILogger<PacketLog>? logger = null, int capacity = 2000)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PacketLog>.Instance;
        _capacity = capacity;
    }

    public bool IsEnabled
    {
        get { lock (_gate) return _allEnabled || _enabledEndpoints.Count > 0; }
    }

    public void SetEnabled(EndpointId? endpoint, bool enabled)
    {
        lock (_gate)
        {
            if (endpoint is null)
            {
                _allEnabled = enabled;
                if (!enabled) _enabledEndpoints.Clear();
                return;
            }

            if (enabled) _enabledEndpoints.Add(endpoint.Value);
            else _enabledEndpoints.Remove(endpoint.Value);
        }
    }

    public void Record(string deviceId, EndpointId endpoint, ReadOnlySpan<byte> payload, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (!_allEnabled && !_enabledEndpoints.Contains(endpoint)) return;

            var record = new PacketRecord(deviceId, endpoint, payload.ToArray(), at);
            _records.Enqueue(record);
            while (_records.Count > _capacity) _records.Dequeue();

            _logger.LogTrace("RX {Device} {Endpoint} {Hex}", deviceId, endpoint, record.Hex);
        }
    }

    public IReadOnlyList<PacketRecord> Recent(int max = 200)
    {
        lock (_gate) return _records.TakeLast(max).ToArray();
    }
}

/// <summary>
/// Per-device escape hatch for non-compliant hardware.
/// <para>
/// Rather than littering the parsers with vendor special cases, a quirk is registered against
/// a device-name predicate and gets a chance to rewrite the payload before parsing and the
/// decoded values after. Every known-bad device stays in one place with a note about what it
/// does wrong.
/// </para>
/// </summary>
public sealed class DeviceQuirks
{
    private readonly List<Quirk> _quirks = [];

    /// <summary>One registered workaround.</summary>
    /// <param name="Name">Short identifier, appears in logs when the quirk fires.</param>
    /// <param name="Applies">Given a device name and manufacturer, does this quirk apply?</param>
    /// <param name="RewritePayload">
    /// Optional pre-parse fixup, e.g. trimming a trailing vendor octet that shifts nothing but
    /// trips a strict length check.
    /// </param>
    /// <param name="Endpoint">Restrict to one endpoint, or null for all.</param>
    public sealed record Quirk(
        string Name,
        Func<string, string?, bool> Applies,
        Func<byte[], byte[]>? RewritePayload = null,
        EndpointId? Endpoint = null);

    public void Register(Quirk quirk) => _quirks.Add(quirk);

    /// <summary>Quirks that apply to a device, in registration order.</summary>
    public IReadOnlyList<Quirk> For(string deviceName, string? manufacturer = null) =>
        _quirks.Where(q => q.Applies(deviceName, manufacturer)).ToArray();

    /// <summary>Runs every applicable payload rewrite for an endpoint.</summary>
    public byte[] Rewrite(string deviceName, string? manufacturer, EndpointId endpoint, byte[] payload)
    {
        foreach (var quirk in _quirks)
        {
            if (quirk.RewritePayload is null) continue;
            if (quirk.Endpoint is { } e && e != endpoint) continue;
            if (!quirk.Applies(deviceName, manufacturer)) continue;
            payload = quirk.RewritePayload(payload);
        }
        return payload;
    }
}
