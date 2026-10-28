using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DynamicIsland;

static partial class Program
{
    static readonly DateTimeOffset QuotaNow = DateTimeOffset.FromUnixTimeSeconds(1792400000);
    static readonly string AccountA = new('A', 64), AccountB = new('B', 64);
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    static CodexUsage Usage(DateTimeOffset at, bool sliding = true, double used = 0, DateTimeOffset? fixedReset = null) => new(at, AccountA,
        new("codex", "primary", used, 300, fixedReset ?? (sliding ? at.AddHours(5) : QuotaNow.AddHours(3)), false),
        new("codex", "secondary", 40, 10080, QuotaNow.AddDays(4), false), true);
    static JsonElement WindowJson(int minutes, double used, DateTimeOffset? reset = null) => Json(new
        { windowDurationMins = minutes, usedPercent = used, resetsAt = (reset ?? QuotaNow.AddHours(3)).ToUnixTimeSeconds() });
    static JsonElement Payload(CodexUsage u) => Json(new
    {
        accountId = "fixture-account", ordinaryUsageAllowed = u.OrdinaryAllowed,
        rateLimitsByLimitId = new Dictionary<string, object> { ["codex"] = new
        {
            limitId = "codex", spendControlReached = u.Five?.Blocked == true,
            primary = u.Five == null ? (object?)null : WindowJson(u.Five.Minutes, u.Five.Used, u.Five.Reset),
            secondary = u.Week == null ? (object?)null : WindowJson(u.Week.Minutes, u.Week.Used, u.Week.Reset),
        } },
    });
    static JsonElement ModelJson(string name = "gpt-6-luna", bool hidden = false, string? upgrade = null, string[]? efforts = null) => Json(new
    {
        id = name, model = name, hidden, upgrade, inputModalities = new[] { "text" },
        supportedReasoningEfforts = (efforts ?? ["high", "low", "medium"]).Select(reasoningEffort => new { reasoningEffort }).ToArray(),
    });
    static bool Fails(Action action) { try { action(); return false; } catch (CodexFailure) { return true; } }

