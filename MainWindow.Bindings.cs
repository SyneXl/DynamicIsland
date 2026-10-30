using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    KeyBindings _bindings = Settings.Bindings;
    Func<KeyBindings, bool> _saveBindings = Settings.TrySetBindings;
    readonly Dictionary<string, (Button Button, Border Badge, TextBlock Value)> _bindingRows = [];
    readonly HashSet<MouseButton> _bindingSuppressedUps = [];
    readonly HashSet<string> _bindingUnavailable = [];
    HoverHotkeys? _hoverHotkeys;
    bool _hotkeysActive;
    string? _recordingBinding;
    BindingGesture? _pendingBinding;
    IntPtr _bindingReturnFocus;
    ModifierKeys _leftBindingModifiers;
    Action<string>? _bindingActionTest = null;
    Func<bool>? _bindingPointerTest = null;
    Func<string, float, (bool Changed, float Level)>? _bindingAppVolumeTest = null;

    void PrepareBindings()
    {
        string group = "";
        foreach (var definition in KeyBindings.Catalog)
        {
            if (definition.Group != group)
            {
                group = definition.Group;
                BindingRows.Children.Add(new TextBlock { Text = group, FontSize = 11.5, Foreground = _dim, Margin = new(12, 14, 12, 4) });
            }
            var content = new Grid();
            content.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            content.Children.Add(new TextBlock { Text = definition.Title, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 0, 12, 0) });
            var value = new TextBlock { FontSize = 12, Foreground = _dim, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 177,
                TextTrimming = TextTrimming.CharacterEllipsis };
            var badge = new Border { Child = value, Background = new SolidColorBrush(Color.FromRgb(16, 16, 16)),
                BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(7), Padding = new Thickness(9, 4, 9, 4),
                MinHeight = 26, MaxWidth = 195, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(badge, 1); content.Children.Add(badge);
            var row = new Button { Content = content, Style = (Style)FindResource("RowButton"), Focusable = true };
            AutomationProperties.SetName(row, definition.Title);
            row.Click += (_, _) => BeginBindingCapture(definition.Id);
            BindingRows.Children.Add(row);
            _bindingRows.Add(definition.Id, (row, badge, value));
        }
        PreviewKeyDown += Binding_PreviewKeyDown;
        PreviewMouseDown += Binding_PreviewMouseDown;
        PreviewMouseUp += (_, e) => { if (_bindingSuppressedUps.Remove(e.ChangedButton)) e.Handled = true; };
        PreviewMouseWheel += Binding_PreviewMouseWheel;
        GotKeyboardFocus += (_, _) => SyncHoverHotkeys();
        LostKeyboardFocus += (_, _) => Dispatcher.InvokeAsync(SyncHoverHotkeys);
        IsVisibleChanged += (_, _) => SyncHoverHotkeys();
        Deactivated += (_, _) => { if (_recordingBinding != null) EndBindingCapture(false); SyncHoverHotkeys(); };
        Closed += (_, _) => { _hoverHotkeys?.Dispose(); _hoverHotkeys = null; };
        RefreshBindingRows();
    }

    bool BindingsAllowed => _hovered && IsVisible && !_sentAway && !_hiddenByFullscreen && !_pickingArea && !_pickingFiles
        && !_dragOver && !_draggingOut && _recordingBinding == null && _panel != Panel.Bindings;

    bool KeyboardBindingsAllowed => BindingsAllowed && !IsTyping
        && !(IsActive && (Keyboard.FocusedElement is TextBoxBase or PasswordBox
            || _panel == Panel.MenuLayout && MenuLayoutView.IsKeyboardFocusWithin
            || _panel == Panel.Shelf && ShelfView.IsKeyboardFocusWithin));

    // Use WPF's clipped, transformed visible island, not the large transparent host rectangle.
    bool PointerOverBindingSurface()
    {
        if (_bindingPointerTest != null) return _bindingPointerTest();
        if (!IsVisible || !Island.IsVisible) return false;
        return BindingHitTest(Native.CursorPosition);
    }

    bool BindingHitTest(Point screenPoint) => Island.InputHitTest(Island.PointFromScreen(screenPoint)) != null;

    void InitializeHoverHotkeys()
    {
        if (App.SmokeTest) return;
        _hoverHotkeys = new HoverHotkeys(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook(OnBindingMessage);
        SyncHoverHotkeys();
    }

    void SyncHoverHotkeys()
    {
        bool enabled = KeyboardBindingsAllowed && PointerOverBindingSurface();
        if (_hoverHotkeys == null || enabled == _hotkeysActive) return;
        _hotkeysActive = enabled;
        if (!enabled) { _hoverHotkeys.Disable(); return; }
        _bindingUnavailable.Clear();
        foreach (string action in _hoverHotkeys.Enable(_bindings)) _bindingUnavailable.Add(action);
    }

    IntPtr OnBindingMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != HoverHotkeys.Message) return IntPtr.Zero;
        if (_hoverHotkeys?.Action(wParam.ToInt32()) is not { } action) return IntPtr.Zero;
        handled = true;
        if (_bindings[action] is not { Input: BindingInput.Key } gesture) return IntPtr.Zero;
        long payload = lParam.ToInt64();
        if (((payload >> 16) & 0xffff) != KeyInterop.VirtualKeyFromKey(gesture.Key) || (payload & 0xf) != (int)gesture.Modifiers) return IntPtr.Zero;
        // A message already queued before mouse leave must not act on another app.
        if (KeyboardBindingsAllowed && PointerOverBindingSurface()) RunBindingAction(action);
        return IntPtr.Zero;
    }

    bool DispatchBinding(BindingGesture gesture)
    {
        if (!BindingsAllowed || !PointerOverBindingSurface() || _bindings.Find(gesture) is not { } action) return false;
        RunBindingAction(action);
        return true;
    }

    void RunBindingAction(string action)
    {
        if (_bindingActionTest != null) { _bindingActionTest(action); return; }
        var e = new RoutedEventArgs();
        switch (action)
        {
            case "open": ToggleOpen(); break;
            case "menu":
                CancelGrab(); SelectPanel(_panel == Panel.Menu ? Panel.None : Panel.Menu); UpdateSwitches(false); UpdateView(); break;
            case "hide":
                CancelGrab(); SelectPanel(Panel.None); _sentAway = true; _awayTimeout.Start(AwayDuration); UpdateView(); UpdateTargets(); SyncHoverHotkeys(); break;
            case "volume-up": AdjustBindingVolume(1); break;
            case "volume-down": AdjustBindingVolume(-1); break;
            case "app-volume-up": AdjustBindingAppVolume(_media.Source, 1); break;
            case "app-volume-down": AdjustBindingAppVolume(_media.Source, -1); break;
            case "page-previous": NavigateOpenPage(-1); break;
            case "page-next": NavigateOpenPage(1); break;
            case "play": Play_Click(this, e); break;
            case "previous": Prev_Click(this, e); break;
            case "next": Next_Click(this, e); break;
            case "translation": StartImageTranslation(); break;
            case "mixer": OpenMixer(); break;
            case "shelf": ShelfRow_Click(this, e); break;
            case "weather": ShowPanel(Panel.Weather); break;
            case "timer": TimerRow_Click(this, e); break;
            case "record": ShowPanel(Panel.Record); break;
            case "qr": ReadQr(false); break;
            case "qr-area": ReadQr(true); break;
            case "codex": ShowPanel(Panel.Codex); break;
            case "zapret": ZapretRow_Click(this, e); break;
            case "throne": ThroneRow_Click(this, e); break;
            case "settings": SettingsRow_Click(this, e); break;
            case "look": LookRow_Click(this, e); break;
            case "lyrics": LyricsRow_Click(this, e); break;
            case "update": UpdateRow_Click(this, e); break;
            case "bindings": BindingsRow_Click(this, e); break;
        }
    }

    void AdjustBindingVolume(int step)
    {
        if (_view == View.MediaBig && Settings.AppVolume && _audio.AdjustAppVolume(_media.Source, step * WheelVolumeStep, out float level))
            ShowPlayerVolume(level, false, true);
        else if (IsVolumeAtLimit(step > 0)) OvershootVolume(step > 0);
        else _audio.AdjustVolume(step * WheelVolumeStep);
    }

    void AdjustBindingAppVolume(string source, int step)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        bool changed;
        float level;
        if (_bindingAppVolumeTest != null) (changed, level) = _bindingAppVolumeTest(source, step * WheelVolumeStep);
        else changed = _audio.AdjustAppVolume(source, step * WheelVolumeStep, out level);
        // An unavailable application must never turn a per-app command into a
        // system-volume change. The ordinary wheel keeps its existing behavior.
        if (changed) ShowPlayerVolume(level, false, true);
    }

    void BindingsRow_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.Bindings);
        if (_hoverHotkeys != null)
        {
            _bindingUnavailable.Clear();
            foreach (var d in KeyBindings.Catalog)
                if (_bindings[d.Id] is { Input: BindingInput.Key } gesture && !_hoverHotkeys.CanRegister(gesture)) _bindingUnavailable.Add(d.Id);
        }
        RefreshBindingRows();
    }
    void BindingsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Settings);

    void RefreshBindingRows()
    {
        foreach (var definition in KeyBindings.Catalog)
        {
            var (button, _, value) = _bindingRows[definition.Id];
            value.Text = _recordingBinding == definition.Id ? "Запись…" : _bindings[definition.Id]?.Label ?? "Не назначено";
            value.Foreground = _recordingBinding == definition.Id ? _green : _bindingUnavailable.Contains(definition.Id) ? _orange : _dim;
            button.ToolTip = _bindingUnavailable.Contains(definition.Id) ? "Сочетание занято другой программой" : null;
            AutomationProperties.SetHelpText(button, value.Text);
        }
        bool recording = _recordingBinding != null;
        BindingCaptureActions.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        BindingReplace.Visibility = _pendingBinding != null ? Visibility.Visible : Visibility.Collapsed;
        BindingDefaults.Visibility = _pendingBinding == null ? Visibility.Visible : Visibility.Collapsed;
        BindingClear.Visibility = _pendingBinding == null ? Visibility.Visible : Visibility.Collapsed;
        if (!recording) BindingPrompt.Text = "Работают при наведении на остров";
    }

    void BeginBindingCapture(string action)
    {
        if (!KeyBindings.Catalog.Any(d => d.Id == action)) return;
        if (_recordingBinding == null) _bindingReturnFocus = IsActive ? IntPtr.Zero : Native.ForegroundWindow;
        _recordingBinding = action; _pendingBinding = null;
        CancelGrab(); _collapseTimeout.Cancel(); SyncHoverHotkeys();
        BindingPrompt.Text = "Нажми клавишу, кнопку мыши или прокрути колесо";
        RefreshBindingRows();
        _bindingRows[action].Button.BringIntoView();
        if (!App.SmokeTest) { Activate(); BindingCapture.Focus(); }
    }

    void EndBindingCapture(bool restoreFocus = true)
    {
        _recordingBinding = null; _pendingBinding = null;
        RefreshBindingRows();
        IntPtr previous = _bindingReturnFocus; _bindingReturnFocus = IntPtr.Zero;
        if (restoreFocus && IsActive && previous != IntPtr.Zero && previous != _hwnd) Native.BringForward(previous);
        SyncHoverHotkeys();
    }

    void Binding_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_recordingBinding == null) return;
        e.Handled = true;
        if (e.IsRepeat) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { EndBindingCapture(); return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
        OfferBinding(new(BindingInput.Key, Keyboard.Modifiers, key));
    }

    static bool BindingButtonSource(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is ButtonBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    void Binding_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!PointerOverBindingSurface()) return;
        ModifierKeys modifiers = Native.InputModifiers;
        if (_recordingBinding != null)
        {
            // The editor's own left-click buttons remain usable while recording.
            if (e.ChangedButton == MouseButton.Left && BindingButtonSource(e.OriginalSource as DependencyObject)) return;
            e.Handled = true; _bindingSuppressedUps.Add(e.ChangedButton);
            OfferBinding(new(BindingGesture.Mouse(e.ChangedButton), modifiers));
        }
        else if (e.ChangedButton == MouseButton.Left && modifiers != ModifierKeys.None
            && DispatchBinding(new(BindingInput.LeftClick, modifiers)))
        {
            e.Handled = true; _bindingSuppressedUps.Add(e.ChangedButton);
        }
    }

    void Binding_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || !PointerOverBindingSurface()) return;
        var gesture = new BindingGesture(e.Delta > 0 ? BindingInput.WheelUp : BindingInput.WheelDown, Native.InputModifiers);
        if (_recordingBinding != null) { e.Handled = true; OfferBinding(gesture); }
        else if (gesture.Modifiers != ModifierKeys.None && DispatchBinding(gesture)) e.Handled = true;
    }

    void OfferBinding(BindingGesture gesture)
    {
        if (_recordingBinding is not { } action) return;
        _pendingBinding = null;
        if (!gesture.Valid) { BindingPrompt.Text = "Эта клавиша зарезервирована. Выбери другую"; RefreshBindingRows(); return; }
        if (_bindings.Conflict(action, gesture) is { } conflict)
        {
            _pendingBinding = gesture;
            BindingPrompt.Text = "Уже назначено: " + KeyBindings.Catalog.First(d => d.Id == conflict).Title;
            RefreshBindingRows(); return;
        }
        CommitBinding(action, gesture, false);
    }

    bool CommitBinding(string action, BindingGesture? gesture, bool replace)
    {
        if (gesture is { Input: BindingInput.Key } && _hoverHotkeys != null && !_hoverHotkeys.CanRegister(gesture))
        {
            BindingPrompt.Text = "Сочетание занято системой или другой программой"; return false;
        }
        KeyBindings next = _bindings.Assign(action, gesture, replace);
        if (!SaveBindings(next)) return false;
        _bindingUnavailable.Remove(action);
        EndBindingCapture();
        return true;
    }

    bool SaveBindings(KeyBindings next)
    {
        if (!next.HasSettingsRoute) { BindingPrompt.Text = "Оставь сочетание для меню или настроек"; return false; }
        if (!_saveBindings(next)) { BindingPrompt.Text = "Не удалось сохранить сочетания. Попробуй ещё раз"; return false; }
        _bindings = next;
        _hoverHotkeys?.Disable(); _hotkeysActive = false; SyncHoverHotkeys();
        RefreshBindingRows(); return true;
    }

    void BindingCancel_Click(object sender, RoutedEventArgs e) => EndBindingCapture();
    void BindingReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingBinding is { } action && _pendingBinding is { } gesture) CommitBinding(action, gesture, true);
    }
    void BindingClear_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingBinding is { } action) CommitBinding(action, null, false);
    }
    void BindingDefaults_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingBinding is { } action)
        {
            var gesture = KeyBindings.Catalog.First(d => d.Id == action).Default;
            if (gesture != null) OfferBinding(gesture); else CommitBinding(action, null, false);
        }
    }
    void BindingsReset_Click(object sender, RoutedEventArgs e)
    {
        if (SaveBindings(new KeyBindings())) { _bindingUnavailable.Clear(); EndBindingCapture(); }
    }
}
