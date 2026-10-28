using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using DynamicIsland;

static partial class Program
{
    static void MpcParsing()
    {
        Check(MpcTimeline.Supports("mpc-be64.exe"), "MPC-BE x64 source supported");
        Check(MpcTimeline.Supports("MPC-BE.exe"), "MPC-BE x86 source supported");
        Check(!MpcTimeline.Supports("other-mpc-be64.exe"), "unrelated source rejected");
        Check(MpcTimeline.TryParse("42:00 / 42:35", out var r) && r.Position.TotalSeconds == 2520 && r.Duration.TotalSeconds == 2555, "native MPC position and duration");
        Check(MpcTimeline.TryParse("01:02:03 / 02:00:00", out r) && r.Position.TotalSeconds == 3723, "MPC hour timestamps");
        Check(MpcTimeline.TryParse("- 00:35 / 42:35", out r) && r.Position.TotalSeconds == 2520, "MPC remaining-time mode");
        Check(MpcTimeline.TryParse("\u2212 00:35 / 42:35", out r) && r.Position.TotalSeconds == 2520, "Unicode remaining-time marker");
        Check(MpcTimeline.TryParse("01:02.250 / 03:00.500", out r) && r.Position.TotalSeconds == 62.25, "MPC milliseconds");
        Check(MpcTimeline.TryParse("01:02,250 / 03:00,500", out r) && r.Position.TotalSeconds == 62.25, "decimal-comma timestamp");
        foreach (string label in new[] { "", "Live", "0 / 100", "00:00 / 00:00", "00:61 / 02:00", "03:00 / 02:00", "- 03:00 / 02:00", "NaN:00 / 02:00", "1:00 / 2:00 / 3:00" })
            Check(!MpcTimeline.TryParse(label, out _), "invalid/unknown timeline rejected: " + label);
        Check(MpcTimeline.CaptionMatches("Movie.mkv - MPC-BE x64 1.9.0", "Movie.mkv"), "MPC caption binds selected track");
        Check(!MpcTimeline.CaptionMatches("Movie2.mkv - MPC-BE x64 1.9.0", "Movie.mkv"), "different track cannot receive seek");
        Check(MpcTimeline.TryPoint(1920, 4, .5, true, 96, out int x, out _) && x == 960, "dark seek channel coordinates");
        Check(MpcTimeline.TryPoint(400, 24, 0, false, 96, out x, out _) && x == 8, "light seek channel left inset");
        Check(MpcTimeline.TryPoint(400, 24, 1, false, 96, out x, out _) && x == 391, "seek endpoint remains inside channel");
        Check(MpcTimeline.TryPoint(800, 48, 0, false, 192, out x, out _) && x == 16, "HiDPI seek channel inset");
        Check(!MpcTimeline.TryPoint(1, 1, .5, true, 96, out _, out _), "unlaid-out seek control rejected");
        Check(!MpcTimeline.TryPoint(400, 24, double.NaN, true, 96, out _, out _), "nonfinite seek rejected");
        var service = new MediaService(Dispatcher.CurrentDispatcher);
        Set(service, "_noTimeline", true);
        Call(service, "ApplyMpcReading", new MpcTimeline.Reading(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(120)));
        Check(service.Seekable && service.HasBarPosition && service.Duration.TotalSeconds == 120 && service.Position.TotalSeconds == 20, "MPC fallback reaches MediaService seek model");
        Call(service, "UpdateBarDuration");
        Check(service.Duration.TotalSeconds == 120, "SMTC poll does not erase native duration");
        Call(service, "ForgetBar");
        Call(service, "UpdateBarDuration");
        Check(!service.Seekable && service.Duration == TimeSpan.Zero && service.Position == TimeSpan.Zero, "track/session reset drops stale MPC timeline");
    }

