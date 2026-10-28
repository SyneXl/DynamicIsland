using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using DynamicIsland;

static partial class Program
{
    sealed class TestMixerVolume(string name, float volume, bool muted = false, List<string>? writes = null) : IMixerVolume
    {
        public MixerLevel Level { get; private set; } = new(volume, muted);
        public bool FailNext, ThrowNext;
        public bool SetVolume(float value)
        {
            writes?.Add(name + ":volume=" + value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
            if (ThrowNext) { ThrowNext = false; throw new COMException("fixture disconnected"); }
            if (FailNext) { FailNext = false; return false; }
            Level = Level with { Volume = value }; return true;
        }
        public bool SetMute(bool value)
        {
            writes?.Add(name + ":mute=" + value);
            if (FailNext) { FailNext = false; return false; }
            Level = Level with { Muted = value }; return true;
        }
    }

    static int MixerParityTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var trace = new List<string>();
            Check(MixerVolume.Percent(.625f) == 63 && MixerVolume.Percent(.505f) == 51 && MixerVolume.Percent(.5f) == 50,
                "percentage uses SndVol half-up rounding, not banker's rounding");
            var master = new TestMixerVolume("master", 1, false, trace);
            var music = new TestMixerVolume("music", 1, false, trace);
            var video = new TestMixerVolume("video", 1, false, trace);
            var second = new TestMixerVolume("second", .4f, true, trace);
            MixerTarget[] sessions = [new("music", music), new("video", video), new("second", second)];
            bool Change(string[]? ids, float? value = null, bool? muted = null) => MixerVolume.Change(master, sessions, ids, value, muted);
            void Near(float actual, float expected, string name) => Check(Math.Abs(actual - expected) < .00001f, name);
            Check(Change(null, .5f), "master can be lowered");
            Near(music.Level.Volume, 1, "lowering master preserves relative session volume");
            Near(MixerVolume.Display(master.Level.Volume, music.Level.Volume), .5f, "app fader follows master down to 50%, like SndVol");
            Near(MixerVolume.Display(master.Level.Volume, second.Level.Volume), .2f, "quieter app follows master proportionally");
            int before = trace.Count;
            Check(Change(["music"], .75f), "raising app beyond master succeeds");
            Near(master.Level.Volume, .75f, "raising app to 75% raises master to 75%");
            Near(music.Level.Volume, 1, "target app is at full relative volume");
            Near(MixerVolume.Display(master.Level.Volume, video.Level.Volume), .5f, "other app stays at 50% after master promotion");
            Near(MixerVolume.Display(master.Level.Volume, second.Level.Volume), .2f, "muted background app retains its absolute fader");
            Check(second.Level.Muted, "master promotion preserves existing application mute");
            Check(trace.IndexOf("master:volume=0.75", before) > trace.FindIndex(before, x => x.StartsWith("video:volume=")),
                "other sessions are attenuated before master rises, avoiding a loud transient");
            Check(Change(["video"], .3f), "lowering one app is independent");
            Near(master.Level.Volume, .75f, "lowering app does not lower master");
            Near(video.Level.Volume, .4f, "75% master and 30% fader maps to raw 40%, matching user's screenshot");
            Near(MixerVolume.Display(master.Level.Volume, video.Level.Volume), .3f, "display is combined scalar, not raw session percentage");
            Check(Change(null, 1), "raising master changes all effective app faders");
            Near(video.Level.Volume, .4f, "master changes never rewrite stored session balance");
            Near(MixerVolume.Display(master.Level.Volume, video.Level.Volume), .4f, "app follows raised master");

