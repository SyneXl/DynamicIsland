using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using DynamicIsland;

static partial class Program
{
    sealed class UpdateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return reply(request, token);
        }
    }

    static byte[] UpdateBytes() { byte[] bytes = new byte[1024]; new Random(42).NextBytes(bytes); bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; return bytes; }
    static string ReleaseJson(byte[] bytes, string? digest = null, string? url = null, string tag = "v1.29.0", bool draft = false, bool prerelease = false) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease, body = "# Release\n- New headset notices\n- Lyrics settings\n",
        assets = new[] { new { name = "DynamicIsland.exe", size = bytes.Length,
            digest = digest ?? "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)),
            browser_download_url = url ?? $"https://github.com/SyneXl/DynamicIsland/releases/download/{tag}/DynamicIsland.exe" } }
    });
    static HttpResponseMessage JsonReply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    static async Task<int> UpdaterTests()
    {
        try
        {
            byte[] bytes = UpdateBytes();
            await RollbackSharingTest();
            await UpdaterPageTests(bytes);
            using var noReleaseHandler = new UpdateHttp((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
            using (var updater = new Updater(new HttpClient(noReleaseHandler), new Version(1, 28, 0)))
            {
                await updater.CheckAsync();
                Check(updater.State == Updater.Stage.NoRelease && updater.Error == "", "new fork without releases is a quiet valid state");
                await updater.CheckAsync(); Check(noReleaseHandler.Requests == 1, "automatic checks are throttled");
                await updater.CheckAsync(true); Check(noReleaseHandler.Requests == 2, "explicit refresh bypasses the interval");
                Check(!await updater.InstallAsync(), "no-release state cannot start an installer");
            }
            foreach (var (json, name) in new[]
            {
                (ReleaseJson(bytes, digest: ""), "missing digest"),
                (ReleaseJson(bytes, digest: "sha256:bad"), "malformed digest"),
                (ReleaseJson(bytes, url: "https://github.com/mihailkotovski/DynamicIsland/releases/download/v1.29.0/DynamicIsland.exe"), "original author channel"),
                (ReleaseJson(bytes, url: "http://github.com/SyneXl/DynamicIsland/releases/download/v1.29.0/DynamicIsland.exe"), "HTTP payload"),
                (ReleaseJson(bytes, tag: "v1.29.0-preview"), "invalid stable tag"),
                (ReleaseJson(bytes, draft: true), "draft release"),
                (ReleaseJson(bytes, prerelease: true), "prerelease"),
                ("{\"tag_name\":\"v1.29.0\",\"assets\":[]}", "missing executable")
            })
            {
                using var handler = new UpdateHttp((_, _) => Task.FromResult(JsonReply(json)));
                using var updater = new Updater(new HttpClient(handler), new Version(1, 28, 0));
                await updater.CheckAsync();
                Check(updater.State == Updater.Stage.Failed && !await updater.InstallAsync(), "reject " + name);
            }
            foreach (var version in new[] { new Version(1, 29, 0), new Version(2, 0, 0) })
            {
                using var handler = new UpdateHttp((_, _) => Task.FromResult(JsonReply(ReleaseJson(bytes))));
                using var updater = new Updater(new HttpClient(handler), version);
                await updater.CheckAsync();
                Check(updater.State == Updater.Stage.Latest, "no downgrade/equal-version reinstall for " + version);
            }
            foreach (string mode in new[] { "valid", "hash", "truncated", "oversized", "not-exe", "stalled" })
            {
                byte[] downloaded = bytes.ToArray();
                if (mode == "hash") downloaded[22] ^= 1;
                if (mode == "truncated") downloaded = downloaded[..512];
                if (mode == "oversized") downloaded = [.. downloaded, 10];
                if (mode == "not-exe") downloaded[0] = 0;
                string metadata = ReleaseJson(mode == "not-exe" ? downloaded : bytes);
                string root = Path.Combine(Path.GetTempPath(), "island-updater-tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                string installed = Path.Combine(root, "installed.exe");
                await File.WriteAllTextAsync(installed, "ORIGINAL");
                bool handedOff = false;
                using var handler = new UpdateHttp(async (request, token) =>
                {
                    if (request.RequestUri!.AbsoluteUri == Updater.LatestReleaseUrl) return JsonReply(metadata);
                    if (mode == "stalled") await Task.Delay(Timeout.Infinite, token);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(downloaded) };
                });
                string stage = Path.Combine(root, "stage");
                using var updater = new Updater(new HttpClient(handler), new Version(1, 28, 0), installed,
                    update => { handedOff = update.Sha256.Equals(Convert.ToHexString(SHA256.HashData(bytes)), StringComparison.OrdinalIgnoreCase); },
                    () => { Directory.CreateDirectory(stage); return stage; }, TimeSpan.FromMilliseconds(150));
                await updater.CheckAsync();
                Check(updater.State == Updater.Stage.Available && updater.LatestVersion == new Version(1, 29, 0), "verified release is available: " + mode);
                bool accepted = await updater.InstallAsync();
                Check(accepted == (mode == "valid") && handedOff == accepted, "handoff only after verification: " + mode);
                Check(await File.ReadAllTextAsync(installed) == "ORIGINAL", "download leaves the running executable intact: " + mode);
                if (accepted) Check(updater.Percent == 100 && updater.DownloadedBytes == bytes.Length, "download progress reaches verified byte count");
                else Check(updater.State == Updater.Stage.Failed && !File.Exists(Path.Combine(stage, "DynamicIsland.exe")), "failed/stalled download is removed: " + mode);
                foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.Delete(f);
                if (Directory.Exists(stage)) Directory.Delete(stage);
                Directory.Delete(root);
            }
            Check(Updater.ParseNotes(string.Join('\n', Enumerable.Range(0, 35).Select(i => "- Item " + i))).Length == 35,
                "release notes are not silently cut to the old six-item limit");
            Console.WriteLine($"PASS: {_checks} updater checks; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static async Task RollbackSharingTest()
    {
        string folder = Path.Combine(Path.GetTempPath(), "island-rollback-sharing-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, "target.exe"), backup = Path.Combine(folder, "backup.old");
        await File.WriteAllTextAsync(target, "NEW"); await File.WriteAllTextAsync(backup, "ORIGINAL");
        using (var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Task restore = UpdateHandoff.RestoreBackupAsync(backup, target);
            await Task.Delay(180);
            Check(!restore.IsCompleted && File.Exists(backup), "rollback preserves backup and waits through a transient executable sharing lock");
            held.Dispose(); await restore;
        }
        Check(await File.ReadAllTextAsync(target) == "ORIGINAL" && !File.Exists(backup), "rollback restores exact original once the file is released");
        File.Delete(target); Directory.Delete(folder);
    }

    static HttpResponseMessage UpdatePage(string url, string html, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, url), Content = new StringContent(html)
    };

    static async Task UpdaterPageTests(byte[] bytes)
    {
        const string tag = "v1.29.0";
        string releasePage = $"https://github.com/{Updater.Repository}/releases/tag/{tag}";
        string digest = Convert.ToHexString(SHA256.HashData(bytes));
        string asset = $"<li><a href=\"/{Updater.Repository}/releases/download/{tag}/DynamicIsland.exe\">DynamicIsland.exe</a><clipboard-copy value=\"sha256:{digest}\"></clipboard-copy></li>";
        string notes = "<div class=\"markdown-body tmp-my-3\"><ul><li><strong>Fix</strong> &amp; update</li><li>https://example.org/not-a-release-note</li></ul></div>";
        Check(Updater.PageNotes(notes).SequenceEqual(["Fix & update"]), "release-page notes are decoded, stripped and filtered like API notes");
        foreach (string mode in new[] { "403", "429", "503", "network", "api-timeout", "latest", "no-release", "missing-hash", "other-row-hash", "foreign-repo", "foreign-host", "prerelease", "bad-download", "no-length", "all-offline" })
        {
            string root = Path.Combine(Path.GetTempPath(), "island-page-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); int apiCalls = 0, pageCalls = 0; bool handoff = false;
            using var handler = new UpdateHttp((request, _) =>
            {
                Check(request.Headers.Authorization == null, "public updater never embeds GitHub credentials: " + mode);
                string url = request.RequestUri!.AbsoluteUri;
                if (url == Updater.LatestReleaseUrl)
                {
                    apiCalls++;
                    if (mode is "network" or "all-offline") throw new HttpRequestException("API offline");
                    if (mode == "api-timeout") throw new TaskCanceledException("API timeout");
                    var response = new HttpResponseMessage(mode == "429" ? HttpStatusCode.TooManyRequests : mode == "503" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.Forbidden);
                    response.Headers.TryAddWithoutValidation("X-RateLimit-Reset", DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds().ToString());
                    return Task.FromResult(response);
                }
                if (url == Updater.LatestReleasePage)
                {
                    pageCalls++;
                    if (mode == "all-offline") throw new HttpRequestException("Page offline");
                    string final = mode switch
                    {
                        "foreign-repo" => "https://github.com/mihailkotovski/DynamicIsland/releases/tag/v1.29.0",
                        "foreign-host" => "https://example.org/SyneXl/DynamicIsland/releases/tag/v1.29.0",
                        "prerelease" => releasePage + "-beta.1",
                        _ => releasePage
                    };
                    return Task.FromResult(UpdatePage(final, notes, mode == "no-release" ? HttpStatusCode.NotFound : HttpStatusCode.OK));
                }
                if (url.StartsWith(Updater.ReleaseAssetsPage))
                {
                    string html = mode == "missing-hash" ? asset.Replace("value=\"sha256:", "ignored=\"sha256:")
                        : mode == "other-row-hash" ? asset.Replace("</a>", "</a></li><li>") : asset;
                    return Task.FromResult(UpdatePage(url, html));
                }
                byte[] payload = bytes.ToArray(); if (mode == "bad-download") payload[^1] ^= 1;
                HttpContent content = mode == "no-length" ? new ChunkedUpdateContent(payload) : new ByteArrayContent(payload);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content });
            });
            using var updater = new Updater(new HttpClient(handler), mode == "latest" ? new Version(1, 29, 0) : new Version(1, 28, 0),
                Path.Combine(root, "target.exe"), _ => handoff = true, () => root);
            await updater.CheckAsync(true);
            await updater.CheckAsync(true);
            Check(apiCalls == 1 && pageCalls == 2, "manual retry honors API reset time while the public page remains usable: " + mode);
            if (mode == "latest") Check(updater.State == Updater.Stage.Latest, "rate-limited installed version is still correctly recognized as current");
            else if (mode == "no-release") Check(updater.State == Updater.Stage.NoRelease, "public page confirms valid empty release channel");
            else if (mode is "missing-hash" or "other-row-hash" or "foreign-repo" or "foreign-host" or "prerelease" or "all-offline")
                Check(updater.State == Updater.Stage.Failed && !await updater.InstallAsync() && !handoff, "fallback fails closed: " + mode);
            else
            {
                Check(updater.State == Updater.Stage.Available && updater.Notes.SequenceEqual(["Fix & update"]), "fallback finds verified release: " + mode);
                bool installed = await updater.InstallAsync();
                Check(installed == (mode != "bad-download") && handoff == installed, "page-channel SHA-256 and handoff enforced: " + mode);
                if (mode == "no-length") Check(updater.TotalBytes == bytes.Length && updater.Percent == 100, "chunked fallback download finishes with verified size and progress");
            }
            if (Directory.Exists(root)) foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }

    sealed class ChunkedUpdateContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
    }

    static async Task<int> UpdateNetworkTest(string output)
    {
        Directory.CreateDirectory(output);
        VerifiedUpdate? received = null;
        using var updater = new Updater(current: new Version(1, 0, 0), target: Path.Combine(output, "untouched.exe"),
            handoff: update => received = update, workspace: () => { string f = Path.Combine(output, "download"); Directory.CreateDirectory(f); return f; });
        await updater.CheckAsync(true);
        Check(updater.State == Updater.Stage.Available, "live public fork release is discoverable without credentials");
        Check(await updater.InstallAsync() && received != null, "live release downloads and passes mandatory SHA-256");
        await File.WriteAllTextAsync(Path.Combine(output, "network-result.json"), JsonSerializer.Serialize(new
        { version = updater.LatestVersion?.ToString(), updater.DownloadedBytes, sha256 = received!.Sha256, repository = Updater.Repository }));
        Console.WriteLine($"PASS: {_checks} live update checks; exit=0");
        return 0;
    }

    static async Task<int> UpdateHandoffTests(string sourceExe, string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            string? baseline = Environment.GetEnvironmentVariable("ISLAND_OTA_BASELINE");
            var baselineInfo = FileVersionInfo.GetVersionInfo(baseline ?? sourceExe);
            var baselineVersion = new Version(baselineInfo.FileMajorPart, baselineInfo.FileMinorPart, baselineInfo.FileBuildPart);
            if (baseline != null) Check(baselineVersion < Updater.CurrentVersion, "migration baseline is an actually older executable, not just an old folder name");
            foreach (string variant in new[] { "success", "real-helper", "parent-wait", "bad-hash", "changed-target", "rollback", "notes-write-fail" })
            {
                string test = Path.Combine(output, "Каталог с пробелами", variant); Directory.CreateDirectory(test);
                string target = Path.Combine(test, "DynamicIsland.exe");
                File.Copy(Environment.GetEnvironmentVariable("ISLAND_OTA_BASELINE") ?? sourceExe, target, true);
                // Change a harmless appended overlay byte so restoration is observable.
                await using (var stream = new FileStream(target, FileMode.Append)) await stream.WriteAsync(new byte[] { 42 });
                string original = UpdateHandoff.Hash(target);
                string folder = UpdateHandoff.NewFolder();
                string payload = Path.Combine(folder, "DynamicIsland.exe"); File.Copy(sourceExe, payload);
                string hash = UpdateHandoff.Hash(payload);
                string smoke = Path.Combine(test, "smoke.json");
                string[] args = ["--updated", Path.Combine(folder, "ready.json"), "--smoke-test", smoke];
                Process? parent = null;
                if (variant == "parent-wait")
                {
                    var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
                    foreach (string argument in new[] { "-NoProfile", "-Command", "Start-Sleep -Milliseconds 900" }) info.ArgumentList.Add(argument);
                    parent = Process.Start(info)!;
                }
                var plan = new UpdateHandoff.Plan(target, payload, variant == "bad-hash" ? new string('0', 64) : hash,
                    variant == "rollback" ? "99.0.0" : Updater.CurrentVersion.ToString(),
                    variant == "changed-target" ? new string('0', 64) : original, parent?.Id ?? int.MaxValue, parent?.StartTime.ToUniversalTime().Ticks ?? 0, args);
                string manifest = Path.Combine(folder, "plan.json");
                await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(plan));
                int notesCommits = 0; var noteStore = new ReleaseNotesStore(Path.Combine(test, "history.json"));
                bool CommitNotes(ReleaseNotesSnapshot notes)
                {
                    notesCommits++;
                    Check(File.Exists(Path.Combine(folder, "ready.json")) && UpdateHandoff.Hash(target) == hash,
                        "installed history commits only after exact new executable acknowledges startup");
                    return variant != "notes-write-fail" && noteStore.SaveInstalled(notes);
                }
                async Task<int> RunRealHelper()
                {
                    var info = new ProcessStartInfo(payload) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = folder };
                    info.ArgumentList.Add("--apply-update"); info.ArgumentList.Add(manifest);
                    using var helper = Process.Start(info)!;
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await helper.WaitForExitAsync(timeout.Token);
                    return helper.ExitCode;
                }
                Task<int> operation = variant == "real-helper" ? RunRealHelper() : UpdateHandoff.RunAsync(manifest, TimeSpan.FromSeconds(8), CommitNotes);
                if (parent != null)
                {
                    await Task.Delay(150);
                    Check(UpdateHandoff.Hash(target) == original, "target is never replaced while the original process is alive");
                }
                int code = await operation;
                parent?.Dispose();
                string actual = UpdateHandoff.Hash(target);
                if (variant is "success" or "parent-wait" or "real-helper" or "notes-write-fail")
                {
                    Check(code == 0 && actual == hash, "atomic replacement installs the candidate only after the previous process is gone");
                    Check(File.Exists(Path.Combine(folder, "ready.json")), "new WPF process acknowledges its PID and actual version");
                }
                else Check(code == 1 && actual == original, "original file retained/restored: " + variant);
                if (variant is "success" or "parent-wait")
                {
                    Check(notesCommits == 1 && new ReleaseNotesStore(Path.Combine(test, "history.json")).Installed(Updater.CurrentVersion)?.Complete == true,
                        "old updater plan without notes recovers complete history from bundled release archive");
                    if (baselineVersion < Updater.CurrentVersion)
                        Check(noteStore.Installed(Updater.CurrentVersion)?.From == baselineVersion.ToString()
                            && noteStore.Installed(Updater.CurrentVersion)!.Releases.All(r => Version.Parse(r.Version) > baselineVersion),
                            "real migration history starts at actual old PE version and excludes that installed base");
                }
                if (variant is "bad-hash" or "changed-target" or "rollback") Check(notesCommits == 0, "failed/rolled-back update cannot commit installed history: " + variant);
                if (variant == "notes-write-fail") Check(notesCommits == 1 && !File.Exists(Path.Combine(test, "history.json")), "history write failure does not roll back a successfully acknowledged executable");
                using var result = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(folder, "result.json")));
                Check(result.RootElement.GetProperty("restored").GetBoolean() == (variant == "rollback"), "rollback evidence: " + variant);
                File.Copy(Path.Combine(folder, "result.json"), Path.Combine(test, "result.json"), true);
                // Wait only for our smoke-test copy, never the user's island.
                await Task.Delay(1300);
                foreach (string f in Directory.EnumerateFiles(folder)) File.Delete(f);
                Directory.Delete(folder);
            }
            Console.WriteLine($"PASS: {_checks} handoff checks; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
