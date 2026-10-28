using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double CompactMediaWidth = 210;
    const double MaxCompactMediaWidth = 440;
    const double TrackNameWidth = 300;
    const double LyricInset = 77;
    const double LyricFadeEdge = 8;
    const double LyricScrollSpeed = 36;
    const double LyricGapSeconds = 4;
    const double PlayerHeight = 176;
    const double PlayerLyricsHeight = 74;
    const double PlayerLineGap = 4;
    const double UnsungLineOpacity = 0.4;
    const double UnsungLineScale = 0.94;
    const double UnsungLineBlur = 1.5;
    const double UnsungPartOpacity = 0.6;
    const double MaxLineFillSeconds = 8, MinLineFillSeconds = 0.3;
    const double LineScrollStagger = 0.04;
    const int VisibleLineRange = 4, BlurredLineRange = 2;
    static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(200);

    sealed class PlayerLine(Lyric row, double middle)
    {
        public Lyric Row { get; } = row;
        public double Middle { get; } = middle;
        public Spring Y { get; } = new(0, 170, 22);
        public double DueAt { get; set; }
    }

    readonly BitmapCache _lyricCache = new();
    readonly FrameLoop _partLoop;
    LyricsService.Line[] _lyricLines = [];
    int _lyricIndex = -1;
    string _lyricTitle = "";
    bool _lyricShowsTitle;
    double _compactMediaWidth = CompactMediaWidth;
    CompactLyric _lyricBlock;
    LyricsService.Line[] _playerLines = [];
    PlayerLine[] _playerRows = [];
    double _playerScroll;
    bool _playerScrolling;
    int _playerIndex = -1;
    bool _playerHasLyricRoom;
    bool _playerLyricsPending;

    TimeSpan LyricTime => _media.Position + LyricLead;

    void TrackLyrics() => _lyrics.Track(Settings.Lyrics && _media.HasTrack ? _media.Title : "", _media.Artist, Settings.LyricSources);

    void OnLyricsChanged()
    {
        _media.AssumeDuration(_lyrics.UsualLength);
        UpdateLyric();
    }

    void LyricBox_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double width = Math.Max(e.NewSize.Width, 2 * LyricFadeEdge);
        LyricMask.EndPoint = new Point(width, 0);
        LyricMaskIn.Offset = LyricFadeEdge / width;
        LyricMaskOut.Offset = 1 - LyricFadeEdge / width;
    }

    static int LineAt(LyricsService.Line[] lines, TimeSpan time)
    {
        int index = lines.Length - 1;
        while (index >= 0 && lines[index].Time > time) index--;
        return index;
    }

    void UpdateLyric(bool snap = false)
    {
        if (_view == View.MediaBig) UpdatePlayerLyrics(snap);
        if (_view != View.Media) return;

        LyricsService.Line[] lines = _lyrics.LinesFor(_media.Duration);
        TimeSpan at = LyricTime;
        int index = LineAt(lines, at);

        string title = _media.IsLoading ? "Загрузка…" : _media.HasTrack ? _media.Name : "";
        string text = index < 0 ? "" : lines[index].Text;
        TimeSpan end = index + 1 < lines.Length ? lines[index + 1].Time : _media.Duration;
        double seconds = (end - at).TotalSeconds;
        bool named = text.Length == 0 && (index < 0 || seconds >= LyricGapSeconds);
        if (ReferenceEquals(lines, _lyricLines) && index == _lyricIndex && (!named || title == _lyricTitle)) return;

        _lyricLines = lines;
        _lyricIndex = index;
        _lyricTitle = title;
        ShowLyric(named ? title : text, seconds, snap, named);
    }

    void ShowLyric(string text, double seconds, bool snap, bool named)
    {
        CompactLyric old = _lyricBlock;
        if (text == old.Text && (text.Length == 0 || (named && _lyricShowsTitle))) return;
        CompactLyric next = _lyricBlock = old == LyricA ? LyricB : LyricA;
        _lyricShowsTitle = named;

        old.Leave(snap);
        next.Show(text, named ? _dim : Brushes.White,
            named ? TrackNameWidth - LyricInset - 2 * LyricFadeEdge : double.PositiveInfinity, Settings.LyricChange);
        next.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = next.DesiredSize.Width;
        Canvas.SetTop(next, Math.Round((LyricBox.Height - next.DesiredSize.Height) / 2));

        _compactMediaWidth = text.Length == 0
            ? CompactMediaWidth
            : Math.Clamp(width + 2 * LyricFadeEdge + LyricInset, CompactMediaWidth, MaxCompactMediaWidth);
        next.IslandWidth = _compactMediaWidth;
        CentreLyrics();
        double box = _compactMediaWidth - LyricInset;
        double overflow = width - (box - 2 * LyricFadeEdge);
        if (!snap) _width.Tune(280, 30);
        UpdateTargets();

        next.Offset.X = overflow > 0 ? LyricFadeEdge : (box - width) / 2;
        next.Offset.BeginAnimation(TranslateTransform.XProperty, overflow > 0 ? CreateLyricScroll(overflow, seconds) : null);
        next.Enter();
        StirLyrics();
    }

    void CentreLyrics()
    {
        MediaView.UpdateLayout();
        double boxLeft = VisualTreeHelper.GetOffset(MediaView).X + VisualTreeHelper.GetOffset(LyricBox).X;
        LyricA.Centring.X = (HostWidth - LyricA.IslandWidth) / 2 + LyricBox.Margin.Left - boxLeft;
        LyricB.Centring.X = (HostWidth - LyricB.IslandWidth) / 2 + LyricBox.Margin.Left - boxLeft;
    }

    static DoubleAnimationUsingKeyFrames CreateLyricScroll(double overflow, double seconds)
    {
        double hold = Math.Clamp(seconds * 0.2, 0, 0.6);
        double run = Math.Min(overflow / LyricScrollSpeed, Math.Max(seconds - hold - 0.5, 0.6));
        return Delayed(LyricFadeEdge, LyricFadeEdge - overflow, TimeSpan.FromSeconds(hold), TimeSpan.FromSeconds(run),
            new SineEase { EasingMode = EasingMode.EaseInOut });
    }

    void UpdatePlayerLyrics(bool snap)
    {
        LyricsService.Line[] lines = _lyrics.LinesFor(_media.Duration);
        if (!ReferenceEquals(lines, _playerLines))
        {
            _playerLines = lines;
            _playerIndex = -1;
            BuildPlayerLyrics(lines);
            if (!snap) PlayerLyricLines.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(300)));
            snap = true;
        }

        bool room = lines.Length > 0 || (_playerHasLyricRoom && _lyrics.IsPending);
        if (room != _playerHasLyricRoom)
        {
            _playerHasLyricRoom = room;
            _height.Tune(280, 30);
            UpdateTargets();
        }
        ShowLyricsPlaceholder(room && lines.Length == 0);
        if (lines.Length == 0) return;

        int index = LineAt(lines, LyricTime);
        if (index == _playerIndex && !snap) return;

        Duration fade = Ms(snap ? 0 : 300);
        int was = _playerIndex;
        _playerIndex = index;
        for (int i = 0; i < _playerRows.Length; i++)
        {
            Lyric row = _playerRows[i].Row;
            if (i == index || i == was) HighlightLine(row, i == index, fade);
            if (i != index && Math.Abs(i - index) <= BlurredLineRange && Settings.LyricEffects) BlurLine(row, UnsungLineBlur, fade);
            else if (i == index) BlurLine(row, 0, fade);
            else row.Effect = null;
        }
        FillSungLine();

        double middle = _playerRows[Math.Max(index, 0)].Middle;
        ScrollPlayerLyrics(PlayerLyricBox.Height / 2 - middle, Math.Max(was, 0), Math.Max(index, 0), snap);
    }

    void ScrollPlayerLyrics(double to, int was, int index, bool snap)
    {
        bool up = to <= _playerScroll;
        int lead = index + (up ? -1 : 1);
        double now = _clock.Elapsed.TotalSeconds;
        _playerScroll = to;
        StirLyrics();
        for (int i = 0; i < _playerRows.Length; i++)
        {
            PlayerLine line = _playerRows[i];
            if (snap || (Math.Abs(i - index) > VisibleLineRange && Math.Abs(i - was) > VisibleLineRange))
            {
                line.Y.Snap(to);
                line.DueAt = 0;
                LineOffsetOf(line.Row).Y = to;
                continue;
            }
            int behind = Math.Clamp(up ? i - lead : lead - i, 0, VisibleLineRange + 1);
            line.DueAt = now + behind * LineScrollStagger;
            _playerScrolling = true;
        }
    }

    void AdvancePlayerLyrics(double now, double dt)
    {
        if (!_playerScrolling) return;
        bool rolling = false;
        foreach (PlayerLine line in _playerRows)
        {
            Spring y = line.Y;
            if (line.DueAt > 0)
            {
                if (now < line.DueAt) rolling = true;
                else
                {
                    line.DueAt = 0;
                    y.Target = _playerScroll;
                }
            }
            if (y.Value == y.Target && y.Velocity == 0) continue;
            rolling |= y.Advance(dt);
            LineOffsetOf(line.Row).Y = y.Value;
        }
        _playerScrolling = rolling;
    }

    void StirLyrics() => _partLoop.Start();

    bool AdvanceLyricParting(double dt)
    {
        bool parting = _hovered && Settings.LyricParting;
        bool overPlayer = parting && _view == View.MediaBig, overCompact = parting && _view == View.Media;
        bool moving = overPlayer && _playerScrolling;
        int sung = Math.Max(_playerIndex, 0);
        for (int i = 0; i < _playerRows.Length; i++)
        {
            if (Math.Abs(i - sung) > VisibleLineRange) continue;
            Lyric row = _playerRows[i].Row;
            moving |= row.Part(overPlayer ? Mouse.GetPosition(row) : null, dt);
        }
        foreach (CompactLyric line in new[] { LyricA, LyricB })
            moving |= line.Part(overCompact && line == _lyricBlock ? Mouse.GetPosition(line) : null, dt);
        return moving;
    }

    static ScaleTransform LineScaleOf(Lyric row) => (ScaleTransform)((TransformGroup)row.RenderTransform).Children[0];

    static TranslateTransform LineOffsetOf(Lyric row) => (TranslateTransform)((TransformGroup)row.RenderTransform).Children[1];

    void ShowLyricsPlaceholder(bool on)
    {
        if (on == _playerLyricsPending) return;
        _playerLyricsPending = on;
        if (on) PlayerLyricWait.SetRunning(true);
        var fade = new DoubleAnimation(on ? 1 : 0, Ms(on ? 300 : 200));
        if (!on)
        {
            fade.Completed += (_, _) =>
            {
                if (!_playerLyricsPending) PlayerLyricWait.SetRunning(false);
            };
        }
        PlayerLyricWait.BeginAnimation(OpacityProperty, fade);
    }

    void RebuildPlayerLyrics()
    {
        _playerIndex = -1;
        BuildPlayerLyrics(_playerLines);
        if (_view == View.MediaBig) UpdatePlayerLyrics(true);
    }

    void BuildPlayerLyrics(LyricsService.Line[] lines)
    {
        PlayerLyricLines.Children.Clear();
        _playerRows = new PlayerLine[lines.Length];
        _playerScrolling = false;

        double width = PlayerLyricBox.Width, top = 0, size = Settings.LyricEffects ? UnsungLineScale : 1;
        for (int i = 0; i < lines.Length; i++)
        {
            var row = new Lyric(lines[i].Text)
            {
                Width = width,
                Opacity = UnsungLineOpacity,
                RenderTransformOrigin = new Point(0, 0.5),
                RenderTransform = new TransformGroup { Children = { new ScaleTransform(size, size), new TranslateTransform() } },
            };
            PlayerLyricLines.Children.Add(row);
            row.Measure(new Size(width, double.PositiveInfinity));
            Canvas.SetTop(row, top);
            _playerRows[i] = new PlayerLine(row, top + row.DesiredSize.Height / 2);
            top += row.DesiredSize.Height + PlayerLineGap;
        }
    }

    void HighlightLine(Lyric row, bool sung, Duration time)
    {
        bool effects = Settings.LyricEffects;
        row.BeginAnimation(OpacityProperty, new DoubleAnimation(sung ? 1 : UnsungLineOpacity, time));
        row.BeginAnimation(Lyric.UnsungProperty, new DoubleAnimation(sung && effects ? UnsungPartOpacity : 1, time));
        LineScaleOf(row).AnimateScale(new DoubleAnimation(sung || !effects ? 1 : UnsungLineScale, time)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    void BlurLine(Lyric row, double radius, Duration time)
    {
        if (row.Effect is not BlurEffect blur)
        {
            if (radius == 0) return;
            row.Effect = blur = new BlurEffect { Radius = 0 };
        }
        var change = new DoubleAnimation(radius, time);
        change.Completed += (_, _) =>
        {
            if (ReferenceEquals(row.Effect, blur) && blur.Radius < 0.01) row.Effect = null;
        };
        blur.BeginAnimation(BlurEffect.RadiusProperty, change);
    }

    void FillSungLine()
    {
        int index = _playerIndex;
        if (index < 0 || index >= _playerRows.Length) return;

        TimeSpan start = _playerLines[index].Time;
        TimeSpan end = index + 1 < _playerLines.Length ? _playerLines[index + 1].Time : _media.Duration;
        double longest = Lyric.IsWordless(_playerLines[index].Text) ? double.MaxValue : MaxLineFillSeconds;
        double seconds = Math.Clamp((end - start).TotalSeconds, MinLineFillSeconds, longest);
        _playerRows[index].Row.Progress = Math.Clamp((LyricTime - start).TotalSeconds / seconds, 0, 1);
    }
}
