using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZdC.App;

// Recolor the ink in a texture while retaining its highlights and each pixel's original alpha.
public sealed class InkTintExtension : MarkupExtension
{
    public required ImageBrush Source { get; set; }
    public Color Ink { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var source = new FormatConvertedBitmap((BitmapSource)Source.ImageSource, PixelFormats.Bgra32, null, 0);
        int stride = checked(source.PixelWidth * 4);
        var pixels = new byte[checked(stride * source.PixelHeight)];
        source.CopyPixels(pixels, stride, 0);
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = Tint(pixels[i], Ink.B);
            pixels[i + 1] = Tint(pixels[i + 1], Ink.G);
            pixels[i + 2] = Tint(pixels[i + 2], Ink.R);
        }

        var result = BitmapSource.Create(source.PixelWidth, source.PixelHeight, source.DpiX, source.DpiY,
            PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        var brush = Source.Clone();
        brush.ImageSource = result;
        brush.Freeze();
        return brush;
    }

    private static byte Tint(byte value, byte ink) => (byte)(value + ((255 - value) * ink + 127) / 255);
}
