using System.Buffers.Binary;
using RideLink.Model;

namespace RideLink.Simulation;

/// <summary>
/// Builds real GATT characteristic values.
/// <para>
/// The simulated devices encode to the wire format and the engine decodes them with the same
/// parsers it uses on hardware. Handing pre-decoded structs to the capability layer instead
/// would make the simulator prove nothing about the part most likely to be wrong, so the
/// round trip is deliberate.
/// </para>
/// <para>
/// These are also the encoders a peripheral-role implementation would need, and the fixtures
/// the parser tests assert against.
/// </para>
/// </summary>
public static class GattEncoders
{
    /// <summary>
    /// Indoor Bike Data (<c>0x2AD2</c>). FTMS v1.0, §4.9.
    /// <para>
    /// Bit 0 is "More Data" and is left <b>clear</b> when speed is present, matching the
    /// inverted sense in the spec.
    /// </para>
    /// </summary>
    public static byte[] IndoorBikeData(
        double? speedMetersPerSecond = null,
        double? cadenceRpm = null,
        double? totalDistanceMeters = null,
        int? instantaneousPowerWatts = null,
        int? heartRateBpm = null,
        short? resistanceLevel = null,
        TimeSpan? elapsed = null)
    {
        ushort flags = 0;
        var body = new List<byte>();

        if (speedMetersPerSecond is { } speed)
        {
            // More Data stays 0, which is what signals "speed present".
            Write16(body, (ushort)Math.Round(speed / Units.KmhToMs / 0.01));
        }
        else
        {
            flags |= 1 << 0;
        }

        if (cadenceRpm is { } cadence)
        {
            flags |= 1 << 2;
            Write16(body, (ushort)Math.Round(cadence / 0.5));
        }

        if (totalDistanceMeters is { } distance)
        {
            flags |= 1 << 4;
            Write24(body, (uint)Math.Max(0, Math.Round(distance)));
        }

        if (resistanceLevel is { } resistance)
        {
            flags |= 1 << 5;
            Write16(body, unchecked((ushort)resistance));
        }

        if (instantaneousPowerWatts is { } watts)
        {
            flags |= 1 << 6;
            Write16(body, unchecked((ushort)(short)watts));
        }

        if (heartRateBpm is { } bpm)
        {
            flags |= 1 << 9;
            body.Add((byte)bpm);
        }

        if (elapsed is { } time)
        {
            flags |= 1 << 11;
            Write16(body, (ushort)Math.Clamp(time.TotalSeconds, 0, ushort.MaxValue));
        }

        var packet = new List<byte>(2 + body.Count);
        Write16(packet, flags);
        packet.AddRange(body);
        return packet.ToArray();
    }

    /// <summary>
    /// Heart Rate Measurement (<c>0x2A37</c>). Heart Rate Service v1.0, §3.1.
    /// </summary>
    public static byte[] HeartRateMeasurement(
        int bpm,
        bool? sensorContact = true,
        IReadOnlyList<double>? rrIntervalsSeconds = null,
        int? energyExpendedKilojoules = null)
    {
        byte flags = 0;
        var body = new List<byte>();

        bool wide = bpm > byte.MaxValue;
        if (wide)
        {
            flags |= 1 << 0;
            Write16(body, (ushort)bpm);
        }
        else
        {
            body.Add((byte)bpm);
        }

        if (sensorContact is { } contact)
        {
            flags |= 1 << 2;                       // contact feature supported
            if (contact) flags |= 1 << 1;          // contact detected
        }

        if (energyExpendedKilojoules is { } energy)
        {
            flags |= 1 << 3;
            Write16(body, (ushort)energy);
        }

        if (rrIntervalsSeconds is { Count: > 0 })
        {
            flags |= 1 << 4;
            foreach (double seconds in rrIntervalsSeconds)
                Write16(body, (ushort)Math.Round(seconds * 1024.0));
        }

        var packet = new List<byte>(1 + body.Count) { flags };
        packet.AddRange(body);
        return packet.ToArray();
    }

    /// <summary>
    /// Cycling Power Measurement (<c>0x2A63</c>). CPS v1.1, §3.1.
    /// </summary>
    /// <param name="watts">Instantaneous power.</param>
    /// <param name="cumulativeCrankRevolutions">uint16 counter, wraps naturally.</param>
    /// <param name="lastCrankEventTime">uint16 counter in 1/1024 s.</param>
    /// <param name="cumulativeWheelRevolutions">uint32 counter.</param>
    /// <param name="lastWheelEventTime">uint16 counter in 1/2048 s.</param>
    public static byte[] CyclingPowerMeasurement(
        int watts,
        ushort? cumulativeCrankRevolutions = null,
        ushort? lastCrankEventTime = null,
        uint? cumulativeWheelRevolutions = null,
        ushort? lastWheelEventTime = null)
    {
        ushort flags = 0;
        var body = new List<byte>();
        Write16(body, unchecked((ushort)(short)watts));

        if (cumulativeWheelRevolutions is { } wheelRevs && lastWheelEventTime is { } wheelTime)
        {
            flags |= 1 << 4;
            Write32(body, wheelRevs);
            Write16(body, wheelTime);
        }

        if (cumulativeCrankRevolutions is { } crankRevs && lastCrankEventTime is { } crankTime)
        {
            flags |= 1 << 5;
            Write16(body, crankRevs);
            Write16(body, crankTime);
        }

        var packet = new List<byte>(2 + body.Count);
        Write16(packet, flags);
        packet.AddRange(body);
        return packet.ToArray();
    }

