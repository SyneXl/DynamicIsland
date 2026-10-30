using System.Globalization;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland;

static partial class Program
{
    static byte[] LensFloat(int field, float value)
    {
        byte[] result = new byte[5]; result[0] = (byte)((field << 3) | 5);
        BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(1), value); return result;
    }
    static byte[] LensGeometry(float x = .5f, float y = .25f, float w = .9f, float h = .2f, ulong units = 1) => LensProto.Bytes(1,
        LensProto.Pack(LensFloat(1, x), LensFloat(2, y), LensFloat(3, w), LensFloat(4, h), LensProto.Integer(6, units)));
    static byte[] LensResponse(string language = "en", string text = "Привет, мир. Это тест.", string paragraphId = "paragraph:1", string translationId = "paragraph:1", ulong status = 1,
        byte[]? geometry = null, string target = "ru", string originalText = "Hello world.")
    {
        geometry ??= LensGeometry();
        var parts = originalText.Split(' ');
        var words = LensProto.Pack(parts.Select((part, i) => LensProto.Bytes(1,
            LensProto.Pack(LensProto.String(2, part), LensProto.String(3, i + 1 < parts.Length ? " " : "")))).ToArray());
        var line = LensProto.Pack(words, LensProto.Bytes(2, geometry));
        var paragraph = LensProto.Pack(LensProto.Bytes(2, line), LensProto.Bytes(3, geometry), LensProto.String(6, paragraphId));
        var ocr = LensProto.Pack(LensProto.Bytes(1, LensProto.Bytes(1, paragraph)), LensProto.String(2, language));
        var style = LensProto.Pack(LensProto.Integer(1, 0xffffffff), LensProto.Integer(2, 0xff252525));
        var translation = LensProto.Pack(LensProto.Bytes(1, LensProto.Integer(1, status)), LensProto.String(2, target),
            LensProto.String(3, language), LensProto.String(4, text), LensProto.Bytes(5, LensProto.Bytes(3, style)));
        var gleam = LensProto.Pack(LensProto.Bytes(10, translation), LensProto.String(11, translationId));
        return LensProto.Bytes(2, LensProto.Pack(LensProto.Bytes(3, ocr), LensProto.Bytes(4, gleam)));
    }
    static HttpResponseMessage LensHttpResponse(byte[]? body = null, HttpStatusCode status = HttpStatusCode.OK, string mime = "application/x-protobuf")
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? LensResponse()) };
        response.Content.Headers.ContentType = new(mime); return response;
    }
    static byte[] LensMixedResponse(params byte[][] responses)
    {
        var objects = responses.Select(r => new LensProto(r).Message(2)).ToArray();
        var paragraphs = LensProto.Pack(objects.Select(o => LensProto.Bytes(1, o.Message(3).Message(1).Data(1).ToArray())).ToArray());
        var text = LensProto.Pack(LensProto.Bytes(1, paragraphs), LensProto.String(2, "en"));
        var gleams = LensProto.Pack(objects.Reverse().Select(o => LensProto.Bytes(4, o.Data(4).ToArray())).ToArray());
        return LensProto.Bytes(2, LensProto.Pack(LensProto.Bytes(3, text), gleams));
    }
    static byte[] LensPixels(BitmapSource image)
    {
        var source = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] pixels = new byte[source.PixelWidth * source.PixelHeight * 4]; source.CopyPixels(pixels, source.PixelWidth * 4, 0); return pixels;
    }
    static bool LensRejected(byte[] response)
    {
        try { GoogleImageTranslator.ParseResponse(response, 720, 180); return false; }
        catch (Exception e) when (e is ImageTranslationException or FormatException or OverflowException or System.Text.DecoderFallbackException) { return true; }
    }
    static int GoogleImageTranslationTests(string output, bool live)
    {
        try
        {
            Directory.CreateDirectory(output);
            var original = ImageFixture(); byte[] png = ImageTranslationRenderer.Png(original);
            File.WriteAllBytes(Path.Combine(output, "original.png"), png);
            var model = GoogleImageTranslator.ParseResponse(LensResponse(), 720, 180);
            Check(model.Blocks.Length == 1 && model.Blocks[0].Text == "Hello world." && model.Text == "Привет, мир. Это тест.", "Lens binds translated paragraphs to OCR IDs and respects word separators");
            var bounds = model.Blocks[0].Bounds;
            Check(Math.Abs(bounds.Left - 36) < .01 && Math.Abs(bounds.Width - 648) < .01, "normalized center geometry is converted to physical image coordinates");
            Check(model.Blocks[0].Lines[0].Background == Color.FromRgb(37, 37, 37) && model.Blocks[0].Lines[0].Foreground == Colors.White, "Lens ARGB text and background colors reach the renderer");
            var clipped = GoogleImageTranslator.ParseResponse(LensResponse(geometry: LensGeometry(0, 0, 2, 2)), 720, 180);
            Check(clipped.Blocks[0].Bounds == new Rect(0, 0, 720, 180), "off-image Lens rectangles are clipped");
            Check(GoogleImageTranslator.ParseResponse(LensResponse(geometry: LensGeometry(350, 50, 600, 30, 2)), 720, 180).Blocks[0].Bounds == new Rect(50, 35, 600, 30), "pixel-coordinate geometry is not scaled twice");
            Check(GoogleImageTranslator.ParseResponse(LensResponse(language: "ru", status: 4), 720, 180).Language == "ru", "Russian source is preserved without unnecessary repainting");
            Check(GoogleImageTranslator.ParseResponse(LensResponse(paragraphId: ""), 720, 180).Text.Contains("Привет"), "single-paragraph legacy response can omit its OCR ID");
            foreach (string unchanged in new[] { "149", "Qwen3.8 27B", "↓" })
            {
                var noOp = GoogleImageTranslator.ParseResponse(LensResponse(status: 9, text: "", originalText: unchanged), 720, 180);
                Check(noOp.Text == unchanged && noOp.Blocks[0].PreserveOriginal, "Lens NO_OP_TRANSLATION is a valid unchanged number, name or symbol, not an incomplete translation");
            }
            foreach (string literal in new[] { "147", "35", "−12,5%", "1 024", "١٢٣", "１２３", "↓", "🔍", "𑃰𑃱" })
            foreach (ulong status in new ulong[] { 0, 1, 3, 4, 5, 8, 9 })
            {
                var numeric = GoogleImageTranslator.ParseResponse(LensResponse(language: "und", status: status, text: "", originalText: literal), 720, 180);
                Check(numeric.Blocks[0].PreserveOriginal && numeric.Text == literal,
                    "numbers, Unicode digits, punctuation and icons do not require a detected source language");
            }
            var unidentifiedName = GoogleImageTranslator.ParseResponse(LensResponse(language: "und", status: 5, text: "", originalText: "RTX 3070 Ti"), 720, 180);
            Check(unidentifiedName.Blocks[0].PreserveOriginal && unidentifiedName.Text == "RTX 3070 Ti", "unknown language in a name is preserved rather than invalidating the image");
            var numericMixed = GoogleImageTranslator.ParseResponse(LensMixedResponse(LensResponse(paragraphId: "heading", translationId: "heading"),
                LensResponse(language: "und", status: 5, text: "", originalText: "147", paragraphId: "value", translationId: "value")), 720, 180);
            Check(numericMixed.Blocks.Length == 2 && numericMixed.Blocks[0].Translation.StartsWith("Привет") && numericMixed.Blocks[1].Translation == "147"
                && numericMixed.Blocks[1].PreserveOriginal, "unknown-language graph values do not cancel valid paragraph translations");
            foreach (ulong status in new ulong[] { 2, 6, 7, 99 })
                Check(LensRejected(LensResponse(language: "und", status: status, text: "", originalText: "147")), "operational errors and unknown status codes are not hidden by numeric preservation");
            foreach (string text in new[] { "Hello", "日本語", "مرحبا", "𐐀𐐨" })
                Check(LensRejected(LensResponse(status: 8, text: "", originalText: text)), "Unicode letters are never classified as numeric symbols to hide an empty translation");
            foreach (ulong unchangedStatus in new ulong[] { 4, 9 })
            {
                var noOp = GoogleImageTranslator.ParseResponse(LensResponse(status: unchangedStatus, text: ""), 720, 180);
                Check(LensPixels(ImageTranslationRenderer.Render(original, noOp)).SequenceEqual(LensPixels(original)), "same-language and no-op blocks retain original pixels, not re-created lettering");
            }
            var mixed = GoogleImageTranslator.ParseResponse(LensMixedResponse(LensResponse(paragraphId: "translated", translationId: "translated"),
                LensResponse(status: 9, text: "", paragraphId: "name", translationId: "name", geometry: LensGeometry(y: .575f))), 720, 180);
            Check(mixed.Blocks.Length == 2 && !mixed.Blocks[0].PreserveOriginal && mixed.Blocks[1].PreserveOriginal && mixed.Blocks[0].Translation.StartsWith("Привет"), "mixed translated and no-op paragraphs succeed even when translation records are out of order");
            Check(mixed.Text.Contains("Hello world."), "copyable translation retains unchanged model names and numbers in reading order");
            byte[] mixedPixels = LensPixels(ImageTranslationRenderer.Render(original, mixed)), originalPixels = LensPixels(original);
            Check(!mixedPixels.SequenceEqual(originalPixels) && mixedPixels.Skip(75 * 720 * 4).SequenceEqual(originalPixels.Skip(75 * 720 * 4)), "mixed result translates the first line while preserving the entire unchanged second line");
            foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[] { 0 }, new byte[] { 8, 128 }, new byte[] { 18, 99 },
                new byte[] { 15 }, new byte[] { 8, 255, 255, 255, 255, 255, 255, 255, 255, 255, 2 },
                LensProto.Bytes(1, LensProto.Integer(1, 1)), LensProto.Bytes(2, []), LensResponse(status: 2),
                LensResponse(text: ""), LensResponse(status: 8, text: ""), LensResponse(status: 0), LensResponse(translationId: "different-id"), LensResponse(target: "fr"),
                LensResponse(geometry: LensGeometry(w: float.NaN)), LensResponse(geometry: LensGeometry(units: 99)) })
                Check(LensRejected(invalid), "malformed, missing, mismatched or incomplete Lens response is rejected");
            var extension = LensProto.Pack(LensResponse(), LensProto.Integer(100, 42));
            Check(GoogleImageTranslator.ParseResponse(extension, 720, 180).Text == model.Text, "unknown protobuf fields are forward-compatible");
            Check(LensRejected(LensMixedResponse(LensResponse(), LensResponse(status: 2, paragraphId: "failed", translationId: "failed"))), "real server errors are not silently treated as no-change blocks");
            Check(LensRejected(LensMixedResponse(LensResponse(), LensResponse(status: 9, paragraphId: "unmatched", translationId: "other"))), "no-op support never relaxes paragraph ID matching");
            byte[] nested = []; for (int i = 0; i < 26; i++) nested = LensProto.Bytes(1, nested);
            bool depthRejected = false; try { var parsed = new LensProto(nested); for (int i = 0; i < 26; i++) parsed = parsed.Message(1); } catch (FormatException) { depthRejected = true; }
            Check(depthRejected, "protobuf recursion has a hard depth limit");
            Task.Run(() => GoogleImageNetworkTests(png)).GetAwaiter().GetResult();
            if (live)
            {
                using var translator = new GoogleImageTranslator(); var timer = System.Diagnostics.Stopwatch.StartNew();
                model = Task.Run(() => translator.TranslateAsync(png, 720, 180)).GetAwaiter().GetResult();
                Check(model.Language != "ru" && model.Blocks.Length >= 2 && model.Blocks.All(b => b.Translation.Any(c => c is >= 'А' and <= 'я')), "live Google Lens translates only a generated English test image into Russian");
                File.WriteAllText(Path.Combine(output, "live-result.json"), JsonSerializer.Serialize(new { passed = true, milliseconds = timer.ElapsedMilliseconds,
                    model.Language, text = model.Text, blocks = model.Blocks.Length, syntheticImageOnly = true, browserCookies = false }));
            }
            var translated = ImageTranslationRenderer.Render(original, model);
            File.WriteAllBytes(Path.Combine(output, "translated.png"), ImageTranslationRenderer.Png(translated));
            Check(translated.IsFrozen && translated.PixelWidth == 720 && translated.PixelHeight == 180, "Google uses the existing region overlay renderer at exact capture size");
            GoogleImageSettingsTests(output);
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, live, userScreenshotsUploaded = false }));
            Console.WriteLine($"PASS: {_checks} Google translation checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static int GoogleImageTranslationReplay(string imagePath, string responsePath, string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(imagePath)); image.EndInit(); image.Freeze();
            var model = GoogleImageTranslator.ParseResponse(File.ReadAllBytes(responsePath), image.PixelWidth, image.PixelHeight);
            var layouts = ImageTranslationLayout.Plan(model, LensPixels(image), image.PixelWidth, image.PixelHeight);
            File.WriteAllText(Path.Combine(output, "layout.json"), JsonSerializer.Serialize(model.Blocks.Select((b, i) => new
                { b.Text, b.Translation, sourceBounds = b.Bounds.ToString(CultureInfo.InvariantCulture),
                    fontSize = layouts[i]?.FontSize, bounds = layouts[i]?.Bounds.ToString(CultureInfo.InvariantCulture), layouts[i]?.Centered,
                    ink = layouts[i]?.Lettering.Bounds.ToString(CultureInfo.InvariantCulture), origin = layouts[i]?.Origin.ToString(CultureInfo.InvariantCulture) })));
            File.WriteAllBytes(Path.Combine(output, "translated.png"), ImageTranslationRenderer.Png(ImageTranslationRenderer.Render(image, model)));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, blocks = model.Blocks.Length,
                russianTranslations = model.Blocks.Count(b => !b.PreserveOriginal && b.Translation.Any(c => c is >= 'А' and <= 'я')),
                preservedBlocks = model.Blocks.Count(b => b.PreserveOriginal) }));
            Console.WriteLine($"PASS: captured Lens response replay; blocks={model.Blocks.Length}; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static async Task GoogleImageNetworkTests(byte[] png)
    {
        ulong previousId = 0;
        using (var handler = new ImageHttp((request, n) =>
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == GoogleImageTranslator.Endpoint, "Google uses one fixed HTTPS protobuf endpoint, not search HTML");
            Check(!request.Headers.Contains("Cookie") && !request.Headers.Contains("Authorization") && request.Headers.Contains("X-Goog-Api-Key"), "Lens request has no browser or user account credentials");
            Check(request.Content!.Headers.ContentType!.MediaType == "application/x-protobuf", "Lens payload has the correct MIME type");
            var root = new LensProto(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            var objects = root.Message(1); var context = objects.Message(1); var image = objects.Message(3);
            Check(root.Number(6) == 1 && image.Message(3).Number(1) == 720 && image.Message(3).Number(2) == 180, "request carries image dimensions and Lens intent");
            Check(image.Message(1).Data(1).Span.SequenceEqual(png), "only the selected image is placed in the binary payload");
            var translate = context.Message(4).Message(17).Message(1);
            Check(translate.Number(1) == 2 && translate.Message(3).Text(1) == "ru" && translate.Message(3).Text(2) == "auto", "Lens requests automatic source detection and Russian translation");
            ulong id = context.Message(3).Number(1);
            Check(id != 0 && id != previousId, "each manual translation has a fresh request identity"); previousId = id;
            return LensHttpResponse();
        }))
        using (var service = new GoogleImageTranslator(handler))
        {
            Check(handler.Count == 0, "Google service initialization performs no network activity");
            var progress = new ImageProgress(); await service.TranslateAsync(png, 720, 180, progress); await service.TranslateAsync(png, 720, 180);
            Check(handler.Count == 2 && progress.Messages.SequenceEqual(["Перевожу на русский…"]), "one request per manual translation with a concise progress label");
        }
        foreach (HttpStatusCode code in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.Forbidden })
        {
            var now = DateTimeOffset.UtcNow;
            using var handler = new ImageHttp((_, n) => { var r = LensHttpResponse(status: n == 1 ? code : HttpStatusCode.OK); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(90)); return r; });
            using var service = new GoogleImageTranslator(handler, () => now);
            for (int i = 0; i < 2; i++)
            {
                bool rejected = false; try { await service.TranslateAsync(png, 720, 180); } catch (ImageTranslationException e) { rejected = e.Message.Contains("позже"); }
                Check(rejected, "rate limit or challenge gives a short error, without solving challenges");
            }
            Check(handler.Count == 1, "cooldown prevents repeated uploads and respects Retry-After");
            now += TimeSpan.FromSeconds(91); await service.TranslateAsync(png, 720, 180);
            Check(handler.Count == 2, "manual retry becomes available after the server's cooldown");
        }
        foreach (var (code, mime, bytes) in new[] { (HttpStatusCode.Redirect, "text/html", new byte[0]), (HttpStatusCode.OK, "text/html", "<html>challenge</html>"u8.ToArray()),
            (HttpStatusCode.OK, "application/x-protobuf", new byte[] { 18, 255 }), (HttpStatusCode.BadGateway, "application/x-protobuf", new byte[0]) })
        {
            using var handler = new ImageHttp((_, _) => LensHttpResponse(bytes, code, mime)); using var service = new GoogleImageTranslator(handler);
            bool rejected = false; try { await service.TranslateAsync(png, 720, 180); } catch (ImageTranslationException) { rejected = true; }
            Check(rejected && handler.Count == 1, "redirect, HTML, bad protobuf or server error stops after one request");
        }
        using (var handler = new ImageHttp((_, _) => { var r = LensHttpResponse(); r.Content.Headers.ContentLength = 12_000_001; return r; }))
        using (var service = new GoogleImageTranslator(handler))
        {
            bool rejected = false; try { await service.TranslateAsync(png, 720, 180); } catch (ImageTranslationException) { rejected = true; }
            Check(rejected, "oversize declared response is rejected before reading");
        }
        using (var cancel = new CancellationTokenSource())
        using (var handler = new ImageHttp((_, _) => throw new Exception("Cancelled upload")))
        using (var service = new GoogleImageTranslator(handler))
        {
            cancel.Cancel(); bool cancelled = false;
            try { await service.TranslateAsync(png, 720, 180, cancellation: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.Count == 0, "cancellation before sending prevents all Google traffic");
        }
    }
    static void GoogleImageSettingsTests(string output)
    {
        var window = ToolTipWindow(false);
        void Capture(string file)
        {
            var root = (FrameworkElement)window.Content;
            Call(window, "UpdateTargets");
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape");
            foreach (UIElement element in ToolTipVisuals(root).OfType<UIElement>()) { element.CacheMode = null; element.InvalidateVisual(); }
            root.UpdateLayout(); Pump(100); Save(root, Path.Combine(output, file));
        }
        try
        {
            int writes = 0;
            Set(window, "_saveImageTranslationProvider", new Func<ImageTranslationProvider, bool>(_ => { writes++; return true; }));
            Set(window, "_imageTranslationProvider", ImageTranslationProvider.Yandex);
            window.Show(); Pump(100);
            Call(window, "ImageTranslationRow_Click", window, new RoutedEventArgs()); Pump(350);
            Check(Call(window, "TargetView")!.ToString() == "ImageTranslation" && !(bool)typeof(MainWindow).GetField("_pickingArea", Private)!.GetValue(window)!, "menu entry opens translation settings without opening the screen picker");
            var view = (FrameworkElement)window.FindName("ImageTranslationView");
            var providers = (FrameworkElement)window.FindName("ImageTranslationProviders");
            var status = (TextBlock)window.FindName("ImageTranslationStatus");
            var action = (Button)window.FindName("ImageTranslationAction");
            var google = (Button)window.FindName("ImageTranslationGoogle"); var yandex = (Button)window.FindName("ImageTranslationYandex");
            Check(providers.Visibility == Visibility.Visible && status.Visibility == Visibility.Collapsed && (string)action.Content == "Выделить область" && view.Height < 180, "idle settings have two providers, one action and no empty status block");
            Check(typeof(MainWindow).GetProperty("SelectedImageTranslator", Private)!.GetValue(window) is YandexImageTranslator, "existing provider remains the default");
            view.BeginAnimation(UIElement.OpacityProperty, null); view.Opacity = 1; view.Visibility = Visibility.Visible;
            Capture("settings-yandex.png");
            Call(window, "ImageTranslationProvider_Click", google, new RoutedEventArgs());
            Check(writes == 1 && typeof(MainWindow).GetProperty("SelectedImageTranslator", Private)!.GetValue(window) is GoogleImageTranslator, "Google selection is persisted and routes new captures to Google");
            Check(google.Foreground == Brushes.White && yandex.Foreground != Brushes.White
                && ToolTipVisuals(google).OfType<Border>().All(b => b.BorderThickness == new Thickness(0)), "selected provider has a restrained borderless highlight");
            Check(google.FontFamily.Equals(window.FontFamily) && yandex.FontFamily.Equals(window.FontFamily), "provider selectors inherit the island font");
            Capture("settings-google.png");
            Call(window, "ImageTranslationRow_Click", window, new RoutedEventArgs());
            Check(typeof(MainWindow).GetProperty("SelectedImageTranslator", Private)!.GetValue(window) is GoogleImageTranslator && writes == 1, "reopening settings does not reset or rewrite the provider");
            Set(window, "_saveImageTranslationProvider", new Func<ImageTranslationProvider, bool>(_ => false));
            Call(window, "ImageTranslationProvider_Click", yandex, new RoutedEventArgs());
            Check(typeof(MainWindow).GetProperty("SelectedImageTranslator", Private)!.GetValue(window) is GoogleImageTranslator && status.Visibility == Visibility.Visible, "failed settings write does not silently change provider");
            Call(window, "SetImageTranslationStatus", "Перевожу на русский…");
            Set(window, "_imageTranslationBusy", true); Call(window, "RefreshImageTranslationSettings");
            Call(window, "ImageTranslationProvider_Click", yandex, new RoutedEventArgs());
            Check(typeof(MainWindow).GetProperty("SelectedImageTranslator", Private)!.GetValue(window) is GoogleImageTranslator && providers.Visibility == Visibility.Collapsed && view.Height <= 140 && (string)action.Content == "Отмена", "in-flight translation keeps its provider and compact cancel UI");
            Capture("loading.png");
            Call(window, "CancelImageTranslation");
            Check(providers.Visibility == Visibility.Visible && google.IsEnabled && (string)action.Content == "Выделить область", "cancellation restores provider controls and selection button");
        }
        finally { window.Close(); }
    }
}
