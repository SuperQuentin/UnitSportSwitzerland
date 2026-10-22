namespace RideLink.Ble;

/// <summary>
/// Assigned 16-bit UUIDs for the services and characteristics the engine understands, plus
/// expansion to the full 128-bit form.
/// <para>
/// Source: Bluetooth SIG "Assigned Numbers" document, §3.4 (GATT services) and §3.8
/// (characteristics). Values are 16-bit aliases inside the Bluetooth Base UUID.
/// </para>
/// </summary>
public static class BleUuids
{
    /// <summary>
    /// Bluetooth Base UUID: <c>00000000-0000-1000-8000-00805F9B34FB</c>.
    /// Core Specification, Vol 3, Part B, §2.5.1.
    /// </summary>
    public static Guid FromShort(ushort shortUuid) =>
        new(shortUuid, 0x0000, 0x1000, 0x80, 0x00, 0x00, 0x80, 0x5F, 0x9B, 0x34, 0xFB);

    /// <summary>True when a 128-bit UUID is the expansion of a 16-bit assigned number.</summary>
    public static bool TryGetShort(Guid uuid, out ushort shortUuid)
    {
        shortUuid = 0;
        Span<byte> bytes = stackalloc byte[16];
        if (!uuid.TryWriteBytes(bytes)) return false;

        Span<byte> baseBytes = stackalloc byte[16];
        FromShort(0).TryWriteBytes(baseBytes);

        // Guid.TryWriteBytes emits the mixed-endian layout; only the first two octets carry
        // the 16-bit alias, so compare everything after them.
        for (int i = 2; i < 16; i++)
            if (bytes[i] != baseBytes[i]) return false;

        shortUuid = (ushort)(bytes[0] | (bytes[1] << 8));
        return true;
    }

    // ---- Services -----------------------------------------------------------------

    /// <summary>Fitness Machine Service. FTMS v1.0, §3.</summary>
    public const ushort FitnessMachineService = 0x1826;

    /// <summary>Cycling Power Service. CPS v1.1, §3.</summary>
    public const ushort CyclingPowerService = 0x1818;

    /// <summary>Cycling Speed and Cadence Service. CSCS v1.0, §3.</summary>
    public const ushort CyclingSpeedAndCadenceService = 0x1816;

    /// <summary>Heart Rate Service. HRS v1.0, §3.</summary>
    public const ushort HeartRateService = 0x180D;

    /// <summary>Running Speed and Cadence Service. RSCS v1.0, §3.</summary>
    public const ushort RunningSpeedAndCadenceService = 0x1814;

    /// <summary>Device Information Service. DIS v1.1.</summary>
    public const ushort DeviceInformationService = 0x180A;

    /// <summary>Battery Service. BAS v1.0.</summary>
    public const ushort BatteryService = 0x180F;

    /// <summary>
    /// The services that make a device interesting to this library, in the order a rider would
    /// think of them. A scan that ignores these is a scan that offers you your headphones.
    /// </summary>
    public static readonly IReadOnlyList<ushort> FitnessServices = new[]
    {
        FitnessMachineService,
        CyclingPowerService,
        CyclingSpeedAndCadenceService,
        HeartRateService,
        RunningSpeedAndCadenceService,
    };

    /// <summary>
    /// Plain-language name for a service, for scan listings.
    ///
    /// <para>
    /// Worth having because a BLE advertisement often carries no local name at all — Windows
    /// only learns one after connecting or pairing — so the advertised service UUIDs are
    /// frequently the only thing that says what a device is. An unnamed strap advertising
    /// 0x180D is a heart rate monitor, and saying so is the difference between a usable scan
    /// list and seven rows of "(unnamed)".
    /// </para>
    /// </summary>
    public static string? DescribeService(ushort shortUuid) => shortUuid switch
    {
        FitnessMachineService => "trainer",
        CyclingPowerService => "power",
        CyclingSpeedAndCadenceService => "speed/cadence",
        HeartRateService => "heart rate",
        RunningSpeedAndCadenceService => "run speed/cadence",
        _ => null,
    };

    public static string? DescribeService(Guid uuid) =>
        TryGetShort(uuid, out ushort s) ? DescribeService(s) : null;

    /// <summary>Comma-joined roles for the advertised services this library understands.</summary>
    public static string DescribeServices(IEnumerable<string> advertised)
    {
        var roles = new List<string>();
        foreach (string raw in advertised)
        {
            if (!Guid.TryParse(raw, out var uuid)) continue;
            if (DescribeService(uuid) is { } role && !roles.Contains(role)) roles.Add(role);
        }
        return string.Join(", ", roles);
    }

