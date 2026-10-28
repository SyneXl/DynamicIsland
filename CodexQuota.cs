using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DynamicIsland;

enum CodexWindowState { Unknown, Absent, Anchored, Unanchored, Exhausted }

sealed record CodexWindow(string Bucket, string Slot, double Used, int Minutes, DateTimeOffset Reset, bool Blocked)
{
    public double Remaining => 100 - Used;
    public string Identity => Bucket + "/" + Slot + "/" + Minutes;
}

sealed record CodexUsage(DateTimeOffset At, string AccountKey, CodexWindow? Five, CodexWindow? Week, bool OrdinaryAllowed)
{
    public static CodexUsage Parse(JsonElement payload, DateTimeOffset at)
    {
        string account = JsonValue.String(payload, "accountId") ?? "";
        string key = account.Length == 0 ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(account)));
        var windows = new List<CodexWindow>();
        var map = JsonValue.Get(payload, "rateLimitsByLimitId");
        if (map.ValueKind == JsonValueKind.Object && map.EnumerateObject().Any())
        {
            foreach (var bucket in map.EnumerateObject()) ReadBucket(bucket.Value, bucket.Name, windows);
        }
        else ReadBucket(JsonValue.Get(payload, "rateLimits"), "codex", windows);
        var five = windows.Where(w => w.Minutes is >= 270 and <= 330).ToArray();
        var official = five.Where(w => w.Bucket == "codex").ToArray();
        CodexWindow? selected = official.Length == 1 ? official[0] : official.Length == 0 && five.Length == 1 ? five[0] : null;
        if (five.Length > 0 && selected == null) throw new CodexFailure("Неоднозначные данные лимитов");
        var weekly = windows.Where(w => w.Minutes == 10080 && w.Bucket == "codex").ToArray();
        if (weekly.Length > 1) throw new CodexFailure("Неоднозначный недельный лимит");
        if (windows.Count == 0) throw new CodexFailure("Codex не передал лимиты");
        return new(at, key, selected, weekly.SingleOrDefault(), JsonValue.Get(payload, "ordinaryUsageAllowed").ValueKind == JsonValueKind.True);
    }

    static void ReadBucket(JsonElement bucket, string fallback, List<CodexWindow> windows)
    {
        if (bucket.ValueKind != JsonValueKind.Object) return;
        string id = JsonValue.String(bucket, "limitId") ?? fallback;
        bool blocked = JsonValue.Get(bucket, "spendControlReached").ValueKind == JsonValueKind.True
            || !string.IsNullOrEmpty(JsonValue.String(bucket, "rateLimitReachedType"));
        foreach (string slot in new[] { "primary", "secondary" })
        {
            var w = JsonValue.Get(bucket, slot);
            if (w.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            if (!JsonValue.Get(w, "usedPercent").TryGetDoubleSafe(out double used) || !double.IsFinite(used) || used is < 0 or > 100
                || !JsonValue.Get(w, "windowDurationMins").TryGetInt32Safe(out int minutes) || minutes <= 0
                || !JsonValue.Get(w, "resetsAt").TryGetInt64Safe(out long seconds) || seconds <= 0 || seconds > 253402300799)
                throw new CodexFailure("Некорректные данные лимитов");
            windows.Add(new(id, slot, used, minutes, DateTimeOffset.FromUnixTimeSeconds(seconds), blocked));
        }
    }
}

static class CodexQuota
{
    // Functional reset-pattern research: UsageLoop (MIT); see docs/codex-limits.md.
    public static CodexWindowState Classify(IReadOnlyList<CodexUsage> samples)
    {
        if (samples.Count == 0) return CodexWindowState.Unknown;
        if (samples.All(s => s.Five == null)) return CodexWindowState.Absent;
        if (samples[^1].Five is { } last && (last.Used >= 100 || last.Blocked)) return CodexWindowState.Exhausted;
        if (samples.Count < 4 || samples.Any(s => s.Five == null)) return CodexWindowState.Unknown;
        var first = samples[0];
        double elapsed = (samples[^1].At - first.At).TotalSeconds;
        if (elapsed is < 30 or > 180 || samples.Any(s => s.AccountKey != first.AccountKey || s.Five!.Identity != first.Five!.Identity || s.Five.Reset <= s.At)) return CodexWindowState.Unknown;
        for (int i = 1; i < samples.Count; i++) if (samples[i].At <= samples[i - 1].At) return CodexWindowState.Unknown;
        var resets = samples.Select(s => s.Five!.Reset.ToUnixTimeSeconds()).ToArray();
        if (resets.Max() - resets.Min() <= 2) return CodexWindowState.Anchored;
        if (samples.Any(s => s.Five!.Used != 0)) return CodexWindowState.Unknown;
        var distance = samples.Select(s => (s.Five!.Reset - s.At).TotalSeconds).ToArray();
        if (distance.Max() - distance.Min() > 5 || samples.Any(s => Math.Abs((s.Five!.Reset - s.At).TotalSeconds - s.Five.Minutes * 60) > 120)) return CodexWindowState.Unknown;
        for (int i = 1; i < samples.Count; i++)
        {
            double wall = (samples[i].At - samples[i - 1].At).TotalSeconds;
            if (Math.Abs(resets[i] - resets[i - 1] - wall) > Math.Max(3, wall * 0.2)) return CodexWindowState.Unknown;
        }
        return CodexWindowState.Unanchored;
    }

    public static bool MayStart(CodexUsage usage) => usage.AccountKey.Length == 64 && usage.OrdinaryAllowed
        && usage.Five is { Bucket: "codex", Minutes: 300, Used: 0, Blocked: false } five && five.Reset > usage.At
        && usage.Week is { Blocked: false, Used: < 99 } week && week.Reset > usage.At;
}

sealed record CodexModel(string Model, string Effort)
{
    public static CodexModel? Select(IEnumerable<JsonElement> catalog)
    {
        var entries = catalog.ToArray();
        foreach (string name in new[] { "gpt-6-luna", "gpt-5.6-luna" })
        {
            var matching = entries.Where(e => (JsonValue.String(e, "model") ?? JsonValue.String(e, "id")) == name).ToArray();
            if (matching.Length > 1) return null;
            if (matching.Length == 0) continue;
            var m = matching[0];
            if (JsonValue.Get(m, "hidden").ValueKind == JsonValueKind.True || JsonValue.Get(m, "upgradeInfo").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                || !string.IsNullOrEmpty(JsonValue.String(m, "upgrade"))) continue;
            var modalities = JsonValue.Get(m, "inputModalities");
            var efforts = JsonValue.Get(m, "supportedReasoningEfforts");
            if (modalities.ValueKind != JsonValueKind.Array || !modalities.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == "text") || efforts.ValueKind != JsonValueKind.Array) continue;
            var allowed = efforts.EnumerateArray().Select(x => JsonValue.String(x, "reasoningEffort")).ToArray();
            foreach (string effort in new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" })
                if (allowed.Contains(effort)) return new(name, effort);
        }
        return null;
    }
}

sealed class CodexFailure(string message) : Exception(message);

static class JsonValue
{
    public static JsonElement Get(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;
    public static string? String(JsonElement value, string name) => Get(value, name) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;
    public static bool TryGetDoubleSafe(this JsonElement value, out double number) { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out number); }
    public static bool TryGetInt32Safe(this JsonElement value, out int number) { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number); }
    public static bool TryGetInt64Safe(this JsonElement value, out long number) { number = 0; return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out number); }
}
