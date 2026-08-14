using System.IO.Compression;
using OpenDisplayNet;
using Xunit;

namespace OpenDisplayNet.Tests;

public sealed class OpenDisplayProtocolTests
{
    [Fact]
    public void CreateStartPacket_UsesBigEndianOpcodeAndLittleEndianImageSize()
    {
        byte[] packet = OpenDisplayProtocol.CreateStartPacket(0x12345678, [0xAA, 0xBB]);

        Assert.Equal([0x00, 0x70, 0x78, 0x56, 0x34, 0x12, 0xAA, 0xBB], packet);
    }

    [Fact]
    public void CreateDataAndEndPackets_UseDirectWriteOpcodes()
    {
        Assert.Equal([0x00, 0x71, 0xAA, 0xBB], OpenDisplayProtocol.CreateDataPacket([0xAA, 0xBB]));
        Assert.Equal([0x00, 0x72, 0x00], OpenDisplayProtocol.CreateEndPacket());
    }

    [Fact]
    public void GetAcknowledgementOpcode_SetsTheProtocolResponseBit()
    {
        Assert.Equal(0x8070, OpenDisplayProtocol.GetAcknowledgementOpcode(OpenDisplayProtocol.DirectWriteStart));
    }

    [Fact]
    public void CreatePipePackets_UseSpecifiedByteOrderAndRefreshMode()
    {
        Assert.Equal(
            [0x00, 0x80, 0x01, 0x01, 0x08, 0x04, 0xF4, 0x00, 0x78, 0x56, 0x34, 0x12],
            OpenDisplayProtocol.CreatePipeStartPacket(
                compressed: true,
                requestedWindow: 8,
                requestedAcknowledgementCadence: 4,
                clientMaximumFrame: 244,
                uncompressedSize: 0x12345678));
        Assert.Equal([0x00, 0x81, 0xFE, 0xAA, 0xBB], OpenDisplayProtocol.CreatePipeDataPacket(0xFE, [0xAA, 0xBB]));
        Assert.Equal([0x00, 0x82, 0x01], OpenDisplayProtocol.CreatePipeEndPacket(fastRefresh: true));
    }

    [Fact]
    public void CreatePartialStartPacket_UsesLegacyBigEndianFields()
    {
        byte[] packet = OpenDisplayProtocol.CreatePartialStartPacket(
            0x11223344,
            0x55667788,
            new OpenDisplayPartialRegion(8, 2, 16, 3),
            compressed: true,
            [0xAA, 0xBB]);

        Assert.Equal(
            [
                0x00, 0x76, 0x01, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88,
                0x00, 0x08, 0x00, 0x02, 0x00, 0x10, 0x00, 0x03, 0xAA, 0xBB,
            ],
            packet);
        Assert.Equal(
            [0x00, 0x72, 0x01, 0x55, 0x66, 0x77, 0x88],
            OpenDisplayProtocol.CreateEndPacket(fastRefresh: true, 0x55667788));
    }

    [Fact]
    public void CreatePipePartialStartPacket_UsesFlagAndLittleEndianExtension()
    {
        byte[] packet = OpenDisplayProtocol.CreatePipeStartPacket(
            compressed: true,
            requestedWindow: 8,
            requestedAcknowledgementCadence: 4,
            clientMaximumFrame: 244,
            uncompressedSize: 12,
            new OpenDisplayPipePartialRequest(0x11223344, new OpenDisplayPartialRegion(8, 2, 16, 3)));

        Assert.Equal(
            [
                0x00, 0x80, 0x01, 0x03, 0x08, 0x04, 0xF4, 0x00, 0x0C, 0x00, 0x00, 0x00,
                0x44, 0x33, 0x22, 0x11, 0x08, 0x00, 0x02, 0x00, 0x10, 0x00, 0x03, 0x00,
            ],
            packet);
        Assert.Equal(
            [0x00, 0x82, 0x01, 0x55, 0x66, 0x77, 0x88],
            OpenDisplayProtocol.CreatePipeEndPacket(fastRefresh: true, 0x55667788));
    }

