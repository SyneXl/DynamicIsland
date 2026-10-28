using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public sealed class CompactLyric : FrameworkElement
{
    const double FontSize = 12;
    const string Ellipsis = "…";
    const char FirstJoinedLetter = '֐', LastJoinedLetter = 'ࣿ';

    const double LineBlur = 6, LineRise = 10;
    const double LineLeaveMs = 260, LineLeaveFadeMs = 200, LineLeaveBlurMs = 220;
    const double LineEnterMs = 380, LineEnterFadeMs = 300;

    const double LetterBlur = 4, LetterRise = 8, LetterDrop = 9, LetterShrink = 0.9;
    const double LetterLeaveMs = 150, LetterLeaveStepMs = 8, LetterLeaveSpreadMs = 100;
    const double LetterEnterMs = 340, LetterEnterWaitMs = 130, LetterEnterStepMs = 14, LetterEnterSpreadMs = 220;

    const double WordTurnMs = 300, WordStepMs = 35;

    const int MaxClusters = 2;

    readonly record struct Pose(double Opacity, double Y, double ScaleX, double ScaleY, double Blur);

    sealed class TurnEase(bool rising) : EasingFunctionBase
    {
        protected override double EaseInCore(double time)
        {
            double angle = time * time * (3 - 2 * time) * Math.PI / 2;
            return rising ? Math.Sin(angle) : 1 - Math.Cos(angle);
        }

        protected override Freezable CreateInstanceCore() => new TurnEase(rising);
    }

    sealed class Piece(Brush fill, ScaleTransform size, TranslateTransform shift)
    {
        public void Glide(Pose from, Pose to, TimeSpan wait, TimeSpan run, IEasingFunction travel, IEasingFunction swell, IEasingFunction fade)
        {
            fill.BeginAnimation(Brush.OpacityProperty, Delayed(from.Opacity, to.Opacity, wait, run, fade));
            shift.BeginAnimation(TranslateTransform.YProperty, Delayed(from.Y, to.Y, wait, run, travel));
            size.BeginAnimation(ScaleTransform.ScaleXProperty, Delayed(from.ScaleX, to.ScaleX, wait, run, swell));
            size.BeginAnimation(ScaleTransform.ScaleYProperty, Delayed(from.ScaleY, to.ScaleY, wait, run, swell));
        }
    }

    sealed class Cluster(ContainerVisual visual, Piece[] pieces)
    {
        BlurEffect? _blur;

        public Piece[] Pieces => pieces;

        public void Haze(double from, double to, TimeSpan wait, TimeSpan run, IEasingFunction fade)
        {
            BlurEffect blur = _blur = new BlurEffect { Radius = from };
            visual.Effect = null;
            var change = new DoubleAnimation(from, to, run) { BeginTime = wait, EasingFunction = fade };
            change.CurrentStateInvalidated += (clock, _) =>
            {
                if (ReferenceEquals(_blur, blur) && ((Clock)clock!).CurrentState == ClockState.Active) visual.Effect = blur;
            };
            change.Completed += (_, _) =>
            {
                if (ReferenceEquals(visual.Effect, blur)) visual.Effect = null;
            };
            blur.BeginAnimation(BlurEffect.RadiusProperty, change);
        }
    }

    static readonly Pose Rest = new(1, 0, 1, 1, 0);
    static readonly Pose LetterBelow = new(0, LetterDrop, LetterShrink, LetterShrink, LetterBlur);
    static readonly Pose LetterAbove = new(0, -LetterRise, LetterShrink, LetterShrink, LetterBlur);
    static readonly IEasingFunction Sink = new CubicEase { EasingMode = EasingMode.EaseIn };
    static readonly IEasingFunction Settle = new CubicEase { EasingMode = EasingMode.EaseOut };
    static readonly IEasingFunction Launch = new QuadraticEase { EasingMode = EasingMode.EaseIn };
    static readonly IEasingFunction Land = new QuinticEase { EasingMode = EasingMode.EaseOut };
    static readonly IEasingFunction TurnSine = new TurnEase(true) { EasingMode = EasingMode.EaseIn };
    static readonly IEasingFunction TurnCosine = new TurnEase(false) { EasingMode = EasingMode.EaseIn };

    readonly VisualCollection _visuals;
    Cluster[] _clusters = [];
    readonly List<TranslateTransform> _partShifts = [];
    readonly List<Point> _partMiddles = [];
    Parting? _parting;
    int _pieceCount;
    Size _size;
    LyricChange _change;

    public CompactLyric()
    {
        _visuals = new VisualCollection(this);
        RenderTransform = new TransformGroup { Children = { Offset, Centring } };
    }

    public string Text { get; private set; } = "";

    public TranslateTransform Offset { get; } = new();

    public TranslateTransform Centring { get; } = new();

    public double IslandWidth { get; set; }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size available) => _size;

    internal void Show(string text, Brush brush, double maxWidth, LyricChange change)
    {
        Text = text;
        string shown = Fit(text, maxWidth);
        FormattedText whole = Format(shown, brush);
        _size = new Size(whole.WidthIncludingTrailingWhitespace, whole.Height);
        _change = shown.Any(letter => letter is >= FirstJoinedLetter and <= LastJoinedLetter) ? LyricChange.Smooth : change;

        _visuals.Clear();
        _partShifts.Clear();
        _partMiddles.Clear();
        (int Start, int Length)[] parts = [.. Split(shown, _change)];
        _pieceCount = parts.Length;
        int clusterSize = Math.Max((parts.Length + MaxClusters - 1) / MaxClusters, 1);
        _clusters = [.. parts.Chunk(clusterSize).Select(cluster => CreateCluster(whole, shown, cluster, brush))];
        _parting = _change == LyricChange.Wave ? new Parting([.. _partMiddles]) : null;
        InvalidateMeasure();
    }

    internal bool Part(Point? pointer, double dt)
    {
        if (_parting == null) return false;
        bool moving = _parting.Advance(pointer, dt);
        for (int i = 0; i < _parting.Count; i++)
        {
            Vector offset = _parting[i];
            _partShifts[i].X = offset.X;
            _partShifts[i].Y = offset.Y;
        }
        return moving;
    }

    internal void Enter()
    {
        BeginAnimation(OpacityProperty, null);
        Offset.BeginAnimation(TranslateTransform.YProperty, null);
        Effect = null;
        Opacity = 1;

        switch (_change)
        {
            case LyricChange.Wave:
                Stagger(LetterBelow, Rest, LetterEnterWaitMs, StepWithin(LetterEnterStepMs, LetterEnterSpreadMs), LetterEnterMs, Land, Land, Settle);
                break;
            case LyricChange.Drum:
                Stagger(WordEdgeOn(1), Rest, 0, WordStepMs, WordTurnMs, TurnCosine, TurnSine, TurnSine);
                break;
            default:
                this.AnimateBlur(LineBlur, 0, Ms(LineEnterFadeMs), Settle);
                Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(LineRise, 0, Ms(LineEnterMs)) { EasingFunction = Settle });
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(LineEnterFadeMs)));
                break;
        }
    }

    internal void Leave(bool snap)
    {
        if (snap)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            return;
        }

        switch (_change)
        {
            case LyricChange.Wave:
                Stagger(Rest, LetterAbove, 0, StepWithin(LetterLeaveStepMs, LetterLeaveSpreadMs), LetterLeaveMs, Launch, Launch, Launch);
                break;
            case LyricChange.Drum:
                Stagger(Rest, WordEdgeOn(-1), 0, WordStepMs, WordTurnMs, TurnSine, TurnCosine, TurnCosine);
                break;
            default:
                this.AnimateBlur(0, LineBlur, Ms(LineLeaveBlurMs), keep: true);
                Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-LineRise, Ms(LineLeaveMs)) { EasingFunction = Sink });
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(LineLeaveFadeMs)));
                break;
        }
    }

    Pose WordEdgeOn(int side) => new(0, side * _size.Height / 2, 1, 0, 0);

    void Stagger(Pose from, Pose to, double waitMs, double stepMs, double runMs,
        IEasingFunction travel, IEasingFunction swell, IEasingFunction fade)
    {
        TimeSpan run = TimeSpan.FromMilliseconds(runMs);
        int started = 0;
        foreach (Cluster cluster in _clusters)
        {
            TimeSpan wait = TimeSpan.FromMilliseconds(waitMs + started * stepMs);
            TimeSpan spread = TimeSpan.FromMilliseconds((cluster.Pieces.Length - 1) * stepMs);
            foreach (Piece piece in cluster.Pieces)
                piece.Glide(from, to, TimeSpan.FromMilliseconds(waitMs + started++ * stepMs), run, travel, swell, fade);
            if (from.Blur != to.Blur) cluster.Haze(from.Blur, to.Blur, wait, run + spread, fade);
        }
    }

    double StepWithin(double longestMs, double spreadMs) => Math.Min(longestMs, spreadMs / Math.Max(_pieceCount, 1));

    static IEnumerable<(int Start, int Length)> Split(string text, LyricChange change)
    {
        if (change == LyricChange.Drum) return Regex.Matches(text, @"\S+").Select(word => (word.Index, word.Length));
        if (change != LyricChange.Wave) return text.Length > 0 ? [(0, text.Length)] : [];

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        return starts
            .Select((start, i) => (start, (i + 1 < starts.Length ? starts[i + 1] : text.Length) - start))
            .Where(letter => !char.IsWhiteSpace(text[letter.start]));
    }

    Cluster CreateCluster(FormattedText whole, string text, (int Start, int Length)[] parts, Brush brush)
    {
        var visual = new ContainerVisual();
        _visuals.Add(visual);
        return new Cluster(visual, [.. parts.Select(part => CreatePiece(visual, whole, text, part.Start, part.Length, brush))]);
    }

    Piece CreatePiece(ContainerVisual cluster, FormattedText whole, string text, int start, int length, Brush brush)
    {
        Rect box = whole.BuildHighlightGeometry(new Point(), start, length)?.Bounds ?? new Rect();
        Brush fill = brush.Clone();
        var size = new ScaleTransform(1, 1, box.Left + box.Width / 2, _size.Height / 2);
        var shift = new TranslateTransform();
        var part = new TranslateTransform();
        _partShifts.Add(part);
        _partMiddles.Add(new Point(box.Left + box.Width / 2, _size.Height / 2));
        var visual = new DrawingVisual { Transform = new TransformGroup { Children = { size, shift, part } } };
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Animated);
        using (DrawingContext dc = visual.RenderOpen()) dc.DrawText(Format(text.Substring(start, length), fill), new Point(box.Left, 0));
        cluster.Children.Add(visual);
        return new Piece(fill, size, shift);
    }

    string Fit(string text, double maxWidth)
    {
        if (double.IsInfinity(maxWidth) || Format(text, Brushes.White).WidthIncludingTrailingWhitespace <= maxWidth) return text;

        int[] starts = StringInfo.ParseCombiningCharacters(text);
        int kept = starts.Length - 1;
        string cut;
        do cut = text[..starts[kept]].TrimEnd() + Ellipsis;
        while (Format(cut, Brushes.White).WidthIncludingTrailingWhitespace > maxWidth && kept-- > 0);
        return cut;
    }

    FormattedText Format(string text, Brush fill) => new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
        new Typeface((FontFamily)GetValue(TextElement.FontFamilyProperty), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
        FontSize, fill, VisualTreeHelper.GetDpi(this).PixelsPerDip);
}
