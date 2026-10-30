using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

sealed record ImageTranslationPlacement(Rect Bounds, Geometry Lettering, Point Origin, double FontSize, bool SingleLine, bool Centered);

// Fit the complete translation, never an already-trimmed display string. Layout
// is planned for the whole image before drawing so adjacent labels can agree on
// typography without touching the service's original text.
static class ImageTranslationLayout
{
    sealed class Candidate(ImageTextBlock block, Rect bounds, bool label, bool enclosed)
    {
        public ImageTextBlock Block = block;
        public Rect Bounds = bounds;
        public bool Label = label, Centered = enclosed;
        public double Size = Math.Clamp(block.Lines.Average(l => l.Bounds.Height) * 1.35, 7, 100);
        public double Baseline;
        public double? RowBaseline;
        public Geometry Lettering = Geometry.Empty;
    }

    public static ImageTranslationPlacement?[] Plan(ImageTranslationResult result, byte[] pixels, int width, int height)
    {
        var candidates = new Candidate?[result.Blocks.Length];
        for (int i = 0; i < candidates.Length; i++)
        {
            var block = result.Blocks[i];
            if (block.PreserveOriginal || block.Lines.Length == 0 || string.IsNullOrWhiteSpace(block.Translation)) continue;
            bool label = block.Lines.Length == 1 && block.Bounds.Height <= 48 && block.Translation.Length <= 80
                && !block.Translation.Contains('\n') && !block.Translation.Contains('\r')
                && block.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 4;
            Rect bounds = Available(block, result.Blocks, pixels, width, height, label, out bool enclosed);
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) continue;
            var candidate = new Candidate(block, bounds, label, enclosed);
            Fit(candidate, candidate.Size);
            candidates[i] = candidate;
        }

        // Only neighbouring, similarly sized short labels share a size. A logo,
        // heading, paragraph or distant column must not shrink an entire row.
        var remaining = candidates.Where(c => c?.Label == true).Cast<Candidate>().OrderBy(c => c.Block.Bounds.Left).ToList();
        while (remaining.Count > 0)
        {
            var row = new List<Candidate> { remaining[0] }; remaining.RemoveAt(0);
            for (int i = 0; i < remaining.Count;)
            {
                if (Neighbours(row[^1], remaining[i])) { row.Add(remaining[i]); remaining.RemoveAt(i); }
                else i++;
            }
            if (row.Count < 2) continue;
            double largest = row.Max(c => c.Size);
            var peers = row.Where(c => c.Size >= largest * .65).ToArray();
            if (peers.Length < 2) continue;
            double common = peers.Min(c => c.Size);
            double[] baselines = peers.Select(c => SourceBaseline(c.Block)).Order().ToArray();
            double baseline = baselines[baselines.Length / 2];
            foreach (var candidate in peers) { candidate.Centered = true; candidate.RowBaseline = baseline; Fit(candidate, common); }
        }

