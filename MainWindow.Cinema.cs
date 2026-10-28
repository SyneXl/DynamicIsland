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

    bool ShouldDimCinema => _cinemaMode && _panel == Panel.None && _transientView == null
        && !_hovered && !_bubbleHovered && !_pressed && !_bubblePressed && !_ringing && !_pickingArea;

    void OnFullscreenVideoChanged() => CheckFullscreen();

    void Cinema_Click(object sender, RoutedEventArgs e)
    {
        Settings.Cinema = !Settings.Cinema;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void SyncCinemaAppearance()
    {
        bool dim = ShouldDimCinema;
        if (dim == _cinemaDimmed) return;
        _cinemaDimmed = dim;
        // Separate body/content alpha keeps the image visible through the pill.
        // No frame capture, scene-dependent tint, live blur or DWM dependency.
        var time = Ms(dim ? 300 : 180);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(dim ? 0.28 : 1, time));
        Body.BeginAnimation(OpacityProperty, new DoubleAnimation(dim ? 0.48 : 1, time));
        _timerTint.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(dim ? QuietTimerColor : (_urgent ? _red : _orange).Color, time));
        Eq.Quiet = dim;
        Eq.Fill = dim ? Brushes.White : _accentBrush;
        ArtSpot.Opacity = dim ? 0.75 : 1;
        SyncRim();
        SyncEq();
        SyncGlass(true);
        ApplyShape();
    }
}
