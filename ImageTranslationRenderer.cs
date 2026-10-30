using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

static class ImageTranslationRenderer
{
    public static BitmapSource Capture(Int32Rect area)
    {
        byte[] pixels = QrReader.Capture(area.X, area.Y, area.Width, area.Height);
        BitmapSource image = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgr32, null, pixels, area.Width * 4);
        double scale = Math.Min(1, 2400.0 / Math.Max(area.Width, area.Height));
        if (scale < 1) image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        image.Freeze(); return image;
    }

    public static byte[] Png(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    public static BitmapSource Render(BitmapSource original, ImageTranslationResult result)
    {
        if (result.Blocks.All(b => b.PreserveOriginal))
        {
            if (original.IsFrozen) return original;
            var unchanged = original.Clone(); unchanged.Freeze(); return unchanged;
        }
        int width = original.PixelWidth, height = original.PixelHeight;
        var source = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[width * height * 4]; source.CopyPixels(pixels, width * 4, 0);
        result = ImageTranslationColors.Resolve(result, pixels, width, height);
        var placements = ImageTranslationLayout.Plan(result, pixels, width, height);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(original, new Rect(0, 0, width, height));
            foreach (var block in result.Blocks)
            {
                if (block.PreserveOriginal) continue;
                // Erase the old strokes using the line geometry and colors provided
                // by OCR, like the website's SVG. This is not generative inpainting.
                foreach (var line in block.Lines)
                {
                    var background = new SolidColorBrush(line.Background);
                    if (line.Curve.Length == 5)
                    {
                        var path = new StreamGeometry();
                        using (var geometry = path.Open())
                        {
                            var c = line.Curve;
                            double Y(double x) => Math.Clamp(((c[0] * x + c[1]) * x + c[2]) * x + c[3], line.Bounds.Top - 10, line.Bounds.Bottom + 10);
                            geometry.BeginFigure(new(line.Bounds.Left, Y(line.Bounds.Left)), false, false);
                            for (double x = line.Bounds.Left + 2; x < line.Bounds.Right; x += 2) geometry.LineTo(new(x, Y(x)), true, false);
                            geometry.LineTo(new(line.Bounds.Right, Y(line.Bounds.Right)), true, false);
                        }
                        var pen = new Pen(background, Math.Clamp(line.Curve[4] * 1.7, line.Bounds.Height + 4, line.Bounds.Height * 2 + 4))
                        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                        drawing.DrawGeometry(null, pen, path);
                    }
                    else
                    {
                        Rect mask = line.Bounds; mask.Inflate(2, 4);
                        drawing.DrawRectangle(background, null, mask);
                    }
                }
            }
            for (int i = 0; i < result.Blocks.Length; i++)
            {
                if (placements[i] is not { } placement) continue;
                var line = result.Blocks[i].Lines[0];
                Color color = line.Foreground;
                if (Math.Abs(Luma(color) - Luma(line.Background)) < 45) color = Luma(line.Background) < 128 ? Colors.White : Colors.Black;
                drawing.PushClip(new RectangleGeometry(placement.Bounds));
                drawing.PushTransform(new TranslateTransform(placement.Origin.X, placement.Origin.Y));
                drawing.DrawGeometry(new SolidColorBrush(color), null, placement.Lettering);
                drawing.Pop(); drawing.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        if (!result.Blocks.Any(b => b.PreserveOriginal)) { bitmap.Freeze(); return bitmap; }
        // Restore no-change regions as bytes, not another sampled DrawImage.
        // This keeps their fonts, icons and backgrounds exact, even when a
        // neighbouring translated line's mask overlaps their outermost pixels.
        var rendered = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        byte[] output = new byte[pixels.Length]; rendered.CopyPixels(output, width * 4, 0);
        foreach (var line in result.Blocks.Where(b => b.PreserveOriginal).SelectMany(b => b.Lines))
        {
            int left = (int)Math.Clamp(Math.Floor(line.Bounds.Left - 1), 0, width), right = (int)Math.Clamp(Math.Ceiling(line.Bounds.Right + 1), 0, width);
            int top = (int)Math.Clamp(Math.Floor(line.Bounds.Top - 1), 0, height), bottom = (int)Math.Clamp(Math.Ceiling(line.Bounds.Bottom + 1), 0, height);
            if (right <= left || bottom <= top) continue;
            for (int y = top; y < bottom; y++)
            {
                int offset = (y * width + left) * 4;
                Buffer.BlockCopy(pixels, offset, output, offset, (right - left) * 4);
            }
        }
        var kept = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, output, width * 4);
        kept.Freeze(); return kept;
    }
    static double Luma(Color c) => c.R * .2126 + c.G * .7152 + c.B * .0722;
}
