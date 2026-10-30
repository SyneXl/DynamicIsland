using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

// The same restrained outline is used during the drag, processing and result.
// It never receives input and is never part of a captured/copied bitmap.
sealed class SelectionFrame : FrameworkElement
{
    public static readonly DependencyProperty IsProcessingProperty = DependencyProperty.Register(nameof(IsProcessing), typeof(bool), typeof(SelectionFrame),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, (d, _) => ((SelectionFrame)d).SyncMotion()));
    static readonly DependencyProperty PhaseProperty = DependencyProperty.Register("Phase", typeof(double), typeof(SelectionFrame),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    PathGeometry? _outline;
    Size _outlineSize;
    public bool IsProcessing { get => (bool)GetValue(IsProcessingProperty); set => SetValue(IsProcessingProperty, value); }
    public SelectionFrame()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => SyncMotion();
        Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
    }
    void SyncMotion()
    {
        BeginAnimation(PhaseProperty, null);
        if (IsLoaded && IsProcessing && SystemParameters.ClientAreaAnimation)
            BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(2.2)) { RepeatBehavior = RepeatBehavior.Forever });
    }
    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 3 || h < 3) return;
        Rect box = new(1.5, 1.5, w - 3, h - 3);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), 2.5), box, 5, 5);
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(195, 255, 255, 255)), 1), box, 5, 5);
        double length = Math.Min(14, Math.Min(w, h) / 3), inset = 1.5;
        var corners = new StreamGeometry();
        using (var g = corners.Open())
            foreach (var (x, y, dx, dy) in new[] { (inset, inset, 1, 1), (w - inset, inset, -1, 1), (inset, h - inset, 1, -1), (w - inset, h - inset, -1, -1) })
            {
                g.BeginFigure(new(x, y + dy * length), false, false);
                g.LineTo(new Point(x, y + dy * 5), true, false);
                g.QuadraticBezierTo(new Point(x, y), new Point(x + dx * 5, y), true, false);
                g.LineTo(new Point(x + dx * length, y), true, false);
            }
        dc.DrawGeometry(null, new Pen(IsProcessing ? new SolidColorBrush(Color.FromRgb(181, 173, 255)) : Brushes.White, 1.7), corners);
        if (!IsProcessing) return;
        if (_outline == null || _outlineSize != RenderSize)
        { _outline = new RectangleGeometry(box, 5, 5).GetFlattenedPathGeometry(); _outlineSize = RenderSize; }
        double phase = (double)GetValue(PhaseProperty), span = Math.Min(.12, 90 / (2 * (w + h)));
        var glint = new StreamGeometry();
        using (var g = glint.Open())
            for (int i = 0; i <= 18; i++)
            {
                _outline.GetPointAtFractionLength((phase + span * i / 18) % 1, out Point p, out _);
                if (i == 0) g.BeginFigure(p, false, false); else g.LineTo(p, true, false);
            }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(208, 202, 255)), 2), glint);
    }
}
