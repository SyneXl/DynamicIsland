using System.Windows;
using System.Windows.Controls;

namespace DynamicIsland;

public partial class MainWindow
{
    int _pageDirection;

    void PagePrevious_Click(object sender, RoutedEventArgs e) => NavigateOpenPage(-1);
    void PageNext_Click(object sender, RoutedEventArgs e) => NavigateOpenPage(1);

    void NavigateOpenPage(int direction)
    {
        CancelGrab();
        _collapseTimeout.Cancel();
        SelectPanel(Panel.Player);
        SwitchSource(direction);
        UpdateView();
    }

    void SyncNavigation()
    {
        bool visible = _media.SourceCount > 0 && _view is View.MediaBig or View.IdleBig;
        PagePrevious.Visibility = PageNext.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        PagePrevious.IsHitTestVisible = PageNext.IsHitTestVisible = visible;
    }

    void PlaceNavigation(Rect pill)
    {
        Canvas.SetLeft(PagePrevious, pill.Left + 1);
        Canvas.SetLeft(PageNext, pill.Right - PageNext.Width - 1);
        Canvas.SetTop(PagePrevious, Math.Max(0, (pill.Height - PagePrevious.Height) / 2));
        Canvas.SetTop(PageNext, Math.Max(0, (pill.Height - PageNext.Height) / 2));
    }
}
