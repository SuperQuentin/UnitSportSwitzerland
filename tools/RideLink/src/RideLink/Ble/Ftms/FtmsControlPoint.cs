using RideLink.Parsing;

namespace RideLink.Ble.Ftms;

/// <summary>
/// Fitness Machine Control Point op codes. FTMS v1.0, §4.16.1, Table 4.15.
/// </summary>
public enum FtmsOpCode : byte
{
    /// <summary>Must succeed before any Set* command is accepted.</summary>
    RequestControl = 0x00,

    /// <summary>Returns the machine to its default state.</summary>
    Reset = 0x01,

    /// <summary>uint16, units of 0.01 km/h.</summary>
    SetTargetSpeed = 0x02,

    /// <summary>sint16, units of 0.1 percent.</summary>
    SetTargetInclination = 0x03,

    /// <summary>uint8, units of 0.1 (unitless resistance level).</summary>
    SetTargetResistanceLevel = 0x04,

    /// <summary>sint16, watts. This is ERG mode.</summary>
    SetTargetPower = 0x05,

    /// <summary>uint8, bpm.</summary>
    SetTargetHeartRate = 0x06,

    StartOrResume = 0x07,

    /// <summary>uint8 parameter: 0x01 stop, 0x02 pause.</summary>
    StopOrPause = 0x08,

    SetTargetedExpendedEnergy = 0x09,
    SetTargetedNumberOfSteps = 0x0A,
    SetTargetedNumberOfStrides = 0x0B,
    SetTargetedDistance = 0x0C,
    SetTargetedTrainingTime = 0x0D,
    SetTargetedTimeInTwoHeartRateZones = 0x0E,
    SetTargetedTimeInThreeHeartRateZones = 0x0F,
    SetTargetedTimeInFiveHeartRateZones = 0x10,

    /// <summary>
    /// Wind speed (sint16, 0.001 m/s), grade (sint16, 0.01 %), Crr (uint8, 0.0001),
    /// Cw (uint8, 0.01 kg/m). This is SIM mode.
    /// </summary>
    SetIndoorBikeSimulationParameters = 0x11,

    /// <summary>uint16, units of 0.1 mm.</summary>
    SetWheelCircumference = 0x12,

    /// <summary>uint8 control parameter.</summary>
    SpinDownControl = 0x13,

    /// <summary>uint16, units of 0.5 rpm.</summary>
    SetTargetedCadence = 0x14,

    /// <summary>Indicated back by the machine; never written.</summary>
    ResponseCode = 0x80,
}

/// <summary>Result codes carried in a Control Point response. FTMS v1.0, §4.16.2.22, Table 4.24.</summary>
public enum FtmsResultCode : byte
{
    Reserved = 0x00,
    Success = 0x01,
    OpCodeNotSupported = 0x02,
    InvalidParameter = 0x03,
    OperationFailed = 0x04,

    /// <summary>Request Control was not done, or control was taken away.</summary>
    ControlNotPermitted = 0x05,
}

/// <summary>A decoded Control Point indication.</summary>
/// <param name="RequestOpCode">The op code being answered.</param>
/// <param name="Result">Outcome.</param>
/// <param name="Parameters">Response parameters, empty for most op codes.</param>
public readonly record struct FtmsControlResponse(
    FtmsOpCode RequestOpCode,
    FtmsResultCode Result,
    byte[] Parameters)
{
    public bool IsSuccess => Result == FtmsResultCode.Success;
}

/// <summary>
/// Builds Control Point writes and decodes the indications that answer them.
/// <para>FTMS v1.0, §4.16.</para>
/// <para>
/// Every field scaling here comes from the spec's parameter tables. Getting one wrong is
/// silently destructive — a grade sent with the wrong scale gives a 100× hill — so each
/// helper states its unit.
/// </para>
/// </summary>
public static class FtmsControlPoint
{
    /// <summary>Request Control (0x00). No parameters. Must precede any Set*.</summary>
    public static byte[] RequestControl() => [(byte)FtmsOpCode.RequestControl];

    /// <summary>Reset (0x01). No parameters.</summary>
    public static byte[] Reset() => [(byte)FtmsOpCode.Reset];

    /// <summary>Start or Resume (0x07).</summary>
    public static byte[] StartOrResume() => [(byte)FtmsOpCode.StartOrResume];

    /// <summary>Stop (0x08 with parameter 0x01).</summary>
    public static byte[] Stop() => [(byte)FtmsOpCode.StopOrPause, 0x01];

    /// <summary>Pause (0x08 with parameter 0x02).</summary>
    public static byte[] Pause() => [(byte)FtmsOpCode.StopOrPause, 0x02];

