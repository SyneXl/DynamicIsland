using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using DynamicIsland;

static partial class Program
{
    static object NotesRelease(string version, string body, bool draft = false, bool prerelease = false) => new { tag_name = "v" + version, body, draft, prerelease };
    static string NotesFeed(params (string Tag, string Html)[] entries)
    {
        XNamespace ns = "http://www.w3.org/2005/Atom";
        return new XDocument(new XElement(ns + "feed", entries.Select(e => new XElement(ns + "entry",
            new XElement(ns + "link", new XAttribute("rel", "alternate"), new XAttribute("href", $"https://github.com/{Updater.Repository}/releases/tag/{e.Tag}")),
            new XElement(ns + "content", new XAttribute("type", "html"), e.Html))))).ToString();
    }
    static int ReleaseNotesTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            string run = Path.Combine(output, "run-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
            ReleaseNotesModelTests(run);
            ReleaseNotesNetworkTests(run).GetAwaiter().GetResult();
            ReleaseNotesWindowTests(output);
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, liveInstall = false, productionCacheWrites = false }));
            Console.WriteLine($"PASS: {_checks} release notes checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void ReleaseNotesModelTests(string output)
    {
        var from = new Version(1, 29, 0); var to = new Version(1, 32, 0);
        var snapshot = ReleaseNotesSnapshot.Create(from, to, [new("1.30.0", ["First"]), new("1.32.0", ["Third"]), new("1.31.0", ["Second"]), new("1.29.0", ["Already installed"]), new("2.0.0", ["Future"])], true);
        Check(snapshot.Valid() && snapshot.Releases.Select(r => r.Version).SequenceEqual(["1.32.0", "1.31.0", "1.30.0"]), "three-version jump excludes installed/future notes and groups in descending semantic order");
        foreach (var invalid in new[] { snapshot with { Repository = "other/repo" }, snapshot with { From = "9.0.0" }, snapshot with { To = "1.32.0-beta" }, snapshot with { Releases = [] }, snapshot with { Releases = [new("1.31.0", ["No target"])] }, snapshot with { Releases = [snapshot.Releases[0], snapshot.Releases[0]] }, snapshot with { Releases = [new("1.32.0", [new string('x', 1001)])] } })
            Check(!invalid.Valid(), "invalid/cross-channel journal is rejected");
        var numeric = ReleaseNotesSnapshot.Create(new Version(1, 2, 0), new Version(1, 2, 10), [new("1.2.9", []), new("1.2.10", [])], true);
        Check(numeric.Releases[0].Version == "1.2.10", "versions sort numerically, not lexically");
        string file = Path.Combine(output, "history.json"); var store = new ReleaseNotesStore(file);
        Check(store.SaveAvailable(snapshot) && store.Installed(to) == null, "checking/caching available history never marks an update installed");
        Check(store.SaveAvailable(snapshot with { Complete = false }) && new ReleaseNotesStore(file).Available(from, to)?.Complete == true, "failed refresh cannot replace complete cache with partial history");
        Check(store.SaveInstalled(snapshot), "confirmed installed snapshot is saved");
        var reload = new ReleaseNotesStore(file);
        Check(reload.Installed(to)?.Releases.Length == 3 && reload.Available(from, to)?.Releases.Length == 3, "restart retains installed range and separate available cache");
        Check(reload.Installed(new Version(1, 31, 0)) == null, "rollback/another version cannot display a newer version's installed history");
        var bundle = BundledReleaseNotes.For(new Version(1, 29, 0), Updater.CurrentVersion);
        Check(bundle?.Valid() == true && bundle.Complete && bundle.Releases.Length >= 3, "payload bundles the fork's release archive for offline migration from older updaters");
        Check(bundle!.Releases.All(r => r.Version is not ("1.29.0" or "1.31.0")), "annotated tags without published releases are excluded from bundled history");
        Check(BundledReleaseNotes.For(null, Updater.CurrentVersion)?.Releases.Length == 1, "fresh install shows its release, not an invented upgrade range");
        Check(BundledReleaseNotes.For(null, new Version(99, 0, 0)) == null, "bundled notes never claim coverage of a future payload");
        File.WriteAllText(Path.Combine(output, "corrupt.json"), "{\"Schema\":1,");
        Check(new ReleaseNotesStore(Path.Combine(output, "corrupt.json")).Installed(to) == null, "truncated cache does not crash startup");
        string blocked = Path.Combine(output, "not-a-directory"); File.WriteAllText(blocked, "fixture");
        Check(!new ReleaseNotesStore(Path.Combine(blocked, "history.json")).SaveInstalled(snapshot), "disk error is reported without breaking executable installation");
    }

    static async Task ReleaseNotesNetworkTests(string output)
    {
        byte[] bytes = UpdateBytes(); var from = new Version(1, 29, 0); var to = new Version(1, 32, 0);
        int lists = 0; var requests = new List<string>();
        var first = Enumerable.Range(0, 99).Select(i => NotesRelease("1.0." + i, "- Old")).Append(NotesRelease("1.32.0", "- Third")).ToArray();
        var second = new[] { NotesRelease("1.30.0", "- First"), NotesRelease("1.31.0", "- Second"), NotesRelease("1.31.1", "- Private", draft: true), NotesRelease("1.31.2", "- Preview", prerelease: true), NotesRelease("1.32.1-beta.1", "- Beta") };
        using var handler = new UpdateHttp((request, _) =>
        {
            string url = request.RequestUri!.AbsoluteUri; requests.Add(url);
            if (url == Updater.LatestReleaseUrl) return Task.FromResult(JsonReply(ReleaseJson(bytes, tag: "v1.32.0")));
            lists++; return Task.FromResult(JsonReply(JsonSerializer.Serialize(url.EndsWith("page=1") ? first : second)));
        });
        string cache = Path.Combine(output, "api-history.json");
        using (var updater = new Updater(new HttpClient(handler), from, notesStore: new ReleaseNotesStore(cache)))
        {
            await updater.CheckAsync(); Check(lists == 0, "background version check does not download the whole changelog");
            await updater.LoadReleaseNotesAsync();
            Check(lists == 2 && updater.ReleaseNotes is { Complete: true, Releases.Length: 3 }, "history pagination continues past low/backdated releases and excludes drafts/prereleases");
            Check(updater.ReleaseNotes!.Releases.Select(r => r.Lines.Single()).SequenceEqual(["Third", "Second", "First"]), "range contains all three published descriptions, not only latest body");
            await updater.LoadReleaseNotesAsync(); Check(lists == 2, "reopening cached complete history makes no extra requests");
            Check(requests.All(u => u.StartsWith("https://api.github.com/repos/" + Updater.Repository + "/", StringComparison.Ordinal)), "history remains on configured fork channel");
        }
        var persisted = new ReleaseNotesStore(cache); persisted.SaveInstalled(persisted.Available(from, to)!);
        using (var offline = new Updater(new HttpClient(new UpdateHttp((_, _) => throw new HttpRequestException("Offline"))), to, notesStore: new ReleaseNotesStore(cache)))
        {
            Check(offline.ReleaseNotes?.From == "1.29.0" && offline.ReleaseNotes.Releases.Length == 3, "installed range is immediately readable offline after restart");
            await offline.CheckAsync();
            Check(offline.State == Updater.Stage.Failed && offline.ReleaseNotes?.Releases.Length == 3, "offline version check does not erase installed descriptions");
        }
        int historyApi = 0, feedPages = 0, unpublishedChecks = 0;
        using var feedHandler = new UpdateHttp((request, _) =>
        {
            string url = request.RequestUri!.AbsoluteUri;
            if (url == Updater.LatestReleaseUrl) return Task.FromResult(JsonReply(ReleaseJson(bytes, tag: "v1.32.0")));
            if (url.StartsWith(Updater.ReleasesUrl + "?")) { historyApi++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
            if (url.StartsWith(Updater.ReleaseAssetsPage)) { unpublishedChecks++; return Task.FromResult(UpdatePage(url, "<li>Source code only</li>")); }
            feedPages++;
            string xml = !url.Contains('?') ? NotesFeed(("v1.32.0", "<ul><li>Третье &amp; новое</li></ul>"), ("v1.32.1-beta.1", "<ul><li>Beta</li></ul>"), ("v1.31.0", "<ul><li>Unpublished tag</li></ul>"), ("v1.31.1", "<ul><li>Второе</li></ul>"))
                : url.EndsWith("v1.31.1") ? NotesFeed(("v1.30.0", "<ul><li>Первое</li></ul>"), ("v1.29.0", "<ul><li>Old</li></ul>")) : NotesFeed();
            return Task.FromResult(UpdatePage(url, xml));
        });
        using (var updater = new Updater(new HttpClient(feedHandler), from))
        {
            await updater.CheckAsync(); await updater.LoadReleaseNotesAsync();
            Check(historyApi == 1 && feedPages == 3 && unpublishedChecks == 1 && updater.ReleaseNotes is { Complete: true, Releases.Length: 3 }, "rate-limited API falls back to cursor-paginated public feed, excluding Git-only tags");
            Check(updater.ReleaseNotes!.Releases[0].Lines.Single() == "Третье & новое", "feed HTML is decoded as plain text");
            await updater.LoadReleaseNotesAsync(true); Check(historyApi == 1, "history retry also honors API cooldown");
        }
        foreach (string mode in new[] { "offline", "repeated-cursor", "foreign", "dtd" })
        {
            using var badHandler = new UpdateHttp((request, _) =>
            {
                string url = request.RequestUri!.AbsoluteUri;
                if (url == Updater.LatestReleaseUrl) return Task.FromResult(JsonReply(ReleaseJson(bytes, tag: "v1.32.0")));
                if (url.StartsWith(Updater.ReleasesUrl + "?")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
                if (mode == "offline") throw new HttpRequestException("offline fixture");
                string xml = mode == "dtd" ? "<!DOCTYPE feed [<!ENTITY data SYSTEM 'file:///C:/must-not-be-read.txt'>]><feed xmlns='http://www.w3.org/2005/Atom'>&data;</feed>"
                    : NotesFeed(("v1.32.0", "<ul><li>Known</li></ul>"));
                return Task.FromResult(UpdatePage(mode == "foreign" ? "https://example.org/releases.atom" : url, xml));
            });
            using var updater = new Updater(new HttpClient(badHandler), from);
            await updater.CheckAsync(); await updater.LoadReleaseNotesAsync();
            Check(updater.State == Updater.Stage.Available && updater.ReleaseNotes is { Complete: false } && !updater.NotesLoading,
                "incomplete/untrusted history is labelled partial without losing verified update: " + mode);
            Check(!updater.NeedsReleaseNotesLoad, "same failed context does not auto-retry in a refresh loop: " + mode);
        }
        VerifiedUpdate? received = null; string stage = Path.Combine(output, "download"); Directory.CreateDirectory(stage);
        using var installHandler = new UpdateHttp((request, _) => Task.FromResult(request.RequestUri!.AbsoluteUri == Updater.LatestReleaseUrl
            ? JsonReply(ReleaseJson(bytes, tag: "v1.32.0")) : request.RequestUri.AbsoluteUri.Contains("/releases/download/")
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) } : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var installStore = new ReleaseNotesStore(Path.Combine(output, "not-installed.json"));
        using (var updater = new Updater(new HttpClient(installHandler), from, Path.Combine(output, "untouched.exe"), u => received = u, () => stage, notesStore: installStore))
        {
            await updater.CheckAsync(); Check(await updater.InstallAsync() && received?.Notes is { Complete: false }, "history outage cannot block a verified executable download/handoff");
            Check(installStore.Installed(to) == null, "download/handoff alone is not a successful installation journal entry");
        }
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously); int version = 32;
        using var raceHandler = new UpdateHttp((request, _) => request.RequestUri!.AbsoluteUri == Updater.LatestReleaseUrl
            ? Task.FromResult(JsonReply(ReleaseJson(bytes, tag: $"v1.{version}.0"))) : pending.Task);
        using (var updater = new Updater(new HttpClient(raceHandler), from))
        {
            await updater.CheckAsync(); Task load = updater.LoadReleaseNotesAsync();
            Check(ReferenceEquals(load, updater.LoadReleaseNotesAsync()), "simultaneous history requests coalesce");
            version = 33; await updater.CheckAsync(true);
            pending.SetResult(JsonReply(JsonSerializer.Serialize(new[] { NotesRelease("1.32.0", "- Old context") }))); await load;
            Check(updater.ReleaseNotes?.To == "1.33.0" && !updater.NotesLoading, "late history response cannot overwrite a newer update context");
            Check(updater.NeedsReleaseNotesLoad, "new target remains eligible for its own history load after the stale task completes");
        }
    }

    static void ReleaseNotesWindowTests(string output)
    {
        var window = ToolTipWindow(false); window.Show(); Pump(100);
        var root = (FrameworkElement)window.Content;
        var panel = typeof(MainWindow).GetNestedType("Panel", System.Reflection.BindingFlags.NonPublic)!;
        void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panel, name)); Pump(300); }
        void Capture(string name)
        {
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape");
            foreach (var node in ToolTipVisuals(root).OfType<UIElement>()) { node.CacheMode = null; node.InvalidateVisual(); }
            root.UpdateLayout(); Pump(100); Save(root, Path.Combine(output, name));
        }
        try
        {
            string path = Path.Combine(output, "ui-history.json"); var store = new ReleaseNotesStore(path);
            var snapshot = BundledReleaseNotes.For(new Version(1, 29, 0), Updater.CurrentVersion)!; store.SaveInstalled(snapshot);
            using var updater = new Updater(new HttpClient(new UpdateHttp((_, _) => throw new Exception("Offline UI must use saved notes"))), notesStore: store);
            Set(window, "_updater", updater); updater.Changed += () => Call(window, "RefreshUpdatePage");
            Call(window, "RefreshUpdatePage"); Page("Update");
            var update = (FrameworkElement)window.FindName("UpdatePage"); var row = (Button)window.FindName("UpdateWhatsNewButton");
            Check(row.Visibility == Visibility.Visible && row.IsEnabled && update.IsAncestorOf(row), "update section always exposes a separate What's New row, including offline");
            Check(!update.IsAncestorOf((DependencyObject)window.FindName("UpdateNotes")) && update.Height < 260, "update page stays compact without inline history");
            Capture("update-page.png"); row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(350);
            Check(Call(window, "TargetView")!.ToString() == "ReleaseNotes", "What's New opens its own child page");
            Check(((TextBlock)window.FindName("ReleaseNotesRange")).Text == "1.29.0 → " + Updater.CurrentVersion, "installed transition survives restart and is shown clearly");
            string text = ((TextBlock)window.FindName("UpdateNotesText")).Text;
            Check(snapshot.Releases.All(r => text.Contains(r.Version)) && !text.Contains("1.29.0"), "child page includes every skipped release but not the installed base");
            Check((double)typeof(MainWindow).GetProperty("UpdateNotesOverflow", Private)!.GetValue(window)! > 0, "long multi-release history has a real bounded scroll range");
            Check(((FrameworkElement)window.FindName("ReleaseNotesPage")).Height <= 460 && ((FrameworkElement)window.FindName("ReleaseNotesMessage")).Visibility == Visibility.Collapsed,
                "complete cached history fits island height without redundant status copy");
            Capture("whats-new-installed.png"); Call(window, "ScrollUpdateNotes", 100);
            var scroll = (Spring)typeof(MainWindow).GetField("_updateNotesScroll", Private)!.GetValue(window)!; scroll.Snap(scroll.Target); Call(window, "ApplyShape");
            Check(((TranslateTransform)window.FindName("UpdateNotesMove")).Y < 0, "wheel scrolls the new page instead of changing system volume");
            Capture("whats-new-scrolled.png"); Call(window, "ReleaseNotesBack_Click", row, new RoutedEventArgs());
            Check(Call(window, "TargetView")!.ToString() == "Update", "back returns specifically to Update, not Settings or main menu");
        }
        finally { window.Close(); }
    }

    sealed class ReadOnlyNotesHttp : DelegatingHandler
    {
        public readonly List<string> Requests = [];
        public bool SimulateHistoryApiLimit;
        public ReadOnlyNotesHttp() : base(new HttpClientHandler()) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (request.Method != HttpMethod.Get) throw new Exception("Read-only probe attempted mutation");
            Requests.Add(request.Method + " " + request.RequestUri);
            if (SimulateHistoryApiLimit && request.RequestUri!.AbsoluteUri.StartsWith(Updater.ReleasesUrl + "?"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { RequestMessage = request });
            return base.SendAsync(request, cancel);
        }
    }
    static async Task<int> ReleaseNotesReadOnly(string output)
    {
        try
        {
            Directory.CreateDirectory(output); using var handler = new ReadOnlyNotesHttp(); using var http = new HttpClient(handler);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland-notes-readonly/1.0");
            var from = new Version(1, 29, 0);
            using var updater = new Updater(http, from, handoff: _ => throw new Exception("Read-only probe cannot install"), notesStore: new ReleaseNotesStore());
            await updater.CheckAsync(true); await updater.LoadReleaseNotesAsync();
            Check(updater.State == Updater.Stage.Available, "real public channel metadata remains verified");
            Check(updater.ReleaseNotes is { Complete: true } && updater.ReleaseNotes.Releases.Length >= 3, "real public feed/list covers skipped releases through end of channel");
            Check(updater.ReleaseNotes!.Releases.All(r => Version.Parse(r.Version) > from && Version.Parse(r.Version) <= updater.LatestVersion), "live history stays inside exact installed-to-target range");
            string[] primary = updater.ReleaseNotes.Releases.Select(r => r.Version).ToArray();
            handler.SimulateHistoryApiLimit = true; await updater.LoadReleaseNotesAsync(true);
            Check(updater.ReleaseNotes is { Complete: true } && updater.ReleaseNotes.Releases.Select(r => r.Version).SequenceEqual(primary),
                "forced API-limit fallback matches actual published history and excludes annotated-only tags");
            Check(updater.ReleaseNotes!.Releases.All(r => r.Version != "1.31.0"), "real unpublished 1.31.0 tag is not presented as a release");
            Check(handler.Requests.All(r => r.StartsWith("GET ")) && updater.DownloadedBytes == 0, "live probe makes only read requests, no executable download or install");
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, updater.LatestVersion, snapshot = updater.ReleaseNotes, requests = handler.Requests, installed = false, productionCacheWrites = false }));
            Console.WriteLine($"PASS: {_checks} live release notes checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
