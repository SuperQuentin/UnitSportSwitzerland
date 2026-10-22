namespace RideLink.Model;

/// <summary>
/// Unit conversions used by the parsers.
/// <para>
/// Every one of these exists because a spec field is transmitted in a scaled integer unit
/// that is *not* the engine's internal unit. Centralising them keeps the scaling factor
/// next to a citation instead of scattered through parser code as magic numbers.
/// </para>
/// </summary>
public static class Units
{
    /// <summary>km/h to m/s.</summary>
    public const double KmhToMs = 1000.0 / 3600.0;

    /// <summary>
    /// FTMS transmits speeds as uint16 in units of 0.01 km/h.
    /// GATT Specification Supplement, "Instantaneous Speed" field.
    /// </summary>
    public static double FtmsSpeedToMs(ushort raw) => raw * 0.01 * KmhToMs;

    /// <summary>
    /// FTMS transmits cadence as uint16 in units of 0.5 rpm.
    /// GATT Specification Supplement, "Instantaneous Cadence" field.
    /// </summary>
    public static double FtmsCadenceToRpm(ushort raw) => raw * 0.5;

    /// <summary>FTMS inclination is sint16 in units of 0.1 percent.</summary>
    public static double FtmsInclinationToPercent(short raw) => raw * 0.1;

    /// <summary>FTMS resistance level is sint16 in units of 0.1 (unitless).</summary>
    public static double FtmsResistanceToLevel(short raw) => raw * 0.1;

    /// <summary>
    /// RSC transmits instantaneous speed as uint16 in units of 1/256 m/s.
    /// GATT Specification Supplement, "RSC Measurement".
    /// </summary>
    public static double RscSpeedToMs(ushort raw) => raw / 256.0;

    /// <summary>RSC total distance is uint32 in units of 0.1 m (decimetres).</summary>
    public static double RscDistanceToMeters(uint raw) => raw * 0.1;

    /// <summary>RSC instantaneous stride length is uint16 in centimetres.</summary>
    public static double RscStrideLengthToMeters(ushort raw) => raw * 0.01;

    /// <summary>Heart Rate RR intervals are uint16 in units of 1/1024 s.</summary>
    public static double RrIntervalToSeconds(ushort raw) => raw / 1024.0;

    /// <summary>Rower pace is uint16 seconds per 500 m; convert to m/s. Zero pace means stopped.</summary>
    public static double RowerPaceToMs(ushort secondsPer500m) =>
        secondsPer500m == 0 ? 0 : 500.0 / secondsPer500m;

    /// <summary>
    /// Treadmill pace is uint8 in units of 0.1 km/min (kilometres per minute). Zero means stopped.
    /// </summary>
    public static double TreadmillPaceToMs(byte raw) => raw * 0.1 * 1000.0 / 60.0;

    /// <summary>Grade as a fraction (0.05 = 5%) from a percentage.</summary>
    public static double PercentToFraction(double percent) => percent / 100.0;

    /// <summary>Grade as a percentage from a fraction.</summary>
    public static double FractionToPercent(double fraction) => fraction * 100.0;
}