    static int MpcIntegration(string player, string fixture, string variant)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        // Only launch the owned portable copy. Never open test media or seek in
        // the user's already-running MPC-BE process.
        string dir = Path.GetFullPath(Path.GetDirectoryName(player)!);
        if (!dir.Contains("mpc-seek-fix", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.ChangeExtension(player, ".ini"))) return 2;
        var start = new ProcessStartInfo(player) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (string arg in new[] { "/new", variant.Contains("paused") ? "/open" : "/play", "/nofocus", "/volume", "0" }) start.ArgumentList.Add(arg);
        if (variant.Contains("fullscreen")) start.ArgumentList.Add("/fullscreen");
        start.ArgumentList.Add(Path.GetFullPath(fixture));
        using var process = Process.Start(start)!;
        try
        {
            string title = Path.GetFileName(fixture);
            string source = Path.GetFileName(player);
            var adapter = new MpcTimeline();
            MpcTimeline.Reading? reading = null;
            var clock = Stopwatch.StartNew();
            while (clock.Elapsed.TotalSeconds < 12 && !process.HasExited)
            {
                reading = adapter.Read(source, title);
                if (reading != null) break;
                Thread.Sleep(200);
            }
            Console.WriteLine($"variant={variant} pid={process.Id} duration={reading?.Duration.TotalSeconds} position={reading?.Position.TotalSeconds}");
            if (reading == null || Math.Abs(reading.Value.Duration.TotalSeconds - 120) > 1) return 1;
            if (variant.Contains("fullscreen")) Thread.Sleep(2500); // Allow auto-hide of native controls.
            object binding = typeof(MpcTimeline).GetField("_bound", Private)!.GetValue(adapter)!;
            var bindingType = binding.GetType();
            IntPtr window = (IntPtr)bindingType.GetProperty("Window")!.GetValue(binding)!;
            IntPtr seek = (IntPtr)bindingType.GetProperty("Seek")!.GetValue(binding)!;
            MpcGetWindowRect(window, out var bounds);
            bool fills = bounds.Right - bounds.Left >= GetSystemMetrics(0) && bounds.Bottom - bounds.Top >= GetSystemMetrics(1);
            Console.WriteLine($"nativeBounds={bounds.Right - bounds.Left}x{bounds.Bottom - bounds.Top} fillsScreen={fills} seekVisible={IsWindowVisible(seek)}");
            if (variant.Contains("fullscreen") && !fixture.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) && !fills) return 1;
            if (variant.Contains("paused"))
            {
                var first = adapter.Read(source, title);
                Thread.Sleep(1200);
                if (adapter.Read(source, title)?.Position != first?.Position) return 1;
                Console.WriteLine("CLI /open fixture verified paused; no SMTC control commands sent");
            }
            foreach (double fraction in new[] { .25, .75, .10 })
            {
                MpcTimeline.Reading? result = adapter.Seek(source, title, fraction);
                Console.WriteLine($"seek={fraction} actual={result?.Position.TotalSeconds}");
                if (result == null || Math.Abs(result.Value.Position.TotalSeconds - fraction * 120) > 4) return 1;
            }
            if (variant.Contains("paused"))
            {
                var before = adapter.Read(source, title);
                Thread.Sleep(1200);
                if (adapter.Read(source, title)?.Position != before?.Position) return 1;
                Console.WriteLine("Native seeks preserve paused playback");
            }
            if (adapter.Seek(source, "wrong track", .9) != null) return 1;
            var model = new MediaService(Dispatcher.CurrentDispatcher);
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () => await model.StartAsync());
            clock.Restart();
            while (clock.Elapsed.TotalSeconds < 3 && typeof(MediaService).GetField("_manager", Private)!.GetValue(model) == null) Pump(100);
            var manager = (Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager?)typeof(MediaService).GetField("_manager", Private)!.GetValue(model);
            var ownSession = manager?.GetSessions().FirstOrDefault(s => s.SourceAppUserModelId == source);
            if (ownSession == null) return 1;
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(() => { Set(model, "_pinned", ownSession); Call(model, "AttachSession"); });
            clock.Restart();
            while (clock.Elapsed.TotalSeconds < 6 && !(model.Title == title && model.Seekable)) Pump(100);
            Console.WriteLine($"model selectedFixture={model.Title == title} seekable={model.Seekable} duration={model.Duration.TotalSeconds}");
            if (model.Title != title || !model.Seekable || model.Duration.TotalSeconds != 120) return 1;
            bool video = model.IsVideoForProcessAsync((uint)process.Id, CancellationToken.None).GetAwaiter().GetResult();
            bool expectedVideo = !fixture.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"own process video={video} expected={expectedVideo}; other MPC instances excluded");
            if (video != expectedVideo) return 1;
            model.Seek(.5);
            clock.Restart();
            while (clock.Elapsed.TotalSeconds < 3 && Math.Abs(model.Position.TotalSeconds - 60) > 3) Pump(100);
            var confirmed = adapter.Read(source, title);
            Console.WriteLine($"model seek=.5 modelTime={model.Position.TotalSeconds} nativeTime={confirmed?.Position.TotalSeconds}");
            if (confirmed == null || Math.Abs(confirmed.Value.Position.TotalSeconds - 60) > 4 || Math.Abs(model.Position.TotalSeconds - 60) > 4) return 1;
            if (variant.StartsWith("navigation")) NavigationIntegration(model, adapter, source, title, variant);
            Console.WriteLine("MPC INTEGRATION PASS; wrong-track negative control passed");
            return 0;
        }
        finally
        {
            if (!process.HasExited)
            {
                // This PID was returned by our own Process.Start, not a player
                // discovered by process name. Close only this test instance.
                process.Kill();
                process.WaitForExit(3000);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)] struct MpcRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] static extern bool MpcGetWindowRect(IntPtr window, out MpcRect rect);
}
