using DynamicIsland;

static partial class Program
{
    sealed class FocusSource(string name) { public string Name = name; public bool Playing; }

    static void PlaybackActivityTests()
    {
        var telegram = new FocusSource("Telegram") { Playing = true };
        var video = new FocusSource("Video");
        var activity = new PlaybackActivity<FocusSource>();
        FocusSource[] sources = [telegram, video];
        bool? Playing(FocusSource s) => s.Playing;
        Check(activity.Observe(sources, Playing, telegram) == telegram, "initial playing source detected");
        Check(activity.Observe(sources, Playing, telegram) == null, "unchanged playback does not steal manual selection");
        telegram.Playing = false;
        Check(activity.Observe(sources, Playing, telegram) == null, "pause is not a playback start");
        video.Playing = true;
        Check(activity.Observe(sources, Playing, telegram) == video, "Telegram paused -> video starts despite stale Windows current source");
        Check(activity.IsPlaying(video) && !activity.IsPlaying(telegram), "all source playback states cached");
        Check(activity.Observe(sources, Playing, telegram) == null, "polling the same states never repeatedly switches");
        telegram.Playing = true;
        Check(activity.Observe(sources, Playing, video) == telegram, "resumed Telegram takes focus from an older playing source");
        telegram.Playing = video.Playing = false;
        activity.Observe(sources, Playing, null);
        telegram.Playing = video.Playing = true;
        Check(activity.Observe(sources, Playing, video) == video, "simultaneous observed starts prefer Windows current source");
        var player = new FocusSource("New player") { Playing = true };
        Check(activity.Observe([telegram, video, player], Playing, telegram) == player, "new already-playing session is a new start");
        Check(activity.Observe([telegram], Playing, null) == null && !activity.IsPlaying(video) && !activity.IsPlaying(player), "removed sessions do not retain playback activity");
        Check(activity.Observe([], Playing, null) == null && !activity.IsPlaying(telegram), "empty session list clears playback state");
        activity.Report(telegram, true);
        Check(!activity.Report(telegram, null) && activity.IsPlaying(telegram), "unknown playback does not erase a known playing state");
        Check(!activity.Report(telegram, true), "recovered provider is not a synthetic resume");
        activity.Report(telegram, false);
        Check(activity.Report(telegram, true), "captured pause and resume still create a real start");
    }
}
