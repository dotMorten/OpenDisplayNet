using System.Drawing;

namespace OpenDisplayNet;

/// <summary>Controls how a source bitmap is fitted to an OpenDisplay panel.</summary>
public enum OpenDisplayImageFit
{
    /// <summary>Do not scale the image; center it and fill unused panel space with white.</summary>
    None,

    /// <summary>Resize the image to exactly fill the panel.</summary>
    Fill,

    /// <summary>Preserve the aspect ratio and fill unused panel space with white.</summary>
    Uniform,

    /// <summary>Preserve the aspect ratio while filling and center-cropping to the panel.</summary>
    UniformToFill,
}

/// <summary>Controls palette reduction when converting a bitmap for an OpenDisplay panel.</summary>
public enum OpenDisplayDithering
{
    /// <summary>Choose the nearest panel palette color without dithering.</summary>
    None,

    /// <summary>Use a 4-by-4 ordered dither pattern during palette reduction.</summary>
    Ordered,
}

/// <summary>Controls bitmap conversion before an image is uploaded.</summary>
public sealed record OpenDisplayImageOptions(
    OpenDisplayImageFit Fit = OpenDisplayImageFit.Fill,
    OpenDisplayDithering Dithering = OpenDisplayDithering.Ordered);

/// <summary>Represents either a source bitmap or a caller-encoded OpenDisplay frame.</summary>
public sealed class OpenDisplayImage : IDisposable
{
    private Bitmap? bitmap;
    private readonly byte[]? encodedPixels;

    /// <summary>Creates an image from a bitmap and conversion options.</summary>
    public OpenDisplayImage(Bitmap bitmap, OpenDisplayImageOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        this.bitmap = (Bitmap)bitmap.Clone();
        Options = options ?? new OpenDisplayImageOptions();
    }

    /// <summary>Loads an image file and creates an image with conversion options.</summary>
    public OpenDisplayImage(string imagePath, OpenDisplayImageOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        using Bitmap source = new(imagePath);
        bitmap = (Bitmap)source.Clone();
        Options = options ?? new OpenDisplayImageOptions();
    }

    /// <summary>Creates an image from a caller-encoded OpenDisplay frame.</summary>
    public OpenDisplayImage(ReadOnlyMemory<byte> encodedPixels)
    {
        if (encodedPixels.IsEmpty)
        {
            throw new ArgumentException("An encoded OpenDisplay frame must contain at least one byte.", nameof(encodedPixels));
        }

        this.encodedPixels = encodedPixels.ToArray();
        Options = new OpenDisplayImageOptions();
    }

    /// <summary>Gets the conversion options used when this image was created from a bitmap.</summary>
    public OpenDisplayImageOptions Options { get; }

    internal bool IsEncoded => encodedPixels is not null;

    internal ReadOnlyMemory<byte> EncodedPixels => encodedPixels ?? [];

    internal Bitmap Bitmap => bitmap ?? throw new ObjectDisposedException(nameof(OpenDisplayImage));

    /// <inheritdoc />
    public void Dispose()
    {
        bitmap?.Dispose();
        bitmap = null;
    }
}
