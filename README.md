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
- Upload `System.Drawing.Bitmap` instances or image files with panel-aware
  palette reduction and ordered dithering, or send caller-encoded frames through
  Direct Write or negotiated `PIPE_WRITE`.
- Authenticate encrypted sessions, send partial updates, and control LEDs and
  buzzers.

## Quick start

Discover a device, connect to it, and upload a bitmap. The library resizes it
to the panel and converts it to the panel's configured color scheme:

```csharp
using System.Drawing;
using OpenDisplayNet;

IReadOnlyList<OpenDisplayDevice> devices = await OpenDisplayDiscovery
    .DiscoverAsync(TimeSpan.FromSeconds(10));

OpenDisplayDevice device = devices.First();
using OpenDisplayClient client = await OpenDisplayClient.ConnectAsync(device);

using Bitmap bitmap = new("dashboard.png");
using OpenDisplayImage image = new(bitmap);
await client.SendImageAsync(image);

// Or load directly from a file.
using OpenDisplayImage fileImage = new("dashboard.png");
await client.SendImageAsync(fileImage);
```

Create `OpenDisplayImage` from a caller-encoded frame when conversion is
handled elsewhere:

```csharp
using OpenDisplayImage encodedImage = new(encodedFrame);
await client.SendImageAsync(encodedImage);
```

## Projects

| Project | Purpose |
| --- | --- |
| `src/OpenDisplayNet` | The Windows BLE client library. |
| `samples/ConsoleTest` | Interactive display discovery and test-pattern uploader. |
| `samples/WinApp` | Reactor-based device discovery, image upload, and camera UI. |
| `tests/OpenDisplayNet.Tests` | Protocol serialization and parsing tests. |

## Build and run

```powershell
dotnet build OpenDisplayNet.slnx
dotnet test OpenDisplayNet.slnx
dotnet run --project samples\ConsoleTest\ConsoleTest.csproj
dotnet run --project samples\WinApp\WinApp.csproj -p:Platform=x64
```

The test application scans until a device is selected, reads its panel
configuration, and uploads a matching test image.

## OpenDisplay resources

- [OpenDisplay website and firmware toolbox](https://opendisplay.org/)
- [OpenDisplay protocol specification](https://github.com/OpenDisplay/opendisplay-protocol)
- [OpenDisplay firmware and hardware documentation](https://github.com/OpenDisplay/Firmware)
- [Reference Python implementation](https://github.com/OpenDisplay/py-opendisplay)
