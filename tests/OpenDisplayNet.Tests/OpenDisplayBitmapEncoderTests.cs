using System.Drawing;
using OpenDisplayNet;

namespace OpenDisplayNet.Tests;

public sealed class OpenDisplayBitmapEncoderTests
{
    [Fact]
    public void Monochrome_EncodesBlackAndWhitePixels()
    {
        using Bitmap bitmap = CreateBitmap(8, 1, [Color.Black, Color.White, Color.Black, Color.White, Color.Black, Color.White, Color.Black, Color.White]);

        Assert.Equal([0x55], Encode(bitmap, OpenDisplayColorScheme.Monochrome));
    }

    [Fact]
    public void ThreeColor_EncodesBitplanesUsingPanelRules()
    {
        Color[] pixels = [Color.Black, Color.White, Color.Red, Color.Black, Color.White, Color.Red, Color.Black, Color.White];
        using Bitmap red = CreateBitmap(8, 1, pixels);
        using Bitmap yellow = CreateBitmap(8, 1, [Color.Black, Color.White, Color.Yellow, Color.Black, Color.White, Color.Yellow, Color.Black, Color.White]);

        Assert.Equal([0x6D, 0x24], Encode(red, OpenDisplayColorScheme.BlackWhiteRed));
        Assert.Equal([0x49, 0x24], Encode(yellow, OpenDisplayColorScheme.BlackWhiteYellow));
    }

    [Fact]
    public void Gray4_EncodesBlackAndWhiteAcrossBothPlanes()
    {
        using Bitmap black = CreateBitmap(8, 1, Enumerable.Repeat(Color.Black, 8).ToArray());
        using Bitmap white = CreateBitmap(8, 1, Enumerable.Repeat(Color.White, 8).ToArray());

        Assert.Equal([0xFF, 0xFF], Encode(black, OpenDisplayColorScheme.Gray4));
        Assert.Equal([0x00, 0x00], Encode(white, OpenDisplayColorScheme.Gray4));
    }

    [Fact]
    public void SixColorSplit_PacksLeftHalfBeforeRightHalf()
    {
        using Bitmap bitmap = CreateBitmap(
            4,
            2,
            [
                Color.Black, Color.White, Color.Green, Color.Blue,
                Color.Red, Color.Yellow, Color.White, Color.Black,
            ]);

        Assert.Equal([0x01, 0x56, 0x23, 0x10], Encode(bitmap, OpenDisplayColorScheme.SixColorSplit));
    }

    [Fact]
    public void RgbSchemes_UseExpectedChannelPacking()
    {
        using Bitmap bitmap = CreateBitmap(2, 1, [Color.Red, Color.Blue]);

        Assert.Equal([0xF8, 0x00, 0x00, 0x1F], Encode(bitmap, OpenDisplayColorScheme.Rgb565));
        Assert.Equal([0xFF, 0x00, 0x00, 0x00, 0x00, 0xFF], Encode(bitmap, OpenDisplayColorScheme.Rgb888));
        Assert.Equal(
            [0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF],
            Encode(bitmap, OpenDisplayColorScheme.Rgb16BitsPerChannel));
    }

    [Fact]
    public void CropFit_CenterCropsWithoutRescaling()
    {
        using Bitmap bitmap = CreateBitmap(4, 1, [Color.Black, Color.White, Color.Black, Color.White]);

        Assert.Equal(
            [0x80],
            OpenDisplayBitmapEncoder.Encode(
                bitmap,
                new OpenDisplayPanelSize(2, 1, OpenDisplayColorScheme.Monochrome),
                new OpenDisplayImageOptions(OpenDisplayImageFit.Crop, OpenDisplayDithering.None)));
    }

    private static byte[] Encode(Bitmap bitmap, OpenDisplayColorScheme colorScheme)
        => OpenDisplayBitmapEncoder.Encode(
            bitmap,
            new OpenDisplayPanelSize(bitmap.Width, bitmap.Height, colorScheme),
            new OpenDisplayImageOptions(Dithering: OpenDisplayDithering.None));

    private static Bitmap CreateBitmap(int width, int height, IReadOnlyList<Color> pixels)
    {
        Assert.Equal(width * height, pixels.Count);
        Bitmap bitmap = new(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, pixels[y * width + x]);
            }
        }

        return bitmap;
    }
}
