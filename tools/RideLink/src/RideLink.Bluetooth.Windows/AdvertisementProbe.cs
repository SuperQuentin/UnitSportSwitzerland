using System.Text;
using Windows.Devices.Bluetooth.Advertisement;

namespace RideLink.Bluetooth.Windows;

/// <summary>
/// Dumps what BLE advertisements actually contain, per device.
///
/// <para>
/// Exists because "every device is unnamed" has several possible causes that look identical
/// from the outside, and guessing between them wastes time. A BLE advertisement is only 31
/// bytes, so a device that wants to advertise service UUIDs and manufacturer data often has no
/// room for its name and puts it in the <i>scan response</i> — a second packet sent only when
/// an active scanner asks for one. Other devices simply never advertise a name at all and
/// expect you to read Device Information after connecting.
/// </para>
///
/// <para>
/// This reports the AD types present (0x08 shortened name, 0x09 complete name, 0x02/0x03
/// 16-bit service UUIDs, 0xFF manufacturer data), whether a scan response ever arrived, and
/// whether any packet carried a name — which is enough to tell those cases apart.
/// </para>
/// </summary>
public static class AdvertisementProbe
{
    private sealed class Seen
    {
        public int Packets;
        public int ScanResponses;
        public string? Name;
        public int? Rssi;
        public readonly SortedSet<byte> DataTypes = new();
        public readonly SortedSet<string> Services = new();
    }

    public static async Task RunAsync(TimeSpan duration, Action<string> report,
        CancellationToken cancellationToken = default)
    {
        var seen = new Dictionary<ulong, Seen>();
        var gate = new object();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            lock (gate)
            {
                if (!seen.TryGetValue(args.BluetoothAddress, out var entry))
                    seen[args.BluetoothAddress] = entry = new Seen();

                entry.Packets++;
                entry.Rssi = args.RawSignalStrengthInDBm;

                if (args.AdvertisementType == BluetoothLEAdvertisementType.ScanResponse)
                    entry.ScanResponses++;

                if (!string.IsNullOrWhiteSpace(args.Advertisement.LocalName))
                    entry.Name = args.Advertisement.LocalName;

                foreach (var section in args.Advertisement.DataSections)
                    entry.DataTypes.Add(section.DataType);

                foreach (var uuid in args.Advertisement.ServiceUuids)
                    entry.Services.Add(Short(uuid));
            }
        }

        watcher.Received += OnReceived;
        watcher.Start();

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stop();
        }

        lock (gate)
        {
            report($"{seen.Count} devices seen in {duration.TotalSeconds:F0}s");
            report("");
            report($"{"address",-14} {"kind",-9} {"pkts",4} {"rsp",4}  {"name",-20} {"AD types",-22} services");

            foreach (var (address, entry) in seen.OrderByDescending(kv => kv.Value.Rssi ?? -200))
            {
                string types = string.Join(" ", entry.DataTypes.Select(t => $"{t:X2}{Describe(t)}"));
                report($"{address:X12}  {AddressKind(address),-9} {entry.Packets,4} {entry.ScanResponses,4}  "
                     + $"{entry.Name ?? "-",-20} {types,-22} {string.Join(",", entry.Services)}");
            }

            report("");
            int named = seen.Values.Count(v => v.Name != null);
            int withScanResponse = seen.Values.Count(v => v.ScanResponses > 0);
            int withNameAdType = seen.Values.Count(v => v.DataTypes.Contains(0x08) || v.DataTypes.Contains(0x09));

            report($"named: {named}/{seen.Count}   "
                 + $"sent a scan response: {withScanResponse}/{seen.Count}   "
                 + $"carried a name AD type (08/09): {withNameAdType}/{seen.Count}");
        }
    }

    /// <summary>
    /// Classifies the address by its top two bits, per Core Spec Vol 6 Part B §1.3.
    ///
    /// <para>
    /// This is what explains a scan list that keeps changing. A <c>resolvable</c> address is a
    /// privacy address that its owner re-randomises every fifteen minutes or so, so phones,
    /// watches and laptops appear as a stream of different "devices" — and since they are
    /// broadcasting proximity beacons rather than offering services, they have no name and no
    /// service UUIDs to show. A fitness sensor you can actually connect to is normally
    /// <c>public</c> or <c>static</c>, and keeps the same address between sessions.
    /// </para>
    /// </summary>
    private static string AddressKind(ulong address)
    {
        byte top = (byte)(address >> 40);
        return (top & 0xC0) switch
        {
            0xC0 => "static",       // random static: fixed for the power cycle
            0x40 => "resolvable",   // rotating privacy address
            0x00 => "nonresolv",
            _ => "public",
        };
    }

    private static string Describe(byte dataType) => dataType switch
    {
        0x01 => "=flags",
        0x02 or 0x03 => "=uuid16",
        0x06 or 0x07 => "=uuid128",
        0x08 => "=shortname",
        0x09 => "=name",
        0x0A => "=txpower",
        0x16 => "=svcdata",
        0xFF => "=mfr",
        _ => "",
    };

    private static string Short(Guid uuid)
    {
        var bytes = uuid.ToByteArray();
        // the Bluetooth base UUID is 0000xxxx-0000-1000-8000-00805F9B34FB
        var text = uuid.ToString("D");
        return text.StartsWith("0000", StringComparison.OrdinalIgnoreCase)
               && text.EndsWith("-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase)
            ? "0x" + text.Substring(4, 4).ToUpperInvariant()
            : text;
    }
}
