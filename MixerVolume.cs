namespace DynamicIsland;

readonly record struct MixerLevel(float Volume, bool Muted);

interface IMixerVolume
{
    MixerLevel Level { get; }
    bool SetVolume(float value);
    bool SetMute(bool value);
}

readonly record struct MixerTarget(string Id, IMixerVolume Control);

// SndVol's app fader is endpoint scalar * session scalar (not an amplitude/dB conversion).
// Keep this policy separate from COM so compensation, ordering and failures can be tested.
static class MixerVolume
{
    public static float Display(float master, float session) => Math.Clamp(master * session, 0, 1);
    public static int Percent(float value) => (int)(Math.Clamp(value, 0, 1) * 100f + .5f);

    public static bool Change(IMixerVolume master, IReadOnlyList<MixerTarget> sessions, string[]? ids, float? value, bool? muted)
    {
        if (value is { } number && !float.IsFinite(number) || value == null && muted == null) return false;
        float? requested = value is { } v ? Math.Clamp(v, 0, 1) : null;
        var undo = new Stack<Action>();
        bool Volume(IMixerVolume control, float next)
        {
            float was = control.Level.Volume;
            if (was == next) return true;
            undo.Push(() => control.SetVolume(was));
            return control.SetVolume(next);
        }
        bool Mute(IMixerVolume control, bool next)
        {
            bool was = control.Level.Muted;
            if (was == next) return true;
            undo.Push(() => control.SetMute(was));
            return control.SetMute(next);
        }
        bool Apply()
        {
            if (ids == null)
                return (requested == null || Volume(master, requested.Value))
                    && Mute(master, muted ?? (requested == 0));

            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var targets = sessions.Where(s => wanted.Contains(s.Id)).ToArray();
            // A closed/replaced session must never cause a master-volume change.
            if (targets.Length == 0) return false;
            float previousMaster = master.Level.Volume;
            float nextMaster = Math.Max(previousMaster, requested ?? 0);
            bool unmute = muted == false || muted == null && requested > 0;
            foreach (MixerTarget session in sessions.Where(s => !wanted.Contains(s.Id)))
            {
                // Attenuate others BEFORE raising master; never briefly blast another app.
                if (nextMaster > previousMaster && !Volume(session.Control, session.Control.Level.Volume * previousMaster / nextMaster)) return false;
                // Unmuting one app while master is muted must not unmute every other app.
                if (unmute && master.Level.Muted && !Mute(session.Control, true)) return false;
            }
            foreach (MixerTarget target in targets)
            {
                if (requested is { } level && !Volume(target.Control, nextMaster == 0 ? 0 : level / nextMaster)) return false;
                if ((muted != null || unmute) && !Mute(target.Control, muted ?? false)) return false;
            }
            return (nextMaster == previousMaster || Volume(master, nextMaster)) && (!unmute || Mute(master, false));
        }
        bool successful;
        try { successful = Apply(); }
        catch { successful = false; }
        if (!successful)
        {
            // Core Audio has no atomic transaction. Best-effort restore every attempted field;
            // the UI always rereads actual Windows state, including a disconnected device.
            while (undo.TryPop(out Action? restore)) { try { restore(); } catch { } }
        }
        return successful;
    }
}
