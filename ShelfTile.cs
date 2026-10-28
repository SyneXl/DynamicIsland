using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

sealed class ShelfTile : Grid
{
    public const double TileWidth = 64;
    const double FrameSize = 56, FrameRadius = 13;
    const double IconSize = 40;
    const double HoverScale = 1.06;

    readonly Border _frame;
    readonly ScaleTransform _frameScale = new(1, 1);
    readonly Border _cross;
    readonly TextBlock _name;

    public ShelfTile(Shelf.Item item)
    {
        Item = item;
        Width = TileWidth;
        Background = Brushes.Transparent;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(FrameSize) });
        RowDefinitions.Add(new RowDefinition());
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = new ScaleTransform(1, 1);

        _frame = new Border
        {
            Width = FrameSize,
            Height = FrameSize,
            CornerRadius = new CornerRadius(FrameRadius),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            Clip = new RectangleGeometry(new Rect(0, 0, FrameSize, FrameSize), FrameRadius, FrameRadius),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _frameScale,
        };
        RenderOptions.SetBitmapScalingMode(_frame, BitmapScalingMode.HighQuality);
        Children.Add(_frame);

        _name = new TextBlock
        {
            Text = item.Name,
            FontSize = 10.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        };
        SetRow(_name, 1);
        Children.Add(_name);

        _cross = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)),
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -6, -2, 0),
            Opacity = 0,
            Cursor = Cursors.Hand,
            Child = new Icon { Kind = Glyph.Cross, Width = 10, Height = 10 },
        };
        _cross.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _cross.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Removed?.Invoke(this);
        };
        Children.Add(_cross);

        Cursor = Cursors.Hand;
        MouseEnter += (_, _) => SetHovered(true);
        MouseLeave += (_, _) => SetHovered(false);
        ShowPicture();
    }

    public Shelf.Item Item { get; }

    public event Action<ShelfTile>? Removed;

    public void ShowPicture()
    {
        _name.Text = Item.Name;
        if (Item.Text != null)
        {
            _frame.Child = new TextBlock { Text = new string(Item.Text.Take(180).ToArray()), TextWrapping = TextWrapping.Wrap, FontSize = 8,
                Margin = new Thickness(7), Foreground = Brushes.White, IsHitTestVisible = false };
            return;
        }
        if (Item.Picture is not { } picture) return;
        FrameworkElement shown = Item.IsPhoto
            ? new Border
            {
                CornerRadius = new CornerRadius(FrameRadius),
                Background = new ImageBrush(picture) { Stretch = Stretch.UniformToFill },
            }
            : new Image { Source = picture, Stretch = Stretch.Uniform, Width = IconSize, Height = IconSize };
        shown.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(220)));
        _frame.Child = shown;
    }

    void SetHovered(bool on)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _frameScale.AnimateScale(new DoubleAnimation(on ? HoverScale : 1, Ms(on ? 160 : 260)) { EasingFunction = ease });
        _cross.BeginAnimation(OpacityProperty, new DoubleAnimation(on ? 1 : 0, Ms(on ? 120 : 220)));
    }
}
