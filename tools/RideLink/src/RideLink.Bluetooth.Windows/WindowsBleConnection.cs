using System.Reactive.Subjects;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using RideLink.Model;
using RideLink.Transport;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace RideLink.Bluetooth.Windows;

/// <summary>
/// One GATT link, backed by a WinRT <see cref="BluetoothLEDevice"/>.
/// <para>
/// Services and characteristics are discovered once and cached by UUID, so callers address
/// endpoints by UUID and no handle is ever hardcoded — a device that renumbers its handles
/// between firmware versions still works.
/// </para>
/// </summary>
public sealed class WindowsBleConnection : IDeviceConnection
{
    private readonly BluetoothLEDevice _device;
    private readonly DiscoveredDevice _descriptor;
    private readonly ILogger _logger;

    private readonly Dictionary<EndpointId, GattCharacteristic> _characteristics = [];
    private readonly List<GattDeviceService> _services = [];
    private readonly BehaviorSubject<ConnectionState> _state = new(ConnectionState.Connected);
    private readonly object _gate = new();

    internal WindowsBleConnection(
        BluetoothLEDevice device, DiscoveredDevice descriptor, ILogger logger)
    {
        _device = device;
        _descriptor = descriptor;
        _logger = logger;

        _device.ConnectionStatusChanged += OnConnectionStatusChanged;
    }

    public string DeviceId => _descriptor.Id;

    public string Name => string.IsNullOrWhiteSpace(_device.Name) ? _descriptor.Name : _device.Name;

    public TransportKind Transport => TransportKind.BluetoothLowEnergy;

    public ConnectionState State => _state.Value;

    public IObservable<ConnectionState> StateChanges => _state;

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        var state = sender.ConnectionStatus == BluetoothConnectionStatus.Connected
            ? ConnectionState.Connected
            : ConnectionState.Disconnected;

