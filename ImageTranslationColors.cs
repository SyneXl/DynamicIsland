using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

static class ImageTranslationColors
{
    // OCR's RGB values can describe the grayscale recognition image. Its Points
    // identify pixels on the ORIGINAL color image, just as in the web client.
    internal static ImageTranslationResult Resolve(ImageTranslationResult result, byte[] pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height * 4 != pixels.Length) throw new ArgumentException("Invalid pixel buffer.");
        return result with { Blocks = result.Blocks.Select(block => block with
        {
            Lines = block.Lines.Select(line => line with
            {
                Background = Sample(line.BackgroundPoints, pixels, width, height, line.Background),
                Foreground = Sample(line.ForegroundPoints, pixels, width, height, line.Foreground),
            }).ToArray(),
        }).ToArray() };
    }
    static Color Sample(Point[] points, byte[] pixels, int width, int height, Color fallback)
    {
        var samples = new List<Color>();
        foreach (Point p in points.Take(64))
        {
            if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X >= width || p.Y >= height) continue;
            int offset = ((int)p.Y * width + (int)p.X) * 4;
            if (pixels[offset + 3] == 0) continue;
            samples.Add(Color.FromArgb(pixels[offset + 3], pixels[offset + 2], pixels[offset + 1], pixels[offset]));
        }
        if (samples.Count == 0) return fallback;
        byte Median(Func<Color, byte> channel)
        {
            byte[] values = samples.Select(channel).Order().ToArray(); int mid = values.Length / 2;
            return values.Length % 2 == 1 ? values[mid] : (byte)((values[mid - 1] + values[mid]) / 2);
        }
        return Color.FromArgb(Median(c => c.A), Median(c => c.R), Median(c => c.G), Median(c => c.B));
    }
}
