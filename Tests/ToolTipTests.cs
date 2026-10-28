using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr window, uint command);

    static bool WindowAbove(IntPtr upper, IntPtr lower)
    {
        for (int i = 0; i < 1024 && lower != IntPtr.Zero; i++)
        {
            lower = GetWindow(lower, 3); // GW_HWNDPREV, towards the top of the Z order.
            if (lower == upper) return true;
        }
        return false;
    }

    static Button AutoPingButton(MainWindow window) =>
        (Button)LogicalTreeHelper.GetParent(LogicalTreeHelper.GetParent((DependencyObject)window.FindName("CodexAutoSwitch")));

    static MainWindow ToolTipWindow(bool visible)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
        var window = new MainWindow { Title = "Dynamic Island — Tooltip preview" };
        window.Loaded += (_, _) =>
        {
            window.Left = visible ? 80 : -10000;
            window.Top = visible ? 140 : -10000;
            Call(window, "ShowPanel", Enum.Parse(typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!, "Codex"));
        };
        return window;
    }

    static IEnumerable<string> ToolTipGlyphs(Drawing? drawing)
    {
        if (drawing is GlyphRunDrawing glyph) yield return glyph.GlyphRun.GlyphTypeface.FontUri.ToString();
        if (drawing is DrawingGroup group)
            foreach (Drawing child in group.Children)
                foreach (string font in ToolTipGlyphs(child)) yield return font;
    }

    static IEnumerable<DependencyObject> ToolTipVisuals(DependencyObject node)
    {
        yield return node;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            foreach (var child in ToolTipVisuals(VisualTreeHelper.GetChild(node, i))) yield return child;
    }

    static int ToolTipTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var window = ToolTipWindow(false);
            var owner = AutoPingButton(window);
            Check(ToolTipService.GetInitialShowDelay(owner) == 1200, "tooltip waits 1.2 seconds before showing");
            Check(ToolTipService.GetBetweenShowDelay(owner) == 0, "moving to another control does not bypass the hover delay");
            Check(new DependencyObject[] { new Button(), new TextBlock(), new Grid(), (DependencyObject)window.FindName("CodexFiveValue") }
                .All(o => ToolTipService.GetInitialShowDelay(o) == 1200 && ToolTipService.GetShowDuration(o) == 6000),
                "same timing covers dynamic controls, text and custom digit controls");
            Check(owner.ToolTip is string { Length: <= 75 }, "autoping hint is short rather than a paragraph");
            window.Show(); Pump(700);
            var foreground = Native.ForegroundWindow;
            var tooltip = new ToolTip { PlacementTarget = owner, Placement = PlacementMode.Bottom,
                Content = "Запрос после сброса лимита. Островок должен работать." };
            void Open() { tooltip.IsOpen = true; Pump(180); }
            Open();
            IntPtr parent = new WindowInteropHelper(window).Handle;
            IntPtr popup = ((HwndSource)PresentationSource.FromVisual(tooltip)).Handle;
            Check(tooltip.Style == (Style)window.FindResource("IslandToolTip"), "native popup uses the island tooltip style");
            Check(tooltip.FontSize == 12 && tooltip.FontFamily.Source.Contains("SF Pro Text") && tooltip.MaxWidth == 280,
                "tooltip has the embedded body font and a bounded readable width");
            Check(tooltip.ActualWidth <= 280 && tooltip.ActualHeight < 100, "hint wraps into a compact card instead of a long desktop strip");
            Check(!tooltip.IsHitTestVisible && !tooltip.Focusable && Mouse.Captured == null, "tooltip does not capture pointer or keyboard focus");
            Check(GetWindow(popup, 4) == parent && WindowAbove(popup, parent), "tooltip HWND is owned by and above the island");
            for (int i = 0; i < 5; i++)
            {
                Call(window, "KeepIslandOnTop"); Pump(200);
                Check(tooltip.IsOpen && WindowAbove(popup, parent), "periodic island raise cannot cover its tooltip: " + i);
            }
            Check(Native.ForegroundWindow == foreground, "tooltip and periodic raise do not activate the island");
            var fonts = ToolTipVisuals(tooltip).OfType<Visual>().SelectMany(v => ToolTipGlyphs(VisualTreeHelper.GetDrawing(v))).Distinct().ToArray();
            Check(fonts.Any(f => f.Contains("sf-pro-text-regular.otf", StringComparison.OrdinalIgnoreCase)),
                "Cyrillic hint is actually drawn from the embedded SF Pro font");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(tooltip.ActualWidth), (int)Math.Ceiling(tooltip.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(tooltip);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(Path.Combine(output, "tooltip.png"))) encoder.Save(file);
            File.WriteAllText(Path.Combine(output, "native.json"), JsonSerializer.Serialize(new { parent = parent.ToInt64(), popup = popup.ToInt64(), owned = GetWindow(popup, 4) == parent,
                above = WindowAbove(popup, parent), tooltip.ActualWidth, tooltip.ActualHeight, fonts }));
            tooltip.Content = string.Join(' ', Enumerable.Repeat("Очень длинный путь к выбранной папке", 50)); Pump(150);
            Check(tooltip.ActualWidth <= 280 && tooltip.ActualHeight <= 100,
                "unexpectedly long dynamic text cannot grow into an oversized tooltip");
            tooltip.Content = "Запрос после сброса, пока островок работает.";
            Call(window, "MorphTo", Enum.Parse(typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!, "Look"));
            Check(!tooltip.IsOpen, "page change closes a tooltip from the previous page");
            Open();
            window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
            Check(!tooltip.IsOpen, "scroll dismisses an open tooltip");
            Open();
            window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(!tooltip.IsOpen, "press dismisses a tooltip without waiting for its duration");
            Open(); window.Hide();
            Check(!tooltip.IsOpen, "hiding the island also hides its popup");
            window.Show(); Open(); window.Close(); Pump(150);
            Check(!tooltip.IsOpen, "closing the owner leaves no orphan tooltip");
            Console.WriteLine($"TOOLTIP PASS: {_checks} checks; real popup HWND; no live integrations or settings writes; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int ToolTipPreview(string output)
    {
        Directory.CreateDirectory(output);
        var window = ToolTipWindow(true);
        window.PreviewMouseDown += (_, e) => e.Handled = true; // Preview is read-only: no quota requests or toggle changes.
        var button = AutoPingButton(window);
        long entered = 0;
        void Record(object item) => File.AppendAllText(Path.Combine(output, "hover.jsonl"), JsonSerializer.Serialize(item) + "\n");
        button.MouseEnter += (_, _) => { entered = Environment.TickCount64; Record(new { action = "enter", at = entered }); };
        button.MouseLeave += (_, _) => Record(new { action = "leave", at = Environment.TickCount64 });
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler((sender, _) =>
        {
            var tip = (ToolTip)sender;
            if (Window.GetWindow(tip.PlacementTarget) != window) return;
            Record(new { action = "open", at = Environment.TickCount64, hoverMs = Environment.TickCount64 - entered, content = tip.Content });
        }));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => Call(window, "KeepIslandOnTop"); timer.Start();
        var close = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
        close.Tick += (_, _) => window.Close(); close.Start();
        window.Closed += (_, _) => { timer.Stop(); close.Stop(); Application.Current.Shutdown(); };
        Application.Current.Run(window);
        return 0;
    }

    // The same native experiment also compiles against the pre-fix source tree.
    static int ToolTipLayerProbe(string output)
    {
        try
        {
            var window = ToolTipWindow(false);
            window.Show(); Pump(650);
            var tooltip = new ToolTip { PlacementTarget = AutoPingButton(window), Content = "Подсказка", Placement = PlacementMode.Bottom };
            tooltip.IsOpen = true; Pump(200);
            IntPtr parent = new WindowInteropHelper(window).Handle;
            IntPtr popup = ((HwndSource)PresentationSource.FromVisual(tooltip)).Handle;
            bool owned = GetWindow(popup, 4) == parent;
            var samples = new List<bool> { WindowAbove(popup, parent) };
            for (int i = 0; i < 5; i++)
            {
                Native.KeepOnTop(parent);
                ((Glass)typeof(MainWindow).GetField("_glass", Private)!.GetValue(window)!).Place();
                Pump(150);
                samples.Add(WindowAbove(popup, parent));
            }
            tooltip.IsOpen = false; window.Close();
            File.WriteAllText(output, JsonSerializer.Serialize(new { owned, aboveBeforeAndAfterOwnerRaise = samples }));
            Console.WriteLine($"LAYER PROBE: owned={owned}; above={string.Join(',', samples)}; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
