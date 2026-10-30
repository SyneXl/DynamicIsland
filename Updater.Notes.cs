using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace DynamicIsland;

sealed partial class Updater
{
    internal const string ReleasesUrl = "https://api.github.com/repos/" + Repository + "/releases";
    internal const string ReleasesFeed = "https://github.com/" + Repository + "/releases.atom";
    readonly ReleaseNotesStore _notesStore;
    Task? _notesTask;
    string _lastNotesAttempt = "";
    public ReleaseNotesSnapshot? ReleaseNotes { get; private set; }
    public bool NotesLoading { get; private set; }
    public string NotesError { get; private set; } = "";
    string NotesContextKey => (State is Stage.Available or Stage.Loading && LatestVersion > _current ? _current.ToString() : ReleaseNotes?.From ?? "")
        + ">" + (State is Stage.Available or Stage.Loading && LatestVersion > _current ? LatestVersion : _current);
    public bool NeedsReleaseNotesLoad => !_lifetime.IsCancellationRequested && !NotesLoading && ReleaseNotes?.Complete != true
        && State is Stage.Idle or Stage.Available or Stage.Latest or Stage.NoRelease && _lastNotesAttempt != NotesContextKey;

    void SelectReleaseNotes(bool available)
    {
        NotesError = "";
        if (available && LatestVersion is { } latest && latest > _current)
            ReleaseNotes = _notesStore.Available(_current, latest)
                ?? ReleaseNotesSnapshot.Create(_current, latest, [new(latest.ToString(), Notes)], false);
        else ReleaseNotes = _notesStore.Installed(_current) ?? BundledReleaseNotes.For(null, _current)
                ?? (LatestVersion == _current ? ReleaseNotesSnapshot.Create(null, _current, [new(_current.ToString(), Notes)], true) : null);
    }

    public void ReloadSavedReleaseNotes()
    {
        _notesStore.Reload(); SelectReleaseNotes(State is Stage.Available or Stage.Loading);
    }

    public Task LoadReleaseNotesAsync(bool force = false)
    {
        if (_lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (_notesTask is { IsCompleted: false }) return _notesTask;
        ReloadSavedReleaseNotes();
        if (!force && ReleaseNotes?.Complete == true) return Task.CompletedTask;
        return _notesTask = LoadReleaseNotesCoreAsync();
    }

    async Task LoadReleaseNotesCoreAsync()
    {
        bool available = State is Stage.Available or Stage.Loading && LatestVersion > _current;
        Version to = available ? LatestVersion! : _current;
        Version? from = available ? _current : ReleaseNotesSnapshot.TryVersion(ReleaseNotes?.From, out var original) ? original : null;
        var collected = new Dictionary<Version, ReleaseNote>();
        void Add(ReleaseNote note) { if (ReleaseNotesSnapshot.TryVersion(note.Version, out var v)) collected[v] = note; }
        foreach (var note in ReleaseNotes?.Releases ?? []) Add(note);
        _lastNotesAttempt = NotesContextKey;
        NotesLoading = true; NotesError = ""; Changed?.Invoke();
        bool complete = false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            if (DateTimeOffset.UtcNow >= _apiRetryAt)
            {
                try
                {
                    using var api = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    api.CancelAfter(TimeSpan.FromSeconds(3));
                    complete = await ReadNotesApiAsync(Add, to, api.Token);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or InvalidOperationException)
                { if (!_lifetime.IsCancellationRequested) _apiRetryAt = DateTimeOffset.UtcNow.AddMinutes(1); }
            }
            if (!complete) complete = await ReadNotesFeedAsync(Add, from, to, timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or XmlException or OperationCanceledException or InvalidOperationException)
        { if (!_lifetime.IsCancellationRequested) NotesError = "Не все описания удалось загрузить"; }
        finally
        {
            NotesLoading = false;
            // A check for another version may finish while history is loading.
            // Never let that late request overwrite the new context or its cache.
            bool sameContext = available == (State is Stage.Available or Stage.Loading && LatestVersion > _current)
                && to == (available ? LatestVersion : _current);
            if (!_lifetime.IsCancellationRequested && sameContext)
            {
                var result = ReleaseNotesSnapshot.Create(from, to, collected.Values, complete);
                if (result.Valid())
                {
                    if (ReleaseNotes is not { Complete: true } || result.Complete) ReleaseNotes = result;
                    bool saved = available ? _notesStore.SaveAvailable(ReleaseNotes!) : _notesStore.SaveInstalled(ReleaseNotes!);
                    if (!saved) NotesError = "Не удалось сохранить список изменений";
                    else if (ReleaseNotes?.Complete == true) NotesError = "";
                }
                else NotesError = "Не удалось загрузить описания изменений";
            }
            if (!_lifetime.IsCancellationRequested) Changed?.Invoke();
        }
    }

    async Task<bool> ReadNotesApiAsync(Action<ReleaseNote> add, Version target, CancellationToken cancel)
    {
        bool sawTarget = false;
        // Release order is publication time, not semantic version order. Read all
        // pages before filtering; do not stop at the first low/backdated version.
        for (int page = 1; page <= 10; page++)
        {
            using var response = await _http.GetAsync(ReleasesUrl + "?per_page=100&page=" + page, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
            { _apiRetryAt = RetryAfter(response); return false; }
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { } final && (final is not { Scheme: "https", Host: "api.github.com" }
                || final.AbsolutePath != "/repos/" + Repository + "/releases")) throw new InvalidDataException("Foreign releases list.");
            using var doc = JsonDocument.Parse(await ReadNotesBodyAsync(response, cancel));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid releases list.");
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
                    || release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                if (!release.TryGetProperty("tag_name", out var tag) || !ReleaseNotesSnapshot.TryVersion(tag.GetString(), out var version)) continue;
                if (version == target) sawTarget = true;
                add(new(version.ToString(), ParseNotes(release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "")));
            }
            if (doc.RootElement.GetArrayLength() < 100) return sawTarget;
        }
        return false; // Bounded history must never masquerade as exhaustive.
    }

