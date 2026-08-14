using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using Microsoft.UI.Reactor.Layout;
using Microsoft.UI.Xaml;
using OpenDisplayNet;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage;
using Windows.Storage.Pickers;
using static Microsoft.UI.Reactor.Factories;

ReactorApp.Run<App>("OpenDisplay", width: 1040, height: 760);

sealed class App : Component
{
    private static readonly IReadOnlyList<OpenDisplayDevice> EmptyDevices = [];

    public override Element Render()
    {
        var (devices, setDevices) = UseState(EmptyDevices);
        var (selectedDeviceIndex, setSelectedDeviceIndex) = UseState(0);
        var (client, setClient) = UseState<OpenDisplayClient?>(null);
        var (display, setDisplay) = UseState<DisplayInformation?>(null);
        var (imagePath, setImagePath) = UseState<string?>(null);
        var (fitIndex, setFitIndex) = UseState((int)OpenDisplayImageFit.Fill);
        var (ditheringIndex, setDitheringIndex) = UseState(1);
        var (isBusy, setIsBusy) = UseState(false);
        var (status, setStatus) = UseState<string?>(null);
        var (error, setError) = UseState<string?>(null);

        string[] deviceNames = devices
            .Select(device => $"{device.Name} ({device.BluetoothAddress:X12})")
            .ToArray();
        bool canConnect = devices.Count > 0 && selectedDeviceIndex >= 0 && selectedDeviceIndex < devices.Count;
        bool canUpload = client is not null && imagePath is not null;
        Element errorBar = InfoBar("OpenDisplay error", error ?? string.Empty) with
        {
            IsOpen = error is not null,
            IsClosable = true,
            OnClosed = () => setError(null),
        };
        Element statusBar = InfoBar("Status", status ?? string.Empty) with
        {
            IsOpen = status is not null,
            IsClosable = true,
            OnClosed = () => setStatus(null),
        };

        return FlexColumn(
                TitleBar("OpenDisplay").Flex(shrink: 0),
                ScrollView(
            FlexColumn(
                Heading("OpenDisplay device uploader"),
                TextBlock("Discover an OpenDisplay device, inspect its configuration, then upload a panel-aware image."),

                Heading("1. Discover and connect").FontSize(20),
                Button("Discover devices", async () =>
                {
                    setIsBusy(true);
                    setError(null);
                    setStatus("Scanning for OpenDisplay devices...");
                    try
                    {
                        IReadOnlyList<OpenDisplayDevice> discovered = await OpenDisplayDiscovery
                            .DiscoverAsync(TimeSpan.FromSeconds(8));
                        setDevices(discovered);
                        setSelectedDeviceIndex(0);
                        setStatus(discovered.Count == 0
                            ? "No OpenDisplay devices were found."
                            : $"Found {discovered.Count} device(s).");
                    }
                    catch (Exception exception)
                    {
                        setError(exception.Message);
                        setStatus(null);
                    }
                    finally
                    {
                        setIsBusy(false);
                    }
                }).IsEnabled(!isBusy),
                HStack(8,
                    ComboBox(deviceNames, selectedDeviceIndex, setSelectedDeviceIndex)
                        .Header("Discovered device")
                        .IsEnabled(!isBusy && deviceNames.Length > 0),
                    Button("Connect", async () =>
                    {
                        if (!canConnect)
                        {
                            return;
                        }

                        setIsBusy(true);
                        setError(null);
                        setStatus("Connecting and reading display information...");
                        try
                        {
                            client?.Dispose();
                            OpenDisplayDevice device = devices[selectedDeviceIndex];
                            OpenDisplayClient connected = await OpenDisplayClient.ConnectAsync(device);
                            OpenDisplayPanelSize panel = await connected.GetPanelSizeAsync();
                            OpenDisplayFirmwareVersion firmware = await connected.GetFirmwareVersionAsync();
                            OpenDisplayManufacturerData manufacturerData = await connected.GetManufacturerDataAsync();
                            IReadOnlyList<OpenDisplaySensorReading> sensors = await connected.ReadSensorsAsync();
                            setClient(connected);
                            setDisplay(new DisplayInformation(device, panel, firmware, manufacturerData, sensors));
                            setStatus($"Connected to {device.Name}.");
                        }
                        catch (Exception exception)
                        {
                            setError(exception.Message);
                            setStatus(null);
                        }
                        finally
                        {
                            setIsBusy(false);
                        }
                    }).IsEnabled(!isBusy && canConnect).VAlign(VerticalAlignment.Bottom),
                    Button("Disconnect", () =>
                    {
                        client?.Dispose();
                        setClient(null);
                        setDisplay(null);
                        setStatus("Disconnected.");
                    }).IsEnabled(client is not null).VAlign(VerticalAlignment.Bottom)
                ),

                When(display is not null, () => DisplayDetails(display!)),

                Heading("2. Select an image").FontSize(20),
                HStack(8,
                    Button("Browse for image", async () =>
                    {
                        setIsBusy(true);
                        setError(null);
                        try
                        {
                            // Picker hooks are designed to be invoked from UI event handlers.
#pragma warning disable REACTOR_HOOKS_001
                            StorageFile? file = await UseFilePickerAsync(
                                new FilePickerOptions(
                                    FileTypeFilter: [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff"],
                                    SuggestedStartLocation: PickerLocationId.PicturesLibrary));
#pragma warning restore REACTOR_HOOKS_001
                            if (file is not null)
                            {
                                setImagePath(file.Path);
                                setStatus($"Selected {file.Name}.");
                            }
                        }
                        catch (Exception exception)
                        {
                            setError($"Image selection failed: {exception.Message}");
                            setStatus(null);
                        }
                        finally
                        {
                            setIsBusy(false);
                        }
                    }).IsEnabled(!isBusy),
                    Button("Capture webcam image", async () =>
                    {
                        setIsBusy(true);
                        setError(null);
                        setStatus("Capturing a webcam image...");
                        try
                        {
                            string capturedImagePath = await CaptureWebcamImageAsync();
                            setImagePath(capturedImagePath);
                            setStatus("Webcam image captured.");
                        }
                        catch (Exception exception)
                        {
                            setError($"Webcam capture failed: {exception.Message}");
                            setStatus(null);
                        }
                        finally
                        {
                            setIsBusy(false);
                        }
                    }).IsEnabled(!isBusy),
                    Button("Clear image", () => setImagePath(null)).IsEnabled(imagePath is not null)
                ),
                TextBlock(imagePath is null ? "No image selected." : $"Selected image: {imagePath}"),

                Heading("3. Convert and upload").FontSize(20),
                ComboBox(["None", "Fill", "Uniform", "UniformToFill"], fitIndex, setFitIndex)
                    .Header("Image fit")
                    .IsEnabled(!isBusy),
                ComboBox(["No dithering", "Ordered dithering"], ditheringIndex, setDitheringIndex)
                    .Header("Palette reduction")
                    .IsEnabled(!isBusy),
                Button("Send image", async () =>
                {
                    if (client is null || imagePath is null)
                    {
                        return;
                    }

                    setIsBusy(true);
                    setError(null);
                    setStatus("Converting and sending image...");
                    try
                    {
                        using OpenDisplayImage image = new(
                            imagePath,
                            new OpenDisplayImageOptions(
                                (OpenDisplayImageFit)fitIndex,
                                (OpenDisplayDithering)ditheringIndex));
                        await client.SendImageAsync(image);
                        setStatus("Image sent.");
                    }
                    catch (Exception exception)
                    {
                        setError($"Image upload failed: {exception.Message}");
                        setStatus(null);
                    }
                    finally
                    {
                        setIsBusy(false);
                    }
                }).IsEnabled(!isBusy && canUpload),

                When(isBusy, () => HStack(ProgressRing().Width(20).Height(20), TextBlock("Working..."))),
                errorBar,
                statusBar
            ) with
            {
                RowGap = 12,
                FlexPadding = new Thickness(24),
            }).Flex(grow: 1, basis: 0)
        )
        .Backdrop(BackdropKind.Mica);
    }

    private static Element DisplayDetails(DisplayInformation display)
    {
        string sensors = display.Sensors.Count == 0
            ? "None reported"
            : string.Join(
                Environment.NewLine,
                display.Sensors.Select(sensor =>
                    $"{sensor.SensorType}: {sensor.TemperatureCelsius:F1} C, {sensor.HumidityPercent:F1}% RH"));
        return Border(
            FlexColumn(
                Heading("Connected display").FontSize(20),
                TextBlock($"Device: {display.Device.Name} ({display.Device.BluetoothAddress:X12})"),
                TextBlock($"Panel: {display.Panel.Width} x {display.Panel.Height}, {display.Panel.ColorScheme}"),
                TextBlock($"Firmware: {display.Firmware.Major}.{display.Firmware.Minor}.{display.Firmware.Patch} ({display.Firmware.Sha})"),
                TextBlock($"MCU temperature: {display.ManufacturerData.ChipTemperatureCelsius:F1} C; battery: {display.ManufacturerData.BatteryMillivolts} mV"),
                TextBlock($"Sensors: {sensors}")
            ) with { RowGap = 4 })
            .Padding(12);
    }

    private static async Task<string> CaptureWebcamImageAsync()
    {
        using MediaCapture mediaCapture = new();
        await mediaCapture.InitializeAsync(
            new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                PhotoCaptureSource = PhotoCaptureSource.Auto,
            });
        StorageFile imageFile = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
            $"OpenDisplay-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.jpg",
            CreationCollisionOption.GenerateUniqueName);
        await mediaCapture.CapturePhotoToStorageFileAsync(ImageEncodingProperties.CreateJpeg(), imageFile);
        return imageFile.Path;
    }
}

sealed record DisplayInformation(
    OpenDisplayDevice Device,
    OpenDisplayPanelSize Panel,
    OpenDisplayFirmwareVersion Firmware,
    OpenDisplayManufacturerData ManufacturerData,
    IReadOnlyList<OpenDisplaySensorReading> Sensors);
