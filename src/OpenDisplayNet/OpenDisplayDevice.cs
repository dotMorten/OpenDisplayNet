using Windows.Devices.Bluetooth;

namespace OpenDisplayNet;

/// <summary>Identifies an OpenDisplay peripheral found through Bluetooth Low Energy advertising.</summary>
public sealed record OpenDisplayDevice(
    ulong BluetoothAddress,
    BluetoothAddressType BluetoothAddressType,
    string Name,
    short? Rssi);
