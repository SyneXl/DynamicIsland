using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace DynamicIsland;

public partial class MainWindow
{
    readonly CancellationTokenSource _throneLifetime = new();
    readonly SemaphoreSlim _throneGate = new(1, 1);
    ThronePreferences _thronePreferences = Settings.Throne;
    Func<ThronePreferences, bool> _saveThronePreferences = Settings.TrySetThrone;
    Func<ThronePreferences, ThroneDiscoveryResult> _findThrone = ThroneDiscovery.Find;
    ThroneDiscoveryResult? _throneInstallation;
    IThroneApi? _throneApi;
    ThroneStatus? _throneStatus;
    ThroneGroup[] _throneGroups = [];
    ThroneProfile[] _throneProfiles = [];
    Task? _throneRefreshTask;
    bool _throneLoading, _throneBusy, _throneTesting, _throneDownloading, _throneClosed, _throneReadError;
    int? _throneJob;
    long _thronePolledAt;
    string _throneMessage = "";
    ThroneInstallerFile? _throneInstallerFile;
    readonly ThroneInstaller _throneInstaller = new();
    CancellationTokenSource? _throneDownloadCancel;
    IntPtr _throneTypedAfter;

    void PrepareThrone()
    {
        Closed += (_, _) =>
        {
            _throneClosed = true; _throneLifetime.Cancel();
            _throneInstallation?.Endpoint?.Dispose();
        };
        if (!App.SmokeTest) Loaded += async (_, _) => await RefreshThroneAsync(true);
        SyncThrone();
    }

    async void ThroneRow_Click(object sender, RoutedEventArgs e) { ShowPanel(Panel.Throne); await RefreshThroneAsync(true); }
    async void ThroneRefresh_Click(object sender, RoutedEventArgs e) { _throneMessage = ""; await RefreshThroneAsync(true); }
    void ThroneBack_Click(object sender, RoutedEventArgs e) { ShowPanel(Panel.Throne); SyncThrone(); }
    void PollThrone()
    {
        if (_throneApi != null && !_throneBusy && !_throneLoading && !_throneClosed && Environment.TickCount64 - _thronePolledAt > 2000)
            _ = RefreshThroneAsync(false);
    }
    Task RefreshThroneAsync(bool discover)
    {
        if (_throneClosed || _throneBusy) return Task.CompletedTask;
        if (_throneRefreshTask is { IsCompleted: false }) return _throneRefreshTask;
        return _throneRefreshTask = RefreshThroneCoreAsync(discover);
    }
    async Task RefreshThroneCoreAsync(bool discover)
    {
        _throneLoading = discover; if (discover) SyncThrone();
        await _throneGate.WaitAsync();
        try
        {
            if (discover && !_throneTesting)
            {
                var found = await Task.Run(() => _findThrone(_thronePreferences), _throneLifetime.Token);
                if (_throneClosed) { found.Endpoint?.Dispose(); return; }
                _throneInstallation?.Endpoint?.Dispose();
                _throneInstallation = found;
                _throneApi = found.Endpoint == null ? null : new ThroneApi(found.Endpoint);
                _throneStatus = null;
                MenuLayout.ThroneDefaultVisible = found.Capable;
                // Automatic visibility survives reorders and later installs;
                // an explicit toggle in the editor always wins over detection.
                var detectedMenu = _menuLayout.DetectThrone(found.Capable);
                if (!App.SmokeTest && !detectedMenu.Entries.SequenceEqual(_menuLayout.Entries)) SaveMenuLayout(detectedMenu);
                _throneMessage = found.Error ?? "";
            }
            if (_throneApi != null)
            {
                _throneStatus = ThroneStatus.Parse(await _throneApi.CallAsync("GET", "/v1/status", cancel: _throneLifetime.Token));
                if (_throneReadError) { _throneMessage = ""; _throneReadError = false; }
                if (discover)
                {
                    _throneGroups = ThroneData.Groups(await _throneApi.CallAsync("GET", "/v1/groups", cancel: _throneLifetime.Token));
                    _throneProfiles = ThroneData.Profiles(await _throneApi.CallAsync("GET", "/v1/profiles", cancel: _throneLifetime.Token));
                    NormalizeThroneSelection();
                }
            }
        }
        catch (OperationCanceledException) when (_throneClosed) { }
        catch (Exception ex) { _throneStatus = null; _throneReadError = true; _throneMessage = ThroneError(ex); }
        finally
        {
            _thronePolledAt = Environment.TickCount64; _throneLoading = false; _throneGate.Release();
            if (!_throneClosed)
            {
                SyncThrone();
                if (discover) { FillThroneGroups(); FillThroneServers(); }
            }
        }
    }