    /// <summary>True when the advertisement carries any service this library can use.</summary>
    public static bool IsFitnessDevice(IEnumerable<string> advertised) =>
        DescribeServices(advertised).Length > 0;

    // ---- Fitness Machine characteristics -------------------------------------------

    /// <summary>Fitness Machine Feature. FTMS v1.0, §4.3. Read first to learn capabilities.</summary>
    public const ushort FitnessMachineFeature = 0x2ACC;

    /// <summary>Treadmill Data. FTMS v1.0, §4.4.</summary>
    public const ushort TreadmillData = 0x2ACD;

    /// <summary>Cross Trainer Data. FTMS v1.0, §4.5.</summary>
    public const ushort CrossTrainerData = 0x2ACE;

    /// <summary>Rower Data. FTMS v1.0, §4.7.</summary>
    public const ushort RowerData = 0x2AD1;

    /// <summary>Indoor Bike Data. FTMS v1.0, §4.9.</summary>
    public const ushort IndoorBikeData = 0x2AD2;

    /// <summary>Training Status. FTMS v1.0, §4.10.</summary>
    public const ushort TrainingStatus = 0x2AD3;

    /// <summary>Supported Resistance Level Range. FTMS v1.0, §4.12.</summary>
    public const ushort SupportedResistanceLevelRange = 0x2AD6;

    /// <summary>Supported Power Range. FTMS v1.0, §4.14.</summary>
    public const ushort SupportedPowerRange = 0x2AD8;

    /// <summary>Fitness Machine Control Point. FTMS v1.0, §4.16. Write + Indicate.</summary>
    public const ushort FitnessMachineControlPoint = 0x2AD9;

    /// <summary>Fitness Machine Status. FTMS v1.0, §4.17. Notify.</summary>
    public const ushort FitnessMachineStatus = 0x2ADA;

    // ---- Cycling Power characteristics ---------------------------------------------

    /// <summary>Cycling Power Measurement. CPS v1.1, §3.1. Notify.</summary>
    public const ushort CyclingPowerMeasurement = 0x2A63;

    /// <summary>Cycling Power Feature. CPS v1.1, §3.3. Read.</summary>
    public const ushort CyclingPowerFeature = 0x2A65;

    /// <summary>Cycling Power Control Point. CPS v1.1, §3.5. Write + Indicate.</summary>
    public const ushort CyclingPowerControlPoint = 0x2A66;

    /// <summary>Sensor Location, shared by CPS and CSCS.</summary>
    public const ushort SensorLocation = 0x2A5D;

    // ---- Speed and cadence ----------------------------------------------------------

    /// <summary>CSC Measurement. CSCS v1.0, §3.1. Notify.</summary>
    public const ushort CscMeasurement = 0x2A5B;

    /// <summary>CSC Feature. CSCS v1.0, §3.2. Read.</summary>
    public const ushort CscFeature = 0x2A5C;

    /// <summary>RSC Measurement. RSCS v1.0, §3.1. Notify.</summary>
    public const ushort RscMeasurement = 0x2A53;

    /// <summary>RSC Feature. RSCS v1.0, §3.2. Read.</summary>
    public const ushort RscFeature = 0x2A54;

    // ---- Heart rate, battery, device information -------------------------------------

    /// <summary>Heart Rate Measurement. HRS v1.0, §3.1. Notify.</summary>
    public const ushort HeartRateMeasurement = 0x2A37;

    /// <summary>Body Sensor Location. HRS v1.0, §3.2. Read.</summary>
    public const ushort BodySensorLocation = 0x2A38;

    /// <summary>Battery Level, percent. BAS v1.0, §3.1.</summary>
    public const ushort BatteryLevel = 0x2A19;

    /// <summary>Manufacturer Name String. DIS v1.1.</summary>
    public const ushort ManufacturerNameString = 0x2A29;

    /// <summary>Model Number String. DIS v1.1.</summary>
    public const ushort ModelNumberString = 0x2A24;

    /// <summary>Serial Number String. DIS v1.1.</summary>
    public const ushort SerialNumberString = 0x2A25;

    /// <summary>Firmware Revision String. DIS v1.1.</summary>
    public const ushort FirmwareRevisionString = 0x2A26;

    /// <summary>
    /// Client Characteristic Configuration Descriptor. Core Specification, Vol 3, Part G,
    /// §3.3.3.3. Writing 0x0001 enables notifications, 0x0002 enables indications; every
    /// measurement characteristic needs this before it will report anything.
    /// </summary>
    public const ushort ClientCharacteristicConfiguration = 0x2902;
}
