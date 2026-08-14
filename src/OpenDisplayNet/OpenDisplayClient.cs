using System.Threading.Channels;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;

namespace OpenDisplayNet;

/// <summary>Connects to an OpenDisplay peripheral and uploads monochrome display frames.</summary>
public sealed class OpenDisplayClient : IDisposable
{
    /// <summary>The UUID used for OpenDisplay's GATT service and characteristic.</summary>
    public static readonly Guid ServiceUuid = new("00002446-0000-1000-8000-00805F9B34FB");

    private const int DataPayloadLength = 230;
    private const int PipeRequestedWindow = 8;
    private const int PipeRequestedAcknowledgementCadence = 4;
    private const int PipeMaximumFrameLength = 244;
    private const int PipeStartTimeoutSeconds = 2;
    private const int PipeAcknowledgementTimeoutSeconds = 5;
    private const int MaxStartPacketLength = 200;
    private const int CompressedStartDataLength = MaxStartPacketLength - sizeof(ushort) - sizeof(int);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan InitialConfigurationTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfigurationChunkTimeout = TimeSpan.FromSeconds(2);

    private readonly BluetoothLEDevice device;
    private readonly GattDeviceService service;
    private readonly GattCharacteristic characteristic;
    private readonly Channel<byte[]> notifications = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private byte[]? sessionKey;
    private byte[]? sessionId;
    private ulong sessionCounter;
    private bool disposed;

    private OpenDisplayClient(
        BluetoothLEDevice device,
        GattDeviceService service,
        GattCharacteristic characteristic)
    {
        this.device = device;
        this.service = service;
        this.characteristic = characteristic;
        characteristic.ValueChanged += OnCharacteristicValueChanged;
    }

    /// <summary>Connects to an OpenDisplay peripheral discovered by <see cref="OpenDisplayDiscovery"/>.</summary>
    public static async Task<OpenDisplayClient> ConnectAsync(
        OpenDisplayDevice device,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);

        BluetoothLEDevice? bluetoothDevice = await BluetoothLEDevice
            .FromBluetoothAddressAsync(device.BluetoothAddress, device.BluetoothAddressType)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (bluetoothDevice is null)
        {
            throw new InvalidOperationException($"Bluetooth device {device.Name} could not be opened.");
        }

        try
        {
            GattDeviceServicesResult services = await bluetoothDevice
                .GetGattServicesForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0)
            {
                throw new InvalidOperationException($"OpenDisplay service was unavailable ({services.Status}).");
            }

            GattDeviceService service = services.Services[0];
            GattCharacteristicsResult characteristics = await service
                .GetCharacteristicsForUuidAsync(ServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (characteristics.Status != GattCommunicationStatus.Success || characteristics.Characteristics.Count == 0)
            {
                service.Dispose();
                throw new InvalidOperationException($"OpenDisplay characteristic was unavailable ({characteristics.Status}).");
            }

            GattCharacteristic characteristic = characteristics.Characteristics[0];
            if ((characteristic.CharacteristicProperties &
                 GattCharacteristicProperties.Write) == 0)
            {
                service.Dispose();
                throw new InvalidOperationException("OpenDisplay characteristic does not support write-with-response.");
            }

            OpenDisplayClient client = new(bluetoothDevice, service, characteristic);
            GattCommunicationStatus notificationStatus = await characteristic
                .WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (notificationStatus != GattCommunicationStatus.Success)
            {
                client.Dispose();
                throw new InvalidOperationException($"OpenDisplay notifications could not be enabled ({notificationStatus}).");
            }

            return client;
        }
        catch
        {
            bluetoothDevice.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Sends a full 1-bit monochrome frame. Pixels are packed MSB-first, where 0 is black and 1 is white.
    /// </summary>
    public async Task SendMonochromeImageAsync(
        int width,
        int height,
        ReadOnlyMemory<byte> pixels,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be positive.");
        }

        int expectedLength = checked(((width + 7) / 8) * height);
        if (pixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"A {width}x{height} monochrome frame must contain {expectedLength} bytes.",
                nameof(pixels));
        }