    /// <summary>
    /// Set Target Power (0x05): sint16 watts. FTMS v1.0, §4.16.2.6.
    /// </summary>
    public static byte[] SetTargetPower(int watts)
    {
        short value = (short)Math.Clamp(watts, short.MinValue, short.MaxValue);
        return [(byte)FtmsOpCode.SetTargetPower, (byte)(value & 0xFF), (byte)((value >> 8) & 0xFF)];
    }

    /// <summary>
    /// Set Target Resistance Level (0x04): uint8 in units of 0.1. FTMS v1.0, §4.16.2.5.
    /// </summary>
    public static byte[] SetTargetResistanceLevel(double level)
    {
        int raw = (int)Math.Round(level / 0.1);
        return [(byte)FtmsOpCode.SetTargetResistanceLevel, (byte)Math.Clamp(raw, 0, 255)];
    }

    /// <summary>
    /// Set Target Inclination (0x03): sint16 in units of 0.1 percent. FTMS v1.0, §4.16.2.4.
    /// </summary>
    public static byte[] SetTargetInclination(double percent)
    {
        short raw = (short)Math.Clamp(Math.Round(percent / 0.1), short.MinValue, short.MaxValue);
        return [(byte)FtmsOpCode.SetTargetInclination, (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF)];
    }

    /// <summary>
    /// Set Indoor Bike Simulation Parameters (0x11). FTMS v1.0, §4.16.2.18.
    /// <para>
    /// Wind Speed — sint16, units of 0.001 m/s.
    /// Grade — sint16, units of 0.01 percent (so 5% is 500, not 5).
    /// Coefficient of Rolling Resistance — uint8, units of 0.0001.
    /// Wind Resistance Coefficient — uint8, units of 0.01 kg/m.
    /// </para>
    /// </summary>
    /// <param name="gradeFraction">
    /// Grade as a fraction: 0.05 for a 5% climb. The engine works in fractions and converts
    /// here, so callers never touch the spec's percent-times-100 encoding.
    /// </param>
    /// <param name="windSpeedMetersPerSecond">Head wind positive, tail wind negative.</param>
    /// <param name="rollingResistanceCoefficient">Crr, typically 0.003–0.010.</param>
    /// <param name="windResistanceCoefficient">Cw in kg/m, typically 0.3–0.6.</param>
    public static byte[] SetIndoorBikeSimulationParameters(
        double gradeFraction,
        double windSpeedMetersPerSecond = 0,
        double rollingResistanceCoefficient = 0.004,
        double windResistanceCoefficient = 0.51)
    {
        short wind = (short)Math.Clamp(
            Math.Round(windSpeedMetersPerSecond / 0.001), short.MinValue, short.MaxValue);

        // fraction -> percent -> hundredths of a percent
        short grade = (short)Math.Clamp(
            Math.Round(gradeFraction * 100.0 / 0.01), short.MinValue, short.MaxValue);

        byte crr = (byte)Math.Clamp(Math.Round(rollingResistanceCoefficient / 0.0001), 0, 255);
        byte cw = (byte)Math.Clamp(Math.Round(windResistanceCoefficient / 0.01), 0, 255);

        return
        [
            (byte)FtmsOpCode.SetIndoorBikeSimulationParameters,
            (byte)(wind & 0xFF), (byte)((wind >> 8) & 0xFF),
            (byte)(grade & 0xFF), (byte)((grade >> 8) & 0xFF),
            crr,
            cw,
        ];
    }

    /// <summary>
    /// Set Wheel Circumference (0x12): uint16 in units of 0.1 mm. FTMS v1.0, §4.16.2.19.
    /// </summary>
    public static byte[] SetWheelCircumference(double millimetres)
    {
        ushort raw = (ushort)Math.Clamp(Math.Round(millimetres / 0.1), 0, ushort.MaxValue);
        return [(byte)FtmsOpCode.SetWheelCircumference, (byte)(raw & 0xFF), (byte)((raw >> 8) & 0xFF)];
    }

    /// <summary>
    /// Decodes a Control Point indication: 0x80, request op code, result code, [parameters].
    /// FTMS v1.0, §4.16.2.22.
    /// </summary>
    /// <exception cref="RideLinkParseException">
    /// The value is not a Response Code packet. Machines occasionally indicate junk; the
    /// caller treats that as a failed transaction rather than crashing.
    /// </exception>
    public static FtmsControlResponse ParseResponse(ReadOnlySpan<byte> value)
    {
        var reader = new SpanReader(value);
        byte responseOpCode = reader.ReadUInt8("Response Op Code");
        if (responseOpCode != (byte)FtmsOpCode.ResponseCode)
            throw new RideLinkParseException(
                $"Expected Control Point Response Code 0x80, got 0x{responseOpCode:X2}", value, 0);

        var request = (FtmsOpCode)reader.ReadUInt8("Request Op Code");
        var result = (FtmsResultCode)reader.ReadUInt8("Result Code");
        byte[] parameters = reader.PeekRest().ToArray();

        return new FtmsControlResponse(request, result, parameters);
    }
}
