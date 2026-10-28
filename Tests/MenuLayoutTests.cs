using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using DynamicIsland;

static partial class Program
{
    // In-memory, no-services preview for native UI inspection. Auto-closes in three minutes.
    static int MenuPreview()
    {
        var app = new Application();
        typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
        var window = new MainWindow { Title = "Dynamic Island — Menu preview" };
        Set(window, "_menuLayout", MenuLayout.Default());
        Set(window, "_saveMenuLayout", (Func<MenuLayout, bool>)(_ => true));
        Call(window, "SyncMenuLayout");
        window.Loaded += (_, _) => Call(window, "ShowPanel", Enum.Parse(typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!, "MenuLayout"));
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        timer.Tick += (_, _) => { timer.Stop(); window.Close(); };
        timer.Start();
        app.Run(window);
        return 0;
    }

    static int MenuWindowTest(string output)
    {
        try
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
            typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
            Directory.CreateDirectory(output);
            var window = new MainWindow();
            window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
            window.Show();
            Pump(500);
            Check(window.IsLoaded && new WindowInteropHelper(window).Handle != IntPtr.Zero, "menu integration uses a real HWND and Loaded controls without external services");
            var root = (FrameworkElement)window.FindName("Root");
            MenuLayoutPresentation(window, root, output);
            ZapretPresentation(window, root, output);
            window.Close();
            Console.WriteLine($"MENU WINDOW PASS: {_checks} checks; no registry writes or live integrations; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void MenuLayoutTests()
    {
        MenuLayout layout = MenuLayout.Default();
        string[] defaults = ["timer", "obs", "zapret", "shelf", "weather", "qr", "codex", "settings", "appearance", "exit"];
        Check(layout.Entries.Select(e => e.Id).SequenceEqual(defaults), "menu preserves the existing default order");
        Check(layout.IsDefault && layout.VisibleCount == 10, "first launch keeps all registered menu entries");
        string[] legacy = ["timer", "obs", "shelf", "weather", "qr", "codex", "settings", "appearance", "exit"];
        string Legacy(string[] ids, int version = 1) => System.Text.Json.JsonSerializer.Serialize(new { Version = version, Entries = ids.Select(id => new MenuEntry(id, id != "obs")).ToArray() });
        var migrated = MenuLayout.Parse(Legacy(legacy.Append("zapret").ToArray()));
        Check(migrated.Entries.Select(e => e.Id).SequenceEqual(defaults) && !migrated.Entries.Single(e => e.Id == "obs").Visible,
            "legacy Zapret appended below Exit migrates beside OBS without resetting visibility");
        Check(MenuLayout.Parse(Legacy(legacy)).Entries.Select(e => e.Id).SequenceEqual(defaults),
            "older menu missing Zapret inserts it beside integrations rather than after Exit");
        Check(MenuLayout.Parse(migrated.Serialize()).Entries.SequenceEqual(migrated.Entries), "migration roundtrip is stable in menu format v2");
        var explicitTail = MenuLayout.Parse(Legacy(legacy.Append("zapret").ToArray(), 2));
        Check(explicitTail.Entries[^1].Id == "zapret", "new explicit tail placement remains the user's choice");
        var custom = new[] { "timer", "obs", "codex", "weather", "zapret", "qr", "shelf", "settings", "appearance", "exit" };
        Check(MenuLayout.Parse(Legacy(custom)).Entries.Select(e => e.Id).SequenceEqual(custom), "existing custom menu sort is not replaced by the new default");
        layout = layout.SetVisible("obs", false).Move("codex", -6).Move("exit", -2);
        Check(layout.Entries[0].Id == "codex" && !layout.Entries.Single(e => e.Id == "obs").Visible, "menu hides OBS and reorders independently");
        Check(layout.Entries[7].Id == "exit", "essential entries can also be reordered");
        Check(MenuLayout.Parse(layout.Serialize()).Entries.SequenceEqual(layout.Entries), "menu order and visibility survive serialization/restart");
        foreach (string id in new[] { "settings", "appearance", "exit" })
            Check(layout.SetVisible(id, false).Entries.Single(e => e.Id == id).Visible, "menu cannot hide essential entry: " + id);
        Check(layout.Move("absent", 1).Entries.SequenceEqual(layout.Entries), "unknown menu movement is harmless");
        Check(layout.SetVisible("absent", false).Entries.SequenceEqual(layout.Entries), "unknown menu toggle is harmless");
        Check(layout.Move("obs", int.MinValue).Entries[0].Id == "obs", "menu move clamps safely at the first item");
        Check(layout.Move("obs", int.MaxValue).Entries[^1].Id == "obs", "menu move clamps safely at the last item");
        foreach (string? damaged in new[] { null, "", "broken", "null", "[]", "{\"Version\":2,\"Entries\":[]}",
                     "{\"Version\":1,\"Entries\":null}", "{\"Version\":1,\"Entries\":[{\"Id\":\"obs\",\"Visible\":\"no\"}]}", new string(' ', 65_537) })
            Check(MenuLayout.Parse(damaged).IsDefault, "corrupt/unsupported menu data falls back without losing essentials");
        var repaired = MenuLayout.Parse("""
            {"Version":1,"Entries":[null,{"Id":null},{"Id":"unknown","Visible":false},
            {"Id":"obs","Visible":false},{"Id":"obs","Visible":true},{"Id":"settings","Visible":false}]}
            """);
        Check(repaired.Entries.Count == 10 && repaired.Entries.Select(e => e.Id).Distinct().Count() == 10,
            "menu repair removes unknown/null/duplicate IDs and fills omitted entries");
        Check(repaired.Entries[0] == new MenuEntry("obs", false) && repaired.Entries[2] == new MenuEntry("settings"),
            "menu repair keeps first preference and force-enables protected routes");
        var futureCatalog = MenuLayout.Catalog.Append(new MenuDefinition("future", "Future", Glyph.Note)).ToArray();
        var future = MenuLayout.Parse(layout.Serialize(), futureCatalog);
        Check(future.Entries.Take(defaults.Length).SequenceEqual(layout.Entries) && future.Entries[^1] == new MenuEntry("future"),
            "a new integration is appended without resetting previous order or hidden preferences");
        Check(layout.SetVisible("obs", true).Entries.Select(e => e.Id).SequenceEqual(layout.Entries.Select(e => e.Id)),
            "showing a hidden entry restores its chosen position");
        Check(!layout.IsDefault && MenuLayout.Default().IsDefault, "menu-only reset has a stable default");
        var random = new Random(20261028);
        for (int i = 0; i < 600; i++)
        {
            string id = defaults[random.Next(defaults.Length)];
            layout = random.Next(2) == 0 ? layout.Move(id, random.Next(-15, 16)) : layout.SetVisible(id, random.Next(2) == 0);
            layout = MenuLayout.Parse(layout.Serialize());
            if (layout.Entries.Count != defaults.Length || layout.Entries.Select(e => e.Id).Distinct().Count() != defaults.Length
                || MenuLayout.Catalog.Where(d => d.Required).Any(d => !layout.Entries.Single(e => e.Id == d.Id).Visible))
                throw new Exception("FAIL: randomized menu invariants");
        }
        Check(true, "600 mixed reorder/toggle/restart operations preserve uniqueness and protected routes");
    }

    static void MenuLayoutPresentation(MainWindow window, FrameworkElement root, string? output)
    {
        var rows = (Panel)window.FindName("MenuRows");
        var editorRows = (StackPanel)window.FindName("MenuEditorRows");
        var editorScroll = (ScrollViewer)window.FindName("MenuEditorScroll");
        var menuScroll = (ScrollViewer)window.FindName("MenuScroll");
        var menuView = (FrameworkElement)window.FindName("MenuView");
        var editorView = (FrameworkElement)window.FindName("MenuLayoutView");
        var oldLayout = typeof(MainWindow).GetField("_menuLayout", Private)!.GetValue(window)!;
        var saveField = typeof(MainWindow).GetField("_saveMenuLayout", Private)!;
        var oldSave = saveField.GetValue(window);
        string? saved = null;
        int saves = 0;
        Func<MenuLayout, bool> save = layout => { saved = layout.Serialize(); saves++; return true; };
        saveField.SetValue(window, save);
        var views = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        var panels = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        string[] VisibleIds() => rows.Children.OfType<Button>().Where(b => b.Visibility == Visibility.Visible).Select(b => (string)b.Tag).ToArray();
        Button EditorButton(string id, int index = 0) => ((Grid)editorRows.Children.OfType<Grid>().Single(g => (string)g.Tag == id)).Children.OfType<Button>().ElementAt(index);
        void Click(Button b) { Check(b.IsEnabled, "menu test targets an enabled control"); b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
        void Page(string panel)
        {
            Call(window, "ShowPanel", Enum.Parse(panels, panel));
            Pump(650);
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape");
            root.UpdateLayout();
        }
        void Capture(string file)
        {
            if (output == null) return;
            // Off-screen RenderTargetBitmap also needs previously cached template
            // visuals invalidated, not just the one last touched by mouse/scroll.
            void Invalidate(DependencyObject node)
            {
                if (node is UIElement element) element.InvalidateVisual();
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Invalidate(VisualTreeHelper.GetChild(node, i));
            }
            Invalidate(root);
            root.UpdateLayout();
            Pump(120);
            Save(root, Path.Combine(output, file));
        }
        try
        {
            Set(window, "_ringing", false);
            Set(window, "_cinemaMode", false);
            Set(window, "_menuLayout", MenuLayout.Default());
            Call(window, "SyncMenuLayout");
            Page("Menu");
            var originalButtons = rows.Children.OfType<Button>().ToDictionary(b => (string)b.Tag);
            Check(VisibleIds().Length == 10 && Math.Abs(menuView.Height - 448) < 0.01, "default WPF menu sizes to the registered entries");
            Check(window.FindName("MenuLayoutRow") is Button, "menu editor has a direct Appearance entry");
            Page("Look");
            Click((Button)window.FindName("MenuLayoutRow"));
            Check(Call(window, "TargetView")!.ToString() == "MenuLayout", "Appearance row opens the dedicated menu editor");
            Page("MenuLayout");
            var menuTitle = ((Grid)rows.Children.OfType<Button>().Single(b => (string)b.Tag == "timer").Content).Children.OfType<TextBlock>().First();
            var editorTitles = editorRows.Children.OfType<Grid>()
                .Select(r => ((Grid)((Border)r.Children[0]).Child).Children.OfType<TextBlock>().Single()).ToArray();
            Check(editorTitles.Length == 10 && editorTitles.All(t => t.FontSize == menuTitle.FontSize && t.FontFamily.Equals(menuTitle.FontFamily)
                && t.FontWeight == menuTitle.FontWeight), "every menu editor title uses the main menu's SF Pro typography and 13.5 px size");
            var editorContent = (Grid)((Grid)editorView).Children[0];
            var status = (TextBlock)window.FindName("MenuLayoutStatus");
            Check(editorContent.RowDefinitions.Count == 3 && editorContent.Children.OfType<TextBlock>().SequenceEqual([status]),
                "menu editor has no top instructions or permanent bottom caption");
            Check(status.Visibility == Visibility.Collapsed && status.Text.Length == 0 && editorView.Height == 408,
                "normal menu editor is compact with no reserved space for removed captions");
            Check(editorRows.Children.Count == 10 && editorView.Height <= 500, "menu editor is bounded and includes visible plus hidden rows");
            Check(editorScroll.ScrollableHeight > 0, "menu editor scrolls rather than overflowing the island");
            var scrollbar = (ScrollBar?)editorScroll.Template.FindName("PART_VerticalScrollBar", editorScroll);
            Console.WriteLine($"Menu scrollbar: actual={scrollbar?.ActualWidth}; width={scrollbar?.Width}; min={scrollbar?.MinWidth}");
            Check(scrollbar is { ActualWidth: <= 6.1 },
                "menu editor uses a narrow scrollbar rather than the system's wide light track");
            Check(!EditorButton("settings").IsEnabled && !EditorButton("appearance").IsEnabled && !EditorButton("exit").IsEnabled,
                "essential rows expose no hide action in WPF");
            Check(editorRows.Children.OfType<Grid>().All(r => r.Children.OfType<Button>().Count() == 2)
                && EditorButton("exit", 1).IsEnabled, "arrows are removed; every row including required entries has a drag grip");
            Check(EditorButton("obs").Focusable && EditorButton("obs", 1).Focusable, "editor controls support keyboard focus");
            Capture("menu-editor-default.png");
            Click(EditorButton("obs"));
            Check(!VisibleIds().Contains("obs") && saves == 1, "OBS hide is applied and saved immediately");
            Check(!((TextBlock)window.FindName("MenuLayoutStatus")).Text.Contains("Не удалось"), "successful save gives a quiet consistent status");
            Check(editorRows.Children.Count == 10, "hidden OBS remains in the editor for re-enabling");
            Call(window, "MoveMenuItem", "codex", -1);
            Check(VisibleIds()[4] == "codex", "keyboard reorder still changes the actual menu");
            Check(rows.Children.OfType<Button>().All(b => ReferenceEquals(b, originalButtons[(string)b.Tag])),
                "reorder preserves original buttons, live counters and event routes");
            foreach (string id in new[] { "timer", "shelf", "weather", "qr", "codex", "zapret" }) Click(EditorButton(id));
            Check(VisibleIds().SequenceEqual(new[] { "settings", "appearance", "exit" }) && menuView.Height == 168,
                "hiding every optional entry leaves a compact menu with all recovery routes");
            Check(((TextBlock)window.FindName("MenuLayoutSummary")).Text == "3 из 10", "Appearance summary reflects current visible count");
            Page("Menu");
            Capture("menu-minimal.png");
            Page("MenuLayout");
            Call(window, "MoveMenuItem", "obs", -1);
            Click(EditorButton("obs"));
            Check(VisibleIds()[0] == "obs" && menuView.Height == 208, "hidden entry can move and reappear at its selected position");
            string beforeFailure = saved!;
            saveField.SetValue(window, (Func<MenuLayout, bool>)(_ => false));
            Click(EditorButton("weather"));
            Check(!VisibleIds().Contains("weather") && saved == beforeFailure, "failed persistence does not pretend the new layout was saved");
            Check(((TextBlock)window.FindName("MenuLayoutStatus")).Text.Contains("Не удалось"), "failed persistence is visible without closing the editor");
            Check(status.Visibility == Visibility.Visible && editorView.Height > 408 && !string.IsNullOrWhiteSpace(status.Text),
                "save error alone expands the editor rather than failing silently");
            Page("MenuLayout");
            Check(status.ActualHeight > 0 && status.TransformToAncestor(editorView).Transform(new Point(0, status.ActualHeight)).Y <= editorView.ActualHeight,
                "conditional save error fits inside the compact editor");
            Capture("menu-editor-save-error.png");
            saveField.SetValue(window, save);
            var settingsField = typeof(Settings).GetField("_menu", BindingFlags.Static | BindingFlags.NonPublic)!;
            object? settingsWas = settingsField.GetValue(null);
            try
            {
                settingsField.SetValue(null, MenuLayout.Parse(saved));
                var reopened = new MainWindow();
                Check(((Panel)reopened.FindName("MenuRows")).Children.OfType<Button>()
                    .Where(b => b.Visibility == Visibility.Visible).Select(b => (string)b.Tag).SequenceEqual(VisibleIds()),
                    "new WPF window restores persisted order and hidden entries");
                reopened.Close();
                Check(!Application.Current.Dispatcher.HasShutdownStarted, "reopening/closing a settings preview does not stop the owning dispatcher");
            }
            finally { settingsField.SetValue(null, settingsWas); }
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            editorScroll.RaiseEvent(wheel);
            root.UpdateLayout();
            Check(wheel.Handled && editorScroll.VerticalOffset > 0, "editor wheel scrolls and is consumed before any volume handler");
            wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            menuScroll.RaiseEvent(wheel);
            Check(wheel.Handled, "even a short menu consumes the wheel instead of changing volume");
            wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.MouseWheelEvent };
            Call(window, "Root_MouseWheel", root, wheel);
            Check(wheel.Handled && Call(window, "TargetView")!.ToString() == "MenuLayout", "editor header/footer wheel neither changes volume nor switches media sources");
            int beforeDragSaves = saves;
            string[] beforeDrag = editorRows.Children.OfType<Grid>().Select(r => (string)r.Tag).ToArray();
            int from = Array.IndexOf(beforeDrag, "exit");
            Call(window, "BeginMenuReorder", "exit", from * 44.0 + 12, 12.0);
            Call(window, "UpdateMenuReorder", 12.0);
            Check(saves == beforeDragSaves && ((Grid)editorRows.Children[0]).Tag.Equals("exit"), "drag previews order without persisting intermediate moves");
            Call(window, "EndMenuReorder", true);
            Check(saves == beforeDragSaves + 1 && VisibleIds()[0] == "exit", "drop saves exactly once and can reorder a required entry");
            string priorCancel = saved!;
            Call(window, "BeginMenuReorder", "exit", 12.0, 12.0);
            Call(window, "UpdateMenuReorder", 9 * 44.0 + 12);
            Call(window, "EndMenuReorder", false);
            Check(saved == priorCancel && VisibleIds()[0] == "exit", "cancel/lost capture restores order without saving");
            saveField.SetValue(window, (Func<MenuLayout, bool>)(_ => false));
            Call(window, "BeginMenuReorder", "exit", 12.0, 12.0);
            Call(window, "UpdateMenuReorder", 9 * 44.0 + 12);
            Call(window, "EndMenuReorder", true);
            Check(VisibleIds()[0] == "exit" && ((Grid)editorRows.Children[0]).Tag.Equals("exit"), "failed drop persistence restores both menu and editor order");
            saveField.SetValue(window, save);
            Click((Button)window.FindName("MenuLayoutReset"));
            Check(VisibleIds().Length == 10 && menuView.Height == 448 && MenuLayout.Parse(saved).IsDefault,
                "reset restores default visibility/order and persists it");
            Check(window.FindName("MenuLayoutReset") is Button { IsEnabled: false, Opacity: <= 0.5 }, "redundant reset is disabled at defaults");
            Check(status.Visibility == Visibility.Collapsed && status.Text.Length == 0 && editorView.Height == 408,
                "successful reset removes the error and restores the compact editor without a success caption");
            Page("MenuLayout");
            editorScroll.ScrollToBottom();
            root.UpdateLayout();
            Pump(200);
            var last = editorRows.Children.OfType<Grid>().Last();
            Check(last.TransformToAncestor(editorScroll).Transform(new Point(0, last.ActualHeight)).Y <= editorScroll.ActualHeight + 1,
                "last editor row and its controls are fully reachable after scrolling");
            Capture("menu-editor-bottom.png");
            Click((Button)window.FindName("MenuLayoutBack"));
            Check(Call(window, "TargetView")!.ToString() == "Look", "editor back returns to Appearance, not the main menu");
            Page("MenuLayout");
            if (PresentationSource.FromVisual(window) is { } source)
            {
                int savesBeforeEscape = saves;
                Call(window, "BeginMenuReorder", "exit", 9 * 44.0 + 12, 12.0);
                Call(window, "UpdateMenuReorder", 12.0);
                var cancelDrag = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editorView.RaiseEvent(cancelDrag);
                Check(cancelDrag.Handled && saves == savesBeforeEscape && Call(window, "TargetView")!.ToString() == "MenuLayout"
                    && ((Grid)editorRows.Children[^1]).Tag.Equals("exit"), "Escape cancels a drag without saving or leaving the editor");
                var escape = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent };
                editorView.RaiseEvent(escape);
                Check(escape.Handled && Call(window, "TargetView")!.ToString() == "Look", "Escape exits the loaded menu editor to Appearance");
            }
            var direction = typeof(MainWindow).GetMethod("MenuDirection", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check((int)direction.Invoke(null, [Enum.Parse(views, "Look"), Enum.Parse(views, "MenuLayout")])! == 1
                && (int)direction.Invoke(null, [Enum.Parse(views, "MenuLayout"), Enum.Parse(views, "Look")])! == -1,
                "menu editor navigation follows the Appearance parent page in both directions");
        }
        finally
        {
            Set(window, "_menuLayout", oldLayout);
            saveField.SetValue(window, oldSave);
            Call(window, "SyncMenuLayout");
        }
    }
}
