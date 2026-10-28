using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public sealed class Equalizer : FrameworkElement
{
    const double BarWidth = 3;
    const double AttackSeconds = 0.03, ReleaseSeconds = 0.17;
    const double StillBelow = 0.002;
    const double Absent = 0.001;
    const double QuietOpacity = 0.55, LoudnessGain = 0.45;

    const double DotPitch = 4.2;
    const double UnlitOpacity = 0.16, LitOpacity = 0.72;
    const double HeadWhiteness = 0.5;

    static readonly double[] WobbleFast = [7.1, 9.3, 6.2, 10.4, 8.0, 5.6, 9.9, 7.7];
    static readonly double[] WobbleSlow = [2.3, 3.1, 1.7, 2.9, 3.7, 2.1, 1.3, 3.3];

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(SolidColorBrush), typeof(Equalizer),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty QuietProperty = DependencyProperty.Register(
        nameof(Quiet), typeof(bool), typeof(Equalizer),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool Quiet { get => (bool)GetValue(QuietProperty); set => SetValue(QuietProperty, value); }

    readonly Shades _shades = new();

    int _bars = 5, _few = 5;
    double _open;
    bool _dots;
    double[] _levels, _targets;
    int[] _bandOfBar, _barOfBand;
    BandMeter _closed, _opened;

    public Equalizer() => Rebuild();

    public SolidColorBrush Fill
    {
        get => (SolidColorBrush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public int Bars
    {
        get => _bars;
        set
        {
            _bars = Math.Max(2, value);
            Rebuild();
        }
    }

    public int Few
    {
        get => Math.Min(_few, _bars);
        set
        {
            _few = Math.Max(2, value);
            Rebuild();
        }
    }

    public double Open
    {
        get => _open;
        set
        {
            value = Math.Clamp(value, 0, 1);
            if (value == _open) return;
            _open = value;
            InvalidateVisual();
        }
    }

    public bool Dots
    {
        get => _dots;
        set
        {
            if (value == _dots) return;
            _dots = value;
            InvalidateVisual();
        }
    }

    public double Level(int band) => _levels[_barOfBand[band]];

    public bool Tick(float[]? spectrum, double peak, bool playing, double t, double dt)
    {
        if (!playing) Array.Clear(_targets);
        else if (spectrum != null) FollowSpectrum(spectrum, dt);
        else FollowPeak(peak, t);

        double rise = 1 - Math.Exp(-dt / (Quiet ? 0.12 : AttackSeconds));
        double fall = 1 - Math.Exp(-dt / (Quiet ? 0.35 : ReleaseSeconds));
        bool moved = false;
        for (int i = 0; i < _bars; i++)
        {
            double next = _levels[i] + (_targets[i] - _levels[i]) * (_targets[i] > _levels[i] ? rise : fall);
            if (Math.Abs(next - _levels[i]) > StillBelow) moved = true;
            _levels[i] = next;
        }

        if (moved) InvalidateVisual();
        return moved;
    }

    [MemberNotNull(nameof(_levels), nameof(_targets), nameof(_bandOfBar), nameof(_barOfBand), nameof(_closed), nameof(_opened))]
    void Rebuild()
    {
        _levels = new double[_bars];
        _targets = new double[_bars];
        _closed = new BandMeter(Few);
        _opened = new BandMeter(_bars);

        _bandOfBar = new int[_bars];
        _barOfBand = new int[_bars];
        int center = (_bars - 1) / 2;
        for (int band = 0; band < _bars; band++)
        {
            int bar = center + (band % 2 == 1 ? (band + 1) / 2 : -band / 2);
            _barOfBand[band] = bar;
            _bandOfBar[bar] = band;
        }
    }

    void FollowSpectrum(float[] spectrum, double dt)
    {
        _closed.Measure(spectrum, dt);
        _opened.Measure(spectrum, dt);
        for (int bar = 0; bar < _bars; bar++)
        {
            int band = _bandOfBar[bar];
            double opened = _opened.Heights[band];
            _targets[bar] = band < _closed.Heights.Length ? _closed.Heights[band] + (opened - _closed.Heights[band]) * _open : opened;
        }
    }

    void FollowPeak(double peak, double t)
    {
        double level = Math.Pow(Math.Clamp(peak * 1.8, 0, 1), 0.6);
        double mid = (_bars - 1) / 2.0;
        for (int i = 0; i < _bars; i++)
        {
            double noise = 0.5 + 0.5 * Math.Sin(t * WobbleFast[i % WobbleFast.Length] + i * 1.9) * Math.Cos(t * WobbleSlow[i % WobbleSlow.Length] + i * 0.7);
            double envelope = 1 - 0.3 * Math.Abs(i - mid) / mid;
            _targets[i] = level * envelope * (0.3 + 0.7 * noise);
        }
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        Color fill = Fill.Color;
        double standing = Few + (_bars - Few) * _open;
        double gap = (w - standing * BarWidth) / Math.Max(standing - 1, 1), x = 0;
        for (int bar = 0; bar < _bars; bar++)
        {
            double presence = Math.Clamp(standing - _bandOfBar[bar], 0, 1);
            if (presence > Absent)
            {
                if (_dots && !Quiet) DrawDots(dc, fill, x, h, _levels[bar], presence);
                else DrawBar(dc, fill, x, h, _levels[bar] * (Quiet ? 0.34 : 1), presence);
            }
            x += presence * (BarWidth + gap);
        }
    }

    void DrawBar(DrawingContext dc, Color fill, double x, double h, double level, double presence)
    {
        double width = BarWidth * presence, height = width + level * (h - width) * presence;
        Brush shade = _shades.Of(fill, (QuietOpacity + LoudnessGain * level) * presence);
        dc.DrawRoundedRectangle(shade, null, new Rect(x, (h - height) / 2, width, height), width / 2, width / 2);
    }

    void DrawDots(DrawingContext dc, Color fill, double x, double h, double level, double presence)
    {
        double fit = h / DotPitch, whole = Math.Floor(fit), part = Math.Clamp((fit - whole - 0.3) / 0.4, 0, 1);
        double rows = Math.Max(whole + part * part * (3 - 2 * part), 1);
        double gap = (h - rows * BarWidth) / Math.Max(rows - 1, 1), y = h;
        double lit = 1 + level * (rows - 1);
        for (int r = 0; r < rows; r++)
        {
            double row = Math.Min(rows - r, 1), size = BarWidth * presence * row;
            double on = Math.Clamp(lit - r, 0, 1), head = on * (1 - Math.Clamp(lit - r - 1, 0, 1));
            var center = new Point(x + BarWidth * presence / 2, y - size / 2);
            Brush shade = _shades.Of(fill, (UnlitOpacity + (LitOpacity - UnlitOpacity) * on + (1 - LitOpacity) * head) * presence * row);
            dc.DrawEllipse(shade, null, center, size / 2, size / 2);
            if (head > 0.01) dc.DrawEllipse(Shades.White(HeadWhiteness * head * presence * row), null, center, size / 2, size / 2);
            y -= row * (BarWidth + gap);
        }
    }
}