    /// <summary>
    /// CSC Measurement (<c>0x2A5B</c>). CSCS v1.0, §3.1. Both event times are 1/1024 s.
    /// </summary>
    public static byte[] CscMeasurement(
        uint? cumulativeWheelRevolutions = null,
        ushort? lastWheelEventTime = null,
        ushort? cumulativeCrankRevolutions = null,
        ushort? lastCrankEventTime = null)
    {
        byte flags = 0;
        var body = new List<byte>();

        if (cumulativeWheelRevolutions is { } wheelRevs && lastWheelEventTime is { } wheelTime)
        {
            flags |= 1 << 0;
            Write32(body, wheelRevs);
            Write16(body, wheelTime);
        }

        if (cumulativeCrankRevolutions is { } crankRevs && lastCrankEventTime is { } crankTime)
        {
            flags |= 1 << 1;
            Write16(body, crankRevs);
            Write16(body, crankTime);
        }

        var packet = new List<byte>(1 + body.Count) { flags };
        packet.AddRange(body);
        return packet.ToArray();
    }

    /// <summary>
    /// RSC Measurement (<c>0x2A53</c>). RSCS v1.0, §3.1.
    /// </summary>
    public static byte[] RscMeasurement(
        double speedMetersPerSecond,
        int cadenceStepsPerMinute,
        double? strideLengthMeters = null,
        double? totalDistanceMeters = null,
        bool running = true)
    {
        byte flags = 0;
        var body = new List<byte>();
        Write16(body, (ushort)Math.Round(speedMetersPerSecond * 256.0));
        body.Add((byte)Math.Clamp(cadenceStepsPerMinute, 0, 255));

        if (strideLengthMeters is { } stride)
        {
            flags |= 1 << 0;
            Write16(body, (ushort)Math.Round(stride / 0.01));
        }

        if (totalDistanceMeters is { } distance)
        {
            flags |= 1 << 1;
            Write32(body, (uint)Math.Round(distance / 0.1));
        }

        if (running) flags |= 1 << 2;

        var packet = new List<byte>(1 + body.Count) { flags };
        packet.AddRange(body);
        return packet.ToArray();
    }

    /// <summary>Fitness Machine Feature (<c>0x2ACC</c>): two uint32 fields. FTMS v1.0, §4.3.</summary>
    public static byte[] FitnessMachineFeature(uint features, uint targetFeatures)
    {
        var packet = new List<byte>(8);
        Write32(packet, features);
        Write32(packet, targetFeatures);
        return packet.ToArray();
    }

    /// <summary>Supported Power Range (<c>0x2AD8</c>): sint16, sint16, uint16 — watts.</summary>
    public static byte[] SupportedPowerRange(int minWatts, int maxWatts, int incrementWatts)
    {
        var packet = new List<byte>(6);
        Write16(packet, unchecked((ushort)(short)minWatts));
        Write16(packet, unchecked((ushort)(short)maxWatts));
        Write16(packet, (ushort)incrementWatts);
        return packet.ToArray();
    }

    /// <summary>Supported Resistance Level Range (<c>0x2AD6</c>): units of 0.1.</summary>
    public static byte[] SupportedResistanceLevelRange(double min, double max, double increment)
    {
        var packet = new List<byte>(6);
        Write16(packet, unchecked((ushort)(short)Math.Round(min / 0.1)));
        Write16(packet, unchecked((ushort)(short)Math.Round(max / 0.1)));
        Write16(packet, (ushort)Math.Round(increment / 0.1));
        return packet.ToArray();
    }

    /// <summary>Control Point response: 0x80, request op code, result code. FTMS v1.0, §4.16.2.22.</summary>
    public static byte[] ControlPointResponse(byte requestOpCode, byte resultCode) =>
        [0x80, requestOpCode, resultCode];

    /// <summary>Fitness Machine Status (<c>0x2ADA</c>): op code plus parameters.</summary>
    public static byte[] FitnessMachineStatus(byte opCode, params byte[] parameters)
    {
        var packet = new List<byte>(1 + parameters.Length) { opCode };
        packet.AddRange(parameters);
        return packet.ToArray();
    }

    private static void Write16(List<byte> into, ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(tmp, value);
        into.Add(tmp[0]);
        into.Add(tmp[1]);
    }

    private static void Write24(List<byte> into, uint value)
    {
        into.Add((byte)(value & 0xFF));
        into.Add((byte)((value >> 8) & 0xFF));
        into.Add((byte)((value >> 16) & 0xFF));
    }

    private static void Write32(List<byte> into, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(tmp, value);
        for (int i = 0; i < 4; i++) into.Add(tmp[i]);
    }
}
