namespace RideLink.Ant;

/// <summary>
/// ANT+ device profile identifiers and radio settings.
/// <para>
/// Values from the ANT+ device profile documents (thisisant.com). Every ANT+ profile shares
/// RF frequency 2457 MHz — expressed to the chip as channel number 57, an offset from
/// 2400 MHz — and differs in device type and channel period.
/// </para>
/// </summary>
public static class AntProfiles
{
    /// <summary>2457 MHz, expressed as an offset from 2400 MHz. Common to all ANT+ profiles.</summary>
    public const byte RadioFrequency = 57;

    /// <summary>Wildcard: pair with the first device of the right type that answers.</summary>
    public const ushort WildcardDeviceNumber = 0;

    /// <summary>Wildcard transmission type, used during search.</summary>
    public const byte WildcardTransmissionType = 0;

    /// <summary>Heart Rate Monitor. ANT+ Device Profile – Heart Rate Monitor.</summary>
    public static AntChannelConfig HeartRate { get; } = new()
    {
        Name = "HRM",
        DeviceType = 120,
        ChannelPeriod = 8070,   // ~4.06 Hz
    };

    /// <summary>Bicycle Power. ANT+ Device Profile – Bicycle Power.</summary>
    public static AntChannelConfig BicyclePower { get; } = new()
    {
        Name = "Bicycle Power",
        DeviceType = 11,
        ChannelPeriod = 8182,   // ~4.00 Hz
    };

    /// <summary>Combined Bike Speed and Cadence. ANT+ Device Profile – Bicycle Speed and Cadence.</summary>
    public static AntChannelConfig BikeSpeedAndCadence { get; } = new()
    {
        Name = "Bike Speed and Cadence",
        DeviceType = 121,
        ChannelPeriod = 8086,   // ~4.05 Hz
    };

    /// <summary>Bike Cadence only.</summary>
    public static AntChannelConfig BikeCadence { get; } = new()
    {
        Name = "Bike Cadence",
        DeviceType = 122,
        ChannelPeriod = 8102,
    };

    /// <summary>Bike Speed only.</summary>
    public static AntChannelConfig BikeSpeed { get; } = new()
    {
        Name = "Bike Speed",
        DeviceType = 123,
        ChannelPeriod = 8118,
    };

    /// <summary>
    /// Fitness Equipment Control — trainers. ANT+ Device Profile – Fitness Equipment.
    /// This is the ANT+ equivalent of BLE FTMS and carries both data and control.
    /// </summary>
    public static AntChannelConfig FitnessEquipment { get; } = new()
    {
        Name = "FE-C",
        DeviceType = 17,
        ChannelPeriod = 8192,   // exactly 4 Hz
    };

    /// <summary>Every profile the engine knows how to configure.</summary>
    public static IReadOnlyList<AntChannelConfig> All { get; } =
    [
        HeartRate, BicyclePower, BikeSpeedAndCadence, BikeCadence, BikeSpeed, FitnessEquipment,
    ];
}

/// <summary>Everything needed to open a channel for one ANT+ profile.</summary>
public sealed record AntChannelConfig
{
    /// <summary>Human-readable profile name.</summary>
    public required string Name { get; init; }

    /// <summary>ANT+ device type for this profile.</summary>
    public required byte DeviceType { get; init; }

    /// <summary>
    /// Channel period in counts of 32768 Hz. The device transmits at
    /// <c>32768 / ChannelPeriod</c> Hz and the channel must agree or messages are missed.
    /// </summary>
    public required ushort ChannelPeriod { get; init; }

    /// <summary>RF frequency offset from 2400 MHz.</summary>
    public byte RadioFrequency { get; init; } = AntProfiles.RadioFrequency;

    /// <summary>
    /// Specific device number to pair with, or <see cref="AntProfiles.WildcardDeviceNumber"/>
    /// to search. A paired number makes reconnection deterministic in a room full of trainers.
    /// </summary>
    public ushort DeviceNumber { get; init; } = AntProfiles.WildcardDeviceNumber;

    /// <summary>Transmission type, wildcard during search.</summary>
    public byte TransmissionType { get; init; } = AntProfiles.WildcardTransmissionType;

    /// <summary>Returns a copy bound to one specific device number.</summary>
    public AntChannelConfig PairedTo(ushort deviceNumber, byte transmissionType = 0) =>
        this with { DeviceNumber = deviceNumber, TransmissionType = transmissionType };
}

/// <summary>A broadcast message received on an ANT channel.</summary>
/// <param name="DeviceNumber">Which device sent it, resolved once the channel has paired.</param>
/// <param name="Payload">The 8-octet data page.</param>
/// <param name="ReceivedAt">Arrival time.</param>
public readonly record struct AntBroadcast(ushort DeviceNumber, byte[] Payload, DateTimeOffset ReceivedAt);

/// <summary>
/// One open ANT channel.
/// <para>
/// This is the seam. Everything above it — page parsing, FE-C control, the capability layer —
/// is plain testable code in this assembly. Below it sits either
/// <see cref="SimulatedAntChannelHost"/> or a thin adapter over the ANT Managed Library, which
/// is licence-gated behind a thisisant.com account and so cannot be vendored here.
/// </para>
/// </summary>
public interface IAntChannel : IAsyncDisposable
{
    /// <summary>Configuration this channel was opened with.</summary>
    AntChannelConfig Config { get; }

    /// <summary>Device number once the channel has paired; zero while still searching.</summary>
    ushort DeviceNumber { get; }

    /// <summary>Broadcast pages as they arrive.</summary>
    IObservable<AntBroadcast> Broadcasts { get; }

    /// <summary>
    /// Sends an acknowledged data message — how every FE-C control page is delivered.
    /// Returns false when the device did not acknowledge.
    /// </summary>
    Task<bool> SendAcknowledgedAsync(byte[] page, CancellationToken cancellationToken = default);
}

/// <summary>
/// The USB stick, or a stand-in for it.
/// <para>
/// Implement this over <c>ANT_Managed_Library</c>'s <c>ANT_Device</c> and <c>ANT_Channel</c>
/// to get real hardware: open the device, set the network key, then for each channel assign
/// type 0x00 (bidirectional receive), set the channel ID from
/// <see cref="AntChannelConfig.DeviceType"/>, set RF frequency and period, and open it.
/// </para>
/// </summary>
public interface IAntChannelHost : IAsyncDisposable
{
    /// <summary>Whether a stick is present and the network key has been accepted.</summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>Description of the attached hardware, for logs and the device picker.</summary>
    string Description { get; }

    /// <summary>Opens a channel for a profile.</summary>
    Task<IAntChannel> OpenChannelAsync(
        AntChannelConfig config, CancellationToken cancellationToken = default);
}
