using System.IO;
using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Media;
using Windows.Storage.Streams;
#if MEDIA_TEST_API
using Manager = DynamicIsland.Testing.TestMediaManager;
using Session = DynamicIsland.Testing.TestMediaSession;
#else
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
#endif
using Status = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DynamicIsland;

sealed class MediaService : IDisposable
{
    const string Untitled = "Без названия";
    const string BarSeparator = " – ";
    const int MinPercentToMeasure = 10;
    const int PollPlayingMs = 250, PollPausedMs = 1000;
    const int UnsetYear = 2000;
    const int MetadataTimeoutMs = 3000, RetryMs = 1000, SessionScanMs = 1000;

    readonly Dispatcher _ui;
    readonly Action<Exception> _log;
    readonly CancellationTokenSource _lifetime = new();
    readonly ConcurrentQueue<(Session Session, bool? Playing)> _pendingPlayback = new();
    readonly object _barGate = new();
    readonly PlayerBar _bar = new();
    readonly MpcTimeline _mpc = new();
    readonly PlaybackActivity<Session> _activity = new();
    Manager? _manager;
    Session? _session;
    Session? _pinned;
    Session? _recentlyStarted;
    Session[] _sessions = [];
    int _refreshVersion;
    long _contextVersion, _nextSessionScan, _nextMetadataRetry;
    int _seekVersion;
    volatile bool _ready, _disposed;
    bool _started;
    Task? _playbackTask, _barTask;

    string _title = "", _artist = "";
    TimeSpan _position, _duration;
    DateTime _positionAt = DateTime.UtcNow;
    DateTimeOffset _timelineStamp;
    double _rate = 1;
    Status _status;

    bool _noTimeline;
    bool _barFound;
    bool _mpcFound;
    TimeSpan _mpcDuration;
    string _barText = "", _barTitle = "", _barArtist = "";
    TimeSpan _barTime = TimeSpan.MinValue;
    int _barPercent = -1;
    TimeSpan _measuredLength;
    TimeSpan _assumedLength;

    public MediaService(Dispatcher ui, Action<Exception>? log = null) { _ui = ui; _log = log ?? App.Log; }

    public event Action? Changed;
    public bool HomeSelected { get; private set; }
    public int SourceCount => _sessions.Length;
    public bool HasSelectedSession => _pinned != null;
    public bool IsLoading { get; private set; }
    public bool HasSession => _session != null;
    public bool CanControl => _ready && !_disposed && _session != null;
    public long ContextVersion => Interlocked.Read(ref _contextVersion);
    public long CaptureContext() => CanControl ? ContextVersion : -1;
    public bool IsCurrentContext(long context) => context >= 0 && context == ContextVersion && CanControl;

    public string Title => _title.Length > 0 ? _title : _barTitle;
    public string Artist => _title.Length > 0 ? _artist : _barArtist;
    public string Name => Title.Length > 0 ? Title : SourceApp.Name(Source) is { Length: > 0 } app ? app : Untitled;
    public ImageSource? Art { get; private set; }
    public Color[] Palette { get; private set; } = CoverPalette.Plain;
    public Color Accent => Palette[0];
    public bool IsPlaying { get; private set; }
    public bool HasTrack => CanControl && (Title.Length > 0 || IsLive);
    public TimeSpan Duration => _duration;
    public bool HasBarPosition => _noTimeline && (_barFound || _mpcFound);
    public bool Seekable => !IsLoading && (_noTimeline ? _mpcFound || _barFound : _duration.TotalSeconds >= 1);

    public double Progress => _duration.TotalSeconds >= 1 ? Math.Clamp(Position / _duration, 0, 1)
        : HasBarPosition && _barPercent > 0 ? _barPercent / 100.0 : 0;

    public string Source
    {
        get
        {
            try { return _session?.SourceAppUserModelId ?? ""; }
            catch { return ""; }
        }
    }

    public TimeSpan Position
    {
        get
        {
            TimeSpan position = RunningPosition;
            if (position < TimeSpan.Zero || (_noTimeline && !_barFound && !_mpcFound)) return TimeSpan.Zero;
            bool endless = _noTimeline && _duration <= TimeSpan.Zero;
            return position > _duration && !endless ? _duration : position;
        }
    }

