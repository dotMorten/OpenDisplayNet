namespace OpenDisplayNet;

/// <summary>Describes a byte-aligned region of a 1-bit OpenDisplay panel.</summary>
public readonly record struct OpenDisplayPartialRegion(ushort X, ushort Y, ushort Width, ushort Height);

/// <summary>
/// Contains the already encoded old and new 1-bit pixels for an OpenDisplay partial refresh.
/// </summary>
/// <remarks>
/// The image buffers contain only the region, packed MSB-first and row-major. Their length must
/// be <c>(Width / 8) * Height</c>; <see cref="OpenDisplayClient"/> does not depend on an image
/// or display library to encode them.
/// </remarks>
public sealed record OpenDisplayPartialUpdate(
    uint OldEtag,
    uint NewEtag,
    OpenDisplayPartialRegion Region,
    ReadOnlyMemory<byte> OldPixels,
    ReadOnlyMemory<byte> NewPixels);

/// <summary>Describes the outcome of a partial update request.</summary>
public enum OpenDisplayPartialUpdateResult
{
    /// <summary>The device applied the legacy 0x0076 partial transfer.</summary>
    AppliedLegacy,

    /// <summary>The device applied the negotiated PIPE_WRITE partial transfer.</summary>
    AppliedPipe,

    /// <summary>
    /// The device cannot safely apply the partial update. Send a full image to re-establish its
    /// displayed etag before attempting another partial update.
    /// </summary>
    FullRefreshRequired,
}
