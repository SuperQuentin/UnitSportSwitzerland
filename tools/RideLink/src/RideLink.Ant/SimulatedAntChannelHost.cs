using System.Reactive.Subjects;

namespace RideLink.Ant;

/// <summary>
/// An in-memory stand-in for the ANT USB stick.
/// <para>
/// Lets the FE-C page parsing, control-page encoding and capability mapping be exercised with
/// no hardware and no licence-gated DLL. Feed it broadcasts with
/// <see cref="SimulatedAntChannel.Broadcast"/> and read back whatever control pages the engine
/// sent via <see cref="SimulatedAntChannel.SentPages"/>.
/// </para>
/// </summary>
public sealed class SimulatedAntChannelHost : IAntChannelHost
{
    private readonly List<SimulatedAntChannel> _channels = [];
    private readonly object _gate = new();

    public string Description => "Simulated ANT host (no USB stick)";

    /// <summary>Device number handed to channels that open with a wildcard.</summary>
    public ushort PairedDeviceNumber { get; init; } = 12345;

    /// <summary>Set false to exercise the "no stick plugged in" path.</summary>
    public bool Available { get; init; } = true;

    /// <summary>Channels opened so far.</summary>
    public IReadOnlyList<SimulatedAntChannel> Channels
    {
        get { lock (_gate) return _channels.ToArray(); }
    }

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Available);

    public Task<IAntChannel> OpenChannelAsync(
        AntChannelConfig config, CancellationToken cancellationToken = default)
    {
        ushort deviceNumber = config.DeviceNumber == AntProfiles.WildcardDeviceNumber
            ? PairedDeviceNumber
            : config.DeviceNumber;

        var channel = new SimulatedAntChannel(config, deviceNumber);
        lock (_gate) _channels.Add(channel);
        return Task.FromResult<IAntChannel>(channel);
    }

    public async ValueTask DisposeAsync()
    {
        SimulatedAntChannel[] channels;
        lock (_gate)
        {
            channels = _channels.ToArray();
            _channels.Clear();
        }

        foreach (var channel in channels) await channel.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>A simulated ANT channel: push broadcasts in, inspect control pages out.</summary>
public sealed class SimulatedAntChannel(AntChannelConfig config, ushort deviceNumber) : IAntChannel
{
    private readonly Subject<AntBroadcast> _broadcasts = new();
    private readonly List<byte[]> _sent = [];
    private readonly object _gate = new();

    public AntChannelConfig Config { get; } = config;

    public ushort DeviceNumber { get; } = deviceNumber;

    public IObservable<AntBroadcast> Broadcasts => _broadcasts;

    /// <summary>Set false to exercise the "trainer did not acknowledge" path.</summary>
    public bool AcknowledgeSends { get; set; } = true;

    /// <summary>Control pages the engine has sent, oldest first.</summary>
    public IReadOnlyList<byte[]> SentPages
    {
        get { lock (_gate) return _sent.ToArray(); }
    }

    /// <summary>Most recently sent page, or null.</summary>
    public byte[]? LastSentPage
    {
        get { lock (_gate) return _sent.Count == 0 ? null : _sent[^1]; }
    }

    /// <summary>Delivers one 8-octet broadcast to whatever is subscribed.</summary>
    public void Broadcast(byte[] page, DateTimeOffset? at = null) =>
        _broadcasts.OnNext(new AntBroadcast(DeviceNumber, page, at ?? DateTimeOffset.UtcNow));

    public Task<bool> SendAcknowledgedAsync(byte[] page, CancellationToken cancellationToken = default)
    {
        lock (_gate) _sent.Add(page);
        return Task.FromResult(AcknowledgeSends);
    }

    public ValueTask DisposeAsync()
    {
        _broadcasts.Dispose();
        return ValueTask.CompletedTask;
    }
}