    void NormalizeThroneSelection()
    {
        var profile = _throneProfiles.FirstOrDefault(p => p.Id == _thronePreferences.Profile);
        int group = _thronePreferences.Group;
        if (!_throneGroups.Any(g => g.Id == group)) group = _throneProfiles.FirstOrDefault(p => p.Id == _throneStatus?.ProfileId)?.Group ?? _throneGroups.FirstOrDefault()?.Id ?? -1;
        if (profile?.Group != group)
            profile = _throneProfiles.FirstOrDefault(p => p.Group == group && p.Id == (_throneStatus?.ProfileId >= 0 ? _throneStatus.ProfileId : _throneStatus?.LastProfileId))
                ?? _throneProfiles.FirstOrDefault(p => p.Group == group);
        // Discovery defaults are in-memory until a deliberate user choice.
        _thronePreferences = _thronePreferences with { Group = group, Profile = profile?.Id ?? -1 };
    }

    bool SaveThrone(ThronePreferences preferences)
    {
        if (!_saveThronePreferences(preferences)) { _throneMessage = "Не удалось сохранить выбор"; SyncThrone(); return false; }
        _thronePreferences = preferences; return true;
    }
    static string ThroneError(Exception error) => error switch
    {
        ThroneApiException or InvalidDataException or TimeoutException => error.Message,
        SocketException or EndOfStreamException => "Нет связи с Throne. Запусти его и проверь API",
        OperationCanceledException => "Действие отменено",
        _ => "Не удалось связаться с Throne. Обнови подключение"
    };

    void SyncThrone()
    {
        if (_throneClosed) return;
        bool connected = _throneApi != null && _throneStatus != null;
        bool capable = _throneInstallation?.Capable == true;
        bool running = _throneStatus?.Running == true;
        bool changing = _throneBusy || _throneStatus?.Transitioning == true;
        ThroneControls.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        ThroneSetup.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        ThroneSetupTitle.Text = _throneLoading ? "Подключаюсь…" : !capable ? "Нужна новая версия Throne" : "Подключи API Throne";
        ThroneSetupText.Text = !capable ? "Управление доступно в версии с API. Скачаем актуальный установщик, включая бета-версии."
            : "В Throne: Настройки → Настройки интеграции → Throne API → Включить. Затем нажми обновление сверху.";
        ThroneUpdate.Visibility = !capable ? Visibility.Visible : Visibility.Collapsed;
        ThroneBrowse.Visibility = !connected ? Visibility.Visible : Visibility.Collapsed;
        ThroneUpdate.IsEnabled = !_throneLoading;
        ThroneUpdate.Content = _throneDownloading ? "Отменить загрузку" : "Обновить Throne";
        ThroneRefresh.IsEnabled = !_throneBusy && !_throneLoading && !_throneTesting;
        ThroneDataFolder.IsEnabled = ThroneBrowse.IsEnabled = !_throneBusy && !_throneLoading && !_throneTesting;
        ThroneOpen.Visibility = !connected && _throneInstallation?.Executable != null ? Visibility.Visible : Visibility.Collapsed;
        ThroneState.Text = _throneBusy ? "Переключаю…" : _throneStatus?.State switch { "running" => "Включён", "idle" => "Выключен", "connecting" => "Подключаю…", "stopping" => "Отключаю…", _ => "Нет связи" };
        ThroneState.Foreground = running ? _green : _dim;
        ThroneActive.Text = running ? _throneStatus!.ProfileName : "";
        ThroneActive.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ThroneAction.IsEnabled = connected && !changing && !_throneLoading && !_throneTesting && (running || _thronePreferences.Profile >= 0 || _throneStatus?.LastProfileId >= 0);
        ThronePlay.Visibility = !running && !changing ? Visibility.Visible : Visibility.Collapsed;
        ThronePause.Visibility = running && !changing ? Visibility.Visible : Visibility.Collapsed;
        ThroneBusyIcon.Visibility = changing ? Visibility.Visible : Visibility.Collapsed;
        string action = running ? "Выключить Throne" : "Включить Throne";
        ThroneAction.ToolTip = action; AutomationProperties.SetName(ThroneAction, action);
        MenuThrone.Text = connected ? running ? "Вкл." : _throneStatus?.Transitioning == true ? "…" : "Выкл." : "";
        ThroneGroupText.Text = _throneGroups.FirstOrDefault(g => g.Id == _thronePreferences.Group)?.Name ?? "Нет групп";
        ThroneServerText.Text = _throneProfiles.FirstOrDefault(p => p.Id == _thronePreferences.Profile)?.Name ?? "Нет серверов";
        ThroneGroupButton.IsEnabled = ThroneServerButton.IsEnabled = connected && !changing && !_throneLoading && !_throneTesting;
        ThronePing.Content = _throneTesting ? "Остановить тест" : "Проверить пинг";
        ThronePing.IsEnabled = connected && !_throneBusy && (_throneTesting ? _throneJob != null : _throneProfiles.Any(p => p.Group == _thronePreferences.Group && p.Type != "autoselector"));
        ThroneSort.Content = _thronePreferences.SortByLatency ? "По пингу ↑" : "По порядку";
        foreach (var rows in new[] { ThroneGroupRows, ThroneServerRows })
            foreach (Button row in rows.Children.OfType<Button>()) row.IsEnabled = connected && !changing && !_throneLoading && !_throneTesting;
        ThroneNote.Text = _throneMessage;
        ThroneNote.Visibility = _throneMessage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ThroneInstallOffer.Visibility = _throneInstallerFile == null ? Visibility.Collapsed : Visibility.Visible;
        ThroneContent.Measure(new Size(340, double.PositiveInfinity));
        ThroneView.Height = Math.Clamp(ThroneContent.DesiredSize.Height, 220, 480);
        if (_view == View.Throne) UpdateTargets();
    }

