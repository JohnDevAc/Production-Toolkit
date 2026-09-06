using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ToolkitLauncher;

/// <summary>A shared set of surfaces and controls, tinted by the icon's dominant chromatic hue.</summary>
public sealed record IconTheme(SolidColorBrush Primary, SolidColorBrush Background,
    SolidColorBrush Panel, SolidColorBrush Border, SolidColorBrush IconWell)
{
    public static IconTheme FromIcon(BitmapSource source)
    {
        // Limit sampling work even if a future project supplies an unusually large image.
        var scale = Math.Min(1, 128d / Math.Max(source.PixelWidth, source.PixelHeight));
        BitmapSource small = scale < 1 ? new TransformedBitmap(source, new ScaleTransform(scale, scale)) : source;
        var bitmap = new FormatConvertedBitmap(small, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        var weights = new double[24];
        var red = new double[24]; var green = new double[24]; var blue = new double[24];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var r = pixels[i + 2] / 255d; var g = pixels[i + 1] / 255d; var b = pixels[i] / 255d;
            var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;
            // Ignore transparency, greys and dark backing plates in favour of the coloured mark.
            if (pixels[i + 3] < 160 || max < .22 || delta < .12) continue;
            var hue = max == r ? ((g - b) / delta + 6) % 6 : max == g ? (b - r) / delta + 2 : (r - g) / delta + 4;
            var bin = (int)Math.Round(hue * 4) % weights.Length;
            var weight = delta * max * max * pixels[i + 3] / 255;
            weights[bin] += weight; red[bin] += r * weight; green[bin] += g * weight; blue[bin] += b * weight;
        }
        var best = Enumerable.Range(0, weights.Length).MaxBy(i => weights[i]);
        var accent = weights[best] > 0
            ? Color.FromRgb((byte)Math.Round(red[best] / weights[best] * 255),
                (byte)Math.Round(green[best] / weights[best] * 255), (byte)Math.Round(blue[best] / weights[best] * 255))
            : Color.FromRgb(67, 91, 109);
        var primary = accent;
        while (Contrast(primary, Colors.White) < 7) primary = Mix(primary, Colors.Black, .06);
        return new(Brush(primary), Brush(Mix(accent, Colors.White, .965)),
            Brush(Mix(accent, Colors.White, .92)), Brush(Mix(accent, Colors.White, .67)),
            Brush(Mix(accent, Colors.White, .86)));
    }

    public static double Contrast(Color first, Color second)
    {
        static double Luminance(Color c)
        {
            static double Linear(byte value) { var x = value / 255d; return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4); }
            return .2126 * Linear(c.R) + .7152 * Linear(c.G) + .0722 * Linear(c.B);
        }
        var a = Luminance(first); var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }

    private static Color Mix(Color a, Color b, double amount) => Color.FromRgb(
        (byte)Math.Round(a.R * (1 - amount) + b.R * amount),
        (byte)Math.Round(a.G * (1 - amount) + b.G * amount),
        (byte)Math.Round(a.B * (1 - amount) + b.B * amount));
    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
}