    sealed class QuotaTemp : IDisposable
    {
        readonly string _base = Path.Combine(Path.GetTempPath(), "DynamicIsland-quota-tests");
        public string PathName { get; }
        public QuotaTemp() { PathName = Path.Combine(_base, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(PathName); }
        public void Dispose()
        {
            if (!Path.GetFullPath(PathName).StartsWith(Path.GetFullPath(_base) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid fixture path");
            for (int attempt = 0; ; attempt++)
            {
                try { Directory.Delete(PathName, recursive: true); return; }
                catch (IOException) when (attempt < 20) { Thread.Sleep(100); }
            }
        }
    }
    sealed class QuotaClock
    {
        public DateTimeOffset Now = QuotaNow;
        public Task Delay(TimeSpan by, CancellationToken stop) { stop.ThrowIfCancellationRequested(); Now += by; return Task.CompletedTask; }
    }
    sealed class QuotaClient(QuotaClock clock) : ICodexSession
    {
        public int Reads, Pings, Catalogs, Disposals;
        public bool Active, FailPing, KeepSliding, Allowed = true, Weekly = true;
        public double WeekUsed = 40;
        public DateTimeOffset? NewAnchor;
        public JsonElement[] Models = [ModelJson()];
        public Action? OnModels;
        public Task<JsonElement> ReadUsageAsync(CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested(); Reads++;
            var u = Usage(clock.Now, !Active, Active ? 3 : 0, NewAnchor);
            if (!Weekly) u = u with { Week = null };
            else u = u with { Week = u.Week! with { Used = WeekUsed } };
            return Task.FromResult(Payload(u with { OrdinaryAllowed = Allowed }));
        }
        public Task<JsonElement[]> ModelsAsync(CancellationToken stop) { Catalogs++; OnModels?.Invoke(); return Task.FromResult(Models); }
        public Task PingAsync(CodexModel model, CancellationToken stop)
        {
            stop.ThrowIfCancellationRequested(); Pings++;
            if (FailPing) throw new IOException("fixture interrupted transport");
            if (!KeepSliding) { Active = true; NewAnchor = clock.Now.AddHours(5); }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    static CodexQuotaService QuotaService(QuotaClient client, QuotaClock clock, string folder) => new(
        _ => Task.FromResult<ICodexSession>(client), new(folder), () => clock.Now, clock.Delay);

    static async Task CodexTests()
    {
        var u = Usage(QuotaNow);
        var parsed = CodexUsage.Parse(Payload(u), QuotaNow);
        Check(parsed.Five?.Remaining == 100 && parsed.Week?.Remaining == 60, "Codex displays remaining, not consumed percent");
        Check(parsed.AccountKey.Length == 64 && parsed.AccountKey != "fixture-account", "Codex account identity is hashed");
        var reversed = CodexUsage.Parse(Json(new { accountId = "fixture", ordinaryUsageAllowed = true,
            rateLimits = new { primary = WindowJson(10080, 64), secondary = WindowJson(300, 12) } }), QuotaNow);
        Check(reversed.Five?.Slot == "secondary" && reversed.Week?.Remaining == 36, "Codex legacy slots identified by duration");
        var multi = CodexUsage.Parse(Json(new { rateLimitsByLimitId = new Dictionary<string, object>
        { ["extra"] = new { primary = WindowJson(300, 90) }, ["codex"] = new { primary = WindowJson(300, 20), secondary = WindowJson(10080, 45) } } }), QuotaNow);
        Check(multi.Five?.Bucket == "codex" && multi.Five.Used == 20, "Codex multi-bucket official window preferred");
        Check(Fails(() => CodexUsage.Parse(Json(new { rateLimits = new { primary = new { windowDurationMins = 300, usedPercent = 101, resetsAt = 10 } } }), QuotaNow)), "Codex malformed quota fails closed");
        Check(Fails(() => CodexUsage.Parse(Json(new { rateLimitsByLimitId = new Dictionary<string, object>
        { ["a"] = new { primary = WindowJson(300, 0) }, ["b"] = new { primary = WindowJson(300, 0) } } }), QuotaNow)), "Codex ambiguous buckets cannot trigger a ping");
        Check(!CodexUsage.Parse(Json(new { accountId = "fixture", rateLimits = new { primary = WindowJson(300, 0) } }), QuotaNow).OrdinaryAllowed,
            "Codex missing ordinary-usage permission is not recovery evidence");
        var fixedSamples = Enumerable.Range(0, 4).Select(i => Usage(QuotaNow.AddSeconds(i * 10), false, 3)).ToArray();
        var sliding = Enumerable.Range(0, 4).Select(i => Usage(QuotaNow.AddSeconds(i * 10))).ToArray();
        Check(CodexQuota.Classify(fixedSamples) == CodexWindowState.Anchored, "Codex fixed reset is an active window");
        Check(CodexQuota.Classify(sliding) == CodexWindowState.Unanchored, "Codex four sliding samples over 30 seconds prove idle window");
        Check(CodexQuota.Classify(sliding.Take(3).ToArray()) == CodexWindowState.Unknown, "Codex short evidence cannot start a request");
        Check(CodexQuota.Classify(sliding.Select((s, i) => s with { At = QuotaNow.AddSeconds(i) }).ToArray()) == CodexWindowState.Unknown, "Codex short observation span rejected");
        Check(CodexQuota.Classify(sliding.Select((s, i) => s with { At = s.At.AddMinutes(i * 10) }).ToArray()) == CodexWindowState.Unknown, "Codex sleep and stale samples rejected");
        Check(CodexQuota.Classify(sliding.Select((s, i) => i == 3 ? s with { AccountKey = AccountB } : s).ToArray()) == CodexWindowState.Unknown, "Codex account switch invalidates reset evidence");
        Check(CodexQuota.Classify(sliding.Select(s => s with { Five = s.Five! with { Used = 1 } }).ToArray()) == CodexWindowState.Unknown, "Codex used but drifting data does not count as reset");
        Check(CodexQuota.Classify([u with { Five = null }]) == CodexWindowState.Absent, "Codex absent five-hour window stays absent");
        Check(CodexQuota.Classify([u with { Five = u.Five! with { Used = 100 } }]) == CodexWindowState.Exhausted, "Codex exhausted window cannot trigger");
        Check(CodexQuota.MayStart(u), "Codex confirmed eligible windows may start");
        Check(!CodexQuota.MayStart(u with { Week = null }) && !CodexQuota.MayStart(u with { Week = u.Week! with { Used = 99 } }), "Codex missing or nearly exhausted weekly quota blocks ping");
        Check(!CodexQuota.MayStart(u with { AccountKey = "" }) && !CodexQuota.MayStart(u with { OrdinaryAllowed = false }), "Codex anonymous or disallowed usage blocks ping");
        Check(!CodexQuota.MayStart(u with { Five = u.Five! with { Blocked = true } }) && !CodexQuota.MayStart(u with { Week = u.Week! with { Reset = QuotaNow.AddSeconds(-1) } }), "Codex blocked or stale quota blocks ping");
        Check(CodexModel.Select([ModelJson("gpt-5.6-luna"), ModelJson()]) == new CodexModel("gpt-6-luna", "low"), "Codex live lightweight model with lowest supported effort");
        Check(CodexModel.Select([ModelJson(hidden: true), ModelJson("gpt-5.6-luna", upgrade: "other")]) == null, "Codex hidden and superseded models rejected");
        Check(CodexModel.Select([ModelJson("gpt-6-astra")]) == null && CodexModel.Select([ModelJson(), ModelJson()]) == null, "Codex no expensive or ambiguous model fallback");
        string ResetAfter(TimeSpan remaining) => (string)typeof(MainWindow).GetMethod("QuotaReset", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [QuotaNow + remaining, QuotaNow])!;
        Check(ResetAfter(TimeSpan.FromMinutes(47)) == "Сброс через 47 мин", "Codex reset caption keeps only a short countdown");
        Check(ResetAfter(TimeSpan.FromHours(1)) == "Сброс через 1 ч", "Codex countdown omits zero-minute tail");
        Check(ResetAfter(TimeSpan.FromDays(4) + TimeSpan.FromHours(17)) == "Сброс через 4 д 17 ч", "Codex weekly countdown stays compact");
        Check(ResetAfter(TimeSpan.FromSeconds(1)) == "Сброс через 1 мин" && ResetAfter(TimeSpan.FromSeconds(-1)) == "Ожидаю сброса", "Codex imminent and elapsed reset never show negative or fake zero time");
        using (var folder = new QuotaTemp())
        {
            var store = new CodexQuotaStore(folder.PathName);
            Check(store.Reserve(u), "Codex durable reservation precedes possible request");
            Check(!new CodexQuotaStore(folder.PathName).Reserve(u), "Codex reservation survives process restart");
            Check(store.Get(AccountA)?.Outcome == "uncertain", "Codex unconfirmed send is not success");
            Check(store.Reserve(u with { AccountKey = AccountB }), "Codex deduplication isolates accounts");
            var anchor = Usage(QuotaNow, false, 3, QuotaNow.AddSeconds(20));
            store.Verified(anchor);
            Check(!store.Reserve(Usage(QuotaNow.AddSeconds(79))), "Codex reset boundary has a full 60-second buffer");
            Check(store.Reserve(Usage(QuotaNow.AddSeconds(80))), "Codex next verified window creates one new opportunity");
            Check(!store.Reserve(Usage(QuotaNow.AddSeconds(90))), "Codex same reset never pings twice");
            File.WriteAllText(Path.Combine(folder.PathName, "state.json"), "not json");
            Check(Fails(() => store.Reserve(u)), "Codex corrupt journal blocks instead of forgetting prior sends");
            File.WriteAllText(Path.Combine(folder.PathName, "state.json"), JsonSerializer.Serialize(new[] { new CodexQuotaStore.Account { Key = AccountA, Bucket = "codex", Anchor = long.MaxValue } }));
            Check(Fails(() => store.Reserve(u)), "Codex invalid saved boundary cannot overflow safety delay");
        }
        using (var folder = new QuotaTemp())
        {
            var clock = new QuotaClock(); var client = new QuotaClient(clock) { Active = true };
            using var service = QuotaService(client, clock, folder.PathName);
            await service.RefreshAsync();
            Check(client.Pings == 0 && client.Catalogs == 0 && service.WindowState == CodexWindowState.Anchored, "Codex disabled toggle still reads quotas without inference");
            int reads = client.Reads; clock.Now += TimeSpan.FromMinutes(1); await service.RefreshAsync();
            Check(client.Reads == reads + 1, "Codex cached fixed anchor needs only one quota read");
        }
        using (var folder = new QuotaTemp())
        {
            var clock = new QuotaClock(); var client = new QuotaClient(clock);
            using var service = QuotaService(client, clock, folder.PathName);
            service.SetEnabled(true, monitor: false); await service.RefreshAsync();
            Check(client.Pings == 1 && service.WindowState == CodexWindowState.Anchored, "Codex simulated reset -> one ping -> verified new window");
            string account = CodexUsage.Parse(Payload(u), QuotaNow).AccountKey;
            Check(new CodexQuotaStore(folder.PathName).Get(account)?.Outcome == "verified", "Codex success persisted only after fixed-reset evidence");
            await service.RefreshAsync();
            Check(client.Pings == 1 && !service.Busy && client.Disposals == 2, "Codex active window neither repeats ping nor leaks session");
        }
        using (var folder = new QuotaTemp())
        {
            var clock = new QuotaClock(); var client = new QuotaClient(clock) { FailPing = true };
            using (var service = QuotaService(client, clock, folder.PathName)) { service.SetEnabled(true, monitor: false); await service.RefreshAsync(); }
            clock.Now += TimeSpan.FromMinutes(5);
            var second = new QuotaClient(clock);
            using var restarted = QuotaService(second, clock, folder.PathName);
            restarted.SetEnabled(true, monitor: false); await restarted.RefreshAsync();
            Check(client.Pings == 1 && second.Pings == 0, "Codex interrupted send is not retried after restart");
        }
        using (var folder = new QuotaTemp())
        {
            var clock = new QuotaClock(); var client = new QuotaClient(clock) { KeepSliding = true };
            using var service = QuotaService(client, clock, folder.PathName);
            service.SetEnabled(true, monitor: false); await service.RefreshAsync(); await service.RefreshAsync();
            Check(client.Pings == 1 && service.WindowState == CodexWindowState.Unanchored, "Codex completed turn without anchored quota is not verified or retried");
        }
        foreach (string condition in new[] { "weekly missing", "weekly exhausted", "ordinary usage denied", "no lightweight model", "normal Codex usage starts concurrently", "toggle disabled during check" })
        {
            using var folder = new QuotaTemp(); var clock = new QuotaClock(); var client = new QuotaClient(clock);
            using var service = QuotaService(client, clock, folder.PathName);
            service.SetEnabled(true, monitor: false);
            switch (condition)
            {
                case "weekly missing": client.Weekly = false; break;
                case "weekly exhausted": client.WeekUsed = 100; break;
                case "ordinary usage denied": client.Allowed = false; break;
                case "no lightweight model": client.Models = []; break;
                case "normal Codex usage starts concurrently": client.OnModels = () => client.Active = true; break;
                case "toggle disabled during check": client.OnModels = () => service.SetEnabled(false); break;
            }
            await service.RefreshAsync();
            Check(client.Pings == 0, "Codex negative control: " + condition);
        }
        await CodexTransportTest();
    }

    static async Task CodexTransportTest()
    {
        using var folder = new QuotaTemp();
        string transcript = Path.Combine(folder.PathName, "rpc.jsonl");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--codex-rpc-fixture"); start.ArgumentList.Add(transcript);
        await using (var rpc = await CodexRpc.OpenAsync(start, folder.PathName, CancellationToken.None))
        {
            Check(CodexUsage.Parse(await rpc.ReadUsageAsync(default), QuotaNow).Five?.Remaining == 100, "Codex real NDJSON transport parses quota response");
            Check((await rpc.ModelsAsync(default)).Length == 2, "Codex real NDJSON transport follows catalog pagination");
            await rpc.PingAsync(new("gpt-6-luna", "low"), default);
        }
        var calls = File.ReadAllLines(transcript).Select(s => JsonDocument.Parse(s).RootElement.Clone()).ToArray();
        Check(calls.Count(x => JsonValue.String(x, "method") == "turn/start") == 1, "Codex transport sends exactly one simulated turn");
        var thread = JsonValue.Get(calls.Single(x => JsonValue.String(x, "method") == "thread/start"), "params");
        Check(JsonValue.Get(thread, "ephemeral").ValueKind == JsonValueKind.True && JsonValue.String(thread, "sandbox") == "read-only"
            && JsonValue.String(thread, "approvalPolicy") == "never", "Codex simulated request is ephemeral, read-only, no approvals");
        var turn = JsonValue.Get(calls.Single(x => JsonValue.String(x, "method") == "turn/start"), "params");
        Check(JsonValue.String(turn, "model") == "gpt-6-luna" && JsonValue.String(turn, "effort") == "low" && JsonValue.String(turn, "serviceTier") == "default", "Codex simulated request uses resolved lightweight model and standard tier");
        Check(calls.Any(x => JsonValue.Get(x, "id").TryGetInt32Safe(out int id) && id == 999 && JsonValue.Get(x, "error").ValueKind == JsonValueKind.Object),
            "Codex transport rejects server tool and approval requests");
        var init = JsonValue.Get(calls.First(), "params");
        Check(JsonValue.Get(JsonValue.Get(init, "capabilities"), "experimentalApi").ValueKind == JsonValueKind.False, "Codex stable protocol handshake without experimental fallback");
    }

    static int CodexRpcFixture(string transcript)
    {
        Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
        using var log = new StreamWriter(transcript, false, new UTF8Encoding(false)) { AutoFlush = true };
        void Send(object value) { Console.WriteLine(JsonSerializer.Serialize(value)); Console.Out.Flush(); }
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            log.WriteLine(line); var request = JsonDocument.Parse(line).RootElement;
            string? method = JsonValue.String(request, "method");
            var id = JsonValue.Get(request, "id");
            if (method == null || method == "initialized") continue;
            object result = method switch
            {
                "initialize" => new { userAgent = "fixture" },
                "account/rateLimits/read" => Payload(Usage(QuotaNow)),
                "model/list" when JsonValue.String(JsonValue.Get(request, "params"), "cursor") == null => new { data = new[] { ModelJson() }, nextCursor = "page2" },
                "model/list" => new { data = new[] { ModelJson("gpt-5.6-luna") }, nextCursor = (string?)null },
                "thread/start" => new { thread = new { id = "fixture-thread", ephemeral = true } },
                "turn/start" => new { turn = new { id = "fixture-turn", status = "inProgress" } },
                _ => new { },
            };
            Send(new { method = "fixture/notification", @params = new { } });
            Send(new { id, result });
            if (method == "turn/start")
            {
                Send(new { id = 999, method = "item/commandExecution/requestApproval", @params = new { } });
                Send(new { method = "turn/completed", @params = new { threadId = "fixture-thread", turn = new { id = "fixture-turn", status = "completed" } } });
            }
        }
        return 0;
    }

    static async Task<int> CodexReadOnlyProbe()
    {
        try
        {
            using var folder = new QuotaTemp();
            await using var rpc = await CodexRpc.ConnectAsync(folder.PathName, default);
            var samples = new List<CodexUsage>();
            for (int i = 0; i < 4; i++)
            {
                if (i != 0) await Task.Delay(TimeSpan.FromSeconds(10));
                samples.Add(CodexUsage.Parse(await rpc.ReadUsageAsync(default), DateTimeOffset.UtcNow));
            }
            var usage = samples[^1];
            var model = CodexModel.Select(await rpc.ModelsAsync(default));
            Console.WriteLine(JsonSerializer.Serialize(new { mode = "read-only", inferenceRequests = 0,
                observations = samples.Count, observedSeconds = (samples[^1].At - samples[0].At).TotalSeconds,
                windowState = CodexQuota.Classify(samples).ToString(),
                five = usage.Five == null ? null : new { remaining = usage.Five.Remaining, minutes = usage.Five.Minutes, reset = usage.Five.Reset },
                week = usage.Week == null ? null : new { remaining = usage.Week.Remaining, minutes = usage.Week.Minutes, reset = usage.Week.Reset },
                ordinaryAllowed = usage.OrdinaryAllowed, lightweightModel = model?.Model, effort = model?.Effort }));
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("Codex read-only probe failed: " + e.GetType().Name + "; no inference requested"); return 1; }
    }

    static void CodexPresentation(MainWindow window, FrameworkElement root, string? output)
    {
        var codex = (CodexQuotaService)typeof(MainWindow).GetField("_codex", Private)!.GetValue(window)!;
        Type view = typeof(MainWindow).GetNestedType("View", BindingFlags.NonPublic)!;
        Type panel = typeof(MainWindow).GetNestedType("Panel", BindingFlags.NonPublic)!;
        Set(window, "_cinemaMode", false); Set(window, "_panel", Enum.Parse(panel, "Codex"));
        Call(window, "SyncCinemaAppearance"); Call(window, "SyncCodex");
        Check(((Digits)window.FindName("CodexFiveValue")).Text == "—", "Codex missing quota shows dash, not invented usage");
        var sample = Usage(DateTimeOffset.UtcNow, false, 24, DateTimeOffset.UtcNow.AddHours(3).AddMinutes(12));
        sample = sample with { Week = sample.Week! with { Reset = DateTimeOffset.UtcNow.AddDays(3), Used = 62 } };
        Set(codex, "<Latest>k__BackingField", sample); Set(codex, "<WindowState>k__BackingField", CodexWindowState.Anchored);
        Set(codex, "<Status>k__BackingField", "Ожидаю следующего сброса");
        Call(window, "SyncCodex"); Call(window, "MorphTo", Enum.Parse(view, "Codex")); SnapWindow(window, root);
        Check(((Digits)window.FindName("CodexFiveValue")).Text == "76%" && ((Digits)window.FindName("CodexWeekValue")).Text == "38%", "Codex WPF panel renders both remaining quotas");
        Check(Math.Abs(((Border)window.FindName("CodexFiveFill")).Width - 264 * 0.76) < 0.01, "Codex quota bar matches actual remaining fraction");
        Check(((TextBlock)window.FindName("CodexFiveReset")).Text.StartsWith("Сброс "), "Codex WPF panel renders reset time and countdown");
        Check(!codex.Enabled, "Codex off-screen UI construction does not enable requests");
        Check(((TextBlock)window.FindName("CodexFiveLabel")).Text == "5 часов" && ((TextBlock)window.FindName("CodexWeekLabel")).Text == "Неделя", "Codex headings omit duplicate remaining text and dot separators");
        Check(((TextBlock)window.FindName("CodexAutoLabel")).Text == "Автопинг", "Codex switch caption is concise");
        var status = (TextBlock)window.FindName("CodexStatus");
        var page = (FrameworkElement)window.FindName("CodexView");
        Check(status.Visibility == Visibility.Collapsed && page.Height < 310, "Codex routine waiting has no extra line or empty reserved space");
        var reset = (TextBlock)window.FindName("CodexFiveReset");
        Check(!reset.Text.Contains('·') && reset.ToolTip is string detail && detail.Contains(':') && detail.StartsWith("Сброс "), "Codex exact reset date and time remain available on hover");
        Check(((Digits)window.FindName("CodexFiveValue")).ToolTip as string == "Осталось 76%", "Codex remaining-percent meaning is preserved in tooltip");
        if (output != null) Save(root, Path.Combine(output, "codex-limits.png"));
        double quietHeight = page.Height;
        Set(codex, "<Status>k__BackingField", "Нет доступной лёгкой модели для пинга");
        Call(window, "SyncCodex"); SnapWindow(window, root);
        Check(status.Visibility == Visibility.Visible && page.Height > quietHeight && status.Text == "Нет доступной лёгкой модели для пинга", "Codex actionable message remains visible and expands the card");
        var refresh = (FrameworkElement)window.FindName("CodexRefresh");
        Check(refresh.TranslatePoint(new Point(0, refresh.ActualHeight), page).Y < page.Height - 12, "Codex error-state footer is not clipped");
        Check(((StackPanel)window.FindName("CodexBody")).Children.Cast<UIElement>().Where(c => c.Visibility == Visibility.Visible).All(c => c.Opacity > 0.99), "Codex resizing retains visible content rows");
        if (output != null)
        {
            Call(window, "MorphTo", Enum.Parse(view, "Menu")); SnapWindow(window, root);
            Call(window, "MorphTo", Enum.Parse(view, "Codex")); SnapWindow(window, root);
            Save(root, Path.Combine(output, "codex-message.png"));
        }
        Set(codex, "<Status>k__BackingField", "Автопинг выключен");
        Call(window, "SyncCodex"); SnapWindow(window, root);
        Check(status.Visibility == Visibility.Collapsed && page.Height == quietHeight, "Codex disabled switch also avoids duplicate status text");
        Call(window, "MorphTo", Enum.Parse(view, "Menu")); SnapWindow(window, root);
        var item = (FrameworkElement)window.FindName("MenuCodex");
        Check(IsDescendant(item, (FrameworkElement)window.FindName("MenuView")) && !IsDescendant(item, (FrameworkElement)window.FindName("SettingsView")),
            "Codex is a separate menu entry, not a setting");
        if (output != null) Save(root, Path.Combine(output, "codex-menu.png"));
    }
    static bool IsDescendant(DependencyObject child, DependencyObject parent)
    {
        for (DependencyObject? at = child; at != null; at = System.Windows.Media.VisualTreeHelper.GetParent(at)) if (at == parent) return true;
        return false;
    }
}
