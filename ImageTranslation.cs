using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

sealed record ImageTextLine(Rect Bounds, string Text, Color Background, Color Foreground, double[] Curve);
sealed record ImageTextBlock(Rect Bounds, string Text, ImageTextLine[] Lines, string Translation = "");
sealed record ImageTranslationResult(string Language, ImageTextBlock[] Blocks)
{
    public string Text => string.Join(Environment.NewLine + Environment.NewLine, Blocks.Select(b => b.Translation));
}

sealed class ImageTranslationException(string message) : Exception(message);

// A separate anonymous session. No browser cookies, HAR data, account tokens or
// screenshots are persisted. Only the region explicitly selected by the user is sent.
sealed class YandexImageTranslator : IDisposable
{
    internal const string Website = "https://translate.yandex.ru/ocr";
    internal const string RecognizeUrl = "https://translate.yandex.net/ocr/v1.1/recognize";
    internal const string TranslateUrl = "https://translate.yandex.net/api/v1/tr.json/translate";
    const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36";
    readonly HttpClient _http;
    string? _sid;
    DateTime _sessionAt;
    int _request;

    public YandexImageTranslator(HttpMessageHandler? handler = null)
    {
        _http = new HttpClient(handler ?? new HttpClientHandler
        {
            CookieContainer = new CookieContainer(), AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
    }

    public async Task<ImageTranslationResult> TranslateAsync(byte[] png, int width, int height,
        IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (png.Length is < 8 or > 20_000_000 || width is < 16 or > 4096 || height is < 16 or > 4096)
            throw new ImageTranslationException("Выбери область поменьше.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (_sid == null || DateTime.UtcNow - _sessionAt > TimeSpan.FromMinutes(10))
                    {
                        progress?.Report("Подключаюсь…");
                        string html = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Website), 2_000_000, timeout.Token);
                        _sid = ParseSession(html); _sessionAt = DateTime.UtcNow;
                    }
                    progress?.Report("Распознаю текст…");
                    using var upload = new MultipartFormDataContent();
                    var file = new ByteArrayContent(png); file.Headers.ContentType = new("image/png");
                    upload.Add(file, "file", "region.png");
                    string json = await SendAsync(new HttpRequestMessage(HttpMethod.Post,
                        RecognizeUrl + "?srv=tr-image&sid=" + Uri.EscapeDataString(_sid) + "&lang=*") { Content = upload }, 4_000_000, timeout.Token);
                    var result = ParseRecognition(json, width, height);
                    if (result.Blocks.Length == 0) throw new ImageTranslationException("В этой области текст не найден.");
                    if (result.Language == "ru") return result with { Blocks = result.Blocks.Select(b => b with { Translation = b.Text }).ToArray() };
                    progress?.Report("Перевожу на русский…");
                    // Repeated text fields preserve paragraphs and their geometry.
                    var fields = result.Blocks.Select(b => new KeyValuePair<string, string>("text", b.Text)).ToList();
                    fields.Add(new("options", "0"));
                    using var form = new FormUrlEncodedContent(fields);
                    string id = _sid + "-" + Interlocked.Increment(ref _request) + "-0";
                    string url = TranslateUrl + "?id=" + Uri.EscapeDataString(id) + "&srv=tr-image&source_lang="
                        + Uri.EscapeDataString(result.Language) + "&target_lang=ru&reason=ocr&format=plain&strategy=0&disable_cache=false";
                    string translated = await SendAsync(new HttpRequestMessage(HttpMethod.Post, url) { Content = form }, 2_000_000, timeout.Token);
                    return ApplyTranslation(result, translated);
                }
                catch (SessionExpiredException) when (attempt == 0) { _sid = null; }
                catch (SessionExpiredException) { throw new ImageTranslationException("Не удалось отправить запрос. Попробуй позже."); }
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new ImageTranslationException("Сервис долго не отвечает. Попробуй ещё раз."); }
        catch (HttpRequestException)
        { throw new ImageTranslationException("Нет связи с сервисом. Проверь подключение."); }
        catch (JsonException)
        { throw new ImageTranslationException("Не удалось прочитать ответ сервиса."); }
    }

    sealed class SessionExpiredException : Exception;

    async Task<string> SendAsync(HttpRequestMessage request, int limit, CancellationToken cancellation)
    {
        using (request)
        {
            request.Headers.Referrer = new(Website);
            request.Headers.TryAddWithoutValidation("Origin", "https://translate.yandex.ru");
            request.Headers.Accept.ParseAdd(request.Method == HttpMethod.Get ? "text/html" : "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode == 429) throw new ImageTranslationException("Слишком много запросов. Попробуй позже.");
            if (response.Content.Headers.ContentLength > limit) throw new ImageTranslationException("Ответ сервиса слишком большой.");
            await using var source = await response.Content.ReadAsStreamAsync(cancellation);
            using var buffer = new MemoryStream(); byte[] part = new byte[8192]; int count;
            while ((count = await source.ReadAsync(part, cancellation)) != 0)
            {
                if (buffer.Length + count > limit) throw new ImageTranslationException("Ответ сервиса слишком большой.");
                buffer.Write(part, 0, count);
            }
            string text = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentType?.MediaType == "application/json")
                throw new SessionExpiredException();
            if (!response.IsSuccessStatusCode || text.Contains("showcaptcha", StringComparison.OrdinalIgnoreCase)
                || request.Method == HttpMethod.Post && text.TrimStart().StartsWith('<'))
                throw new ImageTranslationException("Не удалось перевести область. Попробуй позже.");
            return text;
        }
    }

    internal static string ParseSession(string html)
    {
        var match = Regex.Match(html, "\"SID\"\\s*:\\s*(\"[A-Za-z0-9.\\-]{8,200}\")", RegexOptions.None, TimeSpan.FromSeconds(1));
        if (!match.Success) throw new ImageTranslationException("Не удалось подключиться к переводчику.");
        string encoded = JsonSerializer.Deserialize<string>(match.Groups[1].Value)!;
        // The site's prepareLoggingEnvironment reverses each SID segment.
        return string.Join('.', encoded.Split('.').Select(p => new string(p.Reverse().ToArray())));
    }

    internal static ImageTranslationResult ParseRecognition(string json, int width, int height)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String || status.GetString() != "success"
            || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
            throw new ImageTranslationException("Не удалось распознать текст.");
        string language = data.TryGetProperty("detected_lang", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() ?? "en" : "en";
        if (!Regex.IsMatch(language, "^[a-z]{2,3}(-[a-z]{2,4})?$")) language = "en";
        if (data.TryGetProperty("rotate", out var rotate) && rotate.ValueKind == JsonValueKind.Number && rotate.TryGetInt32(out int angle) && angle != 0)
            throw new ImageTranslationException("Выбери область с горизонтальным текстом.");
        var result = new List<ImageTextBlock>(); int characters = 0, lineCount = 0, scannedBlocks = 0;
        foreach (var block in blocks.EnumerateArray())
        {
            if (result.Count >= 160 || ++scannedBlocks > 600) throw new ImageTranslationException("Слишком много текста. Выбери область поменьше.");
            if (block.ValueKind != JsonValueKind.Object) continue;
            if (!block.TryGetProperty("boxes", out var boxes) || boxes.ValueKind != JsonValueKind.Array) continue;
            var lines = new List<ImageTextLine>();
            foreach (var box in boxes.EnumerateArray())
            {
                if (box.ValueKind != JsonValueKind.Object) continue;
                if (++lineCount > 1200) throw new ImageTranslationException("Слишком много текста. Выбери область поменьше.");
                string text = box.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
                if (text.Length == 0) continue;
                characters += text.Length;
                if (characters > 30_000) throw new ImageTranslationException("Слишком много текста. Выбери область поменьше.");
                Rect bounds = Bounds(box, width, height);
                if (bounds.IsEmpty || bounds.Width < 1 || bounds.Height < 1) continue;
                double[] curve = [];
                if (box.TryGetProperty("polyCoefs", out var poly) && poly.ValueKind == JsonValueKind.Object && Number(poly, "fromXtoY", 1) != 0)
                    curve = [Number(poly, "a3"), Number(poly, "a2"), Number(poly, "a1"), Number(poly, "a0"), Math.Clamp(Number(poly, "hh", bounds.Height), 1, 300)];
                lines.Add(new(bounds, text, ReadColor(box, "backgroundColor", Colors.Black), ReadColor(box, "textColor", Colors.White), curve));
            }
            if (lines.Count == 0) continue;
            Rect area = lines[0].Bounds; foreach (var line in lines.Skip(1)) area.Union(line.Bounds);
            result.Add(new(area, string.Join(" ", lines.Select(l => l.Text)), lines.ToArray()));
        }
        return new(language, result.ToArray());
    }

    internal static ImageTranslationResult ApplyTranslation(ImageTranslationResult result, string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out int value) || value != 200
            || !root.TryGetProperty("text", out var texts) || texts.ValueKind != JsonValueKind.Array || texts.GetArrayLength() != result.Blocks.Length)
            throw new ImageTranslationException("Получен неполный перевод. Попробуй ещё раз.");
        string[] values = texts.EnumerateArray().Select(t => t.ValueKind == JsonValueKind.String ? t.GetString()! : "").ToArray();
        if (values.Any(v => string.IsNullOrWhiteSpace(v) || v.Length > 30_000)) throw new ImageTranslationException("Получен неполный перевод.");
        return result with { Blocks = result.Blocks.Select((b, i) => b with { Translation = values[i] }).ToArray() };
    }

    static Rect Bounds(JsonElement e, int width, int height)
    {
        double x = Number(e, "x"), y = Number(e, "y"), w = Number(e, "w"), h = Number(e, "h");
        if (w <= 0 || h <= 0) return Rect.Empty;
        double right = Math.Min(width, x + w), bottom = Math.Min(height, y + h);
        x = Math.Max(0, x); y = Math.Max(0, y);
        return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : Rect.Empty;
    }
    static double Number(JsonElement e, string name, double fallback = 0)
    {
        if (!e.TryGetProperty(name, out var p)) return fallback;
        if (p.ValueKind is JsonValueKind.True or JsonValueKind.False) return p.GetBoolean() ? 1 : 0;
        return p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out double n) && double.IsFinite(n) && Math.Abs(n) < 1e9 ? n : fallback;
    }
    static Color ReadColor(JsonElement e, string name, Color fallback)
    {
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.Object) return fallback;
        return Color.FromRgb((byte)Math.Clamp(Number(p, "r", fallback.R), 0, 255), (byte)Math.Clamp(Number(p, "g", fallback.G), 0, 255), (byte)Math.Clamp(Number(p, "b", fallback.B), 0, 255));
    }
    public void Dispose() => _http.Dispose();
}
