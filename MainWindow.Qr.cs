using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public partial class MainWindow
{
    const double QrNoticeSeconds = 7, PickerHintSeconds = 600;
    static readonly TimeSpan MenuHideTime = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan PickerHideTime = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan FocusSettleTime = TimeSpan.FromSeconds(3);

    bool _readingQr, _pickingArea;
    bool _pickerObscured;
    Rect _pickerSelection = Rect.Empty;
    Func<AreaPicker> _makeAreaPicker = () => new AreaPicker(QrReader.Desktop);
    DateTime _focusSettlesAt;

    bool IsFocusUnsettled => _pickingArea || DateTime.UtcNow < _focusSettlesAt;

    void QrRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Qr);

    void QrScreen_Click(object sender, RoutedEventArgs e) => ReadQr(false);

    void QrArea_Click(object sender, RoutedEventArgs e) => ReadQr(true);

    async void ReadQr(bool pickArea)
    {
        if (_readingQr || _pickingArea || _imageTranslationBusy) return;

        _readingQr = true;
        ShowPanel(Panel.None);
        string? found = null;
        bool looked = false;
        try
        {
            await Task.Delay(MenuHideTime);
            Int32Rect? area = pickArea ? await PickAreaAsync() : QrReader.Desktop;
            if (area is { } chosen)
            {
                found = await Task.Run(() => QrReader.Read(chosen));
                looked = true;
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _readingQr = false;
        }
        if (looked) ShowQr(found, pickArea);
    }

    void PickerSelectionChanged(Rect selection)
    {
        _pickerSelection = selection;
        bool overlap = !selection.IsEmpty && IsLoaded && selection.IntersectsWith(MenuScreenBounds(Math.Max(_width.Value, MinPillSize), Math.Max(_height.Value, MinPillSize)));
        if (overlap == _pickerObscured) return;
        _pickerObscured = overlap;
        SyncGlass(false);
        BeginAnimation(OpacityProperty, new DoubleAnimation(overlap ? .12 : 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 0))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    async Task<Int32Rect?> PickAreaAsync(string title = "Обведите QR-код", string detail = "Esc или правая кнопка — отмена", Glyph glyph = Glyph.Qr, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        var picker = _makeAreaPicker();
        _pickingArea = true;
        bool wasClickThrough = false, inputChanged = false;
        picker.SelectionChanged += PickerSelectionChanged;
        try
        {
            wasClickThrough = Native.SetClickThrough(_hwnd, true); inputChanged = true;
            SyncGlass(false);
            Notify(glyph, Brushes.White, title, detail, PickerHintSeconds, true);
            using var cancel = cancellation.Register(() => Dispatcher.BeginInvoke(new Action(() => { if (picker.IsVisible) picker.Close(); })));
            cancellation.ThrowIfCancellationRequested();
            picker.ShowDialog();
        }
        finally
        {
            picker.SelectionChanged -= PickerSelectionChanged;
            PickerSelectionChanged(Rect.Empty);
            if (inputChanged) Native.SetClickThrough(_hwnd, wasClickThrough);
            _pickingArea = false;
            SyncGlass(false);
            _focusSettlesAt = DateTime.UtcNow + FocusSettleTime;
            EndTransient();
        }
        if (picker.Picked != null) await Task.Delay(PickerHideTime);
        return picker.Picked;
    }

    void ShowQr(string? found, bool inArea)
    {
        if (string.IsNullOrWhiteSpace(found))
        {
            Notify(Glyph.Qr, _dim, "QR-код не найден", inArea ? "В выбранной области его не видно" : "На экране его не видно", force: true);
            return;
        }

        string text = found.Trim();
        bool link = Uri.TryCreate(text, UriKind.Absolute, out Uri? address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps);
        bool copied = Copy(text);
        string title = link ? copied ? "Скопировано · клик откроет" : "Ссылка · клик откроет" : copied ? "Текст скопирован" : "QR-код";
        Notify(Glyph.Qr, Brushes.White, title, text.ReplaceLineEndings(" "), QrNoticeSeconds, true, open: link ? () => OpenLink(address!) : null);
    }

    static bool Copy(string text)
    {
        try
        {
            Clipboard.SetDataObject(text, true);
            return true;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            return false;
        }
    }

    static void OpenLink(Uri address)
    {
        try { Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }
}
