namespace OpenDisplayNet;

/// <summary>Contains a reading reported by a configured SHT40 sensor.</summary>
public sealed record OpenDisplaySensorReading(
    byte InstanceNumber,
    OpenDisplaySensorType SensorType,
    double TemperatureCelsius,
    double HumidityPercent);
