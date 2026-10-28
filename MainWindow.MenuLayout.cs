using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland;

public partial class MainWindow
{
    const double MenuRowHeight = 40, MenuChromeHeight = 48, MenuMaxListHeight = 400;
    const double MenuEditorRowHeight = 44, MenuEditorMaxListHeight = 352, MenuEditorChromeHeight = 56;
    sealed record MenuEditorRow(Grid Row, Button ToggleButton, Toggle? Switch, Button Grip, FrameworkElement Label);
    readonly Dictionary<string, Button> _menuButtons = new(StringComparer.Ordinal);
    readonly Dictionary<string, MenuEditorRow> _menuEditorRows = new(StringComparer.Ordinal);
    MenuLayout _menuLayout = Settings.Menu;
    Func<MenuLayout, bool> _saveMenuLayout = Settings.TrySetMenuLayout;
    readonly DispatcherTimer _menuDragScroll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    string? _menuDragId;
    int _menuDragFrom, _menuDragTarget;
    double _menuDragStartY, _menuDragGrabY;
    bool _menuDragged;

    void PrepareMenuLayout()
    {
        foreach (Button row in MenuRows.Children.OfType<Button>())
            _menuButtons.Add((string)row.Tag, row);
        // Fail early in development if a newly registered page has no corresponding route.
        if (!_menuButtons.Keys.ToHashSet().SetEquals(MenuLayout.Catalog.Select(d => d.Id)))
            throw new InvalidOperationException("Menu catalog and page routes must have matching stable IDs.");

        foreach (MenuDefinition definition in MenuLayout.Catalog)
            _menuEditorRows.Add(definition.Id, CreateMenuEditorRow(definition));
        MenuEditorRows.PreviewMouseMove += MenuReorder_MouseMove;
        MenuEditorRows.PreviewMouseLeftButtonUp += (_, e) => { if (_menuDragId != null) { e.Handled = true; EndMenuReorder(true); } };
        MenuEditorRows.LostMouseCapture += (_, _) => { if (_menuDragId != null) EndMenuReorder(false); };
        _menuDragScroll.Tick += (_, _) => AutoScrollMenuReorder();
        Closed += (_, _) => EndMenuReorder(false);
        SyncMenuLayout();
    }

