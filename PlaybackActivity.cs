namespace DynamicIsland;

// Detect starts/resumes, not merely an already-playing background session.
// This lets manual browsing survive until the user starts another source.
sealed class PlaybackActivity<T> where T : class
{
    readonly Dictionary<T, bool> _playing = new(ReferenceEqualityComparer.Instance);

    public bool IsPlaying(T source) => _playing.TryGetValue(source, out bool value) && value;

    public bool Report(T source, bool? playing)
    {
        // A failed provider read is unknown, not a synthetic pause/resume.
        if (playing is not bool now) return false;
        bool started = now && !IsPlaying(source);
        _playing[source] = now;
        return started;
    }

    public T? Observe(IReadOnlyList<T> sources, Func<T, bool?> playing, T? preferred)
    {
        foreach (T removed in _playing.Keys.Where(s => !sources.Any(live => ReferenceEquals(s, live))).ToArray())
            _playing.Remove(removed);
        T? started = null;
        foreach (T source in sources)
        {
            if (Report(source, playing(source)) && (started == null || ReferenceEquals(source, preferred))) started = source;
        }
        return started;
    }
}
