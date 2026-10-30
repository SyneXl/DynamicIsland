using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland;

static partial class Program
{
    static FormattedText LayoutText(string text, double size, double width = 1_000_000) => new(text,
        CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, Brushes.Black, 1)
        { Trimming = TextTrimming.None, MaxTextWidth = width };

    static ImageTextBlock LayoutBlock(Rect bounds, string source, string translation, Color? background = null, bool preserve = false) =>
        new(bounds, source, [new(bounds, source, background ?? Colors.White, Colors.Black, [])], translation, preserve);

    static BitmapSource LayoutFixture(ImageTranslationResult model)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 720, 220));
            dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(28, 4, 155, 37), 19, 19);
            dc.DrawRectangle(Brushes.White, null, new Rect(43, 17, 9, 9));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(232, 232, 232)), null, new Rect(198, 4, 392, 37), 19, 19);
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(423, 8, 74, 29), 15, 15);
            foreach (var block in model.Blocks)
            {
                if (block.Bounds.Top > 50) continue;
                var ink = LayoutText(block.Text, block.Bounds.Height == 19 ? 18 : 14).BuildGeometry(new Point());
                dc.PushTransform(new TranslateTransform(block.Bounds.Left - ink.Bounds.Left, block.Bounds.Bottom - ink.Bounds.Bottom));
                dc.DrawGeometry(block.Lines[0].Background == Colors.Black ? Brushes.White : Brushes.Black, null, ink); dc.Pop();
            }
        }
        var bitmap = new RenderTargetBitmap(720, 220, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }

    static int TranslationLayoutTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output); RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var gray = Color.FromRgb(232, 232, 232);
            var model = new ImageTranslationResult("en", [
                LayoutBlock(new(62, 12, 108, 19), "Local Analysis", "Локальный анализ", Colors.Black),
                LayoutBlock(new(212, 16, 81, 10), "Best models", "Лучшие модели", gray),
                LayoutBlock(new(316, 16, 95, 14), "Compatibility", "Совместимость", gray),
                LayoutBlock(new(431, 16, 56, 10), "Devices", "Устройства"),
                LayoutBlock(new(508, 16, 67, 14), "Compare", "Сравнивать", gray)
            ]);
            model = model with { Blocks = model.Blocks.Select((b, i) => i == 0 ? b with { Lines = [b.Lines[0] with { Foreground = Colors.White }] } : b).ToArray() };
            var original = LayoutFixture(model); byte[] pixels = LensPixels(original);
            File.WriteAllBytes(Path.Combine(output, "original-navbar.png"), ImageTranslationRenderer.Png(original));
            var old = new FormattedText("Совместимость", CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 18.9, Brushes.Black, 1) { MaxTextWidth = 95 };
            Check(old.Trimming == TextTrimming.WordEllipsis, "negative control: WPF defaults to ellipsis even without an explicit trimming request");
            Check(LayoutText("Совместимость", 18.9).BuildGeometry(new()).Bounds.Width > old.BuildGeometry(new()).Bounds.Width,
                "negative control: measuring trimmed geometry hides the missing end of a long label");
            var planned = ImageTranslationLayout.Plan(model, pixels, 720, 220);
            Check(planned.All(p => p is not null && p.SingleLine), "short interface labels stay on one line");
            for (int i = 0; i < model.Blocks.Length; i++)
            {
                var p = planned[i]!; Rect ink = p.Lettering.Bounds; ink.Offset(p.Origin.X, p.Origin.Y);
                Check(p.Bounds.Contains(ink), $"complete label {i} fits its safe region without clipping");
                Geometry complete = LayoutText(model.Blocks[i].Translation, p.FontSize).BuildGeometry(new());
                Check(Math.Abs(complete.GetArea() - p.Lettering.GetArea()) < .01 && Math.Abs(complete.Bounds.Width - p.Lettering.Bounds.Width) < .01,
                    $"label {i} contains the geometry of the entire server translation, not an ellipsis");
                Check(p.Bounds.Width < model.Blocks[i].Bounds.Width + 50, $"label {i} does not claim a distant empty part of the screen");
            }
            Check(planned.Skip(1).Max(p => p!.FontSize) - planned.Skip(1).Min(p => p!.FontSize) < .01,
                "neighbouring navigation items use one font size despite OCR descender-height differences");
            Check(planned.Skip(1).All(p => p!.Centered), "navigation captions retain centred placement");
            Check(planned.Skip(1).All(p => p!.FontSize >= 12), "full Russian navigation stays readable instead of shrinking to the emergency minimum");
            Check(planned[0]!.Bounds.Left >= 55 && planned[0]!.Bounds.Right <= 181, "logo translation leaves its icon and capsule edges alone");
            for (int i = 1; i < 4; i++) Check(planned[i]!.Bounds.Right < planned[i + 1]!.Bounds.Left,
                $"adjacent navigation regions {i} and {i + 1} do not overlap");
            var translated = ImageTranslationRenderer.Render(original, model);
            File.WriteAllBytes(Path.Combine(output, "translated-navbar.png"), ImageTranslationRenderer.Png(translated));
            byte[] rendered = LensPixels(translated);
            Check(pixels.SequenceEqual(LensPixels(original)), "layout and rendering do not mutate the original image");
            Check(pixels.Skip((20 * 720 + 43) * 4).Take(9 * 4).SequenceEqual(rendered.Skip((20 * 720 + 43) * 4).Take(9 * 4)), "logo icon remains pixel-exact");
            Check(pixels.Skip(45 * 720 * 4).SequenceEqual(rendered.Skip(45 * 720 * 4)), "navigation changes cannot alter content below its row");

            ImageTextBlock paragraph = new(new(24, 70, 240, 50), "Read the instructions\nThen open settings.",
                [new(new(24, 70, 240, 19), "Read the instructions", Colors.White, Colors.Black, []),
                 new(new(24, 100, 230, 19), "Then open settings.", Colors.White, Colors.Black, [])],
                "Прочитайте инструкцию перед продолжением.\nЗатем откройте настройки приложения.");
            var extras = new ImageTranslationResult("en", [paragraph,
                LayoutBlock(new(310, 73, 60, 12), "Server", "Сервер"),
                LayoutBlock(new(390, 73, 65, 12), "Identifier", new string('Ж', 240)),
                LayoutBlock(new(490, 73, 70, 12), "Status", "Состояние"),
                LayoutBlock(new(20, 150, 2, 10), "Key", new string('Ш', 80)),
                LayoutBlock(new(120, 150, 100, 16), "First", "Первая строка\nВторая строка"),
                LayoutBlock(new(280, 150, 80, 12), "保留", "保留", preserve: true),
                LayoutBlock(new(400, 150, 80, 12), "Blank", "  ")]);
            var additional = ImageTranslationLayout.Plan(extras, pixels, 720, 220);
            Check(!additional[0]!.SingleLine && additional[0]!.FontSize > 7, "paragraphs retain wrapping and explicit line breaks at a readable size");
            Check(additional[0]!.Bounds.Left == 23, "paragraph left edge is preserved instead of consuming empty space to its left");
            Check(additional[1]!.FontSize > 12 && additional[3]!.FontSize > 12, "an exceptionally long identifier does not shrink unrelated short captions");
            Check(!additional[5]!.SingleLine, "explicit newlines never become a single-line UI label");
            Check(additional[6] is null && additional[7] is null, "preserved and empty translations create no lettering");
            for (int i = 0; i < 6; i++)
            {
                var p = additional[i]!; Rect ink = p.Lettering.Bounds; ink.Offset(p.Origin.X, p.Origin.Y);
                Rect tolerance = p.Bounds; tolerance.Inflate(.001, .001);
                Check(tolerance.Contains(ink), $"paragraph/long-word layout {i} keeps every glyph inside its region");
            }
            Check(extras.Blocks[4].Translation.Length == 80, "even an impossibly narrow crop keeps the full copyable translation");
            var kept = model with { Blocks = model.Blocks.Select(b => b with { PreserveOriginal = true }).ToArray() };
            Check(ReferenceEquals(original, ImageTranslationRenderer.Render(original, kept)), "an unchanged translation bypasses layout and preserves the exact original bitmap");
            var mixed = model with { Blocks = model.Blocks.Select((b, i) => i == 3 ? b with { PreserveOriginal = true } : b).ToArray() };
            byte[] mixedPixels = LensPixels(ImageTranslationRenderer.Render(original, mixed));
            bool exact = true;
            for (int y = 15; y < 27; y++) for (int x = 430; x < 488; x++)
            { int n = (y * 720 + x) * 4; exact &= pixels.AsSpan(n, 4).SequenceEqual(mixedPixels.AsSpan(n, 4)); }
            Check(exact, "a preserved neighbour retains its original pixels beside newly fitted labels");
            File.WriteAllBytes(Path.Combine(output, "paragraphs-and-long-words.png"), ImageTranslationRenderer.Png(ImageTranslationRenderer.Render(original, extras)));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, navigationFont = planned[1]!.FontSize }));
            Console.WriteLine($"PASS: {_checks} translation layout checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
