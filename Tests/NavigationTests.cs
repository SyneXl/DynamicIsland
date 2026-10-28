using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DynamicIsland;

static partial class Program
{
    static void NavigationMath()
    {
        Check(PageRing.Next(0, 0, 1) == 0, "no source: Home is the only page");
        Check(PageRing.Next(0, 1, 1) == 1, "Home to single media source");
        Check(PageRing.Next(1, 1, 1) == 0, "single source back to Home");
        Check(PageRing.Next(0, 3, -1) == 3, "Home wraps backwards to last source");
        Check(PageRing.Next(2, 3, 1) == 3, "next media source page");
        Check(PageRing.Next(1, 3, -1) == 0, "first media source to Home");
        Check(PageRing.Next(3, 3, 1) == 0, "last media source to Home");
        Check(PageRing.Next(0, -1, -1) == 0, "empty negative count is bounded");
        Check(PageRing.Next(40, 1, 1) == 0, "stale page index clamped after source removal");
        Check(PageRing.Next(1, 3, 0) == 1, "zero direction preserves page");
    }

    static void SnapWindow(MainWindow window, FrameworkElement root)
    {
        var springs = (Array)typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!;
        foreach (object spring in springs)
        {
            Type t = spring.GetType();
            t.GetMethod("Snap")!.Invoke(spring, [t.GetProperty("Target")!.GetValue(spring)]);
        }
        Call(window, "ApplyShape");
        root.UpdateLayout();
        Pump(550);
    }

    static void HomePresentation(MainWindow window, FrameworkElement root, string? output)
    {
        Type view = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        Type panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        var weather = (WeatherService)typeof(MainWindow).GetField("_weather", Private)!.GetValue(window)!;
        Set(window, "_ringing", false);
        Set(window, "_cinemaMode", false);
        Set(window, "_panel", Enum.Parse(panel, "None"));
        Call(window, "StopTimer");
        Call(window, "SyncCinemaAppearance");
        Set(weather, "<Now>k__BackingField", new WeatherService.Report(-12, -15, 3, Sky.Snow, false, []));
        Call(window, "SyncWeather");
        Call(window, "MorphTo", Enum.Parse(view, "Idle"));
        SnapWindow(window, root);
        var compact = (FrameworkElement)window.FindName("IdleWeather");
        Check(compact.Visibility == Visibility.Visible && ((Digits)window.FindName("IdleDegrees")).Text == "−12°", "idle island shows real formatted weather");
        Check(((Icon)window.FindName("IdleWeatherIcon")).Kind == Glyph.Snow, "idle sky icon follows weather report");
        Check(((FrameworkElement)window.FindName("IdleView")).Width == 176, "idle clock and weather have reserved width");
        if (output != null) Save(root, Path.Combine(output, "home-weather.png"));
        Set(weather, "<Now>k__BackingField", null!);
        Call(window, "SyncWeather");
        Check(compact.Visibility == Visibility.Collapsed && ((FrameworkElement)window.FindName("IdleView")).Width == 118, "missing/off weather collapses without fake temperature");

        Set(window, "_cinemaMode", true);
        Call(window, "SyncCinemaAppearance");
        Call(window, "MorphTo", Enum.Parse(view, "CinemaMedia"));
        Call(window, "SyncEq");
        var eq = (Equalizer)window.FindName("Eq");
        Check(eq.Quiet && eq.Fill == Brushes.White, "cinema spectrum is monochrome and restrained");
        var loop = (FrameLoop)typeof(MainWindow).GetField("_eqLoop", Private)!.GetValue(window)!;
        Check(loop.Running && Math.Abs(loop.ShortestStep - 1.0 / 30) < 0.001, "quiet media keeps a throttled live equalizer");
        for (int i = 0; i < 40; i++) eq.Tick(null, 0.8, true, i * 0.033, 0.033);
        Check(Enumerable.Range(0, 8).Any(i => eq.Level(i) > 0.01), "quiet equalizer still responds to audio levels");
        SnapWindow(window, root);
        loop.Stop();
        for (int i = 0; i < 40; i++) eq.Tick(null, 0.8, true, i * 0.033, 0.033);
        if (output != null) Save(root, Path.Combine(output, "quiet-media.png"));
        Check(!((FrameworkElement)window.FindName("PageNext")).IsVisible, "quiet cinema does not show navigation arrows");
        Set(window, "_hovered", true);
        Call(window, "SyncCinemaAppearance");
        Check(!eq.Quiet, "hover restores full audio visualization");
        Call(window, "SyncEq");
        Check(Math.Abs(loop.ShortestStep - 0.012) < 0.001, "normal spectrum frame interval restored");
        Call(window, "MorphTo", Enum.Parse(view, "IdleBig"));
        SnapWindow(window, root);
        Check(((FrameworkElement)window.FindName("PageNext")).Visibility == Visibility.Collapsed, "no media sessions: arrows have no dead targets");
    }

