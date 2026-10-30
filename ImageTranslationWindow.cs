using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;

namespace DynamicIsland;

// Only covers the captured region. Its separate toolbar is not included when
// copying an image; positioning uses physical pixels, including negative monitors.
sealed class ImageTranslationWindow : Window
{
    readonly BitmapSource _original;
    BitmapSource _translated;
    ImageTranslationResult _result;
    readonly Image _image;
    readonly SelectionFrame _frame;
    readonly Window _toolbar;
    readonly Button _originalButton, _translatedButton, _copyButton;
    readonly Int32Rect _area;
    bool _showOriginal;
    public bool IsProcessing { get; private set; }

    public ImageTranslationWindow(MainWindow owner, Int32Rect area, BitmapSource original, BitmapSource translated,
        ImageTranslationResult result, Action selectAgain, bool processing = false)
    {
        Owner = owner; _area = area; _original = original; _translated = translated; _result = result;
        IsProcessing = processing;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        AllowsTransparency = true; Background = Brushes.Transparent; Title = "Перевод области";
        _image = new Image { Name = "TranslationImage", Source = processing ? original : translated, Stretch = Stretch.Fill };
        var surface = new Grid { ClipToBounds = true };
        surface.Children.Add(new Image { Source = original, Stretch = Stretch.Fill, IsHitTestVisible = false });
        surface.Children.Add(_image);
        // Two fine contrasting edges keep the region legible on both light and
        // dark backgrounds. They overlay the preview, never the source bitmap.
        _frame = new SelectionFrame { Name = "TranslationRegionFrame", IsProcessing = processing };
        surface.Children.Add(_frame);
        Content = surface;
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6) };
        Button MakeButton(string text, RoutedEventHandler click)
        {
            var button = new Button { Content = text, Style = (Style)owner.FindResource("ChipButton"), Height = 30,
                MinWidth = Math.Max(32, text.Length * 7 + 20), FontSize = 12, Focusable = true,
                Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(2, 0, 2, 0) };
            button.Click += click; bar.Children.Add(button); return button;
        }
        _translatedButton = MakeButton("Перевод", (_, _) => ShowOriginal(false));
        _originalButton = MakeButton("Оригинал", (_, _) => ShowOriginal(true));
        _copyButton = MakeButton("Копировать", (_, _) => CopyImage());
        MakeButton("Текст", (_, _) => CopyText());
        MakeButton("Ещё", (_, _) => { Close(); selectAgain(); });
        var close = MakeButton("", (_, _) => Close());
        close.Content = new Icon { Kind = Glyph.Cross, Fill = Brushes.White, Width = 12, Height = 12 };
        System.Windows.Automation.AutomationProperties.SetName(close, "Закрыть перевод");
        _toolbar = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
            Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            FontFamily = new FontFamily(new Uri("pack://application:,,,/"), "./Fonts/#SF Pro Text, Segoe UI"),
            Foreground = Brushes.White,
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(12, 12, 12)), CornerRadius = new CornerRadius(21), Child = bar },
        };
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), area.X, area.Y, area.Width, area.Height, 0x0040);
        ContentRendered += (_, _) => { if (!IsProcessing) ShowToolbar(); Activate(); Focus(); };
        Closed += (_, _) => { if (_toolbar.IsVisible) _toolbar.Close(); };
        PreviewKeyDown += KeyPressed; _toolbar.PreviewKeyDown += KeyPressed;
        MouseRightButtonUp += (_, _) => Close();
        ShowOriginal(false);
    }

    void ShowToolbar()
    {
        if (_toolbar.IsVisible) return;
        _toolbar.Owner = this; _toolbar.Show(); PositionToolbar();
        if (SystemParameters.ClientAreaAnimation)
            _toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }
    internal void Complete(BitmapSource translated, ImageTranslationResult result)
    {
        _translated = translated; _result = result;
        IsProcessing = false; _frame.IsProcessing = false;
        ShowOriginal(false);
        if (SystemParameters.ClientAreaAnimation)
            _image.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        if (IsVisible) ShowToolbar();
    }

    void PositionToolbar()
    {
        var handle = new WindowInteropHelper(this).Handle;
        IntPtr monitor = MonitorFromWindow(handle, 2);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(monitor, ref info);
        var dpi = VisualTreeHelper.GetDpi(_toolbar);
        int width = (int)Math.Ceiling(_toolbar.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(_toolbar.ActualHeight * dpi.DpiScaleY);
        int x = Math.Clamp(_area.X + (_area.Width - width) / 2, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
        int y = _area.Y + _area.Height + 8;
        if (y + height > info.Work.Bottom) y = _area.Y - height - 8;
        if (y < info.Work.Top) y = Math.Clamp(_area.Y + 8, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        SetWindowPos(new WindowInteropHelper(_toolbar).Handle, new IntPtr(-1), x, y, width, height, 0x0010 | 0x0040);
    }
    internal void ShowOriginal(bool original)
    {
        _image.BeginAnimation(OpacityProperty, null);
        _showOriginal = original; _image.Source = original ? _original : _translated;
        _originalButton.Foreground = original ? Brushes.White : Brushes.Gray;
        _translatedButton.Foreground = original ? Brushes.Gray : Brushes.White;
    }
    void CopyImage()
    {
        try { Clipboard.SetImage(_showOriginal ? _original : _translated); _copyButton.Content = "Скопировано"; }
        catch { _copyButton.Content = "Повтори"; }
    }
    void CopyText()
    {
        try { Clipboard.SetDataObject(_showOriginal ? string.Join("\n\n", _result.Blocks.Select(b => b.Text)) : _result.Text, true); }
        catch { _copyButton.Content = "Повтори"; }
    }
    void KeyPressed(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
        else if (!IsProcessing && e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        { e.Handled = true; if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) CopyText(); else CopyImage(); }
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
