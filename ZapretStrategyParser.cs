using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace DynamicIsland;

sealed record ZapretLaunch(string Root, string StrategyFile, string Executable, string[] Arguments)
{
    public string CommandLine => WindowsArgument(Executable) + " " + string.Join(" ", Arguments.Select(WindowsArgument));
    public static string WindowsArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var s = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            s.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0; s.Append(c);
        }
        return s.Append('\\', slashes * 2).Append('"').ToString();
    }
}

static class ZapretStrategyParser
{
    public static ZapretLaunch Load(string root, string strategy, bool validateFiles = true)
    {
        var installation = ZapretInstallation.Inspect(root) ?? throw new InvalidDataException("Папка zapret отсутствует или повреждена.");
        if (Path.GetFileName(strategy) != strategy || !installation.Strategies.Any(s => s.FileName.Equals(strategy, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Стратегия отсутствует в выбранной папке.");
        root = installation.Root;
        if (validateFiles && (!File.Exists(Path.Combine(root, "bin", "WinDivert.dll")) || !Directory.EnumerateFiles(Path.Combine(root, "bin"), "*.sys").Any()))
            throw new FileNotFoundException("В папке bin отсутствуют WinDivert.dll или драйвер .sys. Служба не изменена.");
        string service = ZapretInstallation.ReadText(Path.Combine(root, "service.bat"));
        // Support the reviewed Flowseal service contract, not arbitrary service managers.
        foreach (string marker in new[] { "sc create", "sc delete", "start= auto", "zapret-discord-youtube", "WinDivert14" })
            if (!service.Contains(marker, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Неизвестный формат service.bat. Установка не выполнена.");
        string source = ZapretInstallation.ReadText(Path.Combine(root, strategy));
        string[] lines = source.Replace("\r", "").Split('\n');
        int start = Array.FindIndex(lines, l => !l.TrimStart().StartsWith("::") && !l.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase)
            && Regex.IsMatch(l, @"winws\.exe""", RegexOptions.IgnoreCase));
        if (start < 0) throw new InvalidDataException("Команда winws.exe не найдена.");
        var command = new StringBuilder();
        for (int i = start; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            bool continuation = line.EndsWith('^');
            command.Append(' ').Append(continuation ? line[..^1] : line);
            if (!continuation) break;
        }
        string raw = Regex.Split(command.ToString(), @"winws\.exe""", RegexOptions.IgnoreCase)[1].Trim();
        var (tcp, udp, legacy) = GameFilter(root, service);
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BIN"] = Path.Combine(root, "bin") + "\\", ["LISTS"] = Path.Combine(root, "lists") + "\\",
            ["BIN_PATH"] = Path.Combine(root, "bin") + "\\", ["LISTS_PATH"] = Path.Combine(root, "lists") + "\\",
            ["GameFilter"] = legacy, ["GameFilterTCP"] = tcp, ["GameFilterUDP"] = udp,
        };
        // The reviewed batches escape the fake-TLS auto sentinel as ^!.
        // It is a literal winws argument, not a path or delayed expansion.
        raw = raw.Replace("^!", "!");
        raw = raw.Replace("%~dp0", root + "\\", StringComparison.OrdinalIgnoreCase);
        raw = Regex.Replace(raw, @"%([\w]+)%", m => vars.TryGetValue(m.Groups[1].Value, out string? value) ? value : throw new InvalidDataException("Неизвестная переменная стратегии: " + m.Value));
        if (raw.Contains('%') || raw.Contains('\r') || raw.Contains('\n')) throw new InvalidDataException("Неподдерживаемая подстановка BAT.");
        string[] tokens = Tokens(raw);
        var args = new List<string>();
        foreach (string token in tokens)
        {
            if (token.StartsWith("--", StringComparison.Ordinal)) args.Add(token);
            else
            {
                // service.bat's FOR parser rejoins unquoted modifier lists with commas.
                if (args.Count == 0 || token is "&" or "&&" or "|" or ">" or "<") throw new InvalidDataException("Неподдерживаемая команда в стратегии.");
                args[^1] += args[^1].EndsWith('=') ? token : "," + token;
            }
        }
        if (!args.Any(a => a.StartsWith("--wf-")) || !args.Any(a => a.StartsWith("--dpi-desync="))) throw new InvalidDataException("Нет параметров фильтрации winws.");
        for (int i = 0; i < args.Count; i++)
        {
            int equal = args[i].IndexOf('='); if (equal < 0) continue;
            string key = args[i][..equal], value = args[i][(equal + 1)..];
            bool file = key is "--hostlist" or "--hostlist-exclude" or "--ipset" or "--ipset-exclude" or "--hostlist-auto"
                || key.StartsWith("--dpi-desync-fake-") && !key.EndsWith("-mod") && !value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                || key.EndsWith("-pattern");
            if (!file || value.Length == 0 || value == "!" || value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) continue;
            bool at = value.StartsWith('@');
            string path = at ? value[1..] : value;
            path = Path.GetFullPath(path, root);
            args[i] = key + "=" + (at ? "@" : "") + path;
            if (validateFiles && !File.Exists(path) && key != "--hostlist-auto") throw new FileNotFoundException("Не найден файл стратегии: " + path);
        }
        if (string.Join(' ', args).Length > 28000) throw new InvalidDataException("Слишком длинная команда службы.");
        // Flowseal's FOR tokenizer turns --option=value into two argv entries.
        // Keep that exact form, including repeated file/modifier options.
        string[] argv = args.SelectMany(arg => arg.Contains('=') ? new[] { arg[..arg.IndexOf('=')], arg[(arg.IndexOf('=') + 1)..] } : new[] { arg }).ToArray();
        return new(root, strategy, installation.Executable, argv);
    }

    public static string[] Tokens(string text)
    {
        var result = new List<string>(); var token = new StringBuilder(); bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c)) { if (token.Length > 0) { result.Add(token.ToString()); token.Clear(); } }
            else token.Append(c);
        }
        if (quoted) throw new InvalidDataException("Незакрытая кавычка в стратегии.");
        if (token.Length > 0) result.Add(token.ToString());
        return result.ToArray();
    }

