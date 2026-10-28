using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland;

sealed record MixerSession(string Id, string App, string Name, float Volume, bool Muted, bool Active, string IconPath = "");
sealed record MixerChannel(string Key, string Name, string[] Sessions, float Volume, bool Muted, bool Active, string IconPath = "");
sealed record MixerSnapshot(string Device, string Name, float Volume, bool Muted, MixerChannel[] Channels)
{
    public static MixerChannel[] Group(IEnumerable<MixerSession> sessions) => sessions
        .GroupBy(s => s.App, StringComparer.OrdinalIgnoreCase)
        .Select(g => new MixerChannel(g.Key, g.First().Name, g.Select(s => s.Id).Distinct().ToArray(),
            g.Max(s => s.Volume), g.All(s => s.Muted), g.Any(s => s.Active), g.Select(s => s.IconPath).FirstOrDefault(p => p.Length > 0) ?? ""))
        .OrderByDescending(c => c.Active).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
}

sealed partial class AudioService
{
    readonly Dictionary<uint, (long Started, string Path, string Name)> _mixerNames = [];

    public MixerSnapshot? ReadMixer()
    {
        try
        {
            using var connection = MixerConnection.OpenDefault();
            if (connection == null) return null;
            var rows = new List<MixerSession>();
            foreach (var session in connection.Sessions)
            {
                var control = session.Control;
                string instance = session.Id;
                bool system = control.IsSystemSoundsSession() == 0;
                string app, name;
                if (system) (app, name) = ("system", "Системные звуки");
                else if (control.GetProcessId(out uint pid) >= 0) (app, name) = MixerName(pid, instance);
                else (app, name) = (instance, "Приложение");
                string iconPath = SessionIcon(control);
                if (iconPath.Length == 0) iconPath = system ? System.IO.Path.Combine(Environment.SystemDirectory, "SndVol.exe") : app;
                rows.Add(new(instance, app, name, session.Volume.Level.Volume, session.Volume.Level.Muted, session.Active, iconPath));
            }
            // A default-device change during enumeration must not label another device's sessions.
            if (AudioEndpoint.DefaultId() != connection.Id) return null;
            var output = Describe(connection.Device);
            float master = connection.Master.Level.Volume;
            return new(connection.Id, output.Name.Length > 0 ? output.Name : output.Kind,
                master, connection.Master.Level.Muted, MixerSnapshot.Group(rows)
                    .Select(c => c with { Volume = MixerVolume.Display(master, c.Volume) }).ToArray());
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

    static string SessionIcon(IAudioSessionControl2 control)
    {
        IntPtr pointer = IntPtr.Zero;
        try { return control.GetIconPath(out pointer) == 0 ? Marshal.PtrToStringUni(pointer) ?? "" : ""; }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    // Re-resolve live instance IDs for each command; never retain session COM objects or target a reused PID.
    public bool SetMixer(string device, string[]? sessions, float? level = null, bool? muted = null)
    {
        if (level is { } v && !float.IsFinite(v)) return false;
        try
        {
            using var connection = MixerConnection.OpenDefault();
            if (connection == null || connection.Id != device || AudioEndpoint.DefaultId() != device) return false;
            return MixerVolume.Change(connection.Master, connection.Sessions.Select(s => new MixerTarget(s.Id, s.Volume)).ToArray(), sessions, level, muted);
        }
        catch (Exception ex) { App.Log(ex); return false; }
    }
}

// One bound IMMDevice per operation: a default-output switch cannot mix the old
// endpoint volume with the new endpoint's sessions. RCWs live only for this call.
sealed class MixerConnection : IDisposable
{
    internal sealed record Session(string Id, IAudioSessionControl2 Control, NativeMixerVolume Volume, bool Active);
    readonly List<object> _owned = [];
    public IMMDevice Device { get; }
    public string Id { get; }
    public NativeMixerVolume Master { get; }
    public List<Session> Sessions { get; } = [];

    public static MixerConnection? OpenDefault() => AudioEndpoint.Default() is { } device ? new(device) : null;

    // Takes ownership of the supplied device reference. Tests may bind an unused
    // secondary output without changing the user's default device or its volume.
    public MixerConnection(IMMDevice device)
    {
        Device = device;
        _owned.Add(device);
        try
        {
            Marshal.ThrowExceptionForHR(device.GetId(out string? id));
            Id = id ?? throw new InvalidOperationException("Missing endpoint identity.");
            var endpoint = device.Activate<IAudioEndpointVolume>() ?? throw new InvalidOperationException("Missing endpoint volume.");
            _owned.Add(endpoint); Master = new(endpoint);
            var manager = device.Activate<IAudioSessionManager2>() ?? throw new InvalidOperationException("Missing audio sessions.");
            _owned.Add(manager);
            Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out var enumerator));
            if (enumerator == null) throw new InvalidOperationException("Missing session enumerator.");
            _owned.Add(enumerator);
            Marshal.ThrowExceptionForHR(enumerator.GetCount(out int count));
            for (int i = 0; i < count; i++)
            {
                Marshal.ThrowExceptionForHR(enumerator.GetSession(i, out object? session));
                if (session == null) continue;
                _owned.Add(session);
                if (session is not IAudioSessionControl2 control || session is not ISimpleAudioVolume volume) continue;
                if (control.GetState(out int state) != 0 || state == 2) continue;
                IntPtr pointer = IntPtr.Zero;
                try
                {
                    Marshal.ThrowExceptionForHR(control.GetSessionInstanceIdentifier(out pointer));
                    string? instance = Marshal.PtrToStringUni(pointer);
                    if (instance != null) Sessions.Add(new(instance, control, new(volume), state == 1));
                }
                finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
            }
        }
        catch { Dispose(); throw; }
    }

    public void Dispose()
    {
        for (int i = _owned.Count - 1; i >= 0; i--) Marshal.ReleaseComObject(_owned[i]);
        _owned.Clear();
    }
}

sealed class NativeMixerVolume : IMixerVolume
{
    readonly IAudioEndpointVolume? _endpoint;
    readonly ISimpleAudioVolume? _session;
    public MixerLevel Level { get; private set; }
    public NativeMixerVolume(IAudioEndpointVolume endpoint)
    {
        _endpoint = endpoint;
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float level));
        Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool muted));
        Level = new(Math.Clamp(level, 0, 1), muted);
    }
    public NativeMixerVolume(ISimpleAudioVolume session)
    {
        _session = session;
        Marshal.ThrowExceptionForHR(session.GetMasterVolume(out float level));
        Marshal.ThrowExceptionForHR(session.GetMute(out bool muted));
        Level = new(Math.Clamp(level, 0, 1), muted);
    }
    public bool SetVolume(float value)
    {
        Guid context = Guid.Empty;
        int result = _endpoint != null ? _endpoint.SetMasterVolumeLevelScalar(value, ref context) : _session!.SetMasterVolume(value, ref context);
        if (result != 0) return false;
        Level = Level with { Volume = value }; return true;
    }
    public bool SetMute(bool value)
    {
        Guid context = Guid.Empty;
        int result = _endpoint != null ? _endpoint.SetMute(value, ref context) : _session!.SetMute(value, ref context);
        if (result != 0) return false;
        Level = Level with { Muted = value }; return true;
    }
}
