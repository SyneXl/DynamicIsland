using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly YandexImageTranslator _imageTranslator = new();
    CancellationTokenSource? _imageTranslationCancellation;
    ImageTranslationWindow? _imageTranslationWindow;
    bool _imageTranslationBusy, _imageTranslationClosed;

    void PrepareImageTranslation()
    {
        SetImageTranslationStatus(ImageTranslationStatus.Text);
        PreviewKeyDown += (_, e) =>
        {
            if (_panel == Panel.ImageTranslation && e.Key == System.Windows.Input.Key.Escape)
            { e.Handled = true; CancelImageTranslation(); ShowPanel(Panel.None); }
        };
        Closed += (_, _) =>
        {
            _imageTranslationClosed = true; CancelImageTranslation();
            _imageTranslationWindow?.Close(); _imageTranslator.Dispose();
        };
    }
    void ImageTranslationRow_Click(object sender, RoutedEventArgs e) => StartImageTranslation();
    void ImageTranslationBack_Click(object sender, RoutedEventArgs e) { CancelImageTranslation(); ShowPanel(Panel.Menu); }
    void ImageTranslationAction_Click(object sender, RoutedEventArgs e)
    {
        if (_imageTranslationBusy) { CancelImageTranslation(); SetImageTranslationStatus("Перевод отменён"); }
        else StartImageTranslation();
    }
    void SetImageTranslationStatus(string text)
    {
        ImageTranslationStatus.Text = text;
        // Hidden views can retain an old DesiredSize until the next layout pass.
        // Measure the text directly so the very first error/status also fits.
        double Vertical(Thickness margin) => margin.Top + margin.Bottom;
        var bodyMargin = ImageTranslationContent.Margin;
        var statusMargin = ImageTranslationStatus.Margin;
        var header = (FrameworkElement)ImageTranslationContent.Children[0];
        var measured = new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
            ImageTranslationStatus.FlowDirection,
            new Typeface(ImageTranslationStatus.FontFamily, ImageTranslationStatus.FontStyle,
                ImageTranslationStatus.FontWeight, ImageTranslationStatus.FontStretch),
            ImageTranslationStatus.FontSize, ImageTranslationStatus.Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip)
        {
            MaxTextWidth = Math.Max(1, ImageTranslationView.Width - bodyMargin.Left - bodyMargin.Right - statusMargin.Left - statusMargin.Right),
        };
        double height = Math.Clamp(Math.Ceiling(Vertical(bodyMargin) + header.Height + Vertical(header.Margin)
            + Vertical(statusMargin) + measured.Height + ImageTranslationAction.Height + Vertical(ImageTranslationAction.Margin)), 120, 220);
        if (ImageTranslationView.Height == height) return;
        ImageTranslationView.Height = height;
        if (_view == View.ImageTranslation) UpdateTargets();
    }
    void CancelImageTranslation()
    {
        _imageTranslationCancellation?.Cancel(); _imageTranslationBusy = false;
        ImageTranslationAction.Content = "Выделить область";
    }

    async void StartImageTranslation()
    {
        if (_imageTranslationClosed || _pickingArea || _readingQr) return;
        if (_imageTranslationBusy) { ShowPanel(Panel.ImageTranslation); return; }
        _imageTranslationWindow?.Close(); _imageTranslationWindow = null;
        var operation = new CancellationTokenSource(); _imageTranslationCancellation = operation;
        _imageTranslationBusy = true;
        ImageTranslationAction.Content = "Отмена";
        try
        {
            ShowPanel(Panel.None);
            await Task.Delay(MenuHideTime, operation.Token);
            Int32Rect? selected = await PickAreaAsync("Обведите текст", "Снимок отправится в онлайн-сервис", Glyph.Lines);
            operation.Token.ThrowIfCancellationRequested();
            if (selected is not { } area) { ShowPanel(Panel.None); return; }
            BitmapSource original;
            // Do not capture the island's hint or its return animation.
            Hide();
            try
            {
                await Task.Delay(100, operation.Token);
                original = await Task.Run(() => ImageTranslationRenderer.Capture(area), operation.Token);
            }
            finally { if (!_imageTranslationClosed) Show(); }
            operation.Token.ThrowIfCancellationRequested();
            SetImageTranslationStatus("Подключаюсь…");
            ShowPanel(Panel.ImageTranslation);
            var progress = new Progress<string>(message =>
            {
                if (!_imageTranslationClosed && _imageTranslationCancellation == operation && !operation.IsCancellationRequested)
                    SetImageTranslationStatus(message);
            });
            byte[] png = await Task.Run(() => ImageTranslationRenderer.Png(original), operation.Token);
            var result = await _imageTranslator.TranslateAsync(png, original.PixelWidth, original.PixelHeight, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            BitmapSource translated = result.Language == "ru" ? original : ImageTranslationRenderer.Render(original, result);
            if (_imageTranslationClosed || _imageTranslationCancellation != operation) return;
            _imageTranslationBusy = false;
            ShowPanel(Panel.None);
            var window = new ImageTranslationWindow(this, area, original, translated, result, StartImageTranslation);
            _imageTranslationWindow = window;
            window.Closed += (_, _) => { if (_imageTranslationWindow == window) _imageTranslationWindow = null; };
            window.Show();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (_imageTranslationClosed || operation.IsCancellationRequested || _imageTranslationCancellation != operation) return;
            SetImageTranslationStatus(error is ImageTranslationException ? error.Message : "Не удалось перевести область. Попробуй ещё раз.");
            ShowPanel(Panel.ImageTranslation);
        }
        finally
        {
            if (_imageTranslationCancellation == operation)
            {
                _imageTranslationCancellation = null; _imageTranslationBusy = false;
                ImageTranslationAction.Content = "Выделить область";
            }
            operation.Dispose();
        }
    }
}