    [Fact]
    public void PartialUpdate_ExposesTypedRegionAndBuffers()
    {
        OpenDisplayPartialUpdate valid = new(
            1,
            2,
            new OpenDisplayPartialRegion(8, 0, 8, 2),
            new byte[] { 0x00, 0x00 },
            new byte[] { 0xFF, 0xFF });

        Assert.Equal(2, valid.OldPixels.Length);
        Assert.Equal(2, valid.NewPixels.Length);
        Assert.Equal((ushort)8, valid.Region.Width);
        Assert.Equal((uint)1, valid.OldEtag);
        Assert.Equal((uint)2, valid.NewEtag);
    }

    [Fact]
    public void PipeResponses_ParseNegotiationAndSelectiveAcknowledgements()
    {
        Assert.True(OpenDisplayProtocol.TryParsePipeStartResponse(
            [0x00, 0x80, 0x01, 0x10, 0x08, 0xF4, 0x00, 0x01],
            out OpenDisplayPipeStartResponse start,
            out byte rejection));
        Assert.Equal(0, rejection);
        Assert.Equal((byte)1, start.Version);
        Assert.Equal((byte)16, start.MaximumWindow);
        Assert.Equal((ushort)244, start.MaximumFrame);
        Assert.Equal((byte)1, start.Flags);

        OpenDisplayPipeSack sack = OpenDisplayProtocol.ParsePipeSack([0x00, 0x81, 0x02, 0x03, 0x00, 0x00, 0x00]);
        Assert.Equal([0, 1, 2], OpenDisplayProtocol.GetAcknowledgedPipeChunks(sack, 0).Order());
    }

    [Fact]
    public void PipeStartRejectionAndDataNack_AreRecognized()
    {
        Assert.False(OpenDisplayProtocol.TryParsePipeStartResponse(
            [0xFF, 0x80, 0x02, 0x00],
            out _,
            out byte rejection));
        Assert.Equal(0x02, rejection);
        Assert.True(OpenDisplayProtocol.IsPipeDataNack([0xFF, 0x81, 0x03], out byte error));
        Assert.Equal(0x03, error);
    }

    [Fact]
    public void PipeSack_ResolvesSequenceWrapAgainstCurrentWindow()
    {
        OpenDisplayPipeSack sack = new(0x00, 0x00000001);

        Assert.Equal([255, 256], OpenDisplayProtocol.GetAcknowledgedPipeChunks(sack, 255).Order());
    }

    [Fact]
    public void NineBitZlibStream_UsesValidFirmwareWindowHeader()
    {
        byte[] source = Enumerable.Range(0, 70_000).Select(value => (byte)value).ToArray();
        byte[] compressed = OpenDisplayProtocol.CreateNineBitZlibStream(source);

        Assert.Equal([0x18, 0x19], compressed[..2]);
        using ZLibStream zlib = new(new MemoryStream(compressed), CompressionMode.Decompress);
        using MemoryStream restored = new();
        zlib.CopyTo(restored);
        Assert.Equal(source, restored.ToArray());
    }

    [Fact]
    public void ParsePanelSize_ReadsDisplayPacketAfterRequiredConfigurationPackets()
    {
        byte[] configuration = new byte[3 + (2 + 22) + (2 + 22) + (2 + 30) + (2 + 46) + 2];
        configuration[2] = 1;

        int offset = 3;
        offset = AddPacket(configuration, offset, 0, 0x01, 22);
        offset = AddPacket(configuration, offset, 1, 0x02, 22);
        offset = AddPacket(configuration, offset, 2, 0x04, 30);
        AddPacket(configuration, offset, 3, 0x20, 46);
        configuration[offset + 2 + 4] = 0x28;
        configuration[offset + 2 + 5] = 0x01;
        configuration[offset + 2 + 6] = 0x80;
        configuration[offset + 2 + 7] = 0x02;
        configuration[offset + 2 + 21] = (byte)OpenDisplayColorScheme.Gray4;

        Assert.Equal(
            new OpenDisplayPanelSize(296, 640, OpenDisplayColorScheme.Gray4),
            OpenDisplayProtocol.ParsePanelSize(configuration));

        configuration[offset + 2 + 21] = 0xFE;
        Assert.Equal(
            (OpenDisplayColorScheme)0xFE,
            OpenDisplayProtocol.ParsePanelSize(configuration).ColorScheme);
    }

