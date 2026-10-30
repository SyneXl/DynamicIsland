using System.Reflection;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynamicIsland;

static partial class Program
{
    static void ThroneHomeTests(MainWindow window, string output, Action<string> capture)
    {
        CountryFlagTests();
        void Complete(Task task)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && watch.ElapsedMilliseconds < 5000) Pump(10);
            Check(task.IsCompleted, "Throne home fixture does not stall WPF"); task.GetAwaiter().GetResult();
        }
        var panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panel, name)); Pump(400); }
        T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        ThronePreferences Prefs() => Field<ThronePreferences>("_thronePreferences");
        void Poll() => Complete((Task)Call(window, "RefreshThroneAsync", false)!);
        string Text(string name) => ((TextBlock)window.FindName(name)).Text;
        var rows = (StackPanel)window.FindName("ThroneServerRows");
        bool Checked(int id) => rows.Children.OfType<Button>().Where(b => (int)b.Tag == id)
            .Any(b => ((Grid)b.Content).Children.OfType<StackPanel>().Any(s => s.Children.OfType<Icon>().Any(i => i.Kind == Glyph.Check)));
        var fake = new ThroneFakeApi { State = "running", Profile = 11, ActiveName = "🇩🇪 Frankfurt" };
        ((TextBox)window.FindName("ThroneServerFilter")).Clear();
        int saves = 0;
        Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => { saves++; return true; }));
        Set(window, "_throneApi", fake); Set(window, "_throneReadError", false);
        Set(window, "_throneInstallation", new ThroneDiscoveryResult("Throne.exe", "fixture", true, true, null, null));
        Set(window, "_thronePreferences", new ThronePreferences(Group: 1, Profile: 12));
        Set(window, "_throneObservedRunning", false);
        Set(window, "_throneGroups", new[] { new ThroneGroup(1, "Subscription One", true, 2), new ThroneGroup(2, "Subscription Two", true, 1) });
        Set(window, "_throneProfiles", new[] { new ThroneProfile(11, 1, "🇩🇪 Frankfurt", 85, "vless", false), new ThroneProfile(12, 1, "🇳🇱 Amsterdam", 25, "vless", false), new ThroneProfile(21, 2, "🇫🇮 Ultima Free", 19, "vless", false) });
        Page("Throne"); Poll();
        Check(Prefs().Profile == 11 && Text("ThroneServerText") == "Frankfurt" && Text("ThroneActive") == "Frankfurt",
            "first confirmed running state replaces the old persisted server in both labels");
        Check(((CountryFlag)window.FindName("ThroneServerFlag")).Code == "DE" && Checked(11) && !Checked(12), "selected flag and checkmark follow the actual running server");
        int reads = fake.Requests.Count;
        fake.Profile = 12; fake.ActiveName = "🇳🇱 Amsterdam"; Poll();
        Check(Prefs().Profile == 12 && Text("ThroneServerText") == "Amsterdam" && Checked(12) && !Checked(11),
            "external same-group server change updates selector and checkmarks without reopening the page");
        Check(fake.Requests.Count == reads + 1 && saves == 0, "known external switch requires one status read and no settings writes");
        var stableRow = rows.Children[0]; Poll();
        Check(ReferenceEquals(rows.Children[0], stableRow), "unchanged heartbeats preserve row instances instead of rebuilding hovered controls");
        fake.Profile = 21; fake.ActiveName = "🇫🇮 Ultima Free"; Poll();
        Check(Prefs().Group == 2 && Prefs().Profile == 21 && Text("ThroneGroupText") == "Subscription Two" && Checked(21),
            "external cross-group switch follows the active subscription as well as its server");
        capture("throne-external-server.png");
        Set(window, "_thronePreferences", Prefs() with { Group = 1, Profile = 11 }); Call(window, "FillThroneServers"); Poll();
        Check(Prefs().Group == 1 && Prefs().Profile == 11 && !Checked(11), "same-server heartbeat does not undo deliberate browsing of another group or mark it as connected");
        fake.Profile = 12; fake.ActiveName = "🇳🇱 Amsterdam"; Poll();
        Check(Prefs().Profile == 12, "a later real external change overrides stale browsing selection");
        fake.State = "idle"; Set(window, "_thronePreferences", Prefs() with { Profile = 11 }); Poll();
        Check(Prefs().Profile == 11 && Text("ThroneServerText") == "Frankfurt", "stopped VPN preserves the user's next-start preference");
        fake.State = "connecting"; fake.Profile = 12; Poll();
        Check(Prefs().Profile == 11, "intermediate connecting status does not overwrite selection as confirmed success");
        fake.State = "running"; fake.Profile = 999; fake.ActiveName = "🇯🇵 New Tokyo";
        fake.ProfilesJson = "{\"profiles\":[{\"id\":999,\"group\":2,\"name\":\"🇯🇵 New Tokyo\",\"latency_ms\":50,\"type\":\"vless\",\"running\":true}]}";
        reads = fake.Requests.Count; Poll();
        Check(Prefs().Profile == 999 && Prefs().Group == 2 && Text("ThroneServerText") == "New Tokyo", "unknown active profile refreshes metadata and resolves its actual group");
        Check(fake.Requests.Skip(reads).Select(r => r.Path).SequenceEqual(["/v1/status", "/v1/groups", "/v1/profiles"]), "metadata is fetched only when the new active profile is absent from cache");
        reads = fake.Requests.Count; Poll(); Check(fake.Requests.Count == reads + 1, "resolved new profile goes back to lightweight status-only polling");
        Check(fake.Requests.All(r => r.Method == "GET"), "observing external switches never sends VPN connection commands");

        // Render the same flag-rich rows as the user's subscription screenshot.
        Set(window, "_throneProfiles", new[] { new ThroneProfile(999, 2, "🇫🇮 Ultima Free (YT no ads, no torrent)", 19, "vless", true),
            new ThroneProfile(31, 2, "🇵🇱 WHITE 1 BOSS MOUSER", 56, "vless", false), new ThroneProfile(32, 2, "🇵🇱 WHITE 2 BOSS SHREDDER", 56, "vless", false),
            new ThroneProfile(33, 2, "🇯🇵 Japan 2", 248, "vless", false), new ThroneProfile(34, 2, "🇯🇵 Japan 1", 254, "vless", false), new ThroneProfile(35, 2, "🇫🇮 Bridge FI (no torrent)", -1, "vless", false) });
        fake.ActiveName = "🇫🇮 Ultima Free (YT no ads, no torrent)"; Poll();
        Set(window, "_thronePreferences", Prefs() with { SortByLatency = true });
        Call(window, "SyncThrone");
        Page("ThroneServers"); Call(window, "FillThroneServers");
        Check(rows.Children.OfType<Button>().All(b => ((Grid)b.Content).Children.OfType<CountryFlag>().Any(f => f.Visibility == Visibility.Visible)), "each country-labelled server row contains a bundled color flag");
        capture("throne-color-flags.png");

        var home = (FrameworkElement)window.FindName("HomeThroneButton");
        ((FrameworkElement)window.FindName("InfoWeatherRow")).Visibility = Visibility.Visible;
        ((Digits)window.FindName("InfoWeather")).Text = "+9°"; ((Digits)window.FindName("InfoVol")).Text = "95%";
        Page("Player"); Call(window, "SyncHomeThrone");
        Check(Call(window, "TargetView")!.ToString() == "IdleBig" && home.Visibility == Visibility.Visible && Text("HomeThroneText") == "VPN вкл.", "expanded home shows a compact confirmed VPN status");
        bool Polling() => (bool)typeof(MainWindow).GetProperty("ShouldPollThrone", Private)!.GetValue(window)!;
        Check(Polling(), "visible home indicator enables bounded Throne polling");
        Set(window, "_hiddenByFullscreen", true); Check(!Polling(), "hidden fullscreen home does not poll Throne in the background"); Set(window, "_hiddenByFullscreen", false);
        Check(((FrameworkElement)window.FindName("IdleBigView")).Height == 124, "ordinary weather/volume/VPN home keeps the original size");
        capture("home-vpn-on.png");
        var date = (FrameworkElement)window.FindName("BigDate");
        Rect dateBounds = date.TransformToAncestor(window).TransformBounds(new Rect(date.RenderSize));
        Rect buttonBounds = home.TransformToAncestor(window).TransformBounds(new Rect(home.RenderSize));
        Check(!dateBounds.IntersectsWith(buttonBounds), "home VPN shortcut never covers the clock date");
        fake.State = "idle"; Poll(); Check(Text("HomeThroneText") == "VPN выкл.", "home distinguishes confirmed stopped VPN"); capture("home-vpn-off.png");
        fake.State = "connecting"; Poll(); Check(Text("HomeThroneText") == "VPN …", "home indicates transitions without claiming connected state");
        fake.Fail = true; Poll(); Check(Text("HomeThroneText") == "VPN —", "API failure is unknown status, never false switched-off or connected state");
        Page("None"); Page("Player"); capture("home-vpn-unavailable.png");
        fake.Fail = false; fake.State = "running"; Poll();
        Set(window, "_thronePolledAt", Environment.TickCount64 - 10000); Call(window, "SyncHomeThrone");
        Check(Text("HomeThroneText") == "VPN —", "stale cached status is not presented as live after reopening home"); Poll();
        ((FrameworkElement)window.FindName("InfoHeadsetRow")).Visibility = Visibility.Visible;
        ((FrameworkElement)window.FindName("InfoBatRow")).Visibility = Visibility.Visible;
        ((Digits)window.FindName("InfoHeadset")).Text = "80%"; ((Digits)window.FindName("InfoBat")).Text = "64%";
        Call(window, "SyncHomeThrone"); capture("home-vpn-all-indicators.png");
        var indicators = (FrameworkElement)window.FindName("HomeIndicators"); var idle = (FrameworkElement)window.FindName("IdleBigView");
        Check(idle.Height >= indicators.DesiredSize.Height + 28, "home grows when headset and battery indicators need space, rather than clipping VPN");
        ((FrameworkElement)window.FindName("InfoHeadsetRow")).Visibility = Visibility.Collapsed;
        ((FrameworkElement)window.FindName("InfoBatRow")).Visibility = Visibility.Collapsed; Call(window, "SyncHomeThrone");
        Check(idle.Height == 124, "home returns to its original height when extra indicators disappear");
        Page("Throne"); var toggle = (Button)window.FindName("ThroneHomeRow");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!Prefs().ShowOnHome && home.Visibility == Visibility.Collapsed && saves == 1, "home status toggle persists explicit opt-out immediately");
        Page("Player"); Check(!Polling(), "disabled home indicator does not keep VPN polling alive"); Page("Throne");
        Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => false)); toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!Prefs().ShowOnHome && home.Visibility == Visibility.Collapsed, "failed preference write does not pretend home status was enabled");
        Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => true)); toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(Prefs().ShowOnHome && home.Visibility == Visibility.Visible, "home shortcut can be re-enabled without changing the VPN connection");
        Set(window, "_findThrone", (Func<ThronePreferences, ThroneDiscoveryResult>)(_ => new("Throne.exe", "fixture", true, false, null, null)));
        Page("Player"); ((Button)home).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump(150);
        Check(Call(window, "TargetView")!.ToString() == "Throne", "home status click opens Throne, not a connection toggle");
        Check(fake.Requests.All(r => r.Method == "GET"), "home display, preference toggle and shortcut never start or stop the VPN");
        Set(window, "_throneInstallation", new ThroneDiscoveryResult(null, null, false, false, null, null)); Call(window, "SyncThrone");
        Check(home.Visibility == Visibility.Collapsed, "clients without compatible Throne do not get an empty home indicator");
        Page("None"); Check(!Polling(), "ordinary compact island does not run a permanent VPN polling loop");
        Page("Throne");
    }

    static void CountryFlagTests()
    {
        foreach ((string name, string? code, string label) in new[] { ("🇫🇮 Ultima Free", "FI", "Ultima Free"), ("🇵🇱 WHITE 1", "PL", "WHITE 1"),
            ("🇯🇵 Japan 2", "JP", "Japan 2"), ("DE Frankfurt", "DE", "Frankfurt"), ("[UK] London", "GB", "London"), ("  (🇳🇱) Amsterdam", "NL", "Amsterdam"),
            ("", (string?)null, ""), ("🇫", null, "🇫"), ("ZZ Unknown", null, "ZZ Unknown"), ("internet", null, "internet"), ("https://example.org", null, "https://example.org") })
        {
            var parsed = CountryFlags.Parse(name); Check(parsed.Code == code && parsed.Label == label, "country prefix parser preserves labels and rejects ambiguous input: " + name);
        }
        Check(CountryFlags.Image("../../outside") == null && CountryFlags.Image("http://example.org") == null, "flag lookups are restricted to bundled asset identifiers");
        foreach (string code in CountryFlags.Codes)
        {
            var image = CountryFlags.Image(code); Check(image is { PixelWidth: 100, IsFrozen: true }, "bundled flag decodes locally and is cached/frozen: " + code);
        }
        foreach (string code in new[] { "FI", "PL", "JP", "DE", "NL" })
        {
            var image = new FormatConvertedBitmap(CountryFlags.Image(code)!, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
            Check(Enumerable.Range(0, pixels.Length / 4).Any(i => Math.Max(pixels[i * 4], Math.Max(pixels[i * 4 + 1], pixels[i * 4 + 2])) - Math.Min(pixels[i * 4], Math.Min(pixels[i * 4 + 1], pixels[i * 4 + 2])) > 100), "flag contains real color, not Windows monochrome regional letters: " + code);
        }
    }

    static int ThroneHomeReadOnly(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
            typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
            typeof(Settings).GetField("_quietWhenIdle", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
            var found = ThroneDiscovery.Find(Settings.Throne);
            if (found.Endpoint == null) { found.Endpoint?.Dispose(); throw new Exception("No enabled local Throne API available for the read-only probe"); }
            var window = new MainWindow(); window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
            window.Show(); Pump(250);
            try
            {
                var reader = new ReadOnlyThroneApi(new ThroneApi(found.Endpoint));
                Set(window, "_throneInstallation", found); Set(window, "_throneApi", reader);
                Set(window, "_thronePreferences", Settings.Throne with { ShowOnHome = true });
                Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => throw new Exception("Unexpected settings write in read-only probe")));
                var refresh = (Task)Call(window, "RefreshThroneAsync", false)!;
                var watch = System.Diagnostics.Stopwatch.StartNew();
                while (!refresh.IsCompleted && watch.ElapsedMilliseconds < 45000) Pump(20);
                Check(refresh.IsCompleted, "live read-only API refresh completes"); refresh.GetAwaiter().GetResult();
                var status = (ThroneStatus?)typeof(MainWindow).GetField("_throneStatus", Private)!.GetValue(window);
                Check(status != null, "local Throne API returned authoritative status");
                var preferences = (ThronePreferences)typeof(MainWindow).GetField("_thronePreferences", Private)!.GetValue(window)!;
                Check(!status!.Running || preferences.Profile == status.ProfileId, "live confirmed profile and selector agree");
                var home = (TextBlock)window.FindName("HomeThroneText");
                Check(home.Text == (status.Running ? "VPN вкл." : status.Transitioning ? "VPN …" : status.State == "idle" ? "VPN выкл." : "VPN —"), "live home indicator reflects the API without mutating the connection");
                var flag = CountryFlags.Parse(status.ProfileName);
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, state = status.State,
                    activeId = status.ProfileId, selectedId = preferences.Profile, selectedGroup = preferences.Group, country = flag.Code,
                    home = home.Text, apiRequests = reader.Reads, liveVpnChanged = false, registryWrites = false }));
                Console.WriteLine($"PASS: {_checks} live read-only Throne checks; country={flag.Code ?? "unknown"}; exit=0");
            }
            finally { window.Close(); }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    sealed class ReadOnlyThroneApi(IThroneApi inner) : IThroneApi
    {
        public List<string> Reads { get; } = [];
        public Task<JsonElement> CallAsync(string method, string path, object? body = null, Dictionary<string, string>? query = null, CancellationToken cancel = default)
        {
            if (method != "GET") throw new Exception("Read-only probe rejected a mutating API request");
            Reads.Add(method + " " + path); return inner.CallAsync(method, path, body, query, cancel);
        }
    }
}
