# OpenDisplayNet

`OpenDisplayNet` is a standalone .NET library for discovering and controlling
OpenDisplay Bluetooth Low Energy displays on Windows.

## Features

- OpenDisplay BLE discovery, including paired devices.
- Configuration, panel capability, firmware, and manufacturer-data queries.
- Direct Write and negotiated PIPE_WRITE image uploads with firmware-compatible
  9-bit zlib compression.
- Monochrome and caller-encoded image uploads, partial updates, authentication,
  sensor reads, LED effects, and buzzer activation.

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
