using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace DynamicIsland;

public sealed class Toggle : FrameworkElement
{
    const double KnobInset = 2, KnobAspect = 26.0 / 18;
    const double PressedWider = 0.2, PressedTaller = 0.32;
    const double DragThreshold = 3, DragOverreach = 0.2;
    const double StretchSpeed = 9, StretchWider = 0.32, StretchFlatter = 0.1;
    const double LensZoom = 0.22, LensZoomTaller = 1.08;
    const double GlassClearing = 0.9, GlassShown = 0.01;
    const double RimThickness = 0.8;
    const double SettledShare = 0.18;
    const double ShadowLayers = 3, ShadowSpread = 0.6, PressedShadowSpread = 1.5;
    const double ShadowDrop = 0.6, PressedShadowDrop = 1.0, ShadowAlpha = 0.1, PressedShadowAlpha = 0.05;
    static readonly TimeSpan ShortestPress = TimeSpan.FromMilliseconds(140);

    static readonly Color OffColor = Color.FromRgb(0x39, 0x39, 0x3D);
    static readonly Color OnColor = Color.FromRgb(0x30, 0xD1, 0x58);
    static readonly DependencyPropertyDescriptor RowPressed =
        DependencyPropertyDescriptor.FromProperty(ButtonBase.IsPressedProperty, typeof(ButtonBase));

    readonly Spring _share = new(0, 520, 30);
    readonly Spring _press = new(0, 900, 38);
    readonly FrameLoop _loop;
    ButtonBase? _row;
    bool _on, _down, _dragging, _held;
    long _pressedAt;
    double _downX, _downShare;

    public Toggle()
    {
        _loop = new FrameLoop(Advance);
        Loaded += (_, _) => AttachToRow();
    }

    public void Set(bool on, bool animate)
    {
        _on = on;
        if (_dragging) return;
        if (animate)
        {
            _share.Target = on ? 1 : 0;
            _loop.Start();
        }
        else
        {
            _share.Snap(on ? 1 : 0);
            InvalidateVisual();
        }
    }

