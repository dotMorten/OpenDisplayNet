using OpenDisplayNet;

using CancellationTokenSource cancellation = new();

using CancellationTokenSource scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
List<OpenDisplayDevice> devices = [];
object devicesLock = new();
TaskCompletionSource<OpenDisplayDevice> firstDiscoveredDevice = new(
    TaskCreationOptions.RunContinuationsAsynchronously);
bool selectFirstDevice = args.Contains("--first", StringComparer.OrdinalIgnoreCase);

Console.WriteLine("Scanning for OpenDisplay devices. Press Escape to exit.");
Task scanTask = Task.Run(async () =>
{
    try
    {
        await foreach (OpenDisplayDevice device in OpenDisplayDiscovery.ScanAsync(scanCancellation.Token))
        {
            int number;
            lock (devicesLock)
            {
                devices.Add(device);
                number = devices.Count;
            }
            firstDiscoveredDevice.TrySetResult(device);

            string signalStrength = device.Rssi is { } rssi ? $"{rssi} dBm" : "unknown signal";
            Console.WriteLine($"{number}. {device.Name} ({device.BluetoothAddress:X12}, {signalStrength})");
            if (number == 1)
            {
                Console.WriteLine("Enter a device number to connect, or press Escape to exit.");
            }
        }
    }
    catch (OperationCanceledException) when (scanCancellation.IsCancellationRequested)
    {
    }
});

OpenDisplayDevice? selectedDevice = selectFirstDevice
    ? await firstDiscoveredDevice.Task.WaitAsync(cancellation.Token)
    : ReadSelection(devices, devicesLock);
scanCancellation.Cancel();
await scanTask;
if (selectedDevice is null)
{
    return;
}

Console.WriteLine($"Connecting to {selectedDevice.Name}...");
try
{
    await using OpenDisplayClient client = await OpenDisplayClient.ConnectAsync(selectedDevice, cancellation.Token);

    OpenDisplayPanelSize panelSize = await client.GetPanelSizeAsync(cancellation.Token);
    OpenDisplayFirmwareVersion firmware = await client.GetFirmwareVersionAsync(cancellation.Token);
    OpenDisplayManufacturerData manufacturerData = await client.GetManufacturerDataAsync(cancellation.Token);
    IReadOnlyList<OpenDisplaySensorReading> sensors = await client.ReadSensorsAsync(cancellation.Token);
    WriteDeviceInformation(selectedDevice, panelSize, firmware, manufacturerData, sensors);
    if (args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
    {
        return;
    }

    Console.WriteLine($"Sending a {panelSize.Width}x{panelSize.Height} {panelSize.ColorScheme} test pattern...");
    byte[] testPattern = CreateTestPattern(panelSize);
    if (panelSize.ColorScheme == OpenDisplayColorScheme.Monochrome)
    {
        await client.SendMonochromeImageAsync(panelSize.Width, panelSize.Height, testPattern, cancellation.Token);
    }
    else
    {
        await client.SendImageAsync(testPattern, cancellation.Token);
    }

    Console.WriteLine("Test pattern sent.");
}
catch (TimeoutException exception)
{
    Console.Error.WriteLine($"The display stopped responding: {exception.Message}");
}

static OpenDisplayDevice? ReadSelection(List<OpenDisplayDevice> devices, object devicesLock)
{
    string selection = string.Empty;
    bool selectionStarted = false;

    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Escape)
        {
            return null;
        }

        int deviceCount;
        lock (devicesLock)
        {
            deviceCount = devices.Count;
        }

        if (deviceCount == 0)
        {
            continue;
        }

        if (!selectionStarted)
        {
            Console.Write("Select a device: ");
            selectionStarted = true;
        }

        if (key.Key == ConsoleKey.Backspace && selection.Length > 0)
        {
            selection = selection[..^1];
            Console.Write("\b \b");
            continue;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            Console.WriteLine();
            if (int.TryParse(selection, out int selectedNumber))
            {
                lock (devicesLock)
                {
                    if (selectedNumber >= 1 && selectedNumber <= devices.Count)
                    {
                        return devices[selectedNumber - 1];
                    }
                }
            }

            Console.WriteLine("Enter the number of a discovered device.");
            Console.Write("Select a device: ");
            selection = string.Empty;
            continue;
        }

        if (char.IsAsciiDigit(key.KeyChar))
        {
            selection += key.KeyChar;
            Console.Write(key.KeyChar);
        }
    }
}

