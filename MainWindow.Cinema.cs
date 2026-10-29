using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    static readonly Color QuietTimerColor = Color.FromRgb(0xCC, 0xD0, 0xD4);
    readonly FullscreenVideoService _fullscreenVideo;
    bool _cinemaMode, _cinemaDimmed;
    enum PassiveAppearance { Normal, Quiet, Cinema }
    PassiveAppearance _passiveAppearance;
    readonly QuietAppearanceState _quietAppearance = new();
    readonly DelayedAction _quietAppearanceTimeout;
    bool _quietAppearanceScheduled, _appearanceExiting;
    Func<bool, bool> _saveQuietAppearance = Settings.TrySetQuietWhenIdle;

    bool IsPassiveAppearance => _passiveAppearance != PassiveAppearance.Normal;
    bool IsQuietAppearance => _passiveAppearance == PassiveAppearance.Quiet;
    const double QuietBodyOpacity = .58, QuietContentOpacity = .9;

    bool ShouldDimCinema => _cinemaMode && _panel == Panel.None && _transientView == null
        && !_hovered && !_bubbleHovered && !_pressed && !_bubblePressed && !_ringing && !_pickingArea;

    bool ShouldDimQuiet => Settings.QuietWhenIdle && !_cinemaMode && _panel == Panel.None && _transientView == null
        && !_hovered && !_bubbleHovered && !_pressed && !_bubblePressed && _grab == Grab.None
        && !_ringing && !(_urgent && _countdown.IsRunning) && !_pickingArea && !_pickingFiles && !_dragOver
        && !_hiddenByFullscreen && !_sentAway && !IsKeyboardFocusWithin
        && TargetView() is View.Idle or View.Media or View.Timer or View.Record;

    void QuietAppearance_Click(object sender, RoutedEventArgs e)
    {
        _saveQuietAppearance(!Settings.QuietWhenIdle);
        RefreshLookPage();
        UpdateView();
    }

    void OnFullscreenVideoChanged() => CheckFullscreen();

    void Cinema_Click(object sender, RoutedEventArgs e)
    {
        Settings.Cinema = !Settings.Cinema;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void SyncCinemaAppearance()
    {
        if (_appearanceExiting) return;
        _cinemaDimmed = ShouldDimCinema;
        bool quiet = _quietAppearance.Update(ShouldDimQuiet, _clock.Elapsed.TotalSeconds);
        if (_quietAppearance.Pending && !_quietAppearanceScheduled)
        {
            _quietAppearanceScheduled = true;
            _quietAppearanceTimeout.Start(TimeSpan.FromSeconds(Math.Max(.001, _quietAppearance.Remaining)));
        }
        else if (!_quietAppearance.Pending && _quietAppearanceScheduled)
        {
            _quietAppearanceScheduled = false;
            _quietAppearanceTimeout.Cancel();
        }
        PassiveAppearance next = _cinemaDimmed ? PassiveAppearance.Cinema : quiet ? PassiveAppearance.Quiet : PassiveAppearance.Normal;
        if (next == _passiveAppearance) return;
        _passiveAppearance = next;
        bool dim = IsPassiveAppearance;
        // Separate body/content alpha keeps the image visible through the pill.
        // No frame capture, scene-dependent tint, live blur or DWM dependency.
        var time = Ms(_cinemaDimmed ? 300 : quiet ? 240 : 180);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(_cinemaDimmed ? .28 : 1, time));
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(_cinemaDimmed ? .48 : quiet ? QuietBodyOpacity : 1, time));
        IslandContent.BeginAnimation(OpacityProperty, new DoubleAnimation(quiet ? QuietContentOpacity : 1, time));
        _timerTint.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(dim ? QuietTimerColor : (_urgent ? _red : _orange).Color, time));
        SyncRecordTint(time);
        BubbleUpdateIcon.Fill = quiet ? Brushes.White : _indigo;
        Eq.KeepStyleWhenQuiet = quiet;
        Eq.Quiet = dim;
        Eq.Fill = dim ? Brushes.White : _accentBrush;
        ArtSpot.BeginAnimation(OpacityProperty, new DoubleAnimation(_cinemaDimmed ? .75 : quiet ? .72 : 1, time));
        if (quiet)
            foreach (var dot in new[] { RecordDot, BubbleRecordDot }) dot.BeginAnimation(OpacityProperty, null);
        SyncRim();
        SyncEq();
        SyncGlass(true);
        ApplyShape();
    }
}
