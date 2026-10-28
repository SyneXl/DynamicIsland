using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double TileGap = 4;
    const double TilePitch = ShelfTile.TileWidth + TileGap;
    const double ShelfEdgeFade = 16;
    const double TileBlur = 8;
    const double CarriedOpacity = 0.35;
    const double DropZoneOpacity = 0.5, HoverZoneOpacity = 0.25;
    static readonly TimeSpan DragLeaveDelay = TimeSpan.FromMilliseconds(150);

    readonly Shelf _shelf;
    readonly Dictionary<Shelf.Item, ShelfTile> _tiles = [];
    readonly Spring _shelfScroll = new(0, 260, 30);
    readonly DelayedAction _dragLeaveTimeout;
    int _shownShelfCount;
    bool _dragOver, _draggingOut, _pickingFiles;
    Panel _panelBeforeDrag;
    ShelfTile? _pressedTile;
    Point _tilePressPoint;
    Shelf.Item? _editingShelfNote;
    IntPtr _shelfTypedAfter;
    bool _discardShelfNote;
    Func<IDataObject?> _shelfClipboardReader = Clipboard.GetDataObject;
    Action<string> _shelfClipboardWriter = Clipboard.SetText;

    double ShelfOverflow => Math.Max(_shelf.Items.Count * TilePitch - TileGap
        - (ShelfView.Width - ShelfStrip.Margin.Left - ShelfStrip.Margin.Right), 0);

    void SyncShelf()
    {
        ShelfStatus.Text = "";
        SyncShelfStatus();
        foreach ((Shelf.Item item, ShelfTile tile) in _tiles.ToList())
        {
            if (_shelf.Items.Contains(item)) continue;
            _tiles.Remove(item);
            AnimateTileOut(tile);
        }
        foreach (Shelf.Item item in _shelf.Items)
        {
            if (!_tiles.ContainsKey(item)) AddTile(item);
            else if (item.Text != null) _tiles[item].ShowPicture();
        }

        int count = _shelf.Items.Count;
        BubbleShelfText.RollDown = MenuShelf.RollDown = count < _shownShelfCount;
        _shownShelfCount = count;
        if (count > 0) BubbleShelfText.Text = count.ToString();
        MenuShelf.Text = count > 0 ? count.ToString() : "";
        ShelfClear.SetVisible(count > 0);
        SyncShelfHint();
        ScrollShelf(0);
        UpdateTargets();
    }

    void AddTile(Shelf.Item item)
    {
        var tile = new ShelfTile(item) { Margin = new Thickness(0, 0, TileGap, 0) };
        tile.MouseLeftButtonDown += Tile_MouseLeftButtonDown;
        tile.MouseMove += Tile_MouseMove;
        tile.MouseLeftButtonUp += Tile_MouseLeftButtonUp;
        tile.Removed += removed => RunShelfAction(() => _shelf.Remove(removed.Item));
        _tiles[item] = tile;
        ShelfTiles.Children.Add(tile);
        if (ShelfView.IsVisible) AnimateTileIn(tile);
    }

    void OnShelfPictureLoaded(Shelf.Item item)
    {
        if (_tiles.TryGetValue(item, out ShelfTile? tile)) tile.ShowPicture();
    }

    static void AnimateTileIn(ShelfTile tile)
    {
        tile.RenderTransform.AnimateScale(new DoubleAnimation(0.5, 1, Ms(420)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 } });
        tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Ms(240)));
        tile.AnimateBlur(TileBlur, 0, Ms(320));
    }

    void AnimateTileOut(ShelfTile tile)
    {
        tile.IsHitTestVisible = false;
        bool seen = ShelfView.IsVisible;
        Duration time = Ms(seen ? 300 : 0);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        tile.RenderTransform.AnimateScale(new DoubleAnimation(0.5, time) { EasingFunction = ease });
        tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, Ms(seen ? 180 : 0)));
        tile.BeginAnimation(MarginProperty, new ThicknessAnimation(new Thickness(0), time) { EasingFunction = ease });
        var close = new DoubleAnimation(0, time) { EasingFunction = ease };
        close.Completed += (_, _) => ShelfTiles.Children.Remove(tile);
        tile.BeginAnimation(WidthProperty, close);
    }

    void SyncShelfHint()
    {
        bool empty = _shelf.Items.Count == 0;
        ShelfHintText.Text = _dragOver ? "Отпустите, чтобы положить" : "Файлы, текст и изображения";
        ShelfHintMore.Opacity = _dragOver ? 0 : 1;
        ShelfHint.IsHitTestVisible = empty && !_dragOver;
        ShelfHint.BeginAnimation(OpacityProperty, new DoubleAnimation(empty ? 1 : 0, Ms(200)));
        double zone = _dragOver ? DropZoneOpacity : empty && ShelfHint.IsMouseOver ? HoverZoneOpacity : 0;
        ShelfZone.BeginAnimation(OpacityProperty, new DoubleAnimation(zone, Ms(zone > 0 ? 150 : 300)));
    }

    void ShelfHint_MouseHover(object sender, MouseEventArgs e) => SyncShelfHint();

    void ShelfAdd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Положить на полку", Multiselect = true };
        bool picked;
        _pickingFiles = true;
        try { picked = dialog.ShowDialog(this) == true; }
        catch (Exception ex)
        {
            App.Log(ex);
            picked = false;
        }
        finally { _pickingFiles = false; }
        if (picked) RunShelfAction(() => _shelf.Add(dialog.FileNames));

        ResyncPointer(LongCollapseDelay);
        SyncShelfHint();
    }

    void ResyncPointer(TimeSpan collapseAfter)
    {
        Mouse.Synchronize();
        _hovered = Island.IsMouseOver;
        UpdateTargets();
        if (!_hovered && _panel != Panel.None) _collapseTimeout.Start(collapseAfter);
    }

    static Color EdgeFade(double past) => Color.FromArgb((byte)Math.Round(255 * (1 - Math.Clamp(past / ShelfEdgeFade, 0, 1))), 0, 0, 0);

    void ScrollShelf(int tiles)
    {
        _shelfScroll.Target = Math.Clamp(_shelfScroll.Target + tiles * TilePitch, 0, ShelfOverflow);
        StartShapeLoop();
    }

    void ApplyShelfScroll()
    {
        double scrolled = _shelfScroll.Value;
        ShelfMove.X = -scrolled;
        ShelfEdgeLeft.Color = EdgeFade(scrolled);
        ShelfEdgeRight.Color = EdgeFade(ShelfOverflow - scrolled);
    }

    void ShelfRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Shelf);

    void BeginShelfTyping(bool note)
    {
        if (_dragOver || App.SmokeTest || !IsLoaded) return;
        IntPtr foreground = Native.ForegroundWindow;
        if (foreground != _hwnd && _shelfTypedAfter == IntPtr.Zero) _shelfTypedAfter = foreground;
        Activate();
        Dispatcher.BeginInvoke(() =>
        {
            if (note && _view == View.ShelfNote) ShelfNoteEditor.Focus();
            else if (_view == View.Shelf) ShelfView.Focus();
        }, DispatcherPriority.Input);
    }

    void EndShelfTyping()
    {
        if (IsActive && _shelfTypedAfter != IntPtr.Zero) Native.BringForward(_shelfTypedAfter);
        _shelfTypedAfter = IntPtr.Zero;
    }

    void Shelf_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleShelfKey(e.Key, Keyboard.Modifiers)) e.Handled = true;
    }

    bool HandleShelfKey(Key key, ModifierKeys modifiers)
    {
        if (_panel == Panel.Shelf && key == Key.V && modifiers == ModifierKeys.Control)
        {
            PasteShelf(); return true;
        }
        if (_panel == Panel.ShelfNote && key == Key.Enter && modifiers == ModifierKeys.Control)
        {
            ShowPanel(Panel.Shelf); return true;
        }
        if (_panel == Panel.ShelfNote && key == Key.Escape)
        {
            _discardShelfNote = true; ShowPanel(Panel.Shelf); return true;
        }
        return false;
    }

    void ShelfPaste_Click(object sender, RoutedEventArgs e) => PasteShelf();

    void PasteShelf()
    {
        try
        {
            if (_shelfClipboardReader() is not { } data || !_shelf.Paste(data)) ShelfStatus.Text = "В буфере нет файлов, текста или изображения";
            else { ShelfStatus.Text = ""; ScrollShelfToEnd(); }
        }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { App.Log(ex); ShelfStatus.Text = ex is InvalidOperationException ? ex.Message : "Не удалось вставить. Попробуй ещё раз"; }
        SyncShelfStatus();
    }

    void SyncShelfStatus()
    {
        bool error = ShelfStatus.Text.Length > 0;
        ShelfStatus.SetVisible(error);
        ShelfView.Height = 136 + (error ? 20 : 0);
        UpdateTargets();
    }

    void ScrollShelfToEnd()
    {
        _shelfScroll.Target = ShelfOverflow; StartShapeLoop();
    }

    void ShelfNoteNew_Click(object sender, RoutedEventArgs e) => EditShelfNote(null);

    void EditShelfNote(Shelf.Item? item)
    {
        _editingShelfNote = item; _discardShelfNote = false;
        ShelfNoteEditor.Text = item?.Text ?? "";
        ShelfNoteError.Text = "";
        ShowPanel(Panel.ShelfNote);
    }

    bool CommitShelfNote()
    {
        if (_discardShelfNote) { _discardShelfNote = false; return true; }
        try
        {
            string text = ShelfNoteEditor.Text;
            if (_editingShelfNote != null) _shelf.UpdateText(_editingShelfNote, text);
            else if (!string.IsNullOrWhiteSpace(text)) _editingShelfNote = _shelf.AddText(text);
            ShelfNoteError.Text = "";
            return true;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { App.Log(ex); ShelfNoteError.Text = "Не удалось сохранить заметку"; return false; }
    }

    void ShelfNoteDone_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Shelf);

    void ShelfNoteCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ShelfNoteEditor.Text.Length > 0) _shelfClipboardWriter(ShelfNoteEditor.Text);
            ShelfNoteError.Text = "";
        }
        catch (COMException ex) { App.Log(ex); ShelfNoteError.Text = "Буфер занят. Попробуй ещё раз"; }
    }

    void ShelfClear_Click(object sender, RoutedEventArgs e) => RunShelfAction(_shelf.Clear);

    void RunShelfAction(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { App.Log(ex); ShelfStatus.Text = ex is InvalidOperationException ? ex.Message : "Не удалось сохранить полку"; SyncShelfStatus(); }
    }

    void Tile_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _pressedTile = (ShelfTile)sender;
        _tilePressPoint = e.GetPosition(this);
        _pressedTile.CaptureMouse();
    }

    void Tile_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedTile != sender || e.LeftButton != MouseButtonState.Pressed) return;
        Vector moved = e.GetPosition(this) - _tilePressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        ShelfTile tile = _pressedTile;
        _pressedTile = null;
        tile.ReleaseMouseCapture();
        DragOut(tile);
    }

    void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_pressedTile != sender) return;
        e.Handled = true;
        ShelfTile tile = _pressedTile;
        _pressedTile = null;
        tile.ReleaseMouseCapture();

        if (tile.Item.Text != null) { EditShelfNote(tile.Item); return; }

        try { Process.Start(new ProcessStartInfo(tile.Item.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            App.Log(ex);
            if (!Shelf.Exists(tile.Item.Path)) RunShelfAction(() => _shelf.Remove(tile.Item));
            return;
        }
        SelectPanel(Panel.None);
        UpdateView();
        UpdateTargets();
    }

    void DragOut(ShelfTile tile)
    {
        var data = tile.Item.Text is { } text ? new DataObject(DataFormats.UnicodeText, text)
            : new DataObject(DataFormats.FileDrop, new[] { tile.Item.Path });
        tile.BeginAnimation(OpacityProperty, new DoubleAnimation(CarriedOpacity, Ms(120)));
        DragDropEffects done;
        _draggingOut = true;
        try { done = DragDrop.DoDragDrop(tile, data, DragDropEffects.Copy | DragDropEffects.Link); }
        catch (Exception ex)
        {
            App.Log(ex);
            done = DragDropEffects.None;
        }
        finally { _draggingOut = false; }

        if (done != DragDropEffects.None && !tile.Item.Owned) RunShelfAction(() => _shelf.Remove(tile.Item));
        else tile.BeginAnimation(OpacityProperty, new DoubleAnimation(1, Ms(200)));
        ResyncPointer(CollapseDelay);
    }

    static DragDropEffects DropEffect(DragEventArgs e) =>
        e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : e.AllowedEffects & DragDropEffects.Link;

    void Root_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_draggingOut || _hiddenByFullscreen || _sentAway || !Shelf.CanPaste(e.Data))
        {
            e.Effects = DragDropEffects.None;
            return;
        }
        e.Effects = DropEffect(e);
        _dragLeaveTimeout.Cancel();
        _collapseTimeout.Cancel();
        if (_dragOver) return;

        _dragOver = true;
        _panelBeforeDrag = _panel;
        SelectPanel(Panel.Shelf);
        SyncShelfHint();
        UpdateView();
    }

    void Root_DragLeave(object sender, DragEventArgs e)
    {
        if (_dragOver) _dragLeaveTimeout.Start(DragLeaveDelay);
    }

    void Root_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (!_dragOver) return;
        _dragLeaveTimeout.Cancel();
        e.Effects = DropEffect(e);
        EndDragOver();
        try { _shelf.Paste(e.Data); ShelfStatus.Text = ""; }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or UnauthorizedAccessException or InvalidOperationException)
        { App.Log(ex); ShelfStatus.Text = "Не удалось добавить на полку"; }
        SyncShelfStatus();
        _collapseTimeout.Start(LongCollapseDelay);
    }

    void EndDragOver()
    {
        _dragOver = false;
        SyncShelfHint();
    }

    void OnDragLeft()
    {
        EndDragOver();
        ShowPanel(_panelBeforeDrag);
    }
}
