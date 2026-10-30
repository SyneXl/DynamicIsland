using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    static int MenuRetentionTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            MenuRetentionModelTests();
            MenuRetentionWindowTests(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                systemPointerMoved = false, userSettingsChanged = false, externalServices = false }));
            Console.WriteLine($"PASS: {_checks} menu retention checks; exit=0"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static void MenuRetentionModelTests()
    {
        var state = new MenuHoverRetention();
        var large = new Rect(100, 20, 320, 454);
        var small = new Rect(100, 20, 320, 150);
        var pointer = new Point(250, 450);
        Check(!state.Active && !state.Retains(small, pointer), "ordinary leave has no retention footprint");
        state.Resize(large, small, pointer);
        Check(state.Active && state.Bounds == large, "shrinking page retains its actual previous screen footprint");
        for (int i = 0; i < 30; i++) Check(state.Retains(small, pointer), "stationary pointer never expires by a time limit " + i);
        Check(!state.Retains(small, new Point(250, 80)) && !state.Active, "entering the smaller destination restores normal hover behavior");
        state.Resize(large, small, pointer);
        Check(!state.Retains(small, new Point(430, 450)) && !state.Active, "leaving former footprint releases protection");
        state.Resize(large, small, pointer);
        state.Resize(small, new Rect(120, 20, 280, 120), pointer);
        Check(state.Bounds == large, "a second shrink while stranded retains original footprint without accumulating new regions");
        Check(!state.Retains(large, pointer), "destination growth underneath pointer ends protection");
        state.Resize(large, small, new Point(250, 80));
        Check(!state.Active, "a pointer already inside the destination needs no protection");
        state.Resize(large, small, new Point(80, 200));
        Check(!state.Active, "resize cannot hold a cursor that was outside the previous menu");
        state.Resize(small, large, new Point(250, 160));
        Check(!state.Active, "growing menus do not create a retention zone");
        state.Resize(large, large, pointer);
        Check(!state.Active, "same-size refresh does not create a retention zone");
        var wide = new Rect(40, 20, 440, 460);
        var tallNarrow = new Rect(60, 20, 400, 466);
        state.Resize(wide, tallNarrow, new Point(475, 100));
        Check(state.Active, "width shrink is protected even when the destination becomes taller");
        state.Clear();
        var negative = new Rect(-2000, -300, 440, 460);
        var negativeSmall = new Rect(-1940, -300, 320, 150);
        state.Resize(negative, negativeSmall, new Point(-1780, 120));
        Check(state.Active && state.Retains(negativeSmall, new Point(-1780, 120)), "screen-space retention supports negative monitor coordinates");
        state.Resize(Rect.Empty, small, pointer);
        Check(!state.Active, "invalid geometry clears stale protection");
        state.Resize(large, small, pointer); state.Clear();
        Check(!state.Active && !state.Retains(small, pointer), "explicit close clears retained geometry");
    }

    static void MenuRetentionWindowTests(string output)
    {
        var window = ToolTipWindow(false); window.Show(); Pump(160);
        var panelType = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        var viewType = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        object? Field(string name) => typeof(MainWindow).GetField(name, Private)!.GetValue(window);
        var state = (MenuHoverRetention)Field("_menuRetention")!;
        var timer = (DispatcherTimer)Field("_menuRetentionTimer")!;
        var width = (Spring)Field("_width")!;
        var height = (Spring)Field("_height")!;
        Point pointer = new(-99999, -99999);
        Set(window, "_menuPointerPosition", (Func<Point>)(() => pointer));
        string Panel() => Field("_panel")!.ToString()!;
        void Snap()
        {
            foreach (var spring in (Spring[])Field("_springs")!) spring.Snap(spring.Target);
            Call(window, "ApplyShape"); window.UpdateLayout();
        }
        void Page(string page) { Call(window, "ShowPanel", Enum.Parse(panelType, page)); Snap(); }
        Rect Bounds() => (Rect)Call(window, "MenuScreenBounds", width.Value, height.Value)!;
        void Leave() => Call(window, "Island_MouseLeave", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        void Enter() => Call(window, "Island_MouseEnter", window, new MouseEventArgs(Mouse.PrimaryDevice, 0));
        void ArmSettingsUpdate()
        {
            pointer = new(-99999, -99999); Page("Settings");
            var before = Bounds(); pointer = new Point(before.Left + before.Width / 2, before.Bottom - 12);
            Set(window, "_hovered", true);
            Call(window, "ShowPanel", Enum.Parse(panelType, "Update"));
        }
        try
        {
            ArmSettingsUpdate();
            Check(state.Active && timer.IsEnabled, "Settings to Update arms a temporary guard before the shrink animation");
            // Before animation finishes, pointer is still over the visible old
            // frame. It must not count as arrival in the smaller destination.
            Call(window, "PollMenuRetention");
            Check(state.Active, "animation's still-large frame does not release the guard prematurely");
            Snap(); Leave();
            Check(!(bool)Call(window, "BindingHitTest", pointer)! && Mouse.Captured == null,
                "old footprint remains transparent and never captures the pointer");
            Check(!(bool)Call(window, "DispatchBinding", new BindingGesture(BindingInput.RightClick))!,
                "retention zone is not hover for configured shortcuts");
            Call(window, "Collapse"); Pump(1800);
            Check(Panel() == "Update" && state.Active, "a stationary cursor below the shrunken Update page stays open beyond normal collapse delay");
            Save((FrameworkElement)window.Content, Path.Combine(output, "update-retained.png"));
            var after = Bounds(); pointer = new Point(after.Left + after.Width / 2, after.Top + 70);
            Enter();
            Check(!state.Active && !timer.IsEnabled, "entering the final smaller island retires footprint and stops polling");
            pointer = new Point(after.Right + 100, after.Bottom + 100); Leave(); Pump(750);
            Check(Panel() == "None", "ordinary auto-collapse works again after reaching the smaller island");

            ArmSettingsUpdate(); Snap(); Leave();
            var former = state.Bounds; pointer = new Point(former.Right + 30, former.Bottom - 10);
            Call(window, "PollMenuRetention");
            Check(!state.Active && !timer.IsEnabled && Panel() == "Update", "leaving the former footprint starts normal grace delay, not an abrupt close");
            Pump(750);
            Check(Panel() == "None", "leaving former footprint eventually collapses even without native MouseMove events");

            ArmSettingsUpdate(); Snap(); Leave(); Page("None");
            Check(!state.Active && !timer.IsEnabled, "explicit close clears geometry and polling immediately");
            pointer = new(-99999, -99999); Page("Update"); Set(window, "_hovered", false);
            Call(window, "PollMenuRetention"); Pump(750);
            Check(Panel() == "Update", "stale retention callback cannot schedule collapse for a newly opened page");

            ArmSettingsUpdate(); Snap(); Leave(); window.Hide();
            Check(!state.Active && !timer.IsEnabled, "hiding the island cancels its former footprint");
            window.Show();
            foreach (string flag in new[] { "_sentAway", "_hiddenByFullscreen", "_pickingArea" })
            {
                ArmSettingsUpdate(); Snap(); Leave(); Set(window, flag, true); Call(window, "UpdateTargets");
                Check(!state.Active && !timer.IsEnabled, "non-interactive state resets retention: " + flag);
                Set(window, flag, false);
            }

            // Exercise the common geometry path for every open panel without
            // activating text fields, starting services or making web requests.
            foreach (var panel in Enum.GetValues(panelType).Cast<object>().Where(p => p.ToString() is not ("None" or "Bindings")))
            {
                Call(window, "ResetMenuRetention"); pointer = new(-99999, -99999);
                Set(window, "_panel", Enum.Parse(panelType, "Bindings")); Set(window, "_view", Enum.Parse(viewType, "Bindings"));
                Call(window, "UpdateTargets"); Snap();
                var before = Bounds(); pointer = new Point(before.Right - 3, before.Top + 100);
                Set(window, "_panel", panel); Set(window, "_view", Call(window, "TargetView")!);
                Call(window, "UpdateTargets");
                Check(state.Active, "shared retention works for open panel: " + panel);
            }
            Call(window, "ResetMenuRetention"); pointer = new(-99999, -99999); Page("ThroneServers");
            var servers = Bounds(); pointer = new Point(servers.Left + servers.Width / 2, servers.Bottom - 2);
            Call(window, "ShowPanel", Enum.Parse(panelType, "Bindings"));
            Check(state.Active, "Hotkeys editor also retains the cursor when reached from a taller page");

            Call(window, "ResetMenuRetention"); pointer = new(-99999, -99999); Page("Update");
            var update = (FrameworkElement)window.FindName("UpdatePage");
            double originalHeight = update.Height;
            update.Height = 300; Call(window, "UpdateTargets"); Snap();
            var expanded = Bounds(); pointer = new Point(expanded.Left + expanded.Width / 2, expanded.Bottom - 10);
            update.Height = 120; Call(window, "UpdateTargets"); Snap(); Leave();
            Check(state.Active, "same-page asynchronous content shrink uses the identical protection");
            Call(window, "Collapse"); Check(Panel() == "Update", "dynamic-content shrink does not close its page");
            update.Height = originalHeight;
            Call(window, "ResetMenuRetention");
            pointer = new(-99999, -99999); Page("None"); Page("Update");
            Check(!state.Active, "opening a menu from compact state never arms a phantom footprint");
            ArmSettingsUpdate(); window.Left -= 50;
            Check(!state.Active && !timer.IsEnabled, "window relocation invalidates old screen-space coordinates");
            ArmSettingsUpdate();
        }
        finally { window.Close(); Pump(40); }
        Check(!state.Active && !timer.IsEnabled, "closing the window disposes temporary pointer polling");
    }
}
