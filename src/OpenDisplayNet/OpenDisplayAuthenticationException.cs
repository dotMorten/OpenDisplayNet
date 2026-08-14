namespace OpenDisplayNet;

/// <summary>Indicates that an OpenDisplay device rejected authentication.</summary>
public sealed class OpenDisplayAuthenticationException(byte status)
    : InvalidOperationException($"OpenDisplay authentication failed with status 0x{status:X2}.")
{
    /// <summary>The authentication status returned by the device.</summary>
    public byte Status { get; } = status;
}