        return candidates.Select(c =>
        {
            if (c is null || c.Lettering.Bounds.IsEmpty) return null;
            Rect ink = c.Lettering.Bounds;
            double x = c.Centered
                ? Math.Clamp(c.Block.Bounds.Left + c.Block.Bounds.Width / 2 - ink.Width / 2, c.Bounds.Left, Math.Max(c.Bounds.Left, c.Bounds.Right - ink.Width))
                : Math.Clamp(c.Block.Bounds.Left - 1, c.Bounds.Left, Math.Max(c.Bounds.Left, c.Bounds.Right - ink.Width));
            double y = c.Label ? c.Bounds.Top + (c.Bounds.Height - ink.Height) / 2 : c.Bounds.Top;
            if (c.RowBaseline is { } baseline) y = Math.Clamp(baseline - c.Baseline + ink.Top, c.Bounds.Top, Math.Max(c.Bounds.Top, c.Bounds.Bottom - ink.Height));
            return new ImageTranslationPlacement(c.Bounds, c.Lettering, new Point(x - ink.Left, y - ink.Top), c.Size, c.Label, c.Centered);
        }).ToArray();
    }

    static bool Neighbours(Candidate a, Candidate b)
    {
        Rect x = a.Block.Bounds, y = b.Block.Bounds;
        double h = Math.Min(x.Height, y.Height), gap = y.Left - x.Right;
        Color ab = a.Block.Lines[0].Background, bb = b.Block.Lines[0].Background;
        return gap >= 0 && gap <= h * 3 && Math.Abs((x.Top + x.Height / 2) - (y.Top + y.Height / 2)) <= Math.Max(3, h * .25)
            // OCR includes descenders in "Compatibility" but not "Devices";
            // their boxes differ despite the original font being the same size.
            && Math.Max(x.Height, y.Height) <= h * 1.65 && Distance(ab, bb) <= 110;
    }

    static double SourceBaseline(ImageTextBlock block)
    {
        var text = new FormattedText(block.Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), 20, Brushes.Black, 1) { Trimming = TextTrimming.None };
        Rect ink = text.BuildGeometry(new Point()).Bounds;
        return ink.IsEmpty ? block.Bounds.Bottom : block.Bounds.Top + (text.Baseline - ink.Top) * block.Bounds.Height / ink.Height;
    }

    static void Fit(Candidate candidate, double maximum)
    {
        double Baseline(double size) => new FormattedText(candidate.Block.Translation, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
            new Typeface("Segoe UI"), size, Brushes.Black, 1).Baseline;
        Geometry Measure(double size)
        {
            var text = new FormattedText(candidate.Block.Translation, CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, Brushes.Black, 1)
            {
                // None is essential: WordEllipsis is WPF's default and its
                // shortened geometry otherwise falsely passes the fit test.
                Trimming = TextTrimming.None,
                MaxTextWidth = candidate.Label ? 1_000_000 : Math.Max(1, candidate.Bounds.Width),
                TextAlignment = TextAlignment.Left
            };
            return text.BuildGeometry(new Point());
        }
        bool Fits(Geometry g) => g.Bounds.IsEmpty || (g.Bounds.Width <= candidate.Bounds.Width && g.Bounds.Height <= candidate.Bounds.Height);
        Geometry lettering = Measure(maximum);
        double size = maximum;
        if (!Fits(lettering))
        {
            double low = .5, high = maximum;
            lettering = Measure(low);
            for (int i = 0; i < 13; i++)
            {
                double middle = (low + high) / 2; Geometry measured = Measure(middle);
                if (Fits(measured)) { low = middle; lettering = measured; } else high = middle;
            }
            size = low;
            // Even an exceptionally narrow crop keeps the complete geometry.
            // Uniform scaling here is a last resort, not clipping or ellipsis.
            if (!lettering.Bounds.IsEmpty && !Fits(lettering))
            {
                double scale = Math.Min(candidate.Bounds.Width / lettering.Bounds.Width, candidate.Bounds.Height / lettering.Bounds.Height);
                lettering.Transform = new ScaleTransform(scale, scale); size *= scale;
            }
        }
        candidate.Lettering = lettering; candidate.Size = size; candidate.Baseline = Baseline(size);
    }

    static Rect Available(ImageTextBlock block, ImageTextBlock[] blocks, byte[] pixels, int width, int height, bool label, out bool enclosed)
    {
        Rect bounds = block.Bounds; bounds.Inflate(1, 3); bounds.Intersect(new Rect(0, 0, width, height));
        enclosed = false;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return bounds;
        double leftLimit = label ? Math.Max(0, bounds.Left - block.Bounds.Height * 2) : bounds.Left;
        double rightLimit = label ? Math.Min(width, bounds.Right + block.Bounds.Height * 2) : width;
        foreach (var other in blocks)
        {
            if (ReferenceEquals(other, block) || other.Bounds.Bottom <= bounds.Top || other.Bounds.Top >= bounds.Bottom) continue;
            // Split whitespace, rather than assigning the same gap to two labels.
            if (other.Bounds.Left >= block.Bounds.Right) rightLimit = Math.Min(rightLimit, (other.Bounds.Left + block.Bounds.Right) / 2 - 2);
            if (other.Bounds.Right <= block.Bounds.Left) leftLimit = Math.Max(leftLimit, (other.Bounds.Right + block.Bounds.Left) / 2 + 2);
        }
        Color background = block.Lines[0].Background;
        bool Clear(int x)
        {
            foreach (double y in new[] { block.Bounds.Top, block.Bounds.Top + block.Bounds.Height / 2, block.Bounds.Bottom - 1 })
            {
                int offset = (Math.Clamp((int)y, 0, height - 1) * width + Math.Clamp(x, 0, width - 1)) * 4;
                if (Math.Abs(pixels[offset] - background.B) + Math.Abs(pixels[offset + 1] - background.G) + Math.Abs(pixels[offset + 2] - background.R) > 70) return false;
            }
            return true;
        }
        int left = (int)Math.Floor(bounds.Left), right = (int)Math.Ceiling(bounds.Right);
        while (left > leftLimit && Clear(left - 1)) left--;
        while (right < rightLimit && Clear(right)) right++;
        enclosed = label && left > leftLimit + 1 && right < rightLimit - 1;
        double padding = label ? Math.Max(3, Math.Min(5, block.Bounds.Height * .25)) : 2;
        double safeLeft = Math.Min(bounds.Left, left + padding), safeRight = Math.Max(bounds.Right, right - padding);
        // A standalone caption keeps its left edge; centred controls can use
        // padding on both sides without crossing borders or neighbouring text.
        if (!label) safeLeft = bounds.Left;
        return new Rect(safeLeft, bounds.Top, safeRight - safeLeft, bounds.Height);
    }

    static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