    [Fact]
    public void AuthenticationPacketsAndCrypto_MatchReferenceProtocol()
    {
        byte[] key = Enumerable.Range(0, 16).Select(value => (byte)value).ToArray();
        byte[] clientNonce = Enumerable.Range(16, 16).Select(value => (byte)value).ToArray();
        byte[] serverNonce = Enumerable.Range(32, 16).Select(value => (byte)value).ToArray();
        byte[] deviceId = [0xDE, 0xAD, 0xBE, 0xEF];

        Assert.Equal([0x00, 0x50, 0x00], OpenDisplayProtocol.CreateAuthenticateStep1Packet());
        (byte[] parsedServerNonce, byte[] parsedDeviceId) = OpenDisplayProtocol.ParseAuthenticateChallenge(
            [0x80, 0x50, 0, .. serverNonce, .. deviceId]);
        Assert.Equal(serverNonce, parsedServerNonce);
        Assert.Equal(deviceId, parsedDeviceId);
        Assert.Equal(
            Enumerable.Repeat((byte)0xA5, 16).ToArray(),
            OpenDisplayProtocol.ParseAuthenticateSuccess(
                [0x00, 0x50, 0, .. Enumerable.Repeat((byte)0xA5, 16)]));
        Assert.Equal(
            [0x00, 0x50, .. clientNonce, .. OpenDisplayProtocol.ComputeChallengeResponse(key, serverNonce, clientNonce, deviceId)],
            OpenDisplayProtocol.CreateAuthenticateStep2Packet(
                clientNonce,
                OpenDisplayProtocol.ComputeChallengeResponse(key, serverNonce, clientNonce, deviceId)));

        byte[] sessionKey = OpenDisplayProtocol.DeriveSessionKey(key, clientNonce, serverNonce, deviceId);
        Assert.Equal("779B24BBFBCC1D0A2FC788FED85C7584", Convert.ToHexString(sessionKey));
        Assert.Equal("1A3825300EB2E9FE", Convert.ToHexString(OpenDisplayProtocol.DeriveSessionId(sessionKey, clientNonce, serverNonce)));
        Assert.Equal("B22B81060AB8CED14880C701D7E3F4C5", Convert.ToHexString(OpenDisplayProtocol.ComputeChallengeResponse(key, serverNonce, clientNonce, deviceId)));
        Assert.Equal("D139694FE023F7DDEE8EADF01EC088AA", Convert.ToHexString(OpenDisplayProtocol.ComputeServerProof(sessionKey, serverNonce, clientNonce, deviceId)));

        byte[] encrypted = OpenDisplayProtocol.EncryptCommand(
            sessionKey,
            OpenDisplayProtocol.DeriveSessionId(sessionKey, clientNonce, serverNonce),
            7,
            [0x00, 0x73, 0x02, 0xAB, 0xCD, 0xEF]);
        Assert.Equal(
            "00731A3825300EB2E9FE000000000000000788A7FA9CA8B6C9D4CF458E37C333ADBE9C",
            Convert.ToHexString(encrypted));
        Assert.Equal([0x00, 0x73, 0x02, 0xAB, 0xCD, 0xEF], OpenDisplayProtocol.DecryptResponse(sessionKey, encrypted));
    }

    [Theory]
    [InlineData(0x01, OpenDisplayAuthenticationStatus.InvalidKey)]
    [InlineData(0x02, OpenDisplayAuthenticationStatus.AlreadyAuthenticated)]
    [InlineData(0x03, OpenDisplayAuthenticationStatus.EncryptionNotConfigured)]
    [InlineData(0x04, OpenDisplayAuthenticationStatus.RateLimited)]
    [InlineData(0xFF, OpenDisplayAuthenticationStatus.Error)]
    public void AuthenticationFailure_ExposesProtocolStatus(byte rawStatus, OpenDisplayAuthenticationStatus expectedStatus)
    {
        OpenDisplayAuthenticationException exception = Assert.Throws<OpenDisplayAuthenticationException>(
            () => OpenDisplayProtocol.ParseAuthenticateSuccess([0x80, 0x50, rawStatus]));

        Assert.Equal(expectedStatus, exception.Status);
        Assert.Equal(rawStatus, exception.RawStatus);
    }

    [Fact]
    public void AuthenticationFailure_PreservesUnknownStatus()
    {
        OpenDisplayAuthenticationException exception = Assert.Throws<OpenDisplayAuthenticationException>(
            () => OpenDisplayProtocol.ParseAuthenticateChallenge([0x80, 0x50, 0x7E]));

        Assert.Equal((OpenDisplayAuthenticationStatus)0x7E, exception.Status);
        Assert.Equal(0x7E, exception.RawStatus);
    }

