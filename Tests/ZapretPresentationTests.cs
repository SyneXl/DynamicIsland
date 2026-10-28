using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DynamicIsland;

static partial class Program
{
    static int ZapretPreview(string output, string state)
    {
        try
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
            typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
            var window = new MainWindow();
            var system = new FakeZapretSystem { Status = state == "on" ? new ZapretSystem().Read() : new(MissingService(), MissingService(), MissingService(), []) };
            Set(window, "_zapretSystem", system);
            Set(window, "_saveZapretPreferences", (Func<ZapretPreferences, bool>)(_ => true));
            Set(window, "_zapretPreferences", ZapretPreferences.Empty);
            Set(window, "_runZapretOperation", (Func<bool, string?, string?, ZapretStatus, Task<ZapretOperationResult>>)((_, _, _, _) => throw new Exception("Preview must never invoke an operation")));
            Set(window, "_zapretStatus", system.Status);
            var install = ZapretInstallation.Inspect(@"C:\zapret")!;
            Call(window, "AddZapretInstall", install); Call(window, "SelectZapretInstall", install);
            window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
            window.Show(); Pump(300);
            var panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
            Call(window, "ShowPanel", Enum.Parse(panel, "Zapret")); Pump(700);
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape");
            var root = (FrameworkElement)window.FindName("Root"); root.UpdateLayout();
            Save(root, output);
            Check(((TextBlock)window.FindName("ZapretState")).Text == (state == "on" ? "Включён" : "Выключен"), "fresh WPF preview matches requested state");
            Check(window.FindName("ZapretConfirmation") == null && ((FrameworkElement)window.FindName("ZapretOperationNote")).Visibility == Visibility.Collapsed,
                "fresh preview has no confirmation or filler text");
            window.Close();
            Console.WriteLine($"PREVIEW PASS: {_checks} checks; read-only; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CommandLineToArgvW(string line, out int count);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr memory);
    static string[] WindowsArgv(string line)
    {
        IntPtr array = CommandLineToArgvW("dummy " + line, out int count);
        if (array == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        try { return Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(array, i * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(array); }
    }
    static int ZapretParity(string corpus)
    {
        try
        {
            var entries = JsonSerializer.Deserialize<Dictionary<string, string?>[]>(File.ReadAllText(corpus))!;
            foreach (var entry in entries)
            {
                string root = entry["Root"]!, file = entry["File"]!;
                var launch = ZapretStrategyParser.Load(root, file, false);
                string raw = File.ReadAllText(entry["RawFile"]!).Trim().Replace("\\\"", "\"");
                string[] original = WindowsArgv(raw);
                if (!original.SequenceEqual(launch.Arguments))
                {
                    File.WriteAllText(Path.Combine(root, "mismatch.json"), JsonSerializer.Serialize(new { original, native = launch.Arguments }));
                    throw new Exception("FAIL: original/native argv mismatch: " + root + " / " + file);
                }
                Check(WindowsArgv(string.Join(' ', launch.Arguments.Select(ZapretLaunch.WindowsArgument))).SequenceEqual(original),
                    "original BAT parity: " + entry["Original"] + " / " + (entry["Mode"] ?? "disabled"));
            }
            Console.WriteLine($"PARITY PASS: {_checks} original batch parser cases match native argv; no live service mutations; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void ZapretPresentation(MainWindow window, FrameworkElement root, string? output)
    {
        string fixtureRoot = output ?? Path.Combine(Path.GetTempPath(), "DynamicIsland-zapret-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        File.WriteAllText(Path.Combine(fixtureRoot, ".dynamic-island-test-fixtures"), "");
        string first = MakeZapretFixture(Path.Combine(fixtureRoot, "zapret-fixture-a"));
        string second = MakeZapretFixture(Path.Combine(fixtureRoot, "zapret-fixture-b"));
        var names = new[] { "_zapretSystem", "_zapretPreferences", "_saveZapretPreferences", "_runZapretOperation", "_zapretRoot", "_zapretStrategy", "_zapretStatus", "_zapretMessage", "_zapretInstalling", "_zapretRefreshTask" };
        var originals = names.ToDictionary(n => n, n => typeof(MainWindow).GetField(n, Private)!.GetValue(window));
        var installs = (Dictionary<string, ZapretInstallation>)typeof(MainWindow).GetField("_zapretInstalls", Private)!.GetValue(window)!;
        var oldInstalls = installs.ToArray();
        var system = new FakeZapretSystem();
        int saved = 0, operations = 0;
        bool cancel = false;
        TaskCompletionSource<ZapretOperationResult>? pending = null;
        var panelType = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        bool Busy() => (bool)typeof(MainWindow).GetField("_zapretBusy", Private)!.GetValue(window)!;
        void Until(Func<bool> condition)
        {
            long deadline = Environment.TickCount64 + 5000;
            while (!condition() && Environment.TickCount64 < deadline) Pump(20);
            if (!condition()) throw new Exception("Zapret UI operation timed out");
        }
        void Sync() { Set(window, "_zapretStatus", system.Status); Call(window, "SyncZapret"); }
        FrameworkElement Element(string name) => (FrameworkElement)window.FindName(name);
        void Capture(string file)
        {
            Call(window, "ShowPanel", Enum.Parse(panelType, "Zapret"));
            Pump(450);
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape"); root.UpdateLayout();
            void Invalidate(DependencyObject node)
            {
                if (node is UIElement element) element.InvalidateVisual();
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
                    Invalidate(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
            }
            Invalidate(root); root.UpdateLayout(); Pump(80);
            if (output != null) Save(root, Path.Combine(output, file));
        }
        try
        {
            Set(window, "_zapretSystem", system);
            Set(window, "_zapretPreferences", ZapretPreferences.Empty);
            Set(window, "_saveZapretPreferences", (Func<ZapretPreferences, bool>)(_ => { saved++; return true; }));
            Set(window, "_runZapretOperation", (Func<bool, string?, string?, ZapretStatus, Task<ZapretOperationResult>>)((install, folder, strategy, status) =>
            {
                operations++;
                if (pending != null) return pending.Task;
                if (cancel) return Task.FromResult(new ZapretOperationResult(false, "Запрос прав администратора отменён.", []));
                return ZapretOperations.ExecuteAsync(install, folder, strategy, status.Identity, system);
            }));
            installs.Clear();
            Call(window, "AddZapretInstall", ZapretInstallation.Inspect(first)!);
            Call(window, "AddZapretInstall", ZapretInstallation.Inspect(second)!);
            Call(window, "SelectZapretInstall", ZapretInstallation.Inspect(first)!);
            Set(window, "_zapretStatus", system.Status);
            Call(window, "SyncZapret");
            Check(saved == 1 && ((TextBlock)window.FindName("ZapretStrategyText")).Text == "general", "selecting a copy persists its visible default without running it");
            Call(window, "FillZapretFolders");
            Check(((Panel)window.FindName("ZapretFolderRows")).Children.Count == 2, "Zapret UI lists separate copies rather than silently choosing the newest one");
            Call(window, "FillZapretStrategies");
            var strategies = (Panel)window.FindName("ZapretStrategyRows");
            Check(strategies.Children.Count == 2, "Zapret UI uses strategies from the selected copy only");
            strategies.Children.OfType<Button>().Single(b => (string)b.Tag == "general (ALT2).bat").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((TextBlock)window.FindName("ZapretStrategyText")).Text == "general (ALT2)" && operations == 0,
                "changing the default strategy does not restart or install a service");
            Call(window, "SelectZapretInstall", ZapretInstallation.Inspect(second)!);
            Call(window, "SelectZapretInstall", ZapretInstallation.Inspect(first)!);
            Check(((TextBlock)window.FindName("ZapretStrategyText")).Text == "general (ALT2)", "switching copies restores each copy's own selected strategy");
            system.Status = system.Status with { Driver = new(true, 4, "other-driver", 3, "") };
            Sync();
            Check(((TextBlock)window.FindName("ZapretState")).Text == "Выключен" && ((Button)window.FindName("ZapretCleanup")).Visibility == Visibility.Collapsed,
                "a shared VPN driver alone produces neither an on state nor a cleanup button");
            Check(window.FindName("ZapretConfirmation") == null && Element("ZapretOperationNote").Visibility == Visibility.Collapsed,
                "confirmation and permanent explanatory paragraphs are absent");
            var vpnDriver = system.Status.Driver;
            system.Status = system.Status with { Service = new(true, 1, ZapretStrategyParser.Load(first, "general.bat").CommandLine, 2, "general") }; Sync();
            ((Button)window.FindName("ZapretCleanup")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !Busy());
            Check(operations == 1 && !system.Status.HasZapretResources && system.Status.Driver == vpnDriver, "stopped-service cleanup runs with one click and preserves the VPN driver");
            string command = ZapretStrategyParser.Load(second, "general.bat").CommandLine;
            system.Status = system.Status with { Service = new(true, 4, command, 2, "general") };
            Sync();
            Check(Element("ZapretApply").Visibility == Visibility.Visible && ((TextBlock)window.FindName("ZapretActive")).Text == "Сейчас: general"
                && Element("ZapretOperationNote").Visibility == Visibility.Collapsed,
                "selected/active mismatch uses an active-strategy subtitle and Apply rather than a paragraph");
            Check(Element("ZapretPauseIcon").Visibility == Visibility.Visible && Element("ZapretPlayIcon").Visibility == Visibility.Collapsed,
                "running service has a pause glyph in the circular transport button");
            ((Button)window.FindName("ZapretAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !Busy());
            Check(operations == 2 && !system.Status.HasZapretResources && ((TextBlock)window.FindName("ZapretState")).Text == "Выключен",
                "one pause click removes the service and updates the displayed state");
            Check(Element("ZapretPlayIcon").Visibility == Visibility.Visible && Element("ZapretOperationNote").Visibility == Visibility.Collapsed,
                "successful removal returns the play glyph without a success paragraph");
            ((Button)window.FindName("ZapretAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => !Busy());
            Check(operations == 3 && system.Status.Service.Running && system.Status.Service.Strategy == "general (ALT2)"
                && system.Status.Service.Root == first && ((TextBlock)window.FindName("ZapretState")).Text == "Включён",
                "one play click installs the selected copy/strategy and refreshes the running state");
            Check(Element("ZapretOperationNote").Visibility == Visibility.Collapsed && Element("ZapretActions").Visibility == Visibility.Collapsed
                && Element("ZapretView").Height == 284, "normal UI has no footer noise or empty oversized panel");
            Capture("zapret-on.png");
            pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ((Button)window.FindName("ZapretAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => operations == 4);
            Check(!((Button)window.FindName("ZapretAction")).IsEnabled && !((Button)window.FindName("ZapretFolderButton")).IsEnabled,
                "busy operation blocks duplicate toggles and copy changes");
            Check(Element("ZapretSpinner").Visibility == Visibility.Visible && Element("ZapretPauseIcon").Visibility == Visibility.Collapsed,
                "pending operation replaces the transport glyph with a progress indicator");
            ((Button)window.FindName("ZapretAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(operations == 4, "duplicate click during UAC/SCM wait does not create a second operation");
            pending.SetResult(new(false, "Запрос прав администратора отменён.", []));
            Until(() => !Busy()); pending = null;
            Check(((TextBlock)window.FindName("ZapretState")).Text == "Включён" && ((TextBlock)window.FindName("ZapretOperationNote")).Text.Contains("отменён"),
                "cancelled removal preserves and displays the actual running service");
            ((Button)window.FindName("ZapretRefresh")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Until(() => Element("ZapretOperationNote").Visibility == Visibility.Collapsed);
            Check(operations == 4, "explicit refresh clears stale errors without changing services");
            system.Status = system.Status with { Service = system.Status.Service with { State = 1 } }; Sync();
            Check(Element("ZapretPlayIcon").Visibility == Visibility.Visible && Element("ZapretCleanup").Visibility == Visibility.Visible,
                "stopped service offers play/retry and separate cleanup instead of a misleading pause action");
            ((Button)window.FindName("ZapretAction")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Until(() => !Busy());
            Check(operations == 5 && system.Status.Service.Running, "play retries an installed but stopped service");
            system.Status = system.Status with { Service = system.Status.Service with { State = 1 }, ProcessRoots = [second] }; Sync();
            Check(Element("ZapretPauseIcon").Visibility == Visibility.Visible && ((TextBlock)window.FindName("ZapretActive")).Text == "Вручную",
                "a manual winws alongside a stopped service still offers the matching stop action");
            system.Status = system.Status with { Service = system.Status.Service with { State = 2 } }; Sync();
            Check(!((Button)window.FindName("ZapretAction")).IsEnabled && ((TextBlock)window.FindName("ZapretState")).Text == "Переход…",
                "native SCM transition cannot be mistaken for an actionable stable state");
            system.Status = new(MissingService(), MissingService(), MissingService(), []);
            Sync(); cancel = true;
            var operation = (Task)Call(window, "ExecuteZapretAsync", true, first, "general.bat", system.Status)!;
            Until(() => operation.IsCompleted);
            Check(operation.IsCompleted, "simulated elevated operation completes on the WPF dispatcher");
            operation.GetAwaiter().GetResult();
            Check(operations == 6 && ((TextBlock)window.FindName("ZapretState")).Text == "Выключен"
                && ((TextBlock)window.FindName("ZapretOperationNote")).Text.Contains("отменён"),
                "UAC cancellation restores controls and never fakes an enabled state");
            Capture("zapret-error.png"); cancel = false;
            // An old in-flight status poll must be awaited before the operation and
            // must not overwrite the final service state after a successful start.
            using var release = new ManualResetEventSlim();
            int reads = 0;
            system.OnRead = () => { if (Interlocked.Increment(ref reads) == 1) release.Wait(3000); return system.Status; };
            var poll = (Task)Call(window, "RefreshZapretAsync")!;
            operation = (Task)Call(window, "ExecuteZapretAsync", true, first, "general.bat", system.Status)!;
            Check(Busy() && !operation.IsCompleted, "operation waits for an already running status poll");
            release.Set(); Until(() => operation.IsCompleted); operation.GetAwaiter().GetResult(); system.OnRead = null;
            Check(system.Status.Service.Running && ((TextBlock)window.FindName("ZapretState")).Text == "Включён" && !Busy(),
                "final refresh cannot be overwritten by a stale pre-operation poll");
            // Pure read-only live snapshot for visual inspection; operations still
            // use the fake backend, and preferences remain in-memory throughout.
            if (ZapretInstallation.Inspect(@"C:\zapret") is { } local)
            {
                Call(window, "AddZapretInstall", local);
                system.Status = new ZapretSystem().Read();
                Set(window, "_zapretPreferences", ZapretPreferences.Empty);
                Set(window, "_zapretStatus", system.Status);
                Call(window, "SelectZapretInstall", local);
                Capture("zapret.png");
            }
        }
        finally
        {
            Set(window, "_zapretBusy", false);
            installs.Clear(); foreach (var item in oldInstalls) installs.Add(item.Key, item.Value);
            foreach (var item in originals) typeof(MainWindow).GetField(item.Key, Private)!.SetValue(window, item.Value);
            Call(window, "SyncZapret");
        }
    }
}
