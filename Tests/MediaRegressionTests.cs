#if MEDIA_TEST_API
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland;
using DynamicIsland.Testing;
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

static partial class Program
{
    sealed class MediaFixture : IDisposable
    {
        public readonly TestMediaSession A, B;
        public readonly TestMediaManager Manager;
        public readonly MediaService Model;
        public readonly List<Exception> Errors = [];
        public MediaFixture(bool playing = false, string? title = null)
        {
            A = new("FakeA.exe", title ?? "Track A", 10, 100);
            B = new("FakeB.exe", title ?? "Track B", 7, 200);
            A.Playback.PlaybackStatus = playing ? Status.Playing : Status.Paused;
            Manager = new() { Live = [A, B], Current = A };
            Model = new(Dispatcher.CurrentDispatcher, Errors.Add);
            Set(Model, "_manager", Manager); Call(Model, "AttachSession");
        }
        public void PlayB()
        {
            A.Playback.PlaybackStatus = Status.Paused;
            B.Playback.PlaybackStatus = Status.Playing;
            Manager.Current = B; Call(Model, "ReconsiderSession");
        }
        public void Pin(TestMediaSession session) { Set(Model, "_pinned", session); Call(Model, "AttachSession"); }
        public void Dispose() => Model.Dispose();
    }
    static T MediaField<T>(object value, string field) => (T)value.GetType().GetField(field, Private)!.GetValue(value)!;
    static int MediaRegressions()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var lyrics = typeof(Settings).GetField("_lyrics", BindingFlags.Static | BindingFlags.NonPublic)!;
        var glass = typeof(Settings).GetField("_glass", BindingFlags.Static | BindingFlags.NonPublic)!;
        var oldLyrics = lyrics.GetValue(null); var oldGlass = glass.GetValue(null);
        lyrics.SetValue(null, false); glass.SetValue(null, false); // Test process only, no registry writes.
        try
        {
            using (var f = new MediaFixture(true))
            {
                f.Pin(f.A);
                f.A.Playback.PlaybackStatus = Status.Paused;
                f.B.Playback.PlaybackStatus = Status.Playing; // Windows current deliberately stays A.
                Call(f.Model, "ReconsiderSession");
                Check(f.Model.Source == "FakeB.exe", "paused pinned A -> fresh playing B still works with a stale Windows current pointer");
                f.Pin(f.A); f.Manager.Current = f.B; Call(f.Model, "AttachSession");
                Check(f.Model.Source == "FakeA.exe" && f.Model.HasSelectedSession, "current-session notification alone cannot steal manual browsing of paused A");
            }
            using (var f = new MediaFixture(true))
            {
                f.B.Playback.PlaybackStatus = Status.Playing; f.B.EmitPlayback();
                f.Model.NavigatePage(1); f.Model.NavigatePage(1); f.Model.NavigatePage(1); // B -> Home -> A.
                Pump(100);
                Check(f.Model.Source == "FakeA.exe" && f.Model.HasSelectedSession && f.A.Commands == 0 && f.B.Commands == 0,
                    "manual navigation drains old captured edges and never emits playback commands");
            }
            using (var f = new MediaFixture(true))
            {
                f.B.Playback.PlaybackStatus = Status.Playing; Call(f.Model, "ReconsiderSession");
                f.Pin(f.A);
                f.Manager.Live.Remove(f.A); f.Manager.Current = f.B; Call(f.Model, "AttachSession");
                Check(f.Model.Source == "FakeB.exe" && !f.Model.HomeSelected, "removed manual source follows B instead of inventing an explicit Home choice");
                Set(f.Model, "<HomeSelected>k__BackingField", true);
                f.A.Playback.PlaybackStatus = Status.Playing; f.Manager.Live.Add(f.A); Call(f.Model, "AttachSession");
                Check(f.Model.HomeSelected, "real manually selected Home survives new playback and session changes");
            }
            using (var f = new MediaFixture(true))
            {
                f.Pin(f.A); f.Manager.Live.Clear(); f.Manager.Current = null; Call(f.Model, "AttachSession");
                Check(f.Model.SourceCount == 0 && !f.Model.HasTrack && !f.Model.HomeSelected && f.Model.Title == "", "closing all sources clears media without permanently selecting Home");
                f.B.Playback.PlaybackStatus = Status.Playing; f.Manager.Live.Add(f.B); f.Manager.Current = f.B; Call(f.Model, "AttachSession");
                Check(f.Model.HasTrack && f.Model.Source == "FakeB.exe" && !f.Model.HomeSelected, "a future source can reappear automatically after all old sources were closed");
            }
            using (var f = new MediaFixture(true))
            {
                var pending = new TaskCompletionSource<TestMediaProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.B.PropertiesPending = pending.Task;
                long oldContext = f.Model.CaptureContext(); f.PlayB();
                Check(f.Model.Source == "FakeB.exe" && f.Model.Title == "" && f.Model.Art == null && f.Model.IsLoading, "switch immediately clears previous source metadata and exposes neutral loading state");
                Check(!f.Model.CanControl && !f.Model.Seekable && !f.Model.IsCurrentContext(oldContext), "pending metadata disables wrong-source controls and invalidates old context");
                f.Model.TogglePlay(); f.Model.Seek(.4);
                Check(f.A.Commands == 0 && f.B.Commands == 0 && f.B.Seeks == 0, "loading title cannot silently control or seek a different player");
                pending.SetResult(f.B.Properties); Pump(100);
                Check(f.Model.CanControl && f.Model.Title == "Track B" && f.Model.Duration.TotalSeconds == 200, "new metadata and timeline become ready together without stale A fields");
            }
            using (var f = new MediaFixture())
            {
                var pending = new TaskCompletionSource<TestMediaProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.B.PropertiesPending = pending.Task; f.PlayB(); f.Pin(f.A);
                pending.SetResult(f.B.Properties); Pump(100);
                Check(f.Model.Source == "FakeA.exe" && f.Model.Title == "Track A" && f.Model.Duration.TotalSeconds == 100, "late B metadata cannot overwrite the user's newer return to A");
            }
            using (var f = new MediaFixture(true, "Same title"))
            {
                var cover = new DrawingImage(new GeometryDrawing(Brushes.Red, null, new RectangleGeometry(new Rect(0, 0, 16, 16)))); cover.Freeze();
                Set(f.Model, "<Art>k__BackingField", cover);
                f.PlayB();
                Check(f.Model.Artist == "FakeB.exe" && f.Model.Art == null, "equal-title different source does not inherit another player's artwork");
                Set(f.Model, "<Art>k__BackingField", cover);
                f.B.Properties.Artist = "Another artist";
                f.B.EmitProperties(); Pump(80);
                Check(f.Model.Artist == "Another artist" && f.Model.Art == null, "same-session equal-title artist change also clears obsolete artwork");
            }
            using (var f = new MediaFixture())
            {
                f.A.SeekPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Model.Seek(.5); f.Pin(f.B);
                f.A.SeekPending.SetResult(true); Pump(100);
                Check(f.Model.Source == "FakeB.exe" && f.Model.Position.TotalSeconds == 7 && f.B.Seeks == 0, "late A seek acknowledgement cannot overwrite B position");
            }
            using (var f = new MediaFixture())
            {
                var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.A.SeekPending = first; f.Model.Seek(.2);
                f.A.SeekPending = second; f.Model.Seek(.8);
                second.SetResult(true); Pump(60); first.SetResult(true); Pump(60);
                Check(f.Model.Position.TotalSeconds == 80, "out-of-order seek acknowledgements cannot overwrite a newer seek on the same track");
                f.A.SeekPending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                f.Model.Seek(.5);
                f.A.Properties.Title = "A next track"; f.A.Timeline.Position = TimeSpan.FromSeconds(3);
                f.A.Timeline.LastUpdatedTime = DateTimeOffset.UtcNow; f.A.EmitProperties(); Pump(60);
                f.A.SeekPending.SetResult(true); Pump(60);
                Check(f.Model.Title == "A next track" && f.Model.Position.TotalSeconds == 3, "late seek cannot overwrite a new track within the same session");
            }
            using (var f = new MediaFixture())
            {
                var window = new MainWindow(); Set(window, "_media", f.Model);
                Set(window, "_hiddenByFullscreen", true); // No capture/visualizer worker during tests.
                Set(window, "_scrubbing", true); Set(window, "_scrubFraction", .6);
                Set(window, "_scrubContext", f.Model.CaptureContext());
                f.PlayB();
                Call(window, "Seek_MouseLeftButtonUp", null!, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseUpEvent });
                Check(f.A.Seeks == 0 && f.B.Seeks == 0, "source change while dragging cancels mouse-up instead of seeking B");
                Set(window, "_scrubbing", true); Set(window, "_scrubContext", f.Model.CaptureContext());
                Set(window, "_scrubHeldUntil", 99999d);
                f.B.Properties.Title = "New B track"; f.B.EmitProperties(); Pump(60);
                Call(window, "SyncSeekContext");
                Check(!MediaField<bool>(window, "_scrubbing") && MediaField<double>(window, "_scrubHeldUntil") == 0, "context change clears drag and optimistic seek hold");
                window.Close();
            }
            using (var f = new MediaFixture())
            {
                var window = new MainWindow(); Set(window, "_media", f.Model); Set(window, "_hiddenByFullscreen", true);
                Type panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
                Set(window, "_panel", Enum.Parse(panel, "Player"));
                var pending = new TaskCompletionSource<TestMediaProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.B.PropertiesPending = pending.Task;
                f.Model.Changed += () => Call(window, "OnMediaChanged");
                f.PlayB();
                Check(((System.Windows.Controls.TextBlock)window.FindName("TitleBig")).Text == "Загрузка…" && !((UIElement)window.FindName("PlayerControls")).IsEnabled,
                    "real WPF player shows loading with disabled transport instead of old A title");
                Check(Call(window, "TargetView")!.ToString() == "MediaBig", "loading in the expanded player does not turn into an unrelated Home page");
                pending.SetResult(f.B.Properties); Pump(100);
                Check(((System.Windows.Controls.TextBlock)window.FindName("TitleBig")).Text == "Track B" && ((UIElement)window.FindName("PlayerControls")).IsEnabled,
                    "real WPF controls enable only for the freshly published B snapshot");
                window.Close();
            }
            using (var f = new MediaFixture(true))
            {
                f.B.Playback.PlaybackStatus = Status.Playing; Call(f.Model, "ReconsiderSession"); f.Pin(f.A);
                f.B.PlaybackThrows = true; Call(f.Model, "ReconsiderSession");
                f.B.PlaybackThrows = false; Call(f.Model, "ReconsiderSession");
                Check(f.Model.Source == "FakeA.exe" && f.Model.HasSelectedSession, "unknown provider status and recovery do not steal a manual source");
                f.B.Playback.PlaybackStatus = Status.Paused; f.B.EmitPlayback();
                f.B.Playback.PlaybackStatus = Status.Playing; f.B.EmitPlayback();
                Pump(100);
                Check(f.Model.Source == "FakeB.exe" && !f.Model.HasSelectedSession, "queued genuine pause/resume edges are captured even before Dispatcher processes them");
            }
            using (var f = new MediaFixture(true))
            {
                f.Manager.ThrowCurrent = true;
                Call(f.Model, "ReconsiderSession");
                Check(f.Model.Source == "FakeA.exe", "failed Windows current pointer does not clear a healthy selected source");
                f.Manager.ThrowCurrent = false;
                var c = new TestMediaSession("FakeC.exe", "Track C"); c.Playback.PlaybackStatus = Status.Playing;
                f.Manager.Live.Add(c); f.Manager.Current = c;
                Set(f.Model, "_nextSessionScan", 0L); Call(f.Model, "PollPlayback");
                Check(f.Model.Source == "FakeC.exe" && f.Model.SourceCount == 3, "periodic rescan discovers a new source even when manager notifications were missed");
            }
            using (var f = new MediaFixture(true))
            {
                f.B.Playback.PlaybackStatus = Status.Playing; f.Manager.Current = f.B; f.Manager.ThrowListing = true;
                using var stop = new CancellationTokenSource();
                var monitor = (Task)Call(f.Model, "WatchPlaybackAsync", stop.Token)!;
                Pump(400); f.Manager.ThrowListing = false; f.A.Playback.PlaybackStatus = Status.Paused;
                Pump(1250);
                Check(f.Errors.Count == 1 && !monitor.IsCompleted && f.Model.Source == "FakeB.exe", "fallback polling survives a provider exception and recovers without a new OS event");
                stop.Cancel(); Pump(60);
                Check(monitor.IsCompletedSuccessfully, "poll monitor cancellation is observed and does not surface as async-void exception");
            }
            using (var f = new MediaFixture())
            {
                var pending = new TaskCompletionSource<TestMediaProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.B.PropertiesPending = pending.Task; f.PlayB();
                Pump(3200);
                Check(!f.Model.IsLoading && !f.Model.CanControl && f.Model.Title == "" && f.Errors.Count == 1, "hung metadata request is bounded and never restores old player controls");
                f.B.PropertiesPending = null; Set(f.Model, "_nextMetadataRetry", 0L); Call(f.Model, "PollPlayback");
                Check(f.Model.CanControl && f.Model.Title == "Track B", "metadata retry recovers when the provider becomes responsive");
                pending.SetResult(new() { Title = "Stale delayed B", Artist = "stale" }); Pump(80);
                Check(f.Model.Title == "Track B", "late timed-out metadata cannot overwrite a successful retry");
            }
            using (var f = new MediaFixture())
            {
                var pending = new TaskCompletionSource<TestMediaProperties>(TaskCreationOptions.RunContinuationsAsynchronously);
                f.B.PropertiesPending = pending.Task; f.PlayB();
                long context = f.Model.ContextVersion; f.Model.Dispose();
                pending.SetResult(f.B.Properties); Pump(80);
                f.Model.TogglePlay(); f.Model.Seek(.5);
                Check(!f.Model.IsCurrentContext(context) && !f.Model.CanControl && f.B.Commands == 0 && f.B.Seeks == 0, "closed service drops pending metadata and rejects all subsequent commands");
                Check(f.A.Subscribers == 0 && f.B.Subscribers == 0, "dispose detaches per-session event handlers");
            }
            using (var f = new MediaFixture())
            {
                var lifetime = MediaField<CancellationTokenSource>(f.Model, "_lifetime");
                var monitor = (Task)Call(f.Model, "WatchPlaybackAsync", lifetime.Token)!;
                f.Model.Dispose(); Pump(80);
                Check(monitor.IsCompletedSuccessfully, "disposing the owning service stops its own fallback monitor");
            }
            using (var f = new MediaFixture())
            {
                var window = new MainWindow(); Set(window, "_media", f.Model);
                Type view = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
                Set(window, "_ringing", true);
                Call(window, "ShowTransient", Enum.Parse(view, "Notice"), 12.0, true);
                foreach (string name in new[] { "Volume", "Toast", "Charge", "Focus" })
                    Call(window, "ShowTransient", Enum.Parse(view, name), .05, false);
                Pump(150);
                Check(Call(window, "TargetView")!.ToString() == "Notice" && MediaField<bool>(window, "_ringing"), "volume, media toast, charge and focus cannot replace or shorten a timer alarm");
                Call(window, "Collapse");
                Check(MediaField<bool>(window, "_ringing"), "automatic collapse cannot silence an active alarm");
                Call(window, "EndTransient");
                Check(!MediaField<bool>(window, "_ringing"), "original alarm timeout still completes normally");
                Type panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
                Call(window, "ShowTransient", Enum.Parse(view, "Notice"), 2.0, false);
                Call(window, "ShowPanel", Enum.Parse(panel, "Menu"));
                Call(window, "ShowPanel", Enum.Parse(panel, "None"));
                Check(Call(window, "TargetView")!.ToString() != "Notice", "opening and closing a panel cannot resurrect a hidden stale notification");
                window.Close();
            }
            Console.WriteLine($"MEDIA REGRESSIONS PASS: {_checks} checks; no live publishers, player commands, real alarms, Codex pings or registry changes; exit=0");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { lyrics.SetValue(null, oldLyrics); glass.SetValue(null, oldGlass); }
    }
}
#endif
