using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Control;

static partial class Program
{
    // Own silent SMTC publishers, never commands to the user's media sessions.
    static int SmtcFixture(string id, string control, string wave)
    {
        SetCurrentProcessExplicitAppUserModelID(id);
        using var player = new MediaPlayer { IsMuted = true, AutoPlay = false };
        player.CommandManager.IsEnabled = false;
        player.Source = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(wave)));
        var smtc = player.SystemMediaTransportControls;
        smtc.IsEnabled = smtc.IsPlayEnabled = smtc.IsPauseEnabled = true;
        smtc.DisplayUpdater.Type = MediaPlaybackType.Music;
        smtc.DisplayUpdater.MusicProperties.Title = id;
        smtc.DisplayUpdater.MusicProperties.Artist = "Owned silent test fixture";
        smtc.DisplayUpdater.Update();
        smtc.PlaybackStatus = MediaPlaybackStatus.Paused;
        File.WriteAllText(control + ".ready", "ready");
        string last = "";
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 100)
        {
            string command = File.Exists(control) ? File.ReadAllText(control).Trim() : "";
            if (command != last && command.Length > 0)
            {
                last = command;
                string action = command.Split(':')[^1];
                if (action == "exit") break;
                if (action is "play" or "pause")
                {
                    smtc.PlaybackStatus = action == "play" ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
                    File.WriteAllText(control + ".ack", command);
                }
            }
            Pump(60);
        }
        smtc.IsEnabled = false;
        return 0;
    }

    static int AutoSwitchIntegration(string fixtureRoot)
    {
        string folder = Path.GetFullPath(fixtureRoot);
        if (!folder.Contains("auto-switch-fix", StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(folder);
        string wave = Path.Combine(folder, "silent.wav");
        // PCM silence: these fixtures expose status only, not real sound.
        using (var output = new BinaryWriter(File.Create(wave)))
        {
            const int bytes = 16000;
            output.Write("RIFF"u8); output.Write(bytes + 36); output.Write("WAVEfmt "u8);
            output.Write(16); output.Write((short)1); output.Write((short)1); output.Write(8000);
            output.Write(16000); output.Write((short)2); output.Write((short)16);
            output.Write("data"u8); output.Write(bytes); output.Write(new byte[bytes]);
        }
        string prefix = "DynamicIsland.FocusTest." + Guid.NewGuid().ToString("N");
        string a = prefix + ".A", b = prefix + ".B";
        string ca = Path.Combine(folder, "a.control"), cb = Path.Combine(folder, "b.control");
        foreach (string file in new[] { ca, cb, ca + ".ready", cb + ".ready", ca + ".ack", cb + ".ack" })
            if (File.Exists(file)) File.Delete(file);
        Process? pa = null, pb = null;
        var hosts = new List<string>();
        int sequence = 0;
        try
        {
            Process Spawn(string id, string path)
            {
                string apphost = Path.ChangeExtension(typeof(Program).Assembly.Location, ".exe");
                string isolated = Path.Combine(Path.GetDirectoryName(apphost)!, "FocusFixture-" + Guid.NewGuid().ToString("N") + ".exe");
                File.Copy(apphost, isolated);
                hosts.Add(isolated);
                var start = new ProcessStartInfo(isolated)
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                foreach (string arg in new[] { "--smtc-fixture", id, path, wave }) start.ArgumentList.Add(arg);
                return Process.Start(start)!;
            }
            pa = Spawn(a, ca); pb = Spawn(b, cb);
            bool Wait(Func<bool> condition, int milliseconds = 5000)
            {
                var time = Stopwatch.StartNew();
                while (time.ElapsedMilliseconds < milliseconds) { if (condition()) return true; Pump(80); }
                return condition();
            }
            void Command(string path, string command)
            {
                string message = (++sequence) + ":" + command;
                File.WriteAllText(path, message);
                if (!Wait(() => File.Exists(path + ".ack") && File.ReadAllText(path + ".ack") == message)) throw new Exception("Fixture acknowledgement timeout");
            }
            if (!Wait(() => File.Exists(ca + ".ready") && File.Exists(cb + ".ready"), 12000)) throw new Exception("Fixture startup timeout");
            var manager = GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().GetAwaiter().GetResult();
            GlobalSystemMediaTransportControlsSession? Find(string title)
            {
                foreach (var s in manager.GetSessions())
                    if (s.TryGetMediaPropertiesAsync().AsTask().GetAwaiter().GetResult().Title == title) return s;
                return null;
            }
            GlobalSystemMediaTransportControlsSession? sa = null, sb = null;
            if (!Wait(() => (sa = Find(a)) != null && (sb = Find(b)) != null, 12000)) throw new Exception("Owned SMTC sessions not published");
            Console.WriteLine($"Owned fixtures ready: distinctSessions={!ReferenceEquals(sa, sb)} distinctSourceIds={sa!.SourceAppUserModelId != sb!.SourceAppUserModelId}");
            if (sa.SourceAppUserModelId == sb.SourceAppUserModelId) throw new Exception("Fixture sources are not isolated");
            Command(ca, "play");
            var model = new MediaService(Dispatcher.CurrentDispatcher);
            _ = Dispatcher.CurrentDispatcher.InvokeAsync(async () => await model.StartAsync());
            if (!Wait(() => typeof(MediaService).GetField("_manager", Private)!.GetValue(model) != null)) return 1;
            manager = (GlobalSystemMediaTransportControlsSessionManager)typeof(MediaService).GetField("_manager", Private)!.GetValue(model)!;
            sa = Find(a); sb = Find(b);
            if (sa == null || sb == null) throw new Exception("Model fixture sessions missing");
            Set(model, "_pinned", sa!); Call(model, "AttachSession");
            if (!Wait(() => model.Title == a && model.IsPlaying))
                throw new Exception($"A selection failed: titleA={model.Title == a}, sourceA={model.Source == sa.SourceAppUserModelId}, nativeStatus={sa.GetPlaybackInfo().PlaybackStatus}, modelPlaying={model.IsPlaying}");
            Command(ca, "pause");
            if (!Wait(() => !model.IsPlaying)) throw new Exception("A pause not received");
            Command(cb, "play");
            if (!Wait(() => model.Title == b && model.IsPlaying))
            {
                Console.WriteLine($"AUTO SWITCH FAIL: pinned A paused -> B playing; selectedA={model.Title == a}; selectedB={model.Title == b}; exit=1");
                return 1;
            }
            Console.WriteLine("AUTO SWITCH PASS: manually selected A -> pause A -> play B -> selects B");
            Set(model, "_pinned", sa!); Call(model, "AttachSession");
            if (!Wait(() => model.Title == a && !model.IsPlaying)) return 1;
            Pump(1400);
            if (model.Title != a) throw new Exception("Already-playing B stole manual browsing of paused A");
            Command(cb, "pause");
            if (!Wait(() => sb!.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)) return 1;
            Pump(150);
            Command(cb, "play");
            if (!Wait(() => model.Title == b && model.IsPlaying)) return 1;
            Console.WriteLine("AUTO SWITCH PASS: paused A can be browsed while B already plays; a fresh B resume releases the manual pin");
            Command(cb, "pause");
            if (!Wait(() => !model.IsPlaying)) return 1;
            Command(ca, "play");
            if (!Wait(() => model.Title == a && model.IsPlaying)) return 1;
            Console.WriteLine("AUTO SWITCH PASS: B paused -> resume A -> selects A");
            // Explicit Home is a page choice, not a media-source pin.
            Set(model, "<HomeSelected>k__BackingField", true);
            Command(ca, "pause"); Command(cb, "play");
            if (!Wait(() => model.Title == b && model.IsPlaying) || !model.HomeSelected) return 1;
            Console.WriteLine("AUTO SWITCH PASS: background source follows B; explicitly selected Home stays Home");
            Console.WriteLine("AUTO SWITCH INTEGRATION PASS; user players received no commands; exit=0");
            return 0;
        }
        finally
        {
            foreach (var item in new[] { (pa, ca), (pb, cb) })
            {
                if (item.Item1 == null) continue;
                File.WriteAllText(item.Item2, "exit");
                if (!item.Item1.WaitForExit(2500)) { item.Item1.Kill(); item.Item1.WaitForExit(2500); }
                item.Item1.Dispose();
            }
            foreach (string host in hosts) File.Delete(host);
            Console.WriteLine("FIXTURE CLEANUP: both owned publisher PIDs exited");
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
}
