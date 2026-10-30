using System.Globalization;
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
        int width = original.PixelWidth, height = original.PixelHeight;
        var source = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[width * height * 4]; source.CopyPixels(pixels, width * 4, 0);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(original, new Rect(0, 0, width, height));
            foreach (var block in result.Blocks)
            {
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
                if (string.IsNullOrWhiteSpace(block.Translation)) continue;
                Rect bounds = block.Bounds;
                bounds.Inflate(1, 3); bounds.Intersect(new Rect(0, 0, width, height));
                // Use genuinely empty, same-colored space to the right before
                // shrinking longer Russian text. Never cross another text block.
                double edge = width - 3;
                foreach (var other in result.Blocks)
                    if (!ReferenceEquals(other, block) && other.Bounds.Left >= bounds.Right
                        && other.Bounds.Bottom > bounds.Top && other.Bounds.Top < bounds.Bottom)
                        edge = Math.Min(edge, other.Bounds.Left - 5);
                Color bg = block.Lines[0].Background;
                int extension = (int)Math.Ceiling(bounds.Right);
                for (; extension < edge; extension += 2)
                {
                    bool clear = true;
                    foreach (int row in new[] { (int)bounds.Top, (int)(bounds.Top + bounds.Height / 2), Math.Max(0, (int)bounds.Bottom - 1) })
                    {
                        int pixel = (Math.Clamp(row, 0, height - 1) * width + Math.Clamp(extension, 0, width - 1)) * 4;
                        if (Math.Abs(pixels[pixel] - bg.B) + Math.Abs(pixels[pixel + 1] - bg.G) + Math.Abs(pixels[pixel + 2] - bg.R) > 70) { clear = false; break; }
                    }
                    if (!clear) break;
                }
                bounds.Width = Math.Max(bounds.Width, Math.Min(extension - bounds.Left - 2, width - bounds.Left));
                drawing.PushClip(new RectangleGeometry(bounds));
                var color = block.Lines[0].Foreground;
                var backgroundColor = block.Lines[0].Background;
                if (Math.Abs(Luma(color) - Luma(backgroundColor)) < 45) color = Luma(backgroundColor) < 128 ? Colors.White : Colors.Black;
                var typeface = new Typeface("Segoe UI");
                double size = Math.Clamp(block.Lines.Average(l => l.Bounds.Height) * 1.35, 7, 100);
                FormattedText Text(double fontSize) => new(block.Translation, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    typeface, fontSize, new SolidColorBrush(color), 1) { MaxTextWidth = Math.Max(1, bounds.Width), TextAlignment = TextAlignment.Left };
                var text = Text(size);
                Geometry lettering = text.BuildGeometry(new Point());
                // Shrink only as far as necessary. Explicit newlines and wrapped
                // paragraphs survive; separate OCR blocks never share translations.
                while ((lettering.Bounds.Height > bounds.Height + .5 || lettering.Bounds.Width > bounds.Width) && size > 5)
                { size = Math.Max(5, size - .5); text = Text(size); lettering = text.BuildGeometry(new Point()); }
                if (!lettering.Bounds.IsEmpty)
                {
                    drawing.PushTransform(new TranslateTransform(bounds.Left - lettering.Bounds.Left, bounds.Top - lettering.Bounds.Top));
                    drawing.DrawGeometry(new SolidColorBrush(color), null, lettering);
                    drawing.Pop();
                }
                drawing.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    static double Luma(Color c) => c.R * .2126 + c.G * .7152 + c.B * .0722;
}