            Check(Change(null, 0) && master.Level.Muted, "master zero is muted without dividing by zero");
            Near(video.Level.Volume, .4f, "master zero retains application ratios for a later master raise");
            Check(Change(["video"], .25f), "an app can recover from zero master");
            Near(master.Level.Volume, .25f, "app raises zero master to requested level");
            Check(!master.Level.Muted && !video.Level.Muted && music.Level.Muted && second.Level.Muted,
                "recovering one app from global mute leaves other apps muted, like SndVol");
            Near(music.Level.Volume, 0, "other app at zero stays zero after master promotion");
            Check(Change(null, muted: false), "explicit master unmute is valid");
            Check(Change(null, muted: true) && Change(["music"], muted: false), "unmuting an app can release endpoint mute");
            Check(!master.Level.Muted && !music.Level.Muted && video.Level.Muted, "app unmute does not unexpectedly unmute another application");
            Check(Change(["music", "second"], .1f), "multiple exact sessions can be grouped behind one fader");
            Near(music.Level.Volume, .4f, "first session receives requested group level");
            Near(second.Level.Volume, .4f, "second session receives identical group level");
            Check(Change(["music", "expired"], .05f), "remaining live group sessions survive one expired member");
            var saved = new[] { master.Level, music.Level, video.Level, second.Level };
            before = trace.Count;
            Check(!Change(["expired"], .9f) && trace.Count == before, "expired target cannot raise master or touch unrelated apps");
            Check(!Change([], .9f) && trace.Count == before, "empty target list does not mean master");
            Check(!Change(null, float.NaN) && !Change(null, float.PositiveInfinity) && !Change(["music"], float.NegativeInfinity),
                "nonfinite user inputs are rejected");
            Check(!Change(null) && trace.Count == before, "empty command has no side effects");
            video.FailNext = true;
            Check(!Change(["music"], .9f), "compensation failure aborts master promotion");
            Check(saved.SequenceEqual(new[] { master.Level, music.Level, video.Level, second.Level }), "failed compensation restores all attempted controls");
            master.FailNext = true;
            Check(!Change(["music"], .9f), "endpoint write failure is propagated");
            Check(saved.SequenceEqual(new[] { master.Level, music.Level, video.Level, second.Level }), "endpoint failure rolls back already compensated sessions");
            second.ThrowNext = true;
            Check(!Change(["music"], .9f), "session disconnection exception is contained");
            Check(saved.SequenceEqual(new[] { master.Level, music.Level, video.Level, second.Level }), "disconnection still restores previously changed sessions");
            Check(Change(["music"], -1), "negative finite input is clamped"); Near(music.Level.Volume, 0, "negative app level becomes exact silence");
            music.SetVolume(.0000005f);
            Check(Change(["music"], 0) && music.Level.Volume == 0, "zero must be exact silence, even below a floating-point epsilon");
            Check(Change(["music"], 2), "oversized finite input is clamped"); Near(master.Level.Volume, 1, "clamped app level promotes master to 100%");

