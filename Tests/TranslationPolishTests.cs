using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    static readonly Color TranslationBlue = Color.FromRgb(48, 28, 240);
    static BitmapSource ColoredTranslationFixture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(TranslationBlue), null, new Rect(0, 0, 720, 100));
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 100, 720, 100));
            foreach (var (text, y, color) in new[] { ("Hello world. This is a test.", 24, Brushes.White), ("Open settings to continue.", 124, Brushes.Black) })
                dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), 28, color, 1), new Point(24, y));
        }
        var bitmap = new RenderTargetBitmap(720, 200, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    static string ColoredOcrFixture(BitmapSource image)
    {
        byte[] pixels = LensPixels(image);
        object[] Points(Color color, int top, int bottom) => Enumerable.Range(top, bottom - top).SelectMany(y => Enumerable.Range(24, 600).Select(x => (x, y)))
            .Where(p => { int i = (p.y * 720 + p.x) * 4; return pixels[i] == color.B && pixels[i + 1] == color.G && pixels[i + 2] == color.R; })
            .Take(10).Select(p => (object)new { p.x, p.y }).ToArray();
        return JsonSerializer.Serialize(new { status = "success", data = new { detected_lang = "en", rotate = 0,
            blocks = new[] {
                new { boxes = new[] { new { x = 24, y = 29, w = 600, h = 32, text = "Hello world. This is a test.",
                    backgroundColor = new { r = 100, g = 100, b = 100, Points = Points(TranslationBlue, 20, 24) },
                    textColor = new { r = 200, g = 200, b = 200, Points = Points(Colors.White, 29, 60) } } } },
                new { boxes = new[] { new { x = 24, y = 129, w = 640, h = 32, text = "Open settings to continue.",
                    backgroundColor = new { r = 160, g = 160, b = 160, Points = Points(Colors.White, 120, 124) },
                    textColor = new { r = 80, g = 80, b = 80, Points = Points(Colors.Black, 129, 160) } } } }
            } } });
    }
    static int TranslationPolishTests(string output, bool live)
    {
        try
        {
            Directory.CreateDirectory(output); RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            var original = ColoredTranslationFixture(); byte[] pixels = LensPixels(original);
            File.WriteAllBytes(Path.Combine(output, "original.png"), ImageTranslationRenderer.Png(original));
            var model = YandexImageTranslator.ParseRecognition(ColoredOcrFixture(original), 720, 200);
            Check(model.Blocks.All(b => b.Lines[0].BackgroundPoints.Length == 10 && b.Lines[0].ForegroundPoints.Length == 10), "OCR keeps original-image color sample coordinates instead of throwing them away");
            var resolved = ImageTranslationColors.Resolve(model, pixels, 720, 200);
            Check(resolved.Blocks[0].Lines[0].Background == TranslationBlue && resolved.Blocks[0].Lines[0].Foreground == Colors.White, "blue region recovers its real blue background and white lettering, not grayscale OCR RGB");
            Check(resolved.Blocks[1].Lines[0].Background == Colors.White && resolved.Blocks[1].Lines[0].Foreground == Colors.Black, "different colored sections resolve independently");
            var line = model.Blocks[0].Lines[0];
            var hostile = line with { BackgroundPoints = [new(-1, 20), new(double.NaN, 20), new(999999, 20)], ForegroundPoints = [] };
            var fallback = ImageTranslationColors.Resolve(new("en", [new(line.Bounds, line.Text, [hostile])]), pixels, 720, 200).Blocks[0].Lines[0];
            Check(fallback.Background == line.Background && fallback.Foreground == line.Foreground, "absent or invalid samples use bounded OCR fallbacks without edge clamping");
            var outlier = line with { BackgroundPoints = [new(1, 1), new(2, 1), new(2, 199)] };
            Check(ImageTranslationColors.Resolve(new("en", [new(line.Bounds, line.Text, [outlier])]), pixels, 720, 200).Blocks[0].Lines[0].Background == TranslationBlue,
                "median rejects a contrasting outlier instead of tinting the whole text background");
            var invalidJson = System.Text.RegularExpressions.Regex.Replace(ColoredOcrFixture(original), "\"x\":\\d+", "\"x\":-100");
            Check(YandexImageTranslator.ParseRecognition(invalidJson, 720, 200).Blocks[0].Lines[0].ForegroundPoints.Length == 0,
                "untrusted negative color sample coordinates are rejected while valid geometry is clipped");
            model = YandexImageTranslator.ApplyTranslation(model, ImageTranslationFixture);
            var withoutPoints = model with { Blocks = model.Blocks.Select(b => b with
                { Lines = b.Lines.Select(l => l with { BackgroundPoints = [], ForegroundPoints = [] }).ToArray() }).ToArray() };
            var baseline = ImageTranslationRenderer.Render(original, withoutPoints);
            File.WriteAllBytes(Path.Combine(output, "baseline-gray.png"), ImageTranslationRenderer.Png(baseline));
            var translated = ImageTranslationRenderer.Render(original, model);
            byte[] rendered = LensPixels(translated); int sample = (30 * 720 + 22) * 4;
            Check(LensPixels(baseline).Skip(sample).Take(3).All(c => c == 100), "baseline without color points reproduces the gray mask on a colored source");
            Check(rendered[sample] == TranslationBlue.B && rendered[sample + 1] == TranslationBlue.G && rendered[sample + 2] == TranslationBlue.R,
                "actual erase mask uses the sampled blue rather than leaving gray capsules");
            File.WriteAllBytes(Path.Combine(output, "translated.png"), ImageTranslationRenderer.Png(translated));
            Check(pixels.SequenceEqual(LensPixels(original)), "color sampling and rendering never alter the original snapshot");
            if (live)
            {
                using var service = new YandexImageTranslator(); byte[] png = ImageTranslationRenderer.Png(original);
                model = Task.Run(() => service.TranslateAsync(png, 720, 200)).GetAwaiter().GetResult();
                var actual = ImageTranslationColors.Resolve(model, pixels, 720, 200);
                Check(actual.Blocks.Length >= 2 && actual.Blocks.Any(b => b.Lines.Any(l => l.Background == TranslationBlue)), "live OCR color coordinates restore a synthetic blue background exactly");
                Check(actual.Blocks.All(b => b.Translation.Any(c => c is >= 'А' and <= 'я')), "live generated blue-and-white test picture translates to Russian");
                File.WriteAllBytes(Path.Combine(output, "live-translated.png"), ImageTranslationRenderer.Png(ImageTranslationRenderer.Render(original, model)));
            }
            TranslationSelectionWindowTests(output, original, translated, model);
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, live, syntheticImageOnly = true }));
            Console.WriteLine($"PASS: {_checks} translation polish checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void TranslationSelectionWindowTests(string output, BitmapSource original, BitmapSource translated, ImageTranslationResult model)
    {
        var main = ToolTipWindow(false); main.Show(); Pump(100);
        object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(main);
        var picker = new AreaPicker(new Int32Rect(-10000, -10000, 720, 400));
        try
        {
            picker.Show(); Pump(70);
            Rect reported = Rect.Empty; picker.SelectionChanged += r => reported = r;
            Call(picker, "Outline", new Rect(40, 45, 520, 220)); Pump(30);
            var frame = (SelectionFrame)typeof(AreaPicker).GetField("_frame", Private)!.GetValue(picker)!;
            var badge = (Border)typeof(AreaPicker).GetField("_sizeBadge", Private)!.GetValue(picker)!;
            Check(frame.Visibility == Visibility.Visible && !frame.IsHitTestVisible && !frame.IsProcessing, "drag frame follows selection without receiving input or running an idle animation");
            Check(badge.Visibility == Visibility.Visible && ((TextBlock)badge.Child).Text.Contains('×'), "selection shows a compact physical-pixel size badge");
            Check(reported == new Rect(picker.PointToScreen(new(40, 45)), picker.PointToScreen(new(560, 265))), "selection change coordinates are physical screen pixels, not window-relative DIPs");
            var canvas = (Canvas)picker.Content; canvas.Width = picker.ActualWidth; canvas.Height = picker.ActualHeight;
            canvas.Background = new ImageBrush(original) { Stretch = Stretch.Fill };
            Save(canvas, Path.Combine(output, "selection.png"));
            Call(picker, "Outline", new Rect(-20, -30, 120, 130));
            Check(((RectangleGeometry)typeof(AreaPicker).GetField("_hole", Private)!.GetValue(picker)!).Rect == new Rect(0, 0, 100, 100), "drag outside the virtual desktop is clipped");
            Call(picker, "Outline", new Rect(5, 5, 8, 8));
            Check(badge.Visibility != Visibility.Visible, "tiny accidental selections do not show a misleading size badge");
            Call(picker, "Outline", Rect.Empty);
            Check(frame.Visibility != Visibility.Visible && reported.IsEmpty, "clearing selection also clears its frame and overlap signal");
            picker.Close();

            var bounds = (Rect)Call(main, "MenuScreenBounds", ((Spring)Field("_width")!).Value, ((Spring)Field("_height")!).Value)!;
            Call(main, "PickerSelectionChanged", bounds); Pump(180);
            Check((bool)Field("_pickerObscured")! && main.Opacity <= .13, "island becomes genuinely translucent when selection intersects it");
            Call(main, "PickerSelectionChanged", new Rect(bounds.Right + 100, bounds.Bottom + 100, 30, 30)); Pump(180);
            Check(!(bool)Field("_pickerObscured")! && main.Opacity >= .99, "leaving the island restores its opacity independently of cinema/quiet Root opacity");
            var islandHeight = (Spring)Field("_height")!; double oldHeight = islandHeight.Value;
            Call(main, "PickerSelectionChanged", new Rect(bounds.Left + 5, bounds.Bottom + 10, 25, 20));
            islandHeight.Snap(oldHeight + 80); Call(main, "ApplyShape"); Pump(180);
            Check((bool)Field("_pickerObscured")! && main.Opacity <= .13, "island resizing into a stationary selection also triggers transparency");
            islandHeight.Snap(oldHeight); Call(main, "ApplyShape"); Call(main, "PickerSelectionChanged", Rect.Empty); Pump(180);
            IntPtr hwnd = new WindowInteropHelper(main).Handle;
            bool before = Native.SetClickThrough(hwnd, true);
            Check(Native.SetClickThrough(hwnd, true), "native layered island can pass mouse input through to the picker");
            Native.SetClickThrough(hwnd, before);
            Check(Native.SetClickThrough(hwnd, before) == before, "native input transparency restores the exact previous bit");

            Set(main, "_makeAreaPicker", new Func<AreaPicker>(() => new AreaPicker(new Int32Rect(-10000, -10000, 720, 400))));
            using (var cancel = new CancellationTokenSource())
            {
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                timer.Tick += (_, _) => { timer.Stop(); Call(main, "PickerSelectionChanged", bounds); cancel.Cancel(); };
                timer.Start();
                try { ((Task<Int32Rect?>)Call(main, "PickAreaAsync", "Тест", "Тест", Glyph.Lines, cancel.Token)!).GetAwaiter().GetResult(); }
                finally { timer.Stop(); }
                Pump(180);
                Check(!(bool)Field("_pickingArea")! && !(bool)Field("_pickerObscured")! && main.Opacity >= .99, "cancelling the actual modal picker restores selection state and island opacity");
                Check(Native.SetClickThrough(hwnd, before) == before, "modal picker cancellation restores native input style in finally");
            }
            using (var operation = new CancellationTokenSource())
            {
                Set(main, "_imageTranslationCancellation", operation); Set(main, "_imageTranslationBusy", true);
                var window = (ImageTranslationWindow)Call(main, "ShowTranslationRegion", new Int32Rect(-10000, -10000, 720, 200), original, operation)!;
                Pump(70);
                var toolbar = (Window)typeof(ImageTranslationWindow).GetField("_toolbar", Private)!.GetValue(window)!;
                var surface = (Grid)window.Content;
                var outline = surface.Children.OfType<SelectionFrame>().Single();
                Check(window.IsProcessing && outline.IsProcessing && !toolbar.IsVisible, "selected snapshot and animated outline stay visible while translating, without premature copy controls");
                surface.Width = 720; surface.Height = 200; Save(surface, Path.Combine(output, "processing.png"));
                window.Complete(translated, model); Pump(220);
                Check(!window.IsProcessing && !outline.IsProcessing && toolbar.IsVisible, "completion stops the processing effect and reveals result controls in the same window");
                Check(surface.Children.OfType<Image>().Single(i => i.Name == "TranslationImage").Source == translated, "completed region displays the translated bitmap without reselecting or opening another mode");
                Save(surface, Path.Combine(output, "result.png"));
                Set(main, "_imageTranslationCancellation", null!); Set(main, "_imageTranslationBusy", false);
                window.Close(); Check(!operation.IsCancellationRequested && !toolbar.IsVisible, "closing a completed result does not cancel a finished request and closes its toolbar");
            }
            using (var operation = new CancellationTokenSource())
            {
                Set(main, "_imageTranslationCancellation", operation); Set(main, "_imageTranslationBusy", true);
                var window = (ImageTranslationWindow)Call(main, "ShowTranslationRegion", new Int32Rect(-10000, -10000, 720, 200), original, operation)!;
                window.Close();
                Check(operation.IsCancellationRequested && Field("_imageTranslationWindow") == null, "closing a processing region cancels its current request rather than leaving a hidden upload running");
                Set(main, "_imageTranslationCancellation", null!); Set(main, "_imageTranslationBusy", false);
            }
            using (var operation = new CancellationTokenSource())
            {
                Set(main, "_imageTranslationCancellation", operation); Set(main, "_imageTranslationBusy", true);
                var window = (ImageTranslationWindow)Call(main, "ShowTranslationRegion", new Int32Rect(-10000, -10000, 720, 200), original, operation)!;
                Call(main, "CancelImageTranslation"); Pump(180);
                Check(operation.IsCancellationRequested && !window.IsVisible && !(bool)Field("_imageTranslationBusy")! && main.Opacity >= .99, "island cancel closes the pending region and restores all transient appearance state");
                Set(main, "_imageTranslationCancellation", null!);
            }
        }
        finally { if (picker.IsVisible) picker.Close(); main.Close(); }
    }
}
