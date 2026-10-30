using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static int _checks;

    [STAThread]
    static int Main(string[] args)
    {
#if MEDIA_TEST_API
        if (args is ["--media-regressions"]) return MediaRegressions();
#endif
        if (args is ["--smtc-fixture", var fixtureId, var control, var wave]) return SmtcFixture(fixtureId, control, wave);
        if (args is ["--auto-switch-test", var fixtureRoot]) return AutoSwitchIntegration(fixtureRoot);
        if (args is ["--windows"]) { Console.WriteLine(JsonSerializer.Serialize(BrowserWindows())); return 0; }
        if (args is ["--probe", var handle]) return Probe(new IntPtr(long.Parse(handle)));
        if (args is ["--live-window", var result]) return LiveWindow(result);
        if (args is ["--media-probe", var pid]) return MediaProbe(uint.Parse(pid));
        if (args is ["--mpc-integration", var player, var fixture, var variant]) return MpcIntegration(player, fixture, variant);
        if (args is ["--codex-probe"]) return CodexReadOnlyProbe().GetAwaiter().GetResult();
        if (args is ["--codex-rpc-fixture", var transcript]) return CodexRpcFixture(transcript);
        if (args is ["--menu-window-test", var menuOutput]) return MenuWindowTest(menuOutput);
        if (args is ["--menu-preview"]) return MenuPreview();
        if (args is ["--tooltip-tests", var tooltipOutput]) return ToolTipTests(tooltipOutput);
        if (args is ["--tooltip-layer-probe", var tooltipLayer]) return ToolTipLayerProbe(tooltipLayer);
        if (args is ["--tooltip-preview", var tooltipPreview]) return ToolTipPreview(tooltipPreview);
        if (args is ["--updater-tests"]) return UpdaterTests().GetAwaiter().GetResult();
        if (args is ["--release-notes-tests", var notesOutput]) return ReleaseNotesTests(notesOutput);
        if (args is ["--image-translation-tests", var imageOutput]) return ImageTranslationTests(imageOutput, false);
        if (args is ["--image-translation-live", var imageLiveOutput]) return ImageTranslationTests(imageLiveOutput, true);
        if (args is ["--release-notes-readonly", var notesReadOutput]) return ReleaseNotesReadOnly(notesReadOutput).GetAwaiter().GetResult();
        if (args is ["--island-features-tests", var featureOutput]) return IslandFeaturesTests(featureOutput);
        if (args is ["--quiet-appearance-tests", var quietOutput]) return QuietAppearanceTests(quietOutput);
        if (args is ["--native-mixer-test", var nativeOutput]) return NativeMixerIntegration(nativeOutput);
        if (args is ["--mixer-parity-tests", var parityOutput]) return MixerParityTests(parityOutput);
        if (args is ["--native-mixer-parity", var nativeParityOutput]) return NativeMixerParity(nativeParityOutput);
        if (args is ["--throne-tests", var throneOutput]) return ThroneTests(throneOutput);
        if (args is ["--network-conflict-tests", var conflictOutput]) return NetworkConflictTests(conflictOutput);
        if (args is ["--throne-readonly", var throneReadOutput]) return ThroneReadOnly(throneReadOutput);
        if (args is ["--throne-home-readonly", var throneHomeReadOutput]) return ThroneHomeReadOnly(throneHomeReadOutput);
        if (args is ["--throne-fixture-probe", var thronePort, var throneKey, var throneResult]) return ThroneFixtureProbe(int.Parse(thronePort), throneKey, throneResult).GetAwaiter().GetResult();
        if (args is ["--throne-installer-network", var throneInstallerOutput]) return ThroneInstallerNetwork(throneInstallerOutput).GetAwaiter().GetResult();
        if (args is ["--upstream-tests", var upstreamOutput]) return UpstreamTests(upstreamOutput);
        if (args is ["--update-handoff-tests", var updateExe, var handoffOutput]) return UpdateHandoffTests(updateExe, handoffOutput).GetAwaiter().GetResult();
        if (args is ["--update-network-test", var downloadOutput]) return UpdateNetworkTest(downloadOutput).GetAwaiter().GetResult();
        if (args is ["--zapret-tests", var zapretOutput]) return ZapretTests(zapretOutput);
        if (args is ["--zapret-identity-tests", var identityOutput]) return ZapretIdentityTests(identityOutput);
        if (args is ["--zapret-isolation-tests", var isolationOutput]) return ZapretIsolationTests(isolationOutput);
        if (args is ["--zapret-process-scope-tests", var ownedExe, var foreignExe, var scopeOutput]) return ZapretProcessScopeTests(ownedExe, foreignExe, scopeOutput);
        if (args is ["--zapret-preview", var previewOutput, var previewState]) return ZapretPreview(previewOutput, previewState);
        if (args is ["--zapret-parity", var corpus]) return ZapretParity(corpus);
        if (args is ["--zapret-discovery", var scanOutput]) return ZapretDiscoveryTest(scanOutput);
        try
        {
            Surfaces();
            MpcParsing();
            NavigationMath();
            PlaybackActivityTests();
            MenuLayoutTests();
            CodexTests().GetAwaiter().GetResult();
            Transitions();
            Services();
            WindowSmoke(args.FirstOrDefault());
            Console.WriteLine($"PASS: {_checks} checks; exit=0");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        _checks++;
        Console.WriteLine("PASS: " + name);
    }

    static void Surfaces()
    {
        Rect screen = new(0, 0, 1920, 1080);
        Check(VideoSurface.HasVideoTag("display:block;tag:video;text-align:left;"), "IA2 video tag");
        Check(VideoSurface.HasVideoTag("tag:VIDEO;"), "case-insensitive video tag");
        Check(!VideoSurface.HasVideoTag("tag:audio;description:video;"), "audio is not video");
        Check(!VideoSurface.HasVideoTag("tag:div;class:video-player;"), "player name is not video evidence");
        Check(!VideoSurface.HasVideoTag("description:tag:video;tag:img;"), "attribute substring cannot impersonate video");
        Check(VideoSurface.FillsScreen(screen, screen), "full-screen video");
        Check(VideoSurface.FillsScreen(new Rect(240, 0, 1440, 1080), screen), "4:3 pillarboxed video");
        Check(VideoSurface.FillsScreen(new Rect(0, 130, 1920, 820), screen), "cinemascope letterboxed video");
        Check(!VideoSurface.FillsScreen(new Rect(0, 0, 640, 360), screen), "inline video in fullscreen page rejected");
        Check(!VideoSurface.FillsScreen(new Rect(20, 200, 1600, 650), screen), "large off-center page player rejected");
        Check(!VideoSurface.FillsScreen(new Rect(1920, 0, 1920, 1080), screen), "other monitor rejected");
        Check(VideoSurface.FillsScreen(new Rect(-1920, 0, 1920, 1080), new Rect(-1920, 0, 1920, 1080)), "negative monitor coordinates");
        Check(!VideoSurface.FillsScreen(Rect.Empty, screen), "empty surface rejected");
        Check(!VideoSurface.FillsScreen(new Rect(0, 0, 0, 0), screen), "zero-size surface rejected");
        Check(!VideoSurface.FillsScreen(new Rect(double.NaN, 0, 1920, 1080), screen), "nonfinite surface rejected");
    }

    static void Transitions()
    {
        var state = new CinemaState();
        Check(!state.IsActive(0), "no evidence, no cinema");
        state.Report(true, 0);
        Check(!state.IsActive(0.3), "entry debounce");
        Check(state.IsActive(0.7), "confirmed video activates");
        state.Report(true, 1.2);
        state.Report(false, 1.5);
        Check(state.IsActive(2), "transient provider rebuild does not flash");
        Check(!state.IsActive(4.8), "stale or hung provider evidence expires");
        state.Report(true, 5);
        Check(!state.IsActive(5.1), "expired evidence requires fresh entry delay");
        Check(state.IsActive(5.7), "new confirmation activates");
        state.Reset();
        Check(!state.IsActive(5.8), "fullscreen exit and window changes clear immediately");
        state.Report(false, 6);
        Check(!state.IsActive(7), "fullscreen non-video never activates");
    }

    static void WindowSmoke(string? output)
    {
        // Deterministic off-screen snapshots must not depend on the desktop GPU's
        // retained composition surfaces for templated controls.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Off-screen rendering has no HWND ancestry for the optional DWM backdrop.
        // Change only the test process's in-memory setting, never the registry.
        FieldInfo glassSetting = typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? glassWas = glassSetting.GetValue(null);
        glassSetting.SetValue(null, false);
        var window = new MainWindow(); // Not shown: Loaded does not start external services.
        var root = (FrameworkElement)window.FindName("Root");
        root.Opacity = 1;
        window.Measure(new Size(1000, 1000));
        window.Arrange(new Rect(0, 0, 1000, 1000));
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        Check(window.FindName("CinemaSwitch") != null, "cinema setting exists");
        Check(window.FindName("UpdatePage") != null && window.FindName("LoadingView") != null, "own-fork update UI exists");
        Check(Updater.Repository == "SyneXl/DynamicIsland", "updates only use the owner's fork");
#if !MEDIA_TEST_API
        Check(typeof(MainWindow).Assembly.GetType("DynamicIsland.Testing.TestMediaSession") == null, "test media API is absent from the production assembly");
#endif
        Type viewType = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        foreach (object view in Enum.GetValues(viewType))
        {
            Call(window, "MorphTo", view);
            var springs = (Array)typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!;
            foreach (object spring in springs)
            {
                Type type = spring.GetType();
                type.GetMethod("Snap")!.Invoke(spring, [type.GetProperty("Target")!.GetValue(spring)]);
            }
            Call(window, "ApplyShape");
            root.UpdateLayout();
        }
        Check(true, "all remaining views construct and lay out");
        Type panelType = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        Set(window, "_panel", Enum.Parse(panelType, "None"));
        Set(window, "_cinemaMode", true);
        Call(window, "StartTimer", TimeSpan.FromMinutes(14));
        Pump(450);
        Check(Math.Abs(root.Opacity - 0.28) < 0.01, "cinema content alpha");
        Check(Math.Abs(((FrameworkElement)window.FindName("Body")).Opacity - 0.48) < 0.01, "cinema body alpha");
        Check(Call(window, "TargetView")!.ToString() == "Timer", "cinema retains useful timer without track toast");
        Check(!((FrameLoop)typeof(MainWindow).GetField("_eqLoop", Private)!.GetValue(window)!).Running, "no equalizer render loop in quiet cinema");
        if (output != null)
        {
            Directory.CreateDirectory(output);
            Save(root, Path.Combine(output, "cinema-timer.png"));
        }
        Set(window, "_hovered", true);
        Call(window, "UpdateTargets");
        Pump(240);
        Check(Math.Abs(root.Opacity - 1) < 0.01, "hover restores readability");
        if (output != null) Save(root, Path.Combine(output, "revealed-timer.png"));
        Set(window, "_hovered", false);
        Call(window, "ShowPanel", Enum.Parse(panelType, "Settings"));
        Pump(240);
        Check(Math.Abs(root.Opacity - 1) < 0.01, "open settings stay readable");
        Set(window, "_panel", Enum.Parse(panelType, "None"));
        Set(window, "_ringing", true); // No sound is played in this test.
        Call(window, "UpdateView");
        Pump(240);
        Check(Math.Abs(root.Opacity - 1) < 0.01, "timer alarm bypasses dimming");
        var media = (MediaService)typeof(MainWindow).GetField("_media", Private)!.GetValue(window)!;
        Set(media, "_noTimeline", true);
        Call(media, "ApplyMpcReading", new MpcTimeline.Reading(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(120)));
        Call(window, "MorphTo", Enum.Parse(viewType, "MediaBig"));
        Call(window, "AdvanceSeek", 0.016);
        Check(((Digits)window.FindName("PosText")).Text == "0:20", "native MPC time reaches WPF position label");
        Check(((Digits)window.FindName("RemText")).Text == "-1:40", "native MPC duration reaches WPF remaining label");
        HomePresentation(window, root, output);
        CodexPresentation(window, root, output);
        MenuLayoutPresentation(window, root, output);
        ZapretPresentation(window, root, output);
        window.Close();
        glassSetting.SetValue(null, glassWas);
    }

    static void Services()
    {
        var target = new Native.FullscreenWindow(new IntPtr(1), 123, new Rect(0, 0, 1920, 1080), false);
        int calls = 0;
        using (var service = new FullscreenVideoService(Dispatcher.CurrentDispatcher, (_, _) =>
               { Interlocked.Increment(ref calls); return Task.FromResult(true); }))
        {
            service.Observe(null);
            Pump(80);
            Check(calls == 0, "no provider queries without fullscreen target");
            service.Observe(target);
            Pump(120);
            Check(service.Pending && !service.Active, "async provider entry remains debounced");
            Pump(700);
            Check(service.Active, "async provider confirms cinema");
            service.Observe(target);
            Check(calls == 1, "provider queries are throttled");
            service.Observe(null);
            Check(!service.Active && !service.Pending, "fullscreen exit resets service immediately");
        }
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        calls = 0;
        using (var service = new FullscreenVideoService(Dispatcher.CurrentDispatcher, (_, _) =>
               { Interlocked.Increment(ref calls); return pending.Task; }))
        {
            service.Observe(target);
            Pump(100);
            service.Observe(null);
            service.Observe(target with { Handle = new IntPtr(2) });
            Check(calls == 1 && !service.Active, "slow provider cannot spawn replacement workers");
            pending.SetResult(true);
            Pump(120);
            Check(!service.Active, "old window's late result is discarded");
        }
    }

    static object? Call(object instance, string method, params object[] args) => instance.GetType().GetMethod(method, Private)!.Invoke(instance, args);
    static void Set(object instance, string field, object value) => instance.GetType().GetField(field, Private)!.SetValue(instance, value);
    static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
    static void Save(FrameworkElement root, string path)
    {
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(root);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        png.Save(stream);
    }

    static int Probe(IntPtr handle)
    {
        // Physical pixels, matching the PerMonitorV2 application.
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        var results = new List<object>();
        var task = Task.Run(() =>
        {
            for (int i = 0; i < 4; i++)
            {
                var sw = Stopwatch.StartNew();
                bool foreground = Native.TryGetForegroundFullscreen(IntPtr.Zero, out var target);
                Rect screen = new(0, 0, GetSystemMetrics(0), GetSystemMetrics(1));
                bool video = VideoAccessibility.ContainsFullscreenVideo(handle, screen);
                results.Add(new { video, milliseconds = sw.Elapsed.TotalMilliseconds, foregroundFullscreen = foreground,
                    matchesForeground = foreground && target.Handle == handle, browser = foreground && target.Browser });
                Thread.Sleep(100);
            }
        });
        if (!task.Wait(TimeSpan.FromSeconds(8))) { Console.WriteLine("probe-timeout"); return 2; }
        Console.WriteLine(JsonSerializer.Serialize(results));
        return 0;
    }

    static int LiveWindow(string result)
    {
        // A real overlay exercises Win32 -> background provider -> Dispatcher ->
        // MainWindow appearance, without starting weather/audio/network services.
        var app = new Application();
        var window = new MainWindow();
        var original = (RoutedEventHandler)typeof(MainWindow).GetMethod("OnLoaded", Private | BindingFlags.DeclaredOnly)!
            .CreateDelegate(typeof(RoutedEventHandler), window);
        window.Loaded -= original;
        bool entered = false, left = false;
        double minimumAlpha = 1;
        var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        window.Loaded += (_, _) =>
        {
            Call(window, "CenterOnScreen");
            Call(window, "UpdateClock");
            Call(window, "PlayIntro");
            timer.Start();
        };
        timer.Tick += (_, _) =>
        {
            Call(window, "CheckFullscreen");
            var root = (FrameworkElement)window.FindName("Root");
            bool cinema = (bool)typeof(MainWindow).GetField("_cinemaMode", Private)!.GetValue(window)!;
            bool dimmed = (bool)typeof(MainWindow).GetField("_cinemaDimmed", Private)!.GetValue(window)!;
            if (cinema && dimmed && root.Opacity <= 0.29) entered = true;
            if (entered && !cinema) left = true;
            if (cinema) minimumAlpha = Math.Min(minimumAlpha, root.Opacity);
            if (clock.Elapsed.TotalSeconds < 6) return;
            timer.Stop();
            File.WriteAllText(result, JsonSerializer.Serialize(new { entered, left, minimumAlpha, finalCinema = cinema }));
            window.Close();
            app.Shutdown(entered && left ? 0 : 1);
        };
        window.Show();
        return app.Run();
    }

    static int MediaProbe(uint pid)
    {
        var manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
        foreach (var session in manager.GetSessions())
        {
            string source = session.SourceAppUserModelId;
            if (!source.Contains("mpc", StringComparison.OrdinalIgnoreCase)) continue;
            var time = session.GetTimelineProperties();
            var properties = session.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult();
            var native = new MpcTimeline().Read(source, properties.Title);
            Console.WriteLine(JsonSerializer.Serialize(new { source, matchesProcess = SourceApp.OwnsProcess(source, pid),
                duration = (time.EndTime - time.StartTime).TotalSeconds, position = time.Position.TotalSeconds,
                updated = time.LastUpdatedTime, status = session.GetPlaybackInfo().PlaybackStatus.ToString(),
                nativeDuration = native?.Duration.TotalSeconds, nativePosition = native?.Position.TotalSeconds }));
        }
        return 0;
    }
    [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    static List<object> BrowserWindows()
    {
        var result = new List<object>();
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            try
            {
                if (Process.GetProcessById((int)pid).ProcessName is not ("chrome" or "vivaldi" or "msedge" or "firefox")) return true;
                var title = new System.Text.StringBuilder(512);
                GetWindowText(h, title, title.Capacity);
                result.Add(new { handle = h.ToInt64(), title = title.ToString(), visible = IsWindowVisible(h), foreground = h == Native.ForegroundWindow });
            }
            catch (ArgumentException) { }
            return true;
        }, IntPtr.Zero);
        return result;
    }
    delegate bool WindowProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] static extern bool EnumWindows(WindowProc callback, IntPtr state);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr window, System.Text.StringBuilder title, int capacity);
}