        await SendImageAsync(pixels, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads the panel dimensions from the device's OpenDisplay configuration.</summary>
    public async Task<OpenDisplayPanelSize> GetPanelSizeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return OpenDisplayProtocol.ParsePanelSize(
                await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>Authenticates with a device configured for OpenDisplay AES-128-CCM security.</summary>
    public async Task AuthenticateAsync(ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
            {
                ThrowIfDisposed();
                if (key.Length != 16)
                {
                    throw new ArgumentException("The OpenDisplay authentication key must be 16 bytes.", nameof(key));
                }

                await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    sessionKey = null;
                    sessionId = null;
                    sessionCounter = 0;
                    byte[] serverNonce = [];
                    byte[] deviceId = [];
                    for (int attempt = 0; attempt < 2; attempt++)
                    {
                        ClearNotifications();
                        await WriteRawAsync(OpenDisplayProtocol.CreateAuthenticateStep1Packet(), GattWriteOption.WriteWithResponse, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            (serverNonce, deviceId) = OpenDisplayProtocol.ParseAuthenticateChallenge(
                                await ReadRawNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false));
                            break;
                        }
                        catch (OpenDisplayAuthenticationException exception) when (exception.Status == OpenDisplayAuthenticationStatus.AlreadyAuthenticated && attempt == 0)
                        {
                        }
                    }

                    if (serverNonce.Length != 16)
                    {
                        throw new InvalidOperationException("OpenDisplay did not provide an authentication challenge.");
                    }

                    byte[] clientNonce = RandomNumberGenerator.GetBytes(16);
                    byte[] challenge = OpenDisplayProtocol.ComputeChallengeResponse(key.Span, serverNonce, clientNonce, deviceId);
                    ClearNotifications();
                    await WriteRawAsync(OpenDisplayProtocol.CreateAuthenticateStep2Packet(clientNonce, challenge), GattWriteOption.WriteWithResponse, cancellationToken).ConfigureAwait(false);
                    byte[] proof = OpenDisplayProtocol.ParseAuthenticateSuccess(
                        await ReadRawNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false));
                    byte[] derivedKey = OpenDisplayProtocol.DeriveSessionKey(key.Span, clientNonce, serverNonce, deviceId);
                    if (!CryptographicOperations.FixedTimeEquals(
                        proof,
                        OpenDisplayProtocol.ComputeServerProof(derivedKey, serverNonce, clientNonce, deviceId)))
                    {
                        throw new OpenDisplayAuthenticationException(OpenDisplayAuthenticationStatus.InvalidKey);
                    }

                    sessionKey = derivedKey;
                    sessionId = OpenDisplayProtocol.DeriveSessionId(derivedKey, clientNonce, serverNonce);
                    sessionCounter = 0;
                }
                finally
                {
                    writeLock.Release();
                }
            }

