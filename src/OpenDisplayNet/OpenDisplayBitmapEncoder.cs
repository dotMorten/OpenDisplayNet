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

    public static byte[] Encode(Bitmap bitmap, OpenDisplayPanelSize panelSize, OpenDisplayImageOptions options)
    {
        using Bitmap scaled = Scale(bitmap, panelSize.Width, panelSize.Height, options.Fit);
        return panelSize.ColorScheme switch
        {
            OpenDisplayColorScheme.Monochrome => EncodeMonochrome(scaled, options.Dithering),
            OpenDisplayColorScheme.BlackWhiteRed => EncodeThreeColor(scaled, RedPalette, red: true, options.Dithering),
            OpenDisplayColorScheme.BlackWhiteYellow => EncodeThreeColor(scaled, YellowPalette, red: false, options.Dithering),
            OpenDisplayColorScheme.BlackWhiteRedYellow => EncodePacked(scaled, RedYellowPalette, 2, options.Dithering),
            OpenDisplayColorScheme.SixColor => EncodePacked(scaled, SixColorPalette, 4, options.Dithering, SixColorCodes),
            OpenDisplayColorScheme.Gray4 => EncodeGray4(scaled, options.Dithering),
            OpenDisplayColorScheme.Gray16 => EncodeGray16(scaled, options.Dithering),
            OpenDisplayColorScheme.SevenColor => EncodePacked(scaled, SevenColorPalette, 4, options.Dithering),
            OpenDisplayColorScheme.SixColorSplit => EncodeSixColorSplit(scaled, options.Dithering),
            OpenDisplayColorScheme.Rgb565 => EncodeRgb565(scaled),
            OpenDisplayColorScheme.Rgb888 => EncodeRgb888(scaled),
            OpenDisplayColorScheme.Rgb16BitsPerChannel => EncodeRgb16BitsPerChannel(scaled),
            _ => throw new NotSupportedException($"OpenDisplay color scheme {(byte)panelSize.ColorScheme} is not supported."),
        };
    }

    private static Bitmap Scale(Bitmap source, int width, int height, OpenDisplayImageFit fit)
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
        if (fit == OpenDisplayImageFit.Crop)
        {
            int sourceWidth = Math.Min(source.Width, width);
            int sourceHeight = Math.Min(source.Height, height);
            Rectangle sourceRectangle = new(
                (source.Width - sourceWidth) / 2,
                (source.Height - sourceHeight) / 2,
                sourceWidth,
                sourceHeight);
            Rectangle destinationRectangle = new(
                (width - sourceWidth) / 2,
                (height - sourceHeight) / 2,
                sourceWidth,
                sourceHeight);
            graphics.DrawImage(source, destinationRectangle, sourceRectangle, GraphicsUnit.Pixel);
            return scaled;
        }

        Rectangle destination = fit switch
        {
            OpenDisplayImageFit.Stretch => new Rectangle(0, 0, width, height),
            OpenDisplayImageFit.Contain => FitRectangle(source.Size, new Size(width, height), cover: false),
            OpenDisplayImageFit.Cover => FitRectangle(source.Size, new Size(width, height), cover: true),
            _ => throw new ArgumentOutOfRangeException(nameof(fit)),
        };
        graphics.DrawImage(source, destination);
        return scaled;
    }

    private static Rectangle FitRectangle(Size source, Size target, bool cover)
    {
        double scale = cover
            ? Math.Max((double)target.Width / source.Width, (double)target.Height / source.Height)
            : Math.Min((double)target.Width / source.Width, (double)target.Height / source.Height);
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        return new Rectangle((target.Width - width) / 2, (target.Height - height) / 2, width, height);
    }

    private static byte[] EncodeMonochrome(Bitmap bitmap, OpenDisplayDithering dithering)
    {
        int stride = (bitmap.Width + 7) / 8;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (GetPaletteIndex(bitmap.GetPixel(x, y), x, y, MonochromePalette, dithering) == 1)
                {
                    output[y * stride + (x / 8)] |= (byte)(0x80 >> (x % 8));
                }
            }
        }

        return output;
    }

    private static byte[] EncodeThreeColor(Bitmap bitmap, ReadOnlySpan<Color> palette, bool red, OpenDisplayDithering dithering)
    {
        int stride = (bitmap.Width + 7) / 8;
        int planeLength = checked(stride * bitmap.Height);
        byte[] output = new byte[planeLength * 2];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int color = GetPaletteIndex(bitmap.GetPixel(x, y), x, y, palette, dithering);
                bool blackWhite = color == 1 || (red && color == 2);
                SetBit(output, y * stride + (x / 8), x, blackWhite);
                SetBit(output, planeLength + y * stride + (x / 8), x, color == 2);
            }
        }

        return output;
    }

    private static byte[] EncodeGray4(Bitmap bitmap, OpenDisplayDithering dithering)
    {
        int stride = (bitmap.Width + 7) / 8;
        int planeLength = checked(stride * bitmap.Height);
        byte[] output = new byte[planeLength * 2];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int level = GetGrayLevel(bitmap.GetPixel(x, y), x, y, 4, dithering);
                byte code = Gray4Codes[level];
                SetBit(output, y * stride + (x / 8), x, (code & 1) != 0);
                SetBit(output, planeLength + y * stride + (x / 8), x, (code & 2) != 0);
            }
        }

        return output;
    }

    private static byte[] EncodeGray16(Bitmap bitmap, OpenDisplayDithering dithering)
    {
        int width = bitmap.Width;
        int stride = (width + 1) / 2;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int level = GetGrayLevel(bitmap.GetPixel(x, y), x, y, 16, dithering);
                WriteNibble(output, y * stride + (x / 2), x, (byte)level);
            }
        }

        return output;
    }

    private static byte[] EncodePacked(
        Bitmap bitmap,
        ReadOnlySpan<Color> palette,
        int bitsPerPixel,
        OpenDisplayDithering dithering,
        ReadOnlySpan<byte> colorCodes = default)
    {
        int pixelsPerByte = 8 / bitsPerPixel;
        int stride = (bitmap.Width + pixelsPerByte - 1) / pixelsPerByte;
        byte[] output = new byte[checked(stride * bitmap.Height)];
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                int index = GetPaletteIndex(bitmap.GetPixel(x, y), x, y, palette, dithering);
                byte value = colorCodes.IsEmpty ? (byte)index : colorCodes[index];
                int shift = 8 - bitsPerPixel * ((x % pixelsPerByte) + 1);
                output[y * stride + (x / pixelsPerByte)] |= (byte)(value << shift);
            }
        }

        return output;
    }

    private static byte[] EncodeSixColorSplit(Bitmap bitmap, OpenDisplayDithering dithering)
    {
        int split = bitmap.Width / 2;
        using Bitmap left = bitmap.Clone(new Rectangle(0, 0, split, bitmap.Height), bitmap.PixelFormat);
        using Bitmap right = bitmap.Clone(new Rectangle(split, 0, bitmap.Width - split, bitmap.Height), bitmap.PixelFormat);
        return
        [
            .. EncodePacked(left, SixColorPalette, 4, dithering, SixColorCodes),
            .. EncodePacked(right, SixColorPalette, 4, dithering, SixColorCodes),
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

    private static int GetPaletteIndex(Color color, int x, int y, ReadOnlySpan<Color> palette, OpenDisplayDithering dithering)
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

        int adjustment = dithering == OpenDisplayDithering.Ordered
            ? (Bayer4[(y & 3) * 4 + (x & 3)] - 8) * 8
            : 0;
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

    private static int GetGrayLevel(Color color, int x, int y, int levelCount, OpenDisplayDithering dithering)
    {
        int luminance = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
        if (luminance is 0 or byte.MaxValue)
        {
            return luminance * (levelCount - 1) / byte.MaxValue;
        }

        int adjustment = dithering == OpenDisplayDithering.Ordered
            ? (Bayer4[(y & 3) * 4 + (x & 3)] - 8) * 8
            : 0;
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
