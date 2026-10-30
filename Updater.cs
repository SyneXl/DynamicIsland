using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed partial class Updater : IDisposable
{
    public enum Stage { Idle, Checking, Latest, Available, Loading, NoRelease, Failed }
    public const string Repository = "SyneXl/DynamicIsland";
    internal const string LatestReleaseUrl = "https://api.github.com/repos/" + Repository + "/releases/latest";
    internal const string AssetName = "DynamicIsland.exe";
    const long MaxAssetBytes = 512L << 20;
    static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);
    public static Version CurrentVersion { get; } = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0));
    static readonly HttpClient SharedHttp = CreateClient();
    readonly HttpClient _http;
    readonly Version _current;
    readonly string _target;
    readonly Action<VerifiedUpdate> _handoff;
    readonly Func<string> _workspace;
    readonly TimeSpan _stallTimeout;
    readonly CancellationTokenSource _lifetime = new();
    string _downloadUrl = "", _hash = "";
    DateTime _checkedAt;
    DateTimeOffset _apiRetryAt;

    public event Action? Changed;
    public Stage State { get; private set; }
    public Version? LatestVersion { get; private set; }
    public string[] Notes { get; private set; } = [];
    public string Error { get; private set; } = "";
    public int Percent { get; private set; }
    public long DownloadedBytes { get; private set; }
    public long TotalBytes { get; private set; }

    public Updater(HttpClient? http = null, Version? current = null, string? target = null,
        Action<VerifiedUpdate>? handoff = null, Func<string>? workspace = null, TimeSpan? stallTimeout = null, ReleaseNotesStore? notesStore = null)
    {
        _http = http ?? SharedHttp;
        _current = current ?? CurrentVersion;
        _target = target ?? Environment.ProcessPath ?? "";
        _handoff = handoff ?? UpdateHandoff.Launch;
        _workspace = workspace ?? UpdateHandoff.NewFolder;
        _stallTimeout = stallTimeout ?? TimeSpan.FromSeconds(30);
        _notesStore = notesStore ?? new ReleaseNotesStore(http == null && !App.SmokeTest ? ReleaseNotesStore.DefaultPath : null);
        SelectReleaseNotes(false);
    }

    public async Task CheckAsync(bool force = false)
    {
        if (State is Stage.Checking or Stage.Loading || _lifetime.IsCancellationRequested) return;
        if (!force && DateTime.UtcNow - _checkedAt < CheckInterval) return;
        Error = "";
        SetState(Stage.Checking);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(CheckTimeout);
            if (!await ReadPublishedReleaseAsync(timeout.Token))
            {
                LatestVersion = null;
                Notes = [];
                SetState(Stage.NoRelease);
                return;
            }
            SetState(LatestVersion > _current ? Stage.Available : Stage.Latest);
        }
        catch (Exception ex)
        {
            if (_lifetime.IsCancellationRequested) return;
            App.Log(ex);
            Error = ex is InvalidDataException ? "Файл обновления ещё не готов" : "Не удалось проверить обновления";
            SetState(Stage.Failed);
        }
        finally { _checkedAt = DateTime.UtcNow; }
    }

    void ReadRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()
            || release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
            throw new InvalidDataException("Not a stable published release.");
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+$") || !Version.TryParse(tag.TrimStart('v'), out var version))
            throw new InvalidDataException("Invalid release version.");
        LatestVersion = Normalize(version);
        Notes = release.TryGetProperty("body", out var body) ? ParseNotes(body.GetString() ?? "") : [];
        _downloadUrl = _hash = "";
        TotalBytes = 0;
        if (LatestVersion <= _current) return;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != AssetName) continue;
            string url = asset.GetProperty("browser_download_url").GetString() ?? "";
            string digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
            long size = asset.GetProperty("size").GetInt64();
            string expected = $"https://github.com/{Repository}/releases/download/{tag}/{AssetName}";
            if (!string.Equals(url, expected, StringComparison.OrdinalIgnoreCase)
                || !Regex.IsMatch(digest, @"\Asha256:[0-9a-fA-F]{64}\z") || size is < 64 or > MaxAssetBytes)
                throw new InvalidDataException("Untrusted or incomplete release asset.");
            _downloadUrl = url;
            _hash = digest[7..];
            TotalBytes = size;
            break;
        }
        if (_downloadUrl.Length == 0) throw new InvalidDataException("Executable is not published yet.");
    }

    public async Task<bool> InstallAsync()
    {
        if (State != Stage.Available || _lifetime.IsCancellationRequested) return false;
        string folder = "", file = "";
        Percent = 0;
        DownloadedBytes = 0;
        Error = "";
        SetState(Stage.Loading);
        try
        {
            folder = _workspace();
            file = Path.Combine(folder, AssetName);
            Task notes = LoadReleaseNotesAsync();
            await DownloadAsync(file);
            await notes;
            _handoff(new(file, _target, _hash, LatestVersion!.ToString(), ReleaseNotes));
            return true;
        }
        catch (Exception ex)
        {
            if (!_lifetime.IsCancellationRequested) App.Log(ex);
            try { if (file.Length > 0) File.Delete(file); if (folder.Length > 0) Directory.Delete(folder); }
            catch { }
            Error = ex is InvalidDataException ? "Проверка файла не пройдена" : "Не удалось скачать или установить обновление";
            SetState(Stage.Failed);
            return false;
        }
    }

    async Task DownloadAsync(string destination)
    {
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        stalled.CancelAfter(_stallTimeout);
        using var response = await _http.GetAsync(_downloadUrl, HttpCompletionOption.ResponseHeadersRead, stalled.Token);
        response.EnsureSuccessStatusCode();
        if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Update download left HTTPS.");
        if (TotalBytes == 0 && response.Content.Headers.ContentLength is { } expected) TotalBytes = expected;
        if (TotalBytes < 0 || TotalBytes > MaxAssetBytes) throw new InvalidDataException("Unexpected download length.");
        if (TotalBytes > 0 && response.Content.Headers.ContentLength is { } length && length != TotalBytes)
            throw new InvalidDataException("Unexpected download length.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var from = await response.Content.ReadAsStreamAsync(stalled.Token))
        await using (var to = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
        {
            var buffer = new byte[65536];
            int read;
            while ((read = await from.ReadAsync(buffer, stalled.Token)) > 0)
            {
                stalled.CancelAfter(_stallTimeout);
                if (DownloadedBytes + read > MaxAssetBytes || TotalBytes > 0 && DownloadedBytes + read > TotalBytes) throw new InvalidDataException("Update exceeds advertised size.");
                await to.WriteAsync(buffer.AsMemory(0, read), stalled.Token);
                sha.AppendData(buffer, 0, read);
                DownloadedBytes += read;
                int percent = TotalBytes > 0 ? (int)(DownloadedBytes * 100 / TotalBytes) : 0;
                if (percent == Percent) continue;
                Percent = percent;
                Changed?.Invoke();
            }
            await to.FlushAsync(stalled.Token);
        }
        if (DownloadedBytes < 64 || TotalBytes > 0 && DownloadedBytes != TotalBytes || !Convert.ToHexString(sha.GetHashAndReset()).Equals(_hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Update SHA-256/length mismatch.");
        TotalBytes = DownloadedBytes; Percent = 100; Changed?.Invoke();
        using var exe = File.OpenRead(destination);
        if (exe.ReadByte() != 'M' || exe.ReadByte() != 'Z') throw new InvalidDataException("Update is not a Windows executable.");
    }

    internal static string[] ParseNotes(string notes) => notes.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 2 && line[0] is '-' or '*' or '•' && line[1] == ' ')
        .Select(line => line[2..].Replace("**", "").Replace("`", "").Trim())
        .Where(line => line.Length > 0 && !line.Contains("http", StringComparison.OrdinalIgnoreCase))
        .Take(40).Select(line => line[..Math.Min(line.Length, 400)]).ToArray();

    static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("DynamicIsland/" + CurrentVersion);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }
    void SetState(Stage stage)
    {
        State = stage;
        if (stage is Stage.Available or Stage.Latest or Stage.NoRelease) SelectReleaseNotes(stage == Stage.Available);
        Changed?.Invoke();
    }
    public void Dispose() => _lifetime.Cancel();
}

sealed record VerifiedUpdate(string Payload, string Target, string Sha256, string Version, ReleaseNotesSnapshot? Notes = null);
