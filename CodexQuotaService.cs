using System.IO;
using System.Text.Json;

namespace DynamicIsland;

sealed class CodexQuotaService : IDisposable
{
    readonly Func<CancellationToken, Task<ICodexSession>> _connect;
    readonly CodexQuotaStore _store;
    readonly Func<DateTimeOffset> _now;
    readonly Func<TimeSpan, CancellationToken, Task> _delay;
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _monitor;
    volatile bool _disposed, _enabled;

    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "Codex");
    public CodexQuotaService() : this(s => CodexRpc.ConnectAsync(Path.Combine(DataDirectory, "empty-workspace"), s), new(DataDirectory)) { }
    internal CodexQuotaService(Func<CancellationToken, Task<ICodexSession>> connect, CodexQuotaStore store,
        Func<DateTimeOffset>? now = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    { _connect = connect; _store = store; _now = now ?? (() => DateTimeOffset.UtcNow); _delay = delay ?? Task.Delay; }

    public event Action? Changed;
    public CodexUsage? Latest { get; private set; }
    public CodexWindowState WindowState { get; private set; }
    public bool Enabled => _enabled;
    public bool Busy { get; private set; }
    public string Status { get; private set; } = "Открой, чтобы проверить лимиты";

    void Show(string status) { Status = status; Changed?.Invoke(); }

    public void SetEnabled(bool enabled, bool monitor = true)
    {
        if (_disposed || Enabled == enabled) return;
        _enabled = enabled;
        _monitor?.Cancel();
        _monitor = null;
        if (enabled && monitor)
        {
            var lifetime = _monitor = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = Task.Run(() => MonitorAsync(lifetime.Token));
        }
        else Show(enabled ? "Автопинг включён" : "Автопинг выключен");
    }

    async Task MonitorAsync(CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await RefreshAsync(stop);
                TimeSpan wait = TimeSpan.FromMinutes(5);
                if (Latest?.Five is { } five && WindowState is CodexWindowState.Anchored or CodexWindowState.Exhausted)
                    wait = TimeSpan.FromSeconds(Math.Clamp((five.Reset.AddSeconds(60) - _now()).TotalSeconds, 10, 300));
                await _delay(wait, stop);
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task RefreshAsync(CancellationToken stop = default)
    {
        if (_disposed || !await _gate.WaitAsync(0, stop)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stop, _lifetime.Token);
        try
        {
            Busy = true;
            WindowState = CodexWindowState.Unknown;
            Show("Проверяю лимиты…");
            await using var client = await _connect(linked.Token);
            var samples = new List<CodexUsage>();
            async Task Read()
            {
                Latest = CodexUsage.Parse(await client.ReadUsageAsync(linked.Token), _now());
                samples.Add(Latest);
                if (samples.Count > 4) samples.RemoveAt(0);
                Changed?.Invoke();
            }
            async Task CompleteEvidence()
            {
                while (samples.Count < 4)
                {
                    await _delay(TimeSpan.FromSeconds(10), linked.Token);
                    await Read();
                }
                WindowState = CodexQuota.Classify(samples);
            }

            await Read();
            var remembered = Latest!.AccountKey.Length == 64 ? _store.Get(Latest.AccountKey) : null;
            bool knownFixed = remembered?.Anchor is { } reset && Latest.Five is { } known
                && known.Reset.ToUnixTimeSeconds() == reset && known.Reset > Latest.At;
            if (knownFixed) WindowState = Latest.Five!.Blocked || Latest.Five.Used >= 100 ? CodexWindowState.Exhausted : CodexWindowState.Anchored;
            else if (Latest.Five == null) WindowState = CodexWindowState.Absent;
            else if (Latest.Five.Blocked || Latest.Five.Used >= 100) WindowState = CodexWindowState.Exhausted;
            else await CompleteEvidence();
            if (WindowState == CodexWindowState.Anchored) _store.Anchor(Latest);

            if (!Enabled) { Show("Автопинг выключен"); return; }
            if (WindowState == CodexWindowState.Anchored) { Show("Ожидаю следующего сброса"); return; }
            if (WindowState != CodexWindowState.Unanchored) { Show(WindowState == CodexWindowState.Absent ? "Codex не передал 5-часовое окно" : "Ожидаю восстановления лимита"); return; }
            if (!CodexQuota.MayStart(Latest)) { Show("Ожидаю доступного лимита Codex"); return; }
            if (remembered?.Anchor is { } boundary && Latest.At.ToUnixTimeSeconds() < boundary + 60) { Show("Жду завершения сброса"); return; }
            if (remembered?.ReservedFor == (remembered?.Anchor ?? 0)) { Show("Последний пинг не повторяется — жду подтверждения окна"); return; }
            var model = CodexModel.Select(await client.ModelsAsync(linked.Token));
            if (model == null) { Show("Нет доступной лёгкой модели для пинга"); return; }
            // Revalidate after the capability check, in case normal use started a window.
            await _delay(TimeSpan.FromSeconds(10), linked.Token);
            await Read();
            WindowState = CodexQuota.Classify(samples);
            if (!Enabled || WindowState != CodexWindowState.Unanchored || !CodexQuota.MayStart(Latest)) { Show("Лимиты изменились — пинг не нужен"); return; }
            linked.Token.ThrowIfCancellationRequested();
            if (!_store.Reserve(Latest)) { Show("Пинг для этого окна уже учтён"); return; }
            string account = Latest.AccountKey, identity = Latest.Five!.Identity;
            Show("Запускаю новое окно…");
            try
            {
                await client.PingAsync(model, linked.Token);
                // A completed turn is not proof: the reset must stop sliding.
                samples.Clear();
                await _delay(TimeSpan.FromSeconds(3), linked.Token);
                await Read();
                await CompleteEvidence();
                if (WindowState == CodexWindowState.Anchored && Latest.AccountKey == account && Latest.Five!.Identity == identity)
                {
                    _store.Verified(Latest);
                    Show(Enabled ? "Новое окно запущено" : "Автопинг выключен");
                }
                else Show("Пинг отправлен — новое окно пока не подтверждено");
            }
            catch (Exception) { Show("Результат пинга не подтверждён; повтор отключён для этого окна"); }
        }
        catch (OperationCanceledException) { if (!_disposed) Show(Enabled ? "Проверка прервана" : "Автопинг выключен"); }
        catch (CodexFailure e) { Show(e.Message); }
        catch (Exception) { Show("Не удалось связаться с Codex"); }
        finally { Busy = false; Changed?.Invoke(); _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor?.Cancel();
        _lifetime.Cancel();
    }
}
