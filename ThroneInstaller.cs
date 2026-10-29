using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed record ThroneRelease(string Version, string FileName, string Url, long Size, string Sha256);
sealed record ThroneInstallerFile(string Path, long Size, string Sha256);

sealed class ThroneInstaller
{
    internal const string ReleasesUrl = "https://api.github.com/repos/throneproj/Throne/releases?per_page=100";
    const long MaxSize = 512L << 20;
    readonly HttpClient _http;
    public ThroneInstaller(HttpClient? http = null) => _http = http ?? SharedHttp;
    internal static async Task RunAsync(ProcessStartInfo start, CancellationToken cancel)
    {
        using var process = Process.Start(start) ?? throw new IOException("Установщик не запущен");
        // Cancelling the island's lifetime releases the wait, never kills setup.
        await process.WaitForExitAsync(cancel);
    }
    static readonly HttpClient SharedHttp = CreateHttp();
    static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-Throne/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
    public async Task<ThroneRelease> LatestAsync(CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel); timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var response = await _http.GetAsync(ReleasesUrl, timeout.Token);
        // Public GitHub API is shared-IP rate limited. The public Releases page
        // exposes the same per-asset SHA-256; no account or token is required.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            return await LatestFromHtmlAsync(timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(timeout.Token));
        return Select(json.RootElement);
    }

    async Task<string> HtmlAsync(string url, CancellationToken cancel)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url); request.Headers.Accept.ParseAdd("text/html");
        using var response = await _http.SendAsync(request, cancel); response.EnsureSuccessStatusCode();
        string html = await response.Content.ReadAsStringAsync(cancel);
        if (html.Length > 4_000_000) throw new InvalidDataException("Слишком большая страница релизов");
        return html;
    }
    async Task<ThroneRelease> LatestFromHtmlAsync(CancellationToken cancel)
    {
        string html = await HtmlAsync("https://github.com/throneproj/Throne/releases", cancel);
        foreach (string tag in HtmlTags(html).Take(5))
        {
            string assets = await HtmlAsync("https://github.com/throneproj/Throne/releases/expanded_assets/" + tag, cancel);
            if (HtmlAsset(tag, assets) is { } release) return release;
        }
        throw new InvalidDataException("Подходящий установщик Throne пока не опубликован");
    }
    internal static string[] HtmlTags(string html) => Regex.Matches(html, "href=\"/throneproj/Throne/releases/tag/(v?(\\d{1,6}\\.\\d{1,6}\\.\\d{1,6})(?:-beta\\.(\\d{1,6}))?)\"", RegexOptions.CultureInvariant)
        .Select(m => (Tag: m.Groups[1].Value, Version: Version.Parse(m.Groups[2].Value), Beta: m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : int.MaxValue))
        .Where(x => x.Version > new Version(1, 4, 0) || x.Version == new Version(1, 4, 0) && x.Beta >= 2)
        .OrderByDescending(x => x.Version).ThenByDescending(x => x.Beta).Select(x => x.Tag).Distinct().ToArray();
    internal static ThroneRelease? HtmlAsset(string tag, string html)
    {
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+(?:-beta\.\d+)?$")) return null;
        string name = $"Throne-{tag.TrimStart('v')}-windows-universal-installer.exe";
        string path = $"/throneproj/Throne/releases/download/{tag}/{name}";
        foreach (Match row in Regex.Matches(html, @"<li\b[^>]*>[\s\S]*?</li>", RegexOptions.CultureInvariant))
        {
            if (!row.Value.Contains("href=\"" + path + "\"", StringComparison.Ordinal)) continue;
            var digests = Regex.Matches(row.Value, "value=\"sha256:([a-fA-F0-9]{64})\"", RegexOptions.CultureInvariant);
            if (digests.Count == 1) return new(tag, name, "https://github.com" + path, 0, digests[0].Groups[1].Value);
        }
        return null;
    }
    internal static ThroneRelease Select(JsonElement releases)
    {
        var found = new List<(Version Version, int Beta, ThroneRelease Release)>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.GetProperty("draft").GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            var versionMatch = Regex.Match(tag, @"^v?(\d+\.\d+\.\d+)(?:-beta\.(\d+))?$", RegexOptions.CultureInvariant);
            if (!versionMatch.Success || !Version.TryParse(versionMatch.Groups[1].Value, out var version)) continue;
            int beta = versionMatch.Groups[2].Success && int.TryParse(versionMatch.Groups[2].Value, out int b) ? b : int.MaxValue;
            if (version < new Version(1, 4, 0) || version == new Version(1, 4, 0) && beta < 2) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (name != $"Throne-{tag.TrimStart('v')}-windows-universal-installer.exe") continue;
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.Query.Length != 0 || uri.Fragment.Length != 0
                    || uri.AbsolutePath != $"/throneproj/Throne/releases/download/{tag}/{name}") continue;
                string digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
                if (!Regex.IsMatch(digest, "^sha256:[0-9a-fA-F]{64}$")) continue;
                long size = asset.GetProperty("size").GetInt64();
                if (size is < 1024 or > MaxSize) continue;
                found.Add((version, beta, new(tag, name, url, size, digest[7..])));
            }
        }
        return found.OrderByDescending(x => x.Version).ThenByDescending(x => x.Beta).FirstOrDefault().Release
            ?? throw new InvalidDataException("Подходящий установщик Throne пока не опубликован");
    }

    public async Task<ThroneInstallerFile> DownloadAsync(ThroneRelease release, string downloads, IProgress<int>? progress, CancellationToken cancel)
    {
        Directory.CreateDirectory(downloads);
        string partial = System.IO.Path.Combine(downloads, ".throne-" + Guid.NewGuid().ToString("N") + ".part");
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel); stall.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            using var response = await _http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, stall.Token); response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is not { Scheme: "https" } final ||
                !(final.Host == "github.com" || final.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Неизвестный источник установщика");
            long expected = release.Size > 0 ? release.Size : response.Content.Headers.ContentLength ?? 0;
            if (expected < 0 || expected > MaxSize || release.Size > 0 && response.Content.Headers.ContentLength is { } length && length != release.Size) throw new InvalidDataException("Размер установщика изменился");
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; int read;
                while ((read = await input.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(TimeSpan.FromSeconds(30)); total += read;
                    if (total > MaxSize || expected > 0 && total > expected) throw new InvalidDataException("Размер установщика изменился");
                    await output.WriteAsync(buffer.AsMemory(0, read), stall.Token); sha.AppendData(buffer, 0, read);
                    if (expected > 0) progress?.Report((int)(total * 100 / expected));
                }
                await output.FlushAsync(stall.Token);
            }
            if (total < 1024 || expected > 0 && total != expected || !CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), Convert.FromHexString(release.Sha256))) throw new InvalidDataException("Проверка установщика не пройдена");
            using (var file = File.OpenRead(partial)) if (file.ReadByte() != 'M' || file.ReadByte() != 'Z') throw new InvalidDataException("Некорректный установщик");
            string target = System.IO.Path.Combine(downloads, release.FileName);
            if (File.Exists(target)) target = System.IO.Path.Combine(downloads, System.IO.Path.GetFileNameWithoutExtension(release.FileName) + "-" + Guid.NewGuid().ToString("N")[..8] + ".exe");
            cancel.ThrowIfCancellationRequested(); File.Move(partial, target);
            return new(target, total, release.Sha256);
        }
        finally { try { File.Delete(partial); } catch { } }
    }

    public static async Task<bool> VerifyAsync(ThroneInstallerFile installer, CancellationToken cancel)
    {
        try
        {
            await using var file = new FileStream(installer.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            return file.Length == installer.Size && CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(file, cancel), Convert.FromHexString(installer.Sha256));
        }
        catch (IOException) { return false; }
    }

    public static string Downloads()
    {
        Guid downloads = new("374DE290-123F-4565-9164-39C4925E467B");
        Marshal.ThrowExceptionForHR(SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var path));
        try { return Marshal.PtrToStringUni(path) ?? throw new IOException("Папка загрузок недоступна"); }
        finally { Marshal.FreeCoTaskMem(path); }
    }
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
