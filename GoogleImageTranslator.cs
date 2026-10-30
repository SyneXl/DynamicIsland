using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

// Chromium Lens' image protocol, not Google's search HTML. No browser profile,
// account cookies, HAR sessions, consent tokens, CAPTCHA solvers or redirects.
sealed class GoogleImageTranslator : IImageTranslator
{
    enum TranslationStatus : ulong
    {
        Unknown = 0, Success = 1, ServerError = 2, UnsupportedLanguagePair = 3,
        SameLanguage = 4, UnknownSourceLanguage = 5, InvalidRequest = 6,
        DeadlineExceeded = 7, EmptyTranslation = 8, NoOpTranslation = 9,
    }
    internal const string Endpoint = "https://lensfrontend-pa.googleapis.com/v1/crupload";
    // Public Chromium Lens client key, also used by chrome-lens-ocr. Not an
    // account credential and never taken from the user's browser/capture.
    const string ChromiumClientKey = "AIzaSyDr2UxVnv_U85AbhhY8XSHSIavUW0DC-sY";
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly Func<DateTimeOffset> _now;
    DateTimeOffset _retryAfter;

    public GoogleImageTranslator(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _http = new HttpClient(handler ?? new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
    }

    internal static byte[] BuildRequest(byte[] png, int width, int height)
    {
        var id = LensProto.Pack(LensProto.Integer(1, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8))), LensProto.Integer(2, 1), LensProto.Integer(3, 1));
        var locale = LensProto.Pack(LensProto.String(1, "ru"), LensProto.String(2, "RU"), LensProto.String(3, "Europe/Moscow"));
        var translate = LensProto.Pack(LensProto.Integer(1, 2), LensProto.Bytes(3, LensProto.Pack(LensProto.String(1, "ru"), LensProto.String(2, "auto"))));
        var client = LensProto.Pack(LensProto.Integer(1, 3), LensProto.Integer(2, 4), LensProto.Bytes(4, locale), LensProto.Bytes(17, LensProto.Bytes(1, translate)));
        var context = LensProto.Pack(LensProto.Bytes(3, id), LensProto.Bytes(4, client));
        var image = LensProto.Pack(LensProto.Bytes(1, LensProto.Bytes(1, png)), LensProto.Bytes(3,
            LensProto.Pack(LensProto.Integer(1, (ulong)width), LensProto.Integer(2, (ulong)height))));
        return LensProto.Pack(LensProto.Bytes(1, LensProto.Pack(LensProto.Bytes(1, context), LensProto.Bytes(3, image))), LensProto.Integer(6, 1));
    }

    public async Task<ImageTranslationResult> TranslateAsync(byte[] png, int width, int height, IProgress<string>? progress = null, CancellationToken cancellation = default)
    {
        if (png.Length is < 8 or > 20_000_000 || width is < 16 or > 4096 || height is < 16 or > 4096)
            throw new ImageTranslationException("Выбери область поменьше.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(40));
        bool entered = false;
        try
        {
            await _gate.WaitAsync(timeout.Token); entered = true;
            if (_now() < _retryAfter) throw new ImageTranslationException("Сервис ограничил запросы. Попробуй позже.");
            progress?.Report("Перевожу на русский…");
            byte[] body = await Task.Run(() => BuildRequest(png, width, height), timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new ByteArrayContent(body) };
            request.Content.Headers.ContentType = new("application/x-protobuf");
            request.Headers.Accept.ParseAdd("application/x-protobuf"); request.Headers.Add("X-Goog-Api-Key", ChromiumClientKey);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is 429 or 403)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - _now()) ?? TimeSpan.FromMinutes(1);
                _retryAfter = _now() + TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 30, 900));
                throw new ImageTranslationException("Сервис ограничил запросы. Попробуй позже.");
            }
            if (!response.IsSuccessStatusCode) throw new ImageTranslationException("Не удалось перевести область. Попробуй позже.");
            if (response.Content.Headers.ContentType?.MediaType is not ("application/x-protobuf" or "application/octet-stream"))
                throw new ImageTranslationException("Не удалось прочитать ответ переводчика. Попробуй позже.");
            const int limit = 12_000_000;
            if (response.Content.Headers.ContentLength > limit) throw new ImageTranslationException("Ответ сервиса слишком большой.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream(); byte[] part = new byte[8192]; int count;
            while ((count = await stream.ReadAsync(part, timeout.Token)) != 0)
            {
                if (buffer.Length + count > limit) throw new ImageTranslationException("Ответ сервиса слишком большой.");
                buffer.Write(part, 0, count);
            }
            return ParseResponse(buffer.ToArray(), width, height);
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new ImageTranslationException("Сервис долго не отвечает. Попробуй ещё раз."); }
        catch (HttpRequestException) { throw new ImageTranslationException("Нет связи с сервисом. Проверь подключение."); }
        catch (Exception e) when (e is FormatException or OverflowException or DecoderFallbackException)
        { throw new ImageTranslationException("Не удалось прочитать ответ переводчика."); }
        finally { if (entered) _gate.Release(); }
    }

    internal static ImageTranslationResult ParseResponse(byte[] bytes, int width, int height)
    {
        var root = new LensProto(bytes);
        if (root.Message(1).Number(1) != 0 || !root.Has(2)) throw new ImageTranslationException("Не удалось распознать текст.");
        var objects = root.Message(2); var text = objects.Message(3);
        var paragraphs = text.Message(1).Messages(1).ToArray();
        if (paragraphs.Length == 0) throw new ImageTranslationException("В этой области текст не найден.");
        if (paragraphs.Length > 160) throw new ImageTranslationException("Слишком много текста. Выбери область поменьше.");
        var translations = new Dictionary<string, LensProto>(StringComparer.Ordinal);
        int gleamCount = 0;
        foreach (var gleam in objects.Messages(4))
        {
            if (++gleamCount > 600) throw new FormatException();
            foreach (string id in gleam.Texts(11))
                if (!translations.TryAdd(id, gleam.Message(10))) throw new FormatException();
            if (translations.Count > 600) throw new FormatException();
        }
        var result = new List<ImageTextBlock>(); string language = "ru"; int characters = 0, lineCount = 0;
        foreach (var paragraph in paragraphs)
        {
            string id = paragraph.Text(6, 200);
            bool found = translations.TryGetValue(id, out var translation);
            if (!found && id.Length == 0 && paragraphs.Length == 1 && translations.Count == 1) translation = translations.Values.Single();
            if (translation?.Text(2, 32) is { Length: > 0 } target && target != "ru") throw new FormatException();
            string sourceLanguage = translation?.Text(3, 32) ?? text.Text(2, 32);
            if (sourceLanguage.Length == 0) sourceLanguage = "en";
            var status = (TranslationStatus)(translation?.Message(1).Number(1) ?? 0);
            if (sourceLanguage != "ru") language = sourceLanguage;
            var style = translation?.Messages(5).FirstOrDefault()?.Message(3);
            Color foreground = ColorOf(style?.Number(1, 0xffffffff) ?? 0xffffffff), background = ColorOf(style?.Number(2, 0xff000000) ?? 0xff000000);
            var lines = new List<ImageTextLine>();
            foreach (var line in paragraph.Messages(2))
            {
                if (++lineCount > 1200) throw new FormatException();
                var words = line.Messages(1).Take(2001).ToArray();
                if (words.Length > 2000) throw new FormatException();
                var contentBuilder = new StringBuilder();
                foreach (var (word, i) in words.Select((word, i) => (word, i)))
                {
                    contentBuilder.Append(word.Text(2, 4000)).Append(word.Has(3) ? word.Text(3, 64) : i < words.Length - 1 ? " " : "");
                    if (contentBuilder.Length > 30_000) throw new FormatException();
                }
                string content = contentBuilder.ToString().Trim();
                characters += content.Length;
                if (characters > 30_000) throw new FormatException();
                Rect bounds = Bounds(line.Message(2), width, height);
                if (!string.IsNullOrWhiteSpace(content) && !bounds.IsEmpty) lines.Add(new(bounds, content, background, foreground, []));
            }
            if (lines.Count == 0) continue;
            Rect area = Bounds(paragraph.Message(3), width, height);
            if (area.IsEmpty) { area = lines[0].Bounds; foreach (var line in lines.Skip(1)) area.Union(line.Bounds); }
            string original = string.Join(" ", lines.Select(l => l.Text));
            // A language cannot be detected for a graph value, punctuation or
            // icon. Lens may return UNKNOWN_SOURCE_LANGUAGE rather than NO_OP
            // for exactly the same number in a differently sized screenshot.
            // Decide from both the outcome and actual OCR text, not its size or
            // an ever-growing list of special-case numeric strings.
            bool nonLinguistic = !original.EnumerateRunes().Any(Rune.IsLetter);
            if (!Enum.IsDefined(status) || status is TranslationStatus.ServerError or TranslationStatus.InvalidRequest or TranslationStatus.DeadlineExceeded)
                throw new ImageTranslationException("Не удалось перевести область. Попробуй ещё раз.");
            bool preserveOriginal = nonLinguistic || sourceLanguage == "ru" || status is
                TranslationStatus.SameLanguage or TranslationStatus.NoOpTranslation or TranslationStatus.UnknownSourceLanguage;
            if (!preserveOriginal && status != TranslationStatus.Success)
                throw new ImageTranslationException("Получен неполный перевод. Попробуй ещё раз.");
            string translated = preserveOriginal ? original : translation?.Text(4) ?? "";
            if (string.IsNullOrWhiteSpace(translated)) throw new ImageTranslationException("Получен неполный перевод.");
            characters += translated.Length;
            if (characters > 60_000) throw new FormatException();
            result.Add(new(area, original, lines.ToArray(), translated, preserveOriginal || translated == original));
        }
        if (result.Count == 0) throw new ImageTranslationException("В этой области текст не найден.");
        return new(language, result.ToArray());
    }

    static Color ColorOf(ulong color) => Color.FromArgb((byte)(color >> 24), (byte)(color >> 16), (byte)(color >> 8), (byte)color);
    static Rect Bounds(LensProto geometry, int width, int height)
    {
        var box = geometry.Message(1);
        double x = box.Float(1), y = box.Float(2), w = box.Float(3), h = box.Float(4);
        if (!double.IsFinite(x + y + w + h) || w <= 0 || h <= 0) return Rect.Empty;
        if (box.Number(6) == 1) { x *= width; y *= height; w *= width; h *= height; }
        else if (box.Number(6) != 2) return Rect.Empty;
        double left = Math.Max(0, x - w / 2), top = Math.Max(0, y - h / 2), right = Math.Min(width, x + w / 2), bottom = Math.Min(height, y + h / 2);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : Rect.Empty;
    }
    public void Dispose() => _http.Dispose();
}
