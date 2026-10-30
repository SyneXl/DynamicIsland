using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland;

static partial class Program
{
    const string ImageOcrFixture = """
    {"status":"success","data":{"detected_lang":"en","rotate":0,"blocks":[
      {"boxes":[{"x":24,"y":29,"w":600,"h":32,"text":"Hello world. This is a test.",
        "backgroundColor":{"r":37,"g":37,"b":37},"textColor":{"r":255,"g":255,"b":255},
        "polyCoefs":{"a3":0,"a2":0,"a1":0,"a0":44,"hh":34,"fromXtoY":true}}]},
      {"boxes":[{"x":24,"y":86,"w":640,"h":32,"text":"Open settings to continue.",
        "backgroundColor":{"r":37,"g":37,"b":37},"textColor":{"r":255,"g":255,"b":255}}]}
    ]}}
    """;
    const string ImageTranslationFixture = """
    {"code":200,"text":["Привет, мир. Это тест.","Откройте настройки, чтобы продолжить."]}
    """;

    sealed class ImageHttp(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        { cancel.ThrowIfCancellationRequested(); return Task.FromResult(respond(request, ++Count)); }
    }
    sealed class ImageProgress : IProgress<string>
    {
        public readonly List<string> Messages = [];
        public void Report(string value) => Messages.Add(value);
    }
    static HttpResponseMessage ImageResponse(string body, HttpStatusCode code = HttpStatusCode.OK, string mime = "application/json")
        => new(code) { Content = new StringContent(body, Encoding.UTF8, mime) };

