using System.Drawing;
using System.Drawing.Drawing2D;

namespace OpenDisplayNet;

internal static class OpenDisplayBitmapEncoder
{
    private static readonly Color[] MonochromePalette = [Color.Black, Color.White];
    private static readonly Color[] RedPalette = [Color.Black, Color.White, Color.Red];
    private static readonly Color[] YellowPalette = [Color.Black, Color.White, Color.Yellow];
    private static readonly Color[] RedYellowPalette = [Color.Black, Color.White, Color.Yellow, Color.Red];
    private static readonly Color[] SixColorPalette = [Color.Black, Color.White, Color.Green, Color.Blue, Color.Red, Color.Yellow];
    private static readonly Color[] SevenColorPalette = [Color.Black, Color.White, Color.Green, Color.Blue, Color.Red, Color.Yellow, Color.Orange];
    private static readonly byte[] Gray4Codes = [3, 1, 2, 0];
    private static readonly byte[] SixColorCodes = [0, 1, 2, 3, 5, 6];
    private static readonly byte[] Bayer4 =
    [
         0,  8,  2, 10,
        12,  4, 14,  6,
         3, 11,  1,  9,
        15,  7, 13,  5,
    ];

    public static byte[] Encode(Bitmap bitmap, OpenDisplayPanelSize panelSize)
    {
        using Bitmap scaled = Scale(bitmap, panelSize.Width, panelSize.Height);
        return panelSize.ColorScheme switch
        {
            OpenDisplayColorScheme.Monochrome => EncodeMonochrome(scaled),
            OpenDisplayColorScheme.BlackWhiteRed => EncodeThreeColor(scaled, RedPalette, red: true),
            OpenDisplayColorScheme.BlackWhiteYellow => EncodeThreeColor(scaled, YellowPalette, red: false),
            OpenDisplayColorScheme.BlackWhiteRedYellow => EncodePacked(scaled, RedYellowPalette, 2),
            OpenDisplayColorScheme.SixColor => EncodePacked(scaled, SixColorPalette, 4, SixColorCodes),
            OpenDisplayColorScheme.Gray4 => EncodeGray4(scaled),
            OpenDisplayColorScheme.Gray16 => EncodeGray16(scaled),
            OpenDisplayColorScheme.SevenColor => EncodePacked(scaled, SevenColorPalette, 4),
            OpenDisplayColorScheme.SixColorSplit => EncodeSixColorSplit(scaled),
            OpenDisplayColorScheme.Rgb565 => EncodeRgb565(scaled),
            OpenDisplayColorScheme.Rgb888 => EncodeRgb888(scaled),
            OpenDisplayColorScheme.Rgb16BitsPerChannel => EncodeRgb16BitsPerChannel(scaled),
            _ => throw new NotSupportedException($"OpenDisplay color scheme {(byte)panelSize.ColorScheme} is not supported."),
        };
    }

    private static Bitmap Scale(Bitmap source, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Panel dimensions must be positive.");
        }

        if (source.Width == width && source.Height == height)
        {
            return (Bitmap)source.Clone();
        }

