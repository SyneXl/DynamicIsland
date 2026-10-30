using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace DynamicIsland;

// The bounded subset of protobuf wire types used by Chromium's Lens messages.
// Unknown fields are skipped. No generated code or remote script is executed.
sealed class LensProto
{
    readonly record struct Field(int Number, int Wire, ulong Value, ReadOnlyMemory<byte> Data);
    readonly List<Field> _fields = [];
    readonly int _depth;
    static readonly UTF8Encoding Utf8 = new(false, true);

    public LensProto(ReadOnlyMemory<byte> bytes, int depth = 0)
    {
        if (depth > 24 || bytes.Length > 12_000_000) throw new FormatException();
        _depth = depth;
        var data = bytes.Span; int p = 0;
        while (p < data.Length)
        {
            if (_fields.Count >= 100_000) throw new FormatException();
            ulong tag = ReadVarint(data, ref p);
            int number = checked((int)(tag >> 3)), wire = (int)(tag & 7);
            if (number <= 0 || number > 536870911) throw new FormatException();
            ulong value = 0; ReadOnlyMemory<byte> payload = default;
            switch (wire)
            {
                case 0: value = ReadVarint(data, ref p); break;
                case 1:
                    if (p > data.Length - 8) throw new FormatException();
                    value = BinaryPrimitives.ReadUInt64LittleEndian(data[p..]); p += 8; break;
                case 2:
                    ulong length = ReadVarint(data, ref p);
                    if (length > (ulong)(data.Length - p)) throw new FormatException();
                    payload = bytes.Slice(p, (int)length); p += (int)length; break;
                case 5:
                    if (p > data.Length - 4) throw new FormatException();
                    value = BinaryPrimitives.ReadUInt32LittleEndian(data[p..]); p += 4; break;
                default: throw new FormatException();
            }
            _fields.Add(new(number, wire, value, payload));
        }
    }

    static ulong ReadVarint(ReadOnlySpan<byte> data, ref int p)
    {
        ulong value = 0;
        for (int shift = 0; shift <= 63; shift += 7)
        {
            if (p >= data.Length) throw new FormatException();
            byte part = data[p++];
            if (shift == 63 && (part & 0xfe) != 0) throw new FormatException();
            value |= (ulong)(part & 127) << shift;
            if (part < 128) return value;
        }
        throw new FormatException();
    }
    public bool Has(int number) => _fields.Any(f => f.Number == number);
    public ulong Number(int number, ulong fallback = 0) => _fields.FirstOrDefault(f => f.Number == number && f.Wire == 0) is var f && f.Number != 0 ? f.Value : fallback;
    public double Float(int number) => _fields.FirstOrDefault(f => f.Number == number && f.Wire == 5) is var f && f.Number != 0
        ? BitConverter.Int32BitsToSingle((int)(uint)f.Value) : 0;
    public IEnumerable<LensProto> Messages(int number) => _fields.Where(f => f.Number == number && f.Wire == 2).Select(f => new LensProto(f.Data, _depth + 1));
    public LensProto Message(int number) => Messages(number).FirstOrDefault() ?? new LensProto(ReadOnlyMemory<byte>.Empty, _depth + 1);
    public ReadOnlyMemory<byte> Data(int number) => _fields.FirstOrDefault(f => f.Number == number && f.Wire == 2).Data;
    public string Text(int number, int limit = 30_000)
    {
        var data = Data(number);
        if (data.Length > limit * 4) throw new FormatException();
        string value = Utf8.GetString(data.Span);
        if (value.Length > limit) throw new FormatException();
        return value;
    }
    public IEnumerable<string> Texts(int number, int limit = 200) => _fields.Where(f => f.Number == number && f.Wire == 2).Select(f =>
    {
        if (f.Data.Length > limit * 4) throw new FormatException();
        string text = Utf8.GetString(f.Data.Span);
        return text.Length <= limit ? text : throw new FormatException();
    });

    static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 128) { stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
    public static byte[] Integer(int field, ulong value)
    {
        using var stream = new MemoryStream(); WriteVarint(stream, (ulong)field << 3); WriteVarint(stream, value); return stream.ToArray();
    }
    public static byte[] Bytes(int field, byte[] bytes)
    {
        using var stream = new MemoryStream(); WriteVarint(stream, ((ulong)field << 3) | 2); WriteVarint(stream, (ulong)bytes.Length);
        stream.Write(bytes); return stream.ToArray();
    }
    public static byte[] String(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
    public static byte[] Pack(params byte[][] fields)
    {
        using var stream = new MemoryStream(); foreach (var field in fields) stream.Write(field); return stream.ToArray();
    }
}
