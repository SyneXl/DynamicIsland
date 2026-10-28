namespace DynamicIsland;

// Monotonic time is supplied by the caller, so transitions are deterministic in tests.
sealed class CinemaState
{
    public const double EnterDelay = 0.65;
    // The 1.2 s cadence is quantized by a 0.5 s UI tick (~1.5 s). Retain one
    // missed result plus scheduling/provider slack without flashing the overlay.
    public const double EvidenceLifetime = 3.5;
    double _first = double.NaN, _last = double.NegativeInfinity;

    public void Report(bool video, double now)
    {
        if (!video) return; // Brief accessibility rebuilds must not flash the overlay.
        if (!HasEvidence(now)) _first = now;
        _last = now;
    }

    public bool HasEvidence(double now) => now >= _last && now - _last <= EvidenceLifetime;
    public bool IsActive(double now) => HasEvidence(now) && now - _first >= EnterDelay;
    public void Reset() { _first = double.NaN; _last = double.NegativeInfinity; }
}
