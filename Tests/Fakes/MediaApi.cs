// Compiled into DynamicIsland only with -p:MediaTestApi=true; absent from production.
using System.Runtime.CompilerServices;
using Windows.Foundation;
using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DynamicIsland.Testing;

sealed class TestMediaManager
{
    public List<TestMediaSession> Live = [];
    public TestMediaSession? Current;
    public bool ThrowListing, ThrowCurrent;
    public int Listings;
    public static Task<TestMediaManager> RequestAsync() => throw new InvalidOperationException("Test API: live startup is disabled");
    public event Action<TestMediaManager, object?>? CurrentSessionChanged;
    public event Action<TestMediaManager, object?>? SessionsChanged;
    public int Subscribers => (CurrentSessionChanged?.GetInvocationList().Length ?? 0) + (SessionsChanged?.GetInvocationList().Length ?? 0);
    public IEnumerable<TestMediaSession> GetSessions()
    {
        Listings++;
        return ThrowListing ? throw new InvalidOperationException("Test provider: list failed") : Live;
    }
    public TestMediaSession? GetCurrentSession() => ThrowCurrent ? throw new InvalidOperationException("Test provider: current failed") : Current;
    public void EmitCurrent() => CurrentSessionChanged?.Invoke(this, null);
    public void EmitSessions() => SessionsChanged?.Invoke(this, null);
}

sealed class TestMediaProperties
{
    public string Title = "", Artist = "";
    public IRandomAccessStreamReference? Thumbnail => null;
    public MediaPlaybackType? PlaybackType => MediaPlaybackType.Music;
}
sealed class TestMediaPlayback
{
    public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus;
    public double? PlaybackRate => 1;
}
sealed class TestMediaTimeline
{
    public TimeSpan StartTime => TimeSpan.Zero;
    public TimeSpan EndTime, Position;
    public DateTimeOffset LastUpdatedTime = DateTimeOffset.UtcNow;
}
sealed class TestMediaPropertiesOperation(Task<TestMediaProperties> task)
{
    public TaskAwaiter<TestMediaProperties> GetAwaiter() => task.GetAwaiter();
    public Task<TestMediaProperties> AsTask(CancellationToken stop) => task.WaitAsync(stop);
}
sealed class TestMediaSession(string id, string title, double position = 10, double duration = 100)
{
    public string SourceAppUserModelId => id;
    public TestMediaProperties Properties = new() { Title = title, Artist = id };
    public Task<TestMediaProperties>? PropertiesPending;
    public TestMediaPlayback Playback = new() { PlaybackStatus = GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused };
    public TestMediaTimeline Timeline = new() { Position = TimeSpan.FromSeconds(position), EndTime = TimeSpan.FromSeconds(duration) };
    public TaskCompletionSource<bool>? SeekPending;
    public int Seeks, Commands, PropertyReads;
    public bool PlaybackThrows;
    public event Action<TestMediaSession, MediaPropertiesChangedEventArgs>? MediaPropertiesChanged;
    public event Action<TestMediaSession, PlaybackInfoChangedEventArgs>? PlaybackInfoChanged;
    public event Action<TestMediaSession, TimelinePropertiesChangedEventArgs>? TimelinePropertiesChanged;
    public int Subscribers => (MediaPropertiesChanged?.GetInvocationList().Length ?? 0) + (PlaybackInfoChanged?.GetInvocationList().Length ?? 0) + (TimelinePropertiesChanged?.GetInvocationList().Length ?? 0);
    public TestMediaPlayback GetPlaybackInfo() => PlaybackThrows ? throw new InvalidOperationException("Test provider: status failed") : Playback;
    public TestMediaTimeline GetTimelineProperties() => Timeline;
    public TestMediaPropertiesOperation TryGetMediaPropertiesAsync() { PropertyReads++; return new(PropertiesPending ?? Task.FromResult(Properties)); }
    public Task<bool> TryChangePlaybackPositionAsync(long ticks) { Seeks++; return SeekPending?.Task ?? Task.FromResult(true); }
    public IAsyncOperation<bool> TryTogglePlayPauseAsync() { Commands++; return null!; }
    public IAsyncOperation<bool> TrySkipNextAsync() { Commands++; return null!; }
    public IAsyncOperation<bool> TrySkipPreviousAsync() { Commands++; return null!; }
    public void EmitPlayback() => PlaybackInfoChanged?.Invoke(this, null!);
    public void EmitProperties() => MediaPropertiesChanged?.Invoke(this, null!);
    public void EmitTimeline() => TimelinePropertiesChanged?.Invoke(this, null!);
}
