using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double RecordNoticeSeconds = 5;
    const double RecordBeatLow = 0.35;
    const double PauseDiscAlpha = 0.18, ResumeDiscAlpha = 0.25;
    const double Gigabyte = 1 << 30, Megabyte = 1 << 20;
    static readonly TimeSpan RecordBeat = TimeSpan.FromMilliseconds(900);

    readonly ObsService _obs = new();
    readonly SolidColorBrush _recordTint, _recordPauseDisc = new(Colors.White.WithAlpha(PauseDiscAlpha));
    bool _recordPauseShown;
    int _shownRecordSeconds = -1;

    void ApplyRecordTint()
    {
        RecordDot.Fill = BubbleRecordDot.Fill = RecordBigDot.Fill = _recordTint;
        RecordText.Foreground = BubbleRecordText.Foreground = MenuRecord.Foreground = _recordTint;
        RecordPauseDisc.Fill = _recordPauseDisc;
        foreach (FrameworkElement icon in new FrameworkElement[] { RecordPauseIcon, RecordResumeIcon })
        {
            icon.RenderTransformOrigin = new Point(0.5, 0.5);
            icon.RenderTransform = new ScaleTransform(1, 1);
        }
    }

    void OnObsChanged()
    {
        SyncRecord();
        UpdateView();
        UpdateTargets();
    }

    void SyncRecord()
    {
        bool paused = _obs.Recording && _obs.Paused;
        if (paused != _recordPauseShown)
        {
            _recordPauseShown = paused;
            SwapIcons(paused ? RecordPauseIcon : RecordResumeIcon, paused ? RecordResumeIcon : RecordPauseIcon, RecordBigView.IsVisible);
            SyncRecordTint(Ms(300));
            Puddle.SetTint(RecordPauseButton, paused ? _orange.Color : Colors.White);
            Color disc = paused ? _orange.Color.WithAlpha(ResumeDiscAlpha) : Colors.White.WithAlpha(PauseDiscAlpha);
            _recordPauseDisc.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(disc, Ms(300)));
        }
        RecordBigLabel.Text = paused ? "OBS · Пауза" : "OBS · Запись";
        RecordBigLabel.Foreground = paused ? _orange : Brushes.White;
        RecordText.Opacity = BubbleRecordText.Opacity = RecordBigTime.Opacity = paused ? PausedOpacity : 1;
        RecordInfo.Text = DescribeRecording();
        MenuRecordIcon.Fill = _obs.Recording ? _recordTint : _dim;
        SyncRecordSetPage();
        _shownRecordSeconds = -1;
        UpdateRecord();
    }

    void SyncRecordSetPage()
    {
        (string caption, string title) = _obs.State switch
        {
            ObsService.Link.Ready => (_obs.Scene.Length > 0 ? $"OBS · Сцена «{_obs.Scene}»" : "OBS", _obs.Busy ? "Запускается…" : "Не пишет"),
            ObsService.Link.Disabled => ("Инструменты → Настройки сервера WebSocket", "Включите сервер в OBS"),
            ObsService.Link.Refused => ("Пароль сервера WebSocket не подошёл", "OBS не пустил"),
            _ => ("Остров подключится сам", "OBS не запущен"),
        };
        RecordSetCaption.Text = caption;
        RecordSetTitle.Text = title;
        RecordStart.SetVisible(_obs.IsReady);
        RecordStart.IsEnabled = !_obs.Busy;
        RecordStart.Opacity = _obs.Busy ? PausedOpacity : 1;
        RecordSetWords.HorizontalAlignment = _obs.IsReady ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        RecordSetCaption.TextAlignment = RecordSetTitle.TextAlignment = _obs.IsReady ? TextAlignment.Left : TextAlignment.Center;
        FitRecordSetPage();
    }

    void FitRecordSetPage()
    {
        double widest = PillShapes[View.RecordSet].Width;
        RecordSetWords.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double hugging = Math.Ceiling(RecordSetWords.DesiredSize.Width / 2) * 2;
        double width = _obs.IsReady ? widest : Math.Min(hugging, widest);
        if (width == RecordSetView.Width) return;

        RecordSetView.Width = width;
        if (_view == View.RecordSet) _morphing = true;
    }

    string DescribeRecording()
    {
        string size = _obs.Bytes >= Gigabyte ? (_obs.Bytes / Gigabyte).ToString("0.0") + " ГБ"
            : _obs.Bytes > 0 ? Math.Round(_obs.Bytes / Megabyte) + " МБ" : "";
        string scene = _obs.Scene.Length > 0 ? $"«{_obs.Scene}»" : "";
        return string.Join(" · ", new[] { size, scene }.Where(part => part.Length > 0));
    }

    void UpdateRecord()
    {
        if (!_obs.Recording)
        {
            MenuRecord.Text = "";
            return;
        }
        int seconds = (int)_obs.Elapsed.TotalSeconds;
        if (seconds == _shownRecordSeconds) return;
        _shownRecordSeconds = seconds;
        RecordText.Text = BubbleRecordText.Text = RecordBigTime.Text = MenuRecord.Text = DescribeElapsed(TimeSpan.FromSeconds(seconds));
        RecordInfo.Text = DescribeRecording();
        if (!_obs.Paused) BeatRecordDots();
    }

    static string DescribeElapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    void BeatRecordDots()
    {
        if (IsQuietAppearance) return;
        var beat = new DoubleAnimationUsingKeyFrames { Duration = RecordBeat };
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(RecordBeatLow, KeyTime.FromPercent(0.45), new SineEase { EasingMode = EasingMode.EaseInOut }));
        beat.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new SineEase { EasingMode = EasingMode.EaseInOut }));
        foreach (UIElement dot in new UIElement[] { RecordDot, BubbleRecordDot, RecordBigDot })
            if (dot.IsVisible) dot.BeginAnimation(OpacityProperty, beat);
    }

    void SyncRecordTint(Duration time) => _recordTint.BeginAnimation(SolidColorBrush.ColorProperty,
        new ColorAnimation(IsQuietAppearance ? QuietTimerColor : (_obs.Paused ? _orange : _red).Color, time));

    void OnRecordingSaved(string path, TimeSpan length)
    {
        if (_panel is Panel.Record) _panel = Panel.None;
        Notify(Glyph.Folder, _red, "Запись сохранена · " + DescribeElapsed(length), Path.GetFileName(path), RecordNoticeSeconds,
            open: () => ShowInFolder(path));
    }

    static void ShowInFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.Log(ex); }
    }

    void RecordRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Record);

    void RecordStart_Click(object sender, RoutedEventArgs e) => _ = _obs.StartRecordAsync();

    void RecordStop_Click(object sender, RoutedEventArgs e) => _ = _obs.StopRecordAsync();

    void RecordPause_Click(object sender, RoutedEventArgs e) => _ = _obs.TogglePauseAsync();
}
