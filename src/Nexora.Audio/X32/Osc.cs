using System.Buffers.Binary;
using System.Text;

namespace Nexora.Audio.X32;

/// <summary>
/// One OSC message, the small network language the X32 speaks on UDP port 10023.
/// Arguments can be int, float, string or a byte blob.
/// </summary>
public sealed record OscMessage(string Address, IReadOnlyList<object> Args)
{
    public OscMessage(string address, params object[] args) : this(address, (IReadOnlyList<object>)args) { }

    public string? String(int i) => i < Args.Count ? Args[i] as string : null;
    public int? Int(int i) => i < Args.Count && Args[i] is int v ? v : null;
    public float? Float(int i) => i < Args.Count && Args[i] is float v ? v : null;

    public byte[] Encode()
    {
        var ms = new MemoryStream();
        WriteString(ms, Address);
        var tags = new StringBuilder(",");
        foreach (var a in Args)
            tags.Append(a switch
            {
                int => 'i',
                float => 'f',
                string => 's',
                byte[] => 'b',
                _ => throw new ArgumentException($"OSC cannot send a {a.GetType().Name}"),
            });
        WriteString(ms, tags.ToString());
        Span<byte> four = stackalloc byte[4];
        foreach (var a in Args)
        {
            switch (a)
            {
                case int i:
                    BinaryPrimitives.WriteInt32BigEndian(four, i);
                    ms.Write(four);
                    break;
                case float f:
                    BinaryPrimitives.WriteSingleBigEndian(four, f);
                    ms.Write(four);
                    break;
                case string s:
                    WriteString(ms, s);
                    break;
                case byte[] b:
                    BinaryPrimitives.WriteInt32BigEndian(four, b.Length);
                    ms.Write(four);
                    ms.Write(b);
                    Pad(ms);
                    break;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Reads a message, or returns null if the bytes are not a valid OSC message.</summary>
    public static OscMessage? Decode(ReadOnlySpan<byte> data)
    {
        try
        {
            var pos = 0;
            var address = ReadString(data, ref pos);
            if (!address.StartsWith('/')) return null;
            if (pos >= data.Length) return new OscMessage(address, Array.Empty<object>());
            var tags = ReadString(data, ref pos);
            if (!tags.StartsWith(',')) return null;
            var args = new List<object>();
            foreach (var t in tags.AsSpan(1))
            {
                switch (t)
                {
                    case 'i':
                        args.Add(BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4)));
                        pos += 4;
                        break;
                    case 'f':
                        args.Add(BinaryPrimitives.ReadSingleBigEndian(data.Slice(pos, 4)));
                        pos += 4;
                        break;
                    case 's':
                        args.Add(ReadString(data, ref pos));
                        break;
                    case 'b':
                        var len = BinaryPrimitives.ReadInt32BigEndian(data.Slice(pos, 4));
                        pos += 4;
                        args.Add(data.Slice(pos, len).ToArray());
                        pos += (len + 3) & ~3;
                        break;
                    default:
                        return null;
                }
            }
            return new OscMessage(address, args);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void WriteString(MemoryStream ms, string s)
    {
        ms.Write(Encoding.UTF8.GetBytes(s));
        ms.WriteByte(0);
        Pad(ms);
    }

    private static void Pad(MemoryStream ms)
    {
        while (ms.Length % 4 != 0) ms.WriteByte(0);
    }

    private static string ReadString(ReadOnlySpan<byte> data, ref int pos)
    {
        var end = data[pos..].IndexOf((byte)0);
        if (end < 0) throw new FormatException("Unterminated OSC string");
        var s = Encoding.UTF8.GetString(data.Slice(pos, end));
        pos += (end + 4) & ~3;
        return s;
    }

    public override string ToString() => Address + " " + string.Join(" ", Args);
}
