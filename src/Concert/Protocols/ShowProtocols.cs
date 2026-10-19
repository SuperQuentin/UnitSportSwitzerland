using System.Buffers.Binary;
using System.Text;

namespace UnitSport.Concert.Protocols;

/// <summary>One universe's DMX as it arrived: which universe (the protocol's own numbering), its sequence, its slots.</summary>
public readonly record struct DmxPacket(int Universe, byte Sequence, byte[] Data, int Length);

/// <summary>
/// Art-Net 4 (Artistic Licence), the packets a lighting show needs (#885): <c>ArtDmx</c> in and out,
/// <c>ArtPoll</c> recognised and <c>ArtPollReply</c> answered so a controller (Chataigne, TouchDesigner,
/// a console) finds the game on the network. UDP port 6454; the ID <c>Art-Net\0</c>; the opcode
/// little-endian, everything else big-endian; protocol version 14. The universe is the 15-bit
/// port-address (Net 7 bits, Sub-Net 4, Universe 4). Pure: no socket, no Godot.
/// </summary>
public static class ArtNet
{
    public const int Port = 6454;
    public const ushort OpPoll = 0x2000, OpPollReply = 0x2100, OpDmx = 0x5000;
    private static readonly byte[] Id = "Art-Net\0"u8.ToArray();

    public static ushort OpCode(ReadOnlySpan<byte> p) =>
        p.Length >= 10 && p[..8].SequenceEqual(Id) ? BinaryPrimitives.ReadUInt16LittleEndian(p[8..]) : (ushort)0;

    /// <summary>An ArtDmx packet's universe and slots, or null when it is not one (or is malformed).</summary>
    public static DmxPacket? ParseDmx(ReadOnlySpan<byte> p)
    {
        if (OpCode(p) != OpDmx || p.Length < 18) return null;
        int universe = (p[15] & 0x7f) << 8 | p[14];
        int length = Math.Min((int)BinaryPrimitives.ReadUInt16BigEndian(p[16..]), 512);
        if (length < 1 || p.Length < 18 + length) return null;
        var data = new byte[512];
        p.Slice(18, length).CopyTo(data);
        return new DmxPacket(universe, p[12], data, length);
    }

    /// <summary>An ArtDmx packet for one universe (the length rounded up to even, as the spec asks).</summary>
    public static byte[] EncodeDmx(int universe, byte sequence, ReadOnlySpan<byte> slots)
    {
        int length = Math.Min(512, (slots.Length + 1) & ~1);
        var p = new byte[18 + length];
        Id.CopyTo(p, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), OpDmx);
        p[10] = 0; p[11] = 14;
        p[12] = sequence;
        p[13] = 0;
        p[14] = (byte)(universe & 0xff);
        p[15] = (byte)((universe >> 8) & 0x7f);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(16), (ushort)length);
        slots[..Math.Min(slots.Length, length)].CopyTo(p.AsSpan(18));
        return p;
    }

    /// <summary>
    /// The reply to an ArtPoll: who we are (a node with output ports, the universes it listens to),
    /// so a controller lists the game and patches to it. 239 bytes, as Art-Net 4 lays it out.
    /// </summary>
    public static byte[] EncodePollReply(byte[] ipv4, string shortName, string longName, IReadOnlyList<int> universes)
    {
        var p = new byte[239];
        Id.CopyTo(p, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(8), OpPollReply);
        ipv4.AsSpan(0, 4).CopyTo(p.AsSpan(10));
        BinaryPrimitives.WriteUInt16LittleEndian(p.AsSpan(14), Port);
        p[16] = 0; p[17] = 1;                                    // firmware
        int first = universes.Count > 0 ? universes[0] : 0;
        p[18] = (byte)((first >> 8) & 0x7f);                     // Net
        p[19] = (byte)((first >> 4) & 0x0f);                     // Sub-Net
        p[23] = 0xd0;                                            // status 1: indicators normal, port-address by network
        Encoding.ASCII.GetBytes(shortName.Length > 17 ? shortName[..17] : shortName).CopyTo(p, 26);
        Encoding.ASCII.GetBytes(longName.Length > 63 ? longName[..63] : longName).CopyTo(p, 44);
        Encoding.ASCII.GetBytes("#0001 [0000] UnitSport ok").CopyTo(p, 108);
        int ports = Math.Min(4, universes.Count);
        p[173] = (byte)ports;
        for (int i = 0; i < ports; i++)
        {
            p[174 + i] = 0x80;                                   // port type: can output DMX from the network
            p[182 + i] = 0x80;                                   // good output: data being transmitted
            p[190 + i] = (byte)(universes[i] & 0x0f);            // SwOut
        }
        p[200] = 0;                                              // style: StNode
        return p;
    }
}

