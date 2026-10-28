using System.Numerics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;

namespace DynamicIsland;

sealed class Glass
{
    sealed class Pane
    {
        public readonly SpriteVisual Visual;
        readonly CompositionRoundedRectangleGeometry _outline;

        public Pane(Compositor compositor, CompositionBrush backdrop)
        {
            _outline = compositor.CreateRoundedRectangleGeometry();
            Visual = compositor.CreateSpriteVisual();
            Visual.RelativeSizeAdjustment = Vector2.One;
            Visual.Brush = backdrop;
            Visual.Clip = compositor.CreateGeometricClip(_outline);
            Visual.IsVisible = false;
        }

        public void Fit((Rect Box, double Radius) part, Matrix toPixels)
        {
            Visual.IsVisible = !part.Box.IsEmpty;
            if (part.Box.IsEmpty) return;
            Point from = toPixels.Transform(part.Box.TopLeft), to = toPixels.Transform(part.Box.BottomRight);
            _outline.Offset = new Vector2((float)from.X, (float)from.Y);
            _outline.Size = new Vector2((float)(to.X - from.X), (float)(to.Y - from.Y));
            _outline.CornerRadius = new Vector2((float)(part.Radius * toPixels.M11), (float)(part.Radius * toPixels.M22));
        }
    }

    readonly Window _over;
    Compositor? _compositor;
    DesktopWindowTarget? _target;
    IntPtr _hwnd;
    Pane? _pill, _bubble;

    public Glass(Window over) => _over = over;

    public bool IsOn => _target != null;

    public IntPtr Handle => _hwnd;

    public void Show()
    {
        if (_target != null) return;
        try
        {
            Native.EnsureDispatcherQueue();
            _hwnd = Native.CreateBackdropWindow();
            _compositor = new Compositor();
            _target = DesktopComposition.TargetFor(_compositor, _hwnd);

            CompositionBrush backdrop = _compositor.CreateHostBackdropBrush();
            Windows.UI.Composition.ContainerVisual root = _compositor.CreateContainerVisual();
            root.RelativeSizeAdjustment = Vector2.One;
            _pill = new Pane(_compositor, backdrop);
            _bubble = new Pane(_compositor, backdrop);
            root.Children.InsertAtTop(_pill.Visual);
            root.Children.InsertAtTop(_bubble.Visual);
            _target.Root = root;
            Place();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            Hide();
        }
    }

    public void Hide()
    {
        _target?.Dispose();
        _compositor?.Dispose();
        if (_hwnd != IntPtr.Zero) Native.Destroy(_hwnd);
        _target = null;
        _compositor = null;
        _hwnd = IntPtr.Zero;
        _pill = _bubble = null;
    }

    public void Place()
    {
        if (_hwnd != IntPtr.Zero) Native.PlaceBehind(_hwnd, new WindowInteropHelper(_over).Handle);
    }

    public void Cut((Rect Box, double Radius) pill, (Rect Box, double Radius) bubble, Matrix toPixels)
    {
        _pill?.Fit(pill, toPixels);
        _bubble?.Fit(bubble, toPixels);
    }
}
