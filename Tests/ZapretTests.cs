using System.IO;
using System.Text.Json;
using DynamicIsland;

static partial class Program
{
    static int ZapretProcessScopeTests(string ownedExe, string foreignExe, string output)
    {
        System.Diagnostics.Process? owned = null, foreign = null;
        try
        {
            System.Diagnostics.Process Launch(string exe) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "--fixture")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(exe)! })!;
            owned = Launch(ownedExe); foreign = Launch(foreignExe);
            Thread.Sleep(150);
            Check(!owned.HasExited && !foreign.HasExited, "both disposable same-name process fixtures are running");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ZapretProcessAccess.StopIfOwned(foreign.Id, MissingService());
            Check(!foreign.HasExited, "native stop leaves an unrelated winws.exe process untouched");
            ZapretProcessAccess.StopIfOwned(owned.Id, MissingService());
            Check(owned.WaitForExit(2000), "native stop terminates the verified disposable Zapret process");
            Check(!foreign.HasExited && watch.Elapsed < TimeSpan.FromSeconds(2), "verified process stop is bounded and does not affect the other application");
            File.WriteAllText(output, JsonSerializer.Serialize(new { milliseconds = watch.Elapsed.TotalMilliseconds, ownedStopped = owned.HasExited, foreignPreserved = !foreign.HasExited }));
            Console.WriteLine($"PROCESS SCOPE PASS: {_checks} checks; only disposable child processes touched; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally
        {
            // These two Process objects were created above; never enumerate/kill
            // real player/VPN/Zapret processes in this test's cleanup.
            foreach (var child in new[] { owned, foreign })
                if (child != null) { try { if (!child.HasExited) { child.Kill(); child.WaitForExit(2000); } } catch { } child.Dispose(); }
        }
    }

    static int ZapretIsolationTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(output, "status-before.json"), JsonSerializer.Serialize(new ZapretSystem().Read()));
            var driver = new ZapretServiceInfo(true, 3, @"\??\C:\vpn\sing-box\WinDivert64.sys", 4, "");
            var system = new FakeZapretSystem { KeepDriver = true, Status = new(new(true, 4, @"C:\zapret\bin\winws.exe", 2, "general"), driver, MissingService(), [@"C:\zapret"]) };
            var result = ZapretOperations.ExecuteAsync(false, null, null, system.Status.Identity, system).GetAwaiter().GetResult();
            Check(result.Success && !system.Status.Service.Installed && system.Status.ProcessRoots.Length == 0 && system.Status.Driver == driver,
                "Zapret stop succeeds while the VPN driver remains STOP_PENDING and untouched");
            Check(system.Calls.All(c => !c.Contains("WinDivert")), "routine stop never stops, deletes or waits for shared VPN drivers");
            Check(system.Status.IsOff && !system.Status.HasZapretResources, "off state excludes shared-driver installation and runtime state");
            var unchanged = system.Status;
            Check(unchanged.Identity == (unchanged with { Driver = driver with { State = 4, ImagePath = "changed-vpn-driver" }, Driver14 = new(true, 4, "vpn", 3, "") }).Identity,
                "VPN driver reloads cannot trigger a false configuration conflict");
            system = new FakeZapretSystem { KeepProcess = true, Status = unchanged with { ProcessRoots = [@"C:\zapret"] } };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Status.Identity, system).GetAwaiter().GetResult();
            Check(!result.Success && !system.Status.IsOff, "a remaining real Zapret process is never reported as off");
            system = new FakeZapretSystem { FailRemove = true, Status = unchanged with { Service = new(true, 4, @"C:\zapret\bin\winws.exe", 2, "general") } };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Status.Identity, system).GetAwaiter().GetResult();
            Check(!result.Success, "a remaining running service is a genuine failure");
            system = new FakeZapretSystem { FailRemove = true, Status = unchanged with { Service = new(true, 1, @"C:\zapret\bin\winws.exe", 2, "general") } };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Status.Identity, system).GetAwaiter().GetResult();
            Check(!result.Success, "failed service deletion cannot claim autostart was disabled");
            system = new FakeZapretSystem { MarkDeleted = true, Status = unchanged with { Service = new(true, 1, @"C:\zapret\bin\winws.exe", 2, "general") } };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Status.Identity, system).GetAwaiter().GetResult();
            Check(result.Success && system.Status.IsRemovalComplete, "stopped marked-for-deletion service does not cause a handle wait");
            string fixture = MakeZapretFixture(Path.Combine(output, "owned"));
            Check(ZapretProcessAccess.Root(Path.Combine(fixture, "bin", "winws.exe"), MissingService()) == fixture,
                "manual process in a verified Zapret installation is recognized");
            Check(ZapretProcessAccess.Root(@"C:\vpn\bin\winws.exe", MissingService()) == null,
                "a same-named executable outside a Zapret installation is unrelated");
            Check(ZapretProcessAccess.Root(@"C:\vpn\sing-box.exe", MissingService()) == null,
                "sing-box is never a Zapret process");
            Check(ZapretProcessAccess.Root(null, MissingService()) == null,
                "unreadable image path is not positive ownership evidence");
            var configured = new ZapretServiceInfo(true, 4, "\"C:\\renamed folder\\bin\\winws.exe\" --wf-tcp 443", 2, "general");
            Check(ZapretProcessAccess.Root(@"C:\renamed folder\bin\winws.exe", configured) == @"C:\renamed folder",
                "registered service executable remains identifiable if its folder metadata was removed");
            Check(ZapretProcessAccess.ImagePath(Environment.ProcessId, out int error) == Environment.ProcessPath && error == 0,
                "native limited-access executable query works without module/VM access");
            var live = new ZapretSystem().Read();
            // Exercise the real native no-op path only when there is nothing to
            // stop. A running installation is never toggled by this test.
            if (!live.Service.Installed && live.ProcessRoots.Length == 0 && live.UnidentifiedProcesses == 0
                && System.Diagnostics.Process.GetProcessesByName("winws").Length == 0)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var actual = ZapretOperations.ExecuteAsync(false, null, null, live.Identity, new ZapretSystem()).GetAwaiter().GetResult();
                var final = new ZapretSystem().Read();
                Check(actual.Success && final.IsOff && final.Driver == live.Driver && final.Driver14 == live.Driver14,
                    "native off check succeeds while preserving the actual VPN driver");
                Check(watch.Elapsed < TimeSpan.FromSeconds(2), "native already-off check has no twenty-second driver wait");
                File.WriteAllText(Path.Combine(output, "native-off-result.json"), JsonSerializer.Serialize(new { actual, milliseconds = watch.Elapsed.TotalMilliseconds, before = live, after = final }));
            }
            File.WriteAllText(Path.Combine(output, "status-after.json"), JsonSerializer.Serialize(new ZapretSystem().Read()));
            Console.WriteLine($"ISOLATION PASS: {_checks} checks; no live mutations; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int ZapretIdentityTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var live = new ZapretSystem().Read();
            File.WriteAllText(Path.Combine(output, "live-read-only.json"), JsonSerializer.Serialize(live));
            var normal = new ZapretStatus(new(true, 4, @"C:\zapret\bin\winws.exe --wf-tcp 443", 2, "general (ALT11)"),
                new(true, 4, "shared-driver", 4, ""), MissingService(), [""]);
            var elevated = normal with { ProcessRoots = [@"C:\zapret"] };
            Check(normal.Identity == elevated.Identity, "process-path visibility across UAC does not change the service configuration identity");
            Check(normal.Identity == (normal with { ProcessRoots = [] }).Identity,
                "process exit/restart does not masquerade as an external configuration change");
            Check(normal.Identity != (elevated with { Service = elevated.Service with { Strategy = "general (ALT2)" } }).Identity,
                "a genuinely changed strategy is still rejected");
            Check(normal.Identity != (elevated with { Service = elevated.Service with { ImagePath = @"D:\other\bin\winws.exe" } }).Identity,
                "a genuinely changed service executable is still rejected");
            Check(normal.Identity == (elevated with { Driver = elevated.Driver with { ImagePath = "replacement-driver" } }).Identity,
                "unrelated VPN driver changes do not block a Zapret operation");
            Check(normal.Identity != (elevated with { Service = elevated.Service with { Start = 3 } }).Identity,
                "an external autostart configuration change is still rejected");
            var system = new FakeZapretSystem { Status = elevated };
            var result = ZapretOperations.ExecuteAsync(false, null, null, normal.Identity, system).GetAwaiter().GetResult();
            Check(result.Success && !system.Status.HasZapretResources && system.Calls.Count == 2,
                "a normal-to-elevated path visibility change no longer prevents scoped removal");
            system = new FakeZapretSystem { Status = elevated with { Service = elevated.Service with { Strategy = "changed" } } };
            result = ZapretOperations.ExecuteAsync(false, null, null, normal.Identity, system).GetAwaiter().GetResult();
            Check(!result.Success && system.Calls.Count == 0, "real configuration conflicts abort without touching services");
            Console.WriteLine($"IDENTITY PASS: {_checks} checks; fake SCM mutations only; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int ZapretDiscoveryTest(string output)
    {
        try
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var found = new List<ZapretInstallation>();
            ZapretScanProgress? final = null;
            string[] drives = ZapretDiscovery.LocalRoots().ToArray();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            ZapretDiscovery.ScanAsync(drives, [], progress =>
            {
                final = progress;
                if (progress.Found is { } install) { found.Add(install); Console.WriteLine("FOUND: " + install.Root); }
            }, timeout.Token).GetAwaiter().GetResult();
            File.WriteAllText(output, JsonSerializer.Serialize(new { drives, elapsedSeconds = watch.Elapsed.TotalSeconds, final, found }));
            Check(final?.Complete == true && found.Any(i => i.Root.Equals(@"C:\zapret", StringComparison.OrdinalIgnoreCase)),
                "unhinted full-local-drive discovery completes and finds the real installation");
            Check(found.Count(i => i.Root.StartsWith(@"C:\zapret", StringComparison.OrdinalIgnoreCase)) == 6,
                "full drive traversal finds all six real nested versions");
            Check(found.All(i => !i.Root.Contains("di-zp-", StringComparison.OrdinalIgnoreCase) && !i.Root.Contains("zapret-and-drag-", StringComparison.OrdinalIgnoreCase)),
                "generated reference corpora do not pollute the user's installation picker");
            Console.WriteLine($"DISCOVERY PASS: {found.Count} copies, {final!.Directories} directories, {watch.Elapsed.TotalSeconds:F1}s; read-only; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static int ZapretTests(string output)
    {
        try
        {
            Directory.CreateDirectory(output);
            var before = new ZapretSystem().Read();
            File.WriteAllText(Path.Combine(output, "status-before.json"), JsonSerializer.Serialize(before));
            string fixture = MakeZapretFixture(Path.Combine(output, "fixtures", "copy with spaces"));
            string second = MakeZapretFixture(Path.Combine(output, "fixtures", "nested", "другая копия"));
            var install = ZapretInstallation.Inspect(fixture)!;
            Check(install != null && install.Strategies.Length == 2, "zapret discovers extracted copies without executing their batches");
            var launch = ZapretStrategyParser.Load(fixture, "general.bat");
            Check(launch.Arguments.Contains(Path.Combine(fixture, "lists", "list-general.txt")), "strategy file variables become absolute paths");
            Check(launch.CommandLine.StartsWith('"'), "service command quotes a binary path containing spaces");
            Check(ZapretLaunch.WindowsArgument("a b\\") == "\"a b\\\\\"", "Windows quoting preserves trailing backslashes");
            Check(ZapretStrategyParser.Load(second, "general.bat").Root == second, "strategy parser supports a Unicode installation directory");
            try { ZapretStrategyParser.Load(fixture, "..\\escape.bat"); throw new Exception("FAIL: path traversal accepted"); }
            catch (InvalidDataException) { Check(true, "strategy selection cannot escape its installation"); }
            var prefs = ZapretPreferences.Empty.Select(fixture, "general.bat").Select(second, "general (ALT2).bat");
            var restored = ZapretPreferences.Parse(prefs.Serialize());
            Check(restored.SelectedRoot == second && restored.Strategies[fixture] == "general.bat", "default strategy is remembered independently for each selected copy");
            Check(ZapretPreferences.Parse("broken").SelectedRoot == null, "corrupt zapret preferences fall back without running anything");
            Check(ZapretPreferences.Parse("{\"SelectedRoot\":null,\"Strategies\":{\"C:\\\\zapret\":null}}").Strategies.Count == 0,
                "null strategy values in saved JSON cannot crash startup");
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ZapretDiscovery.ScanAsync([Path.Combine(output, "fixtures")], [fixture, fixture.ToUpperInvariant()], p => { if (p.Found != null) found.Add(p.Found.Root); }, CancellationToken.None).GetAwaiter().GetResult();
            Check(found.SetEquals([fixture, second]), "recursive discovery finds multiple nested installations and deduplicates path casing");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try { ZapretDiscovery.ScanAsync([fixture], [], _ => { }, cancellation.Token).GetAwaiter().GetResult(); throw new Exception("FAIL: cancellation ignored"); }
                catch (OperationCanceledException) { Check(true, "full discovery is cancellable"); }
            }
            string game = Path.Combine(fixture, "utils", "game_filter.enabled");
            string service = File.ReadAllText(Path.Combine(fixture, "service.bat"));
            Check(ZapretStrategyParser.GameFilter(fixture, service) == ("12", "12", "12"), "disabled Game Filter preserves the batch's sentinel port");
            File.WriteAllText(game, "mode=tcp\ntcp=1500-1600,443\nudp=5555\n");
            Check(ZapretStrategyParser.GameFilter(fixture, service) == ("1500-1600,443", "12", "1500-1600,443"), "current Game Filter TCP mode and custom ranges match service.bat");
            File.WriteAllText(game, "mode=all\ntcp=999999\nudp=6000-5000\n");
            Check(ZapretStrategyParser.GameFilter(fixture, service) == ("1024-65535", "1024-65535", "1024-65535"), "invalid port ranges fall back like the service validator");
            File.Delete(game);
            var system = new FakeZapretSystem();
            var result = ZapretOperations.ExecuteAsync(true, fixture, "general.bat", system.Read().Identity, system).GetAwaiter().GetResult();
            Check(result.Success && system.Status.Service.Running && system.Status.Service.Start == 2, "enable creates and starts an automatic zapret service");
            Check(system.Calls.SequenceEqual(["lists", "timestamps", "remove:zapret", "create:auto", "description", "strategy:general.bat", "start"]), "Install Service operation ordering matches the reviewed service contract");
            Check(system.Status.Service.Strategy == "general" && system.Status.Service.Root == fixture, "installed metadata identifies the exact strategy and copy");
            system.Calls.Clear(); system.FailStart = true;
            result = ZapretOperations.ExecuteAsync(true, second, "general.bat", system.Read().Identity, system).GetAwaiter().GetResult();
            Check(!result.Success && !system.Status.Service.Running, "a failed start is reported as failure, not an enabled UI state");
            system = new FakeZapretSystem();
            result = ZapretOperations.ExecuteAsync(true, fixture, "general.bat", "stale", system).GetAwaiter().GetResult();
            Check(!result.Success && system.Calls.Count == 0, "external configuration changes abort before any mutation");
            system = new FakeZapretSystem();
            string payload = Path.Combine(fixture, "bin", "fake.bin");
            File.Move(payload, payload + ".saved", true);
            result = ZapretOperations.ExecuteAsync(true, fixture, "general.bat", system.Read().Identity, system).GetAwaiter().GetResult();
            Check(!result.Success && system.Calls.SequenceEqual(["lists"]), "missing strategy data aborts before timestamps or existing service changes");
            File.Move(payload + ".saved", payload, true);
            string strategyPath = Path.Combine(fixture, "general.bat"), originalStrategy = File.ReadAllText(strategyPath);
            File.WriteAllText(strategyPath, originalStrategy + " --dpi-desync-fake-tls=^!");
            Check(ZapretStrategyParser.Load(fixture, "general.bat").Arguments.Last() == "!", "escaped fake TLS auto sentinel is a literal argument rather than a missing file");
            File.WriteAllText(strategyPath, originalStrategy);
            system = new FakeZapretSystem { Status = new(new(true, 4, launch.CommandLine, 2, "general"), new(true, 4, "driver", 3, ""), new(true, 1, "old-driver", 3, ""), [fixture, second]) };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Read().Identity, system).GetAwaiter().GetResult();
            Check(result.Success && !system.Status.HasZapretResources && system.Status.Driver.Installed && system.Status.Driver14.Installed, "disable removes Zapret while preserving both shared drivers");
            Check(system.Calls.SequenceEqual(["remove:zapret", "stop:zapret-processes"]), "disable is scoped to the Zapret service and verified processes");
            system = new FakeZapretSystem { KeepDriver = true, Status = new(MissingService(), new(true, 4, "driver", 3, ""), MissingService(), []) };
            result = ZapretOperations.ExecuteAsync(false, null, null, system.Read().Identity, system).GetAwaiter().GetResult();
            Check(result.Success && system.Status.Driver.Running && system.Status.IsOff, "a residual VPN driver is irrelevant to the verified off state");
            var real = new List<object>(); int total = 0;
            var roots = new HashSet<string>(new[] { @"C:\zapret" }.Concat(Directory.EnumerateDirectories(@"C:\zapret").Where(d => File.Exists(Path.Combine(d, "service.bat")))), StringComparer.OrdinalIgnoreCase);
            string discovery = Path.Combine(Directory.GetParent(output)!.FullName, "discovery.json");
            if (File.Exists(discovery))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(discovery));
                foreach (var item in document.RootElement.GetProperty("found").EnumerateArray()) roots.Add(item.GetProperty("Root").GetString()!);
            }
            foreach (string root in roots)
            {
                var copy = ZapretInstallation.Inspect(root) ?? throw new Exception("FAIL: installed copy not recognised: " + root);
                foreach (var strategy in copy.Strategies)
                {
                    var parsed = ZapretStrategyParser.Load(root, strategy.FileName, false);
                    Check(parsed.Arguments.All(a => !a.Contains("%BIN%")) && parsed.Arguments.Any(a => a.StartsWith("--wf-")), "read-only real strategy: " + copy.Name + "/" + strategy.FileName);
                    real.Add(new { root, strategy = strategy.FileName, parsed.Arguments }); total++;
                }
            }
            File.WriteAllText(Path.Combine(output, "local-strategies.json"), JsonSerializer.Serialize(real));
            Check(total >= 100, "all discovered copies and their strategies were parsed without execution");
            File.WriteAllText(Path.Combine(output, "status-after.json"), JsonSerializer.Serialize(new ZapretSystem().Read()));
            Console.WriteLine($"ZAPRET PASS: {_checks} checks; {total} local strategies from {roots.Count} copies; fixture-only mutations; no live service changes; exit=0");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    static ZapretServiceInfo MissingService() => new(false, 0, "", 0, "");
    static string MakeZapretFixture(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "bin")); Directory.CreateDirectory(Path.Combine(root, "lists")); Directory.CreateDirectory(Path.Combine(root, "utils"));
        File.Copy(@"C:\zapret\bin\winws.exe", Path.Combine(root, "bin", "winws.exe"), true);
        File.WriteAllBytes(Path.Combine(root, "bin", "WinDivert.dll"), [0]);
        File.WriteAllBytes(Path.Combine(root, "bin", "WinDivert64.sys"), [0]);
        File.WriteAllText(Path.Combine(root, "bin", "fake.bin"), "fixture"); File.WriteAllText(Path.Combine(root, "lists", "list-general.txt"), "example.test");
        File.WriteAllText(Path.Combine(root, "service.bat"), """
            @echo off
            set "LOCAL_VERSION=fixture"
            :service_install
            sc create zapret start= auto
            reg add zapret-discord-youtube
            :service_remove
            sc delete zapret
            sc delete WinDivert14
            :game_switch_status
            set "gameFlagFile=%~dp0utils\game_filter.enabled"
            set "GameFilter=12"
            set "GameFilterMode=disabled"
            set "GameFilterTCPRange=1024-65535"
            :end
            rem winws.exe
            """);
        const string command = """
            @echo off
            start "zapret: %~n0" /min "%BIN%winws.exe" --wf-tcp=443 --filter-tcp=443 ^
            --hostlist="%LISTS%list-general.txt" --dpi-desync=fake --dpi-desync-fake-tls="%BIN%fake.bin"
            """;
        File.WriteAllText(Path.Combine(root, "general.bat"), command); File.WriteAllText(Path.Combine(root, "general (ALT2).bat"), command);
        return root;
    }

    sealed class FakeZapretSystem : IZapretSystem
    {
        public ZapretStatus Status = new(MissingService(), MissingService(), MissingService(), []);
        public readonly List<string> Calls = [];
        public bool FailStart, KeepDriver, KeepProcess, FailRemove, MarkDeleted;
        public Func<ZapretStatus>? OnRead;
        public ZapretStatus Read() => OnRead?.Invoke() ?? Status;
        public Task RemoveServiceAsync(string name)
        {
            Calls.Add("remove:" + name);
            if (name != "zapret") throw new Exception("Foreign driver mutation attempted");
            if (FailRemove) throw new IOException("fixture removal failed");
            Status = Status with { Service = MarkDeleted ? new(true, 0, "", 0, "", DeletePending: true) : MissingService() };
            return Task.CompletedTask;
        }
        public Task EnableTimestampsAsync() { Calls.Add("timestamps"); return Task.CompletedTask; }
        public void EnsureUserLists(string root) => Calls.Add("lists");
        public Task CreateAsync(ZapretLaunch launch) { Calls.Add("create:auto"); Status = Status with { Service = new(true, 1, launch.CommandLine, 2, "") }; return Task.CompletedTask; }
        public Task DescribeAsync() { Calls.Add("description"); return Task.CompletedTask; }
        public void SetStrategy(string strategy) { Calls.Add("strategy:" + strategy); Status = Status with { Service = Status.Service with { Strategy = Path.GetFileNameWithoutExtension(strategy) } }; }
        public Task StartAsync() { Calls.Add("start"); if (FailStart) throw new IOException("fixture start failed"); Status = Status with { Service = Status.Service with { State = 4 } }; return Task.CompletedTask; }
        public void KillZapretProcesses(ZapretServiceInfo service) { Calls.Add("stop:zapret-processes"); if (!KeepProcess) Status = Status with { ProcessRoots = [] }; }
    }
}
