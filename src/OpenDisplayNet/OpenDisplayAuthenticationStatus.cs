namespace OpenDisplayNet;

/// <summary>Describes an authentication status returned by an OpenDisplay device.</summary>
public enum OpenDisplayAuthenticationStatus : byte
{
    /// <summary>A challenge was issued during step 1, or authentication succeeded during step 2.</summary>
    Success = 0x00,

    /// <summary>The supplied authentication key did not match the configured device key.</summary>
    InvalidKey = 0x01,

    /// <summary>The device already has an authenticated session.</summary>
    AlreadyAuthenticated = 0x02,

    /// <summary>The device has no authentication key configured.</summary>
    EncryptionNotConfigured = 0x03,

    /// <summary>The device rejected the request because too many authentication attempts were made.</summary>
    RateLimited = 0x04,

    /// <summary>The device encountered an unspecified authentication error.</summary>
    Error = 0xFF,
}
