using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double SeekTrackWidth = 260;
    const double SeekHeight = 6, SeekDragHeight = 12, MinSeekHeight = 2;
    const double SeekSettledFraction = 0.02;
    const double SeekHoldSeconds = 1;
    const int NoSeconds = -1;
    const string NoTime = "–:––";

    readonly Spring _seekFill = new(0, 170, 26), _seekHeight = new(SeekHeight, 420, 26);
    readonly FrameLoop _seekLoop;
    bool _scrubbing;
    long _scrubContext = -1, _seekContext = -1;
    double _scrubFraction, _scrubHeldUntil;
    (int At, int Total) _seekLabel = (NoSeconds, NoSeconds);
    LyricsService.Line[] _seekLinesFrom = [];
    TimeSpan _seekLinesSpan;
    bool _seekLined;

    void StartSeekLoop()
    {
        _seekFill.Value = _seekFill.Velocity = 0;
        _seekLoop.Start();
    }

    bool AdvanceSeek(double dt)
    {
        SyncSeekContext();
        if (_view != View.MediaBig && !_scrubbing) return false;

        double now = _clock.Elapsed.TotalSeconds;
        TimeSpan duration = _media.Duration;
        bool known = duration.TotalSeconds >= 1;
        bool counted = known || _media.HasBarPosition;
        double played = _media.Progress;
        if (!_scrubbing && now < _scrubHeldUntil && Math.Abs(played - _scrubFraction) < SeekSettledFraction) _scrubHeldUntil = 0;
        double shown = _scrubbing || now < _scrubHeldUntil ? _scrubFraction : played;

        double track = SeekArea.ActualWidth > 0 ? SeekArea.ActualWidth : SeekTrackWidth;
        _seekFill.Target = track * shown;
        _seekHeight.Target = _scrubbing ? SeekDragHeight : SeekHeight;
        _seekFill.Advance(dt);
        _seekHeight.Advance(dt);

        SeekPlain.Thickness = SeekLines.Thickness = Math.Max(_seekHeight.Value, MinSeekHeight);
        SeekPlain.Fill = SeekLines.Fill = Math.Clamp(_seekFill.Value, 0, track);
        double? pointer = SeekArea.IsMouseOver || _scrubbing ? Mouse.GetPosition(SeekLines).X : null;
        SeekPlain.Advance(dt, pointer, Settings.SeekHover);
        SeekLines.Advance(dt, pointer, Settings.SeekHover);
        MarkLineStarts(duration);
        FillSungLine();
        AdvancePlayerLyrics(now, dt);

        TimeSpan at = known ? duration * shown : _media.Position;
        var label = counted ? ((int)at.TotalSeconds, known ? (int)duration.TotalSeconds : NoSeconds) : (NoSeconds, NoSeconds);
        if (label == _seekLabel) return true;

        _seekLabel = label;
        PosText.Text = counted ? FormatTime(at) : NoTime;
        RemText.Text = known ? "-" + FormatTime(duration - at) : NoTime;
        return true;
    }

    void MarkLineStarts(TimeSpan duration)
    {
        if (ReferenceEquals(_playerLines, _seekLinesFrom) && duration == _seekLinesSpan) return;

        _seekLinesFrom = _playerLines;
        _seekLinesSpan = duration;
        double seconds = duration.TotalSeconds;
        SeekLines.SetLines(seconds < 1 ? [] : _playerLines.Select(line => line.Time.TotalSeconds / seconds).ToArray(),
            _playerLines.Select(line => line.Text).ToArray());
        SyncSeekStyle(true);
    }

    void SyncSeekStyle(bool animate)
    {
        bool lined = Settings.LineBar && SeekLines.HasMarks;
        if (lined == _seekLined) return;

        _seekLined = lined;
        Duration time = Ms(animate ? 260 : 0);
        SeekLines.BeginAnimation(OpacityProperty, new DoubleAnimation(lined ? 1 : 0, time));
        SeekPlain.BeginAnimation(OpacityProperty, new DoubleAnimation(lined ? 0 : 1, time));
    }

    static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    double SeekFraction(MouseEventArgs e)
    {
        double x = e.GetPosition(SeekArea).X;
        if (_seekLined) x = SeekLines.Snap(x);
        return Math.Clamp(x / SeekArea.ActualWidth, 0, 1);
    }

    void Seek_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (SeekArea.ActualWidth <= 0 || !_media.Seekable || !_media.CanControl) return;

        _scrubContext = _media.CaptureContext();
        _scrubbing = true;
        _scrubFraction = SeekFraction(e);
        _seekFill.Tune(900, 60);
        PosText.Foreground = RemText.Foreground = Brushes.White;
        if (!SeekArea.CaptureMouse()) CancelScrub();
    }

    void Seek_MouseMove(object sender, MouseEventArgs e)
    {
        if (_scrubbing) _scrubFraction = SeekFraction(e);
    }

    void Seek_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (!_scrubbing) return;

        long context = _scrubContext;
        EndScrub();
        if (_media.IsCurrentContext(context))
        {
            _scrubHeldUntil = _clock.Elapsed.TotalSeconds + SeekHoldSeconds;
            _media.Seek(_scrubFraction, context);
        }
        SeekArea.ReleaseMouseCapture();
    }

    void Seek_LostMouseCapture(object sender, MouseEventArgs e)
    {
        CancelScrub();
    }

    void EndScrub()
    {
        _scrubbing = false;
        _seekFill.Tune(170, 26);
        PosText.Foreground = RemText.Foreground = _dim;
    }

    void SyncSeekContext()
    {
        long context = _media.ContextVersion;
        if (context == _seekContext) return;
        _seekContext = context;
        _scrubHeldUntil = 0;
        _seekLabel = (NoSeconds, NoSeconds);
        CancelScrub();
    }

    void CancelScrub()
    {
        if (!_scrubbing) return;
        EndScrub();
        _scrubHeldUntil = 0;
        SeekArea.ReleaseMouseCapture();
    }
}
