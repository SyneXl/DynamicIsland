using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DynamicIsland;

interface ICodexSession : IAsyncDisposable
{
    Task<JsonElement> ReadUsageAsync(CancellationToken stop);
    Task<JsonElement[]> ModelsAsync(CancellationToken stop);
    Task PingAsync(CodexModel model, CancellationToken stop);
}

sealed class CodexRpc : ICodexSession
{
    readonly Process _process;
    readonly string _workspace;
    int _id;
    string? _thread;
    bool _completed;

    CodexRpc(Process process, string workspace)
    {
        _process = process;
        _workspace = workspace;
        // Drain diagnostics without logging credentials, user paths or upstream errors.
        _ = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() != null) { } } catch { } });
    }

    public static string? FindExecutable()
    {
        try
        {
            string desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(desktop) && Directory.GetFiles(desktop, "codex.exe", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() is { } installed) return installed;
            foreach (var p in Process.GetProcessesByName("codex"))
                using (p) { try { if (p.MainModule?.FileName is { } path && File.Exists(path)) return path; } catch { } }
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                string candidate = Path.Combine(directory.Trim('"'), "codex.exe");
                if (Path.IsPathFullyQualified(candidate) && File.Exists(candidate)) return candidate;
            }
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
            if (Directory.Exists(npm)) return Directory.GetFiles(npm, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { }
        return null;
    }

    public static async Task<ICodexSession> ConnectAsync(string directory, CancellationToken stop)
    {
        string executable = FindExecutable() ?? throw new CodexFailure("Codex не найден");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = directory,
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--stdio");
        return await OpenAsync(start, directory, stop);
    }

    internal static async Task<ICodexSession> OpenAsync(ProcessStartInfo start, string directory, CancellationToken stop)
    {
        var client = new CodexRpc(Process.Start(start) ?? throw new CodexFailure("Codex не запустился"), directory);
        try
        {
            await client.CallAsync("initialize", new { clientInfo = new { name = "dynamic_island_limits", title = "Dynamic Island", version = "1.0.0" }, capabilities = new { experimentalApi = false } }, stop);
            await client.SendAsync(new { method = "initialized" }, stop);
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    async Task SendAsync(object message, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), stop);
        await _process.StandardInput.FlushAsync(stop);
    }

    async Task<JsonElement> ReceiveAsync(CancellationToken stop)
    {
        string? line = await _process.StandardOutput.ReadLineAsync(stop);
        if (line == null) throw new CodexFailure("Соединение с Codex закрыто");
        if (line.Length > 2 * 1024 * 1024) throw new CodexFailure("Неожиданный ответ Codex");
        using var doc = JsonDocument.Parse(line);
        var value = doc.RootElement.Clone();
        string? method = JsonValue.String(value, "method");
        if (method == "turn/completed" && JsonValue.String(JsonValue.Get(value, "params"), "threadId") == _thread) _completed = true;
        if (method != null && JsonValue.Get(value, "id").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            // This client never grants approvals or runs a server-requested tool.
            await SendAsync(new { id = JsonValue.Get(value, "id"), error = new { code = -32601, message = "Tools and approvals are not supported by this client" } }, stop);
        }
        return value;
    }

    async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken stop)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        int id = ++_id;
        await SendAsync(new { id, method, @params = parameters }, deadline.Token);
        while (true)
        {
            var message = await ReceiveAsync(deadline.Token);
            if (!JsonValue.Get(message, "id").TryGetInt32Safe(out int received) || received != id || JsonValue.Get(message, "method").ValueKind == JsonValueKind.String) continue;
            if (JsonValue.Get(message, "error").ValueKind == JsonValueKind.Object)
                throw new CodexFailure(method == "account/rateLimits/read" ? "Лимиты недоступны — проверь вход в Codex" : "Codex отклонил запрос");
            var result = JsonValue.Get(message, "result");
            if (result.ValueKind != JsonValueKind.Object) throw new CodexFailure("Неожиданный ответ Codex");
            return result;
        }
    }

    public Task<JsonElement> ReadUsageAsync(CancellationToken stop) => CallAsync("account/rateLimits/read", null, stop);

    public async Task<JsonElement[]> ModelsAsync(CancellationToken stop)
    {
        var models = new List<JsonElement>();
        string? cursor = null;
        var cursors = new HashSet<string>();
        for (int page = 0; page < 20; page++)
        {
            var result = await CallAsync("model/list", new { includeHidden = false, limit = 100, cursor }, stop);
            var data = JsonValue.Get(result, "data");
            if (data.ValueKind != JsonValueKind.Array) break;
            models.AddRange(data.EnumerateArray().Select(x => x.Clone()));
            cursor = JsonValue.String(result, "nextCursor");
            if (cursor == null) return models.ToArray();
            if (!cursors.Add(cursor)) break;
        }
        throw new CodexFailure("Не удалось проверить модель для пинга");
    }

    internal static object ThreadParameters(CodexModel model, string workspace) => new
    {
        ephemeral = true, cwd = workspace, model = model.Model, modelProvider = "openai",
        serviceTier = "default", sandbox = "read-only", approvalPolicy = "never",
        baseInstructions = "Respond only OK. Do not use tools, access files, browse, or delegate.",
        developerInstructions = "This is a minimal quota-window start. Respond only OK. Do not use tools.",
        config = new Dictionary<string, object> { ["mcp_servers"] = new Dictionary<string, object>(), ["web_search"] = "disabled", ["project_doc_max_bytes"] = 0 },
    };

    internal static object TurnParameters(string thread, CodexModel model) => new
    {
        threadId = thread, model = model.Model, effort = model.Effort, serviceTier = "default",
        turnTrigger = "dynamic-island-window-start", input = new[] { new { type = "text", text = "Reply only OK. Do not use tools." } },
    };

    public async Task PingAsync(CodexModel model, CancellationToken stop)
    {
        var started = await CallAsync("thread/start", ThreadParameters(model, _workspace), stop);
        _thread = JsonValue.String(JsonValue.Get(started, "thread"), "id") ?? throw new CodexFailure("Codex не создал временный запрос");
        if (JsonValue.Get(JsonValue.Get(started, "thread"), "ephemeral").ValueKind != JsonValueKind.True) throw new CodexFailure("Codex не подтвердил временный запрос");
        await CallAsync("turn/start", TurnParameters(_thread, model), stop); // Exactly once; never retried.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        while (!_completed) await ReceiveAsync(deadline.Token);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await _process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                using var stopped = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _process.WaitForExitAsync(stopped.Token);
            }
        }
        catch { }
        finally { _process.Dispose(); }
    }
}