    async void ThroneAction_Click(object sender, RoutedEventArgs e)
    {
        if (!ThroneAction.IsEnabled || _throneBusy || _throneApi == null) return;
        bool start = _throneStatus?.Running != true;
        await ChangeThroneAsync(start, _thronePreferences.Profile);
    }
    async Task<bool> ChangeThroneAsync(bool start, int profile)
    {
        if (_throneBusy || _throneTesting || _throneApi == null) return false;
        _throneBusy = true; _throneMessage = ""; SyncThrone();
        await _throneGate.WaitAsync();
        try
        {
            object body = start ? profile >= 0 ? new { profile, wait = false } : new { select = "last", wait = false } : new { wait = false };
            var result = await _throneApi.CallAsync(start ? "PUT" : "DELETE", "/v1/connection", body, cancel: _throneLifetime.Token);
            _throneStatus = ThroneStatus.Parse(result); // 202 is a transition, never fabricated success
            return true;
        }
        catch (OperationCanceledException) when (_throneClosed) { return false; }
        catch (Exception ex) { _throneMessage = ThroneError(ex); return false; }
        finally { _throneBusy = false; _throneGate.Release(); if (!_throneClosed) SyncThrone(); }
    }

    async void ThroneGroups_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.ThroneGroups); FillThroneGroups(); await RefreshThroneAsync(true);
    }
    async void ThroneServers_Click(object sender, RoutedEventArgs e)
    {
        ShowPanel(Panel.ThroneServers); ThroneServerFilter.Text = ""; FillThroneServers(); await RefreshThroneAsync(true);
    }
    Button ThroneChoice(string title, string detail, bool selected, int id, Action choose)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(82) });
        grid.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0) });
        var tail = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tail.Children.Add(new TextBlock { Text = detail, Foreground = _dim, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        if (selected) tail.Children.Add(new Icon { Kind = Glyph.Check, Width = 12, Height = 12, Fill = Brushes.White, Margin = new(8, 0, 0, 0) });
        Grid.SetColumn(tail, 1); grid.Children.Add(tail);
        var button = new Button { Style = (Style)FindResource("MenuEditButton"), Height = 46, Content = grid, Tag = id, IsEnabled = _throneStatus != null && !_throneStatus.Transitioning && !_throneBusy && !_throneLoading && !_throneTesting };
        AutomationProperties.SetName(button, title + (selected ? ", выбран" : "")); button.Click += (_, _) => choose(); return button;
    }
    void FillThroneGroups()
    {
        if (_throneClosed) return;
        ThroneGroupRows.Children.Clear();
        foreach (var group in _throneGroups)
            ThroneGroupRows.Children.Add(ThroneChoice(group.Name, group.Count.ToString(), group.Id == _thronePreferences.Group, group.Id, () =>
            {
                int profile = _throneProfiles.FirstOrDefault(p => p.Group == group.Id && p.Id == _throneStatus?.ProfileId)?.Id
                    ?? _throneProfiles.FirstOrDefault(p => p.Group == group.Id)?.Id ?? -1;
                if (SaveThrone(_thronePreferences with { Group = group.Id, Profile = profile })) ShowPanel(Panel.Throne);
                SyncThrone();
            }));
        ThroneGroupsEmpty.Visibility = _throneGroups.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ThroneGroupsView.Height = Math.Clamp(68 + _throneGroups.Length * 46, 164, 430);
        if (_view == View.ThroneGroups) UpdateTargets();
    }
    void FillThroneServers()
    {
        if (_throneClosed || ThroneServerRows == null) return;
        double offset = ThroneServersScroll.VerticalOffset;
        ThroneServerRows.Children.Clear();
        string filter = ThroneServerFilter.Text.Trim();
        var profiles = ThroneData.Sorted(_throneProfiles.Where(p => p.Group == _thronePreferences.Group && p.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase)), _thronePreferences.SortByLatency);
        foreach (var profile in profiles)
            ThroneServerRows.Children.Add(ThroneChoice(profile.Name, ThroneData.LatencyText(profile.Latency), profile.Id == _thronePreferences.Profile, profile.Id, async () =>
            {
                if (_throneBusy || _throneTesting) return;
                // Choosing a server switches an existing connection; offline it
                // only chooses the next start, without silently enabling a VPN.
                if (_throneStatus?.Running == true && !await ChangeThroneAsync(true, profile.Id)) { FillThroneServers(); return; }
                if (SaveThrone(_thronePreferences with { Profile = profile.Id })) ShowPanel(Panel.Throne);
                SyncThrone();
            }));
        ThroneServerNote.Text = _throneTesting ? "Проверяю задержку через серверы…" : profiles.Length == 0 ? "Серверы не найдены" : _throneMessage;
        ThroneServersView.Height = Math.Clamp(146 + profiles.Length * 46 + (ThroneServerNote.Text.Length > 0 ? 28 : 0), 238, 466);
        if (_view == View.ThroneServers) UpdateTargets();
        ThroneServersScroll.ScrollToVerticalOffset(offset);
    }
    void ThroneServerFilter_TextChanged(object sender, TextChangedEventArgs e) => FillThroneServers();
    void ThroneSearch_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsActive) _throneTypedAfter = Native.ForegroundWindow;
        Activate(); ThroneServerFilter.Focus();
    }
    void ThroneSearch_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (ThroneServerFilter.Text.Length > 0) ThroneServerFilter.Clear();
        else ShowPanel(Panel.Throne);
        e.Handled = true;
    }
    void EndThroneTyping()
    {
        if (IsActive && _throneTypedAfter != IntPtr.Zero && _throneTypedAfter != _hwnd) Native.BringForward(_throneTypedAfter);
        _throneTypedAfter = IntPtr.Zero;
    }
    void ThroneSort_Click(object sender, RoutedEventArgs e)
    {
        SaveThrone(_thronePreferences with { SortByLatency = !_thronePreferences.SortByLatency }); SyncThrone(); FillThroneServers();
    }

    async void ThronePing_Click(object sender, RoutedEventArgs e)
    {
        if (_throneApi == null || _throneBusy) return;
        if (_throneTesting)
        {
            if (_throneJob is not { } id) return;
            _throneBusy = true; SyncThrone();
            try { await _throneApi.CallAsync("DELETE", $"/v1/tests/{id}", cancel: _throneLifetime.Token); }
            catch (Exception ex) { if (!_throneClosed) _throneMessage = ThroneError(ex); }
            finally { _throneBusy = false; if (!_throneClosed) { SyncThrone(); FillThroneServers(); } }
            return;
        }
        if (!ThronePing.IsEnabled) return;
        _throneTesting = true; _throneMessage = ""; int group = _thronePreferences.Group;
        var api = _throneApi; SyncThrone(); FillThroneServers();
        try
        {
            var job = await api.CallAsync("POST", "/v1/tests", new { kind = "latency", group, wait = false }, cancel: _throneLifetime.Token);
            _throneJob = job.GetProperty("id").GetInt32(); SyncThrone();
            while (job.GetProperty("state").GetString() == "running")
            {
                await Task.Delay(1000, _throneLifetime.Token);
                job = await api.CallAsync("GET", $"/v1/tests/{_throneJob}", cancel: _throneLifetime.Token);
            }
            _throneProfiles = ThroneData.Profiles(await api.CallAsync("GET", "/v1/profiles", cancel: _throneLifetime.Token));
            if (job.GetProperty("state").GetString() == "done") SaveThrone(_thronePreferences with { SortByLatency = true });
        }
        catch (OperationCanceledException) when (_throneClosed) { }
        catch (Exception ex) { _throneMessage = ThroneError(ex); }
        finally { _throneTesting = false; _throneJob = null; if (!_throneClosed) { SyncThrone(); FillThroneGroups(); FillThroneServers(); } }
    }

    async void ThroneBrowse_Click(object sender, RoutedEventArgs e)
    {
        _pickingFiles = true;
        try
        {
            var picker = new OpenFileDialog { Title = "Выбери Throne.exe", Filter = "Throne|Throne.exe", CheckFileExists = true };
            if (picker.ShowDialog(this) == true && SaveThrone(_thronePreferences with { Executable = picker.FileName, DataDirectory = null })) await RefreshThroneAsync(true);
        }
        finally { _pickingFiles = false; }
    }
    async void ThroneDataFolder_Click(object sender, RoutedEventArgs e)
    {
        _pickingFiles = true;
        try
        {
            var picker = new OpenFolderDialog { Title = "Папка Throne, содержащая config\\throne.db", Multiselect = false };
            if (picker.ShowDialog(this) != true) return;
            string path = picker.FolderName;
            if (File.Exists(Path.Combine(path, "throne.db"))) path = Path.GetDirectoryName(path)!;
            if (!File.Exists(Path.Combine(path, "config", "throne.db"))) { _throneMessage = "В этой папке нет config\\throne.db"; SyncThrone(); return; }
            if (SaveThrone(_thronePreferences with { DataDirectory = path })) await RefreshThroneAsync(true);
        }
        finally { _pickingFiles = false; }
    }
    void ThroneOpen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_throneInstallation?.Executable is { } path && File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(path)! });
        }
        catch { _throneMessage = "Не удалось открыть Throne"; SyncThrone(); }
    }
    async void ThroneUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_throneDownloading) { _throneDownloadCancel?.Cancel(); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_throneLifetime.Token);
        _throneDownloadCancel = cancellation;
        _throneDownloading = true; _throneMessage = "Ищу актуальную версию…"; SyncThrone();
        try
        {
            var release = await _throneInstaller.LatestAsync(cancellation.Token);
            _throneMessage = "Скачиваю " + release.Version; SyncThrone();
            _throneInstallerFile = await _throneInstaller.DownloadAsync(release, ThroneInstaller.Downloads(), new Progress<int>(p =>
            {
                if (!_throneClosed && _throneDownloading) { _throneMessage = $"Скачиваю {release.Version} — {p}%"; SyncThrone(); }
            }), cancellation.Token);
            _throneMessage = "";
        }
        catch (OperationCanceledException) when (_throneClosed) { }
        catch (OperationCanceledException) { _throneMessage = "Загрузка отменена"; }
        catch (Exception ex) { _throneMessage = ex is InvalidDataException ? ex.Message : "Не удалось скачать Throne. Попробуй ещё раз"; }
        finally { _throneDownloading = false; _throneDownloadCancel = null; if (!_throneClosed) SyncThrone(); }
    }
    async void ThroneRunInstaller_Click(object sender, RoutedEventArgs e)
    {
        if (_throneInstallerFile is not { } file || !ThroneRunInstaller.IsEnabled) return;
        ThroneRunInstaller.IsEnabled = false;
        try
        {
            if (!await ThroneInstaller.VerifyAsync(file, _throneLifetime.Token)) { _throneMessage = "Установщик изменился. Скачай его снова"; _throneInstallerFile = null; return; }
            Process.Start(new ProcessStartInfo(file.Path) { UseShellExecute = true });
        }
        catch (OperationCanceledException) when (_throneClosed) { }
        catch { _throneMessage = "Установщик не запущен"; }
        finally { if (!_throneClosed) { ThroneRunInstaller.IsEnabled = true; SyncThrone(); } }
    }
}