        Bitmap scaled = new(width, height);
        using Graphics graphics = Graphics.FromImage(scaled);
        graphics.Clear(Color.White);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, width, height));
        return scaled;
    }

    private static byte[] EncodeMonochrome(Bitmap bitmap)
    {
        int stride = (bitmap.Width + 7) / 8;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (GetPaletteIndex(bitmap.GetPixel(x, y), x, y, MonochromePalette) == 1)
                {
                    output[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        return output;
    }

    private static byte[] EncodeThreeColor(Bitmap bitmap, ReadOnlySpan<Color> palette, bool red)
    {
        int stride = (bitmap.Width + 7) / 8;
        int planeLength = checked(stride * bitmap.Height);
        byte[] output = new byte[planeLength * 2];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int color = GetPaletteIndex(bitmap.GetPixel(x, y), x, y, palette);
                bool blackWhite = color == 1 || (red && color == 2);
                SetBit(output, y * stride + (x / 8), x, blackWhite);
                SetBit(output, planeLength + y * stride + (x / 8), x, color == 2);
            }
        }

        return output;
    }

    private static byte[] EncodeGray4(Bitmap bitmap)
    {
        int stride = (bitmap.Width + 7) / 8;
        int planeLength = checked(stride * bitmap.Height);
        byte[] output = new byte[planeLength * 2];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int level = GetGrayLevel(bitmap.GetPixel(x, y), x, y, 4);
                byte code = Gray4Codes[level];
                SetBit(output, y * stride + (x / 8), x, (code & 1) != 0);
                SetBit(output, planeLength + y * stride + (x / 8), x, (code & 2) != 0);
            }
        }

        return output;
    }

    private static byte[] EncodeGray16(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int stride = (width + 1) / 2;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int level = GetGrayLevel(bitmap.GetPixel(x, y), x, y, 16);
                WriteNibble(output, y * stride + (x / 2), x, (byte)level);
            }
        }

        return output;
    }

    private static byte[] EncodePacked(
        Bitmap bitmap,
        ReadOnlySpan<Color> palette,
        int bitsPerPixel,
        ReadOnlySpan<byte> colorCodes = default)
    {
        int pixelsPerByte = 8 / bitsPerPixel;
        int stride = (bitmap.Width + pixelsPerByte - 1) / pixelsPerByte;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int index = GetPaletteIndex(bitmap.GetPixel(x, y), x, y, palette);
                byte value = colorCodes.IsEmpty ? (byte)index : colorCodes[index];
                int shift = 8 - bitsPerPixel * ((x % pixelsPerByte) + 1);
                output[y * stride + (x / pixelsPerByte)] |= (byte)(value << shift);
            }
        }

        return output;
    }

    private static byte[] EncodeSixColorSplit(Bitmap bitmap)
    {
        int split = bitmap.Width / 2;
        using Bitmap left = bitmap.Clone(new Rectangle(0, 0, split, bitmap.Height), bitmap.PixelFormat);
        using Bitmap right = bitmap.Clone(new Rectangle(split, 0, bitmap.Width - split, bitmap.Height), bitmap.PixelFormat);
        return
        [
            .. EncodePacked(left, SixColorPalette, 4, SixColorCodes),
            .. EncodePacked(right, SixColorPalette, 4, SixColorCodes),
        ];
    }

    private static byte[] EncodeRgb565(Bitmap bitmap)
    {
        byte[] output = new byte[checked(bitmap.Width * bitmap.Height * 2)];
        int offset = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                ushort value = (ushort)(((color.R >> 3) << 11) | ((color.G >> 2) << 5) | (color.B >> 3));
                output[offset++] = (byte)(value >> 8);
                output[offset++] = (byte)value;
            }
        }

        return output;
    }

    private static byte[] EncodeRgb888(Bitmap bitmap)
    {
        byte[] output = new byte[checked(bitmap.Width * bitmap.Height * 3)];
        int offset = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                output[offset++] = color.R;
                output[offset++] = color.G;
                output[offset++] = color.B;
            }
        }

        return output;
    }

    private static byte[] EncodeRgb16BitsPerChannel(Bitmap bitmap)
    {
        byte[] output = new byte[checked(bitmap.Width * bitmap.Height * 6)];
        int offset = 0;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                WriteColorComponent(output, ref offset, color.R);
                WriteColorComponent(output, ref offset, color.G);
                WriteColorComponent(output, ref offset, color.B);
            }
        }

        return output;
    }

    private static void WriteColorComponent(Span<byte> output, ref int offset, byte component)
    {
        output[offset++] = component;
        output[offset++] = component;
    }

    private static int GetPaletteIndex(Color color, int x, int y, ReadOnlySpan<Color> palette)
    {
        Color opaque = color.A == byte.MaxValue
            ? color
            : Color.FromArgb(
                byte.MaxValue,
                (color.R * color.A + byte.MaxValue * (byte.MaxValue - color.A)) / byte.MaxValue,
                (color.G * color.A + byte.MaxValue * (byte.MaxValue - color.A)) / byte.MaxValue,
                (color.B * color.A + byte.MaxValue * (byte.MaxValue - color.A)) / byte.MaxValue);
        for (int index = 0; index < palette.Length; index++)
        {
            if (opaque.ToArgb() == palette[index].ToArgb())
            {
                return index;
            }
        }

        int adjustment = (Bayer4[(y & 3) * 4 + (x & 3)] - 8) * 8;
        int red = Math.Clamp(opaque.R + adjustment, 0, byte.MaxValue);
        int green = Math.Clamp(opaque.G + adjustment, 0, byte.MaxValue);
        int blue = Math.Clamp(opaque.B + adjustment, 0, byte.MaxValue);

        int bestIndex = 0;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < palette.Length; index++)
        {
            int redDelta = red - palette[index].R;
            int greenDelta = green - palette[index].G;
            int blueDelta = blue - palette[index].B;
            int distance = redDelta * redDelta + greenDelta * greenDelta + blueDelta * blueDelta;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static int GetGrayLevel(Color color, int x, int y, int levelCount)
    {
        int luminance = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
        if (luminance is 0 or byte.MaxValue)
        {
            return luminance * (levelCount - 1) / byte.MaxValue;
        }

        int adjustment = (Bayer4[(y & 3) * 4 + (x & 3)] - 8) * 8;
        return Math.Clamp((luminance + adjustment) * (levelCount - 1) / byte.MaxValue, 0, levelCount - 1);
    }

    private static void SetBit(Span<byte> target, int offset, int x, bool value)
    {
        if (value)
        {
            target[offset] |= (byte)(0x80 >> (x % 8));
        }
    }

    private static void WriteNibble(Span<byte> target, int offset, int x, byte value)
    {
        target[offset] |= (byte)(x % 2 == 0 ? value << 4 : value);
    }
}
