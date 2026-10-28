using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly Dictionary<string, ZapretInstallation> _zapretInstalls = new(StringComparer.OrdinalIgnoreCase);
    IZapretSystem _zapretSystem = new ZapretSystem();
    ZapretPreferences _zapretPreferences = Settings.Zapret;
    Func<ZapretPreferences, bool> _saveZapretPreferences = Settings.TrySetZapret;
    Func<bool, string?, string?, ZapretStatus, Task<ZapretOperationResult>> _runZapretOperation = ZapretElevation.RunAsync;
    ZapretStatus? _zapretStatus;
    CancellationTokenSource? _zapretScan;
    string? _zapretRoot, _zapretStrategy;
    bool _zapretBusy, _zapretInstalling, _zapretSpinnerActive, _zapretSearched, _zapretClosed;
    Task? _zapretRefreshTask;
    long _zapretStatusAt;
    string _zapretMessage = "";

    void PrepareZapret()
    {
        _zapretRoot = _zapretPreferences.SelectedRoot;
        Closed += (_, _) => { _zapretClosed = true; _zapretScan?.Cancel(); };
        SyncZapret();
    }

    async void ZapretRow_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.Zapret);
        await RefreshZapretAsync();
        if (!_zapretSearched) _ = ScanZapretAsync();
    }
    void ZapretFolders_Click(object sender, RoutedEventArgs e) { ShowPanel(Panel.ZapretFolders); FillZapretFolders(); if (!_zapretSearched) _ = ScanZapretAsync(); }
    void ZapretStrategies_Click(object sender, RoutedEventArgs e) { ShowPanel(Panel.ZapretStrategies); FillZapretStrategies(); }
    void ZapretBack_Click(object sender, RoutedEventArgs e) { ShowPanel(Panel.Zapret); SyncZapret(); }
    async void ZapretRefresh_Click(object sender, RoutedEventArgs e) { _zapretMessage = ""; await RefreshZapretAsync(); }
    async void ZapretScan_Click(object sender, RoutedEventArgs e)
    {
        if (_zapretScan != null) _zapretScan.Cancel();
        else await ScanZapretAsync();
    }

    Task RefreshZapretAsync()
    {
        if (_zapretClosed) return Task.CompletedTask;
        // Share an in-flight read so operations can await it rather than reuse a
        // stale pre-operation snapshot or publish an old poll after completion.
        if (_zapretRefreshTask is { IsCompleted: false }) return _zapretRefreshTask;
        return _zapretRefreshTask = RefreshZapretCoreAsync();
    }

    async Task RefreshZapretCoreAsync()
    {
        try
        {
            var status = await Task.Run(_zapretSystem.Read);
            if (_zapretClosed) return;
            _zapretStatus = status; _zapretStatusAt = Environment.TickCount64;
            if (_zapretRoot == null && status.Service.Root is { } active)
            {
                var install = await Task.Run(() => ZapretInstallation.Inspect(active));
                if (install != null) { AddZapretInstall(install); SelectZapretInstall(install); }
            }
            SyncZapret();
        }
        catch (Exception ex) { _zapretMessage = ex.Message; SyncZapret(); }
    }

    async Task ScanZapretAsync()
    {
        if (_zapretScan != null || _zapretClosed) return;
        var cancellation = _zapretScan = new CancellationTokenSource();
        _zapretSearched = true;
        var initiallyKnown = _zapretInstalls.Keys.ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZapretScanButton.Content = "Остановить поиск";
        try
        {
            string[] drives = ZapretDiscovery.LocalRoots().ToArray();
            string[] hints = _zapretPreferences.Strategies.Keys.Concat(new[] { _zapretRoot, _zapretStatus?.Service.Root })
                .Concat(_zapretStatus?.ProcessRoots ?? []).Where(p => !string.IsNullOrEmpty(p)).Cast<string>()
                .Concat(drives.Select(p => Path.Combine(p, "zapret"))).ToArray();
            await ZapretDiscovery.ScanAsync(drives, hints, progress => Dispatcher.InvokeAsync(() =>
            {
                if (_zapretClosed || cancellation.IsCancellationRequested) return;
                if (progress.Found is { } install)
                {
                    seen.Add(install.Root);
                    AddZapretInstall(install);
                    if (_zapretRoot != null && _zapretRoot.Equals(install.Root, StringComparison.OrdinalIgnoreCase)) RestoreZapretSelection(install);
                }
                ZapretScanNote.Text = progress.Complete
                    ? $"Найдено: {_zapretInstalls.Count}. Недоступных папок: {progress.Inaccessible}."
                    : $"Поиск на {Path.GetPathRoot(progress.Root)} — найдено {_zapretInstalls.Count}";
                if (progress.Complete)
                    foreach (string previous in initiallyKnown.Where(p => !seen.Contains(p))) _zapretInstalls.Remove(previous);
                if (progress.Complete && _zapretRoot == null && _zapretInstalls.Count == 1) SelectZapretInstall(_zapretInstalls.Values.Single());
                if (progress.Found != null || progress.Complete) { FillZapretFolders(); SyncZapret(); }
            }), cancellation.Token);
        }
        catch (OperationCanceledException) { ZapretScanNote.Text = $"Поиск остановлен. Найдено: {_zapretInstalls.Count}."; }
        catch (Exception ex) { ZapretScanNote.Text = ex.Message; }
        finally
        {
            if (ReferenceEquals(_zapretScan, cancellation)) _zapretScan = null;
            cancellation.Dispose();
            ZapretScanButton.Content = "Найти на компьютере";
        }
    }

    void AddZapretInstall(ZapretInstallation install) => _zapretInstalls[install.Root] = install;

    void RestoreZapretSelection(ZapretInstallation install)
    {
        _zapretRoot = install.Root;
        if (_zapretPreferences.Strategies.TryGetValue(install.Root, out string? saved))
            _zapretStrategy = install.Strategies.FirstOrDefault(s => s.FileName.Equals(saved, StringComparison.OrdinalIgnoreCase))?.FileName;
        else
            _zapretStrategy = install.Strategies.FirstOrDefault(s => s.Name.Equals(_zapretStatus?.Service.Strategy, StringComparison.OrdinalIgnoreCase))?.FileName
                ?? install.Strategies.FirstOrDefault(s => s.FileName.Equals("general.bat", StringComparison.OrdinalIgnoreCase))?.FileName
                ?? install.Strategies.FirstOrDefault()?.FileName;
    }

    void SelectZapretInstall(ZapretInstallation install)
    {
        string? previousRoot = _zapretRoot, previousStrategy = _zapretStrategy;
        RestoreZapretSelection(install);
        var next = _zapretPreferences.Select(install.Root, _zapretStrategy);
        if (!_saveZapretPreferences(next)) { _zapretRoot = previousRoot; _zapretStrategy = previousStrategy; _zapretMessage = "Не удалось сохранить выбор папки."; }
        else { _zapretPreferences = next; _zapretMessage = ""; }
        SyncZapret();
    }

    async void ZapretBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_zapretBusy) return;
        _pickingFiles = true;
        try
        {
            var picker = new OpenFolderDialog { Title = "Выбери папку с service.bat и bin\\winws.exe", Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            var install = await Task.Run(() => ZapretInstallation.Inspect(picker.FolderName));
            if (install == null) { ZapretScanNote.Text = "В этой папке не найдена распакованная копия zapret."; return; }
            AddZapretInstall(install); SelectZapretInstall(install); ShowPanel(Panel.Zapret); FillZapretFolders();
        }
        catch (Exception ex) { ZapretScanNote.Text = ex.Message; }
        finally { _pickingFiles = false; }
    }

    Button ZapretChoice(string title, string detail, bool selected, Action choose)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock { Text = detail, FontSize = 10.5, Foreground = _dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new(0, 4, 0, 0) });
        grid.Children.Add(words);
        var check = new Icon { Kind = Glyph.Check, Width = 13, Height = 13, Fill = Brushes.White, Visibility = selected ? Visibility.Visible : Visibility.Collapsed };
        Grid.SetColumn(check, 1); grid.Children.Add(check);
        var button = new Button { Style = (Style)FindResource("MenuEditButton"), Height = 56, Content = grid, ToolTip = detail, IsEnabled = !_zapretBusy };
        button.Click += (_, _) => choose();
        return button;
    }

    void FillZapretFolders()
    {
        ZapretFolderRows.Children.Clear();
        foreach (var install in _zapretInstalls.Values.OrderBy(i => i.Root, StringComparer.OrdinalIgnoreCase))
            ZapretFolderRows.Children.Add(ZapretChoice(install.Name + "  " + install.Version, install.Root,
                string.Equals(_zapretRoot, install.Root, StringComparison.OrdinalIgnoreCase), () => { SelectZapretInstall(install); ShowPanel(Panel.Zapret); }));
        ZapretNoFolders.Visibility = _zapretInstalls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void FillZapretStrategies()
    {
        ZapretStrategyRows.Children.Clear();
        if (_zapretRoot == null || !_zapretInstalls.TryGetValue(_zapretRoot, out var install)) return;
        foreach (var strategy in install.Strategies)
        {
            var row = ZapretChoice(strategy.Name, strategy.FileName, strategy.FileName == _zapretStrategy, () =>
            {
                var next = _zapretPreferences.Select(install.Root, strategy.FileName);
                if (_saveZapretPreferences(next)) { _zapretPreferences = next; _zapretStrategy = strategy.FileName; _zapretMessage = ""; ShowPanel(Panel.Zapret); }
                else _zapretMessage = "Не удалось сохранить стратегию.";
                SyncZapret();
            });
            row.Tag = strategy.FileName;
            ZapretStrategyRows.Children.Add(row);
        }
    }

    void SyncZapret()
    {
        var service = _zapretStatus?.Service;
        bool running = service?.Running == true;
        bool standalone = _zapretStatus?.ProcessRoots.Length > 0 && !running;
        bool known = service != null && service.Error == null && _zapretStatus?.UnidentifiedProcesses == 0;
        bool transitioning = service?.Installed == true && !service.DeletePending && service.State is not (1 or 4);
        ZapretState.Text = _zapretBusy ? _zapretInstalling ? "Включаю…" : "Выключаю…" : !known ? "Нет связи" : transitioning ? "Переход…" : running || standalone ? "Включён" : "Выключен";
        ZapretState.Foreground = running || standalone ? _green : _dim;
        MenuZapret.Text = known ? running || standalone ? "Вкл." : "Выкл." : "";
        ZapretFolderText.Text = _zapretRoot ?? (_zapretInstalls.Count > 0 ? $"Выбери копию ({_zapretInstalls.Count})" : "Выбери папку");
        ZapretFolderText.ToolTip = _zapretRoot;
        ZapretStrategyText.Text = _zapretStrategy == null ? "Выбери стратегию" : Path.GetFileNameWithoutExtension(_zapretStrategy);
        ZapretStrategyButton.IsEnabled = !_zapretBusy && _zapretRoot != null && _zapretInstalls.ContainsKey(_zapretRoot);
        ZapretFolderButton.IsEnabled = ZapretBrowseButton.IsEnabled = !_zapretBusy;
        bool remove = running || standalone;
        string action = remove ? "Выключить Zapret" : "Включить Zapret";
        AutomationProperties.SetName(ZapretAction, action);
        ZapretAction.ToolTip = remove ? "Выключить Zapret" : "Включить выбранную стратегию";
        ZapretPlayIcon.Visibility = !_zapretBusy && !remove ? Visibility.Visible : Visibility.Collapsed;
        ZapretPauseIcon.Visibility = !_zapretBusy && remove ? Visibility.Visible : Visibility.Collapsed;
        ZapretSpinner.Visibility = _zapretBusy ? Visibility.Visible : Visibility.Collapsed;
        if (_zapretSpinnerActive != _zapretBusy)
        {
            _zapretSpinnerActive = _zapretBusy;
            ((RotateTransform)ZapretSpinner.RenderTransform).BeginAnimation(RotateTransform.AngleProperty,
                _zapretBusy ? new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        }
        bool selected = _zapretRoot != null && _zapretStrategy != null && _zapretInstalls.ContainsKey(_zapretRoot);
        ZapretAction.IsEnabled = !_zapretBusy && !transitioning && known && (remove || selected);
        ZapretCleanup.Visibility = _zapretStatus?.HasZapretResources == true && !remove && !transitioning ? Visibility.Visible : Visibility.Collapsed;
        ZapretCleanup.IsEnabled = !_zapretBusy;
        bool mismatch = running && selected && (!string.Equals(service!.Root, _zapretRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(service.Strategy, Path.GetFileNameWithoutExtension(_zapretStrategy), StringComparison.OrdinalIgnoreCase));
        ZapretApply.Visibility = mismatch ? Visibility.Visible : Visibility.Collapsed;
        ZapretApply.IsEnabled = !_zapretBusy;
        ZapretActive.Text = _zapretBusy ? "" : mismatch ? "Сейчас: " + service!.Strategy : standalone ? "Вручную" : service?.Installed == true && !service.DeletePending ? service.Start == 2 ? "Автозапуск включён" : "Без автозапуска" : "";
        ZapretActive.Visibility = ZapretActive.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ZapretActive.ToolTip = mismatch && !string.Equals(service?.Root, _zapretRoot, StringComparison.OrdinalIgnoreCase) ? service?.Root : null;
        ZapretOperationNote.Text = _zapretBusy ? "" : _zapretMessage.Length > 0 ? _zapretMessage : service?.Error ?? "";
        bool note = ZapretOperationNote.Text.Length > 0;
        ZapretOperationNote.Visibility = note ? Visibility.Visible : Visibility.Collapsed;
        bool footer = mismatch || ZapretCleanup.Visibility == Visibility.Visible;
        ZapretActions.Visibility = footer ? Visibility.Visible : Visibility.Collapsed;
        ZapretOperationNote.Measure(new Size(316, double.PositiveInfinity));
        double height = 284 + (footer ? 38 : 0) + (note ? Math.Max(40, ZapretOperationNote.DesiredSize.Height) : 0);
        ZapretView.Height = Math.Min(400, height);
        if (_view == View.Zapret) UpdateTargets();
        ZapretRefresh.IsEnabled = !_zapretBusy;
    }

    async void ZapretAction_Click(object sender, RoutedEventArgs e)
    {
        if (_zapretBusy || _zapretStatus == null) return;
        bool remove = _zapretStatus.Service.Running || _zapretStatus.ProcessRoots.Length > 0;
        await ExecuteZapretAsync(!remove, _zapretRoot, _zapretStrategy, _zapretStatus);
    }
    async void ZapretCleanup_Click(object sender, RoutedEventArgs e)
    {
        if (_zapretStatus != null) await ExecuteZapretAsync(false, null, null, _zapretStatus);
    }
    async void ZapretApply_Click(object sender, RoutedEventArgs e)
    {
        if (_zapretStatus != null) await ExecuteZapretAsync(true, _zapretRoot, _zapretStrategy, _zapretStatus);
    }
    async Task ExecuteZapretAsync(bool install, string? root, string? strategy, ZapretStatus status)
    {
        if (_zapretBusy) return;
        _zapretBusy = true; _zapretInstalling = install; _zapretMessage = ""; SyncZapret();
        try
        {
            await RefreshZapretAsync();
            if (_zapretClosed) return;
            var fresh = _zapretStatus ?? status;
            if (fresh.Service.Error != null) throw new IOException(fresh.Service.Error);
            if (fresh.Identity != status.Identity) throw new IOException("Конфигурация изменилась. Повтори действие.");
            var result = await _runZapretOperation(install, root, strategy, fresh);
            if (!_zapretClosed) _zapretMessage = result.Success ? "" : result.Message;
        }
        catch (Exception ex) { _zapretMessage = ex.Message; }
        finally
        {
            if (!_zapretClosed) await RefreshZapretAsync();
            _zapretBusy = false;
            if (!_zapretClosed) SyncZapret();
        }
    }
}
