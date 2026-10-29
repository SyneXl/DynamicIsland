namespace DynamicIsland;

// Hover/focus/notifications cancel immediately; repeated refreshes never postpone
// the deadline. No global input hook, activity tracking or extra polling loop.
sealed class QuietAppearanceState
{
    public const double DelaySeconds = .65;
    double? _eligibleSince;
    public bool Pending { get; private set; }
    public double Remaining { get; private set; }

    public bool Update(bool eligible, double now)
    {
        if (!eligible)
        {
            _eligibleSince = null; Pending = false; Remaining = 0; return false;
        }
        _eligibleSince ??= now;
        Remaining = Math.Max(0, DelaySeconds - (now - _eligibleSince.Value));
        Pending = Remaining > .000001;
        return !Pending;
    }
}