    /// <summary>Reads the device's firmware version.</summary>
    public async Task<OpenDisplayFirmwareVersion> GetFirmwareVersionAsync(CancellationToken cancellationToken = default)
            {
                ThrowIfDisposed();
                await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    ClearNotifications();
                    await WriteAsync(OpenDisplayProtocol.CreateFirmwareVersionPacket(), GattWriteOption.WriteWithResponse, cancellationToken).ConfigureAwait(false);
                    return OpenDisplayProtocol.ParseFirmwareVersion(
                        await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false));
                }
                finally
                {
                    writeLock.Release();
                }
            }

    /// <summary>Reads the manufacturer-specific telemetry record broadcast by the device.</summary>
    public async Task<OpenDisplayManufacturerData> GetManufacturerDataAsync(CancellationToken cancellationToken = default)
            {
                ThrowIfDisposed();
                await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    return await ReadManufacturerDataAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    writeLock.Release();
                }
            }

    /// <summary>Reads the current values reported by configured SHT40 sensors.</summary>
    public async Task<IReadOnlyList<OpenDisplaySensorReading>> ReadSensorsAsync(CancellationToken cancellationToken = default)
            {
                ThrowIfDisposed();
                await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    byte[] configuration = await ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
                    OpenDisplayManufacturerData manufacturerData = await ReadManufacturerDataAsync(cancellationToken).ConfigureAwait(false);
                    return OpenDisplayProtocol.ReadSht40Sensors(configuration, manufacturerData.RawData.Span);
                }
                finally
                {
                    writeLock.Release();
                }
            }

    /// <summary>Activates an LED flash pattern on the specified LED instance.</summary>
    public Task ActivateLedAsync(byte ledInstance, OpenDisplayLedFlashConfig configuration, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(configuration);
                return SendAndValidateAcknowledgementAsync(
                    OpenDisplayProtocol.CreateLedActivatePacket(ledInstance, configuration),
                    OpenDisplayProtocol.LedActivate,
                    cancellationToken);
            }

    /// <summary>Activates a buzzer pattern on the specified buzzer instance.</summary>
    public Task ActivateBuzzerAsync(byte buzzerInstance, OpenDisplayBuzzerActivateConfig configuration, CancellationToken cancellationToken = default)
            {
                ArgumentNullException.ThrowIfNull(configuration);
                return SendAndValidateAcknowledgementAsync(
                    OpenDisplayProtocol.CreateBuzzerActivatePacket(buzzerInstance, configuration),
                    OpenDisplayProtocol.BuzzerActivate,
                    cancellationToken);
            }

    /// <summary>
    /// Sends a pre-encoded OpenDisplay image. The byte sequence must match the connected panel's color scheme.
    /// </summary>
    public async Task SendImageAsync(
        ReadOnlyMemory<byte> image,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (image.IsEmpty)
        {
            throw new ArgumentException("An image must contain at least one byte.", nameof(image));
        }

        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!await TrySendPipeImageAsync(image, cancellationToken).ConfigureAwait(false))
            {
                await SendLegacyImageAsync(image, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    /// <summary>
    /// Sends an already encoded 1-bit partial refresh. If the device does not support partial
    /// refresh or its displayed etag is no longer current, the caller must send a full image.
    /// </summary>
    public async Task<OpenDisplayPartialUpdateResult> SendPartialUpdateAsync(
        OpenDisplayPartialUpdate update,
        CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            ValidatePartialUpdate(update);

            await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                byte[] stream = new byte[update.OldPixels.Length + update.NewPixels.Length];
                update.OldPixels.CopyTo(stream);
                update.NewPixels.CopyTo(stream.AsMemory(update.OldPixels.Length));

                if (await TrySendPipePartialAsync(update, stream, cancellationToken).ConfigureAwait(false))
                {
                    return OpenDisplayPartialUpdateResult.AppliedPipe;
                }

                return await TrySendLegacyPartialAsync(update, stream, cancellationToken).ConfigureAwait(false)
                    ? OpenDisplayPartialUpdateResult.AppliedLegacy
                    : OpenDisplayPartialUpdateResult.FullRefreshRequired;
            }
            finally
            {
                writeLock.Release();
            }
        }
    private async Task SendLegacyImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        byte[] compressed = CompressForOpenDisplay(image.Span);
        ReadOnlyMemory<byte> transfer = image;
        int offset = 0;

        if (compressed.Length < image.Length)
        {
            try
            {
                await WriteAndAwaitAcknowledgementAsync(
                    OpenDisplayProtocol.CreateStartPacket(
                        image.Length,
                        compressed.AsSpan(0, Math.Min(compressed.Length, CompressedStartDataLength))),
                    OpenDisplayProtocol.DirectWriteStart,
                    GattWriteOption.WriteWithResponse,
                    cancellationToken).ConfigureAwait(false);
                transfer = compressed;
                offset = CompressedStartDataLength;
            }
            catch (OpenDisplayCommandRejectedException exception)
                when (exception.Command == OpenDisplayProtocol.DirectWriteStart)
            {
                await StartUncompressedTransferAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            await StartUncompressedTransferAsync(cancellationToken).ConfigureAwait(false);
        }

        for (; offset < transfer.Length; offset += DataPayloadLength)
        {
            int length = Math.Min(DataPayloadLength, transfer.Length - offset);
            byte acknowledgement = await WriteAndAwaitAcknowledgementAsync(
                OpenDisplayProtocol.CreateDataPacket(transfer.Span.Slice(offset, length)),
                OpenDisplayProtocol.DirectWriteData,
                GetDataWriteOption(),
                cancellationToken).ConfigureAwait(false);

            if (acknowledgement == OpenDisplayProtocol.DirectWriteEnd)
            {
                return;
            }
        }

        await WriteAndAwaitAcknowledgementAsync(
            OpenDisplayProtocol.CreateEndPacket(),
            OpenDisplayProtocol.DirectWriteEnd,
            GattWriteOption.WriteWithResponse,
            cancellationToken).ConfigureAwait(false);
        await WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySendPipeImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
        => await TrySendPipeTransferAsync(image, null, null, cancellationToken).ConfigureAwait(false);

    private async Task<bool> TrySendPipePartialAsync(
        OpenDisplayPartialUpdate update,
        ReadOnlyMemory<byte> stream,
        CancellationToken cancellationToken)
        => await TrySendPipeTransferAsync(
            stream,
            new OpenDisplayPipePartialRequest(update.OldEtag, update.Region),
            update.NewEtag,
            cancellationToken).ConfigureAwait(false);

    private async Task<bool> TrySendPipeTransferAsync(
        ReadOnlyMemory<byte> image,
        OpenDisplayPipePartialRequest? partial,
        uint? newEtag,
        CancellationToken cancellationToken)
    {
        // .NET's ZLibStream cannot select the firmware-required 9-bit window. Stored
        // DEFLATE blocks are a valid 9-bit zlib stream and preserve PIPE's explicit END.
        byte[] transfer = OpenDisplayProtocol.CreateNineBitZlibStream(image.Span);
        OpenDisplayPipeStartResponse parameters;
        try
        {
            ClearNotifications();
            await WriteAsync(
                OpenDisplayProtocol.CreatePipeStartPacket(
                    compressed: true,
                    PipeRequestedWindow,
                    PipeRequestedAcknowledgementCadence,
                    PipeMaximumFrameLength,
                    image.Length,
                    partial),
                GattWriteOption.WriteWithResponse,
                cancellationToken).ConfigureAwait(false);

            byte[] response = await ReadNotificationAsync(
                TimeSpan.FromSeconds(PipeStartTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);
            if (!OpenDisplayProtocol.TryParsePipeStartResponse(response, out parameters, out _))
            {
                return false;
            }
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (parameters.Version != OpenDisplayProtocol.PipeVersion ||
            parameters.MaximumFrame < OpenDisplayProtocol.PipeFrameOverhead ||
            (partial is not null && (parameters.Flags & OpenDisplayProtocol.PipePartialAcceptedFlag) == 0))
        {
            return false;
        }

        int window = Math.Min(PipeRequestedWindow, Math.Min((int)parameters.MaximumWindow, 32));
        int acknowledgementCadence = Math.Min(
            PipeRequestedAcknowledgementCadence,
            Math.Min((int)parameters.MaximumAcknowledgementCadence, window));
        int frameLength = Math.Min(PipeMaximumFrameLength, (int)parameters.MaximumFrame);
        if (window < 1 || acknowledgementCadence < 1 || frameLength <= OpenDisplayProtocol.PipeFrameOverhead)
        {
            return false;
        }

        int chunkLength = frameLength - OpenDisplayProtocol.PipeFrameOverhead;
        int chunkCount = Math.Max(1, (transfer.Length + chunkLength - 1) / chunkLength);
        HashSet<int> acknowledged = [];
        int lowestUnacknowledged = 0;
        int nextToSend = 0;
        int retransmissions = 0;
        int maximumRetransmissions = Math.Max(window * 3, (int)Math.Ceiling(chunkCount * 0.1));

        async Task SendChunkAsync(int chunkIndex)
        {
            int offset = chunkIndex * chunkLength;
            int length = Math.Min(chunkLength, transfer.Length - offset);
            await WriteAsync(
                OpenDisplayProtocol.CreatePipeDataPacket(
                    (byte)chunkIndex,
                    transfer.AsSpan(offset, length)),
                GetDataWriteOption(),
                cancellationToken).ConfigureAwait(false);
        }

        while (lowestUnacknowledged < chunkCount)
        {
            while (nextToSend < chunkCount && nextToSend - lowestUnacknowledged < window)
            {
                await SendChunkAsync(nextToSend++).ConfigureAwait(false);
            }

            byte[] response;
            try
            {
                response = await ReadNotificationAsync(
                    TimeSpan.FromSeconds(PipeAcknowledgementTimeoutSeconds),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                if (++retransmissions > maximumRetransmissions)
                {
                    throw new TimeoutException("OpenDisplay PIPE_WRITE did not acknowledge transfer progress.");
                }

                await SendChunkAsync(lowestUnacknowledged).ConfigureAwait(false);
                continue;
            }

            if (OpenDisplayProtocol.IsPipeDataNack(response, out byte errorCode))
            {
                throw new InvalidOperationException($"OpenDisplay rejected PIPE_WRITE data (error 0x{errorCode:X2}).");
            }

            OpenDisplayPipeSack sack;
            try
            {
                sack = OpenDisplayProtocol.ParsePipeSack(response);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (int chunk in OpenDisplayProtocol.GetAcknowledgedPipeChunks(sack, lowestUnacknowledged))
            {
                if (chunk >= lowestUnacknowledged && chunk < nextToSend)
                {
                    acknowledged.Add(chunk);
                }
            }

            while (acknowledged.Remove(lowestUnacknowledged))
            {
                lowestUnacknowledged++;
            }

            if (lowestUnacknowledged >= chunkCount)
            {
                break;
            }

            int highestAcknowledged = acknowledged.Count == 0 ? lowestUnacknowledged - 1 : acknowledged.Max();
            if (highestAcknowledged > lowestUnacknowledged)
            {
                if ((parameters.Flags & OpenDisplayProtocol.PipeSelectiveRepeatFlag) == 0)
                {
                    nextToSend = lowestUnacknowledged;
                    acknowledged.Clear();
                }
                else
                {
                    for (int chunk = lowestUnacknowledged; chunk < highestAcknowledged; chunk++)
                    {
                        if (!acknowledged.Contains(chunk))
                        {
                            if (++retransmissions > maximumRetransmissions)
                            {
                                throw new InvalidOperationException("OpenDisplay PIPE_WRITE exceeded its retransmission limit.");
                            }

                            await SendChunkAsync(chunk).ConfigureAwait(false);
                        }
                    }
                }
            }
        }

        await WriteAndAwaitPipeEndAsync(newEtag, cancellationToken).ConfigureAwait(false);
        await WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task WriteAndAwaitPipeEndAsync(uint? newEtag, CancellationToken cancellationToken)
    {
        await WriteAsync(
            OpenDisplayProtocol.CreatePipeEndPacket(fastRefresh: true, newEtag),
            GattWriteOption.WriteWithResponse,
            cancellationToken).ConfigureAwait(false);

        while (true)
        {
            byte[] response = await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Length >= sizeof(ushort) &&
                response[0] == 0xFF &&
                response[1] == (byte)OpenDisplayProtocol.PipeWriteEnd)
            {
                throw new InvalidOperationException("OpenDisplay rejected PIPE_WRITE END.");
            }

            if (response.Length >= sizeof(ushort) &&
                response[0] == 0 &&
                OpenDisplayProtocol.ReadOpcode(response) == OpenDisplayProtocol.PipeWriteEnd)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        characteristic.ValueChanged -= OnCharacteristicValueChanged;
        notifications.Writer.TryComplete();
        writeLock.Dispose();
        service.Dispose();
        device.Dispose();
    }

    private async Task<byte> WriteAndAwaitAcknowledgementAsync(
        byte[] packet,
        ushort expectedAcknowledgement,
        GattWriteOption writeOption,
        CancellationToken cancellationToken)
    {
        ClearNotifications();
        await WriteAsync(packet, writeOption, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            byte[] response = await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Length < sizeof(ushort))
            {
                continue;
            }

            ushort opcode = OpenDisplayProtocol.ReadOpcode(response);
            if (response[0] == 0xFF && response[1] == (byte)expectedAcknowledgement)
            {
                throw new OpenDisplayCommandRejectedException(expectedAcknowledgement);
            }

            if (opcode == expectedAcknowledgement ||
                opcode == OpenDisplayProtocol.GetAcknowledgementOpcode(expectedAcknowledgement))
            {
                return (byte)expectedAcknowledgement;
            }

            if (opcode == OpenDisplayProtocol.DirectWriteEnd ||
                opcode == OpenDisplayProtocol.GetAcknowledgementOpcode(OpenDisplayProtocol.DirectWriteEnd))
            {
                return (byte)OpenDisplayProtocol.DirectWriteEnd;
            }
        }
    }

    private async Task WriteAsync(
        byte[] packet,
        GattWriteOption writeOption,
        CancellationToken cancellationToken)
    {
        byte[] frame = packet;
        if (sessionKey is not null && sessionId is not null)
        {
            frame = OpenDisplayProtocol.EncryptCommand(sessionKey, sessionId, sessionCounter++, packet);
        }

        await WriteRawAsync(frame, writeOption, cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteRawAsync(
        byte[] packet,
        GattWriteOption writeOption,
        CancellationToken cancellationToken)
    {
        GattCommunicationStatus status = await characteristic
            .WriteValueAsync(
                CryptographicBuffer.CreateFromByteArray(packet),
                writeOption)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        if (status != GattCommunicationStatus.Success)
        {
            throw new InvalidOperationException($"OpenDisplay write failed ({status}).");
        }
    }

    private GattWriteOption GetDataWriteOption()
        => (characteristic.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
            ? GattWriteOption.WriteWithoutResponse
            : GattWriteOption.WriteWithResponse;

    private async Task StartUncompressedTransferAsync(CancellationToken cancellationToken)
    {
        await WriteAndAwaitAcknowledgementAsync(
            [0x00, (byte)OpenDisplayProtocol.DirectWriteStart],
            OpenDisplayProtocol.DirectWriteStart,
            GattWriteOption.WriteWithResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TrySendLegacyPartialAsync(
        OpenDisplayPartialUpdate update,
        ReadOnlyMemory<byte> uncompressedStream,
        CancellationToken cancellationToken)
    {
        byte[] stream = OpenDisplayProtocol.CreateNineBitZlibStream(uncompressedStream.Span);
        const int fixedHeaderLength = 19;
        int firstLength = Math.Min(stream.Length, MaxStartPacketLength - fixedHeaderLength);

        try
        {
            await WriteAndAwaitAcknowledgementAsync(
                OpenDisplayProtocol.CreatePartialStartPacket(
                    update.OldEtag,
                    update.NewEtag,
                    update.Region,
                    compressed: true,
                    stream.AsSpan(0, firstLength)),
                OpenDisplayProtocol.DirectWritePartialStart,
                GattWriteOption.WriteWithResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OpenDisplayCommandRejectedException exception)
            when (exception.Command == OpenDisplayProtocol.DirectWritePartialStart)
        {
            return false;
        }

        for (int offset = firstLength; offset < stream.Length; offset += DataPayloadLength)
        {
            int length = Math.Min(DataPayloadLength, stream.Length - offset);
            await WriteAndAwaitAcknowledgementAsync(
                OpenDisplayProtocol.CreateDataPacket(stream.AsSpan(offset, length)),
                OpenDisplayProtocol.DirectWriteData,
                GetDataWriteOption(),
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await WriteAndAwaitAcknowledgementAsync(
                OpenDisplayProtocol.CreateEndPacket(fastRefresh: true, update.NewEtag),
                OpenDisplayProtocol.DirectWriteEnd,
                GattWriteOption.WriteWithResponse,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OpenDisplayCommandRejectedException exception)
            when (exception.Command is OpenDisplayProtocol.DirectWriteData or OpenDisplayProtocol.DirectWriteEnd)
        {
            return false;
        }

        await WaitForRefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static void ValidatePartialUpdate(OpenDisplayPartialUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (update.OldEtag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "The old etag must be non-zero.");
        }

        if (update.NewEtag == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(update), "The new etag must be non-zero.");
        }

        OpenDisplayPartialRegion region = update.Region;
        if (region.Width == 0 || region.Height == 0 || region.Width % 8 != 0 || region.X % 8 != 0)
        {
            throw new ArgumentException(
                "Partial update X and width must be non-zero multiples of eight pixels, and height must be non-zero.",
                nameof(update));
        }

        int expectedLength = checked((region.Width / 8) * region.Height);
        if (update.OldPixels.Length != expectedLength || update.NewPixels.Length != expectedLength)
        {
            throw new ArgumentException(
                $"Both partial pixel buffers must contain {expectedLength} bytes for the requested region.",
                nameof(update));
        }
    }

    private static byte[] CompressForOpenDisplay(ReadOnlySpan<byte> input)
    {
        using MemoryStream output = new();
        using (ZLibStream compressor = new(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            compressor.Write(input);
        }

        byte[] compressed = output.ToArray();
        if (compressed.Length < 2)
        {
            throw new InvalidOperationException("Zlib produced an invalid compressed payload.");
        }

        // OpenDisplay firmware accepts only a 9-bit (512-byte) zlib window.
        compressed[0] = 0x18;
        compressed[1] = 0x19;
        return compressed;
    }

    private async Task<byte[]> ReadNotificationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        byte[] response = await ReadRawNotificationAsync(timeout, cancellationToken).ConfigureAwait(false);
        if (sessionKey is not null && response.Length >= 31)
        {
            return OpenDisplayProtocol.DecryptResponse(sessionKey, response);
        }

        if (response.Length == 3 && response[2] == 0xFE)
        {
            throw new OpenDisplayAuthenticationException((OpenDisplayAuthenticationStatus)response[2]);
        }

        if (response.Length == 3 && response[2] == 0xFF)
        {
            throw new CryptographicException("OpenDisplay rejected the encrypted command.");
        }

        return response;
    }

    private async Task<byte[]> ReadRawNotificationAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCancellation = new(timeout);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCancellation.Token);
        try
        {
            return await notifications.Reader.ReadAsync(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCancellation.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"OpenDisplay did not respond within {timeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<byte[]> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        ClearNotifications();
        await WriteAsync(
            OpenDisplayProtocol.CreateReadConfigPacket(),
            GattWriteOption.WriteWithResponse,
            cancellationToken).ConfigureAwait(false);

        byte[] firstResponse = await ReadNotificationAsync(InitialConfigurationTimeout, cancellationToken).ConfigureAwait(false);
        if (firstResponse.Length == 4 &&
            firstResponse[0] == 0xFF &&
            OpenDisplayProtocol.ReadOpcode(firstResponse) == OpenDisplayProtocol.ReadConfig)
        {
            throw new InvalidOperationException("OpenDisplay has no stored configuration.");
        }

        ValidateConfigurationResponse(firstResponse, expectedChunkNumber: 0, isFirstChunk: true);
        int totalLength = BinaryPrimitives.ReadUInt16LittleEndian(firstResponse.AsSpan(4, sizeof(ushort)));
        if (totalLength == 0)
        {
            throw new InvalidOperationException("OpenDisplay returned an empty configuration.");
        }

        List<byte> configuration = [.. firstResponse.AsSpan(6).ToArray()];
        ushort expectedChunkNumber = 1;
        while (configuration.Count < totalLength)
        {
            byte[] response = await ReadNotificationAsync(ConfigurationChunkTimeout, cancellationToken).ConfigureAwait(false);
            ValidateConfigurationResponse(response, expectedChunkNumber, isFirstChunk: false);
            if (response.Length == 4)
            {
                throw new InvalidOperationException("OpenDisplay stopped sending configuration data.");
            }

            configuration.AddRange(response.AsSpan(4).ToArray());
            expectedChunkNumber++;
        }

        return configuration.Take(totalLength).ToArray();
    }

    private async Task<OpenDisplayManufacturerData> ReadManufacturerDataAsync(CancellationToken cancellationToken)
    {
        ClearNotifications();
        await WriteAsync(
            OpenDisplayProtocol.CreateReadManufacturerDataPacket(),
            GattWriteOption.WriteWithResponse,
            cancellationToken).ConfigureAwait(false);
        return OpenDisplayProtocol.ParseManufacturerData(
            await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false));
    }

    private async Task SendAndValidateAcknowledgementAsync(
        byte[] packet,
        ushort command,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearNotifications();
            await WriteAsync(packet, GattWriteOption.WriteWithResponse, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                byte[] response = await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
                if (response.Length < 2)
                {
                    continue;
                }

                if (response[0] == 0xFF && response[1] == (byte)command)
                {
                    throw new InvalidOperationException($"OpenDisplay rejected command 0x{command:X4}.");
                }

                if ((OpenDisplayProtocol.ReadOpcode(response) & ~OpenDisplayProtocol.ResponseHighBitFlag) == command)
                {
                    return;
                }
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private async Task WaitForRefreshAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            byte[] response = await ReadNotificationAsync(CommandTimeout, cancellationToken).ConfigureAwait(false);
            if (response.Length < sizeof(ushort))
            {
                continue;
            }

            ushort opcode = OpenDisplayProtocol.ReadOpcode(response);
            if (opcode == OpenDisplayProtocol.DirectWriteRefreshComplete)
            {
                return;
            }

            if (opcode == OpenDisplayProtocol.DirectWriteRefreshTimeout)
            {
                throw new TimeoutException("OpenDisplay reported that the panel refresh timed out.");
            }
        }
    }

    private static void ValidateConfigurationResponse(
        ReadOnlySpan<byte> response,
        ushort expectedChunkNumber,
        bool isFirstChunk)
    {
        int minimumLength = isFirstChunk ? 6 : 4;
        if (response.Length < minimumLength || OpenDisplayProtocol.ReadOpcode(response) != OpenDisplayProtocol.ReadConfig)
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid configuration response.");
        }

        ushort chunkNumber = BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(2, sizeof(ushort)));
        if (chunkNumber != expectedChunkNumber)
        {
            throw new InvalidOperationException(
                $"OpenDisplay returned configuration chunk {chunkNumber} when chunk {expectedChunkNumber} was expected.");
        }
    }

    private void ClearNotifications()
    {
        while (notifications.Reader.TryRead(out _))
        {
        }
    }

    private void OnCharacteristicValueChanged(GattCharacteristic _, GattValueChangedEventArgs args)
    {
        CryptographicBuffer.CopyToByteArray(args.CharacteristicValue, out byte[] value);
        notifications.Writer.TryWrite(value);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class OpenDisplayCommandRejectedException(ushort command)
        : InvalidOperationException($"OpenDisplay rejected command 0x{command:X4}.")
    {
        public ushort Command { get; } = command;
    }
}