    void AttachToRow()
    {
        if (_row != null) return;
        for (DependencyObject? node = VisualTreeHelper.GetParent(this); node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is not ButtonBase row) continue;
            _row = row;
            RowPressed.AddValueChanged(row, OnRowPressedChanged);
            row.PreviewMouseLeftButtonDown += OnRowDown;
            row.PreviewMouseMove += OnRowMove;
            row.PreviewMouseLeftButtonUp += OnRowUp;
            row.LostMouseCapture += OnRowLostCapture;
            return;
        }
    }

    void OnRowPressedChanged(object? sender, EventArgs e)
    {
        if (_row!.IsPressed) Press();
        else if (!_dragging) _held = false;
    }

    void OnRowDown(object sender, MouseButtonEventArgs e)
    {
        _down = true;
        _dragging = false;
        _downX = e.GetPosition(this).X;
        _downShare = _share.Value;
    }

    void OnRowMove(object sender, MouseEventArgs e)
    {
        if (!_down || e.LeftButton != MouseButtonState.Pressed) return;

        double dx = e.GetPosition(this).X - _downX;
        if (!_dragging)
        {
            if (Math.Abs(dx) < DragThreshold) return;
            _dragging = true;
            _held = true;
            _share.Tune(1400, 70);
        }

        _share.Target = Overreach(_downShare + dx / Travel());
        _loop.Start();
    }

    void OnRowUp(object sender, MouseButtonEventArgs e)
    {
        _down = false;
        if (!_dragging) return;

        e.Handled = true;
        bool on = _share.Target > 0.5;
        EndDrag();
        _row!.ReleaseMouseCapture();
        if (on != _on) _row.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, _row));
        Set(_on, true);
    }

    void OnRowLostCapture(object sender, MouseEventArgs e)
    {
        _down = false;
        if (!_dragging) return;
        EndDrag();
        Set(_on, true);
    }

    void EndDrag()
    {
        _dragging = false;
        _held = false;
        _share.Tune(520, 30);
    }

    double Travel() => Math.Max(1, ActualWidth - KnobInset * 2 - (ActualHeight - KnobInset * 2) * KnobAspect);

    static double Overreach(double share) =>
        share < 0 ? -Resist(-share) : share > 1 ? 1 + Resist(share - 1) : share;

    static double Resist(double beyond) => beyond / (1 + beyond / DragOverreach);

    void Press()
    {
        _held = true;
        _pressedAt = Stopwatch.GetTimestamp();
        _press.Tune(900, 38);
        _press.Target = 1;
        _loop.Start();
    }

    void Release()
    {
        _press.Tune(420, 17);
        _press.Target = 0;
    }

    bool Advance(double dt)
    {
        bool settled = Math.Abs(_share.Value - _share.Target) < SettledShare;
        if (!_held && _press.Target == 1 && settled && Stopwatch.GetElapsedTime(_pressedAt) >= ShortestPress) Release();

        bool moving = _share.Advance(dt) | _press.Advance(dt);
        InvalidateVisual();
        return moving || _press.Target == 1;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double share = Math.Clamp(_share.Value, 0, 1), press = Math.Max(0, _press.Value);
        double stretch = Math.Min(1, Math.Abs(_share.Velocity) / StretchSpeed);
        double restHeight = h - KnobInset * 2, restWidth = restHeight * KnobAspect;
        double knobWidth = restWidth * (1 + PressedWider * press) * (1 + StretchWider * stretch);
        double knobHeight = restHeight * (1 + PressedTaller * press) * (1 - StretchFlatter * stretch);
        double cx = KnobInset + restWidth / 2 + (w - KnobInset * 2 - restWidth) * _share.Value, cy = h / 2;

        var trackBrush = new SolidColorBrush(Mix(OffColor, OnColor, share));
        var track = new RectangleGeometry(new Rect(0, 0, w, h), h / 2, h / 2);
        var knobRect = new Rect(cx - knobWidth / 2, cy - knobHeight / 2, knobWidth, knobHeight);
        var knob = new RectangleGeometry(knobRect, knobHeight / 2, knobHeight / 2);
        bool glass = press > GlassShown;

        dc.DrawGeometry(trackBrush, null, glass ? Geometry.Combine(track, knob, GeometryCombineMode.Exclude, null) : track);
        DrawShadow(dc, knobRect, knob, press);

        if (!glass)
        {
            dc.DrawGeometry(Brushes.White, null, knob);
            return;
        }

        dc.PushClip(knob);
        double zoom = 1 + LensZoom * press;
        dc.PushTransform(new ScaleTransform(zoom, zoom * LensZoomTaller, cx, cy));
        dc.DrawGeometry(trackBrush, null, track);
        dc.Pop();
        dc.DrawRectangle(new SolidColorBrush(White(1 - GlassClearing * press)), null, knobRect);
        dc.DrawRectangle(Vertical((0, 0.35 * press), (0.45, 0), (1, 0.12 * press)), null, knobRect);
        dc.Pop();

        var rim = new Pen(Vertical((0, 0.85 * press), (0.5, 0.25 * press), (1, 0.6 * press)), RimThickness);
        var rimRect = Rect.Inflate(knobRect, -RimThickness / 2, -RimThickness / 2);
        dc.DrawRoundedRectangle(null, rim, rimRect, rimRect.Height / 2, rimRect.Height / 2);
    }

    static void DrawShadow(DrawingContext dc, Rect knobRect, Geometry knob, double press)
    {
        double spread = ShadowSpread + PressedShadowSpread * press, drop = ShadowDrop + PressedShadowDrop * press;
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * (ShadowAlpha + PressedShadowAlpha * press)), 0, 0, 0));
        for (int layer = 1; layer <= ShadowLayers; layer++)
        {
            Rect rect = Rect.Inflate(knobRect, spread * layer, spread * layer);
            rect.Offset(0, drop);
            var halo = new RectangleGeometry(rect, rect.Height / 2, rect.Height / 2);
            dc.DrawGeometry(brush, null, Geometry.Combine(halo, knob, GeometryCombineMode.Exclude, null));
        }
    }

    static LinearGradientBrush Vertical(params (double Offset, double Alpha)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        foreach ((double offset, double alpha) in stops) brush.GradientStops.Add(new GradientStop(White(alpha), offset));
        return brush;
    }

    static Color White(double alpha) => Color.FromArgb((byte)Math.Round(255 * Math.Clamp(alpha, 0, 1)), 255, 255, 255);

    static Color Mix(Color from, Color to, double share) => Color.FromRgb(Mix(from.R, to.R, share), Mix(from.G, to.G, share), Mix(from.B, to.B, share));

    static byte Mix(byte from, byte to, double share) => (byte)Math.Round(from + (to - from) * share);
}
