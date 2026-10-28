using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using Microsoft.Win32;

namespace DynamicIsland;

sealed record ZapretServiceInfo(bool Installed, uint State, string ImagePath, int Start, string Strategy, string? Error = null, uint ProcessId = 0, bool DeletePending = false)
{
    public bool Running => State == 4;
    public string? Root
    {
        get
        {
            try
            {
                string exe = ImagePath.StartsWith('"') ? ImagePath.Split('"')[1] : ImagePath.Split(' ')[0];
                return Path.GetFileName(exe).Equals("winws.exe", StringComparison.OrdinalIgnoreCase) ? Directory.GetParent(exe)?.Parent?.FullName : null;
            }
            catch { return null; }
        }
    }
}
sealed record ZapretStatus(ZapretServiceInfo Service, ZapretServiceInfo Driver, ZapretServiceInfo Driver14, string[] ProcessRoots, int UnidentifiedProcesses = 0)
{
    public bool HasZapretResources => Service.Installed && !Service.DeletePending || ProcessRoots.Length > 0;
    public bool IsOff => Service.Error == null && (!Service.Installed || Service.DeletePending || Service.State == 1)
        && ProcessRoots.Length == 0 && UnidentifiedProcesses == 0;
    public bool IsRemovalComplete => IsOff && (!Service.Installed || Service.DeletePending);
    // Persisted configuration only: MainModule is unreadable for an elevated winws
    // in the UI but readable in the UAC helper. Runtime paths are not config changes.
    public string Identity => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    { Service.Installed, Service.ImagePath, Service.Start, Service.Strategy, Service.DeletePending }))));
}

interface IZapretSystem
{
    ZapretStatus Read();
    Task RemoveServiceAsync(string name);
    Task EnableTimestampsAsync();
    void EnsureUserLists(string root);
    Task CreateAsync(ZapretLaunch launch);
    Task DescribeAsync();
    void SetStrategy(string strategy);
    Task StartAsync();
    void KillZapretProcesses(ZapretServiceInfo service);
}

sealed class ZapretSystem : IZapretSystem
{
    const string ServiceKey = @"SYSTEM\CurrentControlSet\Services\";
    [StructLayout(LayoutKind.Sequential)] struct ServiceState { public uint Type, State, Controls, Exit, SpecificExit, Checkpoint, Wait, Pid, Flags; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceState state, int size, out int needed);
    [DllImport("advapi32.dll")] static extern bool CloseServiceHandle(IntPtr handle);
    static void KnownService(string name) { if (name is not ("zapret" or "WinDivert" or "WinDivert14")) throw new ArgumentException("Unknown service"); }

