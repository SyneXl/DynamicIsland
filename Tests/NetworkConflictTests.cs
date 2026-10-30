using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland;

static partial class Program
{
    sealed class ConflictThroneApi : IThroneApi
    {
        public readonly ThroneFakeApi Inner = new();
        public readonly ConcurrentQueue<string> Events;
        public int IdleAfter = 2, StopReads;
        public bool RejectStop, ConnectionRefused;
        public ConflictThroneApi(ConcurrentQueue<string> events) => Events = events;
        public Task<JsonElement> CallAsync(string method, string path, object? body = null, Dictionary<string, string>? query = null, CancellationToken cancel = default)
        {
            Events.Enqueue(method + " " + path);
            if (ConnectionRefused) throw new System.Net.Sockets.SocketException(10061);
            if (method == "DELETE" && RejectStop) throw new IOException("fixture stop rejected");
            if (method == "GET" && path == "/v1/status" && Inner.State == "stopping" && ++StopReads >= IdleAfter)
            { Inner.State = "idle"; Events.Enqueue("throne:confirmed-off"); }
            return Inner.CallAsync(method, path, body, query, cancel);
        }
    }

    static int NetworkConflictTests(string output)
    {
        MainWindow? window = null;
        try
        {
            Directory.CreateDirectory(output);
            window = ToolTipWindow(false); var root = (FrameworkElement)window.Content;
            window.Show(); Pump(100);
            var events = new ConcurrentQueue<string>();
            var api = new ConflictThroneApi(events);
            var zapret = new FakeZapretSystem();
            string folder = MakeZapretFixture(Path.Combine(output, "zapret-fixture"));
            var off = new ZapretStatus(MissingService(), MissingService(), MissingService(), []);
            var on = off with { Service = new(true, 4, ZapretStrategyParser.Load(folder, "general.bat").CommandLine, 2, "general") };
            int installs = 0, removals = 0;
            bool failZapret = false, leaveZapretRunning = false;
            var panel = typeof(MainWindow).GetNestedType("Panel", System.Reflection.BindingFlags.NonPublic)!;
            var app = typeof(MainWindow).GetNestedType("NetworkApp", System.Reflection.BindingFlags.NonPublic)!;
            T Field<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
            object? Pending() => typeof(MainWindow).GetField("_networkStart", Private)!.GetValue(window);
            object? Answer() => typeof(MainWindow).GetField("_networkConflictAnswer", Private)!.GetValue(window);
            FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
            string Text(string name) => ((TextBlock)Element(name)).Text;
            void Until(Func<bool> ready)
            {
                long deadline = Environment.TickCount64 + 5000;
                while (!ready() && Environment.TickCount64 < deadline) Pump(10);
                if (!ready()) throw new Exception("Conflict fixture timed out: " + string.Join(";", events));
            }
            void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panel, name)); Pump(30); }
            void Click(string name) => ((Button)Element(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Done() => Until(() => Pending() == null);
            void Prompt(string target)
            {
                Until(() => Answer() != null || Pending() == null);
                Check(Answer() != null && Element(target + "ConflictPrompt").Visibility == Visibility.Visible, target + " exposes inline conflict prompt");
                Check(Element(target + "Transport").Visibility == Visibility.Hidden, "prompt replaces transport without reserving another row");
                Check(api.Inner.Requests.All(r => r.Method == "GET") && installs == 0 && removals == 0, "question itself has no network side effects");
            }
            void Capture(string name)
            {
                Call(window, "UpdateTargets");
                foreach (Spring spring in Field<Spring[]>("_springs")) spring.Snap(spring.Target);
                Call(window, "ApplyShape");
                foreach (var node in ToolTipVisuals(root).OfType<UIElement>()) { node.CacheMode = null; node.InvalidateVisual(); }
                root.UpdateLayout(); Pump(200); Save(root, Path.Combine(output, name));
            }
            void CheckPromptAppearance(string target, string alignedLabel)
            {
                var prompt = (Border)Element(target + "ConflictPrompt");
                Check(prompt.Background == null || prompt.Background is SolidColorBrush { Color.A: 0 }, target + " prompt has no visible card background");
                Check(prompt.BorderThickness == new Thickness(0) && prompt.BorderBrush == null, target + " prompt has no outline");
                double questionX = Element(target + "ConflictText").TranslatePoint(new Point(), root).X;
                double labelX = Element(alignedLabel).TranslatePoint(new Point(), root).X;
                Check(Math.Abs(questionX - labelX) <= 0.5 && Math.Abs(prompt.ActualHeight - 96) <= 0.5,
                    target + " prompt aligns with section labels and preserves transport height");
            }
            void Reset(string target, bool vpn, bool bypass)
            {
                Check(Pending() == null, "previous start intent has been released");
                api.Inner.State = vpn ? "running" : "idle"; api.Inner.Profile = vpn ? 11 : -1;
                api.Inner.Fail = api.RejectStop = api.ConnectionRefused = false; api.StopReads = 0; api.IdleAfter = 2;
                api.Inner.Requests.Clear(); events.Clear(); installs = removals = 0;
                failZapret = leaveZapretRunning = false; zapret.OnRead = null; zapret.Status = bypass ? on : off;
                Set(window, "_throneApi", api);
                Set(window, "_throneStatus", new ThroneStatus(api.Inner.State, api.Inner.Profile, "FI Helsinki", 11));
                Set(window, "_throneReadError", false); Set(window, "_zapretReadError", false);
                Set(window, "_throneMessage", ""); Set(window, "_zapretMessage", "");
                Set(window, "_thronePreferences", new ThronePreferences(Group: 1, Profile: 11));
                Set(window, "_zapretStatus", zapret.Status);
                Call(window, "SyncNetworkViews"); Page(target);
            }
            Set(window, "_zapretSystem", zapret);
            Set(window, "_isThroneProcessRunning", (Func<bool>)(() => true));
            Set(window, "_findThrone", (Func<ThronePreferences, ThroneDiscoveryResult>)(_ => new(null, null, false, false, null, null)));
            Set(window, "_throneInstallation", new ThroneDiscoveryResult(null, null, true, true, null, null));
            Set(window, "_saveThronePreferences", (Func<ThronePreferences, bool>)(_ => true));
            Set(window, "_saveZapretPreferences", (Func<ZapretPreferences, bool>)(_ => true));
            Set(window, "_networkStopTimeout", TimeSpan.FromMilliseconds(500));
            Set(window, "_throneGroups", new[] { new ThroneGroup(1, "Subscription", true, 2) });
            Set(window, "_throneProfiles", new[] { new ThroneProfile(11, 1, "FI Helsinki", 19, "vless", false), new ThroneProfile(12, 1, "DE Frankfurt", 30, "vless", false) });
            Call(window, "AddZapretInstall", ZapretInstallation.Inspect(folder)!);
            Call(window, "SelectZapretInstall", ZapretInstallation.Inspect(folder)!);
            Set(window, "_runZapretOperation", (Func<bool, string?, string?, ZapretStatus, Task<ZapretOperationResult>>)((install, selected, strategy, status) =>
            {
                if (install) { installs++; events.Enqueue("zapret:start"); Check(selected == folder && strategy == "general.bat", "start retains selected Zapret installation and strategy"); }
                else { removals++; events.Enqueue("zapret:stop"); Check(selected == null || selected == folder, "off uses existing scoped Zapret helper"); }
                if (failZapret) return Task.FromResult(new ZapretOperationResult(false, "UAC cancelled fixture", []));
                if (install) zapret.Status = on;
                else if (!leaveZapretRunning) { zapret.Status = off; events.Enqueue("zapret:confirmed-off"); }
                return Task.FromResult(new ZapretOperationResult(true, "", []));
            }));

            Reset("Throne", false, false); Click("ThroneAction"); Done();
            Check(api.Inner.State == "connecting" && Answer() == null && installs + removals == 0, "VPN starts directly when Zapret is off");
            Reset("Zapret", false, false); Click("ZapretAction"); Done();
            Check(installs == 1 && api.Inner.Requests.All(r => r.Method == "GET"), "Zapret starts directly when VPN is off");

            Reset("Throne", false, true); Set(window, "_zapretStatus", off); // stale cached off
            Click("ThroneAction"); Prompt("Throne");
            Check(Text("ThroneConflictText") == "Включён Zapret. Отключить?", "VPN warning names Zapret using a fresh read, not cached off");
            Check(!((Button)Element("ZapretAction")).IsEnabled && !((Button)Element("ThroneGroupButton")).IsEnabled, "pending decision locks both transports and mutable selection");
            var duplicate = (Task<bool>)Call(window, "StartNetworkAsync", Enum.Parse(app, "Zapret"), -1, false)!;
            Check(duplicate.IsCompletedSuccessfully && !duplicate.Result, "second start intent cannot replace the first prompt");
            Capture("throne-zapret-conflict.png");
            CheckPromptAppearance("Throne", "ThroneGroupText");
            var question = (TextBlock)Element("ThroneConflictText"); question.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Check(question.DesiredSize.Width <= ((Border)Element("ThroneConflictPrompt")).ActualWidth - 30, "full question fits at 100% scale without truncation");
            Check(ToolTipGlyphs(VisualTreeHelper.GetDrawing(question)).Any(f => f.Contains("fonts", StringComparison.OrdinalIgnoreCase)), "conflict prompt uses bundled island font");
            Click("ThroneConflictNo"); Click("ThroneConflictYes"); Done();
            Check(api.Inner.State == "connecting" && zapret.Status.Service.Running && removals == 0, "No starts VPN and keeps Zapret; immediate second answer is ignored");
            Check(Element("ThroneConflictPrompt").Visibility == Visibility.Collapsed && Element("ThroneTransport").Visibility == Visibility.Visible, "decision restores original transport in place");
            Check(Text("ThroneState") == "Подключаю…", "accepted start is not mislabelled as connected");

            Reset("Zapret", true, false); Click("ZapretAction"); Prompt("Zapret");
            Check(Text("ZapretConflictText") == "Включён VPN. Отключить?", "reverse warning names VPN");
            Capture("zapret-vpn-conflict.png");
            CheckPromptAppearance("Zapret", "ZapretFolderText");
            Click("ZapretConflictNo"); Done();
            Check(installs == 1 && api.Inner.State == "running" && api.Inner.Requests.All(r => r.Method == "GET"), "No starts Zapret and keeps VPN connected");

            Reset("Throne", false, true); Click("ThroneAction"); Prompt("Throne"); Click("ThroneConflictYes"); Done();
            var trace = events.ToArray();
            Check(removals == 1 && zapret.Status.IsOff && api.Inner.State == "connecting"
                && Array.IndexOf(trace, "zapret:confirmed-off") < Array.IndexOf(trace, "PUT /v1/connection"), "Yes stops and verifies Zapret before VPN start");
            Reset("Zapret", true, false); Click("ZapretAction"); Prompt("Zapret"); Click("ZapretConflictYes"); Done();
            trace = events.ToArray();
            Check(installs == 1 && api.Inner.State == "idle" && api.StopReads >= 2
                && Array.IndexOf(trace, "throne:confirmed-off") < Array.IndexOf(trace, "zapret:start"), "Yes waits past DELETE 202 until actual idle before Zapret start");
            File.WriteAllLines(Path.Combine(output, "verified-switch-trace.txt"), trace);

            Reset("Throne", false, true); failZapret = true; Click("ThroneAction"); Prompt("Throne"); Click("ThroneConflictYes"); Done();
            Check(removals == 1 && api.Inner.State == "idle" && zapret.Status.Service.Running && Text("ThroneNote").Contains("Запуск отменён"), "UAC/helper rejection never starts VPN and preserves actual Zapret state");
            Reset("Zapret", true, false); api.RejectStop = true; Click("ZapretAction"); Prompt("Zapret"); Click("ZapretConflictYes"); Done();
            Check(installs == 0 && api.Inner.State == "running" && Text("ZapretOperationNote").Contains("Запуск отменён"), "failed VPN stop never launches Zapret");
            Reset("Zapret", true, false); api.IdleAfter = int.MaxValue; Click("ZapretAction"); Prompt("Zapret"); Click("ZapretConflictYes"); Done();
            Check(installs == 0 && api.Inner.State == "stopping" && Text("ZapretOperationNote").Contains("ещё отключается"), "stuck VPN transition is bounded and never treated as off");
            Reset("Throne", false, true); leaveZapretRunning = true; Click("ThroneAction"); Prompt("Throne"); Click("ThroneConflictYes"); Done();
            Check(api.Inner.State == "idle" && Text("ThroneNote").Contains("ещё отключается"), "helper success flag alone cannot prove Zapret is off");

            Reset("Throne", false, false); zapret.OnRead = () => throw new IOException("read fixture failure");
            Click("ThroneAction"); Done();
            Check(api.Inner.State == "idle" && Text("ThroneNote").Contains("проверить Zapret"), "failed fresh read never reuses cached off status");
            Reset("Throne", false, false); zapret.Status = off with { UnidentifiedProcesses = 1 }; Click("ThroneAction"); Done();
            Check(api.Inner.State == "idle" && Answer() == null, "unidentified elevated process blocks uncertain automatic start");
            Reset("Zapret", true, false); api.Inner.Fail = true; Click("ZapretAction"); Done();
            Check(installs == 0 && Text("ZapretOperationNote").Contains("проверить Throne"), "unreachable running Throne is not assumed disconnected");
            Reset("Zapret", false, false); Set(window, "_throneApi", null!); Click("ZapretAction"); Done();
            Check(installs == 1, "absent API integration does not require installing Throne to use Zapret");
            Reset("Zapret", false, false); api.Inner.Fail = true; Set(window, "_isThroneProcessRunning", (Func<bool>)(() => false));
            Click("ZapretAction"); Done(); Check(installs == 0, "API error is not ignored just because process lookup is empty");
            Reset("Zapret", false, false); api.ConnectionRefused = true;
            Click("ZapretAction"); Done(); Check(installs == 1, "closed installed Throne does not block Zapret");
            Set(window, "_isThroneProcessRunning", (Func<bool>)(() => true));

            Reset("Throne", false, true); api.Inner.State = "running"; api.Inner.Profile = 11;
            Click("ThroneAction"); Done();
            Check(removals == 0 && api.Inner.Requests.All(r => r.Method == "GET"), "outside destination start makes stale Play a no-op, not an unwanted disconnect");
            Reset("Throne", false, true); Click("ThroneAction"); Prompt("Throne"); zapret.Status = off; Click("ThroneConflictYes"); Done();
            Check(removals == 0 && api.Inner.State == "connecting", "outside stop during question avoids redundant UAC");
            Reset("Throne", false, true); Click("ThroneAction"); Prompt("Throne"); Page("None"); Done(); Page("Throne");
            Check(removals == 0 && api.Inner.State == "idle" && Answer() == null && ((Button)Element("ThroneAction")).IsEnabled, "leaving unanswered question cancels intent and restores controls without replay");

            Reset("Zapret", true, false); Click("ZapretApply"); Prompt("Zapret"); Click("ZapretConflictNo"); Done();
            Check(installs == 1 && api.Inner.State == "running", "stale Apply button cannot bypass conflict preflight");
            Reset("ThroneServers", false, true);
            Set(window, "_throneStatus", new ThroneStatus("running", 11, "FI Helsinki", 11)); Call(window, "FillThroneServers");
            ((Button)((StackPanel)Element("ThroneServerRows")).Children[1]).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Prompt("Throne"); Click("ThroneConflictNo"); Done();
            Check(api.Inner.Profile == 12 && removals == 0, "stale live server switch also checks conflict and retains requested server");

            Reset("Throne", false, true);
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                zapret.OnRead = () => { entered.Set(); if (!release.Wait(3000)) throw new TimeoutException(); return zapret.Status; };
                Click("ThroneAction"); Until(() => entered.IsSet); Page("None"); release.Set(); Done();
            }
            Check(api.Inner.State == "idle" && removals == 0 && Answer() == null, "navigation during preflight cancels before showing or executing a hidden prompt");

            Reset("Throne", false, true); Click("ThroneAction"); Prompt("Throne"); window.Close(); Done();
            Check(removals == 0 && api.Inner.State == "idle", "closing with an unanswered prompt releases waiter without network side effects");
            File.WriteAllText(Path.Combine(output, "tests-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, liveVpnChanged = false, externalProcessesLaunched = false }));
            Console.WriteLine($"PASS: {_checks} network conflict checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { window?.Close(); }
    }
}