/// <summary>
/// sACN, ANSI E1.31 (streaming DMX over ACN), data packets (#885): root layer (vector 4), framing
/// layer (vector 2: source name, priority, sequence, universe), DMP layer (start code 0, then the
/// slots). UDP port 5568; a universe's multicast group is 239.255.{hi}.{lo}. Universes count from 1.
/// Pure.
/// </summary>
public static class Sacn
{
    public const int Port = 5568;
    private static readonly byte[] AcnId = "ASC-E1.17\0\0\0"u8.ToArray();

    /// <summary>The multicast group a receiver joins for a universe.</summary>
    public static string Multicast(int universe) => $"239.255.{(universe >> 8) & 0xff}.{universe & 0xff}";

    public static DmxPacket? ParseData(ReadOnlySpan<byte> p)
    {
        if (p.Length < 126 || !p.Slice(4, 12).SequenceEqual(AcnId)) return null;
        if (BinaryPrimitives.ReadUInt32BigEndian(p[18..]) != 4) return null;    // VECTOR_ROOT_E131_DATA
        if (BinaryPrimitives.ReadUInt32BigEndian(p[40..]) != 2) return null;    // VECTOR_E131_DATA_PACKET
        if (p[117] != 2 || p[125] != 0) return null;                            // DMP set property, start code 0
        // options bit 6: stream terminated; bit 7 preview data: neither is a frame to show
        if ((p[112] & 0xc0) != 0) return null;
        int universe = BinaryPrimitives.ReadUInt16BigEndian(p[113..]);
        int count = BinaryPrimitives.ReadUInt16BigEndian(p[123..]) - 1;
        int length = Math.Min(Math.Min(count, 512), p.Length - 126);
        if (length < 1) return null;
        var data = new byte[512];
        p.Slice(126, length).CopyTo(data);
        return new DmxPacket(universe, p[111], data, length);
    }

    public static byte[] EncodeData(int universe, byte sequence, ReadOnlySpan<byte> slots, string source, Guid cid, byte priority = 100)
    {
        int n = Math.Min(512, slots.Length);
        var p = new byte[126 + n];
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(0), 0x0010);
        AcnId.CopyTo(p, 4);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(16), (ushort)(0x7000 | (p.Length - 16)));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(18), 4);
        cid.ToByteArray().CopyTo(p, 22);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(38), (ushort)(0x7000 | (p.Length - 38)));
        BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(40), 2);
        var name = Encoding.UTF8.GetBytes(source);
        name.AsSpan(0, Math.Min(name.Length, 63)).CopyTo(p.AsSpan(44));
        p[108] = priority;
        p[111] = sequence;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(113), (ushort)universe);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(115), (ushort)(0x7000 | (p.Length - 115)));
        p[117] = 2;
        p[118] = 0xa1;
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(121), 1);
        BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(123), (ushort)(n + 1));
        p[125] = 0;
        slots[..n].CopyTo(p.AsSpan(126));
        return p;
    }
}

/// <summary>One OSC message: its address and its arguments (int, float, string or bool).</summary>
public sealed record OscMessage(string Address, object[] Args)
{
    public float Float(int i, float fallback = 0f) => i < Args.Length ? Args[i] switch
    {
        float f => f, int n => n, bool b => b ? 1f : 0f, string s when float.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float v) => v, _ => fallback,
    } : fallback;

    public int Int(int i, int fallback = 0) => i < Args.Length ? Args[i] switch
    {
        int n => n, float f => (int)MathF.Round(f), bool b => b ? 1 : 0, string s when int.TryParse(s, out int v) => v, _ => fallback,
    } : fallback;
}

