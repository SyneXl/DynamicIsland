using System.IO;
using System.Text.Json;

namespace DynamicIsland;

sealed class CodexQuotaStore(string directory)
{
    public sealed class Account
    {
        public string Key { get; set; } = "";
        public string Bucket { get; set; } = "";
        public long? Anchor { get; set; }
        public long? ReservedFor { get; set; }
        public long? AttemptAt { get; set; }
        public string Outcome { get; set; } = "";
    }

    List<Account> Read()
    {
        string file = Path.Combine(directory, "state.json");
        if (!File.Exists(file)) return [];
        if (new FileInfo(file).Length > 1024 * 1024) throw new CodexFailure("Повреждён журнал автопинга");
        try
        {
            var accounts = JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(file)) ?? throw new JsonException();
            if (accounts.Any(a => a == null || a.Key == null || a.Key.Length != 64 || !a.Key.All(Uri.IsHexDigit) || a.Bucket != "codex"
                || a.Anchor is < 0 or > 253402300799 || a.AttemptAt is < 0 or > 253402300799 || a.ReservedFor is < 0 or > 253402300799)) throw new JsonException();
            if (accounts.Select(a => a.Key).Distinct().Count() != accounts.Count) throw new JsonException();
            return accounts;
        }
        catch (JsonException) { throw new CodexFailure("Повреждён журнал автопинга"); }
    }

    T Edit<T>(string key, string bucket, Func<Account, T> edit)
    {
        Directory.CreateDirectory(directory);
        // Cross-process lock plus atomic replace: uncertain requests survive restarts.
        using var gate = new FileStream(Path.Combine(directory, "state.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var data = Read();
        var account = data.SingleOrDefault(a => a.Key == key && a.Bucket == bucket);
        if (account == null) { account = new() { Key = key, Bucket = bucket }; data.Add(account); }
        T result = edit(account);
        string temp = Path.Combine(directory, "state." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, data);
                output.Flush(flushToDisk: true);
            }
            File.Move(temp, Path.Combine(directory, "state.json"), overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return result;
    }

    public Account? Get(string key) => Read().SingleOrDefault(a => a.Key == key);

    public void Anchor(CodexUsage usage)
    {
        if (usage.AccountKey.Length != 64 || usage.Five is not { Bucket: "codex" } five) return;
        Edit(usage.AccountKey, five.Bucket, a => { a.Anchor = five.Reset.ToUnixTimeSeconds(); return true; });
    }

    public bool Reserve(CodexUsage usage)
    {
        if (!CodexQuota.MayStart(usage)) return false;
        return Edit(usage.AccountKey, "codex", a =>
        {
            long opportunity = a.Anchor ?? 0;
            if (a.ReservedFor == opportunity || a.Anchor is { } boundary && usage.At.ToUnixTimeSeconds() < boundary + 60) return false;
            a.ReservedFor = opportunity;
            a.AttemptAt = usage.At.ToUnixTimeSeconds();
            a.Outcome = "uncertain"; // Durable BEFORE turn/start, even if the process dies.
            return true;
        });
    }

    public void Verified(CodexUsage usage) => Edit(usage.AccountKey, "codex", a =>
    {
        a.Anchor = usage.Five!.Reset.ToUnixTimeSeconds();
        a.Outcome = "verified";
        return true;
    });
}
