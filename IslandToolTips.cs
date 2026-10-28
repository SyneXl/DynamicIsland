using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;

namespace DynamicIsland;

// Retain WPF's hover/cancellation service, including keyboard accessibility.
sealed class IslandToolTips
{
    static readonly ConditionalWeakTable<Window, IslandToolTips> Hosts = new();
    readonly Window _owner;
    ToolTip? _current;

    static IslandToolTips()
    {
        // These attached properties don't inherit from Window. Defaults on
        // FrameworkElement also cover controls created later in C#.
        ToolTipService.InitialShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(1200));
        ToolTipService.BetweenShowDelayProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(0));
        ToolTipService.ShowDurationProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(6000));
        EventManager.RegisterClassHandler(typeof(ToolTip), ToolTip.OpenedEvent, new RoutedEventHandler(OnOpened));
    }

    public static void Initialize() { }

    public IslandToolTips(Window owner)
    {
        _owner = owner;
        Hosts.Add(owner, this);
        owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler((_, _) => Dismiss()), true);
        owner.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => Dismiss()), true);
        owner.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Dismiss(); };
        owner.IsVisibleChanged += (_, _) => { if (!owner.IsVisible) Dismiss(); };
        owner.LocationChanged += (_, _) => Dismiss();
        owner.Deactivated += (_, _) => Dismiss();
        owner.Closed += (_, _) => { Dismiss(); Hosts.Remove(owner); };
    }

    static void OnOpened(object sender, RoutedEventArgs e)
    {
        var tooltip = (ToolTip)sender;
        if (tooltip.PlacementTarget == null || Window.GetWindow(tooltip.PlacementTarget) is not { } owner
            || !Hosts.TryGetValue(owner, out var host)) return;
        host.Track(tooltip);
    }

    void Track(ToolTip tooltip)
    {
        if (_current != tooltip) { Dismiss(); _current = tooltip; tooltip.Closed += OnClosed; }
        // Opened runs synchronously, before the popup's first rendered frame.
        tooltip.SetCurrentValue(FrameworkElement.StyleProperty, _owner.FindResource("IslandToolTip"));
        Raise();
    }

    void OnClosed(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _current))
        {
            _current.Closed -= OnClosed;
            _current = null;
        }
    }

    public void Raise()
    {
        if (_current is not { IsOpen: true } tooltip || !_owner.IsVisible) return;
        if (PresentationSource.FromVisual(tooltip) is HwndSource source)
            Native.KeepPopupAbove(source.Handle, new WindowInteropHelper(_owner).Handle);
    }

    public void Dismiss()
    {
        if (_current is not { } tooltip) return;
        _current = null;
        tooltip.Closed -= OnClosed;
        tooltip.SetCurrentValue(ToolTip.IsOpenProperty, false);
    }
}
