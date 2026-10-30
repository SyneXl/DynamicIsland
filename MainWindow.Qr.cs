using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public partial class MainWindow
{
    const double QrNoticeSeconds = 7, PickerHintSeconds = 600;
    static readonly TimeSpan MenuHideTime = TimeSpan.FromMilliseconds(600);
    static readonly TimeSpan PickerHideTime = TimeSpan.FromMilliseconds(150);
    static readonly TimeSpan FocusSettleTime = TimeSpan.FromSeconds(3);

    bool _readingQr, _pickingArea;
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

    async Task<Int32Rect?> PickAreaAsync(string title = "Обведите QR-код", string detail = "Esc или правая кнопка — отмена", Glyph glyph = Glyph.Qr)
    {
        var picker = new AreaPicker(QrReader.Desktop);
        _pickingArea = true;
        Notify(glyph, Brushes.White, title, detail, PickerHintSeconds, true);
        try
        {
            picker.ShowDialog();
        }
        finally
        {
            _pickingArea = false;
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
