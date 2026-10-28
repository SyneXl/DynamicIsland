using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland;

sealed record MixerSession(string Id, string App, string Name, float Volume, bool Muted, bool Active);
sealed record MixerChannel(string Key, string Name, string[] Sessions, float Volume, bool Muted, bool Active);
sealed record MixerSnapshot(string Device, string Name, float Volume, bool Muted, MixerChannel[] Channels)
{
    public static MixerChannel[] Group(IEnumerable<MixerSession> sessions) => sessions
        .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
        .Select(g => new MixerChannel(g.Key, g.First().Name, g.Select(s => s.Id).Distinct().ToArray(),
            g.Max(s => s.Volume), g.All(s => s.Muted), g.Any(s => s.Active)))
        .OrderByDescending(c => c.Active).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
}

sealed partial class AudioService
{
    readonly Dictionary<uint, (long Started, string Path, string Name)> _mixerNames = [];

    public MixerSnapshot? ReadMixer()
    {
        try
        {
            RefreshDevice();
            if (!TryGetVolume(out float level, out bool muted) || _deviceId == null) return null;
            string id = _deviceId;
            var rows = new List<MixerSession>();
            foreach (object session in Sessions())
            {
                if (session is not IAudioSessionControl2 control || session is not ISimpleAudioVolume volume
                    || control.GetState(out int state) != 0 || state == SessionExpired
                    || volume.GetMasterVolume(out float value) != 0 || volume.GetMute(out bool mute) != 0) continue;
                string? instance = SessionId(control);
                if (instance == null) continue;
                bool system = control.IsSystemSoundsSession() == 0;
                string app, name;
                if (system) (app, name) = ("system", "Системные звуки");
                else if (control.GetProcessId(out uint pid) >= 0) (app, name) = MixerName(pid, instance);
                else (app, name) = (instance, "Приложение");
                rows.Add(new(instance, app, name, Math.Clamp(value, 0, 1), mute, state == 1));
            }
            // A default-device change during enumeration must not label another device's sessions.
            if (AudioEndpoint.DefaultId() != id) return null;
            return new(id, Device?.Name is { Length: > 0 } deviceName ? deviceName : Device?.Kind ?? "Звук",
                level, muted, MixerSnapshot.Group(rows));
        }
        catch (Exception ex) { App.Log(ex); return null; }
    }

    (string App, string Name) MixerName(uint pid, string fallback)
    {
        try
        {
            using var process = Process.GetProcessById(checked((int)pid));
            long started = process.StartTime.ToUniversalTime().Ticks;
            if (_mixerNames.TryGetValue(pid, out var cached) && cached.Started == started) return (cached.Path, cached.Name);
            string path = process.MainModule?.FileName ?? "";
            string name = path.Length > 0 ? FileVersionInfo.GetVersionInfo(path).FileDescription ?? "" : "";
            if (string.IsNullOrWhiteSpace(name)) name = process.ProcessName;
            if (_mixerNames.Count > 256) _mixerNames.Clear();
            _mixerNames[pid] = (started, path.Length > 0 ? path : fallback, name);
            return (_mixerNames[pid].Path, name);
        }
        catch { return (fallback, "Приложение " + pid); }
    }

    static string? SessionId(IAudioSessionControl2 control)
    {
        IntPtr pointer = IntPtr.Zero;
        try { return control.GetSessionInstanceIdentifier(out pointer) == 0 ? Marshal.PtrToStringUni(pointer) : null; }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    // Re-resolve live instance IDs for each command; never retain session COM objects or target a reused PID.
    public bool SetMixer(string device, string[]? sessions, float? level = null, bool? muted = null)
    {
        if (level is { } v && !float.IsFinite(v)) return false;
        try
        {
            _checkedAt = DateTime.MinValue;
            RefreshDevice();
            if (_deviceId != device || _volume == null) return false;
            Guid context = Guid.Empty;
            if (sessions == null)
                return (level == null || _volume.SetMasterVolumeLevelScalar(Math.Clamp(level.Value, 0, 1), ref context) == 0)
                    && (muted == null || _volume.SetMute(muted.Value, ref context) == 0);
            var wanted = sessions.ToHashSet(StringComparer.Ordinal);
            bool found = false, successful = true;
            foreach (object session in Sessions())
            {
                if (session is not IAudioSessionControl2 control || session is not ISimpleAudioVolume volume
                    || control.GetState(out int state) != 0 || state == SessionExpired
                    || SessionId(control) is not { } id || !wanted.Contains(id)) continue;
                bool ok = (level == null || volume.SetMasterVolume(Math.Clamp(level.Value, 0, 1), ref context) == 0)
                    && (muted == null || volume.SetMute(muted.Value, ref context) == 0);
                found = true; successful &= ok;
            }
            return found && successful;
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }
}