static void WriteDeviceInformation(
    OpenDisplayDevice device,
    OpenDisplayPanelSize panelSize,
    OpenDisplayFirmwareVersion firmware,
    OpenDisplayManufacturerData manufacturerData,
    IReadOnlyList<OpenDisplaySensorReading> sensors)
{
    Console.WriteLine();
    Console.WriteLine("Device information");
    Console.WriteLine($"  Name: {device.Name}");
    Console.WriteLine($"  Bluetooth address: {device.BluetoothAddress:X12} ({device.BluetoothAddressType})");
    Console.WriteLine($"  Signal strength: {(device.Rssi is { } rssi ? $"{rssi} dBm" : "unknown")}");
    Console.WriteLine($"  Panel: {panelSize.Width}x{panelSize.Height}, {panelSize.ColorScheme} ({(byte)panelSize.ColorScheme})");
    Console.WriteLine($"  Firmware: {firmware.Major}.{firmware.Minor}.{firmware.Patch} ({firmware.Sha})");
    Console.WriteLine($"  Manufacturer data: {Convert.ToHexString(manufacturerData.RawData.Span)}");
    Console.WriteLine($"  MCU chip temperature: {manufacturerData.ChipTemperatureCelsius:F1} C");
    Console.WriteLine($"  Battery voltage: {manufacturerData.BatteryMillivolts} mV");
    Console.WriteLine(
        $"  Manufacturer data format: {manufacturerData.Format}, loop counter: {manufacturerData.LoopCounter}, " +
        $"rebooted: {manufacturerData.Rebooted?.ToString() ?? "not reported"}, " +
        $"connection requested: {manufacturerData.ConnectionRequested?.ToString() ?? "not reported"}");

    if (sensors.Count == 0)
    {
        Console.WriteLine("  Configured SHT40 sensors: none reported");
        return;
    }

    Console.WriteLine("  Configured SHT40 sensors:");
    foreach (OpenDisplaySensorReading sensor in sensors)
    {
        Console.WriteLine(
            $"    {sensor.InstanceNumber}: {sensor.SensorType} ({(ushort)sensor.SensorType}), " +
            $"{sensor.TemperatureCelsius:F1} C, {sensor.HumidityPercent:F1}% RH");
    }
}

static byte[] CreateTestPattern(OpenDisplayPanelSize panelSize)
    => panelSize.ColorScheme switch
    {
        OpenDisplayColorScheme.Monochrome => CreateMonochromeTestPattern(panelSize.Width, panelSize.Height),
        OpenDisplayColorScheme.BlackWhiteRed => CreateThreeColorTestPattern(panelSize.Width, panelSize.Height, red: true),
        OpenDisplayColorScheme.BlackWhiteYellow => CreateThreeColorTestPattern(panelSize.Width, panelSize.Height, red: false),
        OpenDisplayColorScheme.BlackWhiteRedYellow => CreatePackedTestPattern(panelSize.Width, panelSize.Height, 2, 4),
        OpenDisplayColorScheme.SixColor => CreatePackedTestPattern(
            panelSize.Width,
            panelSize.Height,
            4,
            6,
            [0, 1, 2, 3, 5, 6]),
        OpenDisplayColorScheme.Gray4 => CreateGray4TestPattern(panelSize.Width, panelSize.Height),
        OpenDisplayColorScheme.Gray16 => CreatePackedTestPattern(panelSize.Width, panelSize.Height, 4, 16),
        OpenDisplayColorScheme.SevenColor => CreatePackedTestPattern(panelSize.Width, panelSize.Height, 4, 7),
        OpenDisplayColorScheme.SixColorSplit => CreateSixColorSplitTestPattern(panelSize.Width, panelSize.Height),
        _ => throw new NotSupportedException(
            $"The test app does not yet encode OpenDisplay color scheme {panelSize.ColorScheme} ({(byte)panelSize.ColorScheme})."),
    };

