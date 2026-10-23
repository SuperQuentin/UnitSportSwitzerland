using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RideLink.Model;
using RideLink.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace RideLink.Bluetooth.Windows;

/// <summary>
/// BLE central over WinRT's <c>Windows.Devices.Bluetooth</c>.
/// <para>
/// Bound directly rather than through a cross-platform wrapper, because the FTMS Control Point
/// needs explicit indication subscription and acknowledged writes, and WinRT exposes both
/// precisely. The engine above never sees any of this — swapping in Plugin.BLE or Shiny means
/// writing another <see cref="IDeviceTransport"/> and changing nothing else.
/// </para>
/// <para>
/// Windows requires the adapter to be present and on; pairing is handled by the OS. Devices
/// are addressed by their 48-bit Bluetooth address rendered as 12 hex digits, which is stable
/// across sessions and so is usable to reconnect.
/// </para>
/// </summary>
public sealed class WindowsBleTransport : IDeviceTransport
{
    private readonly ILogger _logger;
    private readonly List<WindowsBleConnection> _connections = [];
    private readonly object _gate = new();

    public WindowsBleTransport(ILogger<WindowsBleTransport>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsBleTransport>.Instance;
    }

    public TransportKind Kind => TransportKind.BluetoothLowEnergy;

    public string Name => "Windows BLE (WinRT)";

    /// <summary>
    /// Active scanning asks devices for their scan response, which is where many trainers put
    /// their name. Passive scanning is quieter but often yields unnamed entries.
    /// </summary>
    public bool ActiveScanning { get; init; } = true;

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(cancellationToken)
                .ConfigureAwait(false);

            if (adapter is null)
            {
                _logger.LogWarning("No Bluetooth adapter found");
                return false;
            }

            if (!adapter.IsLowEnergySupported)
            {
                _logger.LogWarning("Bluetooth adapter does not support Low Energy");
                return false;
            }

            var radio = await adapter.GetRadioAsync().AsTask(cancellationToken).ConfigureAwait(false);
            bool on = radio?.State == global::Windows.Devices.Radios.RadioState.On;
            if (!on) _logger.LogWarning("Bluetooth radio is not on (state {State})", radio?.State);
            return on;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not query the Bluetooth adapter");
            return false;
        }
    }

    public async IAsyncEnumerable<DiscoveredDevice> ScanAsync(
        ScanFilter filter,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<DiscoveredDevice>();

        // Advertisement names arrive piecemeal: a device may advertise services in one packet
        // and its name in the scan response. Remembering the best name we have seen keeps the
        // picker from showing "(unnamed)" for a device we already identified.
        var names = new Dictionary<ulong, string>();

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = ActiveScanning
                ? BluetoothLEScanningMode.Active
                : BluetoothLEScanningMode.Passive,
        };

        void OnReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            try
            {
                string address = FormatAddress(args.BluetoothAddress);

                lock (names)
                {
                    if (!string.IsNullOrWhiteSpace(args.Advertisement.LocalName))
                        names[args.BluetoothAddress] = args.Advertisement.LocalName;
                }

                string name;
                lock (names) names.TryGetValue(args.BluetoothAddress, out name!);
                name ??= string.Empty;

                var services = args.Advertisement.ServiceUuids
                    .Select(u => u.ToString("D").ToLowerInvariant())
                    .ToArray();

                if (filter.Services.Count > 0 && !filter.Services.Any(services.Contains)) return;

                if (filter.NameContains is { } needle
                    && !name.Contains(needle, StringComparison.OrdinalIgnoreCase)) return;

                if (filter.MinimumRssi is { } min && args.RawSignalStrengthInDBm < min) return;

                channel.Writer.TryWrite(new DiscoveredDevice(
                    address,
                    name,
                    TransportKind.BluetoothLowEnergy,
                    args.RawSignalStrengthInDBm,
                    services,
                    // BluetoothAddressType would be useful here but needs the 19041 API set;
                    // the minimum supported version is kept lower so this assembly still loads
                    // on 1809, where the rest of the GATT surface is identical.
                    new Dictionary<string, string> { ["address"] = address }));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Advertisement could not be processed");
            }
        }

        watcher.Received += OnReceived;
        watcher.Start();
        _logger.LogInformation("BLE scan started ({Mode})", watcher.ScanningMode);

        using var registration = cancellationToken.Register(() => channel.Writer.TryComplete());

        try
        {
            await foreach (var device in channel.Reader
                .ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                yield return device;
            }
        }
        finally
        {
            watcher.Received -= OnReceived;
            watcher.Stop();
            _logger.LogInformation("BLE scan stopped");
        }
    }

    public async Task<IDeviceConnection> ConnectAsync(
        DiscoveredDevice device, CancellationToken cancellationToken = default)
    {
        ulong address = ParseAddress(device.Id);

        var bleDevice = await BluetoothLEDevice
            .FromBluetoothAddressAsync(address)
            .AsTask(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Could not open {device.Name} ({device.Id}). It may be out of range, or paired "
                + "to another host.");

        var connection = new WindowsBleConnection(bleDevice, device, _logger);
        lock (_gate) _connections.Add(connection);
        return connection;
    }

    internal static string FormatAddress(ulong address) => address.ToString("X12", CultureInfo.InvariantCulture);

    internal static ulong ParseAddress(string id) =>
        ulong.Parse(id.Replace(":", "").Replace("-", ""), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    public async ValueTask DisposeAsync()
    {
        WindowsBleConnection[] connections;
        lock (_gate)
        {
            connections = _connections.ToArray();
            _connections.Clear();
        }

        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
    }
}
