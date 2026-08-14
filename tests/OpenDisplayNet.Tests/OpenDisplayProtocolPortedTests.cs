using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using OpenDisplayNet;
using Xunit;

namespace OpenDisplayNet.Tests;

public sealed class OpenDisplayProtocolPortedTests
{
    [Fact]
    public void AuthenticateChallenge_OldFormatUsesDefaultDeviceId()
    {
        byte[] serverNonce = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();

        (byte[] nonce, byte[] deviceId) = OpenDisplayProtocol.ParseAuthenticateChallenge(
            [0x80, 0x50, 0, .. serverNonce]);

        Assert.Equal(serverNonce, nonce);
        Assert.Equal([0, 0, 0, 1], deviceId);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x02)]
    [InlineData(0x03)]
    [InlineData(0x04)]
    public void AuthenticateResponses_NonSuccessStatusCarriesFirmwareStatus(byte status)
    {
        OpenDisplayAuthenticationException challenge = Assert.Throws<OpenDisplayAuthenticationException>(
            () => OpenDisplayProtocol.ParseAuthenticateChallenge([0, 0x50, status, .. new byte[16]]));
        OpenDisplayAuthenticationException success = Assert.Throws<OpenDisplayAuthenticationException>(
            () => OpenDisplayProtocol.ParseAuthenticateSuccess([0, 0x50, status, .. new byte[16]]));

        Assert.Equal((OpenDisplayAuthenticationStatus)status, challenge.Status);
        Assert.Equal((OpenDisplayAuthenticationStatus)status, success.Status);
        Assert.Equal(status, challenge.RawStatus);
        Assert.Equal(status, success.RawStatus);
    }

    [Fact]
    public void AuthenticateResponses_RejectIncompleteAndUnexpectedFrames()
    {
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.ParseAuthenticateChallenge([0, 0x50, 0, .. new byte[15]]));
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.ParseAuthenticateSuccess([0, 0x50, 0, .. new byte[15]]));
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.ParseAuthenticateSuccess([0, 0x43, 0, .. new byte[16]]));
    }

    [Fact]
    public void AuthenticateStep2_RequiresSixteenByteNonceAndProof()
    {
        Assert.Throws<ArgumentException>(
            () => OpenDisplayProtocol.CreateAuthenticateStep2Packet(new byte[15], new byte[16]));
        Assert.Throws<ArgumentException>(
            () => OpenDisplayProtocol.CreateAuthenticateStep2Packet(new byte[16], new byte[15]));
    }

    [Fact]
    public void CryptoDerivations_ChangeWhenAuthenticationInputsChange()
    {
        byte[] key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        byte[] clientNonce = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        byte[] serverNonce = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        byte[] deviceId = [0, 0, 0, 1];

        byte[] sessionKey = OpenDisplayProtocol.DeriveSessionKey(key, clientNonce, serverNonce, deviceId);
        byte[] changedSessionKey = OpenDisplayProtocol.DeriveSessionKey(key, clientNonce, serverNonce, [0, 0, 0, 2]);
        byte[] challenge = OpenDisplayProtocol.ComputeChallengeResponse(key, serverNonce, clientNonce, deviceId);
        byte[] changedChallenge = OpenDisplayProtocol.ComputeChallengeResponse(key, serverNonce, clientNonce, [0, 0, 0, 2]);

        Assert.Equal(16, sessionKey.Length);
        Assert.Equal(8, OpenDisplayProtocol.DeriveSessionId(sessionKey, clientNonce, serverNonce).Length);
        Assert.NotEqual(sessionKey, changedSessionKey);
        Assert.NotEqual(challenge, changedChallenge);
    }

    [Fact]
    public void EncryptedCommand_ContainsSessionIdAndBigEndianCounter()
    {
        byte[] sessionKey = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        byte[] sessionId = Enumerable.Range(16, 8).Select(value => (byte)value).ToArray();

        byte[] encrypted = OpenDisplayProtocol.EncryptCommand(sessionKey, sessionId, 42, [0, 0x70, 0xAB, 0xCD]);

        Assert.Equal([0, 0x70], encrypted[..2]);
        Assert.Equal(sessionId, encrypted[2..10]);
        Assert.Equal(42UL, BinaryPrimitives.ReadUInt64BigEndian(encrypted.AsSpan(10, 8)));
        Assert.Equal(33, encrypted.Length);
        Assert.Throws<InvalidOperationException>(() => OpenDisplayProtocol.DecryptResponse(sessionKey, new byte[30]));
        Assert.ThrowsAny<CryptographicException>(() => OpenDisplayProtocol.DecryptResponse(new byte[16], encrypted));
    }

    [Fact]
    public void FirmwareAndManufacturerResponses_ValidateLengthEncodingAndEcho()
    {
        Assert.Equal(
            new OpenDisplayFirmwareVersion(1, 5, "gaberin", 0),
            OpenDisplayProtocol.ParseFirmwareVersion([0, 0x43, 1, 5, 7, (byte)'g', (byte)'a', (byte)'b', (byte)'e', (byte)'r', (byte)'i', (byte)'n']));
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.ParseFirmwareVersion([0, 0x43, 1, 5, 10, (byte)'s', (byte)'h', (byte)'o', (byte)'r', (byte)'t']));
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.ParseFirmwareVersion([0, 0x43, 1, 5, 1, 0xFF]));

        byte[] data = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        Assert.Equal(data, OpenDisplayProtocol.ParseManufacturerData([0x80, 0x44, .. data]));
        Assert.Throws<InvalidOperationException>(() => OpenDisplayProtocol.ParseManufacturerData([0, 0x44, .. new byte[15]]));
        Assert.Throws<InvalidOperationException>(() => OpenDisplayProtocol.ParseManufacturerData([0, 0x43, .. data]));
    }

    [Fact]
    public void Sht40Sensors_RespectConfiguredOffsetsAndOmitInvalidReadings()
    {
        byte[] configuration = CreateSensorConfiguration(
            (1, 4, 4),
            (2, 4, 7),
            (3, 5, 7));
        byte[] manufacturerData = new byte[16];
        manufacturerData[6] = 0xF4;
        manufacturerData[7] = 0x41;
        manufacturerData[8] = 0x06;
        manufacturerData[9] = 0xD7;
        manufacturerData[10] = 0xC1;
        manufacturerData[11] = 0x09;

        IReadOnlyList<OpenDisplaySensorReading> readings =
            OpenDisplayProtocol.ReadSht40Sensors(configuration, manufacturerData);

        Assert.Equal(
            [
                new OpenDisplaySensorReading(1, OpenDisplaySensorType.Sht40, 0, 50),
                new OpenDisplaySensorReading(2, OpenDisplaySensorType.Sht40, 22.4, 47.1),
            ],
            readings);

        manufacturerData[9] = manufacturerData[10] = manufacturerData[11] = 0xFF;
        Assert.Single(OpenDisplayProtocol.ReadSht40Sensors(configuration, manufacturerData));
        Assert.Empty(OpenDisplayProtocol.ReadSht40Sensors(CreateSensorConfiguration(), manufacturerData));
        Assert.Throws<ArgumentException>(() => OpenDisplayProtocol.ReadSht40Sensors(configuration, new byte[15]));
    }

    [Fact]
    public void Sht40Sensors_SkipsExtendedDataConfiguration()
    {
        byte[] configuration = new byte[3 + 2 + 288 + 2];
        configuration[4] = 0x2C;

        Assert.Empty(OpenDisplayProtocol.ReadSht40Sensors(configuration, new byte[16]));
    }

    [Fact]
    public void LedFlashConfiguration_SerializesAllStepsAndSpecialRepeatValues()
    {
        OpenDisplayLedFlashConfig configuration = new(
            1,
            8,
            new OpenDisplayLedFlashStep(0xE0, 2, 2, 5),
            new OpenDisplayLedFlashStep(0x1C, 3, 4, 7),
            new OpenDisplayLedFlashStep(0x03, 1, 6, 9),
            4,
            0xAA);

        Assert.Equal(
            [0x71, 0xE0, 0x22, 0x05, 0x1C, 0x43, 0x07, 0x03, 0x61, 0x09, 0x03, 0xAA],
            configuration.ToBytes());
        Assert.Equal(0xFE, new OpenDisplayLedFlashConfig(1, 8, new(0), GroupRepeats: null).ToBytes()[10]);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayLedFlashConfig(1, 0, new(0)).ToBytes());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayLedFlashConfig(1, 8, new(0), GroupRepeats: 255).ToBytes());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayLedFlashConfig(1, 8, new(0, 16)).ToBytes());
    }

    [Fact]
    public void BuzzerConfiguration_SerializesMultiplePatternsAndRejectsEmptyValues()
    {
        OpenDisplayBuzzerActivateConfig configuration = new(
            3,
            [
                new OpenDisplayBuzzerPattern([new OpenDisplayBuzzerStep(5, 10)]),
                new OpenDisplayBuzzerPattern([new OpenDisplayBuzzerStep(200, 50)]),
            ]);

        Assert.Equal([3, 2, 1, 5, 10, 1, 200, 50], configuration.ToBytes());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayBuzzerActivateConfig(0, configuration.Patterns).ToBytes());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayBuzzerActivateConfig(1, []).ToBytes());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenDisplayBuzzerPattern([]).ToBytes());
    }

    [Fact]
    public void PartialAndPipePackets_RepresentUncompressedDataAndRejectInvalidLimits()
    {
        Assert.Equal(
            [0, 0x76, 0, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 0, 0, 8, 0, 1],
            OpenDisplayProtocol.CreatePartialStartPacket(1, 2, new(0, 0, 8, 1), false, []));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => OpenDisplayProtocol.CreatePipeStartPacket(false, 0, 1, 244, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OpenDisplayProtocol.CreatePipeStartPacket(false, 1, 33, 244, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OpenDisplayProtocol.CreatePipeStartPacket(false, 1, 1, 2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => OpenDisplayProtocol.CreatePipeStartPacket(false, 1, 1, 244, -1));
    }

    [Fact]
    public void PipeResponses_HandleTrailingBytesHolesAndMalformedFrames()
    {
        Assert.True(OpenDisplayProtocol.TryParsePipeStartResponse(
            [0, 0x80, 1, 32, 16, 0xF4, 0, 1, 0xAA],
            out OpenDisplayPipeStartResponse start,
            out _));
        Assert.Equal((byte)32, start.MaximumWindow);
        Assert.Equal((byte)16, start.MaximumAcknowledgementCadence);

        OpenDisplayPipeSack sack = OpenDisplayProtocol.ParsePipeSack([0, 0x81, 4, 0b_0000_1101, 0, 0, 0, 0xAA]);
        Assert.Equal([0, 1, 3, 4], OpenDisplayProtocol.GetAcknowledgedPipeChunks(sack, 0).Order());
        Assert.Equal([9], OpenDisplayProtocol.GetAcknowledgedPipeChunks(new(9, 0), 10));
        Assert.Throws<InvalidOperationException>(() => OpenDisplayProtocol.ParsePipeSack([0, 0x81, 3]));
        Assert.Throws<InvalidOperationException>(
            () => OpenDisplayProtocol.TryParsePipeStartResponse([0, 0x70, 0, 0, 0, 0, 0, 0], out _, out _));
    }

    [Fact]
    public void NineBitZlibStream_AlsoEncodesAndRoundTripsAnEmptyPayload()
    {
        byte[] compressed = OpenDisplayProtocol.CreateNineBitZlibStream([]);

        Assert.Equal([0x18, 0x19], compressed[..2]);
        using ZLibStream zlib = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream restored = new();
        zlib.CopyTo(restored);
        Assert.Empty(restored.ToArray());
    }

    private static byte[] CreateSensorConfiguration(params (byte Instance, ushort Type, byte Start)[] sensors)
    {
        byte[] configuration = new byte[3 + sensors.Length * 32 + 2];
        int offset = 3;
        foreach ((byte instance, ushort type, byte start) in sensors)
        {
            configuration[offset + 1] = 0x23;
            configuration[offset + 2] = instance;
            BinaryPrimitives.WriteUInt16LittleEndian(configuration.AsSpan(offset + 3), type);
            configuration[offset + 7] = start;
            offset += 32;
        }

        return configuration;
    }
}
