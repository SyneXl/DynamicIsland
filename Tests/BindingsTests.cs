using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynamicIsland;

static partial class Program
{
    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    static extern bool BindingPostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
    static int BindingCatalogIndex(string id) => KeyBindings.Catalog.ToList().FindIndex(d => d.Id == id);
    static int BindingsTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            BindingModelTests();
            BindingNativeTests();
            BindingWindowTests(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                userSettingsChanged = false, realKeysInjected = false, userAudioChanged = false, externalServices = false }));
            Console.WriteLine($"PASS: {_checks} bindings checks; exit=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void BindingModelTests()
    {
        var defaults = new KeyBindings();
        Check(KeyBindings.Catalog.Select(d => d.Id).Distinct().Count() == KeyBindings.Catalog.Count, "binding action IDs are unique");
        Check(defaults.Find(new(BindingInput.LeftClick)) == "open" && defaults.Find(new(BindingInput.RightClick)) == "menu"
            && defaults.Find(new(BindingInput.MiddleClick)) == "hide", "existing click defaults preserved");
        Check(defaults.Find(new(BindingInput.WheelUp)) == "volume-up" && defaults.Find(new(BindingInput.WheelDown, ModifierKeys.Control)) == "page-next", "volume and source wheel defaults preserved");
        Check(defaults.Find(new(BindingInput.WheelUp, ModifierKeys.Shift)) == "app-volume-up"
            && defaults.Find(new(BindingInput.WheelDown, ModifierKeys.Shift)) == "app-volume-down", "Shift wheel up/down restores independently configurable application volume");
        string beforeRestore = JsonSerializer.Serialize(new { Version = 2, Entries = KeyBindings.Catalog
            .Where(d => !d.Id.StartsWith("app-volume-", StringComparison.Ordinal)).Select(d => new BindingEntry(d.Id, defaults[d.Id])).ToArray() });
        Check(KeyBindings.Parse(beforeRestore)["app-volume-up"] == defaults["app-volume-up"]
            && KeyBindings.Parse(beforeRestore)["app-volume-down"] == defaults["app-volume-down"], "existing preview settings automatically gain the restored Shift bindings");
        string customShift = JsonSerializer.Serialize(new { Version = 2, Entries = KeyBindings.Catalog
            .Where(d => !d.Id.StartsWith("app-volume-", StringComparison.Ordinal)).Select(d => new BindingEntry(d.Id,
                d.Id == "translation" ? defaults["app-volume-up"] : defaults[d.Id])).ToArray() });
        var preservedShift = KeyBindings.Parse(customShift);
        Check(preservedShift["app-volume-up"] == null && preservedShift["translation"] == defaults["app-volume-up"]
            && preservedShift["app-volume-down"] == defaults["app-volume-down"], "restoration never steals an existing custom Shift wheel assignment");
        Check(KeyBindings.Parse(defaults.Assign("app-volume-up", null).Serialize())["app-volume-up"] == null, "explicitly disabled application-volume bind stays disabled after restart");
        Check(defaults.Find(new(BindingInput.WheelUp, ModifierKeys.Control | ModifierKeys.Shift)) == null, "modifier matching is exact, no bare-wheel fallback");
        Check(defaults.Find(new(BindingInput.Key, ModifierKeys.Control, Key.T)) == "translation", "screen translation defaults to Ctrl+T");
        var oldDefault = new BindingGesture(BindingInput.Key, ModifierKeys.Control | ModifierKeys.Shift, Key.T);
        var newDefault = defaults["translation"]!;
        string Legacy(KeyBindings values) => JsonSerializer.Serialize(new { Version = 1,
            Entries = KeyBindings.Catalog.Select(d => new BindingEntry(d.Id, values[d.Id])).ToArray() });
        var previousPreview = defaults.Assign("translation", oldDefault);
        Check(KeyBindings.Parse(Legacy(previousPreview))["translation"] == newDefault, "old preview default migrates to Ctrl+T without resetting the configuration");
        var customTranslation = previousPreview.Assign("translation", new(BindingInput.Mouse5, ModifierKeys.Alt));
        Check(KeyBindings.Parse(Legacy(customTranslation))["translation"] == customTranslation["translation"], "migration preserves a custom translation binding");
        Check(KeyBindings.Parse(Legacy(previousPreview.Assign("translation", null)))["translation"] == null, "migration preserves an explicitly disabled translation binding");
        var occupied = previousPreview.Assign("play", newDefault);
        var occupiedMigrated = KeyBindings.Parse(Legacy(occupied));
        Check(occupiedMigrated["translation"] == oldDefault && occupiedMigrated["play"] == newDefault, "migration never steals Ctrl+T from another assigned action");
        Check(KeyBindings.Parse(previousPreview.Serialize())["translation"] == oldDefault, "new-format configuration preserves an intentional Ctrl+Shift+T assignment");
        var mouse = new BindingGesture(BindingInput.Mouse4, ModifierKeys.Control | ModifierKeys.Shift);
        var changed = defaults.Assign("translation", mouse);
        Check(defaults["translation"] != mouse && changed.Find(mouse) == "translation", "assignment is immutable");
        Check(changed.Find(defaults["translation"]!) == null, "old key stops working after reassignment");
        Check(changed.Assign("translation", null).Find(mouse) == null, "cleared action has no implicit default fallback");
        Check(defaults.Conflict("translation", defaults["menu"]) == "menu", "conflicting gesture identifies original action");
        bool rejected = false;
        try { defaults.Assign("translation", defaults["menu"]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "duplicate assignment requires explicit replacement");
        var replaced = defaults.Assign("translation", defaults["menu"], true);
        Check(replaced["menu"] == null && replaced.Find(new(BindingInput.RightClick)) == "translation", "replace unassigns previous owner without double dispatch");
        foreach (var d in KeyBindings.Catalog)
            Check(KeyBindings.Parse(changed.Serialize())[d.Id] == changed[d.Id], "persisted binding roundtrip: " + d.Id);
        string custom = JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new BindingEntry("translation", new(BindingInput.LeftClick)), new BindingEntry("retired", null) } });
        var migrated = KeyBindings.Parse(custom);
        Check(migrated["translation"] == new BindingGesture(BindingInput.LeftClick) && migrated["open"] == null
            && migrated["menu"] == defaults["menu"], "new defaults never steal existing custom gestures; unknown IDs ignored");
        foreach (string bad in new[] { "{", "null", "{\"Version\":99,\"Entries\":[]}", "{\"Version\":1,\"Entries\":null}", new string('a', 65537),
            JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new BindingEntry("play", mouse), new BindingEntry("play", null) } }),
            JsonSerializer.Serialize(new { Version = 1, Entries = new[] { new BindingEntry("play", mouse), new BindingEntry("next", mouse) } }) })
            Check(KeyBindings.Parse(bad).Serialize() == defaults.Serialize(), "malformed/ambiguous document falls back atomically");
        foreach (var bad in new[] { new BindingGesture((BindingInput)99), new(BindingInput.Key), new(BindingInput.Key, Key: Key.LeftCtrl),
            new(BindingInput.WheelUp, (ModifierKeys)128), new(BindingInput.Mouse4, Key: Key.T), new(BindingInput.Key, Key: Key.Escape),
            new(BindingInput.Key, Key: Key.F12), new(BindingInput.Key, Key: Key.MediaPlayPause) })
            Check(!bad.Valid, "invalid/reserved key gesture rejected: " + bad);
        Check(new BindingGesture(BindingInput.WheelUp, ModifierKeys.Control).Label == "Ctrl + Колесо вверх", "wheel labels are short and readable");
        Check(new BindingGesture(BindingInput.Key, ModifierKeys.Control, Key.OemPlus).Valid, "OEM keyboard keys supported");
        var locked = defaults.Assign("menu", null);
        Check(!locked.HasSettingsRoute && locked.Assign("settings", new(BindingInput.Mouse4)).HasSettingsRoute, "settings access can be moved to any other configurable gesture");
        Check(KeyBindings.Parse(locked.Serialize()).Serialize() == defaults.Serialize(), "corrupt configuration with no settings route recovers access on startup");
    }

    static void BindingNativeTests()
    {
        using var source = new HwndSource(new HwndSourceParameters("Binding test fixture") { Width = 1, Height = 1, WindowStyle = 0 });
        using var first = new HoverHotkeys(source.Handle);
        using var second = new HoverHotkeys(source.Handle);
        var chord = new BindingGesture(BindingInput.Key, ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift, Key.F10);
        var only = new KeyBindings();
        foreach (var d in KeyBindings.Catalog) only = only.Assign(d.Id, null);
        only = only.Assign("translation", chord);
        Check(first.CanRegister(chord), "native keyboard chord can be acquired without a focused island");
        int id = 0x2400 + BindingCatalogIndex("translation");
        Check(first.Enable(only).Count == 0 && first.Action(id) == "translation", "native registration maps WM_HOTKEY ID to the correct action");
        Check(!second.CanRegister(chord), "native conflict with another registration is detected");
        first.Disable();
        Check(first.Action(id) == null && second.CanRegister(chord), "mouse-leave disposal releases key to Windows immediately");
        first.Enable(only); first.Dispose();
        Check(second.CanRegister(chord), "close/dispose restores original keyboard ownership");
    }

    static void BindingWindowTests(string output)
    {
        var window = ToolTipWindow(false); window.Show(); Pump(100);
        var root = (FrameworkElement)window.Content;
        var panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        void Page(string name) { Call(window, "ShowPanel", Enum.Parse(panel, name)); Pump(240); }
        void Capture(string file)
        {
            foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
            Call(window, "ApplyShape"); root.UpdateLayout(); Pump(60); Save(root, Path.Combine(output, file));
        }
        var saved = new List<KeyBindings>();
        Set(window, "_bindings", new KeyBindings());
        Set(window, "_saveBindings", (Func<KeyBindings, bool>)(value => { saved.Add(value); return true; }));
        var actions = new List<string>();
        Set(window, "_bindingActionTest", (Action<string>)actions.Add);
        object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
        try
        {
            Page("Settings"); Capture("settings.png");
            Check(((FrameworkElement)window.FindName("BindingsRow")).IsVisible, "bindings section is reachable from Settings");
            Page("Bindings"); Capture("bindings.png");
            var rows = ((StackPanel)window.FindName("BindingRows")).Children.OfType<Button>().ToArray();
            Check(rows.Length == KeyBindings.Catalog.Count && rows.All(b => b.FontFamily.Equals(window.FontFamily)), "every bind uses the island font and a recordable settings row");
            Check(rows.All(row => ((Grid)row.Content).Children.OfType<Border>().Single() is
                { BorderThickness: var thickness, Background: SolidColorBrush { Color: var color }, CornerRadius: var radius }
                && thickness == new Thickness(0) && color == Color.FromRgb(16, 16, 16) && radius == new CornerRadius(7)),
                "every right-hand binding has a subtly dark rounded badge with no outline");
            Check(ToolTipVisuals((DependencyObject)window.FindName("BindingsRow")).OfType<TextBlock>().Any(t => t.Text == "Горячие клавиши")
                && ToolTipVisuals((DependencyObject)window.FindName("BindingsView")).OfType<Button>().Any(b => b.Content is string title && title == "ГОРЯЧИЕ КЛАВИШИ"),
                "settings row and page header use the clear Hotkeys title");
            rows[BindingCatalogIndex("app-volume-up")].BringIntoView(); Capture("app-volume-bindings.png");
            Check(ToolTipVisuals(rows[BindingCatalogIndex("app-volume-up")]).OfType<TextBlock>().Any(t => t.Text == "Shift + Колесо вверх")
                && ToolTipVisuals(rows[BindingCatalogIndex("app-volume-down")]).OfType<TextBlock>().Any(t => t.Text == "Shift + Колесо вниз"), "restored Shift wheel bindings are visible in the editor");
            rows[BindingCatalogIndex("translation")].BringIntoView(); Capture("translation-default.png");
            Check(ToolTipVisuals(rows[BindingCatalogIndex("translation")]).OfType<TextBlock>().Any(t => t.Text == "Ctrl + T"), "translation badge displays Ctrl+T");
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.LeftClick))!, "editor never executes configured bindings while editing");
            Call(window, "BeginBindingCapture", "translation"); Capture("recording.png");
            Check((string?)Field("_recordingBinding") == "translation", "selecting a row starts recording for that action");
            Call(window, "OfferBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Control));
            Check(saved.Count == 0 && Field("_pendingBinding") != null && ((Button)window.FindName("BindingReplace")).IsVisible, "conflict waits for explicit replacement and is visible");
            Capture("conflict.png");
            Call(window, "BindingCancel_Click", window, new RoutedEventArgs());
            Check(saved.Count == 0 && Field("_recordingBinding") == null, "cancel keeps persisted configuration unchanged");
            Call(window, "BeginBindingCapture", "translation");
            Call(window, "OfferBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Control));
            Call(window, "BindingReplace_Click", window, new RoutedEventArgs());
            Check(saved.Count == 1 && saved[0]["page-previous"] == null && saved[0].Find(new(BindingInput.WheelUp, ModifierKeys.Control)) == "translation", "explicit conflict replacement persists exactly once");
            Call(window, "BeginBindingCapture", "translation");
            Call(window, "BindingClear_Click", window, new RoutedEventArgs());
            Check(saved[^1]["translation"] == null, "clear disables shortcut and saves immediately");
            Set(window, "_saveBindings", (Func<KeyBindings, bool>)(_ => false));
            var before = (KeyBindings)Field("_bindings")!;
            Call(window, "BeginBindingCapture", "translation");
            Call(window, "OfferBinding", new BindingGesture(BindingInput.Mouse5, ModifierKeys.Alt));
            Check(ReferenceEquals(before, Field("_bindings")) && Field("_recordingBinding") != null
                && ((TextBlock)window.FindName("BindingPrompt")).Text.Contains("сохранить"), "storage failure retains previous state and lets user retry");
            Call(window, "EndBindingCapture", false);
            Set(window, "_saveBindings", (Func<KeyBindings, bool>)(value => { saved.Add(value); return true; }));
            Call(window, "BindingsReset_Click", window, new RoutedEventArgs());
            Check(((KeyBindings)Field("_bindings")!).Serialize() == new KeyBindings().Serialize(), "reset restores all defaults including disabled ones");
            Call(window, "BeginBindingCapture", "menu");
            int savesBeforeLockout = saved.Count;
            Call(window, "BindingClear_Click", window, new RoutedEventArgs());
            Check(saved.Count == savesBeforeLockout && Field("_recordingBinding") != null
                && ((TextBlock)window.FindName("BindingPrompt")).Text.Contains("Оставь"), "last route to Settings cannot be removed and lock user out");
            Call(window, "EndBindingCapture", false);
            Set(window, "_bindingPointerTest", (Func<bool>)(() => true));
            Set(window, "_hovered", true);
            Call(window, "BeginBindingCapture", "play");
            var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 1, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(escape);
            Check(escape.Handled && Field("_recordingBinding") == null, "Escape cancels real WPF key recording rather than becoming a bind");
            Call(window, "BeginBindingCapture", "play");
            var modifier = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 1, Key.LeftCtrl) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(modifier);
            Check(modifier.Handled && Field("_recordingBinding") != null, "modifier alone keeps recorder waiting for the main input");
            var keyEvent = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), 1, Key.F9) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(keyEvent);
            Check(keyEvent.Handled && saved[^1]["play"] == new BindingGesture(BindingInput.Key, Key: Key.F9), "a routed keyboard press records and persists without injecting system input");
            Call(window, "BeginBindingCapture", "play");
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            ((ScrollViewer)window.FindName("BindingScroll")).RaiseEvent(wheel);
            Check(wheel.Handled && Field("_pendingBinding") != null, "wheel over child list is captured before its scroll handler");
            Call(window, "EndBindingCapture", false);
            Call(window, "BeginBindingCapture", "play");
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.XButton1) { RoutedEvent = Mouse.PreviewMouseDownEvent };
            window.RaiseEvent(down);
            var up = new MouseButtonEventArgs(Mouse.PrimaryDevice, 1, MouseButton.XButton1) { RoutedEvent = Mouse.PreviewMouseUpEvent };
            window.RaiseEvent(up);
            Check(down.Handled && up.Handled && saved[^1]["play"] == new BindingGesture(BindingInput.Mouse4), "mouse recording consumes both down and up, never executing the newly assigned action");
            Call(window, "BeginBindingCapture", "play"); Page("Settings");
            Check(Field("_recordingBinding") == null, "leaving editor cancels recording without saving");

            Page("None");
            Set(window, "_bindingPointerTest", (Func<bool>)(() => true)); Set(window, "_hovered", false);
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))! && actions.Count == 0, "pointer outside never dispatches mouse actions");
            Set(window, "_hovered", true);
            Check((bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))! && actions.SequenceEqual(["menu"]), "hovered gesture dispatches its one matching action");
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick, ModifierKeys.Alt))!, "unassigned modifier click never invokes unmodified action");
            Page("Menu");
            var scroll = (ScrollViewer)window.FindName("MenuScroll");
            Set(window, "_bindings", new KeyBindings().Assign("translation", new(BindingInput.WheelUp, ModifierKeys.Control), true));
            int scrollCount = actions.Count;
            Check((bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Control))!
                && actions.Count == scrollCount + 1 && actions[^1] == "translation", "modifier wheel uses configured action even on a scrollable page");
            var ordinaryWheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 1, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            scroll.RaiseEvent(ordinaryWheel);
            Check(ordinaryWheel.Handled && actions.Count == scrollCount + 1, "unmodified wheel on a list remains scrolling instead of changing volume");
            Set(window, "_bindings", new KeyBindings()); Page("None");
            Set(window, "_bindingPointerTest", (Func<bool>)(() => false));
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))!, "stale hover state is rejected by a live visible-surface hit test");
            Set(window, "_bindingPointerTest", (Func<bool>)(() => true));
            foreach (string flag in new[] { "_sentAway", "_hiddenByFullscreen", "_pickingArea", "_pickingFiles", "_dragOver" })
            {
                Set(window, flag, true);
                Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))!, "inactive interaction state cannot dispatch: " + flag);
                Set(window, flag, false);
            }
            window.Hide();
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))!, "hidden main window cannot consume shortcuts");
            window.Show();
            Set(window, "_bindingPointerTest", null!);
            Capture("compact.png");
            var island = (FrameworkElement)window.FindName("Island");
            var pill = (FrameworkElement)window.FindName("Pill");
            Check((bool)Call(window, "BindingHitTest", pill.PointToScreen(new Point(pill.ActualWidth / 2, pill.ActualHeight / 2)))!, "real hit test accepts the compact visible pill");
            Check(!(bool)Call(window, "BindingHitTest", island.PointToScreen(new Point(1, 500)))!, "real hit test excludes transparent host space");
            Set(window, "_hovered", true); Set(window, "_bindingPointerTest", (Func<bool>)(() => true));
            using var native = new HoverHotkeys(new WindowInteropHelper(window).Handle);
            Set(window, "_hoverHotkeys", native);
            var source = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!;
            var hook = (HwndSourceHook)Delegate.CreateDelegate(typeof(HwndSourceHook), window, typeof(MainWindow).GetMethod("OnBindingMessage", Private)!);
            source.AddHook(hook);
            Call(window, "SyncHoverHotkeys");
            Check((bool)Field("_hotkeysActive")!, "native hover registration is enabled only in allowed state");
            var key = new KeyBindings()["translation"]!;
            long payload = ((long)KeyInterop.VirtualKeyFromKey(key.Key) << 16) | (uint)key.Modifiers;
            int id = 0x2400 + BindingCatalogIndex("translation");
            object[] message = [IntPtr.Zero, HoverHotkeys.Message, new IntPtr(id), new IntPtr(payload), false];
            int count = actions.Count;
            Call(window, "OnBindingMessage", message);
            Check(actions.Count == count + 1 && actions[^1] == "translation", "inactive-window WM_HOTKEY routes screen translation without global hooks");
            IntPtr foreground = Native.ForegroundWindow;
            BindingPostMessage(new WindowInteropHelper(window).Handle, HoverHotkeys.Message, new IntPtr(id), new IntPtr(payload)); Pump(30);
            Check(actions.Count == count + 2 && Native.ForegroundWindow == foreground, "real HWND message pump executes hovered keyboard action without changing the active application");
            count++;
            Set(window, "_bindingPointerTest", (Func<bool>)(() => false));
            Call(window, "OnBindingMessage", message);
            Check(actions.Count == count + 1, "queued keyboard message after cursor leaves cannot launch an action");
            Set(window, "_bindingPointerTest", (Func<bool>)(() => true));
            message[3] = new IntPtr(payload + 1); Call(window, "OnBindingMessage", message);
            Check(actions.Count == count + 1, "queued stale keyboard gesture after remapping is rejected");
            Set(window, "_hovered", false); Call(window, "SyncHoverHotkeys");
            Check(!(bool)Field("_hotkeysActive")! && native.Action(id) == null && native.CanRegister(key), "leaving island releases native registration, not merely ignoring its actions");
            source.RemoveHook(hook);
            Set(window, "_hoverHotkeys", null!);
            Set(window, "_hovered", true);
            Set(window, "_bindingPointerTest", (Func<bool>)(() => true));
            int beforeShift = actions.Count;
            Check((bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Shift))!
                && (bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.WheelDown, ModifierKeys.Shift))!
                && actions.Skip(beforeShift).SequenceEqual(["app-volume-up", "app-volume-down"]), "hovered Shift wheel routes up and down to their per-application actions");
            Set(window, "_hovered", false);
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Shift))!, "restored Shift binding stays inactive outside the island");
            var remapped = new KeyBindings().Assign("app-volume-up", new(BindingInput.Mouse5));
            Set(window, "_bindings", remapped); Set(window, "_hovered", true);
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.WheelUp, ModifierKeys.Shift))!
                && (bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.Mouse5))! && actions[^1] == "app-volume-up",
                "reassignment removes implicit Shift handling and executes only the user's new gesture");
            var calls = new List<(string Source, float Delta)>();
            Set(window, "_bindingAppVolumeTest", (Func<string, float, (bool Changed, float Level)>)((app, delta) => { calls.Add((app, delta)); return (true, .42f); }));
            Set(window, "_view", Enum.Parse(typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!, "MediaBig"));
            Call(window, "AdjustBindingAppVolume", "fixture-player.exe", 1);
            Call(window, "AdjustBindingAppVolume", "fixture-player.exe", -1);
            Check(calls.SequenceEqual([("fixture-player.exe", .02f), ("fixture-player.exe", -.02f)]), "per-app volume sends the selected source and correct two-percent step to its own audio session");
            Check(((Digits)window.FindName("PlayerVolumeText")).Text == "42%", "application-volume feedback appears in the expanded player");
            Call(window, "AdjustBindingAppVolume", "", 1);
            Check(calls.Count == 2, "no selected media source never touches audio");
            Set(window, "_bindingAppVolumeTest", (Func<string, float, (bool Changed, float Level)>)((app, delta) => (false, 0)));
            Call(window, "AdjustBindingAppVolume", "disconnected-player.exe", 1);
            Check(((Digits)window.FindName("PlayerVolumeText")).Text == "42%", "missing audio session never reports a fabricated successful volume change");
        }
        finally { window.Close(); Pump(40); }
    }
}
