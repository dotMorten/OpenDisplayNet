# OpenDisplayNet

`OpenDisplayNet` is a standalone .NET library for discovering and controlling
[OpenDisplay](https://opendisplay.org/) Bluetooth Low Energy displays from
Windows. It implements the OpenDisplay BLE protocol, including fast
`PIPE_WRITE` image transfer, for applications that want to render their own
content on e-paper displays.

## Features

- Discover advertising and paired OpenDisplay BLE devices.
- Read display configuration, panel dimensions, firmware information, sensors,
  and manufacturer data.
- Upload monochrome or caller-encoded images through Direct Write or negotiated
  `PIPE_WRITE`, with firmware-compatible compression.
- Authenticate encrypted sessions, send partial updates, and control LEDs and
  buzzers.

## Quick start

Discover a device, connect to it, and send an MSB-first 1-bit image:

```csharp
using OpenDisplayNet;

IReadOnlyList<OpenDisplayDevice> devices = await OpenDisplayDiscovery
    .DiscoverAsync(TimeSpan.FromSeconds(10));

OpenDisplayDevice device = devices.First();
await using OpenDisplayClient client = await OpenDisplayClient.ConnectAsync(device);

OpenDisplayPanelSize panel = await client.GetPanelSizeAsync();
int stride = (panel.Width + 7) / 8;
byte[] whiteFrame = Enumerable.Repeat((byte)0xFF, stride * panel.Height).ToArray();

await client.SendMonochromeImageAsync(panel.Width, panel.Height, whiteFrame);
```

For Gray4 and color panels, encode the image according to the panel's
`ColorScheme` and call `SendImageAsync`. The interactive test application
demonstrates configuration-aware test-pattern generation.

## Projects

| Project | Purpose |
| --- | --- |
| `src/OpenDisplayNet` | The Windows BLE client library. |
| `samples/OpenDisplayNet.Test` | Interactive display discovery and test-pattern uploader. |
| `tests/OpenDisplayNet.Tests` | Protocol serialization and parsing tests. |

## Build and run

```powershell
dotnet build OpenDisplayNet.slnx
dotnet test OpenDisplayNet.slnx
dotnet run --project samples\OpenDisplayNet.Test\OpenDisplayNet.Test.csproj
```

The test application scans until a device is selected, reads its panel
configuration, and uploads a matching test image.

## OpenDisplay resources

- [OpenDisplay website and firmware toolbox](https://opendisplay.org/)
- [OpenDisplay protocol specification](https://github.com/OpenDisplay/opendisplay-protocol)
- [OpenDisplay firmware and hardware documentation](https://github.com/OpenDisplay/Firmware)
- [Reference Python implementation](https://github.com/OpenDisplay/py-opendisplay)
