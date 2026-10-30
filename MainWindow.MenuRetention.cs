using System.Windows;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly MenuHoverRetention _menuRetention = new();
    readonly DispatcherTimer _menuRetentionTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    Func<Point> _menuPointerPosition = () => Native.CursorPosition;

    void PrepareMenuRetention()
    {
        _menuRetentionTimer.Tick += (_, _) => PollMenuRetention();
        Closed += (_, _) => ResetMenuRetention();
        IsVisibleChanged += (_, _) => { if (!IsVisible) ResetMenuRetention(); };
        LocationChanged += (_, _) => ResetMenuRetention();
    }

    bool MenuRetentionAllowed => IsLoaded && IsVisible && _panel != Panel.None && !_hiddenByFullscreen && !_sentAway
        && !_pickingArea && !_appearanceExiting;

    Rect MenuScreenBounds(double width, double height)
    {
        var topLeft = Island.PointToScreen(new Point((HostWidth - width) / 2, 0));
        var bottomRight = Island.PointToScreen(new Point((HostWidth + width) / 2, height));
        return new Rect(topLeft, bottomRight);
    }

    void RetainMenuBeforeResize(double width, double height)
    {
        if (!MenuRetentionAllowed || height <= CompactMaxHeight) { ResetMenuRetention(); return; }
        // Only an open page's geometry change can arm the guard, not opening an
        // island from its compact state, or an ordinary leave without a resize.
        bool changed = Math.Abs(width - _width.Target) > .5 || Math.Abs(height - _height.Target) > .5;
        if (!changed || _height.Target <= CompactMaxHeight) return;
        Point pointer = _menuPointerPosition();
        if (_menuRetention.Active || Island.InputHitTest(Island.PointFromScreen(pointer)) != null)
            _menuRetention.Resize(MenuScreenBounds(Math.Max(_width.Value, MinPillSize), Math.Max(_height.Value, MinPillSize)),
                MenuScreenBounds(width, height), pointer);
        if (_menuRetention.Active)
        {
            _collapseTimeout.Cancel();
            _menuRetentionTimer.Start();
        }
    }

    bool RetainMenuForPointer()
    {
        if (!_menuRetention.Active) return false;
        if (!MenuRetentionAllowed) { ResetMenuRetention(); return false; }
        bool retained = _menuRetention.Retains(MenuScreenBounds(_width.Target, _height.Target), _menuPointerPosition());
        if (!retained) _menuRetentionTimer.Stop();
        return retained;
    }

    void PollMenuRetention()
    {
        if (!_menuRetention.Active) { _menuRetentionTimer.Stop(); return; }
        if (RetainMenuForPointer()) { _collapseTimeout.Cancel(); return; }
        // Outside the visible HWND we receive no MouseMove/MouseLeave events.
        // Poll only while a footprint exists, then resume the normal leave delay.
        _menuRetentionTimer.Stop();
        if (MenuRetentionAllowed && !_hovered) _collapseTimeout.Start(CollapseDelay);
    }

    void ResetMenuRetention()
    {
        _menuRetention.Clear();
        _menuRetentionTimer.Stop();
    }
}
