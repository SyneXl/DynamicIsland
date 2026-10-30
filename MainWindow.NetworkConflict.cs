using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public partial class MainWindow
{
    enum NetworkApp { Throne, Zapret }
    NetworkApp? _networkStart;
    TaskCompletionSource<bool?>? _networkConflictAnswer;
    bool _networkStartCancelled, _networkStartApproved;
    string _networkStartStage = "";
    TimeSpan _networkStopTimeout = TimeSpan.FromSeconds(8);
    Func<bool> _isThroneProcessRunning = () =>
    {
        var processes = Process.GetProcessesByName("Throne");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    };

    // One user intent owns both controls, including the time spent answering.
    // No background mutual exclusion: "Нет" explicitly permits both programs.
    async Task<bool> StartNetworkAsync(NetworkApp target, int profile = -1, bool replaceRunning = false)
    {
        if (_networkStart != null || _zapretBusy || _throneBusy || _throneTesting || _throneClosed) return false;
        _networkStart = target; _networkStartCancelled = _networkStartApproved = false;
        int selectedProfile = profile >= 0 ? profile : _thronePreferences.Profile;
        string? selectedRoot = _zapretRoot, selectedStrategy = _zapretStrategy;
        _networkStartStage = "Проверяю…";
        if (target == NetworkApp.Throne) _throneMessage = ""; else _zapretMessage = "";
        SyncNetworkViews();
        try
        {
            // Do not stop the other program for a stale Play button if this one
            // has already been started outside the island.
            bool alreadyActive = await ReadNetworkActiveAsync(target);
            if (alreadyActive && !replaceRunning) return true;
            var other = target == NetworkApp.Throne ? NetworkApp.Zapret : NetworkApp.Throne;
            bool conflict = !alreadyActive && await ReadNetworkActiveAsync(other);
            if (NetworkStartAbandoned) return false;
            bool disableOther = false;
            if (conflict)
            {
                ShowPanel(target == NetworkApp.Throne ? Panel.Throne : Panel.Zapret);
                bool? answer = await AskNetworkConflictAsync(target, other);
                if (answer == null || NetworkStartAbandoned) return false;
                disableOther = answer.Value;
            }
            _networkStartApproved = true;
            if (disableOther)
            {
                _networkStartStage = other == NetworkApp.Zapret ? "Отключаю Zapret…" : "Отключаю VPN…";
                SyncNetworkViews();
                await StopNetworkConfirmedAsync(other);
            }
            if (NetworkStartAbandoned) return false;
            // The prompt can stay open indefinitely. Re-read the destination,
            // preserving any outside start rather than restarting it needlessly.
            if (await ReadNetworkActiveAsync(target) && !replaceRunning) return true;
            if (NetworkStartAbandoned) return false;
            _networkStartStage = "Включаю…"; SyncNetworkViews();
            return target == NetworkApp.Throne
                ? await ChangeThroneAsync(true, selectedProfile)
                : await ExecuteZapretAsync(true, selectedRoot, selectedStrategy, _zapretStatus!);
        }
        catch (OperationCanceledException) when (_throneClosed) { return false; }
        catch (Exception ex)
        {
            if (target == NetworkApp.Throne) _throneMessage = ex.Message; else _zapretMessage = ex.Message;
            return false;
        }
        finally
        {
            _networkConflictAnswer = null; _networkStart = null; _networkStartStage = "";
            if (!_throneClosed) SyncNetworkViews();
        }
    }

    bool NetworkStartAbandoned => _throneClosed || _networkStartCancelled;

    async Task<bool> ReadNetworkActiveAsync(NetworkApp app)
    {
        if (app == NetworkApp.Zapret)
        {
            await RefreshZapretAsync();
            if (_zapretReadError || _zapretStatus == null || _zapretStatus.Service.Error != null || _zapretStatus.UnidentifiedProcesses > 0)
                throw new IOException("Не удалось проверить Zapret. Обнови статус и повтори.");
            var status = _zapretStatus;
            if (status.Service.Running || status.ProcessRoots.Length > 0) return true;
            if (!status.IsOff) throw new IOException("Zapret переключается. Попробуй через несколько секунд.");
            return false;
        }
        if (_throneApi == null) await RefreshThroneAsync(true);
        else await RefreshThroneAsync(false);
        if (_throneApi == null) return false; // No API integration to control.
        if (_throneStatus == null)
        {
            // An installed, closed client is normal. Never treat an unreachable
            // running client as proof that its connection is off.
            if (_throneConnectionRefused && !_isThroneProcessRunning() && _networkStart != NetworkApp.Throne) return false;
            throw new IOException("Не удалось проверить Throne. Обнови подключение.");
        }
        if (_throneStatus.Running || _throneStatus.State == "connecting") return true;
        if (_throneStatus.State != "idle") throw new IOException("Throne переключается. Попробуй через несколько секунд.");
        return false;
    }

    async Task StopNetworkConfirmedAsync(NetworkApp app)
    {
        // Re-check after the answer: another application may already have stopped
        // it. Zapret's helper still validates its fresh service identity at UAC.
        if (!await ReadNetworkActiveAsync(app)) return;
        bool accepted = app == NetworkApp.Zapret
            ? await ExecuteZapretAsync(false, null, null, _zapretStatus!)
            : await ChangeThroneAsync(false, -1);
        if (!accepted) throw new IOException(app == NetworkApp.Zapret
            ? "Не удалось выключить Zapret. Запуск отменён." : "Не удалось выключить Throne. Запуск отменён.");
        long deadline = Environment.TickCount64 + (long)_networkStopTimeout.TotalMilliseconds;
        do
        {
            if (_throneClosed) throw new OperationCanceledException();
            if (app == NetworkApp.Zapret)
            {
                await RefreshZapretAsync();
                if (_zapretReadError || _zapretStatus?.Service.Error != null) throw new IOException("Не удалось проверить отключение Zapret.");
                if (_zapretStatus?.IsOff == true) return;
            }
            else
            {
                // Bound each poll as well as the overall transition. A 202
                // response only acknowledges the request, not an idle VPN.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_throneLifetime.Token);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(3000, deadline - Environment.TickCount64))));
                await _throneGate.WaitAsync(timeout.Token);
                try
                {
                    _throneStatus = ThroneStatus.Parse(await _throneApi!.CallAsync("GET", "/v1/status", cancel: timeout.Token));
                    _thronePolledAt = Environment.TickCount64; SyncThrone();
                    if (_throneStatus.State == "idle") return;
                }
                catch (Exception) when (!_throneClosed) { throw new IOException("Не удалось проверить отключение Throne."); }
                finally { _throneGate.Release(); }
            }
            await Task.Delay(120, _throneLifetime.Token);
        } while (Environment.TickCount64 < deadline);
        throw new IOException(app == NetworkApp.Zapret ? "Zapret ещё отключается. Запуск отменён." : "Throne ещё отключается. Запуск отменён.");
    }

    async Task<bool?> AskNetworkConflictAsync(NetworkApp target, NetworkApp other)
    {
        var answer = _networkConflictAnswer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = target == NetworkApp.Throne ? ThroneConflictPrompt : ZapretConflictPrompt;
        var text = target == NetworkApp.Throne ? ThroneConflictText : ZapretConflictText;
        string name = other == NetworkApp.Zapret ? "Zapret" : "VPN";
        text.Text = $"Включён {name}. Отключить?";
        foreach (var button in new[] { ThroneConflictYes, ZapretConflictYes }) AutomationProperties.SetName(button, $"Да, отключить {name}");
        SyncNetworkViews();
        // No layout jumps; the card replaces the same 96-pixel status area.
        prompt.BeginAnimation(OpacityProperty, null);
        if (InputManager.Current.MostRecentInputDevice is not KeyboardDevice && SystemParameters.ClientAreaAnimation)
            prompt.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
                { EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut } });
        try { return await answer.Task; }
        finally { _networkConflictAnswer = null; if (!_throneClosed) SyncNetworkViews(); }
    }

    void NetworkConflictYes_Click(object sender, RoutedEventArgs e) => AnswerNetworkConflict(true);
    void NetworkConflictNo_Click(object sender, RoutedEventArgs e) => AnswerNetworkConflict(false);
    void AnswerNetworkConflict(bool disableOther)
    {
        _networkConflictAnswer?.TrySetResult(disableOther);
        SyncNetworkConflict(); // Ignore double-clicks immediately, before the continuation.
    }
    void CancelNetworkStartPrompt(Panel panel)
    {
        if (_networkStart == null || _networkStartApproved) return;
        var expected = _networkStart == NetworkApp.Throne ? Panel.Throne : Panel.Zapret;
        if (panel == expected) return;
        _networkStartCancelled = true;
        _networkConflictAnswer?.TrySetResult(null);
    }

    void SyncNetworkViews() { SyncThrone(); SyncZapret(); }
    void SyncNetworkConflict()
    {
        bool asking = _networkConflictAnswer != null;
        bool throne = asking && _networkStart == NetworkApp.Throne;
        bool zapret = asking && _networkStart == NetworkApp.Zapret;
        ThroneConflictPrompt.Visibility = throne ? Visibility.Visible : Visibility.Collapsed;
        ZapretConflictPrompt.Visibility = zapret ? Visibility.Visible : Visibility.Collapsed;
        ThroneTransport.Visibility = throne ? Visibility.Hidden : Visibility.Visible;
        ZapretTransport.Visibility = zapret ? Visibility.Hidden : Visibility.Visible;
        bool canAnswer = _networkConflictAnswer is { Task.IsCompleted: false };
        ThroneConflictYes.IsEnabled = ThroneConflictNo.IsEnabled = ZapretConflictYes.IsEnabled = ZapretConflictNo.IsEnabled = canAnswer;
        if (_networkStart == null) return;
        foreach (var button in new[] { ThroneAction, ThroneRefresh, ThroneGroupButton, ThroneServerButton, ThronePing,
            ThroneBrowse, ThroneDataFolder, ZapretAction, ZapretRefresh, ZapretApply, ZapretCleanup, ZapretFolderButton, ZapretStrategyButton }) button.IsEnabled = false;
        foreach (var rows in new[] { ThroneGroupRows, ThroneServerRows })
            foreach (Button row in rows.Children.OfType<Button>()) row.IsEnabled = false;
        if (!asking)
        {
            if (_networkStart == NetworkApp.Throne) { ThroneState.Text = _networkStartStage; ThronePlay.Visibility = ThronePause.Visibility = Visibility.Collapsed; ThroneBusyIcon.Visibility = Visibility.Visible; }
            else { ZapretState.Text = _networkStartStage; ZapretPlayIcon.Visibility = ZapretPauseIcon.Visibility = Visibility.Collapsed; }
        }
    }
}
