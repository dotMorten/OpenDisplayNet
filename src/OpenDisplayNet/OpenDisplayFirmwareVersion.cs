namespace OpenDisplayNet;

/// <summary>Identifies the firmware running on an OpenDisplay device.</summary>
public sealed record OpenDisplayFirmwareVersion(byte Major, byte Minor, string Sha, byte Patch);
