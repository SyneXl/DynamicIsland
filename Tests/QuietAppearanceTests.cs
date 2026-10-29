using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    static int QuietAppearanceTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            QuietDeadlineTests();
            QuietWindowTests(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                externalServices = false, registryWrites = false, activityTracking = false, screenCapture = false }));
            Console.WriteLine($"PASS: {_checks} quiet appearance checks; exit=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void QuietDeadlineTests()
    {
        var state = new QuietAppearanceState();
        Check(!state.Update(false, 0) && !state.Pending, "disabled/ineligible appearance never starts a timer");
        Check(!state.Update(true, 1) && state.Pending, "eligible compact state starts the quiet delay");
        Check(!state.Update(true, 1.4) && Math.Abs(state.Remaining - .25) < .00001, "refreshes preserve the original deadline");
        Check(state.Update(true, 1.65) && !state.Pending, "quiet appearance activates after 650ms");
        Check(state.Update(true, 200), "stable passive state does not keep rescheduling work");
        Check(!state.Update(false, 201), "hover/focus/notification releases quiet appearance immediately");
        Check(!state.Update(true, 202) && !state.Update(true, 202.4), "leaving starts a fresh delay, never reuses old elapsed time");
        state.Update(false, 202.5);
        Check(!state.Update(true, 202.6) && !state.Update(true, 203), "rapid re-entry cancels an earlier pending fade");
        Check(state.Update(true, 203.25), "fresh eligibility eventually dims without needing another input event");
        for (int i = 0; i < 50; i++)
        {
            state.Update(false, 300 + i);
            state.Update(true, 300 + i);
            Check(!state.Update(false, 300.1 + i) && !state.Pending, "cancelled deadline cannot reactivate a passive style " + i);
        }
    }

    static void QuietWindowTests(string output)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        var quietSetting = typeof(Settings).GetField("_quietWhenIdle", BindingFlags.Static | BindingFlags.NonPublic)!;
        var glassSetting = typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!;
        object? wasQuiet = quietSetting.GetValue(null), wasGlass = glassSetting.GetValue(null);
        quietSetting.SetValue(null, false); glassSetting.SetValue(null, false);
        var window = new MainWindow();
        window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
        window.Show(); Pump(400);
        var root = (FrameworkElement)window.FindName("Root");
        var body = (FrameworkElement)window.FindName("Body");
        var content = (FrameworkElement)window.FindName("IslandContent");
        var eq = (Equalizer)window.FindName("Eq");
        var media = (MediaService)typeof(MainWindow).GetField("_media", Private)!.GetValue(window)!;
        var obs = typeof(MainWindow).GetField("_obs", Private)!.GetValue(window)!;
        var eqLoop = (FrameLoop)typeof(MainWindow).GetField("_eqLoop", Private)!.GetValue(window)!;
        var panelType = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        var viewType = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        string Mode() => typeof(MainWindow).GetField("_passiveAppearance", Private)!.GetValue(window)!.ToString()!;
        string View() => Call(window, "TargetView")!.ToString()!;
        void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panelType, name)); }
        void Sync() => Call(window, "UpdateView");
        void Near(double actual, double expected, string name) => Check(Math.Abs(actual - expected) < .015, name);
        void Invalidate(DependencyObject node)
        {
            if (node is UIElement element) element.InvalidateVisual();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Invalidate(VisualTreeHelper.GetChild(node, i));
        }
        void Capture(string name)
        {
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape"); root.UpdateLayout(); Pump(300);
            eqLoop.Stop();
            if (View() == "Media") for (int i = 0; i < 40; i++) eq.Tick(null, .8, true, i * .033, .033);
            Invalidate(root); root.UpdateLayout(); Pump(80);
            Save(root, Path.Combine(output, name + ".png"));
        }
        try
        {
            Check(!Settings.QuietWhenIdle && Mode() == "Normal", "opt-in setting leaves the ordinary default appearance unchanged");
            var row = (Button)window.FindName("QuietAppearanceRow");
            var label = ((Grid)row.Content).Children.OfType<TextBlock>().Single();
            Check(label.FontFamily.Equals(window.FontFamily), "appearance option inherits the island SF Pro font");
            int saves = 0;
            Set(window, "_saveQuietAppearance", (Func<bool, bool>)(value => { saves++; quietSetting.SetValue(null, value); return true; }));
            Page("Look"); Pump(350);
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Settings.QuietWhenIdle && saves == 1, "one appearance toggle saves the explicit choice without changing cinema settings");
            Check(Mode() == "Normal", "the appearance editor never dims itself");
            root.UpdateLayout();
            var toggle = (FrameworkElement)window.FindName("QuietAppearanceSwitch");
            double labelRight = label.TransformToAncestor(row).Transform(new Point(label.ActualWidth, 0)).X;
            double toggleLeft = toggle.TransformToAncestor(row).Transform(new Point()).X;
            var measuredLabel = new FormattedText(label.Text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, Brushes.White, 1);
            Check(labelRight + 2 <= toggleLeft && label.ActualWidth + .1 >= measuredLabel.WidthIncludingTrailingWhitespace,
                $"full setting label fits without overlapping its toggle (text={measuredLabel.WidthIncludingTrailingWhitespace:0}, room={label.ActualWidth:0})");
            Capture("appearance-option");
            Page("None"); Call(window, "UpdateClock"); Pump(300);
            Check(Mode() == "Normal", "leaving a panel does not start fading immediately");
            Pump(700);
            Check(Mode() == "Quiet" && View() == "Idle", "idle clock enters passive appearance after its delay");
            Near(root.Opacity, 1, "quiet mode never fades the entire root like cinema");
            Near(body.Opacity, .58, "background has its own moderate transparency");
            Near(content.Opacity, .9, "text/content retain much higher opacity than the background");
            Check(!eqLoop.Running, "idle quiet clock does not run an equalizer animation loop");
            var weather = (WeatherService)typeof(MainWindow).GetField("_weather", Private)!.GetValue(window)!;
            Set(weather, "<Now>k__BackingField", new WeatherService.Report(10, 8, 3, Sky.Cloudy, false, []));
            Call(window, "SyncWeather");
            Capture("quiet-clock-weather");
            Check(((Digits)window.FindName("IdleDegrees")).Text == "+10°" && ((FrameworkElement)window.FindName("IdleView")).Width == 176,
                "quiet mode retains clock/weather text and the ordinary compact width");
            Set(window, "_hovered", true); Sync(); Pump(210);
            Near(body.Opacity, 1, "hover restores black background within 180ms");
            Near(content.Opacity, 1, "hover restores full content opacity");
            Capture("revealed-clock-weather");
            Set(window, "_hovered", false); Sync(); Pump(300);
            Set(window, "_bubbleHovered", true); Sync(); Pump(500);
            Check(Mode() == "Normal", "crossing the gap to the side bubble cancels a pending fade");
            Set(window, "_bubbleHovered", false); Sync(); Pump(1000);
            Check(Mode() == "Quiet", "leaving both hit regions eventually dims again");

            Set(media, "<IsLoading>k__BackingField", true); Sync();
            string text = "Полная строка остаётся на месте — ничего не сокращаем";
            Call(window, "ShowLyric", text, 8d, true, false);
            double mediaWidth = ((Spring)typeof(MainWindow).GetField("_width", Private)!.GetValue(window)!).Target;
            Pump(400);
            Check(View() == "Media" && Mode() == "Quiet" && mediaWidth > 210, "passive media uses the ordinary text-sized Media view, not CinemaMedia");
            Check(new[] { (CompactLyric)window.FindName("LyricA"), (CompactLyric)window.FindName("LyricB") }.Any(l => l.Text == text),
                "compact lyric/title text survives passive appearance unchanged");
            Check(eq.Quiet && ReferenceEquals(eq.Fill, Brushes.White) && Math.Abs(eqLoop.ShortestStep - 1d / 30) < .001,
                "media visualizer becomes monochrome, restrained and capped at 30fps");
            Check(eq.KeepStyleWhenQuiet, "passive media preserves the user's bars/matrix choice");
            eqLoop.Stop();
            for (int i = 0; i < 40; i++) eq.Tick(null, .8, true, i * .033, .033);
            Check(Enumerable.Range(0, 8).Any(i => eq.Level(i) > .01), "passive visualizer still responds to audio instead of freezing");
            Capture("quiet-media-full-text");
            Call(window, "StartTimer", TimeSpan.FromMinutes(2)); Pump(300);
            Check(((FrameworkElement)window.FindName("Bubble")).Visibility == Visibility.Visible, "side timer bubble is retained next to passive media");
            Near(((FrameworkElement)window.FindName("BubbleBody")).Opacity, .9, "side bubble content uses the same readable passive opacity");
            Set(window, "_bubbleHovered", true); Sync(); Pump(210);
            Near(((FrameworkElement)window.FindName("BubbleBody")).Opacity, 1, "hovering a side bubble reveals both parts together");
            Set(window, "_bubbleHovered", false); Call(window, "StopTimer"); Sync(); Pump(1000);
            Set(window, "_hovered", true); Sync(); Pump(210);
            Check(!eq.Quiet && Math.Abs(eqLoop.ShortestStep - .012) < .001 && View() == "Media", "hover restores ordinary visualization without replacing the media view");
            Near(((Spring)typeof(MainWindow).GetField("_width", Private)!.GetValue(window)!).Target, mediaWidth, "hover changes appearance, not media width");
            Capture("revealed-media-full-text");
            Page("Player"); Set(window, "_hovered", false); Sync(); Pump(1000);
            Check(View() == "MediaBig" && Mode() == "Normal", "expanded player remains opaque without needing hover");
            foreach (string panel in new[] { "Menu", "Look", "Settings", "Shelf", "Mixer", "Throne" })
            {
                Page(panel); Pump(40); Check(Mode() == "Normal", "open panel is never dimmed: " + panel);
            }
            Set(media, "<IsLoading>k__BackingField", false); Page("None"); Pump(1000);
            foreach (string view in new[] { "Volume", "Charge", "Headset", "Focus", "Notice" })
            {
                Call(window, "ShowTransient", Enum.Parse(viewType, view), 5d, false); Pump(210);
                Check(Mode() == "Normal" && View() == view, "transient announcement restores normal styling: " + view);
                Call(window, "EndTransient");
            }
            Pump(1000);
            Call(window, "StartTimer", TimeSpan.FromMinutes(2)); Pump(1000);
            Check(View() == "Timer" && Mode() == "Quiet", "ordinary running timer can be passive without losing its digits");
            Call(window, "SetUrgent", true); Pump(350);
            Check(Mode() == "Normal", "last ten running timer seconds restore attention immediately");
            Call(window, "StopTimer"); Set(window, "_ringing", true); Sync(); Pump(1000);
            Check(Mode() == "Normal", "ringing alarm can never become passive");
            Set(window, "_ringing", false); Sync(); Pump(1000);
            Set(obs, "<Recording>k__BackingField", true); Call(window, "SyncRecord"); Sync(); Pump(300);
            Check(View() == "Record" && Mode() == "Quiet", "recording status preserves its compact view");
            var tint = (SolidColorBrush)typeof(MainWindow).GetField("_recordTint", Private)!.GetValue(window)!;
            Check(tint.Color.R == tint.Color.G || Math.Abs(tint.Color.R - tint.Color.G) < 10, "recording accent becomes neutral while passive");
            Set(obs, "<Paused>k__BackingField", true); Call(window, "SyncRecord"); Pump(350);
            Check(tint.Color.R < 220 && tint.Color.B > 190, "OBS pause updates do not reintroduce an orange accent into passive mode");
            Set(window, "_hovered", true); Sync(); Pump(210);
            Check(tint.Color.R > tint.Color.B, "hover restores the correct paused recording accent");
            Set(obs, "<Recording>k__BackingField", false); Set(obs, "<Paused>k__BackingField", false);
            Set(window, "_hovered", false); Set(window, "_cinemaMode", true); Sync(); Pump(350);
            Check(Mode() == "Cinema", "cinema retains precedence over the optional appearance mode");
            Near(root.Opacity, .28, "cinema retains its existing root opacity");
            Near(body.Opacity, .48, "cinema retains its existing background opacity");
            Near(content.Opacity, 1, "quiet content opacity never multiplies cinema opacity");
            Set(window, "_cinemaMode", false); Sync(); Pump(210);
            Check(Mode() == "Normal", "fullscreen exit restores the normal appearance before a fresh passive delay");
            Pump(800); Check(Mode() == "Quiet", "passive appearance resumes after leaving cinema");
            foreach (string blocker in new[] { "_pressed", "_bubblePressed", "_pickingArea", "_pickingFiles", "_dragOver", "_hiddenByFullscreen", "_sentAway" })
            {
                Set(window, blocker, true); Sync(); Check(Mode() == "Normal", "interaction/hidden state blocks passive style: " + blocker);
                Set(window, blocker, false); Sync();
            }
            Page("Look");
            Set(window, "_saveQuietAppearance", (Func<bool, bool>)(_ => false));
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Settings.QuietWhenIdle, "failed preference save does not pretend the setting was disabled");
            Set(window, "_saveQuietAppearance", (Func<bool, bool>)(value => { quietSetting.SetValue(null, value); return true; }));
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Page("None"); Pump(1000);
            Check(!Settings.QuietWhenIdle && Mode() == "Normal" && !eq.Quiet, "disabling the option restores ordinary styling and cancels its delayed transition");
            Near(root.Opacity, 1, "normal root opacity is restored after opt-out"); Near(body.Opacity, 1, "normal background opacity is restored after opt-out");
            Near(content.Opacity, 1, "normal content opacity is restored after opt-out");
            Check(typeof(SpectrumService).GetField("_thread", Private)!.GetValue(typeof(MainWindow).GetField("_spectrum", Private)!.GetValue(window)) == null,
                "appearance fixtures never start real audio capture");
            // An already-due appearance callback must not overwrite the exit fade.
            Set(window, "_appearanceExiting", true); Call(window, "SyncCinemaAppearance");
            Check(Mode() == "Normal", "shutdown guard prevents delayed callbacks from restarting appearance animations");
            CompositeQuietPreviews(output);
            QuietEqualizerStyles();
            Set(window, "_appearanceExiting", false); quietSetting.SetValue(null, true); Sync();
            Check((bool)typeof(MainWindow).GetField("_quietAppearanceScheduled", Private)!.GetValue(window)!, "close fixture has an actual pending appearance deadline");
        }
        finally { window.Close(); quietSetting.SetValue(null, wasQuiet); glassSetting.SetValue(null, wasGlass); }
        Pump(800);
        Check(!((bool)typeof(MainWindow).GetField("_quietAppearanceScheduled", Private)!.GetValue(window)!), "closing leaves no pending appearance callback");
    }

    static void CompositeQuietPreviews(string output)
    {
        foreach (string name in new[] { "quiet-clock-weather", "revealed-clock-weather", "quiet-media-full-text", "revealed-media-full-text" })
        {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(Path.GetFullPath(Path.Combine(output, name + ".png"))); image.EndInit(); image.Freeze();
            foreach ((string background, Color color) in new[] { ("light", Color.FromRgb(242, 242, 247)), ("dark", Color.FromRgb(49, 51, 56)) })
            {
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, 620, 100));
                    dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
                }
                var bitmap = new RenderTargetBitmap(620, 100, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, name + "-" + background + ".png")); png.Save(stream);
            }
        }
    }

    static void QuietEqualizerStyles()
    {
        var eq = new Equalizer { Width = 30, Height = 24, Dots = true, KeepStyleWhenQuiet = true, Quiet = true };
        eq.Measure(new Size(30, 24)); eq.Arrange(new Rect(0, 0, 30, 24));
        Pump(280);
        int Ellipses(Drawing? drawing)
        {
            if (drawing is GeometryDrawing { Geometry: EllipseGeometry }) return 1;
            return drawing is DrawingGroup group ? group.Children.Sum(Ellipses) : 0;
        }
        void Render()
        {
            eq.InvalidateVisual(); eq.UpdateLayout();
            new RenderTargetBitmap(30, 24, 96, 96, PixelFormats.Pbgra32).Render(eq);
        }
        Render(); Check(Ellipses(VisualTreeHelper.GetDrawing(eq)) > 0, "passive matrix really renders dots, not replacement bars");
        eq.KeepStyleWhenQuiet = false; Render();
        Check(Ellipses(VisualTreeHelper.GetDrawing(eq)) == 0, "cinema retains its existing quiet bars even when the normal style is matrix");
    }
}