    static BitmapSource ImageFixture()
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(37, 37, 37)), null, new Rect(0, 0, 720, 180));
            foreach (var (text, y) in new[] { ("Hello world. This is a test.", 24), ("Open settings to continue.", 81) })
                dc.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface("Arial"), 28, Brushes.White, 1), new Point(24, y));
        }
        var image = new RenderTargetBitmap(720, 180, 96, 96, PixelFormats.Pbgra32); image.Render(visual); image.Freeze(); return image;
    }
    static int ImageTranslationTests(string output, bool live)
    {
        try
        {
            Directory.CreateDirectory(output);
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
            typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
            BitmapSource original = ImageFixture(); byte[] png = ImageTranslationRenderer.Png(original);
            File.WriteAllBytes(Path.Combine(output, "original.png"), png);
            var model = YandexImageTranslator.ParseRecognition(ImageOcrFixture, 720, 180);
            Check(model.Blocks.Length == 2 && model.Language == "en", "OCR blocks and source language are parsed independently");
            Check(model.Blocks[0].Lines[0].Curve.Length == 5, "polynomial baseline and mask thickness survive parsing");
            model = YandexImageTranslator.ApplyTranslation(model, ImageTranslationFixture);
            Check(model.Blocks[0].Translation.StartsWith("Привет") && model.Text.Contains("настройки"), "batch translation maps to original paragraph geometry");
            Check(YandexImageTranslator.ParseSession("{\"SID\":\"dcba.8765.zyxw\"}") == "abcd.5678.wxyz", "fresh session identifier is decoded according to the protocol");
            foreach (string invalid in new[] { "[]", "{}", "{\"status\":1}", "{\"status\":\"success\",\"data\":[]}" })
            {
                bool rejected = false; try { YandexImageTranslator.ParseRecognition(invalid, 720, 180); } catch (ImageTranslationException) { rejected = true; }
                Check(rejected, "malformed recognition schema is rejected without interpreting markup");
            }
            bool partial = false; try { YandexImageTranslator.ApplyTranslation(model, "{\"code\":200,\"text\":[\"one\"]}"); } catch (ImageTranslationException) { partial = true; }
            Check(partial, "missing translations never shift text onto another paragraph");
            var clipped = YandexImageTranslator.ParseRecognition(ImageOcrFixture.Replace("\"x\":24", "\"x\":-10").Replace("\"w\":600", "\"w\":9999"), 720, 180);
            Check(clipped.Blocks[0].Bounds.Left == 0 && clipped.Blocks[0].Bounds.Right == 720, "untrusted OCR coordinates are clamped to image bounds");
            Task.Run(() => ImageNetworkTests(png)).GetAwaiter().GetResult();
            if (live)
            {
                using var translator = new YandexImageTranslator();
                var timer = System.Diagnostics.Stopwatch.StartNew();
                model = Task.Run(() => translator.TranslateAsync(png, 720, 180)).GetAwaiter().GetResult();
                Check(model.Blocks.Length >= 2 && model.Blocks.All(b => b.Translation.Any(c => c is >= 'А' and <= 'я')), "live anonymous OCR and Russian translation succeed using only a synthetic image");
                File.WriteAllText(Path.Combine(output, "live-result.json"), JsonSerializer.Serialize(new { passed = true, milliseconds = timer.ElapsedMilliseconds,
                    language = model.Language, text = model.Text, syntheticImageOnly = true, browserCookies = false }));
                File.WriteAllText(Path.Combine(output, "live-geometry.json"), JsonSerializer.Serialize(model.Blocks.Select(b => new
                { bounds = b.Bounds.ToString(CultureInfo.InvariantCulture), lines = b.Lines.Select(l => new { bounds = l.Bounds.ToString(CultureInfo.InvariantCulture), l.Curve }) })));
            }
            BitmapSource translated = ImageTranslationRenderer.Render(original, model);
            File.WriteAllBytes(Path.Combine(output, "translated.png"), ImageTranslationRenderer.Png(translated));
            Check(translated.IsFrozen && translated.PixelWidth == original.PixelWidth && translated.PixelHeight == original.PixelHeight, "result is a frozen image with exact capture dimensions");
            byte[] before = new byte[720 * 180 * 4], after = new byte[before.Length];
            original.CopyPixels(before, 720 * 4, 0); translated.CopyPixels(after, 720 * 4, 0);
            Check(before.Take(720 * 4 * 10).SequenceEqual(after.Take(720 * 4 * 10)), "pixels away from text are preserved");
            Check(!before.SequenceEqual(after), "renderer replaces original lettering rather than displaying a separate text popup");
            var main = new MainWindow();
            try
            {
                var catalog = MenuLayout.Default();
                Check(catalog.Entries.Single(e => e.Id == "image-translation").Visible, "translation has a visible default menu entry");
                Check(!MenuLayout.Parse(catalog.SetVisible("image-translation", false).Serialize()).Entries.Single(e => e.Id == "image-translation").Visible,
                    "menu editor can hide translation without resetting other preferences");
                var panel = Enum.Parse(typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!, "ImageTranslation");
                Call(main, "ShowPanel", panel);
                Check(Call(main, "TargetView")!.ToString() == "ImageTranslation", "translation has its own loading/error page and route");
                Check(main.FindName("ImageTranslationWebsite") == null
                    && typeof(MainWindow).GetMethod("ImageTranslationWebsite_Click", Private) == null,
                    "translation contains no browser link or browser-opening handler");
                var view = (FrameworkElement)main.FindName("ImageTranslationView");
                Set(main, "_imageTranslationBusy", true); Call(main, "RefreshImageTranslationSettings");
                Call(main, "SetImageTranslationStatus", "Перевожу на русский…");
                double compactHeight = view.Height;
                var status = (TextBlock)main.FindName("ImageTranslationStatus");
                Check(double.IsNaN(status.Height) && compactHeight <= 140, "loading status has no fixed empty text area and keeps the island compact");
                Call(main, "SetImageTranslationStatus", "Не удалось перевести область. Проверь подключение к интернету и попробуй выделить область ещё раз.");
                Check(view.Height > compactHeight && view.Height < 200, "wrapped error grows only as far as its actual content requires");
                Call(main, "SetImageTranslationStatus", "Перевожу на русский…");
                Check(view.Height == compactHeight, "short status shrinks the island again after a multiline error");
                main.Show(); Pump(350);
                view.BeginAnimation(UIElement.OpacityProperty, null); view.Opacity = 1; view.Visibility = Visibility.Visible;
                Save(view, Path.Combine(output, "island-page.png"));
                if (live)
                {
                var fixture = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                    Width = 720, Height = 180, Left = 100, Top = 300, Topmost = true, Content = new Image { Source = original, Stretch = Stretch.Fill } };
                try
                {
                    fixture.Show(); Pump(100);
                    Point from = fixture.PointToScreen(new Point()), to = fixture.PointToScreen(new Point(720, 180));
                    var captured = ImageTranslationRenderer.Capture(new Int32Rect((int)from.X, (int)from.Y, (int)(to.X - from.X), (int)(to.Y - from.Y)));
                    byte[] corner = new byte[4]; captured.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
                    Check(captured.IsFrozen && captured.PixelWidth > 0 && corner[0] == 37 && corner[1] == 37 && corner[2] == 37,
                        "native capture reads only the opaque synthetic window's physical rectangle");
                }
                finally { fixture.Close(); }
                }
                var overlay = new ImageTranslationWindow(main, new Int32Rect(100, 180, 720, 180), original, translated, model, () => { });
                overlay.Show(); Pump(180);
                Check(overlay.IsVisible && overlay.Topmost && !overlay.ShowInTaskbar, "translated region opens as a topmost overlay, not a separate taskbar app");
                var surface = (Grid)overlay.Content;
                var displayedImage = surface.Children.OfType<Image>().Single(i => i.Name == "TranslationImage");
                var frame = surface.Children.OfType<SelectionFrame>().Single(b => b.Name == "TranslationRegionFrame");
                Check(frame.Visibility == Visibility.Visible && !frame.IsProcessing && !frame.IsHitTestVisible,
                    "translation region retains a fine visible outline without intercepting clicks");
                surface.Width = original.PixelWidth; surface.Height = original.PixelHeight;
                Save(surface, Path.Combine(output, "framed-region.png"));
                byte[] unchanged = new byte[after.Length]; translated.CopyPixels(unchanged, 720 * 4, 0);
                Check(unchanged.SequenceEqual(after), "preview frame does not modify the bitmap used for image copying");
                Call(overlay, "ShowOriginal", true);
                Check(displayedImage.Source == original && frame.Visibility == Visibility.Visible, "original view restores exact captured bitmap and retains region outline");
                Call(overlay, "ShowOriginal", false);
                Check(displayedImage.Source == translated, "translation view restores the translated bitmap");
                var toolbar = (Window)typeof(ImageTranslationWindow).GetField("_toolbar", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(overlay)!;
                var content = (FrameworkElement)toolbar.Content; content.Width = content.ActualWidth; content.Height = content.ActualHeight;
                Save(content, Path.Combine(output, "toolbar.png"));
                Check(toolbar.Owner == overlay && toolbar.IsVisible, "toolbar is owned and has a separate position outside the copied picture");
                overlay.Close(); Check(!toolbar.IsVisible, "closing result also closes its toolbar");
                var controller = new CancellationTokenSource(); Set(main, "_imageTranslationCancellation", controller); Set(main, "_imageTranslationBusy", true);
                Call(main, "ImageTranslationAction_Click", main, new RoutedEventArgs());
                Check(controller.IsCancellationRequested && !(bool)typeof(MainWindow).GetField("_imageTranslationBusy", Private)!.GetValue(main)!, "cancel button stops pending processing and unlocks a new selection");
                Set(main, "_imageTranslationCancellation", null!); controller.Dispose();
            }
            finally { main.Close(); }
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, live, userScreenshotsUploaded = false }));
            Console.WriteLine($"PASS: {_checks} image translation checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static async Task ImageNetworkTests(byte[] png)
    {
        const string page = "{\"SID\":\"dcba.8765.zyxw\"}";
        using (var handler = new ImageHttp((request, n) =>
        {
            Check(request.Headers.UserAgent.Count > 0 && request.Headers.Referrer?.AbsoluteUri == YandexImageTranslator.Website, "requests carry consistent user agent and referrer");
            Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie"), "no user account or browser credentials are injected");
            if (n == 1) return ImageResponse(page, mime: "text/html");
            if (n == 2)
            {
                Check(request.RequestUri!.AbsoluteUri.Contains("sid=abcd.5678.wxyz") && request.Content is MultipartFormDataContent, "PNG uploads use a freshly decoded session and multipart body");
                return ImageResponse(ImageOcrFixture);
            }
            Check(request.RequestUri!.AbsoluteUri.Contains("target_lang=ru") && request.Content is FormUrlEncodedContent, "translation requests target Russian and preserve repeated form fields");
            return ImageResponse(ImageTranslationFixture);
        }))
        using (var service = new YandexImageTranslator(handler))
        {
            Check(handler.Count == 0, "constructing the service does not make network requests");
            var progress = new ImageProgress();
            var result = await service.TranslateAsync(png, 720, 180, progress);
            Check(handler.Count == 3 && result.Blocks.Length == 2, "anonymous processing needs one session, one OCR and one batched translation");
            Check(progress.Messages.SequenceEqual(["Подключаюсь…", "Распознаю текст…", "Перевожу на русский…"]),
                "processing stages match their expected labels");
        }
        using (var handler = new ImageHttp((_, n) => n == 1 ? ImageResponse(page) : ImageResponse("", HttpStatusCode.TooManyRequests)))
        using (var service = new YandexImageTranslator(handler))
        {
            bool limited = false; try { await service.TranslateAsync(png, 720, 180); } catch (ImageTranslationException ex) { limited = ex.Message.Contains("позже"); }
            Check(limited && handler.Count == 2, "rate limits produce one clear error without retry loops");
        }
        using (var handler = new ImageHttp((_, n) => n == 1 || n == 3 ? ImageResponse(page) : n == 2 ? ImageResponse("{}", HttpStatusCode.Forbidden) : n == 4 ? ImageResponse(ImageOcrFixture) : ImageResponse(ImageTranslationFixture)))
        using (var service = new YandexImageTranslator(handler))
        {
            await service.TranslateAsync(png, 720, 180);
            Check(handler.Count == 5, "expired session is renewed once, then normal processing resumes");
        }
        using (var handler = new ImageHttp((_, n) => n == 1 ? ImageResponse(page) : ImageResponse("<html>showcaptcha</html>", HttpStatusCode.Forbidden, "text/html")))
        using (var service = new YandexImageTranslator(handler))
        {
            bool challenge = false; try { await service.TranslateAsync(png, 720, 180); }
            catch (ImageTranslationException ex) { challenge = ex.Message == "Не удалось перевести область. Попробуй позже."; }
            Check(challenge && handler.Count == 2, "failed request stops cleanly with a concise retry message");
        }
        using (var cancellation = new CancellationTokenSource())
        using (var handler = new ImageHttp((_, _) => throw new Exception("Cancelled request must not be sent")))
        using (var service = new YandexImageTranslator(handler))
        {
            cancellation.Cancel(); bool cancelled = false;
            try { await service.TranslateAsync(png, 720, 180, cancellation: cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && handler.Count == 0, "cancellation prevents uploads and does not become a network error");
        }
    }
}
