using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

public sealed class Gadget : FrameworkElement
{
    public enum Model { Earbuds, Headphones, Speaker }

    const int SheetColumns = 9, FrameCount = 81;
    const double FirstTurn = -142, TurnStep = 2;
    const double SpinFrom = -140, DropFrom = -34, LandSeconds = 0.9;
    const double SwayDegrees = 11, SwaySeconds = 1.25, BobHeight = 1, BobSeconds = 1.05;

    static readonly Dictionary<Model, BitmapSource[]> Sheets = [];

    public static readonly DependencyProperty TurnProperty = DependencyProperty.Register(
        nameof(Turn), typeof(double), typeof(Gadget),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    readonly RotateTransform _rock = new();
    readonly TranslateTransform _lift = new();
    BitmapSource[] _frames = [];

    public Gadget()
    {
        IsHitTestVisible = false;
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new TransformGroup { Children = { _rock, _lift } };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public double Turn
    {
        get => (double)GetValue(TurnProperty);
        set => SetValue(TurnProperty, value);
    }

    public void Play(Model model, bool shaky, TimeSpan wait, TimeSpan stay)
    {
        _frames = FramesOf(model);

        var land = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 };
        TimeSpan landed = wait + TimeSpan.FromSeconds(LandSeconds);
        BeginAnimation(TurnProperty, Swaying(SpinFrom, 0, wait, landed, land, SwayDegrees, SwaySeconds, stay));
        _lift.BeginAnimation(TranslateTransform.YProperty,
            Swaying(DropFrom, 0, wait, landed - TimeSpan.FromSeconds(0.2), land, BobHeight, BobSeconds, stay));
        _rock.BeginAnimation(RotateTransform.AngleProperty,
            shaky ? Motion.Sway(landed, TimeSpan.FromMilliseconds(700), -13, 11, -8, 5, -2, 0) : null);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_frames.Length == 0) return;
        double at = Math.Clamp((Turn - FirstTurn) / TurnStep, 0, FrameCount - 1);
        int near = (int)at;
        var box = new Rect(RenderSize);
        dc.DrawImage(_frames[near], box);
        double blend = at - near;
        if (blend <= 0) return;
        dc.PushOpacity(blend);
        dc.DrawImage(_frames[near + 1], box);
        dc.Pop();
    }

    static BitmapSource[] FramesOf(Model model)
    {
        if (Sheets.TryGetValue(model, out BitmapSource[]? frames)) return frames;

        var sheet = new BitmapImage();
        sheet.BeginInit();
        sheet.UriSource = new Uri($"pack://application:,,,/DynamicIsland;component/Gadgets/{model}.png");
        sheet.CacheOption = BitmapCacheOption.OnLoad;
        sheet.EndInit();
        sheet.Freeze();

        int width = sheet.PixelWidth / SheetColumns;
        int height = sheet.PixelHeight / ((FrameCount + SheetColumns - 1) / SheetColumns);
        frames = new BitmapSource[FrameCount];
        for (int i = 0; i < FrameCount; i++)
            frames[i] = Shades.Frozen(new CroppedBitmap(sheet, new Int32Rect(i % SheetColumns * width, i / SheetColumns * height, width, height)));
        return Sheets[model] = frames;
    }

    static DoubleAnimationUsingKeyFrames Swaying(double from, double to, TimeSpan wait, TimeSpan landed, IEasingFunction land,
        double swing, double swingSeconds, TimeSpan stay)
    {
        DoubleAnimationUsingKeyFrames animation = Motion.Delayed(from, to, wait, landed - wait, land);
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        TimeSpan at = landed;
        for (int side = 1; at < stay; side = -side)
        {
            at += TimeSpan.FromSeconds(swingSeconds);
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(to + swing * side, KeyTime.FromTimeSpan(at), ease));
        }
        animation.Duration = at;
        return animation;
    }
}
