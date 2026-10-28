using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChaCha20Poly1305 = Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305;
using Org.BouncyCastle.Crypto.Parameters;

namespace DynamicIsland;

// throne-api/1, checked against the upstream wire vectors. Managed AEAD also
// works on Windows 10 19041, where the OS ChaCha20 provider is unavailable.
static class ThroneWire
{
    public const int MaxRequest = 1_048_576, MaxResponse = 67_108_864;
    public static byte[] Key(string text)
    {
        text = text.Trim();
        if (text.Length != 43 || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new InvalidDataException("Некорректный ключ API Throne");
        byte[] key = Convert.FromBase64String(text.Replace('-', '+').Replace('_', '/') + "=");
        if (key.Length != 32 || Convert.ToBase64String(key).TrimEnd('=').Replace('+', '-').Replace('/', '_') != text)
            throw new InvalidDataException("Некорректный ключ API Throne");
        return key;
    }

    public static byte[] Derive(byte[] key, byte[] server, byte[] client, bool sending)
    {
        if (key.Length != 32 || server.Length != 16 || client.Length != 16) throw new InvalidDataException();
        return HMACSHA256.HashData(key, Encoding.ASCII.GetBytes(sending ? "throne-api/1 c2s" : "throne-api/1 s2c").Concat(server).Concat(client).ToArray());
    }

    public static byte[] Seal(byte[] key, ulong counter, byte[] plain)
    {
        if (plain.Length > MaxResponse - 16) throw new InvalidDataException();
        byte[] frame = new byte[4 + plain.Length + 16];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)plain.Length + 16);
        byte[] nonce = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        var cipher = new ChaCha20Poly1305();
        cipher.Init(true, new AeadParameters(new KeyParameter(key), 128, nonce, frame[..4]));
        int n = cipher.ProcessBytes(plain, 0, plain.Length, frame, 4);
        cipher.DoFinal(frame, 4 + n);
        return frame;
    }

    public static byte[] Open(byte[] key, ulong counter, byte[] frame)
    {
        if (frame.Length < 20 || frame.Length > MaxResponse + 4 || BinaryPrimitives.ReadUInt32BigEndian(frame) != frame.Length - 4)
            throw new InvalidDataException("Некорректный ответ Throne");
        byte[] nonce = new byte[12]; BinaryPrimitives.WriteUInt64BigEndian(nonce.AsSpan(4), counter);
        byte[] plain = new byte[frame.Length - 20];
        try
        {
            var cipher = new ChaCha20Poly1305();
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, frame[..4]));
            int n = cipher.ProcessBytes(frame, 4, frame.Length - 4, plain, 0);
            cipher.DoFinal(plain, n);
            return plain;
        }
        catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
        {
            CryptographicOperations.ZeroMemory(plain);
            throw new InvalidDataException("Ключ API Throne изменился. Обнови подключение");
        }
    }
}

sealed class ThroneEndpoint : IDisposable
{
    public string Address { get; }
    public int Port { get; }
    internal byte[] Secret { get; }
    public ThroneEndpoint(string address, int port, string key)
    {
        if (!IPAddress.TryParse(address, out var ip) || !IPAddress.IsLoopback(ip) || port is < 1 or > 65535)
            throw new InvalidDataException("API Throne должен слушать локальный адрес");
        Address = ip.ToString(); Port = port; Secret = ThroneWire.Key(key);
    }
    public void Dispose() => CryptographicOperations.ZeroMemory(Secret);
    public override string ToString() => $"{Address}:{Port}"; // never print the key
}

sealed class ThroneApiException(string code, int status) : Exception(MessageFor(code))
{
    public string Code { get; } = code;
    public int Status { get; } = status;
    static string MessageFor(string code) => code switch
    {
        "busy" => "Throne занят. Попробуй через несколько секунд",
        "not_found" => "Список изменился. Обнови серверы",
        "needs_privileges" => "Разреши работу TUN в окне Throne",
        "no_profiles" => "В группе нет серверов",
        "group_archived" => "Эта группа находится в архиве",
        "core_unavailable" => "Ядро Throne недоступно",
        "tun_conflict" => "TUN уже используется другим подключением",
        "kill_switch_inactive" or "kill_switch_blocked" => "Проверь Kill Switch в Throne",
        _ => "Throne не выполнил действие. Проверь его окно"
    };
}

interface IThroneApi
{
    Task<JsonElement> CallAsync(string method, string path, object? body = null, Dictionary<string, string>? query = null, CancellationToken cancel = default);
}