    public static (string Tcp, string Udp, string Legacy) GameFilter(string root, string service)
    {
        string body = Regex.Match(service, @"(?ims)^:game_switch_status\s*\r?\n(.*?)(?=^:[a-z_]+|\z)").Groups[1].Value;
        string fallback = Regex.Match(body, "(?im)^\\s*set \\\"?GameFilter=(\\d+)\\\"?\\s*$").Groups[1].Value;
        if (fallback.Length == 0) fallback = "12";
        string relative = Regex.Match(body, "gameFlagFile=%~dp0([^\"]+)", RegexOptions.IgnoreCase).Groups[1].Value;
        string file = Path.Combine(root, relative.Length == 0 ? @"utils\game_filter.enabled" : relative);
        if (!File.Exists(file)) return (fallback, fallback, fallback);
        string config = File.ReadAllText(file).Trim();
        if (!body.Contains("GameFilterMode", StringComparison.OrdinalIgnoreCase)) return ("1024-65535", "1024-65535", "1024-65535");
        if (!body.Contains("GameFilterTCPRange", StringComparison.OrdinalIgnoreCase))
        {
            string oldMode = config.Split('\n')[0].Trim().ToLowerInvariant();
            return oldMode switch { "all" => ("1024-65535", "1024-65535", "1024-65535"), "tcp" => ("1024-65535", fallback, "1024-65535"), _ => (fallback, "1024-65535", "1024-65535") };
        }
        string mode = "disabled", tcp = "1024-65535", udp = tcp;
        foreach (string line in config.Replace("\r", "").Split('\n'))
        {
            string[] pair = line.Split('=', 2); string key = pair[0].Trim().ToLowerInvariant(); string value = pair.Length == 2 ? pair[1].Trim() : "";
            if (key == "mode") mode = value.ToLowerInvariant();
            else if (key == "all") mode = "all";
            else if (key is "tcp" or "udp")
            {
                if (value.Length == 0) mode = key;
                else if (ValidPorts(value)) { if (key == "tcp") tcp = value.Replace(" ", ""); else udp = value.Replace(" ", ""); }
            }
        }
        return mode switch { "all" => (tcp, udp, tcp), "tcp" => (tcp, fallback, tcp), "udp" => (fallback, udp, udp), _ => (fallback, fallback, fallback) };
    }

    static bool ValidPorts(string value) => value.Replace(" ", "").Split(',').All(item =>
    {
        if (!Regex.IsMatch(item, @"^[1-9]\d{0,4}(-[1-9]\d{0,4})?$")) return false;
        int[] range = item.Split('-').Select(int.Parse).ToArray();
        return range.All(p => p <= 65535) && (range.Length == 1 || range[0] <= range[1]);
    });
}
