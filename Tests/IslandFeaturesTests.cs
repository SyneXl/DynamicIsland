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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    static int IslandFeaturesTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            AnnouncementTests(output);
            ShelfContentTests(output);
            MixerGroupingTests();
            FeatureWindowTests(output);
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                userClipboardChanged = false, userAudioChanged = false, externalServices = false, nativeAudioTested = false }));
            Console.WriteLine($"PASS: {_checks} island feature checks; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static void AnnouncementTests(string output)
    {
        string path = Path.Combine(output, "notice-model", "notice.json");
        if (File.Exists(path)) File.Delete(path);
        var notice = new UpdateAnnouncement(path);
        Version current = new(1, 0), next = new(2, 0);
        Check(!notice.Pending && !notice.Present(), "no notification before a verified available version");
        notice.Observe(current, current);
        Check(!notice.Pending, "current version never notifies");
        notice.Observe(next, current);
        Check(notice.Pending && !notice.Presented, "new release enters the automatic notice queue");
        Check(notice.Present() && !notice.Present(), "automatic notice appears only once");
        notice = new UpdateAnnouncement(path);
        Check(notice.Pending && notice.Presented && notice.Version == next, "unread bubble survives restart including interrupted initial toast");
        notice.Observe(next, current); notice.Observe(null, current);
        Check(notice.Pending && notice.Presented, "checking/network failure does not lose an unread update");
        notice.Consume(); notice = new UpdateAnnouncement(path); notice.Observe(next, current);
        Check(!notice.Pending && !notice.Present(), "bubble replay consumes it permanently for this release");
        notice.Observe(new Version(3, 0), current);
        Check(notice.Pending && !notice.Presented, "a genuinely newer release receives its own notification");
        notice.Observe(null, new Version(3, 0));
        Check(!notice.Pending && notice.Version == null, "installing that version clears obsolete notification state");
        File.WriteAllText(path, "broken");
        Check(!new UpdateAnnouncement(path).Pending, "corrupt notification journal is harmless");
    }

    static void ShelfContentTests(string output)
    {
        string root = Path.Combine(output, "shelf-model"); Directory.CreateDirectory(root);
        string userFile = Path.Combine(root, "user.txt"); File.WriteAllText(userFile, "original user file");
        string[] saved = [];
        Shelf Create(IEnumerable<string> initial) => new(Dispatcher.CurrentDispatcher, Path.Combine(root, "owned"), initial, p => saved = p);
        var shelf = Create([userFile]);
        Check(shelf.Items.Single().Text == null && !shelf.Items.Single().Owned, "existing text files remain ordinary user-owned files");
        string text = "Привет, мир!\r\nВторая строка 🎵";
        var note = shelf.AddText(text)!;
        Check(note.Text == text && File.ReadAllText(note.Path) == text && note.Owned, "Unicode multiline note is stored losslessly");
        Check(note.Name == "Привет, мир!  Вторая строка 🎵", "note tile uses a short single-line content preview");
        Check(ReferenceEquals(shelf.AddText(text), note) && shelf.Items.Count == 2, "repeated paste of the same note does not duplicate it");
        Check(shelf.AddText("  \r\n ") == null, "empty clipboard text does not create blank notes");
        Reject(() => shelf.AddText(new string('x', Shelf.MaxTextLength + 1)), "oversized text is rejected before writing");
        shelf.UpdateText(note, "Обновлённый текст");
        Check(File.ReadAllText(note.Path) == note.Text && note.Name == "Обновлённый текст", "editing replaces text atomically and updates preview");
        shelf = Create(saved); note = shelf.Items.Single(i => i.Text != null);
        Check(note.Text == "Обновлённый текст" && note.Owned, "notes restore from the existing shelf persistence list");
        var textData = new DataObject(DataFormats.UnicodeText, "Вставка из буфера");
        Check(Shelf.CanPaste(textData) && shelf.Paste(textData), "explicit clipboard payload pastes text");
        var pixels = new byte[8 * 8 * 4]; Array.Fill<byte>(pixels, 255);
        var image = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 32);
        Check(shelf.Paste(new DataObject(DataFormats.Bitmap, image)), "explicit clipboard payload pastes an image as a durable PNG");
        var picture = shelf.Items.Last();
        BitmapFrame decoded;
        using (var stream = File.OpenRead(picture.Path)) decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check(picture.Owned && File.Exists(picture.Path) && decoded.PixelWidth == 8, "pasted image is readable after saving");
        var files = new DataObject(DataFormats.FileDrop, new[] { userFile });
        files.SetData(DataFormats.UnicodeText, "do not turn file paths into notes");
        int before = shelf.Items.Count; shelf.Paste(files);
        Check(shelf.Items.Count == before, "file-drop format takes precedence over textual clipboard representation");
        var unsupported = new DataObject("Island.Unsupported", new object());
        Check(!Shelf.CanPaste(unsupported) && !shelf.Paste(unsupported), "unsupported clipboard formats are not recorded");
        string notePath = note.Path; shelf.Remove(note);
        Check(!File.Exists(notePath) && File.Exists(userFile), "removing a note deletes only its private content file");
        shelf.Clear();
        Check(saved.Length == 0 && File.Exists(userFile) && File.ReadAllText(userFile) == "original user file", "clearing the shelf never deletes original user files");
        Check(!File.Exists(picture.Path), "clearing removes private pasted-image storage");
        string failedRoot = Path.Combine(root, "failed-save");
        var failing = new Shelf(Dispatcher.CurrentDispatcher, failedRoot, [], _ => throw new IOException("fixture registry write denied"));
        Reject(() => failing.AddText("must not pretend to save"), "failed shelf-list persistence reports a note save failure");
        Check(failing.Items.Count == 0 && Directory.GetFiles(failedRoot).Length == 0, "failed note save rolls back its tile and private file");
        var existing = Create([]); var kept = existing.AddText("keep me")!;
        var failedRemoval = new Shelf(Dispatcher.CurrentDispatcher, Path.Combine(root, "owned"), [kept.Path], _ => throw new IOException("fixture registry write denied"));
        Reject(() => failedRemoval.Clear(), "failed clear is reported instead of deleting unsaved references");
        Check(failedRemoval.Items.Count == 1 && File.Exists(kept.Path), "failed clear preserves note data and the old shelf layout");
        Pump(80);
    }

    static void MixerGroupingTests()
    {
        var grouped = MixerSnapshot.Group([
            new("a1", "browser.exe", "Browser", .2f, true, false),
            new("a2", "BROWSER.EXE", "Browser", .7f, false, true),
            new("b", "player.exe", "Player", .4f, true, false)]);
        Check(grouped.Length == 2 && grouped[0].Sessions.SequenceEqual(["a1", "a2"]), "mixer groups process audio sessions by application, case-insensitively");
        Check(grouped[0].Volume == .7f && !grouped[0].Muted && grouped[0].Active, "group reflects maximum volume and any unmuted/active session");
        Check(grouped[1].Muted && !grouped[1].Active, "paused/muted applications remain controllable");
        using var audio = new AudioService();
        Check(!audio.SetMixer("missing-device", ["missing-session"], .5f), "stale device identity refuses a mixer write");
        Check(!audio.SetMixer("missing-device", null, float.NaN), "nonfinite mixer values never reach Core Audio");
    }

    static void FeatureWindowTests(string output)
    {
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, true);
        typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, false);
        var window = new MainWindow();
        window.Loaded += (_, _) => { window.Left = -10000; window.Top = -10000; };
        window.Show(); Pump(400);
        var root = (FrameworkElement)window.FindName("Root"); root.Opacity = 1;
        var panelType = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        object Panel(string name) => Enum.Parse(panelType, name);
        string View() => Call(window, "TargetView")!.ToString()!;
        void Show(string name) { Call(window, "ShowPanel", Panel(name)); Pump(300); }
        void Click(string name) => ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Capture(string name) { Pump(350); Save(root, Path.Combine(output, name + ".png")); }
        try
        {
            Check(window.IsLoaded && new WindowInteropHelper(window).Handle != IntPtr.Zero, "new features are exercised in a real WPF HWND without live integrations");
            var snapshot = new MixerSnapshot("fixture-device", "Speakers", .65f, false, [
                new("music", "Telegram", ["music-session"], .42f, false, true),
                new("video", "MPC-BE", ["video-session"], .8f, true, false)]);
            var writes = new List<(string Device, string[]? Sessions, float? Level, bool? Muted)>();
            Set(window, "_mixerReader", (Func<MixerSnapshot?>)(() => snapshot));
            Set(window, "_mixerWriter", (Func<string, string[]?, float?, bool?, bool>)((device, ids, level, muted) => { writes.Add((device, ids, level, muted)); return true; }));
            Show("Player"); Click("HomeMixerButton"); Pump(200);
            Check(View() == "Mixer", "home volume control opens the mixer");
            Call(window, "MixerBack_Click", window, new RoutedEventArgs());
            Check(View() == "IdleBig", "mixer back returns to home when opened from home");
            Show("None");
            var viewType = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
            Call(window, "ShowTransient", Enum.Parse(viewType, "Volume"), 3d, false);
            Click("VolumeMixerButton");
            Check(View() == "Mixer", "volume HUD icon opens mixer and cancels its transient timeout");
            Call(window, "EndTransient");
            Check(View() == "Mixer", "an expired HUD cannot dismiss the opened mixer");
            var layout = MenuLayout.Default();
            Check(!layout.Entries.Single(e => e.Id == "mixer").Visible, "mixer menu route is hidden by default");
            Set(window, "_menuLayout", layout.SetVisible("mixer", true));
            Call(window, "SyncMenuLayout"); Show("Menu");
            ((Button)((System.Windows.Controls.Panel)window.FindName("MenuRows")).Children.OfType<Button>().Single(b => (string)b.Tag == "mixer")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(View() == "Mixer", "enabled menu entry opens the same mixer");
            Pump(300);
            var rows = (IDictionary)typeof(MainWindow).GetField("_mixerRows", Private)!.GetValue(window)!;
            object music = rows["music"]!;
            T Prop<T>(object row, string name) => (T)row.GetType().GetProperty(name)!.GetValue(row)!;
            Check(Prop<TextBlock>(music, "Name").FontFamily.Equals(window.FontFamily), "mixer application names inherit the island SF Pro font");
            Check(((Digits)window.FindName("InfoVol")).FontFamily.Equals(window.FontFamily), "clickable home volume retains the island font");
            var slider = Prop<Scrubber>(music, "Slider");
            Check(slider.ActualWidth > 100 && slider.ActualHeight > 15, "mixer slider has a usable rendered hit area");
            slider.Set(25, false);
            typeof(Scrubber).GetField("Changed", Private)!.GetValue(slider)!.GetType().GetMethod("Invoke")!.Invoke(
                typeof(Scrubber).GetField("Changed", Private)!.GetValue(slider), [slider, EventArgs.Empty]);
            Check(writes[^1] is { Device: "fixture-device", Level: .25f } && writes[^1].Sessions!.SequenceEqual(["music-session"]), "application slider commands only its exact session IDs");
            Prop<Button>(music, "Mute").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(writes[^1].Muted == true && writes[^1].Sessions!.Single() == "music-session", "application mute does not touch master or other apps");
            Prop<Button>(rows["master"]!, "Mute").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(writes[^1].Sessions == null && writes[^1].Muted == true, "master mute has a distinct explicit route");
            Capture("mixer");
            var stable = rows["music"];
            snapshot = snapshot with { Channels = snapshot.Channels.Reverse().ToArray() }; Call(window, "RefreshMixer");
            Check(ReferenceEquals(rows["music"], stable), "session refresh retains controls instead of moving/recreating hovered sliders");
            snapshot = snapshot with { Device = "new-device", Channels = [] }; Call(window, "RefreshMixer");
            Check(rows.Count == 1 && ((TextBlock)window.FindName("MixerEmpty")).Visibility == Visibility.Visible, "device/session changes remove obsolete controls and show a compact empty state");
            Set(window, "_mixerReader", (Func<MixerSnapshot?>)(() => null)); Call(window, "RefreshMixer");
            Check(rows.Count == 0 && ((TextBlock)window.FindName("MixerUnavailable")).Visibility == Visibility.Visible,
                "unplugged output clears stale controls rather than pretending they still work");

            string[] stored = [];
            var shelf = new Shelf(Dispatcher.CurrentDispatcher, Path.Combine(output, "shelf-ui"), [], p => stored = p);
            Set(window, "_shelf", shelf); shelf.Changed += () => Call(window, "SyncShelf"); Call(window, "SyncShelf");
            int clipboardReads = 0;
            Set(window, "_shelfClipboardReader", (Func<IDataObject?>)(() => { clipboardReads++; return new DataObject(DataFormats.UnicodeText, "Заметка из буфера"); }));
            string copied = ""; Set(window, "_shelfClipboardWriter", (Action<string>)(text => copied = text));
            Show("Shelf");
            Check(clipboardReads == 0, "opening the shelf never reads clipboard contents automatically");
            Check((bool)Call(window, "HandleShelfKey", Key.V, ModifierKeys.Control)!, "Ctrl+V is handled on the shelf");
            Check(shelf.Items.Single().Text == "Заметка из буфера" && clipboardReads == 1, "shelf keyboard paste uses only the explicit clipboard read");
            Click("ShelfPasteButton");
            Check(shelf.Items.Count == 1 && clipboardReads == 2, "paste button follows the same deduplicating route");
            Set(window, "_shelfClipboardReader", (Func<IDataObject?>)(() => null)); Click("ShelfPasteButton");
            Check(((FrameworkElement)window.FindName("ShelfView")).Height == 156 && ((TextBlock)window.FindName("ShelfStatus")).Visibility == Visibility.Visible,
                "empty-clipboard hint reserves its own space without overlapping shelf tiles");
            Click("ShelfNoteButton");
            var editor = (TextBox)window.FindName("ShelfNoteEditor"); editor.Text = "Список дел\nКупить чай\nОтправить файл";
            Check(editor.FontFamily.Equals(window.FontFamily), "note editor uses the island font instead of a system-textbox fallback");
            Call(window, "ShelfNoteCopy_Click", window, new RoutedEventArgs());
            Check(copied == editor.Text, "copy note preserves the complete multiline text");
            Check(!(bool)Call(window, "HandleShelfKey", Key.V, ModifierKeys.Control)!, "Ctrl+V inside note editor remains native text insertion, not a new shelf item");
            Capture("shelf-note");
            Call(window, "HandleShelfKey", Key.Enter, ModifierKeys.Control); Pump(200);
            Check(View() == "Shelf" && shelf.Items.Count == 2 && stored.Length == 2, "Ctrl+Enter saves a note and returns to shelf");
            Capture("shelf");
            var note = shelf.Items.Last(); Call(window, "EditShelfNote", note); editor.Text = "Изменено"; Show("Menu");
            Check(note.Text == "Изменено", "leaving note editor saves changes instead of silently losing them");
            Call(window, "EditShelfNote", note); editor.Text = "Отменённая правка"; Call(window, "HandleShelfKey", Key.Escape, ModifierKeys.None);
            Check(note.Text == "Изменено", "Escape cancels editing without modifying saved note");

            Show("None"); Set(window, "_transientView", null!);
            string noticePath = Path.Combine(output, "notice-ui.json"); if (File.Exists(noticePath)) File.Delete(noticePath);
            var notice = new UpdateAnnouncement(noticePath); notice.Observe(new Version(99, 0), Updater.CurrentVersion);
            Set(window, "_updateAnnouncement", notice); Set(window, "_cinemaMode", true);
            Call(window, "TryAnnounceUpdate");
            Check(!notice.Presented, "automatic update notice does not interrupt cinema");
            Set(window, "_cinemaMode", false); Set(window, "_hiddenByFullscreen", false); Call(window, "TryAnnounceUpdate");
            Check(View() == "Notice" && notice.Presented && notice.Pending, "available update produces a short island notice");
            Capture("update-notice");
            Call(window, "EndTransient"); Call(window, "UpdateTargets"); Pump(500);
            Check((bool)typeof(MainWindow).GetProperty("UpdateBubblePending", Private)!.GetValue(window)!, "initial notice collapses to a pending update bubble");
            Check(((FrameworkElement)window.FindName("Bubble")).Visibility == Visibility.Visible, "update bubble participates in the existing side bubble");
            Capture("update-bubble-with-shelf");
            Check(Call(window, "BubblePanelAt", ((FrameworkElement)window.FindName("Bubble")).Width - 2)!.ToString() == "Update", "combined shelf/update bubble has a dedicated update hit region");
            var obs = typeof(MainWindow).GetField("_obs", Private)!.GetValue(window)!;
            Set(obs, "<Recording>k__BackingField", true);
            var countdown = (Countdown)typeof(MainWindow).GetField("_countdown", Private)!.GetValue(window)!;
            countdown.Start(TimeSpan.FromMinutes(2)); Call(window, "SyncTimer");
            Call(window, "ShowTransient", Enum.Parse(viewType, "Volume"), 3d, false); Call(window, "UpdateTargets"); Pump(450);
            Check(Call(window, "BubblePanelAt", 25d)!.ToString() == "Record" && Call(window, "BubblePanelAt", 90d)!.ToString() == "Timer"
                && Call(window, "BubblePanelAt", ((FrameworkElement)window.FindName("Bubble")).Width - 2)!.ToString() == "Update",
                "recording, timer, shelf and update coexist with distinct hit regions");
            // Worst compact lyric width plus all bubble parts must remain inside the actual HWND.
            double worstRight = window.Width / 2 + (440 * 1.07 / 2 + ((FrameworkElement)window.FindName("Bubble")).Width * 1.07 + 7) * 1.3;
            Check(worstRight + 3 < window.Width, "transparent window reserves room for the widest lyrics and all side-bubble parts at maximum scale");
            Set(obs, "<Recording>k__BackingField", false); countdown.Stop(); Call(window, "EndTransient");
            Call(window, "ReplayUpdateNotice");
            Check(!notice.Pending && View() == "Notice", "clicking bubble replays notice and consumes bubble immediately");
            Call(window, "EndTransient"); Call(window, "TryAnnounceUpdate");
            Check(!notice.Pending && View() != "Notice", "replayed notice never generates another bubble or automatic replay");
            // Exercise the real Updater.Changed -> RefreshUpdatePage path without HTTP or live integrations.
            var updater = typeof(MainWindow).GetField("_updater", Private)!.GetValue(window)!;
            var smoke = typeof(App).GetField("<SmokeTest>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
            try
            {
                Show("Menu");
                Set(updater, "<State>k__BackingField", Updater.Stage.Available);
                Set(updater, "<LatestVersion>k__BackingField", new Version(100, 0));
                smoke.SetValue(null, false); Call(window, "RefreshUpdatePage");
                Check(notice.Pending && !notice.Presented && View() == "Menu", "updater availability queues an announcement without interrupting an open menu");
                Show("Update"); Call(window, "RefreshUpdatePage");
                Check(!notice.Pending, "viewing the available release manually consumes its pending notification");
                Show("None"); Set(updater, "<LatestVersion>k__BackingField", new Version(101, 0)); Call(window, "RefreshUpdatePage");
                Check(notice.Presented && notice.Pending && View() == "Notice", "updater availability enters the complete automatic notice pipeline when unobstructed");
            }
            finally { smoke.SetValue(null, true); }
        }
        finally { window.Close(); }
    }

    static void NativeMixerTest(string output)
    {
        IMMDevice? device = AudioEndpoint.Default();
        Check(device != null, "native mixer fixture has a Windows audio output");
        IAudioClient? player = device!.Activate<IAudioClient>();
        Marshal.ReleaseComObject(device);
        Check(player != null, "native mixer fixture activates its own shared-mode audio client");
        IntPtr format = IntPtr.Zero;
        object? render = null;
        using var audio = new AudioService();
        MixerSnapshot? snapshot = null; MixerChannel? own = null;
        try
        {
            Check(player!.GetMixFormat(out format) == 0, "native fixture uses the actual endpoint format");
            Check(player.Initialize(0, 0x80000, 10000000, 0, format, IntPtr.Zero) == 0, "native fixture creates a nonpersistent shared audio session");
            Guid renderId = typeof(IFeatureAudioRenderClient).GUID;
            Check(player.GetService(ref renderId, out render) == 0 && render is IFeatureAudioRenderClient, "native fixture obtains an owned render buffer");
            player.GetBufferSize(out uint frames);
            var buffer = (IFeatureAudioRenderClient)render!;
            Check(buffer.GetBuffer(frames, out _) == 0 && buffer.ReleaseBuffer(frames, 2) == 0 && player.Start() == 0,
                "native fixture starts explicitly silent audio, without changing user playback");
            string executable = Environment.ProcessPath!;
            for (int i = 0; i < 35 && own == null; i++)
            {
                Pump(100); snapshot = audio.ReadMixer();
                own = snapshot?.Channels.FirstOrDefault(c => c.Key.Equals(executable, StringComparison.OrdinalIgnoreCase));
            }
            Check(snapshot != null && own != null, "real Core Audio enumerates this test process's owned silent playback");
            Check(audio.SetMixer(snapshot!.Device, own!.Sessions, .37f), "real Core Audio accepts volume change for owned test sessions");
            var changed = audio.ReadMixer()!.Channels.Single(c => c.Key == own.Key);
            Check(Math.Abs(changed.Volume - .37f) < .005, "real mixer volume readback confirms the requested value");
            Check(audio.SetMixer(snapshot.Device, own.Sessions, muted: true), "real Core Audio accepts mute for owned silent fixture");
            Check(audio.ReadMixer()!.Channels.Single(c => c.Key == own.Key).Muted, "real mixer mute readback confirms endpoint-independent app mute");
            Check(!audio.SetMixer(snapshot.Device, ["expired-instance-id"], .1f), "expired session identity never targets another live application");
        }
        finally
        {
            bool restored = false;
            try { if (snapshot != null && own != null) restored = audio.SetMixer(snapshot.Device, own.Sessions, own.Volume, own.Muted); }
            finally
            {
                player?.Stop();
                if (render != null) Marshal.ReleaseComObject(render);
                if (player != null) Marshal.ReleaseComObject(player);
                if (format != IntPtr.Zero) Marshal.FreeCoTaskMem(format);
                Pump(100);
            }
            if (own != null) Check(restored, "owned test session settings are restored");
        }
    }

    static int NativeMixerIntegration(string output)
    {
        try
        {
            Directory.CreateDirectory(output); NativeMixerTest(output);
            File.WriteAllText(Path.Combine(output, "native-result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks, nativeAudioTested = true,
                userAudioChanged = false, fixtureRestored = true }));
            Console.WriteLine($"PASS: {_checks} native Core Audio checks; exit=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFeatureAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr buffer);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }
}
