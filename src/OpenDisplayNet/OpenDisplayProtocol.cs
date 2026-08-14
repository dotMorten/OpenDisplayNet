using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OpenDisplayNet;

internal static class OpenDisplayProtocol
{
    public const ushort DirectWriteStart = 0x0070;
    public const ushort DirectWriteData = 0x0071;
    public const ushort DirectWriteEnd = 0x0072;
    public const ushort DirectWriteRefreshComplete = 0x0073;
    public const ushort DirectWriteRefreshTimeout = 0x0074;
    public const ushort DirectWritePartialStart = 0x0076;
    public const ushort ReadConfig = 0x0040;
    public const ushort FirmwareVersion = 0x0043;
    public const ushort ReadManufacturerData = 0x0044;
    public const ushort Authenticate = 0x0050;
    public const ushort LedActivate = 0x0073;
    public const ushort BuzzerActivate = 0x0077;
    public const ushort PipeWriteStart = 0x0080;
    public const ushort PipeWriteData = 0x0081;
    public const ushort PipeWriteEnd = 0x0082;
    public const ushort ResponseHighBitFlag = 0x8000;
    public const byte PipeVersion = 1;
    public const byte PipeCompressedFlag = 0x01;
    public const byte PipePartialFlag = 0x02;
    public const byte PipeSelectiveRepeatFlag = 0x01;
    public const byte PipePartialAcceptedFlag = 0x02;
    public const int PipeFrameOverhead = 3;