    MenuEditorRow CreateMenuEditorRow(MenuDefinition definition)
    {
        string id = definition.Id;
        var row = new Grid { Height = MenuEditorRowHeight, Tag = id };
        row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(53) });
        row.ColumnDefinitions.Add(new() { Width = new GridLength(28) });
        var content = new Grid();
        content.ColumnDefinitions.Add(new() { Width = new GridLength(27) });
        content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var icon = new Icon { Kind = definition.Icon, Width = 16, Height = 16, Fill = _dim,
                              HorizontalAlignment = HorizontalAlignment.Left };
        content.Children.Add(icon);
        var title = new TextBlock { Text = definition.Title, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center,
                                    TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 8, 0) };
        Grid.SetColumn(title, 1);
        content.Children.Add(title);
        var labelArea = new Border { Background = Brushes.Transparent, Padding = new(12, 0, 4, 0), Child = content, Cursor = Cursors.SizeAll };
        row.Children.Add(labelArea);
        Toggle? toggle = null;
        FrameworkElement state;
        if (definition.Required)
        {
            var label = new TextBlock { Text = "Всегда", FontSize = 10.5, Foreground = _dim,
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            state = label;
        }
        else
        {
            toggle = new Toggle { Width = 40, Height = 21, HorizontalAlignment = HorizontalAlignment.Right };
            state = toggle;
        }
        var button = new Button { Style = (Style)FindResource("MenuEditButton"), Content = state, Padding = new Thickness(0),
            Tag = id, IsEnabled = !definition.Required,
            ToolTip = definition.Required ? "Всегда в меню" : "Показать или скрыть пункт" };
        AutomationProperties.SetName(button, definition.Title);
        button.Click += (_, _) => ToggleMenuItem(id);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        var grip = new Button { Style = (Style)FindResource("MenuMoveButton"), Width = 28, Cursor = Cursors.SizeAll,
            Content = new System.Windows.Shapes.Path { Width = 10, Height = 14, Stretch = Stretch.Uniform, Fill = _dim,
                Data = Geometry.Parse("M1,1 H3 V3 H1 Z M7,1 H9 V3 H7 Z M1,6 H3 V8 H1 Z M7,6 H9 V8 H7 Z M1,11 H3 V13 H1 Z M7,11 H9 V13 H7 Z") },
            ToolTip = "Перетащи для изменения порядка\nAlt+↑ / Alt+↓" };
        AutomationProperties.SetName(grip, definition.Title + ": изменить порядок");
        Grid.SetColumn(grip, 2);
        row.Children.Add(grip);
        void OnDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            grip.Focus();
            BeginMenuReorder(id, e.GetPosition(MenuEditorRows).Y, e.GetPosition(row).Y);
            if (!MenuEditorRows.CaptureMouse()) EndMenuReorder(false);
        }
        labelArea.PreviewMouseLeftButtonDown += OnDown;
        grip.PreviewMouseLeftButtonDown += OnDown;
        row.PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Alt || e.Key != Key.System || e.SystemKey is not (Key.Up or Key.Down)) return;
            e.Handled = true;
            MoveMenuItem(id, e.SystemKey == Key.Up ? -1 : 1);
        };
        return new(row, button, toggle, grip, labelArea);
    }

    void BeginMenuReorder(string id, double pointerY, double grabY)
    {
        EndMenuReorder(false);
        if (!_menuEditorRows.ContainsKey(id)) return;
        _menuDragId = id;
        _menuDragFrom = _menuDragTarget = _menuLayout.Entries.ToList().FindIndex(e => e.Id == id);
        _menuDragStartY = pointerY;
        _menuDragGrabY = Math.Clamp(grabY, 0, MenuEditorRowHeight);
        _menuDragged = false;
        _collapseTimeout.Cancel();
    }

    void MenuReorder_MouseMove(object sender, MouseEventArgs e)
    {
        if (_menuDragId == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndMenuReorder(false); return; }
        UpdateMenuReorder(e.GetPosition(MenuEditorRows).Y);
        e.Handled = true;
    }

    void UpdateMenuReorder(double pointerY)
    {
        if (_menuDragId == null || !double.IsFinite(pointerY)) return;
        if (!_menuDragged && Math.Abs(pointerY - _menuDragStartY) < SystemParameters.MinimumVerticalDragDistance) return;
        _menuDragged = true;
        _menuDragScroll.Start();
        _menuDragTarget = (int)Math.Floor(Math.Clamp((pointerY - _menuDragGrabY + MenuEditorRowHeight / 2) / MenuEditorRowHeight, 0, _menuLayout.Entries.Count - 1));
        var preview = _menuLayout.Move(_menuDragId, _menuDragTarget - _menuDragFrom);
        ReorderMenuChildren(MenuEditorRows.Children, preview.Entries.Select(e => (UIElement)_menuEditorRows[e.Id].Row).ToArray());
        var row = _menuEditorRows[_menuDragId].Row;
        row.Opacity = 0.8;
        System.Windows.Controls.Panel.SetZIndex(row, 10);
        row.RenderTransform = new TranslateTransform(0,
            Math.Clamp(pointerY - _menuDragGrabY, 0, (_menuLayout.Entries.Count - 1) * MenuEditorRowHeight) - _menuDragTarget * MenuEditorRowHeight);
    }

    void AutoScrollMenuReorder()
    {
        if (_menuDragId == null) { _menuDragScroll.Stop(); return; }
        double y = Mouse.GetPosition(MenuEditorScroll).Y;
        double delta = y < 26 ? -9 : y > MenuEditorScroll.ActualHeight - 26 ? 9 : 0;
        if (delta == 0) return;
        MenuEditorScroll.ScrollToVerticalOffset(MenuEditorScroll.VerticalOffset + delta);
        MenuEditorScroll.UpdateLayout();
        UpdateMenuReorder(Mouse.GetPosition(MenuEditorRows).Y);
    }

    void EndMenuReorder(bool commit)
    {
        if (_menuDragId is not { } id) return;
        var next = _menuLayout.Move(id, _menuDragTarget - _menuDragFrom);
        bool moved = _menuDragged;
        _menuDragId = null;
        _menuDragScroll.Stop();
        var row = _menuEditorRows[id].Row;
        row.Opacity = 1;
        row.RenderTransform = Transform.Identity;
        System.Windows.Controls.Panel.SetZIndex(row, 0);
        if (MenuEditorRows.IsMouseCaptured) MenuEditorRows.ReleaseMouseCapture();
        if (commit && moved) SaveMenuLayout(next);
        SyncMenuLayout();
    }

    void MenuLayoutRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.MenuLayout);
    void MenuLayoutBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Look);
    void MenuLayoutReset_Click(object sender, RoutedEventArgs e) { EndMenuReorder(false); SaveMenuLayout(MenuLayout.Default()); }

    void MenuLayout_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (_menuDragId != null) { EndMenuReorder(false); return; }
        ShowPanel(Panel.Look);
    }

    void ToggleMenuItem(string id)
    {
        MenuEntry? entry = _menuLayout.Entries.FirstOrDefault(e => e.Id == id);
        if (entry != null) SaveMenuLayout(_menuLayout.SetVisible(id, !entry.Visible));
    }

    void MoveMenuItem(string id, int offset)
    {
        EndMenuReorder(false);
        IInputElement? focus = Keyboard.FocusedElement;
        SaveMenuLayout(_menuLayout.Move(id, offset));
        if (focus is UIElement { IsEnabled: true } element) element.Focus();
        else if (focus != null)
            _menuEditorRows[id].Grip.Focus();
        _menuEditorRows[id].Row.BringIntoView();
    }

    void SaveMenuLayout(MenuLayout layout)
    {
        if (_menuLayout.Entries.SequenceEqual(layout.Entries)) return;
        if (!_saveMenuLayout(layout))
        {
            MenuLayoutStatus.Text = "Не удалось сохранить. Попробуй ещё раз.";
            MenuLayoutStatus.Visibility = Visibility.Visible;
            SyncMenuLayout();
            return;
        }
        _menuLayout = layout;
        MenuLayoutStatus.Text = "";
        MenuLayoutStatus.Visibility = Visibility.Collapsed;
        SyncMenuLayout();
    }

    void SyncMenuLayout()
    {
        UIElement? focused = Keyboard.FocusedElement as UIElement;
        double scrollOffset = MenuEditorScroll.VerticalOffset;
        ReorderMenuChildren(MenuRows.Children, _menuLayout.Entries.Select(e => (UIElement)_menuButtons[e.Id]).ToArray());
        ReorderMenuChildren(MenuEditorRows.Children, _menuLayout.Entries.Select(e => (UIElement)_menuEditorRows[e.Id].Row).ToArray());
        int index = 0;
        foreach (MenuEntry entry in _menuLayout.Entries)
        {
            // Keep the original button instances: dynamic counters and Click handlers survive reorder.
            Button button = _menuButtons[entry.Id];
            button.Visibility = entry.Visible ? Visibility.Visible : Visibility.Collapsed;
            // A previous row-stagger animation must not leave a reordered row transparent/offset.
            button.BeginAnimation(OpacityProperty, null);
            button.Opacity = 1;
            button.RenderTransform = Transform.Identity;
            MenuEditorRow editor = _menuEditorRows[entry.Id];
            editor.Switch?.Set(entry.Visible, false);
            editor.Label.Opacity = entry.Visible ? 1 : 0.5;
            AutomationProperties.SetHelpText(editor.ToggleButton, editor.Switch == null ? "Всегда в меню" : entry.Visible ? "В меню" : "Скрыт из меню");
            index++;
        }
        MenuScroll.Height = Math.Min(MenuMaxListHeight, _menuLayout.VisibleCount * MenuRowHeight);
        MenuView.Height = MenuChromeHeight + MenuScroll.Height;
        MenuEditorScroll.Height = Math.Min(MenuEditorMaxListHeight, _menuLayout.Entries.Count * MenuEditorRowHeight);
        double errorHeight = 0;
        if (MenuLayoutStatus.Visibility == Visibility.Visible)
        {
            MenuLayoutStatus.Measure(new Size(MenuLayoutView.Width - 20, double.PositiveInfinity));
            errorHeight = MenuLayoutStatus.DesiredSize.Height;
        }
        MenuLayoutView.Height = MenuEditorChromeHeight + MenuEditorScroll.Height + errorHeight;
        MenuLayoutSummary.Text = $"{_menuLayout.VisibleCount} из {_menuLayout.Entries.Count}";
        MenuLayoutReset.IsEnabled = !_menuLayout.IsDefault;
        MenuEditorScroll.ScrollToVerticalOffset(scrollOffset);
        if (focused is { IsEnabled: true, IsVisible: true } && MenuLayoutView.IsVisible) focused.Focus();
        if (_view is View.Menu or View.MenuLayout) UpdateTargets();
    }

    static void ReorderMenuChildren(UIElementCollection children, UIElement[] ordered)
    {
        for (int i = 0; i < ordered.Length; i++)
        {
            if (i < children.Count && ReferenceEquals(children[i], ordered[i])) continue;
            children.Remove(ordered[i]);
            children.Insert(i, ordered[i]);
        }
    }

    bool ScrollMenuPanel(MouseWheelEventArgs e)
    {
        ScrollViewer? scroll = _view switch
        {
            View.MenuLayout => MenuEditorScroll,
            View.Menu => MenuScroll,
            View.Zapret => ZapretScroll,
            View.ZapretFolders => ZapretFoldersScroll,
            View.ZapretStrategies => ZapretStrategiesScroll,
            _ => null,
        };
        if (scroll == null) return false;
        MenuScroll_PreviewMouseWheel(scroll, e);
        return true;
    }

    // A menu/settings scroll must never fall through to the island's volume wheel,
    // including when the list is already at either edge.
    void MenuScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta / 120.0 * 44);
        e.Handled = true;
    }

}
