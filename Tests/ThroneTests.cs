using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DynamicIsland;

static partial class Program
{
    static JsonElement ThroneJson(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    static void Reject(Action action, string name)
    {
        bool rejected = false; try { action(); } catch { rejected = true; } Check(rejected, name);
    }
    static async Task RejectAsync(Func<Task> action, string name)
    {
        bool rejected = false; try { await action(); } catch { rejected = true; } Check(rejected, name);
    }

    static int ThroneTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            ThroneVectors(); ThroneModelTests();
            ThroneTransportTests().GetAwaiter().GetResult();
            ThroneDownloadTests(output).GetAwaiter().GetResult();
            ThroneWindowTests(output);
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, liveVpnChanged = false }));
            Console.WriteLine($"PASS: {_checks} Throne checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void ThroneVectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Throne-v1.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            byte[] key = ThroneWire.Key(c.GetProperty("key").GetString()!);
            byte[] server = Convert.FromHexString(c.GetProperty("server_hello").GetString()!);
            byte[] client = Convert.FromHexString(c.GetProperty("client_hello").GetString()!)[1..];
            byte[] sending = ThroneWire.Derive(key, server, client, true), receiving = ThroneWire.Derive(key, server, client, false);
            Check(Convert.ToHexString(sending).Equals(c.GetProperty("k_c2s").GetString(), StringComparison.OrdinalIgnoreCase), "official c2s key vector");
            Check(Convert.ToHexString(receiving).Equals(c.GetProperty("k_s2c").GetString(), StringComparison.OrdinalIgnoreCase), "official s2c key vector");
            foreach (var frame in c.GetProperty("frames").EnumerateArray())
            {
                byte[] k = frame.GetProperty("dir").GetString() == "c2s" ? sending : receiving;
                ulong counter = frame.GetProperty("counter").GetUInt64();
                byte[] plain = Encoding.UTF8.GetBytes(frame.GetProperty("plaintext").GetString()!), wire = Convert.FromHexString(frame.GetProperty("wire").GetString()!);
                Check(ThroneWire.Seal(k, counter, plain).SequenceEqual(wire), "official byte-exact AEAD seal vector");
                Check(ThroneWire.Open(k, counter, wire).SequenceEqual(plain), "official byte-exact AEAD open vector");
            }
            foreach (var invalid in c.TryGetProperty("invalid", out var invalids) ? invalids.EnumerateArray().ToArray() : [])
                Reject(() => ThroneWire.Open(invalid.GetProperty("dir").GetString() == "c2s" ? sending : receiving,
                    invalid.GetProperty("counter").GetUInt64(), Convert.FromHexString(invalid.GetProperty("wire").GetString()!)), "reject official invalid frame: " + invalid.GetProperty("name").GetString());
        }
        foreach (string key in new[] { "", "key", new string('A', 42), new string('A', 44), new string('!', 43), new string('A', 42) + "B" })
            Reject(() => ThroneWire.Key(key), "reject malformed/noncanonical key");
        string valid = new('A', 43);
        foreach (string address in new[] { "0.0.0.0", "192.168.1.10", "github.com", "8.8.8.8" })
            Reject(() => new ThroneEndpoint(address, 9095, valid), "credentials never sent to a non-loopback endpoint");
        using var endpoint = new ThroneEndpoint("::1", 9095, valid);
        Check(endpoint.ToString() == "::1:9095" && !endpoint.ToString().Contains(valid), "endpoint formatting redacts its secret");
    }

    static void ThroneModelTests()
    {
        Check(!MenuLayout.Default().Entries.Single(e => e.Id == "throne").Visible, "Throne hidden by default without compatible installation");
        MenuLayout.ThroneDefaultVisible = true;
        Check(MenuLayout.Default().Entries.Single(e => e.Id == "throne").Visible, "compatible beta seeds visible menu default");
        Check(!MenuLayout.Parse(MenuLayout.Default().SetVisible("throne", false).Serialize()).Entries.Single(e => e.Id == "throne").Visible, "explicit hide survives compatible-beta detection");
        MenuLayout.ThroneDefaultVisible = false;
        Check(MenuLayout.Parse(MenuLayout.Default().SetVisible("throne", true).Serialize()).Entries.Single(e => e.Id == "throne").Visible, "manual enable is respected on an old or absent Throne");
        var automatic = MenuLayout.Parse(MenuLayout.Default().Move("obs", 3).Serialize());
        Check(automatic.DetectThrone(true).Entries.Single(e => e.Id == "throne").Visible && !automatic.DetectThrone(false).Entries.Single(e => e.Id == "throne").Visible, "automatic visibility adapts to later beta installation without resetting custom order");
        Check(!automatic.SetVisible("throne", false).DetectThrone(true).Entries.Single(e => e.Id == "throne").Visible && automatic.SetVisible("throne", true).DetectThrone(false).Entries.Single(e => e.Id == "throne").Visible, "detection never overwrites explicit show or hide");
        var profiles = new[] { new ThroneProfile(1, 1, "Failed", -1, "vless", false), new ThroneProfile(2, 1, "Untested", 0, "vless", false), new ThroneProfile(3, 1, "Fast", 25, "vless", false), new ThroneProfile(4, 1, "Slow", 100, "vless", false), new ThroneProfile(5, 1, "Broken TUN", -2, "vless", false) };
        Check(ThroneData.Sorted(profiles, true).Select(p => p.Id).SequenceEqual([3, 4, 2, 5, 1]), "latency sort puts working first, unknown next, failed last");
        Check(ThroneData.Sorted(profiles, false).SequenceEqual(profiles), "original Throne server order remains available");
        Check(ThroneData.LatencyText(-2) == "Недоступен" && ThroneData.LatencyText(0) == "—", "test failures are not displayed as negative ping");
        Check(!ThroneStatus.Parse(ThroneJson("{\"state\":\"connecting\",\"profile\":null,\"last_profile_id\":1}")).Running, "connecting response is not successful connection");
        Check(ThronePreferences.Parse("broken") == new ThronePreferences(), "broken preferences recover without changing Throne");
        var pref = new ThronePreferences(@"C:\sample", @"C:\sample\Throne.exe", 7, 9, true);
        Check(ThronePreferences.Parse(pref.Serialize()) == pref, "selection and sorting survive restart without a stored API secret");
    }

    static async Task ThroneTransportTests()
    {
        foreach (string mode in new[] { "valid", "error", "bad-tag", "oversize", "short", "cancel" })
        {
            var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            string secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            using var endpoint = new ThroneEndpoint("127.0.0.1", port, secret);
            bool requestVerified = false;
            var server = Task.Run(async () =>
            {
                using var accepted = await listener.AcceptTcpClientAsync(); using var stream = accepted.GetStream();
                if (mode == "cancel") { await Task.Delay(250); return; }
                byte[] s = RandomNumberGenerator.GetBytes(16), hello = new byte[17];
                await stream.WriteAsync(s); await stream.ReadExactlyAsync(hello);
                byte[] header = new byte[4]; await stream.ReadExactlyAsync(header);
                int size = (int)BinaryPrimitives.ReadUInt32BigEndian(header); byte[] frame = new byte[size + 4]; header.CopyTo(frame, 0); await stream.ReadExactlyAsync(frame.AsMemory(4));
                byte[] request = ThroneWire.Open(ThroneWire.Derive(endpoint.Secret, s, hello[1..], true), 0, frame);
                using var json = JsonDocument.Parse(request); requestVerified = json.RootElement.GetProperty("path").GetString() == "/v1/status" && hello[0] == 1 && !json.RootElement.TryGetProperty("query", out _);
                if (mode == "oversize") { BinaryPrimitives.WriteUInt32BigEndian(header, ThroneWire.MaxResponse + 1); await stream.WriteAsync(header); return; }
                if (mode == "short") { await stream.WriteAsync(new byte[] { 0, 0 }); return; }
                string text = mode == "error" ? "{\"status\":409,\"error\":{\"code\":\"busy\",\"message\":\"secret server detail\"}}" : "{\"status\":200,\"body\":{\"state\":\"idle\",\"profile\":null,\"last_profile_id\":-1}}";
                byte[] reply = ThroneWire.Seal(ThroneWire.Derive(endpoint.Secret, s, hello[1..], false), 0, Encoding.UTF8.GetBytes(text));
                if (mode == "bad-tag") reply[^1] ^= 1;
                // Deliberate fragmentation proves ReadExactly, not one Read.
                foreach (byte b in reply) await stream.WriteAsync(new[] { b });
            });
            try
            {
                var api = new ThroneApi(endpoint); using var cancel = new CancellationTokenSource(); if (mode == "cancel") cancel.CancelAfter(80);
                if (mode == "valid") Check((await api.CallAsync("GET", "/v1/status")).GetProperty("state").GetString() == "idle", "real encrypted TCP exchange with fragmented hello and frame");
                else await RejectAsync(() => api.CallAsync("GET", "/v1/status", cancel: cancel.Token), "TCP rejects " + mode);
                await server;
                if (mode != "cancel") Check(requestVerified, "mock server authenticated request: " + mode);
            }
            finally { listener.Stop(); }
        }
    }

    static string ThroneReleaseJson(byte[] bytes, string tag, bool draft = false, string? digest = null, string? url = null, string? name = null) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease = tag.Contains("beta"), assets = new[] { new { name = name ?? $"Throne-{tag}-windows-universal-installer.exe", size = bytes.Length,
            digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)), browser_download_url = url ?? $"https://github.com/throneproj/Throne/releases/download/{tag}/Throne-{tag}-windows-universal-installer.exe" } }
    });
    static async Task ThroneDownloadTests(string output)
    {
        byte[] bytes = UpdateBytes();
        var json = ThroneJson("[" + string.Join(',', new[] { ThroneReleaseJson(bytes, "1.3.3"), ThroneReleaseJson(bytes, "1.4.0-beta.2"), ThroneReleaseJson(bytes, "1.4.0-beta.4"), ThroneReleaseJson(bytes, "9.0.0", draft: true) }) + "]");
        Check(ThroneInstaller.Select(json).Version == "1.4.0-beta.4", "release discovery includes beta, excludes draft and pre-API versions");
        string name = "Throne-1.4.0-beta.4-windows-universal-installer.exe", sha = Convert.ToHexString(SHA256.HashData(bytes));
        string assetsHtml = $"<li><a href=\"/throneproj/Throne/releases/download/1.4.0-beta.4/{name}\">{name}</a><clipboard-copy value=\"sha256:{sha}\"></clipboard-copy></li>";
        Check(ThroneInstaller.HtmlTags("<a href=\"/throneproj/Throne/releases/tag/1.4.0-beta.1\"></a><a href=\"/throneproj/Throne/releases/tag/1.4.0-beta.4\"></a>").SequenceEqual(["1.4.0-beta.4"]), "public HTML fallback rejects pre-API beta");
        Check(ThroneInstaller.HtmlAsset("1.4.0-beta.4", assetsHtml)?.Sha256 == sha, "HTML checksum bound to exact installer asset row");
        Check(ThroneInstaller.HtmlAsset("1.4.0-beta.4", assetsHtml.Replace("<li>", "<li></li>").Replace("</a>", "</a></li><li>")) == null, "checksum from another asset row is rejected");
        using (var fallbackHandler = new UpdateHttp((request, _) => Task.FromResult(request.RequestUri!.Host == "api.github.com"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden) : JsonReply(request.RequestUri.AbsolutePath.EndsWith("releases") ? "<a href=\"/throneproj/Throne/releases/tag/1.4.0-beta.4\"></a>" : assetsHtml))))
        {
            Check((await new ThroneInstaller(new HttpClient(fallbackHandler)).LatestAsync(default)).Sha256 == sha, "rate-limited API falls back to official HTML without an account or token");
        }
        Check(ThroneInstaller.Select(ThroneJson("[" + ThroneReleaseJson(bytes, "1.4.0-beta.4") + "," + ThroneReleaseJson(bytes, "1.4.0") + "]")).Version == "1.4.0", "stable supersedes beta with the same numeric version");
        foreach (string bad in new[] { ThroneReleaseJson(bytes, "1.4.0-beta.1"), ThroneReleaseJson(bytes, "1.4.0-beta.4", digest: ""), ThroneReleaseJson(bytes, "1.4.0-beta.4", url: "https://example.org/installer.exe"), ThroneReleaseJson(bytes, "1.4.0-beta.4", name: "portable.zip") })
            Reject(() => ThroneInstaller.Select(ThroneJson("[" + bad + "]")), "reject old/unverified/wrong-origin/wrong-asset installer");
        Check(Path.IsPathFullyQualified(ThroneInstaller.Downloads()) && Directory.Exists(ThroneInstaller.Downloads()), "Downloads resolved through Windows Known Folder");
        foreach (string mode in new[] { "valid", "hash", "truncated", "oversized", "not-exe", "cancel" })
        {
            string root = Path.Combine(output, "download-" + mode); Directory.CreateDirectory(root);
            byte[] supplied = bytes.ToArray(); if (mode == "hash") supplied[^1] ^= 1;
            if (mode == "truncated") supplied = supplied[..500]; if (mode == "oversized") supplied = [.. supplied, 5]; if (mode == "not-exe") supplied[0] = 0;
            var release = ThroneInstaller.Select(ThroneJson("[" + ThroneReleaseJson(mode == "not-exe" ? supplied : bytes, "1.4.0-beta.4") + "]"));
            string protectedFile = Path.Combine(root, release.FileName); await File.WriteAllTextAsync(protectedFile, "USER FILE");
            using var handler = new UpdateHttp(async (request, token) =>
            {
                if (mode == "cancel") await Task.Delay(Timeout.Infinite, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(supplied) };
            });
            var installer = new ThroneInstaller(new HttpClient(handler)); using var cancel = new CancellationTokenSource(); if (mode == "cancel") cancel.CancelAfter(80);
            if (mode == "valid")
            {
                var file = await installer.DownloadAsync(release, root, null, cancel.Token);
                Check(file.Path != protectedFile && await ThroneInstaller.VerifyAsync(file, default), "verified download uses collision-free filename and awaits manual launch");
                await File.WriteAllTextAsync(file.Path, "tampered"); Check(!await ThroneInstaller.VerifyAsync(file, default), "installer rechecked at launch after tampering");
            }
            else await RejectAsync(() => installer.DownloadAsync(release, root, null, cancel.Token), "failed download rejected: " + mode);
            Check(await File.ReadAllTextAsync(protectedFile) == "USER FILE" && !Directory.EnumerateFiles(root, "*.part").Any(), "user file preserved and partial cleaned: " + mode);
        }
    }

    sealed class ThroneFakeApi : IThroneApi
    {
        public readonly List<(string Method, string Path, string Body)> Requests = [];
        public string State = "idle"; public int Profile = -1; public bool Fail; public int JobPolls; public bool JobCancelled;
        public Task<JsonElement> CallAsync(string method, string path, object? body = null, Dictionary<string, string>? query = null, CancellationToken cancel = default)
        {
            string text = JsonSerializer.Serialize(body); Requests.Add((method, path, text));
            if (Fail) throw new ThroneApiException("busy", 409);
            if (path == "/v1/tests" && method == "POST") { JobPolls = 0; JobCancelled = false; return Task.FromResult(ThroneJson("{\"id\":41,\"state\":\"running\"}")); }
            if (path == "/v1/tests/41")
            {
                if (method == "DELETE") JobCancelled = true;
                return Task.FromResult(ThroneJson(JsonSerializer.Serialize(new { id = 41, state = JobCancelled ? "cancelled" : JobPolls++ == 0 ? "running" : "done" })));
            }
            if (path == "/v1/profiles") return Task.FromResult(ThroneJson("{\"profiles\":[{\"id\":11,\"group\":1,\"name\":\"DE Frankfurt\",\"latency_ms\":85,\"type\":\"vless\",\"running\":false},{\"id\":12,\"group\":1,\"name\":\"NL Amsterdam\",\"latency_ms\":25,\"type\":\"vless\",\"running\":false},{\"id\":21,\"group\":2,\"name\":\"FI Helsinki\",\"latency_ms\":30,\"type\":\"vless\",\"running\":false}]}"));
            if (method == "PUT") { Profile = ThroneJson(text).GetProperty("profile").GetInt32(); State = "connecting"; }
            if (method == "DELETE") { State = "stopping"; }
            return Task.FromResult(ThroneJson(JsonSerializer.Serialize(new { state = State, profile = Profile < 0 ? null : new { id = Profile, name = "Server " + Profile }, last_profile_id = 11 })));
        }
    }
    static void ThroneWindowTests(string output)
    {
        var window = ToolTipWindow(false); var root = (FrameworkElement)window.Content; window.Show(); Pump(120);
        var panel = typeof(MainWindow).GetNestedType("Panel", System.Reflection.BindingFlags.NonPublic)!;
        void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panel, name)); Pump(700); window.UpdateLayout(); }
        void Capture(string name)
        {
            Call(window, "UpdateTargets");
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape");
            void Invalidate(DependencyObject node)
            {
                if (node is UIElement element) element.InvalidateVisual();
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) Invalidate(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
            Invalidate(root); root.UpdateLayout(); Pump(150); Save(root, Path.Combine(output, name));
        }
        void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Complete(Task task) { while (!task.IsCompleted) Pump(10); task.GetAwaiter().GetResult(); }
        var fake = new ThroneFakeApi();
        Set(window, "_throneApi", fake); Set(window, "_throneInstallation", new ThroneDiscoveryResult(null, null, true, true, null, null));
        Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => true));
        Set(window, "_thronePreferences", new ThronePreferences(Group: 1, Profile: 11));
        Set(window, "_throneGroups", new[] { new ThroneGroup(1, "Subscription One", true, 3), new ThroneGroup(2, "Subscription Two", true, 1) });
        Set(window, "_throneProfiles", new[] { new ThroneProfile(11, 1, "DE Frankfurt", 85, "vless", false), new ThroneProfile(12, 1, "NL Amsterdam", 25, "vless", false), new ThroneProfile(13, 1, "Unavailable", -1, "vless", false), new ThroneProfile(21, 2, "FI Helsinki", 30, "vless", false) });
        Complete((Task)Call(window, "RefreshThroneAsync", false)!); Page("Throne");
        Check(((TextBlock)window.FindName("ThroneState")).Text == "Выключен" && ((Button)window.FindName("ThroneAction")).IsEnabled, "idle state offers enabled play control");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "healthy API hides both manual recovery pickers");
        Capture("throne-off.png");
        Click("ThroneAction");
        Check(fake.Requests.Last().Method == "PUT" && fake.Requests.Last().Body.Contains("11"), "play starts specifically selected profile");
        Check(!((Button)window.FindName("ThroneAction")).IsEnabled && ((TextBlock)window.FindName("ThroneState")).Text == "Подключаю…", "202 connecting disables repeat clicks without false success");
        fake.State = "running"; Complete((Task)Call(window, "RefreshThroneAsync", false)!);
        Check(((TextBlock)window.FindName("ThroneState")).Text == "Включён", "only authoritative running poll shows enabled connection");
        Capture("throne-on.png");
        Page("ThroneServers"); Call(window, "FillThroneServers");
        ((Button)((StackPanel)window.FindName("ThroneServerRows")).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(fake.Profile == 12 && fake.Requests.Last().Method == "PUT", "server choice switches the running VPN via API");
        fake.State = "running"; Complete((Task)Call(window, "RefreshThroneAsync", false)!); Click("ThroneAction");
        Check(fake.Requests.Last().Method == "DELETE" && fake.Requests.Last().Path == "/v1/connection", "pause stops only the API's connection, not another VPN or process");
        fake.State = "idle"; Complete((Task)Call(window, "RefreshThroneAsync", false)!); Page("ThroneServers"); Call(window, "FillThroneServers");
        int mutations = fake.Requests.Count(r => r.Method != "GET");
        ((Button)((StackPanel)window.FindName("ThroneServerRows")).Children[0]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(fake.Requests.Count(r => r.Method != "GET") == mutations, "offline server selection does not enable VPN");
        Page("ThroneServers"); Click("ThroneSort");
        Check((int)((Button)((StackPanel)window.FindName("ThroneServerRows")).Children[0]).Tag == 12, "server UI sorted by actual latency");
        Capture("throne-servers.png");
        ((TextBox)window.FindName("ThroneServerFilter")).Text = "Frank";
        Check(((StackPanel)window.FindName("ThroneServerRows")).Children.Count == 1, "server filtering remains inside selected group");
        Click("ThronePing");
        Check(fake.Requests.Last().Path == "/v1/tests" && fake.Requests.Last().Body.Contains("latency"), "ping invokes Throne URL latency job for selected group");
        Check(!((Button)window.FindName("ThroneAction")).IsEnabled, "no connection changes while the latency test owns controls");
        while ((bool)typeof(MainWindow).GetField("_throneTesting", Private)!.GetValue(window)!) Pump(20);
        Check(fake.JobPolls == 2 && ((Button)window.FindName("ThroneSort")).Content.ToString()!.Contains("пингу"), "job is polled to completion and latency ordering enabled");
        Click("ThronePing"); Click("ThronePing");
        while ((bool)typeof(MainWindow).GetField("_throneTesting", Private)!.GetValue(window)!) Pump(20);
        Check(fake.JobCancelled && fake.Requests.Any(r => r.Method == "DELETE" && r.Path == "/v1/tests/41"), "cancel touches only the latency job created by this island");
        mutations = fake.Requests.Count(r => r.Method != "GET");
        Page("ThroneGroups"); Call(window, "FillThroneGroups"); Capture("throne-groups.png");
        ((Button)((StackPanel)window.FindName("ThroneGroupRows")).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(((TextBlock)window.FindName("ThroneGroupText")).Text == "Subscription Two" && fake.Requests.Count(r => r.Method != "GET") == mutations, "group selection only changes browsing and next-start preference");
        fake.Fail = true; Page("Throne"); Click("ThroneAction");
        Check(((TextBlock)window.FindName("ThroneNote")).Text.Contains("занят") && ((TextBlock)window.FindName("ThroneState")).Text == "Выключен", "API failure leaves confirmed state intact with short error");
        Set(window, "_throneApi", null!); Set(window, "_throneStatus", null!); Set(window, "_throneMessage", ""); Set(window, "_throneInstallation", new ThroneDiscoveryResult(null, null, false, false, null, null)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneUpdate")).Visibility == Visibility.Visible, "old or absent client offers latest installer");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Visible && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "missing executable offers only the executable picker");
        Capture("throne-update.png");
        const string foundExe = @"C:\Throne-fixture\Throne.exe", foundData = @"C:\Throne-fixture";
        Set(window, "_throneReadError", false);
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(foundExe, foundData, true, false, null, null)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneUpdate")).Visibility == Visibility.Collapsed && ((TextBlock)window.FindName("ThroneSetupText")).Text.Contains("API"), "installed beta with disabled API gets setup guide rather than false update requirement");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "found installation with API disabled does not suggest replacing paths");
        Capture("throne-api-setup.png");
        Set(window, "_throneReadError", true); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "old connection error does not expose folder picker when API is disabled");
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(foundExe, foundData, true, true, null, null)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Visible && ((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed,
            "failed connection exposes folder recovery without a redundant executable picker");
        Set(window, "_throneLoading", true); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed,
            "manual controls remain hidden while detection or reconnection is pending");
        Set(window, "_throneLoading", false); Set(window, "_throneReadError", false);
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(foundExe, foundData, true, true, null, "Обнови ключ API в настройках Throne")); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Visible,
            "invalid discovered API credentials retain manual configuration recovery");
        Capture("throne-config-recovery.png");
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(foundExe, null, true, false, null, null)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Visible,
            "missing configuration retains manual data-directory recovery");
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(foundExe, foundData, false, false, null, null)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "found legacy installation offers update rather than unnecessary path selection");
        Set(window, "_throneInstallation", null!); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "initial unknown state does not flash manual controls before discovery");
        Set(window, "_throneReadError", true); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Visible && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "failed discovery with no installation retains executable recovery");
        Set(window, "_throneApi", fake);
        Set(window, "_throneStatus", new ThroneStatus("idle", -1, "", 11)); Call(window, "SyncThrone");
        Check(((Button)window.FindName("ThroneBrowse")).Visibility == Visibility.Collapsed && ((Button)window.FindName("ThroneDataFolder")).Visibility == Visibility.Collapsed,
            "successful reconnection automatically hides both manual controls again");
        ThroneInstallerWindowTests(window, output, Capture);
    }

    static void ThroneInstallerWindowTests(MainWindow window, string output, Action<string> capture)
    {
        void Sync() => Call(window, "SyncThrone");
        bool Offered() => ((FrameworkElement)window.FindName("ThroneInstallOffer")).Visibility == Visibility.Visible;
        void Complete(Task task)
        {
            var watch = Stopwatch.StartNew();
            while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(5)) Pump(10);
            Check(task.IsCompleted, "installer fixture completes without waiting for a real setup process");
            task.GetAwaiter().GetResult();
        }
        string directory = Path.GetFullPath(Path.Combine(output, "installer launch fixture")); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Throne setup.exe");
        byte[] bytes = UpdateBytes(); File.WriteAllBytes(path, bytes);
        var installer = new ThroneInstallerFile(path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)));
        var legacy = new ThroneDiscoveryResult(path, directory, false, false, null, null);
        var compatible = new ThroneDiscoveryResult(path, directory, true, false, null, null);
        bool upgraded = false; int discoveries = 0, launches = 0;
        Set(window, "_findThrone", (Func<ThronePreferences, ThroneDiscoveryResult>)(_ => { discoveries++; return upgraded ? compatible : legacy; }));
        Set(window, "_throneApi", null!); Set(window, "_throneStatus", null!); Set(window, "_throneReadError", false);
        Set(window, "_throneInstallation", legacy); Set(window, "_throneInstallerFile", installer); Sync();
        Check(Offered(), "verified downloaded installer is offered for the legacy client");
        capture("throne-installer-offer.png");
        Set(window, "_thronePolledAt", Environment.TickCount64);
        Call(window, "PollThrone");
        Check(discoveries == 0, "recovery discovery respects its throttle rather than scanning on every UI tick");
        upgraded = true; Set(window, "_thronePolledAt", Environment.TickCount64 - 6000); Call(window, "PollThrone");
        Complete((Task)typeof(MainWindow).GetField("_throneRefreshTask", Private)!.GetValue(window)!);
        Check(discoveries == 1 && !Offered(), "visible page rediscovers a new install and removes the whole installer block without restarting");
        capture("throne-installer-hidden.png");
        Check(((Button)window.FindName("ThroneUpdate")).Visibility == Visibility.Collapsed
            && ((TextBlock)window.FindName("ThroneSetupText")).Text.Contains("API"), "compatible installation with disabled API gets setup guidance, not another installer");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "hiding the installer offer leaves the file in Downloads untouched");
        var healthy = new ThroneFakeApi();
        Set(window, "_throneApi", healthy); Set(window, "_thronePolledAt", Environment.TickCount64 - 3000); Call(window, "PollThrone");
        Complete((Task)typeof(MainWindow).GetField("_throneRefreshTask", Private)!.GetValue(window)!);
        Check(discoveries == 1 && healthy.Requests.Count == 1 && healthy.Requests[0].Path == "/v1/status",
            "healthy API polling reads only status, without scanning installations or loading subscriptions");
        Set(window, "_throneApi", null!); Set(window, "_throneStatus", null!);
        Set(window, "_runThroneInstaller", (Func<ProcessStartInfo, CancellationToken, Task>)((_, _) => { launches++; return Task.CompletedTask; }));
        Complete((Task)Call(window, "RunThroneInstallerAsync")!);
        Check(launches == 0, "stale installer click cannot launch setup after compatible installation is detected");

        upgraded = false; Set(window, "_throneInstallation", legacy); Sync();
        Set(window, "_runThroneInstaller", (Func<ProcessStartInfo, CancellationToken, Task>)((_, _) => throw new System.ComponentModel.Win32Exception(1223)));
        Complete((Task)Call(window, "RunThroneInstallerAsync")!);
        Check(Offered() && ((Button)window.FindName("ThroneRunInstaller")).IsEnabled
            && ((TextBlock)window.FindName("ThroneNote")).Text == "Запуск отменён", "declining UAC leaves a retryable installer offer, not false installed state");
        Set(window, "_runThroneInstaller", (Func<ProcessStartInfo, CancellationToken, Task>)((_, _) => throw new IOException("fixture launch failure")));
        Complete((Task)Call(window, "RunThroneInstallerAsync")!);
        Check(Offered() && ((Button)window.FindName("ThroneRunInstaller")).IsEnabled, "launch failure does not strand a disabled installer button");

        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProcessStartInfo? captured = null;
        Set(window, "_runThroneInstaller", (Func<ProcessStartInfo, CancellationToken, Task>)((start, _) => { captured = start; launches++; return exited.Task; }));
        var installing = (Task)Call(window, "RunThroneInstallerAsync")!;
        var watch = Stopwatch.StartNew(); while (captured == null && !installing.IsCompleted && watch.ElapsedMilliseconds < 3000) Pump(10);
        Check(captured?.FileName == path && captured.UseShellExecute && captured.Verb == "runas"
            && captured.WorkingDirectory == directory && captured.Arguments.Length == 0, "verified exact path, including spaces, launches through the shell with explicit administrator verb");
        Check(!installing.IsCompleted && !((Button)window.FindName("ThroneRunInstaller")).IsEnabled, "launch remains pending until installer exits without blocking WPF");
        Complete((Task)Call(window, "RunThroneInstallerAsync")!);
        Check(launches == 1, "repeated click never starts a second concurrent installer");
        int before = discoveries; exited.SetResult(); Complete(installing);
        Check(discoveries == before + 1 && Offered(), "installer exit alone does not fabricate installation success");

        upgraded = true; before = discoveries;
        var stalePoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Set(window, "_throneRefreshTask", stalePoll.Task);
        installing = (Task)Call(window, "RunThroneInstallerAsync")!; Pump(100);
        Check(!installing.IsCompleted && discoveries == before, "installer completion waits out an older poll before requesting fresh discovery");
        stalePoll.SetResult(); Complete(installing);
        Check(discoveries == before + 1 && !Offered(), "installer completion triggers authoritative discovery and hides the offer immediately after a successful install");
        Check(File.ReadAllBytes(path).SequenceEqual(bytes), "successful install detection neither deletes nor rewrites the downloaded installer");

        upgraded = false; Set(window, "_throneInstallation", legacy); Sync();
        File.WriteAllText(path, "tampered"); before = launches;
        Complete((Task)Call(window, "RunThroneInstallerAsync")!);
        Check(launches == before && !Offered() && ((TextBlock)window.FindName("ThroneNote")).Text.Contains("изменился"), "tampered download never reaches the elevated launcher");

        File.WriteAllBytes(path, bytes); Set(window, "_throneInstallerFile", installer); Sync();
        Set(window, "_runThroneInstaller", (Func<ProcessStartInfo, CancellationToken, Task>)((_, cancel) => Task.Delay(Timeout.Infinite, cancel)));
        installing = (Task)Call(window, "RunThroneInstallerAsync")!; Pump(100);
        Check(!installing.IsCompleted, "close fixture has an actual pending installer lifetime wait");
        window.Close(); Complete(installing);
        Check(File.Exists(path) && !(bool)typeof(MainWindow).GetField("_throneInstalling", Private)!.GetValue(window)!, "closing the island cancels only its wait and leaves the installer file intact");
    }

    static int ThroneReadOnly(string output)
    {
        try
        {
            var found = ThroneDiscovery.Find(Settings.Throne);
            string result = JsonSerializer.Serialize(new { found.Executable, found.DataDirectory, found.Capable, found.Enabled, endpointFound = found.Endpoint != null, found.Error, windowsBuild = Environment.OSVersion.Version.Build });
            found.Endpoint?.Dispose(); File.WriteAllText(output, result); Console.WriteLine(result); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task<int> ThroneInstallerNetwork(string output)
    {
        try
        {
            Directory.CreateDirectory(output); var downloader = new ThroneInstaller();
            var release = await downloader.LatestAsync(default);
            Console.WriteLine("Latest: " + release.Version + " / " + release.FileName + " / bytes=" + release.Size);
            var file = await downloader.DownloadAsync(release, output, null, default);
            Check(await ThroneInstaller.VerifyAsync(file, default), "live official installer digest and complete download verified; NOT launched");
            File.WriteAllText(Path.Combine(output, "network-result.json"), JsonSerializer.Serialize(new { release, file, launched = false }));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task<int> ThroneFixtureProbe(int port, string keyFile, string output)
    {
        try
        {
            using var endpoint = new ThroneEndpoint("127.0.0.1", port, File.ReadAllText(keyFile));
            var api = new ThroneApi(endpoint);
            var status = await api.CallAsync("GET", "/v1/status");
            Check(status.GetProperty("capabilities").GetProperty("headless").GetBoolean() && status.GetProperty("state").GetString() == "idle"
                && !status.GetProperty("tun").GetBoolean() && !status.GetProperty("system_proxy").GetBoolean(), "isolated real-beta fixture is idle, headless, without TUN or system proxy");
            var groups = ThroneData.Groups(await api.CallAsync("GET", "/v1/groups"));
            var profiles = ThroneData.Profiles(await api.CallAsync("GET", "/v1/profiles"));
            Check(profiles.Length == 0, "fixture has empty independent data, not user's profiles");
            // Mutation scope is the disposable, authenticated fixture only.
            var group = await api.CallAsync("POST", "/v1/groups", new { name = "Island integration fixture" }); int id = group.GetProperty("group").GetProperty("id").GetInt32();
            var imported = await api.CallAsync("POST", "/v1/import", new { @as = "profiles", text = "socks://127.0.0.1:9#Fixture-A\nsocks://127.0.0.1:10#Fixture-B", group = id });
            int profile = imported.GetProperty("profile_ids")[0].GetInt32();
            profiles = ThroneData.Profiles(await api.CallAsync("GET", "/v1/profiles"));
            Check(profiles.Length == 2, "real API imports two loopback-only fixture profiles into separate group");
            var started = ThroneStatus.Parse(await api.CallAsync("PUT", "/v1/connection", new { profile, wait = true, timeout = 15 }));
            Check(started.Running && started.ProfileId == profile, "real beta API starts selected proxy profile without changing user's VPN");
            var stopped = ThroneStatus.Parse(await api.CallAsync("DELETE", "/v1/connection", new { wait = true, timeout = 15 }));
            Check(stopped.State == "idle", "real beta API confirms stopped connection");
            var job = await api.CallAsync("POST", "/v1/tests", new { kind = "latency", group = id, wait = false }); int jobId = job.GetProperty("id").GetInt32();
            var deadline = DateTime.UtcNow.AddSeconds(40);
            while (job.GetProperty("state").GetString() == "running" && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500); job = await api.CallAsync("GET", $"/v1/tests/{jobId}");
            }
            if (job.GetProperty("state").GetString() == "running") job = await api.CallAsync("DELETE", $"/v1/tests/{jobId}");
            Check(job.GetProperty("state").GetString() != "running", "real latency job completes or acknowledges targeted cancellation");
            await api.CallAsync("DELETE", $"/v1/groups/{id}");
            Check(ThroneData.Profiles(await api.CallAsync("GET", "/v1/profiles")).Length == 0, "fixture profiles cleaned up");
            File.WriteAllText(output, JsonSerializer.Serialize(new { checks = _checks, passed = true, version = status.GetProperty("version").GetString(), realUserVpnTouched = false }));
            try { await api.CallAsync("POST", "/v1/shutdown", new { wait = false }); } catch (EndOfStreamException) { }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
