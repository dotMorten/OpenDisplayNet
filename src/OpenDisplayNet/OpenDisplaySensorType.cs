namespace OpenDisplayNet;

/// <summary>Describes a sensor or power-management IC configured on an OpenDisplay device.</summary>
public enum OpenDisplaySensorType : ushort
{
    /// <summary>A generic temperature sensor.</summary>
    Temperature = 1,

    /// <summary>A generic humidity sensor.</summary>
    Humidity = 2,

    /// <summary>An AXP2101 power-management IC.</summary>
    Axp2101 = 3,

    /// <summary>A Sensirion SHT40 temperature and humidity sensor.</summary>
    Sht40 = 4,

    /// <summary>A TI BQ27220 fuel gauge.</summary>
    Bq27220 = 5,
}
