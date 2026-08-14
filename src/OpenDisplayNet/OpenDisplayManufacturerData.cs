namespace OpenDisplayNet;

/// <summary>Contains telemetry decoded from an OpenDisplay manufacturer-data record.</summary>
public sealed record OpenDisplayManufacturerData(
    int BatteryMillivolts,
    double ChipTemperatureCelsius,
    byte LoopCounter,
    bool? Rebooted,
    bool? ConnectionRequested,
    ReadOnlyMemory<byte> DynamicData,
    ReadOnlyMemory<byte> RawData)
{
    /// <summary>Decodes a legacy or version 1 OpenDisplay manufacturer-data record.</summary>
    public static OpenDisplayManufacturerData Parse(ReadOnlySpan<byte> manufacturerData)
    {
        ReadOnlySpan<byte> payload = manufacturerData;
        if (payload.Length >= 2 && payload[0] == 0x46 && payload[1] == 0x24)
        {
            payload = payload[2..];
        }

        if (payload.Length == 11)
        {
            return new OpenDisplayManufacturerData(
                payload[7] | (payload[8] << 8),
                (sbyte)payload[9],
                payload[10],
                null,
                null,
                ReadOnlyMemory<byte>.Empty,
                manufacturerData.ToArray());
        }

        if (payload.Length == 14)
        {
            byte status = payload[13];
            return new OpenDisplayManufacturerData(
                (payload[12] | ((status & 1) << 8)) * 10,
                payload[11] / 2d - 40d,
                (byte)(status >> 4),
                (status & 0x02) != 0,
                (status & 0x04) != 0,
                payload[..11].ToArray(),
                manufacturerData.ToArray());
        }

        throw new ArgumentException(
            "OpenDisplay manufacturer data must be an 11-byte legacy or 14-byte version 1 record, optionally prefixed with manufacturer ID 0x2446.",
            nameof(manufacturerData));
    }
}
