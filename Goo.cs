using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public sealed class Goo : FrameworkElement
{
    const double RimWidth = 1;
    const double TearGap = 7.5;
    const double NeckGrip = 0.5;
    const double NeckHandle = 2.4;
    const double Tolerance = 0.02;
    const byte TintedAlpha = 0x8C;
    const double HazeOpacity = 0.14;
    const byte GlassAlpha = 0x99;

    static readonly Color PlainRim = Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF);
    static readonly double[] HazeReach = [2, 3, 4];
    static readonly (double At, double Level)[] FlashFrames = [(0, 0), (0.04, 1), (0.13, 0.3), (0.19, 0.9), (0.5, 0), (1, 0)];

    readonly SolidColorBrush _rim = new(PlainRim), _fill = new(Colors.Black);
    readonly Light _flash = new();
    readonly Pen _edge;

    Rect _pill = Rect.Empty, _bubble = Rect.Empty;
    double _radius;

    public Goo() => _edge = new Pen(_rim, 2 * RimWidth) { LineJoin = PenLineJoin.Round };

    public void Tint(Color? color, Duration time)
    {
        Color to = color is { } c
            ? c.A == 0 ? Colors.Transparent : Color.FromArgb(TintedAlpha, c.R, c.G, c.B)
            : PlainRim;
        _rim.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, time));
    }

    public (Rect Box, double Radius) PillInside => Inside(_pill, _radius);

    public (Rect Box, double Radius) BubbleInside => Inside(_bubble, _bubble.Height / 2);

    public void Thin(bool glass, Duration time, Action thinned)
    {
        var fade = new ColorAnimation(Color.FromArgb(glass ? GlassAlpha : byte.MaxValue, 0, 0, 0), time);
        fade.Completed += (_, _) => thinned();
        _fill.BeginAnimation(SolidColorBrush.ColorProperty, fade);
    }

    public void StartFlashing(Color color, TimeSpan round)
    {
        _flash.Edge.Color = _flash.Mist.Color = color;
        _flash.Edge.BeginAnimation(Brush.OpacityProperty, Flashes(1));
        _flash.Mist.BeginAnimation(Brush.OpacityProperty, Flashes(HazeOpacity));

        DoubleAnimationUsingKeyFrames Flashes(double share)
        {
            var flashes = new DoubleAnimationUsingKeyFrames { Duration = round, RepeatBehavior = RepeatBehavior.Forever, FillBehavior = FillBehavior.Stop };
            foreach ((double at, double level) in FlashFrames)
                flashes.KeyFrames.Add(new LinearDoubleKeyFrame(level * share, KeyTime.FromPercent(at)));
            return flashes;
        }
    }

    public void StopFlashing()
    {
        _flash.Edge.BeginAnimation(Brush.OpacityProperty, null);
        _flash.Mist.BeginAnimation(Brush.OpacityProperty, null);
    }

    public void SetShape(Rect pill, double radius, Rect bubble)
    {
        if (pill == _pill && radius == _radius && bubble == _bubble) return;
        _pill = pill;
        _radius = radius;
        _bubble = bubble;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_pill.IsEmpty) return;
        Geometry pill = Outline(_pill, _radius);
        if (_bubble.IsEmpty)
        {
            DrawBody(dc, pill);
            return;
        }

        Geometry bubble = Outline(_bubble, _bubble.Height / 2);
        Geometry? neck = CreateNeck();
        if (neck == null && !_pill.IntersectsWith(_bubble))
        {
            DrawBody(dc, pill);
            DrawBody(dc, bubble);
            return;
        }

        Geometry body = Union(pill, bubble);
        DrawBody(dc, neck != null ? Union(body, neck) : body);
    }

    public static Geometry Union(Geometry a, Geometry b) =>
        Geometry.Combine(a, b, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);

    void DrawBody(DrawingContext dc, Geometry body)
    {
        Rect around = body.Bounds;
        around.Inflate(2 * RimWidth, 2 * RimWidth);
        var outside = new GeometryGroup { FillRule = FillRule.EvenOdd };
        outside.Children.Add(new RectangleGeometry(around));
        outside.Children.Add(body);

        dc.PushClip(outside);
        dc.DrawGeometry(null, _edge, body);
        dc.DrawGeometry(null, _flash.Line, body);
        dc.Pop();
        dc.DrawGeometry(_fill, null, body);
        foreach (Pen mist in _flash.Mists) dc.DrawGeometry(null, mist, body);
    }

    sealed class Light
    {
        public readonly SolidColorBrush Edge = new(Colors.White) { Opacity = 0 }, Mist = new(Colors.White) { Opacity = 0 };
        public readonly Pen Line;
        public readonly Pen[] Mists;

        public Light()
        {
            Line = new Pen(Edge, 2 * RimWidth) { LineJoin = PenLineJoin.Round };
            Mists = HazeReach.Select(reach => new Pen(Mist, 2 * reach) { LineJoin = PenLineJoin.Round }).ToArray();
        }
    }

    static Geometry Outline(Rect rect, double radius)
    {
        (Rect box, double r) = Inside(rect, radius);
        return Squircle.Of(box, r);
    }

    static (Rect Box, double Radius) Inside(Rect rect, double radius)
    {
        if (rect.IsEmpty) return (rect, 0);
        rect.Inflate(-Math.Min(RimWidth, rect.Width / 2), -Math.Min(RimWidth, rect.Height / 2));
        return (rect, Math.Max(radius - RimWidth, 0));
    }

    Geometry? CreateNeck()
    {
        var c1 = new Point(_pill.Right - _radius, _pill.Top + _radius);
        var c2 = new Point(_bubble.Left + _bubble.Height / 2, _bubble.Top + _bubble.Height / 2);
        return c2.X > c1.X ? Neck(c1, _radius - RimWidth, c2, _bubble.Height / 2 - RimWidth, TearGap) : null;
    }

    public static Geometry? Neck(Point c1, double r1, Point c2, double r2, double tearGap)
    {
        Vector between = c2 - c1;
        double d = between.Length, gap = d - r1 - r2;
        if (r1 <= 0 || r2 <= 0 || d <= Math.Abs(r1 - r2) || gap >= tearGap) return null;

        double u1 = 0, u2 = 0;
        if (gap < 0)
        {
            u1 = Math.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = Math.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }

        double grip = NeckGrip * (1 - Math.Clamp(gap / tearGap, 0, 1));
        double axis = Math.Atan2(between.Y, between.X), wide = Math.Acos((r1 - r2) / d);
        double a1 = axis + u1 + (wide - u1) * grip, a2 = axis - u1 - (wide - u1) * grip;
        double a3 = axis + Math.PI - u2 - (Math.PI - u2 - wide) * grip, a4 = axis - Math.PI + u2 + (Math.PI - u2 - wide) * grip;
        Point p1 = PointOnCircle(c1, a1, r1), p2 = PointOnCircle(c1, a2, r1), p3 = PointOnCircle(c2, a3, r2), p4 = PointOnCircle(c2, a4, r2);

        double reach = Math.Min(grip * NeckHandle, (p1 - p3).Length / (r1 + r2)) * Math.Min(1, 2 * d / (r1 + r2));
        const double Quarter = Math.PI / 2;
        var neck = new StreamGeometry();
        using (StreamGeometryContext g = neck.Open())
        {
            g.BeginFigure(p1, true, true);
            g.BezierTo(PointOnCircle(p1, a1 - Quarter, r1 * reach), PointOnCircle(p3, a3 + Quarter, r2 * reach), p3, true, true);
            g.LineTo(p4, true, true);
            g.BezierTo(PointOnCircle(p4, a4 - Quarter, r2 * reach), PointOnCircle(p2, a2 + Quarter, r1 * reach), p2, true, true);
        }
        return neck;
    }

    static Point PointOnCircle(Point center, double angle, double radius) =>
        new(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle));
}