/// <summary>
/// Open Sound Control 1.0 (#885), what Chataigne and TouchDesigner send: messages (an address, a
/// type tag string, 4-byte aligned big-endian arguments: <c>i</c> int32, <c>f</c> float32, <c>s</c>
/// string, <c>T</c>/<c>F</c> booleans, <c>N</c> nil) and bundles of them (<c>#bundle</c>, a time tag,
/// sized elements, nested). Pure.
/// </summary>
public static class Osc
{
    public const int DefaultPort = 9000;

    /// <summary>Every message in a packet (a bundle's, depth first); malformed parts are skipped.</summary>
    public static List<OscMessage> Parse(ReadOnlySpan<byte> p)
    {
        var list = new List<OscMessage>();
        ParseInto(p, list, 0);
        return list;
    }

    private static void ParseInto(ReadOnlySpan<byte> p, List<OscMessage> into, int depth)
    {
        if (p.Length < 4 || depth > 8) return;
        if (p[0] == '#')
        {
            if (p.Length < 16 || !p[..8].SequenceEqual("#bundle\0"u8)) return;
            int at = 16;
            while (at + 4 <= p.Length)
            {
                int size = BinaryPrimitives.ReadInt32BigEndian(p[at..]);
                at += 4;
                if (size <= 0 || at + size > p.Length) return;
                ParseInto(p.Slice(at, size), into, depth + 1);
                at += size;
            }
            return;
        }
        if (Message(p) is { } m) into.Add(m);
    }

    private static OscMessage? Message(ReadOnlySpan<byte> p)
    {
        int at = 0;
        if (ReadString(p, ref at) is not { } address || address.Length == 0 || address[0] != '/') return null;
        if (at >= p.Length) return new OscMessage(address, Array.Empty<object>());
        if (ReadString(p, ref at) is not { } tags || tags.Length == 0 || tags[0] != ',') return null;
        var args = new List<object>();
        foreach (char t in tags.AsSpan(1))
        {
            switch (t)
            {
                case 'i':
                    if (at + 4 > p.Length) return null;
                    args.Add(BinaryPrimitives.ReadInt32BigEndian(p[at..])); at += 4; break;
                case 'f':
                    if (at + 4 > p.Length) return null;
                    args.Add(BinaryPrimitives.ReadSingleBigEndian(p[at..])); at += 4; break;
                case 's':
                    if (ReadString(p, ref at) is not { } s) return null;
                    args.Add(s); break;
                case 'T': args.Add(true); break;
                case 'F': args.Add(false); break;
                case 'N': break;
                default: return null;   // a type we do not read: the rest cannot be aligned
            }
        }
        return new OscMessage(address, args.ToArray());
    }

    /// <summary>A null-terminated string padded to 4 bytes.</summary>
    private static string? ReadString(ReadOnlySpan<byte> p, ref int at)
    {
        int end = p[at..].IndexOf((byte)0);
        if (end < 0) return null;
        var s = Encoding.UTF8.GetString(p.Slice(at, end));
        at += (end + 4) & ~3;
        return s;
    }

    public static byte[] Encode(string address, params object[] args)
    {
        using var ms = new MemoryStream();
        WriteString(ms, address);
        var tags = new StringBuilder(",");
        foreach (var a in args) tags.Append(a switch { int => 'i', float => 'f', string => 's', true => 'T', false => 'F', _ => 'N' });
        WriteString(ms, tags.ToString());
        Span<byte> b = stackalloc byte[4];
        foreach (var a in args)
            switch (a)
            {
                case int n: BinaryPrimitives.WriteInt32BigEndian(b, n); ms.Write(b); break;
                case float f: BinaryPrimitives.WriteSingleBigEndian(b, f); ms.Write(b); break;
                case string s: WriteString(ms, s); break;
            }
        return ms.ToArray();
    }

    private static void WriteString(Stream s, string v)
    {
        var bytes = Encoding.UTF8.GetBytes(v);
        s.Write(bytes);
        int pad = 4 - bytes.Length % 4;
        for (int i = 0; i < pad; i++) s.WriteByte(0);
    }
}
