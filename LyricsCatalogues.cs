using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed partial class LyricsService
{
    readonly record struct Guess(string Song, string By);

    sealed record Hit(string Id, string Title, string[] Artists, double Duration);

    const string DeezerSite = "https://www.deezer.com/";
    const string DeezerSearchUrl = "https://api.deezer.com/search?limit=10&q=";
    const string DeezerPassUrl = "https://auth.deezer.com/login/anonymous?jo=p&rto=c&i=c";
    const string DeezerLyricUrl = "https://pipe.deezer.com/api";
    const string DeezerLyricQuery = "query TrackLyrics($trackId: String!) { track(trackId: $trackId) { lyrics { synchronizedLines { lrcTimestamp line } } } }";
    const string NetEaseSite = "https://music.163.com/";
    const string NetEaseSearchUrl = NetEaseSite + "api/cloudsearch/pc?type=1&limit=10&s=";
    const string NetEaseLyricUrl = NetEaseSite + "api/song/lyric?lv=-1&kv=-1&tv=-1&id=";
    const string QqMusicSite = "https://y.qq.com/";
    const string QqMusicSearchUrl = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp?format=json&p=1&n=10&w=";
    const string QqMusicLyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&songmid=";
    const string KugouSite = "https://www.kugou.com/";
    const string KugouSearchUrl = "https://songsearch.kugou.com/song_search_v2?page=1&pagesize=10&platform=WebFilter&format=json&keyword=";
    const string KugouLyricSearchUrl = "https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&hash=";
    const string KugouLyricUrl = "https://lyrics.kugou.com/download?ver=1&client=pc&fmt=lrc&charset=utf8&id=";
    const string KugouArtistSeparator = "、";
    const string AppleSearchUrl = "https://itunes.apple.com/search?entity=song&limit=10&country=ru&term=";
    const string PaxsenixLyricUrl = "https://lyrics.paxsenix.org/apple-music/lyrics?id=";
    const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    const double MillisecondsPerSecond = 1000;
    const int VersionsPerCatalogue = 3;
    const int FewestLines = 4;

    static readonly Regex Credit = new(
        @"^(作词|作曲|编曲|制作人|监制|词|曲|lyrics by|lyricist|composer|composed by|written by|produced by|producer|arranged by)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex Markup = new(@"<[^>]+>", RegexOptions.Compiled);

    static Task<Candidate[]> FromDeezer(string title, string artist)
    {
        Task<string>? pass = null;
        return FromCatalogue(title, artist, SearchDeezer, async hit => await DeezerLyric(hit, await (pass ??= DeezerPass())));
    }

    static Task<Candidate[]> FromNetEase(string title, string artist) => FromCatalogue(title, artist, SearchNetEase, NetEaseLyric);

    static Task<Candidate[]> FromQqMusic(string title, string artist) => FromCatalogue(title, artist, SearchQqMusic, QqMusicLyric);

    static Task<Candidate[]> FromKugou(string title, string artist) => FromCatalogue(title, artist, SearchKugou, KugouLyric);

    static Task<Candidate[]> FromPaxsenix(string title, string artist) => FromCatalogue(title, artist, SearchApple, PaxsenixLyric);

    static async Task<Candidate[]> FromCatalogue(string title, string artist, Func<string, Task<Hit[]>> search, Func<Hit, Task<string>> lyric)
    {
        foreach (Guess guess in Guesses(title, artist))
        {
            Hit[] hits = await search(Uri.EscapeDataString(guess.By + " " + guess.Song));
            Hit[] fitting = hits.Where(hit => Fits(hit, guess)).Take(VersionsPerCatalogue).ToArray();
            Candidate?[] found = await Task.WhenAll(fitting.Select(async hit => Timed(WithoutCredits(await lyric(hit), hit), hit.Duration)));
            Candidate[] usable = found.OfType<Candidate>().Where(candidate => candidate.Lines.Length >= FewestLines).ToArray();
            if (usable.Length > 0) return usable;
        }
        return [];
    }

    static IEnumerable<Guess> Guesses(string title, string artist)
    {
        (string song, string by) = Cleaned(title, artist);
        if (by.Length > 0) yield return new Guess(song, by);

        int dash = song.IndexOf(TitleSeparator, StringComparison.Ordinal);
        if (dash > 0) yield return new Guess(song[(dash + TitleSeparator.Length)..].Trim(), song[..dash].Trim());
    }

    static bool Fits(Hit hit, Guess guess)
    {
        if (Plain(Noise.Replace(hit.Title, "")) != Plain(guess.Song)) return false;

        string by = Plain(guess.By);
        return hit.Artists.Select(Plain).Any(name => name.Length > 0 && (by.Contains(name) || name.Contains(by)));
    }

    static string Plain(string words) =>
        new(words.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Select(letter => letter == 'ё' ? 'е' : letter).ToArray());

    static string WithoutCredits(string lrc, Hit hit)
    {
        string heading = Plain(hit.Title);
        return string.Join('\n', lrc.Split('\n').Where(raw =>
        {
            Match stamped = Stamped.Match(raw.Trim());
            if (!stamped.Success) return true;

            string text = stamped.Groups[2].Value.Trim();
            return !Credit.IsMatch(text) && !(heading.Length > 0 && text.Contains(TitleSeparator) && Plain(text).StartsWith(heading, StringComparison.Ordinal));
        }));
    }

    static async Task<Hit[]> SearchDeezer(string query)
    {
        using JsonDocument? json = await AskAsync(DeezerSearchUrl + query, DeezerSite);
        return Items(json, "data")
            .Select(song => new Hit(Text(song, "id"), Text(song, "title"),
                song.TryGetProperty("artist", out JsonElement by) ? [Text(by, "name")] : [], Number(song, "duration")))
            .ToArray();
    }

    static async Task<string> DeezerPass()
    {
        using JsonDocument? json = await AskAsync(DeezerPassUrl, DeezerSite);
        return json == null ? "" : Text(json.RootElement, "jwt");
    }

    static async Task<string> DeezerLyric(Hit hit, string pass)
    {
        if (pass.Length == 0) return "";

        string body = JsonSerializer.Serialize(new { operationName = "TrackLyrics", variables = new { trackId = hit.Id }, query = DeezerLyricQuery });
        using JsonDocument? json = await AskAsync(DeezerLyricUrl, DeezerSite, pass, body);
        return string.Join('\n', Items(json, "data", "track", "lyrics", "synchronizedLines").Select(line => Text(line, "lrcTimestamp") + Text(line, "line")));
    }

    static async Task<Hit[]> SearchNetEase(string query)
    {
        using JsonDocument? json = await AskAsync(NetEaseSearchUrl + query, NetEaseSite);
        return Items(json, "result", "songs")
            .Select(song => new Hit(Text(song, "id"), Text(song, "name"), Items(song, "ar").Select(by => Text(by, "name")).ToArray(),
                Number(song, "dt") / MillisecondsPerSecond))
            .ToArray();
    }

    static async Task<string> NetEaseLyric(Hit hit)
    {
        using JsonDocument? json = await AskAsync(NetEaseLyricUrl + hit.Id, NetEaseSite);
        return json != null && json.RootElement.TryGetProperty("lrc", out JsonElement lrc) ? Text(lrc, "lyric") : "";
    }

    static async Task<Hit[]> SearchQqMusic(string query)
    {
        using JsonDocument? json = await AskAsync(QqMusicSearchUrl + query, QqMusicSite);
        return Items(json, "data", "song", "list")
            .Select(song => new Hit(Text(song, "songmid"), Text(song, "songname"), Items(song, "singer").Select(by => Text(by, "name")).ToArray(),
                Number(song, "interval")))
            .ToArray();
    }

    static async Task<string> QqMusicLyric(Hit hit)
    {
        using JsonDocument? json = await AskAsync(QqMusicLyricUrl + hit.Id, QqMusicSite);
        return json == null ? "" : WebUtility.HtmlDecode(Decoded(Text(json.RootElement, "lyric")));
    }

    static async Task<Hit[]> SearchKugou(string query)
    {
        using JsonDocument? json = await AskAsync(KugouSearchUrl + query, KugouSite);
        return Items(json, "data", "lists")
            .Select(song => new Hit(Text(song, "FileHash"), Markup.Replace(Text(song, "SongName"), ""),
                Markup.Replace(Text(song, "SingerName"), "").Split(KugouArtistSeparator), Number(song, "Duration")))
            .ToArray();
    }

    static async Task<string> KugouLyric(Hit hit)
    {
        using JsonDocument? versions = await AskAsync(KugouLyricSearchUrl + hit.Id, KugouSite);
        JsonElement version = Items(versions, "candidates").FirstOrDefault();
        if (version.ValueKind != JsonValueKind.Object) return "";

        string address = KugouLyricUrl + Uri.EscapeDataString(Text(version, "id")) + "&accesskey=" + Uri.EscapeDataString(Text(version, "accesskey"));
        using JsonDocument? json = await AskAsync(address, KugouSite);
        return json == null ? "" : Decoded(Text(json.RootElement, "content"));
    }

    static async Task<Hit[]> SearchApple(string query)
    {
        using JsonDocument? json = await AskAsync(AppleSearchUrl + query);
        return Items(json, "results")
            .Select(song => new Hit(Text(song, "trackId"), Text(song, "trackName"), [Text(song, "artistName")],
                Number(song, "trackTimeMillis") / MillisecondsPerSecond))
            .ToArray();
    }

    static async Task<string> PaxsenixLyric(Hit hit)
    {
        using JsonDocument? json = await AskAsync(PaxsenixLyricUrl + hit.Id);
        return json == null ? "" : Text(json.RootElement, "lrc");
    }

    static string Decoded(string base64)
    {
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)).TrimStart('﻿'); }
        catch (FormatException) { return ""; }
    }

    static IEnumerable<JsonElement> Items(JsonDocument? json, params string[] path) => json == null ? [] : Items(json.RootElement, path).ToArray();

    static IEnumerable<JsonElement> Items(JsonElement from, params string[] path)
    {
        foreach (string name in path)
            if (from.ValueKind != JsonValueKind.Object || !from.TryGetProperty(name, out from)) return [];
        return from.ValueKind == JsonValueKind.Array ? from.EnumerateArray() : [];
    }

    static string Text(JsonElement from, string name) =>
        from.ValueKind != JsonValueKind.Object || !from.TryGetProperty(name, out JsonElement value) ? ""
        : value.ValueKind == JsonValueKind.String ? value.GetString()!
        : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : "";

    static double Number(JsonElement from, string name) =>
        from.ValueKind == JsonValueKind.Object && from.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble() : 0;
}
