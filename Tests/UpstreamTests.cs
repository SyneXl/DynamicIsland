using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland;

static partial class Program
{
    static int UpstreamTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var window = ToolTipWindow(false); // In-memory smoke mode; no external integrations or registry writes.
            var root = (FrameworkElement)window.FindName("Root");
            root.Opacity = 1;
            window.Measure(new Size(1000, 1000)); window.Arrange(new Rect(0, 0, 1000, 1000));
            root.Measure(new Size(root.Width, root.Height)); root.Arrange(new Rect(0, 0, root.Width, root.Height));
            Call(window, "UpdateSwitches", false);
            Call(window, "RefreshUpdatePage");
            Type views = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
            Type panels = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
            void Page(string name)
            {
                Call(window, "ShowPanel", Enum.Parse(panels, name));
                Pump(700);
                foreach (Spring spring in (Spring[])typeof(MainWindow).GetField("_springs", Private)!.GetValue(window)!) spring.Snap(spring.Target);
                Call(window, "ApplyShape"); root.UpdateLayout(); Pump(80);
            }
            Check(typeof(MainWindow).Assembly.GetType("DynamicIsland.PointerLyric") == null
                && window.FindName("PointerLyricSegments") == null, "cursor lyrics are completely excluded");
            var outline = typeof(Icon).GetMethod("Outline", BindingFlags.Static | BindingFlags.NonPublic, null, [typeof(Glyph)], null)!;
            foreach (Glyph kind in Enum.GetValues<Glyph>().Distinct())
            {
                var geometry = (Geometry)outline.Invoke(null, [kind])!;
                Check(!geometry.IsEmpty() && geometry.Bounds.Width <= 24.1 && geometry.Bounds.Height <= 24.1, "embedded SF Symbol is present and bounded: " + kind);
            }
            foreach (Gadget.Model model in Enum.GetValues<Gadget.Model>())
            {
                var frames = (Array)typeof(Gadget).GetMethod("FramesOf", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [model])!;
                Check(frames.Length == 81 && frames.Cast<System.Windows.Media.Imaging.BitmapSource>().All(f => f.PixelWidth == 144 && f.PixelHeight == 112), "embedded model frames: " + model);
                Call(window, "ShowHeadset", "Test " + model, model, 70, false);
                Call(window, "MorphTo", Enum.Parse(views, "Headset"));
                Pump(1000); root.UpdateLayout(); Save(root, Path.Combine(output, "headset-" + model + ".png"));
                Check(((TextBlock)window.FindName("HeadsetLabel")).Text == "Test " + model
                    && ((Digits)window.FindName("HeadsetPercent")).Text == "70%", "headset model retains device name and charge: " + model);
            }
            Call(window, "ShowHeadset", "Test headphones", Gadget.Model.Headphones, 10, true);
            Check(((TextBlock)window.FindName("HeadsetState")).Text == "Низкий заряд", "low battery has a clear compact label");
            Page("Lyrics");
            var chips = (UniformGrid)window.FindName("SourceChips");
            Check(chips.Children.Count == 6 && chips.Children.OfType<ToggleButton>().Take(5).All(c => c.Content is string)
                && ((ToggleButton)chips.Children[5]).Content is StackPanel, "six lyric sources are compact named chips");
            Check(window.FindName("LyricsSwitch") != null && window.FindName("LyricEffectsSwitch") != null && window.FindName("LyricPartingSwitch") != null,
                "all lyric controls share one dedicated page");
            var lyrics = (FrameworkElement)window.FindName("LyricsPage");
            Check(lyrics.ActualHeight <= 272 && chips.TransformToAncestor(lyrics).Transform(new Point(0, chips.ActualHeight)).Y <= lyrics.ActualHeight,
                "lyrics page and source chips fit the island");
            Save(root, Path.Combine(output, "lyrics-settings.png"));
            Page("Settings");
            var settings = (FrameworkElement)window.FindName("SettingsView");
            var updateLabel = (TextBlock)window.FindName("UpdateText");
            Check(settings.Height == 414 && updateLabel.TransformToAncestor(settings).Transform(new Point(0, updateLabel.ActualHeight)).Y <= settings.ActualHeight,
                "settings fit cinema and the own-fork update route without clipping");
            Save(root, Path.Combine(output, "settings.png"));
            var updater = (Updater)typeof(MainWindow).GetField("_updater", Private)!.GetValue(window)!;
            using (var release = System.Text.Json.JsonDocument.Parse(ReleaseJson(UpdateBytes(), tag: "v9.0.0")))
                Call(updater, "ReadRelease", release.RootElement);
            typeof(Updater).GetProperty("Notes")!.SetValue(updater, Enumerable.Range(1, 35).Select(i => "Изменение " + i + ": короткое описание новой возможности").ToArray());
            Call(updater, "SetState", Updater.Stage.Available);
            Page("Update");
            var updatePage = (FrameworkElement)window.FindName("UpdatePage");
            var updateButton = (Button)window.FindName("UpdateButton");
            Check(updatePage.Height < 440 && updateButton.TransformToAncestor(updatePage).Transform(new Point(0, updateButton.ActualHeight)).Y <= updatePage.ActualHeight,
                "long release notes never push the update button outside the island");
            Check(((Spring)typeof(MainWindow).GetField("_height", Private)!.GetValue(window)!).Target == updatePage.Height,
                "outer pill follows the measured update page instead of clipping it to 150 pixels");
            Check((double)typeof(MainWindow).GetProperty("UpdateNotesOverflow", Private)!.GetValue(window)! > 0, "long update notes have a real scroll range");
            Save(root, Path.Combine(output, "update-available.png"));
            Call(window, "ScrollUpdateNotes", 100);
            var noteScroll = (Spring)typeof(MainWindow).GetField("_updateNotesScroll", Private)!.GetValue(window)!;
            noteScroll.Snap(noteScroll.Target); Call(window, "ApplyShape");
            Check(((TranslateTransform)window.FindName("UpdateNotesMove")).Y < 0, "update notes scroll independently of the action button");
            Call(updater, "SetState", Updater.Stage.Loading);
            Set(window, "_panel", Enum.Parse(panels, "None"));
            Call(window, "ToggleOpen");
            Check(Call(window, "TargetView")!.ToString() == "Update", "clicking compact download progress reopens its page instead of the media player");
            Call(updater, "SetState", Updater.Stage.Available);
            Page("Look");
            Check(window.FindName("MenuLayoutRow") is Button { Visibility: Visibility.Visible }, "spring appearance page keeps the menu editor route");
            var scroll = (Spring)typeof(MainWindow).GetField("_lookScroll", Private)!.GetValue(window)!;
            Call(window, "ScrollLook", 100); scroll.Snap(scroll.Target); Call(window, "ApplyShape"); root.UpdateLayout();
            Check(scroll.Target > 0 && ((TranslateTransform)window.FindName("LookMove")).Y < 0, "appearance wheel really moves the bounded rows");
            Save(root, Path.Combine(output, "appearance-scrolled.png"));
            Call(window, "ScrollLook", -100); scroll.Snap(scroll.Target); Call(window, "ApplyShape");
            Check(scroll.Target == 0, "appearance scroll clamps at its top edge");
            Call(window, "BuildPlayerLyrics", (object)new LyricsService.Line[] { new(TimeSpan.Zero, "A short test line"), new(TimeSpan.FromSeconds(5), "Another line") });
            Set(window, "_playerLines", new LyricsService.Line[] { new(TimeSpan.Zero, "A short test line"), new(TimeSpan.FromSeconds(5), "Another line") });
            Call(window, "RebuildPlayerLyrics");
            Check(((Array)typeof(MainWindow).GetField("_playerRows", Private)!.GetValue(window)!).Length == 2, "effect rebuild retains real lyric rows");
            Set(window, "_playerLines", Array.Empty<LyricsService.Line>()); Call(window, "RebuildPlayerLyrics");
            Check(((Array)typeof(MainWindow).GetField("_playerRows", Private)!.GetValue(window)!).Length == 0, "wordless track rebuild removes stale rows");
            var parseTime = typeof(PlayerBar).GetMethod("ParseTime", BindingFlags.Static | BindingFlags.NonPublic)!;
            Check(parseTime.Invoke(null, ["--:--"]) == null && (TimeSpan?)parseTime.Invoke(null, ["1:23"]) == TimeSpan.FromSeconds(83), "unreadable Telegram time is unknown, not zero");
            var media = (MediaService)typeof(MainWindow).GetField("_media", Private)!.GetValue(window)!;
            Set(media, "_barFound", true); Set(media, "_position", TimeSpan.FromSeconds(43));
            Call(media, "ApplyBarReading", (object)null!);
            Check((bool)typeof(MediaService).GetField("_barFound", Private)!.GetValue(media)!, "one missed Telegram reading preserves its timeline");
            Call(media, "ApplyBarReading", (object)null!); Call(media, "ApplyBarReading", (object)null!);
            Check(!(bool)typeof(MediaService).GetField("_barFound", Private)!.GetValue(media)!, "three missed Telegram readings release the old bar");
            var parseLrc = typeof(LyricsService).GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
            var lines = (LyricsService.Line[])parseLrc.Invoke(null, ["[00:02.00]third\n[00:01.00]first\n[00:01.00]second"])!;
            Check(lines.Select(l => l.Text).SequenceEqual(new[] { "first", "second", "third" }), "equal-time LRC lines keep their original order");
            var loop = new FrameLoop(_ => throw new InvalidOperationException("Expected regression fixture"));
            Check((bool)Call(loop, "AdvanceSafely", 0.016)! == false, "failing animation returns a stop instead of breaking other frame loops");
            bool nextStep = false;
            var safely = typeof(MainWindow).GetMethod("Safely", BindingFlags.Static | BindingFlags.NonPublic)!;
            safely.Invoke(null, [new Action(() => throw new InvalidOperationException("Expected tick fixture"))]);
            safely.Invoke(null, [new Action(() => nextStep = true)]);
            Check(nextStep, "a failed tick step does not prevent the next step");
            var part = new Parting([new Point(-5, 0), new Point(5, 0)]);
            for (int i = 0; i < 100; i++) part.Advance(new Point(0, 0), 0.016);
            Check(part[0].X < 0 && part[1].X > 0, "letters part on opposite sides of the pointer");
            for (int i = 0; i < 200; i++) part.Advance(null, 0.016);
            Check(part[0].Length < 0.01 && part[1].Length < 0.01, "letters settle when the pointer leaves or the option is off");
            window.Close();
            Console.WriteLine($"PASS: {_checks} upstream-port checks; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
