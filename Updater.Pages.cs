using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed partial class Updater
{
    internal const string LatestReleasePage = "https://github.com/" + Repository + "/releases/latest";
    internal const string ReleaseAssetsPage = "https://github.com/" + Repository + "/releases/expanded_assets/";

    async Task<bool> ReadPublishedReleaseAsync(CancellationToken cancel)
    {
        if (DateTimeOffset.UtcNow >= _apiRetryAt)
        {
            try
            {
                using var apiTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                apiTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                using var response = await _http.GetAsync(LatestReleaseUrl, apiTimeout.Token);
                if (response.StatusCode == HttpStatusCode.NotFound) return false;
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                    _apiRetryAt = RetryAfter(response);
                else
                {
                    response.EnsureSuccessStatusCode();
                    string json = await response.Content.ReadAsStringAsync(apiTimeout.Token);
                    if (json.Length > 2_000_000) throw new InvalidDataException("Release metadata is too large.");
                    using var document = JsonDocument.Parse(json);
                    ReadRelease(document.RootElement);
                    return true;
                }
            }
            catch (HttpRequestException) { _apiRetryAt = DateTimeOffset.UtcNow.AddMinutes(1); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { _apiRetryAt = DateTimeOffset.UtcNow.AddMinutes(1); }
        }
        return await ReadReleasePageAsync(cancel);
    }

    static DateTimeOffset RetryAfter(HttpResponseMessage response)
    {
        var now = DateTimeOffset.UtcNow;
        var retry = response.Headers.RetryAfter;
        if (retry?.Date is { } date && date > now) return date;
        if (retry?.Delta is { } delay && delay > TimeSpan.Zero) return now.Add(delay);
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var values) && long.TryParse(values.FirstOrDefault(), out long reset))
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(reset) is var at && at > now ? at : now.AddMinutes(1); }
            catch (ArgumentOutOfRangeException) { }
        }
        return now.AddMinutes(1);
    }

    async Task<HttpResponseMessage> GetPageAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("text/html");
        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
    }

    static async Task<string> ReadPageAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "github.com" }) throw new InvalidDataException("Release page left GitHub HTTPS.");
        const int max = 2_000_000;
        if (response.Content.Headers.ContentLength > max) throw new InvalidDataException("Release page is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[16384]; int count;
        while ((count = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            if (bytes.Length + count > max) throw new InvalidDataException("Release page is too large.");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length);
    }

    async Task<bool> ReadReleasePageAsync(CancellationToken cancel)
    {
        using var page = await GetPageAsync(LatestReleasePage, cancel);
        if (page.StatusCode == HttpStatusCode.NotFound) return false;
        string html = await ReadPageAsync(page, cancel);
        var uri = page.RequestMessage!.RequestUri!;
        string prefix = "/" + Repository + "/releases/tag/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Release page is not from this fork.");
        string tag = uri.AbsolutePath[prefix.Length..];
        if (!Regex.IsMatch(tag, @"\Av?\d+\.\d+\.\d+\z") || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("Not a stable published release.");
        LatestVersion = Normalize(version);
        Notes = PageNotes(html);
        _downloadUrl = _hash = ""; TotalBytes = 0;
        if (LatestVersion <= _current) return true;
        using var assets = await GetPageAsync(ReleaseAssetsPage + tag, cancel);
        string assetHtml = await ReadPageAsync(assets, cancel);
        if (!string.Equals(assets.RequestMessage!.RequestUri!.AbsolutePath, "/" + Repository + "/releases/expanded_assets/" + tag, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Asset list is not from this release.");
        string path = $"/{Repository}/releases/download/{tag}/{AssetName}";
        foreach (Match row in Regex.Matches(assetHtml, @"<li\b[^>]*>[\s\S]*?</li>", RegexOptions.CultureInvariant))
        {
            if (!row.Value.Contains("href=\"" + path + "\"", StringComparison.Ordinal)) continue;
            var digests = Regex.Matches(row.Value, "value=\"sha256:([a-fA-F0-9]{64})\"", RegexOptions.CultureInvariant);
            if (digests.Count != 1) throw new InvalidDataException("Update SHA-256 is missing or ambiguous.");
            _hash = digests[0].Groups[1].Value; _downloadUrl = "https://github.com" + path;
            return true;
        }
        throw new InvalidDataException("Executable is not published yet.");
    }

    internal static string[] PageNotes(string html)
    {
        var body = Regex.Match(html, "<div[^>]*class=\"[^\"]*\\bmarkdown-body\\b[^\"]*\"[^>]*>([\\s\\S]*?)</div>", RegexOptions.CultureInvariant);
        return ParseNotes(string.Join('\n', Regex.Matches(body.Groups[1].Value, @"<li\b[^>]*>([\s\S]*?)</li>", RegexOptions.CultureInvariant)
            .Select(m => "- " + WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]*>", "")).Trim())));
    }
}
