using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static DynamicIsland.Motion;

namespace DynamicIsland;

public partial class MainWindow
{
    const double UpdatePagePadding = 14, UpdateNotesWheelStep = 54;
    const int MegabyteShift = 20;
    const double LookHeight = 430, LookTilesHeight = 72, LookWheelStep = 80;
    const double LookEdgeZone = 70, LookEdgeScale = 0.8;
    const int LargestScale = 130, ScaleStep = 5;
    const int LargestGap = 24, GapStep = 2;

    Border? _openLookTiles;
    readonly Spring _lookScroll = new(0, 260, 30);
    readonly Spring _updateNotesScroll = new(0, 260, 30);
    ReleaseNotesSnapshot? _shownReleaseNotes;
    bool _releaseNotesLoadQueued;

    void SettingsRow_Click(object sender, RoutedEventArgs e) => ShowPanelAndCheckUpdate(Panel.Settings);

    void UpdateRow_Click(object sender, RoutedEventArgs e) => ShowPanelAndCheckUpdate(Panel.Update);

    void UpdateBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Settings);
    async void UpdateWhatsNew_Click(object sender, RoutedEventArgs e)
    {
        _updater.ReloadSavedReleaseNotes();
        RefreshReleaseNotesPage(); ShowPanel(Panel.ReleaseNotes);
        await _updater.LoadReleaseNotesAsync();
    }
    void ReleaseNotesBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Update);
    async void ReleaseNotesRefresh_Click(object sender, RoutedEventArgs e) => await _updater.LoadReleaseNotesAsync(true);

    void ShowPanelAndCheckUpdate(Panel panel)
    {
        ShowPanel(panel);
        if (!App.SmokeTest) _ = _updater.CheckAsync();
    }

    void LookRow_Click(object sender, RoutedEventArgs e)
    {
        _lookScroll.Snap(0);
        ShowPanel(Panel.Look);
    }

    void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Menu);

    void Exit_Click(object sender, RoutedEventArgs e) => Exit();

    void Autostart_Click(object sender, RoutedEventArgs e)
    {
        try { Autostart.Set(!Autostart.Enabled); }
        catch (Exception ex) { App.Log(ex); }
        UpdateSwitches(true);
    }

    void Lyrics_Click(object sender, RoutedEventArgs e)
    {
        Settings.Lyrics = !Settings.Lyrics;
        UpdateSwitches(true);
        TrackLyrics();
    }

    void LyricsRow_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Lyrics);

    void LyricsBack_Click(object sender, RoutedEventArgs e) => ShowPanel(Panel.Settings);

    void Source_Click(object sender, RoutedEventArgs e)
    {
        var picked = (LyricSource)(1 << SourceChips.Children.IndexOf((UIElement)sender));
        LyricSource left = Settings.LyricSources ^ picked;
        if (left != 0)
        {
            Settings.LyricSources = left;
            TrackLyrics();
        }
        UpdateSwitches(true);
    }

    void LyricEffects_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricEffects = !Settings.LyricEffects;
        UpdateSwitches(true);
        RebuildPlayerLyrics();
    }

    void LyricParting_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricParting = !Settings.LyricParting;
        UpdateSwitches(true);
        StirLyrics();
    }

    void Rim_Click(object sender, RoutedEventArgs e)
    {
        Settings.Rim = !Settings.Rim;
        UpdateSwitches(true);
        SyncRim();
    }

    void AppVolume_Click(object sender, RoutedEventArgs e)
    {
        Settings.AppVolume = !Settings.AppVolume;
        UpdateSwitches(true);
    }

    void Network_Click(object sender, RoutedEventArgs e)
    {
        Settings.Network = !Settings.Network;
        UpdateSwitches(true);
    }

    void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        Settings.HideFullscreen = !Settings.HideFullscreen;
        UpdateSwitches(true);
        CheckFullscreen();
    }

    void UpdateSwitches(bool animate)
    {
        LyricsState.Text = Settings.Lyrics ? "Вкл" : "Выкл";
        LyricsSwitch.Set(Settings.Lyrics, animate);
        LyricEffectsSwitch.Set(Settings.LyricEffects, animate);
        LyricPartingSwitch.Set(Settings.LyricParting, animate);
        RimSwitch.Set(Settings.Rim, animate);
        AppVolumeSwitch.Set(Settings.AppVolume, animate);
        NetworkSwitch.Set(Settings.Network, animate);
        WeatherSwitch.Set(Settings.Weather, animate);
        FullscreenSwitch.Set(Settings.HideFullscreen, animate);
        CinemaSwitch.Set(Settings.Cinema, animate);
        AutostartSwitch.Set(Autostart.Enabled, animate);

        for (int i = 0; i < SourceChips.Children.Count; i++)
            ((ToggleButton)SourceChips.Children[i]).IsChecked = Settings.LyricSources.HasFlag((LyricSource)(1 << i));
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updater.State != Updater.Stage.Available) await _updater.CheckAsync(true);
        else if (await _updater.InstallAsync()) Exit();
    }

    void RefreshUpdatePage()
    {
        ObserveUpdateAnnouncement();
        Updater.Stage stage = _updater.State;
        bool loading = stage == Updater.Stage.Loading, found = loading || stage == Updater.Stage.Available;
        Version version = found ? _updater.LatestVersion! : Updater.CurrentVersion;

        UpdateText.Foreground = found ? _orange : _dim;
        UpdateText.Text = loading ? _updater.Percent + "%" : "v" + version;

        UpdateVersion.Text = version.ToString();
        UpdateFrom.Text = stage switch
        {
            Updater.Stage.Checking => "проверяю…",
            Updater.Stage.Latest => "последняя версия",
            Updater.Stage.Failed => "",
            Updater.Stage.NoRelease => "пока без релизов",
            _ => found ? "вместо " + Updater.CurrentVersion : "",
        };
        UpdateError.Text = _updater.Error;
        UpdateError.SetVisible(stage == Updater.Stage.Failed && _updater.Error.Length > 0);
        RefreshReleaseNotesPage();
        if (!App.SmokeTest && _view == View.ReleaseNotes && _updater.NeedsReleaseNotesLoad && !_releaseNotesLoadQueued)
        {
            // A version check may finish after this child page was opened.
            // Queue outside Changed/finally so a completed old task can be replaced.
            _releaseNotesLoadQueued = true;
            _ = Dispatcher.InvokeAsync(() =>
            {
                _releaseNotesLoadQueued = false;
                if (_view == View.ReleaseNotes && _updater.NeedsReleaseNotesLoad) _ = _updater.LoadReleaseNotesAsync();
            });
        }

        UpdateButton.SetVisible(!loading);
        UpdateButton.IsEnabled = stage != Updater.Stage.Checking;
        UpdateLoad.SetVisible(loading);
        UpdateButton.Content = stage switch
        {
            Updater.Stage.Available => "Обновить и перезапустить",
            Updater.Stage.Checking => "Проверяю…",
            Updater.Stage.Failed => "Попробовать ещё раз",
            _ => "Проверить ещё раз",
        };
        if (found) UpdateButton.Background = _orange;
        else UpdateButton.ClearValue(BackgroundProperty);
        UpdateButton.Foreground = found ? Brushes.Black : Brushes.White;

        if (loading) ShowUpdateProgress();
        FitUpdatePage();
        UpdateView();
    }

    void RefreshReleaseNotesPage()
    {
        var notes = _updater.ReleaseNotes;
        if (!ReferenceEquals(notes, _shownReleaseNotes))
        {
            _shownReleaseNotes = notes;
            _updateNotesScroll.Snap(0); UpdateNotesText.Inlines.Clear();
            foreach (var release in notes?.Releases ?? [])
            {
                if (UpdateNotesText.Inlines.Count > 0) { UpdateNotesText.Inlines.Add(new LineBreak()); UpdateNotesText.Inlines.Add(new LineBreak()); }
                UpdateNotesText.Inlines.Add(new Run(release.Version) { FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
                foreach (string line in release.Lines.Length > 0 ? release.Lines : ["Описание не добавлено"])
                {
                    UpdateNotesText.Inlines.Add(new LineBreak()); UpdateNotesText.Inlines.Add(new Run("•  " + line));
                }
            }
        }
        UpdateNotes.SetVisible(notes != null);
        ReleaseNotesRange.Text = notes == null ? "" : notes.From == null ? notes.To : notes.From + " → " + notes.To;
        ReleaseNotesRange.SetVisible(notes != null);
        ReleaseNotesMessage.Text = _updater.NotesLoading ? "Загружаю изменения…" : _updater.NotesError.Length > 0 ? _updater.NotesError
            : notes == null ? "Список изменений пока недоступен" : !notes.Complete ? "Не все описания удалось загрузить" : "";
        ReleaseNotesMessage.SetVisible(ReleaseNotesMessage.Text.Length > 0);
        ReleaseNotesRefresh.IsEnabled = !_updater.NotesLoading;
        ReleaseNotesBody.Measure(new Size(ReleaseNotesPage.Width, double.PositiveInfinity));
        double height = Math.Clamp(Math.Ceiling(ReleaseNotesBody.DesiredSize.Height) + UpdatePagePadding, 150, 460);
        if (ReleaseNotesPage.Height != height) { ReleaseNotesPage.Height = height; if (_view == View.ReleaseNotes) UpdateTargets(); }
    }

    void ShowUpdateProgress()
    {
        UpdateBytes.Text = _updater.TotalBytes > 0 ? $"{_updater.DownloadedBytes >> MegabyteShift} из {_updater.TotalBytes >> MegabyteShift} МБ" : $"{_updater.DownloadedBytes >> MegabyteShift} МБ";
        LoadingText.Text = _updater.Percent + "%";
        var fill = new DoubleAnimation(_updater.Percent / 100.0, Ms(200));
        UpdateRing.BeginAnimation(Ring.ProgressProperty, fill);
        LoadingRing.BeginAnimation(Ring.ProgressProperty, fill);
    }

    void FitUpdatePage()
    {
        UpdateBody.Measure(new Size(UpdatePage.Width, double.PositiveInfinity));
        double height = Math.Ceiling(UpdateBody.DesiredSize.Height) + UpdatePagePadding;
        if (height == UpdatePage.Height) return;

        UpdatePage.Height = height;
        if (_view == View.Update) UpdateTargets();
    }

    double UpdateNotesOverflow => Math.Max(UpdateNotesText.ActualHeight - UpdateNotesBox.ActualHeight, 0);

    void ScrollUpdateNotes(int notches)
    {
        _updateNotesScroll.Target = Math.Clamp(_updateNotesScroll.Target + notches * UpdateNotesWheelStep, 0, UpdateNotesOverflow);
        StartShapeLoop();
    }

    void ApplyUpdateNotesScroll()
    {
        double scrolled = Math.Min(_updateNotesScroll.Value, UpdateNotesOverflow);
        UpdateNotesMove.Y = -scrolled;
        UpdateNotesEdgeTop.Color = EdgeFade(scrolled);
        UpdateNotesEdgeBottom.Color = EdgeFade(UpdateNotesOverflow - scrolled);
    }

    void SizeSlider_Changed(object? sender, EventArgs e) => SetScale((int)SizeSlider.Value);

    void GapSlider_Changed(object? sender, EventArgs e) => SetGap((int)GapSlider.Value);

    void SetScale(int percent)
    {
        int was = Settings.Scale;
        Settings.Scale = percent;
        if (Settings.Scale != was) ApplyLook();
    }

    void SetGap(int px)
    {
        int was = Settings.Gap;
        Settings.Gap = px;
        if (Settings.Gap != was) ApplyLook();
    }

    void Dots_Click(object sender, RoutedEventArgs e)
    {
        Eq.Dots = Settings.Dots = DotsSegments.PickUnderPointer() == 1;
        RefreshLookPage();
    }

    void Glass_Click(object sender, RoutedEventArgs e)
    {
        Settings.Glass = !Settings.Glass;
        RefreshLookPage();
        SyncGlass(true);
    }

    void SeekStyle_Click(object sender, RoutedEventArgs e)
    {
        Settings.LineBar = SeekStyleSegments.PickUnderPointer() == 1;
        RefreshLookPage();
        SyncSeekStyle(true);
    }

    void SeekHover_Click(object sender, RoutedEventArgs e)
    {
        Settings.SeekHover = (SeekHover)SeekHoverSegments.PickUnderPointer();
        RefreshLookPage();
    }

    void BackdropRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(BackdropTiles);

    void LyricChangeRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(LyricChangeTiles);

    void HoverRow_Click(object sender, RoutedEventArgs e) => ToggleLookTiles(HoverTiles);

    void BackdropTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.Backdrop = (Backdrop)BackdropStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
        SyncBackdrop();
    }

    void LyricChangeTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.LyricChange = (LyricChange)LyricChangeStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
    }

    void HoverTile_Click(object sender, RoutedEventArgs e)
    {
        Settings.Hover = (Hover)HoverStrip.Children.IndexOf((UIElement)sender);
        RefreshLookPage();
    }

    void ToggleLookTiles(Border tiles)
    {
        _openLookTiles = tiles == _openLookTiles ? null : tiles;
        SlideLookTiles(BackdropTiles, BackdropChevron);
        SlideLookTiles(LyricChangeTiles, LyricChangeChevron);
        SlideLookTiles(HoverTiles, HoverChevron);
    }

    void SlideLookTiles(Border tiles, Icon chevron)
    {
        bool open = tiles == _openLookTiles;
        if (!open && tiles.Visibility != Visibility.Visible) return;

        tiles.Visibility = Visibility.Visible;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new DoubleAnimation(open ? LookTilesHeight : 0, Ms(open ? 320 : 240)) { EasingFunction = ease };
        slide.Completed += (_, _) =>
        {
            if (tiles != _openLookTiles) tiles.Visibility = Visibility.Collapsed;
        };
        tiles.BeginAnimation(HeightProperty, slide);
        tiles.Child.BeginAnimation(OpacityProperty, new DoubleAnimation(open ? 1 : 0, Ms(open ? 260 : 160)));
        chevron.RenderTransform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(open ? 90 : 0, Ms(240)) { EasingFunction = ease });
    }

    double LookOverflow => Math.Max(LookRows.ActualHeight + LookRows.Margin.Top + LookRows.Margin.Bottom - LookView.Height, 0);

    void LookRows_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double reveal = _openLookTiles == null ? 0
            : _openLookTiles.TranslatePoint(new Point(0, _openLookTiles.ActualHeight), LookRows).Y + LookRows.Margin.Top + LookRows.Margin.Bottom - LookView.Height;
        _lookScroll.Target = Math.Clamp(Math.Max(_lookScroll.Target, reveal), 0, LookOverflow);
        StartShapeLoop();
    }

    void ScrollLook(int notches)
    {
        _lookScroll.Target = Math.Clamp(_lookScroll.Target + notches * LookWheelStep, 0, LookOverflow);
        StartShapeLoop();
    }

    void ShrinkLookEdges(double scrolled)
    {
        double top = Math.Clamp(scrolled / LookEdgeZone, 0, 1), bottom = Math.Clamp((LookOverflow - scrolled) / LookEdgeZone, 0, 1);
        foreach (FrameworkElement row in LookRows.Children)
        {
            double middle = VisualTreeHelper.GetOffset(row).Y + LookRows.Margin.Top + row.ActualHeight / 2 - scrolled;
            double fromTop = Math.Clamp(middle / LookEdgeZone, 0, 1), fromBottom = Math.Clamp((LookView.Height - middle) / LookEdgeZone, 0, 1);
            double shrink = Math.Max(top * Smooth(1 - fromTop), bottom * Smooth(1 - fromBottom));

            if (row.RenderTransform is not ScaleTransform size)
            {
                row.RenderTransform = size = new ScaleTransform();
                row.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            size.ScaleX = size.ScaleY = 1 - (1 - LookEdgeScale) * shrink;
        }
    }

    static double Smooth(double t) => t * t * (3 - 2 * t);

    void ApplyLookScroll()
    {
        double scrolled = _lookScroll.Value;
        LookEdgeTop.Color = EdgeFade(scrolled);
        LookEdgeBottom.Color = EdgeFade(LookOverflow - scrolled);
        if (LookView.IsVisible) ShrinkLookEdges(scrolled);
        if (LookMove.Y == -scrolled) return;

        LookMove.Y = -scrolled;
        if (_view == View.Look) LookRows.FollowPointer();
    }

    void Accent_Click(object sender, RoutedEventArgs e)
    {
        Settings.Accent = ((RadioButton)sender).Background is SolidColorBrush picked ? picked.Color : null;
        RefreshLookPage();
        SyncAccent();
        SyncRim();
    }

    void RefreshLookPage()
    {
        SizeText.Text = Settings.Scale + "%";
        GapText.Text = Settings.Gap + " px";
        SizeSlider.Set(Settings.Scale, LookView.IsVisible);
        GapSlider.Set(Settings.Gap, LookView.IsVisible);
        DotsSegments.Set(Settings.Dots ? 1 : 0, LookView.IsVisible);
        SeekStyleSegments.Set(Settings.LineBar ? 1 : 0, LookView.IsVisible);
        SeekHoverSegments.Set((int)Settings.SeekHover, LookView.IsVisible);
        GlassSwitch.Set(Settings.Glass, LookView.IsVisible);
        QuietAppearanceSwitch.Set(Settings.QuietWhenIdle, LookView.IsVisible);
        ((RadioButton)BackdropStrip.Children[(int)Settings.Backdrop]).IsChecked = true;
        ((RadioButton)LyricChangeStrip.Children[(int)Settings.LyricChange]).IsChecked = true;
        ((RadioButton)HoverStrip.Children[(int)Settings.Hover]).IsChecked = true;
        BackdropText.Text = Settings.Backdrop switch
        {
            Backdrop.Matrix => "Матрица",
            Backdrop.Stars => "Звёзды",
            Backdrop.MatrixAndStars => "Матрица и звёзды",
            _ => "Свечение",
        };
        LyricChangeText.Text = Settings.LyricChange switch
        {
            LyricChange.Wave => "Волна по буквам",
            LyricChange.Drum => "Барабан по словам",
            _ => "Плавно",
        };
        HoverText.Text = Settings.Hover switch
        {
            Hover.Magnet => "Магнит",
            Hover.Flow => "Перетекание",
            _ => "Диск",
        };
        foreach (RadioButton dot in AccentStrip.Children)
        {
            Color? color = dot.Background is SolidColorBrush own ? own.Color : null;
            if (color != Settings.Accent) continue;
            dot.IsChecked = true;
            AccentText.Text = (string)dot.Tag;
        }
    }

    void ApplyLook()
    {
        RefreshLookPage();
        _userScale.Target = Settings.Scale / 100.0;
        _topGap.Target = Settings.Gap;
        UpdateTargets();
    }
}