            var random = new Random(31891);
            for (int i = 0; i < 150; i++)
            {
                float m = (float)random.NextDouble(), a = (float)random.NextDouble(), b = (float)random.NextDouble(), requested = (float)random.NextDouble();
                var endpoint = new TestMixerVolume("master", m);
                var target = new TestMixerVolume("target", a); var other = new TestMixerVolume("other", b);
                Check(MixerVolume.Change(endpoint, [new("a", target), new("b", other)], ["a"], requested, null)
                    && Math.Abs(endpoint.Level.Volume - Math.Max(m, requested)) < .00001f
                    && Math.Abs(MixerVolume.Display(endpoint.Level.Volume, target.Level.Volume) - requested) < .00001f
                    && Math.Abs(MixerVolume.Display(endpoint.Level.Volume, other.Level.Volume) - m * b) < .00001f,
                    "randomized SndVol coupling and background-fader invariant " + i);
            }
            var icons = new MixerIcons();
            string iconFile = Path.Combine(Environment.SystemDirectory, "imageres.dll");
            var image = icons.Get(iconFile, "");
            Check(image is { IsFrozen: true, PixelWidth: > 0 }, "real Win32 application icon is extracted and frozen");
            Check(ReferenceEquals(image, icons.Get(iconFile, "")), "app icons are cached instead of extracted each refresh");
            Check(icons.Get("not-an-icon", iconFile) != null, "invalid session artwork falls back to executable icon");
            Check(icons.Get("missing.exe", "missing") == null, "missing icon is harmless and uses UI fallback");
            Check(MixerIcons.Parse("@\"%SystemRoot%\\System32\\imageres.dll\",-15") is { Index: -15 }, "Windows indirect icon resource IDs and environment variables are parsed");
            Check(MixerIcons.Parse("https://example.com/icon.ico") == null && MixerIcons.Parse(@"\\server\share\icon.ico") == null
                && MixerIcons.Parse("relative.exe") == null, "session icons cannot trigger network or relative-shell lookups");
            using var process = Process.GetCurrentProcess();
            int gdiBefore = GetGuiResources(process.Handle, 0), userBefore = GetGuiResources(process.Handle, 1);
            for (int i = 0; i < 80; i++) _ = new MixerIcons().Get(iconFile, "");
            Check(GetGuiResources(process.Handle, 0) <= gdiBefore + 2 && GetGuiResources(process.Handle, 1) <= userBefore + 2,
                "repeated icon extraction releases both HICONs without GDI/USER handle leaks");
            File.WriteAllText(Path.Combine(output, "policy-trace.json"), JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                nativeReference = "Windows 10 SndVol.exe 10.0.19041.7291", formula = "display = masterScalar * sessionScalar", userAudioChanged = false }));
            Console.WriteLine($"PASS: {_checks} mixer parity checks; exit=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int NativeMixerParity(string output)
    {
        var owned = new List<object>(); var clients = new List<IAudioClient>();
        var restored = new List<(ISimpleAudioVolume Volume, float Level, bool Muted)>();
        bool fixtureRestored = false;
        try
        {
            Directory.CreateDirectory(output);
            string? defaultId = AudioEndpoint.DefaultId();
            var enumerator = (IParityDeviceEnumerator)new MMDeviceEnumeratorCom(); owned.Add(enumerator);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(0, 1, out var collection)); owned.Add(collection);
            collection.GetCount(out uint count);
            string? isolatedId = null;
            for (uint i = 0; i < count; i++)
            {
                collection.Item(i, out var candidate);
                using var connection = new MixerConnection(candidate);
                if (connection.Id != defaultId && connection.Sessions.Count == 0) { isolatedId = connection.Id; break; }
            }
            IMMDevice? device;
            if (isolatedId == null) device = AudioEndpoint.Default();
            else { enumerator.GetDevice(isolatedId, out device); }
            Check(device != null, "native parity fixture binds a real Windows render endpoint");
            owned.Add(device!);
            var sessionIds = new List<string>();
            for (int i = 0; i < 3; i++)
            {
                var client = device!.Activate<IAudioClient>()!; clients.Add(client); owned.Add(client);
                client.GetMixFormat(out IntPtr format);
                IntPtr guid = Marshal.AllocCoTaskMem(16);
                Marshal.StructureToPtr(Guid.NewGuid(), guid, false);
                try { Marshal.ThrowExceptionForHR(client.Initialize(0, 0x80000, 10000000, 0, format, guid)); }
                finally { Marshal.FreeCoTaskMem(format); Marshal.FreeCoTaskMem(guid); }
                Guid renderId = typeof(IFeatureAudioRenderClient).GUID;
                client.GetService(ref renderId, out object? render); owned.Add(render!);
                client.GetBufferSize(out uint frames);
                var buffer = (IFeatureAudioRenderClient)render!;
                Marshal.ThrowExceptionForHR(buffer.GetBuffer(frames, out _));
                Marshal.ThrowExceptionForHR(buffer.ReleaseBuffer(frames, 2)); // explicit silence
                Marshal.ThrowExceptionForHR(client.Start());
                Guid controlId = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"); // GetService requests base IAudioSessionControl, then QI for v2.
                Marshal.ThrowExceptionForHR(client.GetService(ref controlId, out object? rawControl)); owned.Add(rawControl!);
                var control = (IAudioSessionControl2)rawControl!;
                control.GetSessionInstanceIdentifier(out IntPtr text);
                try { sessionIds.Add(Marshal.PtrToStringUni(text)!); } finally { Marshal.FreeCoTaskMem(text); }
                Guid volumeId = typeof(ISimpleAudioVolume).GUID;
                client.GetService(ref volumeId, out object? rawVolume); owned.Add(rawVolume!);
                var volume = (ISimpleAudioVolume)rawVolume!;
                volume.GetMasterVolume(out float level); volume.GetMute(out bool muted); restored.Add((volume, level, muted));
            }
            // One extra device reference is owned by the short-lived connection.
            IMMDevice? bound;
            if (isolatedId == null) bound = AudioEndpoint.Default(); else enumerator.GetDevice(isolatedId, out bound);
            using var live = new MixerConnection(bound!);
            var targets = live.Sessions.Where(s => sessionIds.Contains(s.Id)).Select(s => new MixerTarget(s.Id, s.Volume)).ToArray();
            Check(targets.Length == 3, "Core Audio enumerates all three independent owned silent sessions");
            var userMasterWas = live.Master.Level;
            var foreignWas = live.Sessions.Where(s => !sessionIds.Contains(s.Id)).ToDictionary(s => s.Id, s => s.Volume.Level);
            // If no unused output exists, exercise real session writes against an in-memory
            // master. Never alter the user's active output, mute or unrelated sessions.
            IMixerVolume master = isolatedId == null ? new TestMixerVolume("isolated-master", 1) : live.Master;
            Guid context = Guid.Empty;
            try
            {
                Check(MixerVolume.Change(master, targets, null, 1, false), "native fixture starts from full master");
                foreach (MixerTarget target in targets) { target.Control.SetVolume(1); target.Control.SetMute(false); }
                Check(MixerVolume.Change(master, targets, null, .5f, null), "native master-down path succeeds");
                Check(MixerVolume.Change(master, targets, [targets[0].Id], .75f, null), "native target-above-master path succeeds");
                foreach (var target in targets)
                {
                    var original = restored[sessionIds.IndexOf(target.Id)].Volume;
                    original.GetMasterVolume(out float actual);
                    float expected = target.Id == targets[0].Id ? 1 : 2f / 3;
                    Check(Math.Abs(actual - expected) < .00001, "native API confirms session compensation for " + target.Id);
                }
                Check(MixerVolume.Change(master, targets, [targets[1].Id], .3f, null), "native within-master adjustment succeeds");
                restored[sessionIds.IndexOf(targets[1].Id)].Volume.GetMasterVolume(out float adjusted);
                Check(Math.Abs(adjusted - .4f) < .00001, "native 75% master / 30% fader produces raw 40% session volume");
                Check(!MixerVolume.Change(master, targets, ["expired"], 1, null), "native stale-session control does not touch the master");
            }
            finally
            {
                if (isolatedId != null)
                {
                    live.Master.SetVolume(userMasterWas.Volume); live.Master.SetMute(userMasterWas.Muted);
                    using var verify = GetParityConnection(enumerator, isolatedId);
                    Check(Math.Abs(verify.Master.Level.Volume - userMasterWas.Volume) < .005 && verify.Master.Level.Muted == userMasterWas.Muted,
                        "unused native endpoint volume and mute are restored exactly");
                }
                foreach (var entry in restored)
                {
                    Marshal.ThrowExceptionForHR(entry.Volume.SetMasterVolume(entry.Level, ref context));
                    Marshal.ThrowExceptionForHR(entry.Volume.SetMute(entry.Muted, ref context));
                    entry.Volume.GetMasterVolume(out float volume); entry.Volume.GetMute(out bool mute);
                    Check(Math.Abs(volume - entry.Level) < .00001 && mute == entry.Muted, "owned silent session rollback confirmed by raw API readback");
                }
                fixtureRestored = true;
            }
            Check(AudioEndpoint.DefaultId() == defaultId, "native parity never switches the default output");
            if (isolatedId == null)
            {
                using var verify = MixerConnection.OpenDefault()!;
                Check(verify.Master.Level == userMasterWas, "user endpoint volume and mute remain unchanged after native parity tests");
                Check(verify.Sessions.Where(s => foreignWas.ContainsKey(s.Id)).All(s => s.Volume.Level == foreignWas[s.Id]),
                    "unrelated application volume and mute remain unchanged after native parity tests");
            }
            File.WriteAllText(Path.Combine(output, "native-parity.json"), JsonSerializer.Serialize(new { passed = true, checks = _checks,
                realEndpointMasterTested = isolatedId != null, realSessionCompensationTested = true, userDefaultAudioChanged = false, fixtureRestored,
                limitation = isolatedId == null ? "No unused output: only fixture sessions were changed; master used a test double." : null }));
            Console.WriteLine($"PASS: {_checks} native mixer parity checks; realEndpointMaster={isolatedId != null}; restored={fixtureRestored}; exit=0"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            foreach (var client in clients) { try { client.Stop(); } catch { } }
            for (int i = owned.Count - 1; i >= 0; i--) if (owned[i] != null) Marshal.ReleaseComObject(owned[i]);
        }
    }

    static MixerConnection GetParityConnection(IParityDeviceEnumerator enumerator, string id)
    {
        Marshal.ThrowExceptionForHR(enumerator.GetDevice(id, out var device)); return new(device!);
    }

    [DllImport("user32.dll")] static extern int GetGuiResources(IntPtr process, int flags);
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IParityDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IParityDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice? device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IParityDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }
}
