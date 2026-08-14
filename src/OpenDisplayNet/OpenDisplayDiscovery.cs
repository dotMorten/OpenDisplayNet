using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace OpenDisplayNet;

/// <summary>Discovers nearby OpenDisplay peripherals.</summary>
public static class OpenDisplayDiscovery
{
    /// <summary>OpenDisplay's Bluetooth manufacturer identifier.</summary>
    public const ushort ManufacturerId = 0x2446;

    /// <summary>Scans for OpenDisplay Bluetooth advertisements for the specified duration.</summary>
    public static async Task<IReadOnlyList<OpenDisplayDevice>> DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default)
    {
        if (scanDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scanDuration), "Scan duration must be positive.");
        }

        Dictionary<ulong, OpenDisplayDevice> devices = [];
        BluetoothLEAdvertisementWatcher watcher = new();
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
            new BluetoothLEManufacturerData { CompanyId = ManufacturerId });

        watcher.Received += OnAdvertisementReceived;
        watcher.Start();

        try
        {
            await Task.Delay(scanDuration, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnAdvertisementReceived;
        }

        return devices.Values
            .OrderByDescending(device => device.Rssi)
            .ThenBy(device => device.Name, StringComparer.Ordinal)
            .ToArray();

        void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            string name = args.Advertisement.LocalName;
            devices[args.BluetoothAddress] = new OpenDisplayDevice(
                args.BluetoothAddress,
                args.BluetoothAddressType,
                string.IsNullOrWhiteSpace(name) ? FormatBluetoothAddress(args.BluetoothAddress) : name,
                args.RawSignalStrengthInDBm);
        }
    }

    /// <summary>Continuously yields newly discovered OpenDisplay peripherals until cancelled.</summary>
    public static async IAsyncEnumerable<OpenDisplayDevice> ScanAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ConcurrentDictionary<ulong, byte> discoveredAddresses = [];
        Channel<OpenDisplayDevice> devices = Channel.CreateUnbounded<OpenDisplayDevice>();
        BluetoothLEAdvertisementWatcher watcher = new();
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(
            new BluetoothLEManufacturerData { CompanyId = ManufacturerId });

        watcher.Received += OnAdvertisementReceived;
        watcher.Start();
        Task pairedDeviceDiscovery = DiscoverPairedDevicesAfterDelayAsync(
            discoveredAddresses,
            devices.Writer,
            cancellationToken);

        try
        {
            await foreach (OpenDisplayDevice device in devices.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return device;
            }
        }
        finally
        {
            watcher.Stop();
            watcher.Received -= OnAdvertisementReceived;
            devices.Writer.TryComplete();
            try
            {
                await pairedDeviceDiscovery.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        void OnAdvertisementReceived(BluetoothLEAdvertisementWatcher _, BluetoothLEAdvertisementReceivedEventArgs args)
        {
            if (!discoveredAddresses.TryAdd(args.BluetoothAddress, 0))
            {
                return;
            }

            string name = args.Advertisement.LocalName;
            devices.Writer.TryWrite(new OpenDisplayDevice(
                args.BluetoothAddress,
                args.BluetoothAddressType,
                string.IsNullOrWhiteSpace(name) ? FormatBluetoothAddress(args.BluetoothAddress) : name,
                args.RawSignalStrengthInDBm));
        }
    }

    private static async Task DiscoverPairedDevicesAfterDelayAsync(
        ConcurrentDictionary<ulong, byte> discoveredAddresses,
        ChannelWriter<OpenDisplayDevice> devices,
        CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        if (!discoveredAddresses.IsEmpty)
        {
            return;
        }

        await DiscoverPairedDevicesAsync(discoveredAddresses, devices, cancellationToken).ConfigureAwait(false);
    }

    private static async Task DiscoverPairedDevicesAsync(
        ConcurrentDictionary<ulong, byte> discoveredAddresses,
        ChannelWriter<OpenDisplayDevice> devices,
        CancellationToken cancellationToken)
    {
        DeviceInformationCollection pairedDevices = await DeviceInformation
            .FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        foreach (DeviceInformation pairedDevice in pairedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BluetoothLEDevice? device = await BluetoothLEDevice
                .FromIdAsync(pairedDevice.Id)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (device is null)
            {
                continue;
            }

            try
            {
                GattDeviceServicesResult services = await device
                    .GetGattServicesForUuidAsync(OpenDisplayClient.ServiceUuid, BluetoothCacheMode.Uncached)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    string name = string.IsNullOrWhiteSpace(device.Name) ? pairedDevice.Name : device.Name;
                    bool exposesOpenDisplayService =
                        services.Status == GattCommunicationStatus.Success &&
                        services.Services.Count > 0;
                    if (!exposesOpenDisplayService && !IsKnownOpenDisplayName(name))
                    {
                        continue;
                    }

                    if (!discoveredAddresses.TryAdd(device.BluetoothAddress, 0))
                    {
                        continue;
                    }

                    devices.TryWrite(new OpenDisplayDevice(
                        device.BluetoothAddress,
                        device.BluetoothAddressType,
                        string.IsNullOrWhiteSpace(name) ? FormatBluetoothAddress(device.BluetoothAddress) : name,
                        Rssi: null));
                }
                finally
                {
                    foreach (GattDeviceService service in services.Services)
                    {
                        service.Dispose();
                    }
                }
            }
            finally
            {
                device.Dispose();
            }
        }
    }

    private static bool IsKnownOpenDisplayName(string name)
        => name.Contains("OpenDisplay", StringComparison.OrdinalIgnoreCase) ||
           name.Contains("XIAO", StringComparison.OrdinalIgnoreCase);

    private static string FormatBluetoothAddress(ulong address)
        => string.Create(
            17,
            address,
            static (span, value) =>
            {
                for (int index = 0; index < 6; index++)
                {
                    int shift = (5 - index) * 8;
                    value.TryFormat(span[(index * 3)..(index * 3 + 2)], out _, "X2");
                    if (index < 5)
                    {
                        span[index * 3 + 2] = ':';
                    }
                }
            });
}
