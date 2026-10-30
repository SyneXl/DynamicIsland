using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland;

sealed class AreaPicker : Window
{
    const double ShadeAlpha = 0.4;
    const double FrameRadius = 6;
    const double SmallestSide = 16;
    const int RowLeftUncovered = 1;
    const uint KeepOrder = 0x0004;

    readonly Int32Rect _desktop;
    readonly RectangleGeometry _everything = new(), _hole = new() { RadiusX = FrameRadius, RadiusY = FrameRadius };
    readonly SelectionFrame _frame = new() { Visibility = Visibility.Collapsed };
    readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(9, 5, 9, 5) };
    readonly Border _sizeBadge;
    Point _pressedAt;
    bool _dragging;

    public AreaPicker(Int32Rect desktop)
    {
        _desktop = desktop;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;
        FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#SF Pro Text, Segoe UI");
        _sizeBadge = new Border { Background = new SolidColorBrush(Color.FromArgb(235, 16, 16, 16)), CornerRadius = new CornerRadius(8),
            Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed };

        var shade = new Path
        {
            Fill = new SolidColorBrush(Colors.Black.WithAlpha(ShadeAlpha)),
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, _everything, _hole),
        };
        Content = new Canvas { Children = { shade, _frame, _sizeBadge } };
        SizeChanged += (_, _) => _everything.Rect = new Rect(RenderSize);
    }

    public Int32Rect? Picked { get; private set; }
    public event Action<Rect>? SelectionChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, IntPtr.Zero, _desktop.X, _desktop.Y, _desktop.Width, _desktop.Height - RowLeftUncovered, KeepOrder);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        Activate();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _pressedAt = e.GetPosition(this);
        _dragging = CaptureMouse();
        Outline(new Rect(_pressedAt, _pressedAt));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) Outline(new Rect(_pressedAt, e.GetPosition(this)));
        else if (IsLoaded) { Point p = PointToScreen(e.GetPosition(this)); SelectionChanged?.Invoke(new Rect(p.X - 8, p.Y - 8, 16, 16)); }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_dragging) return;

        _dragging = false;
        ReleaseMouseCapture();
        var area = ClipSelection(new Rect(_pressedAt, e.GetPosition(this)));
        if (area.Width < SmallestSide || area.Height < SmallestSide)
        {
            Outline(Rect.Empty);
            return;
        }

        Point from = PointToScreen(area.TopLeft), to = PointToScreen(area.BottomRight);
        Picked = new Int32Rect((int)from.X, (int)from.Y, (int)(to.X - from.X), (int)(to.Y - from.Y));
        Close();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }
    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;
        _dragging = false; Outline(Rect.Empty);
    }

    Rect ClipSelection(Rect area) { if (!area.IsEmpty) area.Intersect(new Rect(RenderSize)); return area; }

    void Outline(Rect area)
    {
        area = ClipSelection(area);
        _hole.Rect = area;
        _frame.SetVisible(!area.IsEmpty);
        _sizeBadge.SetVisible(!area.IsEmpty && area.Width >= SmallestSide && area.Height >= SmallestSide);
        if (area.IsEmpty) { SelectionChanged?.Invoke(Rect.Empty); return; }

        Canvas.SetLeft(_frame, area.X);
        Canvas.SetTop(_frame, area.Y);
        _frame.Width = area.Width;
        _frame.Height = area.Height;
        if (!IsLoaded) return;
        Point from = PointToScreen(area.TopLeft), to = PointToScreen(area.BottomRight);
        _sizeText.Text = $"{Math.Round(to.X - from.X):0} × {Math.Round(to.Y - from.Y):0}";
        _sizeBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var badge = _sizeBadge.DesiredSize;
        Canvas.SetLeft(_sizeBadge, Math.Clamp(area.Right - badge.Width, 4, Math.Max(4, ActualWidth - badge.Width - 4)));
        double y = area.Bottom + 9;
        if (y + badge.Height > ActualHeight - 4) y = area.Top - badge.Height - 9;
        Canvas.SetTop(_sizeBadge, Math.Clamp(y, 4, Math.Max(4, ActualHeight - badge.Height - 4)));
        SelectionChanged?.Invoke(new Rect(from, to));
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
}
