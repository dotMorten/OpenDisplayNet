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
    Console.WriteLine($"Panel: {panelSize.Width}x{panelSize.Height}, color scheme {panelSize.ColorScheme}");
    if (args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
    {
        return;
    }

    Console.WriteLine($"Sending a {panelSize.Width}x{panelSize.Height} test pattern...");
    switch (panelSize.ColorScheme)
    {
        case OpenDisplayColorScheme.Monochrome:
            await client.SendMonochromeImageAsync(
                panelSize.Width,
                panelSize.Height,
                CreateTestPattern(panelSize.Width, panelSize.Height),
                cancellation.Token);
            break;
        case OpenDisplayColorScheme.Gray4:
            await client.SendImageAsync(
                CreateGray4TestPattern(panelSize.Width, panelSize.Height),
                cancellation.Token);
            break;
        default:
            throw new NotSupportedException(
                $"The test app does not yet encode OpenDisplay color scheme {panelSize.ColorScheme}.");
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

static byte[] CreateTestPattern(int width, int height)
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

static byte[] CreateGray4TestPattern(int width, int height)
{
    int stride = (width + 7) / 8;
    int planeLength = checked(stride * height);
    byte[] pixels = new byte[planeLength * 2];

    for (int y = 0; y < height; y++)
    {
        for (int x = 0; x < width; x++)
        {
            if (!IsTestPatternPixelBlack(x, y, width, height))
            {
                continue;
            }

            byte mask = (byte)(0x80 >> (x % 8));
            int offset = y * stride + (x / 8);
            pixels[offset] |= mask;
            pixels[planeLength + offset] |= mask;
        }
    }

    return pixels;
}

static bool IsTestPatternPixelBlack(int x, int y, int width, int height)
{
    bool border = x < 3 || y < 3 || x >= width - 3 || y >= height - 3;
    bool diagonal = Math.Abs((long)x * height - (long)y * width) < Math.Max(width, height) * 2L;
    bool checkerboard = ((x / 24) + (y / 24)) % 2 == 0;
    return border || diagonal || checkerboard;
}
