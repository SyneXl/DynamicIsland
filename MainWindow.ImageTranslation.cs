using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Controls;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly YandexImageTranslator _imageTranslator = new();
    readonly GoogleImageTranslator _googleImageTranslator = new();
    ImageTranslationProvider _imageTranslationProvider = Settings.ImageTranslationProvider;
    Func<ImageTranslationProvider, bool> _saveImageTranslationProvider = Settings.TrySetImageTranslationProvider;
    CancellationTokenSource? _imageTranslationCancellation;
    ImageTranslationWindow? _imageTranslationWindow;
    bool _imageTranslationBusy, _imageTranslationClosed;

    void PrepareImageTranslation()
    {
        RefreshImageTranslationSettings();
        PreviewKeyDown += (_, e) =>
        {
            if (_panel == Panel.ImageTranslation && e.Key == System.Windows.Input.Key.Escape)
            { e.Handled = true; CancelImageTranslation(); ShowPanel(Panel.None); }
        };
        Closed += (_, _) =>
        {
            _imageTranslationClosed = true; CancelImageTranslation();
            _imageTranslationWindow?.Close(); _imageTranslator.Dispose(); _googleImageTranslator.Dispose();
        };
    }
    void ImageTranslationRow_Click(object sender, RoutedEventArgs e)
    {
        if (!_imageTranslationBusy) SetImageTranslationStatus("");
        RefreshImageTranslationSettings(); ShowPanel(Panel.ImageTranslation);
    }
    void ImageTranslationProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_imageTranslationBusy || sender is not Button button || !Enum.TryParse<ImageTranslationProvider>(button.Tag?.ToString(), out var provider)
            || !Enum.IsDefined(provider) || provider == _imageTranslationProvider) return;
        if (!_saveImageTranslationProvider(provider)) { SetImageTranslationStatus("Не удалось сохранить переводчик. Попробуй ещё раз."); return; }
        _imageTranslationProvider = provider; SetImageTranslationStatus(""); RefreshImageTranslationSettings();
    }
    IImageTranslator SelectedImageTranslator => _imageTranslationProvider == ImageTranslationProvider.Google ? _googleImageTranslator : _imageTranslator;
    void RefreshImageTranslationSettings()
    {
        ImageTranslationProviders.Visibility = _imageTranslationBusy ? Visibility.Collapsed : Visibility.Visible;
        foreach (Button button in ImageTranslationProviders.Children)
        {
            bool selected = button.Tag?.ToString() == _imageTranslationProvider.ToString();
            button.Foreground = selected ? Brushes.White : _dim;
            button.Background = new SolidColorBrush(selected ? Color.FromRgb(38, 38, 38) : Color.FromRgb(16, 16, 16));
            button.IsEnabled = !_imageTranslationBusy;
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "Выбран" : "");
        }
        ImageTranslationAction.Content = _imageTranslationBusy ? "Отмена" : "Выделить область";
        SetImageTranslationStatus(ImageTranslationStatus.Text);
    }
    void ImageTranslationBack_Click(object sender, RoutedEventArgs e) { CancelImageTranslation(); ShowPanel(Panel.Menu); }
    void ImageTranslationAction_Click(object sender, RoutedEventArgs e)
    {
        if (_imageTranslationBusy) { CancelImageTranslation(); SetImageTranslationStatus("Перевод отменён"); }
        else StartImageTranslation();
    }
    void SetImageTranslationStatus(string text)
    {
        ImageTranslationStatus.Text = text;
        ImageTranslationStatus.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
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
        double providerHeight = ImageTranslationProviders.Visibility == Visibility.Visible ? ImageTranslationProviders.Height + Vertical(ImageTranslationProviders.Margin) : 0;
        double statusHeight = ImageTranslationStatus.Visibility == Visibility.Visible ? Vertical(statusMargin) + measured.Height : 0;
        double height = Math.Clamp(Math.Ceiling(Vertical(bodyMargin) + header.Height + Vertical(header.Margin)
            + providerHeight + statusHeight + ImageTranslationAction.Height + Vertical(ImageTranslationAction.Margin)), 120, 260);
        if (ImageTranslationView.Height == height) return;
        ImageTranslationView.Height = height;
        if (_view == View.ImageTranslation) UpdateTargets();
    }
    void CancelImageTranslation()
    {
        _imageTranslationCancellation?.Cancel(); _imageTranslationBusy = false;
        if (_imageTranslationWindow is { IsProcessing: true }) _imageTranslationWindow.Close();
        RefreshImageTranslationSettings();
    }

    ImageTranslationWindow ShowTranslationRegion(Int32Rect area, BitmapSource original, CancellationTokenSource operation)
    {
        var window = new ImageTranslationWindow(this, area, original, original, new("", []), StartImageTranslation, processing: true);
        _imageTranslationWindow = window;
        window.Closed += (_, _) =>
        {
            if (_imageTranslationWindow == window) { _imageTranslationWindow = null; PickerSelectionChanged(Rect.Empty); }
            if (_imageTranslationCancellation == operation && _imageTranslationBusy) operation.Cancel();
        };
        window.Show();
        PickerSelectionChanged(new Rect(area.X, area.Y, area.Width, area.Height));
        return window;
    }

    async void StartImageTranslation()
    {
        if (_imageTranslationClosed || _pickingArea || _readingQr) return;
        if (_imageTranslationBusy) { ShowPanel(Panel.ImageTranslation); return; }
        _imageTranslationWindow?.Close(); _imageTranslationWindow = null;
        var operation = new CancellationTokenSource(); _imageTranslationCancellation = operation;
        IImageTranslator translator = SelectedImageTranslator;
        _imageTranslationBusy = true;
        RefreshImageTranslationSettings();
        try
        {
            ShowPanel(Panel.None);
            await Task.Delay(MenuHideTime, operation.Token);
            Int32Rect? selected = await PickAreaAsync("Обведите текст", "Снимок отправится в онлайн-сервис", Glyph.Lines, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (selected is not { } area) { ShowPanel(Panel.None); return; }
            BitmapSource original;
            // Do not capture the island's hint or its return animation.
            _glass.Hide(); Hide();
            try
            {
                await Task.Delay(100, operation.Token);
                original = await Task.Run(() => ImageTranslationRenderer.Capture(area), operation.Token);
            }
            finally { if (!_imageTranslationClosed) { Show(); SyncGlass(false); } }
            operation.Token.ThrowIfCancellationRequested();
            var window = ShowTranslationRegion(area, original, operation);
            SetImageTranslationStatus("Подключаюсь…");
            ShowPanel(Panel.ImageTranslation);
            var progress = new Progress<string>(message =>
            {
                if (!_imageTranslationClosed && _imageTranslationCancellation == operation && !operation.IsCancellationRequested)
                    SetImageTranslationStatus(message);
            });
            byte[] png = await Task.Run(() => ImageTranslationRenderer.Png(original), operation.Token);
            var result = await translator.TranslateAsync(png, original.PixelWidth, original.PixelHeight, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            BitmapSource translated = result.Language == "ru" ? original : ImageTranslationRenderer.Render(original, result);
            if (_imageTranslationClosed || _imageTranslationCancellation != operation) return;
            _imageTranslationBusy = false;
            ShowPanel(Panel.None);
            if (_imageTranslationWindow == window) window.Complete(translated, result);
        }
        catch (OperationCanceledException)
        {
            if (!_imageTranslationClosed && _imageTranslationCancellation == operation && _panel == Panel.ImageTranslation)
                SetImageTranslationStatus("Перевод отменён");
        }
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
                if (_imageTranslationWindow is { IsProcessing: true }) _imageTranslationWindow.Close();
                RefreshImageTranslationSettings();
            }
            operation.Dispose();
        }
    }
}