        _logger.LogInformation("{Device} link {State}", Name, state);
        _state.OnNext(state);
    }

    public async Task<IReadOnlyList<EndpointDescriptor>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var found = new List<EndpointDescriptor>();

        // Uncached forces a fresh discovery. The Windows GATT cache is per-machine and
        // survives firmware updates, so a cached read can hand back a service list the device
        // no longer has.
        var serviceResult = await _device
            .GetGattServicesAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (serviceResult.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException(
                $"Service discovery on {Name} failed: {serviceResult.Status}");

        foreach (var service in serviceResult.Services)
        {
            lock (_gate) _services.Add(service);

            GattCharacteristicsResult characteristicResult;
            try
            {
                characteristicResult = await service
                    .GetCharacteristicsAsync(BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A service can be present but access-denied (Windows reserves HID, for one).
                _logger.LogDebug(ex, "{Device}: characteristics of {Service} unavailable",
                    Name, service.Uuid);
                continue;
            }

            if (characteristicResult.Status != GattCommunicationStatus.Success)
            {
                _logger.LogDebug("{Device}: {Service} characteristics returned {Status}",
                    Name, service.Uuid, characteristicResult.Status);
                continue;
            }

            foreach (var characteristic in characteristicResult.Characteristics)
            {
                var id = EndpointId.Ble(service.Uuid, characteristic.Uuid);
                lock (_gate) _characteristics[id] = characteristic;
                found.Add(new EndpointDescriptor(id, Translate(characteristic.CharacteristicProperties)));
            }
        }

        _state.OnNext(ConnectionState.Ready);
        return found;
    }

    private static EndpointProperties Translate(GattCharacteristicProperties properties)
    {
        var result = EndpointProperties.None;
        if (properties.HasFlag(GattCharacteristicProperties.Read)) result |= EndpointProperties.Read;
        if (properties.HasFlag(GattCharacteristicProperties.Write)) result |= EndpointProperties.Write;
        if (properties.HasFlag(GattCharacteristicProperties.WriteWithoutResponse))
            result |= EndpointProperties.WriteWithoutResponse;
        if (properties.HasFlag(GattCharacteristicProperties.Notify)) result |= EndpointProperties.Notify;
        if (properties.HasFlag(GattCharacteristicProperties.Indicate)) result |= EndpointProperties.Indicate;
        return result;
    }

    private GattCharacteristic Resolve(EndpointId endpoint)
    {
        lock (_gate)
        {
            if (_characteristics.TryGetValue(endpoint, out var characteristic)) return characteristic;
        }

        throw new InvalidOperationException(
            $"{Name} has no characteristic {endpoint}. Call DiscoverAsync first.");
    }

    public async Task<byte[]> ReadAsync(
        EndpointId endpoint, CancellationToken cancellationToken = default)
    {
        var characteristic = Resolve(endpoint);

        var result = await characteristic
            .ReadValueAsync(BluetoothCacheMode.Uncached)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException(
                $"Read of {endpoint} on {Name} failed: {result.Status}"
                + (result.ProtocolError is { } e ? $" (ATT error 0x{e:X2})" : ""));

        return result.Value.ToArray();
    }

    public async Task WriteAsync(
        EndpointId endpoint,
        ReadOnlyMemory<byte> value,
        WriteMode mode = WriteMode.WithResponse,
        CancellationToken cancellationToken = default)
    {
        var characteristic = Resolve(endpoint);

        var writer = new DataWriter();
        writer.WriteBytes(value.ToArray());

        var option = mode == WriteMode.WithResponse
            ? GattWriteOption.WriteWithResponse
            : GattWriteOption.WriteWithoutResponse;

        var result = await characteristic
            .WriteValueWithResultAsync(writer.DetachBuffer(), option)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException(
                $"Write to {endpoint} on {Name} failed: {result.Status}"
                + (result.ProtocolError is { } e ? $" (ATT error 0x{e:X2})" : ""));
    }

    public async Task<IAsyncDisposable> SubscribeAsync(
        EndpointId endpoint,
        Action<DataPacket> onPacket,
        CancellationToken cancellationToken = default)
    {
        var characteristic = Resolve(endpoint);

        // Indicate is acknowledged and is what the FTMS Control Point requires; notify is the
        // unacknowledged form used by the measurement characteristics. Choose from what the
        // characteristic actually offers rather than assuming.
        var descriptorValue =
            characteristic.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Indicate)
                ? GattClientCharacteristicConfigurationDescriptorValue.Indicate
                : GattClientCharacteristicConfigurationDescriptorValue.Notify;

        void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            // This fires on a WinRT thread pool thread — hence the dispatcher upstream.
            try
            {
                onPacket(new DataPacket(endpoint, args.CharacteristicValue.ToArray(), args.Timestamp));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Device}: handler for {Endpoint} threw", Name, endpoint);
            }
        }

        characteristic.ValueChanged += OnValueChanged;

        // Writing the CCCD is what actually turns the stream on.
        // Core Specification, Vol 3, Part G, §3.3.3.3.
        var status = await characteristic
            .WriteClientCharacteristicConfigurationDescriptorAsync(descriptorValue)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        if (status != GattCommunicationStatus.Success)
        {
            characteristic.ValueChanged -= OnValueChanged;
            throw new InvalidOperationException(
                $"Could not enable {descriptorValue} on {endpoint} of {Name}: {status}");
        }

        _logger.LogDebug("{Device}: subscribed to {Endpoint} ({Mode})", Name, endpoint, descriptorValue);

        return new Unsubscriber(characteristic, OnValueChanged, _logger, Name, endpoint);
    }

    public async ValueTask DisposeAsync()
    {
        _device.ConnectionStatusChanged -= OnConnectionStatusChanged;

        GattDeviceService[] services;
        lock (_gate)
        {
            services = _services.ToArray();
            _services.Clear();
            _characteristics.Clear();
        }

        // Native handles must be released explicitly; leaking them keeps Windows holding the
        // link open and the next connect attempt fails with "already in use".
        foreach (var service in services)
        {
            try { service.Dispose(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Service dispose failed"); }
        }

        _device.Dispose();
        _state.OnNext(ConnectionState.Disconnected);
        _state.Dispose();

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private sealed class Unsubscriber(
        GattCharacteristic characteristic,
        TypedEventHandler<GattCharacteristic, GattValueChangedEventArgs> handler,
        ILogger logger,
        string deviceName,
        EndpointId endpoint) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            characteristic.ValueChanged -= handler;

            try
            {
                await characteristic
                    .WriteClientCharacteristicConfigurationDescriptorAsync(
                        GattClientCharacteristicConfigurationDescriptorValue.None)
                    .AsTask()
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The device is usually already gone by the time we unsubscribe; that is fine.
                logger.LogDebug(ex, "{Device}: clearing CCCD for {Endpoint} failed",
                    deviceName, endpoint);
            }
        }
    }
}