    public static byte[] CreateStartPacket(int uncompressedSize, ReadOnlySpan<byte> firstChunk)
    {
        byte[] packet = new byte[sizeof(ushort) + sizeof(int) + firstChunk.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, DirectWriteStart);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(sizeof(ushort)), uncompressedSize);
        firstChunk.CopyTo(packet.AsSpan(sizeof(ushort) + sizeof(int)));
        return packet;
    }

    public static byte[] CreateDataPacket(ReadOnlySpan<byte> chunk)
    {
        byte[] packet = new byte[sizeof(ushort) + chunk.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, DirectWriteData);
        chunk.CopyTo(packet.AsSpan(sizeof(ushort)));
        return packet;
    }

    public static byte[] CreateEndPacket() => [0x00, (byte)DirectWriteEnd, 0x00];

    public static byte[] CreatePartialStartPacket(
        uint oldEtag,
        uint newEtag,
        OpenDisplayPartialRegion region,
        bool compressed,
        ReadOnlySpan<byte> firstChunk)
    {
        byte[] packet = new byte[19 + firstChunk.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, DirectWritePartialStart);
        packet[2] = compressed ? PipeCompressedFlag : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(3), oldEtag);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(7), newEtag);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(11), region.X);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(13), region.Y);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(15), region.Width);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(17), region.Height);
        firstChunk.CopyTo(packet.AsSpan(19));
        return packet;
    }

    public static byte[] CreateEndPacket(bool fastRefresh, uint newEtag)
    {
        byte[] packet = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(packet, DirectWriteEnd);
        packet[2] = fastRefresh ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(3), newEtag);
        return packet;
    }

    public static byte[] CreateReadConfigPacket() => [0x00, (byte)ReadConfig];

    public static byte[] CreateFirmwareVersionPacket() => [0x00, (byte)FirmwareVersion];

    public static byte[] CreateReadManufacturerDataPacket() => [0x00, (byte)ReadManufacturerData];

    public static byte[] CreateAuthenticateStep1Packet() => [0x00, (byte)Authenticate, 0x00];

    public static byte[] CreateAuthenticateStep2Packet(ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> proof)
    {
        if (clientNonce.Length != 16 || proof.Length != 16)
        {
            throw new ArgumentException("Authentication nonces and proofs must be 16 bytes.");
        }

        byte[] packet = new byte[34];
        BinaryPrimitives.WriteUInt16BigEndian(packet, Authenticate);
        clientNonce.CopyTo(packet.AsSpan(2));
        proof.CopyTo(packet.AsSpan(18));
        return packet;
    }

    public static byte[] CreateLedActivatePacket(byte instance, OpenDisplayLedFlashConfig configuration)
        => [0x00, (byte)LedActivate, instance, .. configuration.ToBytes()];

    public static byte[] CreateBuzzerActivatePacket(byte instance, OpenDisplayBuzzerActivateConfig configuration)
        => [0x00, (byte)BuzzerActivate, instance, .. configuration.ToBytes()];

    public static ushort ReadOpcode(ReadOnlySpan<byte> packet) => BinaryPrimitives.ReadUInt16BigEndian(packet);

    public static ushort GetAcknowledgementOpcode(ushort command) => (ushort)(command | ResponseHighBitFlag);

    public static OpenDisplayFirmwareVersion ParseFirmwareVersion(ReadOnlySpan<byte> response)
    {
        ValidateResponseOpcode(response, FirmwareVersion);
        if (response.Length < 5 || response[4] == 0 || response.Length < 5 + response[4])
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid firmware version response.");
        }

        ReadOnlySpan<byte> shaBytes = response.Slice(5, response[4]);
        if (shaBytes.IndexOfAnyInRange((byte)0x80, byte.MaxValue) >= 0)
        {
            throw new InvalidOperationException("OpenDisplay returned a non-ASCII firmware SHA.");
        }

        string sha = System.Text.Encoding.ASCII.GetString(shaBytes);
        byte patch = response.Length > 5 + response[4] ? response[5 + response[4]] : (byte)0;
        return new OpenDisplayFirmwareVersion(response[2], response[3], sha, patch);
    }

    public static byte[] ParseManufacturerData(ReadOnlySpan<byte> response)
    {
        ValidateResponseOpcode(response, ReadManufacturerData);
        if (response.Length != 18)
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid manufacturer data response.");
        }

        return response[2..].ToArray();
    }

    public static IReadOnlyList<OpenDisplaySensorReading> ReadSht40Sensors(
        ReadOnlySpan<byte> configuration,
        ReadOnlySpan<byte> manufacturerData)
    {
        if (manufacturerData.Length != 16)
        {
            throw new ArgumentException("OpenDisplay manufacturer data must be 16 bytes.", nameof(manufacturerData));
        }

        const int outerHeaderLength = 3;
        const int crcLength = 2;
        const byte sensorPacketId = 0x23;
        const OpenDisplaySensorType sht40 = OpenDisplaySensorType.Sht40;
        List<OpenDisplaySensorReading> readings = [];
        int offset = outerHeaderLength;
        int end = configuration.Length - crcLength;
        if (end < offset)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete configuration.");
        }

        while (offset < end)
        {
            if (end - offset < 2)
            {
                throw new InvalidOperationException("OpenDisplay returned a truncated configuration packet header.");
            }

            byte packetId = configuration[offset + 1];
            offset += 2;
            if (!PacketSizes.TryGetValue(packetId, out int packetSize) || end - offset < packetSize)
            {
                throw new InvalidOperationException("OpenDisplay returned an invalid configuration packet.");
            }

            ReadOnlySpan<byte> packet = configuration.Slice(offset, packetSize);
            offset += packetSize;
            if (packetId != sensorPacketId || BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(1, 2)) != (ushort)sht40)
            {
                continue;
            }

            int start = packet[5] is 0 or 0xFF ? 7 : packet[5];
            if (start > 8)
            {
                continue;
            }

            ReadOnlySpan<byte> raw = manufacturerData.Slice(2 + start, 3);
            int packed = raw[0] | (raw[1] << 8) | (raw[2] << 16);
            if (packed is 0 or 0xFFFFFF)
            {
                continue;
            }

            int humidityDeciPercent = packed & 0x3FF;
            int temperatureUnits = (packed >> 10) & 0x7FF;
            if (humidityDeciPercent > 1000 || temperatureUnits > 1650)
            {
                continue;
            }

            readings.Add(new OpenDisplaySensorReading(
                packet[0],
                sht40,
                (temperatureUnits - 400) / 10d,
                humidityDeciPercent / 10d));
        }

        return readings;
    }

    public static (byte[] ServerNonce, byte[] DeviceId) ParseAuthenticateChallenge(ReadOnlySpan<byte> response)
    {
        ValidateResponseOpcode(response, Authenticate);
        if (response.Length < 3)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete authentication challenge.");
        }

        if (response[2] != 0)
        {
            throw new OpenDisplayAuthenticationException((OpenDisplayAuthenticationStatus)response[2]);
        }

        if (response.Length < 19)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete authentication nonce.");
        }

        return (response.Slice(3, 16).ToArray(), response.Length >= 23 ? response.Slice(19, 4).ToArray() : [0, 0, 0, 1]);
    }

    public static byte[] ParseAuthenticateSuccess(ReadOnlySpan<byte> response)
    {
        ValidateResponseOpcode(response, Authenticate);
        if (response.Length < 3)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete authentication response.");
        }

        if (response[2] != 0)
        {
            throw new OpenDisplayAuthenticationException((OpenDisplayAuthenticationStatus)response[2]);
        }

        if (response.Length < 19)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete authentication proof.");
        }

        return response.Slice(3, 16).ToArray();
    }

    public static byte[] DeriveSessionKey(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> deviceId)
    {
        ValidateCryptoInputs(masterKey, clientNonce, serverNonce, deviceId);
        byte[] cmacInput = [.. "OpenDisplay session"u8, 0, .. deviceId, .. clientNonce, .. serverNonce, 0, 0x80];
        byte[] intermediate = ComputeCmac(masterKey, cmacInput);
        byte[] input = new byte[16];
        input[7] = 1;
        intermediate.AsSpan(0, 8).CopyTo(input.AsSpan(8));
        return EncryptAesBlock(masterKey, input);
    }

    public static byte[] DeriveSessionId(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce)
        => ComputeCmac(sessionKey, [.. clientNonce, .. serverNonce])[..8];

    public static byte[] ComputeChallengeResponse(ReadOnlySpan<byte> masterKey, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> deviceId)
    {
        ValidateCryptoInputs(masterKey, clientNonce, serverNonce, deviceId);
        return ComputeCmac(masterKey, [.. serverNonce, .. clientNonce, .. deviceId]);
    }

    public static byte[] ComputeServerProof(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> deviceId)
        => ComputeChallengeResponse(sessionKey, serverNonce, clientNonce, deviceId);

    public static byte[] EncryptCommand(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> sessionId, ulong counter, ReadOnlySpan<byte> command)
    {
        if (sessionKey.Length != 16 || sessionId.Length != 8 || command.Length < 2 || command.Length - 2 > byte.MaxValue)
        {
            throw new ArgumentException("Invalid encrypted OpenDisplay command inputs.");
        }

        byte[] result = new byte[2 + 16 + command.Length - 1 + 12];
        command[..2].CopyTo(result);
        sessionId.CopyTo(result.AsSpan(2));
        BinaryPrimitives.WriteUInt64BigEndian(result.AsSpan(10), counter);
        byte[] plaintext = [checked((byte)(command.Length - 2)), .. command[2..]];
        using AesCcm aes = new(sessionKey.ToArray());
        aes.Encrypt(result.AsSpan(5, 13), plaintext, result.AsSpan(18, plaintext.Length), result.AsSpan(18 + plaintext.Length, 12), result.AsSpan(0, 2));
        return result;
    }

    public static byte[] DecryptResponse(ReadOnlySpan<byte> sessionKey, ReadOnlySpan<byte> response)
    {
        if (sessionKey.Length != 16 || response.Length < 31)
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid encrypted response.");
        }

        int ciphertextLength = response.Length - 30;
        byte[] plaintext = new byte[ciphertextLength];
        using AesCcm aes = new(sessionKey.ToArray());
        aes.Decrypt(response.Slice(5, 13), response.Slice(18, ciphertextLength), response[^12..], plaintext, response[..2]);
        if (plaintext[0] != plaintext.Length - 1)
        {
            throw new CryptographicException("OpenDisplay encrypted response has an invalid payload length.");
        }

        return [.. response[..2], .. plaintext[1..]];
    }

    private static void ValidateResponseOpcode(ReadOnlySpan<byte> response, ushort opcode)
    {
        if (response.Length < 2 || (ReadOpcode(response) & ~ResponseHighBitFlag) != opcode)
        {
            throw new InvalidOperationException($"OpenDisplay returned an unexpected response for command 0x{opcode:X4}.");
        }
    }

    private static void ValidateCryptoInputs(ReadOnlySpan<byte> key, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, ReadOnlySpan<byte> deviceId)
    {
        if (key.Length != 16 || clientNonce.Length != 16 || serverNonce.Length != 16 || deviceId.Length != 4)
        {
            throw new ArgumentException("OpenDisplay authentication requires a 16-byte key and nonces plus a 4-byte device ID.");
        }
    }

    private static byte[] ComputeCmac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        byte[] zero = new byte[16];
        byte[] subKey1 = DoubleBlock(EncryptAesBlock(key, zero));
        byte[] subKey2 = DoubleBlock(subKey1);
        int blockCount = Math.Max(1, (data.Length + 15) / 16);
        bool complete = data.Length > 0 && data.Length % 16 == 0;
        byte[] last = new byte[16];
        if (complete)
        {
            data[^16..].CopyTo(last);
            Xor(last, subKey1);
        }
        else
        {
            data.Slice((blockCount - 1) * 16).CopyTo(last);
            last[data.Length % 16] = 0x80;
            Xor(last, subKey2);
        }

        byte[] state = new byte[16];
        for (int index = 0; index < blockCount - 1; index++)
        {
            byte[] block = data.Slice(index * 16, 16).ToArray();
            Xor(block, state);
            state = EncryptAesBlock(key, block);
        }

        Xor(last, state);
        return EncryptAesBlock(key, last);
    }

    private static byte[] EncryptAesBlock(ReadOnlySpan<byte> key, ReadOnlySpan<byte> block)
    {
        using Aes aes = Aes.Create();
        aes.Key = key.ToArray();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using ICryptoTransform encryptor = aes.CreateEncryptor();
        return encryptor.TransformFinalBlock(block.ToArray(), 0, 16);
    }

    private static byte[] DoubleBlock(ReadOnlySpan<byte> block)
    {
        byte[] result = new byte[16];
        byte carry = 0;
        for (int index = 15; index >= 0; index--)
        {
            result[index] = (byte)((block[index] << 1) | carry);
            carry = (byte)(block[index] >> 7);
        }

        if (carry != 0)
        {
            result[^1] ^= 0x87;
        }

        return result;
    }

    private static void Xor(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] ^= source[index];
        }
    }

    public static byte[] CreatePipeStartPacket(
        bool compressed,
        byte requestedWindow,
        byte requestedAcknowledgementCadence,
        ushort clientMaximumFrame,
        int uncompressedSize,
        OpenDisplayPipePartialRequest? partial = null)
    {
        if (requestedWindow is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedWindow));
        }

        if (requestedAcknowledgementCadence is 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedAcknowledgementCadence));
        }

        if (clientMaximumFrame < PipeFrameOverhead)
        {
            throw new ArgumentOutOfRangeException(nameof(clientMaximumFrame));
        }

        if (uncompressedSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(uncompressedSize));
        }

        byte[] packet = new byte[partial is null ? 12 : 24];
        BinaryPrimitives.WriteUInt16BigEndian(packet, PipeWriteStart);
        packet[2] = PipeVersion;
        packet[3] = (byte)((compressed ? PipeCompressedFlag : 0) | (partial is null ? 0 : PipePartialFlag));
        packet[4] = requestedWindow;
        packet[5] = requestedAcknowledgementCadence;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), clientMaximumFrame);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(8), uncompressedSize);
        if (partial is { } request)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), request.OldEtag);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), request.Region.X);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(18), request.Region.Y);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(20), request.Region.Width);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(22), request.Region.Height);
        }
        return packet;
    }

    public static byte[] CreatePipeDataPacket(byte sequence, ReadOnlySpan<byte> chunk)
    {
        byte[] packet = new byte[PipeFrameOverhead + chunk.Length];
        BinaryPrimitives.WriteUInt16BigEndian(packet, PipeWriteData);
        packet[2] = sequence;
        chunk.CopyTo(packet.AsSpan(PipeFrameOverhead));
        return packet;
    }

    public static byte[] CreatePipeEndPacket(bool fastRefresh, uint? newEtag = null)
    {
        if (newEtag is null)
        {
            return [0x00, (byte)PipeWriteEnd, fastRefresh ? (byte)1 : (byte)0];
        }

        byte[] packet = new byte[7];
        BinaryPrimitives.WriteUInt16BigEndian(packet, PipeWriteEnd);
        packet[2] = fastRefresh ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(3), newEtag.Value);
        return packet;
    }

    public static bool TryParsePipeStartResponse(
        ReadOnlySpan<byte> response,
        out OpenDisplayPipeStartResponse startResponse,
        out byte rejectionCode)
    {
        startResponse = default;
        rejectionCode = 0;
        if (response.Length >= 3 && response[0] == 0xFF && response[1] == (byte)PipeWriteStart)
        {
            rejectionCode = response[2];
            return false;
        }

        if (response.Length < 8 || ReadOpcode(response) != PipeWriteStart || response[0] != 0)
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid PIPE_WRITE START response.");
        }

        startResponse = new OpenDisplayPipeStartResponse(
            response[2],
            response[3],
            response[4],
            BinaryPrimitives.ReadUInt16LittleEndian(response.Slice(5, sizeof(ushort))),
            response[7]);
        return true;
    }

    public static OpenDisplayPipeSack ParsePipeSack(ReadOnlySpan<byte> response)
    {
        if (response.Length < 7 || response[0] != 0 || ReadOpcode(response) != PipeWriteData)
        {
            throw new InvalidOperationException("OpenDisplay returned an invalid PIPE_WRITE ACK.");
        }

        return new OpenDisplayPipeSack(
            response[2],
            BinaryPrimitives.ReadUInt32LittleEndian(response.Slice(3, sizeof(uint))));
    }

    public static bool IsPipeDataNack(ReadOnlySpan<byte> response, out byte errorCode)
    {
        errorCode = 0;
        if (response.Length < 3 || response[0] != 0xFF || response[1] != (byte)PipeWriteData)
        {
            return false;
        }

        errorCode = response[2];
        return true;
    }

    public static IEnumerable<int> GetAcknowledgedPipeChunks(
        OpenDisplayPipeSack sack,
        int lowestUnacknowledgedChunk)
    {
        int delta = (sack.HighestSeenSequence - (lowestUnacknowledgedChunk & 0xFF)) & 0xFF;
        if (delta > 128)
        {
            delta -= 256;
        }

        int highestChunk = lowestUnacknowledgedChunk + delta;
        if (highestChunk >= 0)
        {
            yield return highestChunk;
        }

        for (int bit = 0; bit < 32; bit++)
        {
            if ((sack.AcknowledgementMask & (1U << bit)) != 0 && highestChunk - bit - 1 >= 0)
            {
                yield return highestChunk - bit - 1;
            }
        }
    }

    public static byte[] CreateNineBitZlibStream(ReadOnlySpan<byte> input)
    {
        using MemoryStream output = new(input.Length + (input.Length / ushort.MaxValue + 1) * 5 + 6);
        output.WriteByte(0x18);
        output.WriteByte(0x19);

        int offset = 0;
        Span<byte> blockHeader = stackalloc byte[sizeof(ushort) * 2];
        do
        {
            int length = Math.Min(ushort.MaxValue, input.Length - offset);
            bool isFinal = offset + length == input.Length;
            output.WriteByte(isFinal ? (byte)0x01 : (byte)0x00);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader, (ushort)length);
            BinaryPrimitives.WriteUInt16LittleEndian(blockHeader[sizeof(ushort)..], (ushort)~length);
            output.Write(blockHeader);
            output.Write(input.Slice(offset, length));
            offset += length;
        }
        while (offset < input.Length);

        uint adler32 = CalculateAdler32(input);
        Span<byte> checksum = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, adler32);
        output.Write(checksum);
        return output.ToArray();
    }

    private static uint CalculateAdler32(ReadOnlySpan<byte> input)
    {
        const uint modulus = 65521;
        uint a = 1;
        uint b = 0;
        foreach (byte value in input)
        {
            a = (a + value) % modulus;
            b = (b + a) % modulus;
        }

        return (b << 16) | a;
    }

    public static OpenDisplayPanelSize ParsePanelSize(ReadOnlySpan<byte> configuration)
    {
        const int outerHeaderLength = 3;
        const int crcLength = 2;
        const int packetHeaderLength = 2;
        const byte displayPacketId = 0x20;

        if (configuration.Length < outerHeaderLength + crcLength)
        {
            throw new InvalidOperationException("OpenDisplay returned an incomplete configuration.");
        }

        int offset = outerHeaderLength;
        int end = configuration.Length - crcLength;
        while (offset < end)
        {
            if (end - offset < packetHeaderLength)
            {
                throw new InvalidOperationException("OpenDisplay returned a truncated configuration packet header.");
            }

            byte packetId = configuration[offset + 1];
            offset += packetHeaderLength;
            if (!PacketSizes.TryGetValue(packetId, out int packetSize))
            {
                throw new InvalidOperationException($"OpenDisplay returned an unknown configuration packet type 0x{packetId:X2}.");
            }

            if (end - offset < packetSize)
            {
                throw new InvalidOperationException($"OpenDisplay returned a truncated configuration packet type 0x{packetId:X2}.");
            }

            if (packetId == displayPacketId)
            {
                int width = BinaryPrimitives.ReadUInt16LittleEndian(configuration.Slice(offset + 4, sizeof(ushort)));
                int height = BinaryPrimitives.ReadUInt16LittleEndian(configuration.Slice(offset + 6, sizeof(ushort)));
                if (width == 0 || height == 0)
                {
                    throw new InvalidOperationException("OpenDisplay returned invalid panel dimensions.");
                }

                return new OpenDisplayPanelSize(width, height, (OpenDisplayColorScheme)configuration[offset + 21]);
            }

            offset += packetSize;
        }

        throw new InvalidOperationException("OpenDisplay did not return a display configuration.");
    }

    private static readonly IReadOnlyDictionary<byte, int> PacketSizes = new Dictionary<byte, int>
    {
        [0x01] = 22,
        [0x02] = 22,
        [0x04] = 30,
        [0x20] = 46,
        [0x21] = 22,
        [0x23] = 30,
        [0x24] = 30,
        [0x25] = 30,
        [0x26] = 160,
        [0x27] = 64,
        [0x28] = 32,
        [0x29] = 32,
        [0x2A] = 32,
        [0x2B] = 32,
        [0x2C] = 288,
    };
}

internal readonly record struct OpenDisplayPipeStartResponse(
    byte Version,
    byte MaximumWindow,
    byte MaximumAcknowledgementCadence,
    ushort MaximumFrame,
    byte Flags);

internal readonly record struct OpenDisplayPipeSack(byte HighestSeenSequence, uint AcknowledgementMask);

internal readonly record struct OpenDisplayPipePartialRequest(uint OldEtag, OpenDisplayPartialRegion Region);