static byte[] CreateMonochromeTestPattern(int width, int height)
{
    int stride = (width + 7) / 8;
    byte[] pixels = new byte[checked(stride * height)];
    Array.Fill(pixels, (byte)0xFF);

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            if (IsTestPatternPixelBlack(x, y, width, height))
            {
                pixels[y * stride + (x / 8)] &= (byte)~(0x80 >> (x % 8));
            }
        }
    }

    return pixels;
}

static byte[] CreateThreeColorTestPattern(int width, int height, bool red)
{
    int stride = (width + 7) / 8;
    int planeLength = checked(stride * height);
    byte[] pixels = new byte[planeLength * 2];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int color = GetPatternColor(x, y, width, 3);
            bool blackWhitePlane = color == 1 || (red && color == 2);
            bool accentPlane = color == 2;
            SetPlanePixel(pixels, 0, planeLength, x, y, stride, blackWhitePlane);
            SetPlanePixel(pixels, 1, planeLength, x, y, stride, accentPlane);
        }
    }

    return pixels;
}

static byte[] CreateGray4TestPattern(int width, int height)
{
    int stride = (width + 7) / 8;
    int planeLength = checked(stride * height);
    byte[] pixels = new byte[planeLength * 2];
    ReadOnlySpan<byte> grayCodes = [3, 1, 2, 0];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            byte code = grayCodes[GetPatternColor(x, y, width, 4)];
            SetPlanePixel(pixels, 0, planeLength, x, y, stride, (code & 1) != 0);
            SetPlanePixel(pixels, 1, planeLength, x, y, stride, (code & 2) != 0);
        }
    }

    return pixels;
}

static byte[] CreateSixColorSplitTestPattern(int width, int height)
{
    int split = width / 2;
    return
    [
        .. CreatePackedTestPattern(split, height, 4, 6, [0, 1, 2, 3, 5, 6], 0),
        .. CreatePackedTestPattern(width - split, height, 4, 6, [0, 1, 2, 3, 5, 6], split),
    ];
}

static byte[] CreatePackedTestPattern(
    int width,
    int height,
    int bitsPerPixel,
    int colorCount,
    ReadOnlySpan<byte> colorCodes = default,
    int xOffset = 0)
{
    int pixelsPerByte = 8 / bitsPerPixel;
    int stride = (width + pixelsPerByte - 1) / pixelsPerByte;
    byte[] pixels = new byte[checked(stride * height)];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            int color = GetPatternColor(x + xOffset, y, width + xOffset, colorCount);
            byte code = colorCodes.IsEmpty ? (byte)color : colorCodes[color];
            int shift = 8 - bitsPerPixel * ((x % pixelsPerByte) + 1);
            pixels[y * stride + (x / pixelsPerByte)] |= (byte)(code << shift);
        }
    }

    return pixels;
}

static int GetPatternColor(int x, int y, int width, int colorCount)
    => (x * colorCount / width + y / 32) % colorCount;

static void SetPlanePixel(
    Span<byte> pixels,
    int plane,
    int planeLength,
    int x,
    int y,
    int stride,
    bool value)
{
    if (!value)
    {
        return;
    }

    pixels[plane * planeLength + y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
}

static bool IsTestPatternPixelBlack(int x, int y, int width, int height)
{
    bool border = x < 3 || y < 3 || x >= width - 3 || y >= height - 3;
    bool diagonal = Math.Abs((long)x * height - (long)y * width) < Math.Max(width, height) * 2L;
    bool checkerboard = ((x / 24) + (y / 24)) % 2 == 0;
    return border || diagonal || checkerboard;
}
