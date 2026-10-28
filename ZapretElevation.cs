using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace DynamicIsland;

sealed record ZapretOperationRequest(int Version, string Nonce, DateTimeOffset Created, bool Install,
    string? Root, string? Strategy, string Identity, Dictionary<string, string> Hashes);

static class ZapretElevation
{
    static string OperationsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynamicIsland", "zapret-operations");
    public static string FileHash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }

    public static async Task<ZapretOperationResult> RunAsync(bool install, string? root, string? strategy, ZapretStatus status)
    {
        string id = Guid.NewGuid().ToString("N"), folder = Path.Combine(OperationsDirectory, id);
        Directory.CreateDirectory(folder);
        try
        {
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (install)
            {
                root = ZapretInstallation.Normalize(root!);
                _ = ZapretStrategyParser.Load(root, strategy!, false);
                foreach (string file in new[] { "service.bat", strategy!, @"bin\winws.exe" }) hashes[file] = FileHash(Path.Combine(root, file));
            }
            var request = new ZapretOperationRequest(1, id, DateTimeOffset.UtcNow, install, root, strategy, status.Identity, hashes);
            await File.WriteAllTextAsync(Path.Combine(folder, "request.json"), JsonSerializer.Serialize(request));
            string exe = Environment.ProcessPath ?? throw new IOException("Путь островка не найден.");
            var start = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = "--zapret-operation " + ZapretLaunch.WindowsArgument(folder) };
            using var process = Process.Start(start) ?? throw new IOException("Операция не запущена.");
            await process.WaitForExitAsync();
            string resultFile = Path.Combine(folder, "result.json");
            var result = File.Exists(resultFile) ? JsonSerializer.Deserialize<ZapretOperationResult>(await File.ReadAllTextAsync(resultFile)) : null;
            result ??= new(false, "Операция завершилась без результата. Проверь текущий статус.", []);
            string last = Path.Combine(Path.GetDirectoryName(OperationsDirectory)!, "zapret-last-operation.json");
            await File.WriteAllTextAsync(last, JsonSerializer.Serialize(result));
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return new(false, "Запрос прав администратора отменён. Ничего не изменено.", []); }
        catch (Exception ex) { return new(false, ex.Message, []); }
        finally
        {
            // Only the two known IPC files in this run's GUID folder; never recursive.
            try { File.Delete(Path.Combine(folder, "request.json")); File.Delete(Path.Combine(folder, "result.json")); Directory.Delete(folder, false); } catch { }
        }
    }

    public static async Task<int> RunHelperAsync(string folder)
    {
        var result = new ZapretOperationResult(false, "Неверный запрос операции.", []);
        bool validFolder = false;
        try
        {
            folder = Path.GetFullPath(folder);
            var directory = new DirectoryInfo(folder);
            if (!Guid.TryParseExact(directory.Name, "N", out _) || directory.Parent?.Name != "zapret-operations" || directory.Parent.Parent?.Name != "DynamicIsland"
                || (directory.Attributes & FileAttributes.ReparsePoint) != 0) return 2;
            validFolder = true;
            if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException("Нужны права администратора.");
            string file = Path.Combine(folder, "request.json");
            if (new FileInfo(file).Length > 65536 || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Неверный запрос.");
            var request = JsonSerializer.Deserialize<ZapretOperationRequest>(await File.ReadAllTextAsync(file)) ?? throw new IOException("Пустой запрос.");
            if (request.Version != 1 || request.Nonce != directory.Name || DateTimeOffset.UtcNow - request.Created > TimeSpan.FromMinutes(10)
                || request.Created > DateTimeOffset.UtcNow.AddMinutes(1)) throw new IOException("Запрос устарел.");
            result = await Task.Run(() =>
            {
                using var mutex = new Mutex(false, @"Global\DynamicIsland.Zapret.Operation");
                bool acquired;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return new ZapretOperationResult(false, "Другая операция zapret ещё выполняется.", []);
                var locks = new List<FileStream>();
                try
                {
                    if (request.Install)
                    {
                        if (request.Root == null || request.Strategy == null || Path.GetFileName(request.Strategy) != request.Strategy) throw new IOException("Неверная стратегия.");
                        string root = ZapretInstallation.Normalize(request.Root);
                        string[] expected = ["service.bat", request.Strategy, @"bin\winws.exe"];
                        if (request.Hashes == null || request.Hashes.Count != 3) throw new IOException("Нет контрольных сумм.");
                        foreach (string relative in expected)
                        {
                            var stream = new FileStream(Path.Combine(root, relative), FileMode.Open, FileAccess.Read, FileShare.Read);
                            locks.Add(stream);
                            if (!request.Hashes.TryGetValue(relative, out string? hash) || Convert.ToHexString(SHA256.HashData(stream)) != hash)
                                throw new IOException("Файлы zapret изменились после выбора. Выбери стратегию заново.");
                        }
                    }
                    return ZapretOperations.ExecuteAsync(request.Install, request.Root, request.Strategy, request.Identity, new ZapretSystem()).GetAwaiter().GetResult();
                }
                finally { foreach (var stream in locks) stream.Dispose(); mutex.ReleaseMutex(); }
            });
        }
        catch (Exception ex) { result = new(false, ex.Message, []); }
        if (validFolder) { try { await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result)); } catch { return 3; } }
        return result.Success ? 0 : 1;
    }
}