    static void NavigationIntegration(MediaService model, MpcTimeline adapter, string source, string title, string variant)
    {
        _ = new Application();
        typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
        var window = new MainWindow(); // Off-screen real WPF controls, no Loaded services.
        Set(window, "_media", model);
        model.Changed += () => Call(window, "OnMediaChanged");
        var root = (FrameworkElement)window.FindName("Root");
        root.Opacity = 1;
        var button = (Button)window.FindName("PageNext");
        Type panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        window.Measure(new Size(1000, 1000));
        window.Arrange(new Rect(0, 0, 1000, 1000));
        Call(window, "UpdateClock");
        Set(window, "_lastPlayedAt", DateTime.UtcNow);
        Set(window, "_hovered", true);
        Call(window, "SelectPanel", Enum.Parse(panel, "Player"));
        Call(window, "OnMediaChanged");
        var weather = (WeatherService)typeof(MainWindow).GetField("_weather", Private)!.GetValue(window)!;
        Set(weather, "<Now>k__BackingField", new WeatherService.Report(10, 8, 3, Sky.Overcast, false, []));
        Call(window, "SyncWeather");
        string? output = Environment.GetEnvironmentVariable("ISLAND_TEST_OUTPUT");
        if (output != null) Directory.CreateDirectory(output);
        SnapWindow(window, root);
        if (button.Visibility != Visibility.Visible) throw new Exception("Navigation arrows missing in player");
        if (output != null) Save(root, Path.Combine(output, "player-carousel.png"));
        int count = model.SourceCount;
        int steps = 0;
        var before = adapter.Read(source, title);
        while (!model.HomeSelected && steps++ <= count)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(350);
        }
        if (!model.HomeSelected || Call(window, "TargetView")!.ToString() != "IdleBig") throw new Exception("Arrow did not navigate player -> Home");
        Call(model, "AttachSession"); // Simulate automatic OS session notification while Home is selected.
        Pump(400);
        if (!model.HomeSelected || Call(window, "TargetView")!.ToString() != "IdleBig") throw new Exception("Automatic playback stole Home");
        SnapWindow(window, root);
        if (output != null) Save(root, Path.Combine(output, "home-carousel.png"));
        Call(window, "ShowPanel", Enum.Parse(panel, "None"));
        Type view = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        Call(window, "ShowTransient", Enum.Parse(view, "Toast"), 3.2, false);
        if (Call(window, "TargetView")!.ToString() != "Idle") throw new Exception("Background media stole compact Home");
        SnapWindow(window, root);
        if (output != null) Save(root, Path.Combine(output, "live-home-weather.png"));
        Call(window, "ShowPanel", Enum.Parse(panel, "Player"));
        if (variant.Contains("paused") && adapter.Read(source, title)?.Position != before?.Position) throw new Exception("Page navigation changed paused player position");
        Console.WriteLine($"NAVIGATION PASS: {count} live sources + Home; actual WPF arrow click -> IdleBig; Home survives OS notification; playback untouched");
        var seen = new HashSet<string>();
        for (int i = 0; i < count; i++)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(350);
            if (model.HomeSelected) throw new Exception("Unexpected early Home wrap");
            seen.Add(model.Source);
        }
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(350);
        if (!model.HomeSelected) throw new Exception("Last source did not wrap to Home");
        Console.WriteLine($"NAVIGATION PASS: full forward ring, {seen.Count} distinct source app IDs");
        // Reverse edge from Home, then return to our source without sending playback commands.
        ((Button)window.FindName("PagePrevious")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(350);
        if (model.HomeSelected) throw new Exception("Reverse Home edge did not select last source");
        for (int i = 0; model.Source != source && i <= count + 1; i++)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(350);
        }
        if (model.Source != source || model.HomeSelected) throw new Exception("Own source did not return");
        Pump(600);
        Call(window, "SelectPanel", Enum.Parse(panel, "None"));
        Set(window, "_hovered", false);
        Set(window, "_cinemaMode", true);
        Call(window, "UpdateView");
        if (Call(window, "TargetView")!.ToString() != "CinemaMedia") throw new Exception("Live selected media did not use quiet cinema view");
        SnapWindow(window, root);
        if (output != null) Save(root, Path.Combine(output, "live-quiet-media.png"));
        Set(window, "_hovered", true);
        Call(window, "UpdateView");
        if (Call(window, "TargetView")!.ToString() != "Media") throw new Exception("Hover did not reveal normal media view");
        Pump(300);
        if (Math.Abs(root.Opacity - 1) > 0.01) throw new Exception("Hover did not restore opacity");
        Console.WriteLine("NAVIGATION PASS: reverse wrap, selected paused source stays available, quiet live media -> normal media on hover");
        window.Close();
    }
}