    async Task<bool> ReadNotesFeedAsync(Action<ReleaseNote> add, Version? from, Version target, CancellationToken cancel)
    {
        string cursor = ""; var seen = new HashSet<string>(StringComparer.Ordinal); bool sawTarget = false;
        for (int page = 0; page < 20; page++)
        {
            using var response = await GetPageAsync(ReleasesFeed + (cursor.Length == 0 ? "" : "?after=" + Uri.EscapeDataString(cursor)), cancel);
            if (response.RequestMessage?.RequestUri is not { Scheme: "https", Host: "github.com" } uri
                || uri.AbsolutePath != "/" + Repository + "/releases.atom") throw new InvalidDataException("Foreign releases feed.");
            string text = await ReadNotesBodyAsync(response, cancel);
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
                { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2_000_000 });
            var xml = XDocument.Load(reader); XNamespace ns = "http://www.w3.org/2005/Atom";
            if (xml.Root?.Name != ns + "feed") throw new InvalidDataException("Invalid releases feed.");
            var entries = xml.Root.Elements(ns + "entry").ToArray();
            if (entries.Length == 0) return sawTarget;
            string next = "";
            foreach (var entry in entries)
            {
                string? url = entry.Elements(ns + "link").FirstOrDefault(e => (string?)e.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value;
                string prefix = "/" + Repository + "/releases/tag/";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var link) || link is not { Scheme: "https", Host: "github.com" }
                    || !link.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || link.Query.Length != 0 || link.Fragment.Length != 0) continue;
                string tag = Uri.UnescapeDataString(link.AbsolutePath[prefix.Length..]);
                if (!Regex.IsMatch(tag, @"\A[A-Za-z0-9._+\-]{1,100}\z", RegexOptions.CultureInvariant)) continue;
                next = tag;
                if (!ReleaseNotesSnapshot.TryVersion(tag, out var version)) continue;
                if (version > target || (from == null ? version != target : version <= from)) continue;
                // GitHub's Atom feed also contains annotated Git tags which were
                // never published as releases. A tag/title/body is not evidence.
                if (version != target && !BundledReleaseNotes.IsPublished(version)
                    && !await HasPublishedNotesAssetAsync(tag, cancel)) continue;
                if (version == target) sawTarget = true;
                string html = entry.Element(ns + "content")?.Value ?? "";
                add(new(version.ToString(), ParseHtmlNotes(html)));
            }
            if (next.Length == 0 || !seen.Add(next)) return false;
            cursor = next;
        }
        return false;
    }

    async Task<bool> HasPublishedNotesAssetAsync(string tag, CancellationToken cancel)
    {
        using var page = await GetPageAsync(ReleaseAssetsPage + Uri.EscapeDataString(tag), cancel);
        if (page.StatusCode == HttpStatusCode.NotFound) return false;
        if (page.RequestMessage?.RequestUri is not { Scheme: "https", Host: "github.com" } uri
            || uri.AbsolutePath != "/" + Repository + "/releases/expanded_assets/" + Uri.EscapeDataString(tag))
            throw new InvalidDataException("Foreign historical release assets.");
        string html = await ReadNotesBodyAsync(page, cancel);
        string expected = "href=\"/" + Repository + "/releases/download/" + tag + "/" + AssetName + "\"";
        return Regex.Matches(html, @"<li\b[^>]*>[\s\S]*?</li>", RegexOptions.CultureInvariant).Any(row => row.Value.Contains(expected, StringComparison.Ordinal)
            && Regex.Matches(row.Value, "value=\"sha256:[a-fA-F0-9]{64}\"", RegexOptions.CultureInvariant).Count == 1);
    }

    internal static string[] ParseHtmlNotes(string html) => ParseNotes(string.Join('\n',
        Regex.Matches(html, @"<li\b[^>]*>([\s\S]*?)</li>", RegexOptions.CultureInvariant)
            .Select(m => "- " + WebUtility.HtmlDecode(Regex.Replace(m.Groups[1].Value, "<[^>]*>", "")).Trim())));

    static async Task<string> ReadNotesBodyAsync(HttpResponseMessage response, CancellationToken cancel)
    {
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 2_000_000) throw new InvalidDataException("Notes are too large.");
        using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var bytes = new MemoryStream(); byte[] buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancel)) != 0)
        {
            if (bytes.Length + count > 2_000_000) throw new InvalidDataException("Notes are too large.");
            bytes.Write(buffer, 0, count);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }
}