    TimeSpan RunningPosition => IsPlaying ? _position + (DateTime.UtcNow - _positionAt) * _rate : _position;

    bool IsLive => _status is Status.Playing or Status.Paused;

    public void AssumeDuration(TimeSpan length)
    {
        _assumedLength = length;
        if (_noTimeline) UpdateBarDuration();
    }

    public async Task StartAsync()
    {
        if (_started || _disposed) return;
        _started = true;
        try
        {
            _manager = await Manager.RequestAsync();
            if (_disposed) return;
            _manager.CurrentSessionChanged += OnManagerChanged;
            _manager.SessionsChanged += OnManagerChanged;
            try { AttachSession(); } catch (Exception ex) { _log(ex); }
            _barTask = WatchBarAsync(_lifetime.Token);
            _playbackTask = WatchPlaybackAsync(_lifetime.Token);
        }
        catch { _started = false; throw; }
    }

    void Queue(Action action)
    {
        if (_disposed || _ui.HasShutdownStarted) return;
        _ = _ui.InvokeAsync(() =>
        {
            if (_disposed) return;
            try { action(); } catch (Exception ex) { _log(ex); }
        });
    }

    void OnManagerChanged(Manager sender, object? args) => Queue(AttachSession);

    public async Task<bool> IsVideoForProcessAsync(uint process, CancellationToken stop)
    {
        Manager? manager = _manager;
        if (manager == null) return false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(600));
        foreach (Session session in manager.GetSessions())
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (!SourceApp.OwnsProcess(session.SourceAppUserModelId, process)) continue;
            var status = session.GetPlaybackInfo().PlaybackStatus;
            if (status is not (Status.Playing or Status.Paused)) continue;
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(deadline.Token).ConfigureAwait(false);
            if (properties.PlaybackType == MediaPlaybackType.Video
                && (!MpcTimeline.Supports(session.SourceAppUserModelId)
                    || MpcTimeline.TrackBelongsToProcess(session.SourceAppUserModelId, properties.Title ?? "", process))) return true;
        }
        return false;
    }

    public bool NavigatePage(int direction)
    {
        try
        {
            if (direction == 0) return false;
            ReadSessions();
            ObservePlaybackActivity(); // Drain already-playing sources before a new manual choice.
            if (_sessions.Length == 0) return false;
            int at = HomeSelected ? 0 : Array.FindIndex(_sessions, s => ReferenceEquals(s, _session)) + 1;
            int next = PageRing.Next(at, _sessions.Length, direction);
            HomeSelected = next == 0;
            if (HomeSelected)
            {
                Changed?.Invoke(); // Keep observing playback; never send a player command.
                return true;
            }
            _pinned = _sessions[next - 1];
            AttachSession();
            Changed?.Invoke();
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void TogglePlay() => SendCommand(session => session.TryTogglePlayPauseAsync());

    public void Next() => SendCommand(session => session.TrySkipNextAsync());

    public void Previous() => SendCommand(session => session.TrySkipPreviousAsync());

    public void Seek(double fraction) => Seek(fraction, CaptureContext());

    public async void Seek(double fraction, long context)
    {
        if (!double.IsFinite(fraction) || !IsCurrentContext(context)) return;
        fraction = Math.Clamp(fraction, 0, 1);
        Session? session = _session;
        if (session == null) return;
        int request = ++_seekVersion;
        bool Current() => IsCurrentContext(context) && request == _seekVersion && ReferenceEquals(session, _session);
        if (_noTimeline)
        {
            if (MpcTimeline.Supports(Source))
            {
                string source = Source, title = Title;
                if (!_mpcFound) return;
                MpcTimeline.Reading? actual = await Task.Run(() => Current() ? _mpc.Seek(source, title, fraction) : null);
                if (!Current() || title != Title || !_noTimeline) return;
                // Failed/late acknowledgement must not erase a valid timeline.
                // Refresh the actual player position rather than inventing one.
                actual ??= await Task.Run(() => _mpc.Read(source, title));
                if (!Current() || title != Title || !_noTimeline) return;
                ApplyMpcReading(actual);
                return;
            }
            string app = Source;
            if (!_barFound || !await Task.Run(() =>
            {
                lock (_barGate) return Current() && _bar.Read(app) != null && Current() && _bar.Seek(fraction);
            }) || !Current()) return;
            _position = _duration * Math.Clamp(fraction, 0, 1);
            _positionAt = DateTime.UtcNow;
            _barTime = TimeSpan.MinValue;
            _barPercent = -1;
            Changed?.Invoke();
            return;
        }
        if (_duration <= TimeSpan.Zero) return;
        try
        {
            var target = TimeSpan.FromTicks((long)(_duration.Ticks * Math.Clamp(fraction, 0, 1)));
            TimeSpan start = session.GetTimelineProperties().StartTime;
            if (await session.TryChangePlaybackPositionAsync((start + target).Ticks) && Current())
            {
                _position = target;
                _positionAt = DateTime.UtcNow;
                Changed?.Invoke();
            }
        }
        catch { }
    }

    async void SendCommand(Func<Session, IAsyncOperation<bool>> command)
    {
        try { if (CanControl && _session != null) await command(_session); }
        catch { }
    }

    bool ReadSessions()
    {
        Session[] live = _manager?.GetSessions().ToArray() ?? [];
        bool changed = live.Length != _sessions.Length || live.Any(s => !_sessions.Contains(s));
        foreach (Session removed in _sessions.Where(s => !live.Contains(s)))
            try { removed.PlaybackInfoChanged -= OnPlayback; } catch { }
        foreach (Session added in live.Where(s => !_sessions.Contains(s)))
            try { added.PlaybackInfoChanged += OnPlayback; } catch { }
        // Preserve page order when Windows changes its current session.
        _sessions = _sessions.Where(s => live.Contains(s))
            .Concat(live.Where(s => !_sessions.Contains(s))).ToArray();
        _nextSessionScan = Environment.TickCount64 + SessionScanMs;
        return changed;
    }

    static bool? IsPlayingSession(Session session)
    {
        try { return session.GetPlaybackInfo().PlaybackStatus == Status.Playing; }
        catch { return null; }
    }

    void AttachSession()
    {
        ReadSessions();
        ObservePlaybackActivity();
        if (_session != null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnProperties;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }
        }

        Session? previous = _session;
        _session = PickSession();
        _timelineStamp = default;
        bool switched = !ReferenceEquals(previous, _session);
        if (switched)
        {
            ClearSnapshot(_session != null);
            ReadPlayback();
            ReadTimeline();
        }

        if (_session != null)
        {
            try { _session.MediaPropertiesChanged += OnProperties; } catch { }
            try { _session.TimelinePropertiesChanged += OnTimeline; } catch { }
        }
        if (switched) Changed?.Invoke();
        _ = RefreshAsync();
    }

    Session? PickSession()
    {
        try
        {
            if (_pinned != null)
            {
                if (_sessions.FirstOrDefault(session => ReferenceEquals(session, _pinned)) is { } pinned) return pinned;
                _pinned = null;
                // Losing a source is not an explicit choice of Home. Keep following
                // playback; an explicitly selected Home remains independent.
            }

            Session? current = CurrentSession();
            if (_recentlyStarted != null && _activity.IsPlaying(_recentlyStarted)) return _recentlyStarted;
            if (current != null && _activity.IsPlaying(current)) return current;
            return _sessions.FirstOrDefault(_activity.IsPlaying)
                ?? (_session != null && _sessions.Contains(_session) ? _session
                    : current != null && _sessions.Contains(current) ? current : _sessions.FirstOrDefault());
        }
        catch
        {
            return null;
        }
    }

    Session? CurrentSession()
    {
        try { return _manager?.GetCurrentSession(); }
        catch { return null; }
    }

    void OnProperties(Session session, MediaPropertiesChangedEventArgs e) => Queue(() =>
    {
        if (!ReferenceEquals(session, _session)) return;
        ClearSnapshot(true); // Invalidate gestures and outstanding seeks before awaiting metadata.
        ReadPlayback(); ReadTimeline(); Changed?.Invoke();
        _ = RefreshAsync();
    });

    void OnPlayback(Session session, PlaybackInfoChangedEventArgs e)
    {
        if (_disposed) return;
        // Preserve queued pause/resume edges before the Dispatcher gets a chance
        // to read only their final state. Manual browsing drains them first.
        _pendingPlayback.Enqueue((session, IsPlayingSession(session)));
        Queue(() =>
        {
            if (ReconsiderSession() || !ReferenceEquals(session, _session)) return;
            ReadPlayback(); ReadTimeline(); Changed?.Invoke();
        });
    }

    void ObservePlaybackActivity()
    {
        Session? current = CurrentSession(), started = null;
        while (_pendingPlayback.TryDequeue(out var sample))
            if (_sessions.Contains(sample.Session) && _activity.Report(sample.Session, sample.Playing)) started = sample.Session;
        started = _activity.Observe(_sessions, IsPlayingSession, current) ?? started;
        if (started == null || !_activity.IsPlaying(started)) return;
        _recentlyStarted = started;
        // HomeSelected is intentionally independent: follow playback behind
        // Home, but do not navigate away from that explicit page choice.
        if (!ReferenceEquals(started, _session)) _pinned = null;
    }

    bool ReconsiderSession()
    {
        if (_manager == null) return false;
        ObservePlaybackActivity();
        if (ReferenceEquals(PickSession(), _session)) return false;
        AttachSession();
        return true;
    }

    void OnTimeline(Session session, TimelinePropertiesChangedEventArgs e) => Queue(() =>
    {
        if (!ReferenceEquals(session, _session)) return;
        ReadPlayback();
        ReadTimeline();
        Changed?.Invoke();
    });

    async Task RefreshAsync()
    {
        if (_disposed) return;
        Session? session = _session;
        int version = ++_refreshVersion;
        bool Current() => !_disposed && version == _refreshVersion && ReferenceEquals(session, _session);

        if (session == null)
        {
            ClearSnapshot(false);
            Changed?.Invoke();
            return;
        }

        try
        {
            if (!_ready && !IsLoading) { IsLoading = true; Changed?.Invoke(); }
            using var metadataStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            metadataStop.CancelAfter(MetadataTimeoutMs);
            var properties = await session.TryGetMediaPropertiesAsync().AsTask(metadataStop.Token).WaitAsync(metadataStop.Token);
            if (!Current()) return;

            string title = properties.Title ?? "";
            string artist = properties.Artist ?? "";
            bool sameTrack = _ready && title == _title && artist == _artist;
            if (!sameTrack) ClearSnapshot(true);
            _title = title;
            _artist = artist;
            ReadPlayback(); ReadTimeline();
            _ready = true;
            IsLoading = false;
            Changed?.Invoke(); // Publish only new-session fields; cover loading cannot hold up controls.
            if (properties.Thumbnail != null)
            {
                try
                {
                    using var coverStop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    coverStop.CancelAfter(MetadataTimeoutMs);
                    var (art, palette) = await LoadCoverAsync(properties.Thumbnail, coverStop.Token).WaitAsync(coverStop.Token);
                    if (!Current()) return;
                    if (art != null) { Art = art; Palette = palette; Changed?.Invoke(); }
                }
                catch (OperationCanceledException) when (_disposed) { }
                catch { } // Artwork is optional; a cover failure must not disable a valid player.
            }
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception ex)
        {
            if (!Current()) return;
            _ready = false; IsLoading = false;
            _nextMetadataRetry = Environment.TickCount64 + RetryMs;
            _log(ex);
            Changed?.Invoke();
        }
    }

    void ClearSnapshot(bool loading)
    {
        Interlocked.Increment(ref _contextVersion);
        _ready = false; IsLoading = loading;
        _title = _artist = "";
        _position = _duration = TimeSpan.Zero;
        _positionAt = DateTime.UtcNow;
        _timelineStamp = default; _rate = 1; _status = default;
        _noTimeline = false;
        ForgetBar();
        IsPlaying = false; Art = null; Palette = CoverPalette.Plain;
    }

    static async Task<(ImageSource? Art, Color[] Palette)> LoadCoverAsync(IRandomAccessStreamReference thumbnail, CancellationToken stop)
    {
        try
        {
            using var source = await thumbnail.OpenReadAsync().AsTask(stop);
            using var stream = source.AsStreamForRead();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, stop);
            buffer.Position = 0;
            return await Task.Run(() => CoverPalette.Decode(buffer), stop);
        }
        catch
        {
            return (null, CoverPalette.Plain);
        }
    }

    bool ReadPlayback()
    {
        Session? session = _session;
        long context = ContextVersion;
        if (session == null) return false;
        try
        {
            var info = session.GetPlaybackInfo();
            if (!ReferenceEquals(session, _session) || context != ContextVersion) return false;
            Status status = info.PlaybackStatus;
            bool playing = status == Status.Playing;
            double rate = info.PlaybackRate is double told && told > 0 ? told : 1;
            bool changed = playing != IsPlaying || rate != _rate || status != _status;
            if (playing != IsPlaying || rate != _rate)
            {
                _position = Position;
                _positionAt = DateTime.UtcNow;
                IsPlaying = playing;
                _rate = rate;
            }
            if (status != _status)
            {
                _status = status;
                if (IsLive) _bar.ResetRetry();
                else ForgetBar();
            }
            return changed;
        }
        catch
        {
            return false;
        }
    }

    bool ReadTimeline()
    {
        Session? session = _session;
        long context = ContextVersion;
        if (session == null) return false;
        try
        {
            var timeline = session.GetTimelineProperties();
            if (!ReferenceEquals(session, _session) || context != ContextVersion) return false;
            _noTimeline = timeline.LastUpdatedTime.Year < UnsetYear && timeline.EndTime <= timeline.StartTime;
            if (_noTimeline)
            {
                UpdateBarDuration();
                return false;
            }

            TimeSpan duration = timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero;
            bool resized = duration != _duration;
            _duration = duration;
            if (timeline.LastUpdatedTime == _timelineStamp) return resized;

            _timelineStamp = timeline.LastUpdatedTime;
            _position = timeline.Position - timeline.StartTime;
            DateTime updated = timeline.LastUpdatedTime.UtcDateTime;
            _positionAt = updated.Year < UnsetYear ? DateTime.UtcNow : updated;
            return true;
        }
        catch
        {
            return false;
        }
    }

    void PollPlayback()
    {
        bool sessionsChanged = Environment.TickCount64 >= _nextSessionScan && ReadSessions();
        if (ReconsiderSession()) return;
        if (ReadPlayback() | ReadTimeline() | sessionsChanged) Changed?.Invoke();
        if (_session != null && !_ready && !IsLoading && Environment.TickCount64 >= _nextMetadataRetry) _ = RefreshAsync();
    }

    async Task WatchPlaybackAsync(CancellationToken stop)
    {
        bool failed = false;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(failed ? RetryMs : IsPlaying ? PollPlayingMs : PollPausedMs, stop);
                try { PollPlayback(); failed = false; }
                catch (Exception ex) { if (!failed) _log(ex); failed = true; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    async Task WatchBarAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(IsPlaying ? PollPlayingMs : PollPausedMs, stop);
                Session? session = _session;
                if (session == null || !_noTimeline || !IsLive) continue;
                long context = CaptureContext();
                if (context < 0) continue;
                try
                {
                    string app = Source;
                    if (MpcTimeline.Supports(app))
                    {
                        string title = Title;
                        MpcTimeline.Reading? nativeReading = await Task.Run(() => _mpc.Read(app, title)).WaitAsync(stop);
                        if (IsCurrentContext(context) && title == Title && _noTimeline && IsLive) ApplyMpcReading(nativeReading);
                        continue;
                    }
                    if (!PlayerBar.Supports(app)) continue;
                    PlayerBar.Reading? reading = await Task.Run(() => { lock (_barGate) return _bar.Read(app); }).WaitAsync(stop);
                    if (IsCurrentContext(context) && _noTimeline && IsLive) ApplyBarReading(reading);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _log(ex);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    void ApplyBarReading(PlayerBar.Reading? reading)
    {
        bool hadTrack = HasTrack;
        (string title, TimeSpan duration) = (Title, _duration);

        if (reading is { } bar)
        {
            _barFound = true;
            if (bar.Text != _barText)
            {
                ForgetBar();
                _barFound = true;
                _barText = bar.Text;
                int dash = bar.Text.IndexOf(BarSeparator, StringComparison.Ordinal);
                (_barArtist, _barTitle) = dash > 0 ? (bar.Text[..dash], bar.Text[(dash + BarSeparator.Length)..]) : ("", bar.Text);
            }
            if (bar.Time != _barTime)
            {
                _barTime = _position = bar.Time;
                _positionAt = DateTime.UtcNow;
            }
            MeasureLength(bar.Percent);
        }
        else if (_barFound)
        {
            ForgetBar();
        }

        UpdateBarDuration();
        if (HasTrack != hadTrack || Title != title || _duration != duration) Changed?.Invoke();
    }

    void ApplyMpcReading(MpcTimeline.Reading? reading)
    {
        bool was = _mpcFound;
        TimeSpan previousDuration = _duration;
        bool jumped = reading is { } next && Math.Abs((next.Position - Position).TotalSeconds) > 1.5;
        _mpcFound = reading != null;
        if (reading is { } actual)
        {
            _mpcDuration = _duration = actual.Duration;
            // Repeated labels must not restart interpolation every 250 ms.
            if (_barTime != actual.Position || !was)
            {
                _barTime = _position = actual.Position;
                _positionAt = DateTime.UtcNow;
            }
        }
        else _mpcDuration = _duration = TimeSpan.Zero;
        if (was != _mpcFound || previousDuration != _duration || jumped) Changed?.Invoke();
    }

    void MeasureLength(int percent)
    {
        if (percent == _barPercent) return;

        bool stepped = IsPlaying && percent == _barPercent + 1;
        _barPercent = percent;
        if (!stepped || percent < MinPercentToMeasure) return;

        double length = RunningPosition.TotalSeconds * 100 / (percent - 0.5);
        if (_measuredLength == TimeSpan.Zero || Math.Abs(length - _measuredLength.TotalSeconds) > 50.0 / percent)
            _measuredLength = TimeSpan.FromSeconds(Math.Round(length));
    }

    void UpdateBarDuration()
    {
        if (_mpcFound) { _duration = _mpcDuration; return; }
        if (_assumedLength > TimeSpan.Zero && _barPercent >= 0
            && Math.Abs(RunningPosition / _assumedLength * 100 - _barPercent) > 1.5 + 150 / _assumedLength.TotalSeconds)
            _assumedLength = TimeSpan.Zero;
        _duration = !_barFound ? TimeSpan.Zero : _measuredLength > TimeSpan.Zero ? _measuredLength : _assumedLength;
    }

    void ForgetBar()
    {
        _mpcFound = false;
        _mpcDuration = TimeSpan.Zero;
        _barFound = false;
        _barText = _barTitle = _barArtist = "";
        _barTime = TimeSpan.MinValue;
        _barPercent = -1;
        _measuredLength = _assumedLength = TimeSpan.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _contextVersion);
        _lifetime.Cancel();
        if (_manager != null)
        {
            try { _manager.CurrentSessionChanged -= OnManagerChanged; } catch { }
            try { _manager.SessionsChanged -= OnManagerChanged; } catch { }
        }
        foreach (var session in _sessions)
        {
            try { session.PlaybackInfoChanged -= OnPlayback; } catch { }
            try { session.MediaPropertiesChanged -= OnProperties; } catch { }
            try { session.TimelinePropertiesChanged -= OnTimeline; } catch { }
        }
        _ready = false; IsLoading = false;
        while (_pendingPlayback.TryDequeue(out _)) { }
    }
}