    public static ZapretServiceInfo Query(string name)
    {
        KnownService(name);
        IntPtr manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) return new(false, 0, "", 0, "", new Win32Exception().Message);
        try
        {
            IntPtr service = OpenService(manager, name, 4);
            if (service == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1072) return new(true, 0, "", 0, "", DeletePending: true);
                return new(false, 0, "", 0, "", error == 1060 ? null : new Win32Exception(error).Message);
            }
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var state, Marshal.SizeOf<ServiceState>(), out _)) throw new Win32Exception();
                using var key = Registry.LocalMachine.OpenSubKey(ServiceKey + name);
                return new(true, state.State, key?.GetValue("ImagePath") as string ?? "", key?.GetValue("Start") as int? ?? 0, key?.GetValue("zapret-discord-youtube") as string ?? "", ProcessId: state.Pid);
            }
            catch (Exception ex) { return new(true, 0, "", 0, "", ex.Message); }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public ZapretStatus Read()
    {
        var roots = new List<string>(); int unidentified = 0;
        var service = Query("zapret");
        foreach (var process in Process.GetProcessesByName("winws"))
        {
            using (process)
            {
                string? path = ZapretProcessAccess.ImagePath(process.Id, out int error);
                string? root = ZapretProcessAccess.Root(path, service);
                // SCM's PID is trustworthy only in the documented stable states.
                if (root == null && path == null && service.State is 4 or 5 or 6 or 7 && service.ProcessId == process.Id) root = service.Root;
                if (root != null) roots.Add(root);
                else if (path == null && error == 5) unidentified++;
            }
        }
        // Driver observations are diagnostic only, never ownership or on/off evidence.
        return new(service, Query("WinDivert"), Query("WinDivert14"), roots.ToArray(), unidentified);
    }

    static async Task<int> Tool(string exe, string[] args, bool optional = false, int timeoutMs = 30000)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, exe))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + exe);
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)); }
        catch (TimeoutException) { try { process.Kill(); } catch { } throw new IOException("Операция " + exe + " превысила время ожидания."); }
        string output = await stdout + await stderr;
        if (process.ExitCode != 0 && !optional) throw new IOException(exe + ": " + output.Trim());
        return process.ExitCode;
    }

    public async Task RemoveServiceAsync(string name)
    {
        // Routine control must never mutate WinDivert/WinDivert14, even if a
        // driver happens to live under a zapret folder: it may be shared by a VPN.
        if (name != "zapret") throw new ArgumentException("Only the zapret service can be modified.");
        var info = Query(name); if (info.Error != null) throw new IOException(info.Error);
        if (!info.Installed || info.DeletePending) return;
        if (info.State != 1)
        {
            try { await Tool("sc.exe", ["stop", name], true, 1500); } catch (IOException) { /* Confirm actual state below. */ }
            if (!await Wait(name, s => !s.Installed || s.DeletePending || s.State == 1, 1000))
            {
                KillZapretProcesses(info);
                if (!await Wait(name, s => !s.Installed || s.DeletePending || s.State == 1, 1500))
                    throw new IOException("Zapret ещё завершает работу. Повтори выключение.");
            }
        }
        info = Query(name);
        if (!info.Installed || info.DeletePending) return;
        await Tool("sc.exe", ["delete", name], timeoutMs: 1500);
        // A stopped, marked-for-deletion service is already off. Open read-only
        // handles elsewhere must not introduce another twenty-second wait.
    }
    static async Task<bool> Wait(string name, Func<ZapretServiceInfo, bool> ready, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var state = Query(name);
            if (state.Error != null) throw new IOException(state.Error);
            if (ready(state)) return true;
            await Task.Delay(75);
        }
        return false;
    }
    public Task EnableTimestampsAsync() => Tool("netsh.exe", ["interface", "tcp", "set", "global", "timestamps=enabled"]);
    public void EnsureUserLists(string root)
    {
        if (!File.Exists(Path.Combine(root, "service.bat")) || !ZapretInstallation.ReadText(Path.Combine(root, "service.bat")).Contains(":load_user_lists")) return;
        string lists = Path.Combine(root, "lists"); Directory.CreateDirectory(lists);
        foreach (var (file, text) in new[] { ("ipset-exclude-user.txt", "203.0.113.113/32\r\n"), ("list-general-user.txt", "# Never leave this file empty\r\ndomain.example.abc\r\n"), ("list-exclude-user.txt", "domain.example.abc\r\n") })
        {
            string path = Path.Combine(lists, file);
            if (File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.CreateNew);
            using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)); writer.Write(text);
        }
    }
    public Task CreateAsync(ZapretLaunch launch) => Tool("sc.exe", ["create", "zapret", "binPath=", launch.CommandLine, "DisplayName=", "zapret", "start=", "auto"]);
    public Task DescribeAsync() => Tool("sc.exe", ["description", "zapret", "Zapret DPI bypass software"]);
    public void SetStrategy(string strategy)
    {
        using var key = Registry.LocalMachine.OpenSubKey(ServiceKey + "zapret", true) ?? throw new IOException("Служба не создана.");
        key.SetValue("zapret-discord-youtube", Path.GetFileNameWithoutExtension(strategy), RegistryValueKind.String);
    }
    public async Task StartAsync()
    {
        await Tool("sc.exe", ["start", "zapret"]);
        if (!await Wait("zapret", s => s.Running, 10000)) throw new IOException("Zapret не подтвердил запуск.");
    }
    public void KillZapretProcesses(ZapretServiceInfo service)
    {
        foreach (var process in Process.GetProcessesByName("winws"))
            using (process) ZapretProcessAccess.StopIfOwned(process.Id, service);
    }
}