    [Fact]
    public void ParsesFirmwareManufacturerDataAndSht40Sensor()
    {
        Assert.Equal(
            new OpenDisplayFirmwareVersion(2, 25, "abc", 1),
            OpenDisplayProtocol.ParseFirmwareVersion([0x80, 0x43, 2, 25, 3, (byte)'a', (byte)'b', (byte)'c', 1]));
        OpenDisplayManufacturerData manufacturerRecord = OpenDisplayProtocol.ParseManufacturerData(
            [0x00, 0x44, 0x46, 0x24, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 120, 0x23, 0xA7]);
        Assert.Equal(2910, manufacturerRecord.BatteryMillivolts);
        Assert.Equal(20, manufacturerRecord.ChipTemperatureCelsius);
        Assert.Equal((byte)10, manufacturerRecord.LoopCounter);
        Assert.True(manufacturerRecord.Rebooted);
        Assert.True(manufacturerRecord.ConnectionRequested);
        Assert.Equal(Enumerable.Range(0, 11).Select(value => (byte)value).ToArray(), manufacturerRecord.DynamicData.ToArray());

        byte[] configuration = new byte[3 + 2 + 30 + 2];
        configuration[3] = 0;
        configuration[4] = 0x23;
        configuration[5] = 2;
        configuration[6] = 4;
        configuration[10] = 7;
        byte[] manufacturerData = new byte[16];
        manufacturerData[9] = 0xD7;
        manufacturerData[10] = 0xC1;
        manufacturerData[11] = 0x09;

        Assert.Equal(
            [new OpenDisplaySensorReading(2, OpenDisplaySensorType.Sht40, 22.4, 47.1)],
            OpenDisplayProtocol.ReadSht40Sensors(configuration, manufacturerData));
    }

    [Fact]
    public void ManufacturerData_ParsesLegacyFormat()
    {
        OpenDisplayManufacturerData manufacturerData = OpenDisplayManufacturerData.Parse(
            [0, 0, 0, 0, 0, 0, 0, 0xBC, 0x0A, unchecked((byte)-5), 42]);

        Assert.Equal(2748, manufacturerData.BatteryMillivolts);
        Assert.Equal(-5, manufacturerData.ChipTemperatureCelsius);
        Assert.Equal((byte)42, manufacturerData.LoopCounter);
        Assert.Null(manufacturerData.Rebooted);
        Assert.Null(manufacturerData.ConnectionRequested);
        Assert.Empty(manufacturerData.DynamicData.ToArray());
    }

    [Fact]
    public void ProtocolIdentifierEnums_PreserveWireValues()
    {
        Assert.Equal(5, (byte)OpenDisplayColorScheme.Gray4);
        Assert.Equal(4, (ushort)OpenDisplaySensorType.Sht40);
        Assert.Equal(0xFE, (byte)(OpenDisplayColorScheme)0xFE);
        Assert.Equal(0xFFFE, (ushort)(OpenDisplaySensorType)0xFFFE);
    }

    [Fact]
    public void LedAndBuzzerConfigurations_SerializeToFirmwarePayloads()
    {
        OpenDisplayLedFlashConfig led = OpenDisplayLedFlashConfig.Single(
            color: 0xE0,
            flashCount: 2,
            loopDelayUnits: 3,
            interDelayUnits: 4,
            brightness: 8,
            groupRepeats: 2);
        Assert.Equal(
            [0x00, 0x73, 0x01, 0x71, 0xE0, 0x32, 0x04, 0, 0, 0, 0, 0, 0, 1, 0],
            OpenDisplayProtocol.CreateLedActivatePacket(1, led));

        OpenDisplayBuzzerActivateConfig buzzer = OpenDisplayBuzzerActivateConfig.Single(120, 20, repeats: 2);
        Assert.Equal([0x00, 0x77, 0x03, 0x02, 0x01, 0x01, 120, 20], OpenDisplayProtocol.CreateBuzzerActivatePacket(3, buzzer));
    }

    private static int AddPacket(byte[] configuration, int offset, byte number, byte id, int payloadLength)
    {
        configuration[offset] = number;
        configuration[offset + 1] = id;
        return offset + 2 + payloadLength;
    }
}
