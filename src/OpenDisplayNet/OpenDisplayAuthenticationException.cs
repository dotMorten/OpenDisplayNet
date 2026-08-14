namespace OpenDisplayNet;

/// <summary>Indicates that an OpenDisplay device rejected authentication.</summary>
public sealed class OpenDisplayAuthenticationException(OpenDisplayAuthenticationStatus status)
    : InvalidOperationException($"OpenDisplay authentication failed with status 0x{(byte)status:X2} ({status}).")
{
    /// <summary>The authentication status returned by the device.</summary>
    public OpenDisplayAuthenticationStatus Status { get; } = status;

    /// <summary>The unmodified authentication status byte returned by the device.</summary>
    /// <remarks>Use this value to diagnose statuses introduced by newer firmware.</remarks>
    public byte RawStatus { get; } = (byte)status;
}
