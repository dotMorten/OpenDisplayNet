namespace OpenDisplayNet;

/// <summary>Describes the pixel color and plane scheme used by an OpenDisplay panel.</summary>
public enum OpenDisplayColorScheme : byte
{
    /// <summary>One-bit black and white.</summary>
    Monochrome = 0,

    /// <summary>Black, white, and red.</summary>
    BlackWhiteRed = 1,

    /// <summary>Black, white, and yellow.</summary>
    BlackWhiteYellow = 2,

    /// <summary>Black, white, red, and yellow.</summary>
    BlackWhiteRedYellow = 3,

    /// <summary>Spectra six-color: black, white, green, blue, red, and yellow.</summary>
    SixColor = 4,

    /// <summary>Two-bit, four-level grayscale.</summary>
    Gray4 = 5,

    /// <summary>Four-bit, sixteen-level grayscale.</summary>
    Gray16 = 6,

    /// <summary>Spectra or ACeP seven-color.</summary>
    SevenColor = 7,

    /// <summary>Spectra six-color with separate left and right planes for dual-chip-select panels.</summary>
    SixColorSplit = 8,

    /// <summary>RGB with 5-6-5 bits per pixel.</summary>
    Rgb565 = 100,

    /// <summary>RGB with 8 bits per channel.</summary>
    Rgb888 = 101,

    /// <summary>RGB with 16 bits per channel.</summary>
    Rgb16BitsPerChannel = 102,
}