sealed class ThroneApi(ThroneEndpoint endpoint) : IThroneApi
{
    public async Task<JsonElement> CallAsync(string method, string path, object? body = null, Dictionary<string, string>? query = null, CancellationToken cancel = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(TimeSpan.FromSeconds(40));
        using var tcp = new TcpClient();
        byte[]? sendKey = null, receiveKey = null;
        try
        {
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                connect.CancelAfter(TimeSpan.FromSeconds(3));
                await tcp.ConnectAsync(IPAddress.Parse(endpoint.Address), endpoint.Port, connect.Token);
            }
            using var stream = tcp.GetStream();
            byte[] client = RandomNumberGenerator.GetBytes(16), server = new byte[16];
            using (var hello = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            {
                hello.CancelAfter(TimeSpan.FromSeconds(3));
                await stream.WriteAsync(new byte[] { 1 }.Concat(client).ToArray(), hello.Token);
                await stream.ReadExactlyAsync(server, hello.Token);
            }
            sendKey = ThroneWire.Derive(endpoint.Secret, server, client, true);
            receiveKey = ThroneWire.Derive(endpoint.Secret, server, client, false);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new { method, path, query, body }, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            if (payload.Length + 16 > ThroneWire.MaxRequest) throw new InvalidDataException();
            await stream.WriteAsync(ThroneWire.Seal(sendKey, 0, payload), timeout.Token);
            byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size < 16 || size > ThroneWire.MaxResponse) throw new InvalidDataException("Слишком большой ответ Throne");
            byte[] frame = new byte[size + 4]; header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(4), timeout.Token);
            byte[] plain = ThroneWire.Open(receiveKey, 0, frame);
            try
            {
                using var doc = JsonDocument.Parse(plain);
                var response = doc.RootElement;
                int status = response.GetProperty("status").GetInt32();
                if (status is < 200 or >= 300)
                    throw new ThroneApiException(response.GetProperty("error").GetProperty("code").GetString() ?? "unknown", status);
                return response.GetProperty("body").Clone();
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new TimeoutException("Throne не ответил. Проверь, включён ли API"); }
        finally
        {
            if (sendKey != null) CryptographicOperations.ZeroMemory(sendKey);
            if (receiveKey != null) CryptographicOperations.ZeroMemory(receiveKey);
        }
    }
}

sealed record ThroneGroup(int Id, string Name, bool Subscription, int Count);
sealed record ThroneProfile(int Id, int Group, string Name, int Latency, string Type, bool Running);
sealed record ThroneStatus(string State, int ProfileId, string ProfileName, int LastProfileId)
{
    public bool Running => State == "running";
    public bool Transitioning => State is "connecting" or "stopping";
    public static ThroneStatus Parse(JsonElement json) => new(json.GetProperty("state").GetString() ?? "unknown",
        json.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetProperty("id").GetInt32() : -1,
        p.ValueKind == JsonValueKind.Object ? p.GetProperty("name").GetString() ?? "" : "",
        json.TryGetProperty("last_profile_id", out var last) ? last.GetInt32() : -1);
}

static class ThroneData
{
    public static ThroneGroup[] Groups(JsonElement body) => body.GetProperty("groups").EnumerateArray()
        .Where(g => !g.GetProperty("archived").GetBoolean())
        .Select(g => new ThroneGroup(g.GetProperty("id").GetInt32(), g.GetProperty("name").GetString() ?? "", g.GetProperty("subscription").GetBoolean(), g.GetProperty("profile_count").GetInt32())).ToArray();
    public static ThroneProfile[] Profiles(JsonElement body) => body.GetProperty("profiles").EnumerateArray()
        .Select(p => new ThroneProfile(p.GetProperty("id").GetInt32(), p.GetProperty("group").GetInt32(), p.GetProperty("name").GetString() ?? "",
            p.GetProperty("latency_ms").GetInt32(), p.GetProperty("type").GetString() ?? "", p.GetProperty("running").GetBoolean())).ToArray();
    public static ThroneProfile[] Sorted(IEnumerable<ThroneProfile> profiles, bool latency) => latency
        ? profiles.OrderBy(p => p.Latency > 0 ? 0 : p.Latency == 0 ? 1 : 2).ThenBy(p => p.Latency > 0 ? p.Latency : 0).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(p => p.Id).ToArray()
        : profiles.ToArray();
    public static string LatencyText(int ms) => ms > 0 ? $"{ms} мс" : ms == 0 ? "—" : "Недоступен";
}