static class ZapretProcessAccess
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder name, ref int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateProcess(IntPtr handle, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    static string? ImagePath(IntPtr handle, out int error)
    {
        var buffer = new StringBuilder(32768); int length = buffer.Capacity;
        if (QueryFullProcessImageName(handle, 0, buffer, ref length)) { error = 0; return buffer.ToString(); }
        error = Marshal.GetLastWin32Error(); return null;
    }
    public static string? ImagePath(int id, out int error)
    {
        var handle = OpenProcess(0x1000, false, id); // PROCESS_QUERY_LIMITED_INFORMATION, not module/VM access.
        if (handle == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return null; }
        try { return ImagePath(handle, out error); } finally { CloseHandle(handle); }
    }
    public static string? Root(string? executable, ZapretServiceInfo service)
    {
        try
        {
            if (executable == null || !Path.GetFileName(executable).Equals("winws.exe", StringComparison.OrdinalIgnoreCase)) return null;
            var bin = Directory.GetParent(executable);
            if (bin?.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) != true || bin.Parent == null) return null;
            string root = bin.Parent.FullName;
            if (service.Root is { } configured && root.Equals(configured, StringComparison.OrdinalIgnoreCase)) return root;
            return ZapretInstallation.HasRuntimeContract(root) ? root : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
    public static void StopIfOwned(int id, ZapretServiceInfo service)
    {
        // Query and terminate the SAME object handle, not a PID reopened after a
        // check (which could have been reused for another process).
        var handle = OpenProcess(0x1000 | 0x100000 | 1, false, id);
        if (handle == IntPtr.Zero)
        {
            if (service.ProcessId == id && service.Running && Marshal.GetLastWin32Error() == 5)
                throw new IOException("Нет доступа к процессу службы Zapret.");
            return;
        }
        try
        {
            if (Root(ImagePath(handle, out _), service) == null) return;
            if (!TerminateProcess(handle, 0) && WaitForSingleObject(handle, 0) != 0) throw new Win32Exception();
            if (WaitForSingleObject(handle, 1500) != 0) throw new IOException("Процесс Zapret ещё завершает работу.");
        }
        finally { CloseHandle(handle); }
    }
}

sealed record ZapretOperationResult(bool Success, string Message, string[] Trace);
static class ZapretOperations
{
    public static async Task<ZapretOperationResult> ExecuteAsync(bool install, string? root, string? strategy, string expectedIdentity, IZapretSystem system)
    {
        var trace = new List<string>();
        try
        {
            var before = system.Read();
            if (before.Service.Error != null) throw new IOException(before.Service.Error);
            if (before.Identity != expectedIdentity) throw new IOException("Служба изменилась в другой программе. Обнови статус и повтори.");
            if (install)
            {
                // Missing user lists are created by service.bat before its Install menu too.
                system.EnsureUserLists(root!); trace.Add("load_user_lists");
                var launch = ZapretStrategyParser.Load(root!, strategy!);
                await system.EnableTimestampsAsync(); trace.Add("tcp timestamps=enabled");
                await system.RemoveServiceAsync("zapret"); trace.Add("stop/delete zapret");
                await system.CreateAsync(launch); trace.Add("create zapret start=auto: " + launch.CommandLine);
                await system.DescribeAsync(); trace.Add("description zapret");
                system.SetStrategy(strategy!); trace.Add("registry strategy=" + Path.GetFileNameWithoutExtension(strategy));
                await system.StartAsync(); trace.Add("start zapret");
                var after = system.Read();
                if (!after.Service.Running || after.Service.Start != 2 || !string.Equals(after.Service.Root, root, StringComparison.OrdinalIgnoreCase)
                    || after.Service.Strategy != Path.GetFileNameWithoutExtension(strategy)) throw new IOException("Служба не подтвердила запуск выбранной стратегии.");
            }
            else
            {
                // Scoped off: only zapret and verified zapret winws processes.
                // Shared drivers belong to their consumers, not to this toggle.
                var errors = new List<string>();
                try { await system.RemoveServiceAsync("zapret"); trace.Add("stop/delete zapret"); } catch (Exception ex) { errors.Add(ex.Message); }
                try { system.KillZapretProcesses(before.Service); trace.Add("stop verified zapret processes"); } catch (Exception ex) { errors.Add(ex.Message); }
                var after = system.Read();
                if (!after.IsRemovalComplete)
                {
                    foreach (string error in errors) trace.Add("error: " + error);
                    string message = after.Service.Error != null ? "Не удалось проверить службу Zapret."
                        : after.UnidentifiedProcesses > 0 ? "Не удалось проверить процессы Zapret."
                        : after.IsOff ? "Zapret остановлен, но его службу не удалось удалить."
                        : "Zapret ещё завершает работу. Повтори выключение.";
                    throw new IOException(message);
                }
                // An intermediate stop error is not a user-facing failure if the
                // verified final state is off (e.g. process exited during stop).
                foreach (string error in errors) trace.Add("recovered: " + error);
            }
            return new(true, install ? "Служба установлена и запущена. Автозапуск включён." : "Zapret выключен.", trace.ToArray());
        }
        catch (Exception ex) { return new(false, ex.Message, trace.ToArray()); }
    }
}
