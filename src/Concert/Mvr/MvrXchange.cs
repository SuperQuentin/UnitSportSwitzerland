using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// MVR-xchange (#960, MVR 1.6 "Communication Format Definition"): the packet framing of TCP mode and
/// the JSON messages, pure .NET so the tests run it. A package is a 28-byte big-endian header (magic
/// 778682, version 1, number, count, type 0 JSON / 1 file, uint64 length) and its payload; a message
/// is one package or several (their payloads in order). Readers are lenient where real stations
/// differ from the spec: numbers or bools sent as strings (libMVRgdtf reads both), FromStationUUID as
/// a string or an array (the spec's table says array, its example a string), a file sent as package
/// 1 of 1 (libMVRgdtf), LEAVE's UUID as StationUUID (BlenderDMX).
/// </summary>
public static class MvrXchange
{
    public const uint Magic = 778682, PackageVersion = 1, TypeJson = 0, TypeFile = 1;
    public const int HeaderSize = 28;
    /// <summary>Refused beyond these: a JSON message, a file, a message's package count.</summary>
    public const int MaxJson = 1 << 20, MaxPackages = 1 << 16;
    public const long MaxFile = 64L << 20;
    public const string Service = "_mvrxchange._tcp.local";
    public const string Provider = "UnitSport";
    public const int VerMajor = 1, VerMinor = 6;

    public const string Join = "MVR_JOIN", JoinRet = "MVR_JOIN_RET", Leave = "MVR_LEAVE", LeaveRet = "MVR_LEAVE_RET",
        CommitType = "MVR_COMMIT", CommitRet = "MVR_COMMIT_RET", Request = "MVR_REQUEST", RequestRet = "MVR_REQUEST_RET",
        NewSessionHost = "MVR_NEW_SESSION_HOST", NewSessionHostRet = "MVR_NEW_SESSION_HOST_RET";

    // ---- packets -------------------------------------------------------------------------------------

    public readonly record struct Header(uint Version, uint Number, uint Count, uint Type, ulong Length);

    public static void WriteHeader(Span<byte> to, uint number, uint count, uint type, ulong length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(to, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(to[4..], PackageVersion);
        BinaryPrimitives.WriteUInt32BigEndian(to[8..], number);
        BinaryPrimitives.WriteUInt32BigEndian(to[12..], count);
        BinaryPrimitives.WriteUInt32BigEndian(to[16..], type);
        BinaryPrimitives.WriteUInt64BigEndian(to[20..], length);
    }

    /// <summary>The header at the start, or null when it is not one (too short, wrong magic).</summary>
    public static Header? ReadHeader(ReadOnlySpan<byte> from)
    {
        if (from.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(from) != Magic) return null;
        return new Header(BinaryPrimitives.ReadUInt32BigEndian(from[4..]), BinaryPrimitives.ReadUInt32BigEndian(from[8..]),
            BinaryPrimitives.ReadUInt32BigEndian(from[12..]), BinaryPrimitives.ReadUInt32BigEndian(from[16..]),
            BinaryPrimitives.ReadUInt64BigEndian(from[20..]));
    }

    /// <summary>A whole message as one package (what every station we know sends, files too).</summary>
    public static byte[] Packet(uint type, ReadOnlySpan<byte> payload)
    {
        var p = new byte[HeaderSize + payload.Length];
        WriteHeader(p, 0, 1, type, (ulong)payload.Length);
        payload.CopyTo(p.AsSpan(HeaderSize));
        return p;
    }

    public static byte[] Packet(JsonObject message) => Packet(TypeJson, Encoding.UTF8.GetBytes(message.ToJsonString()));

    /// <summary>One message read off a stream: its type and its payload, the packages joined.</summary>
    public sealed record Received(uint Type, byte[] Payload)
    {
        public JsonObject? Json => Type == TypeJson ? Parse(Payload) : null;
    }

    /// <summary>
    /// Reads one message (all its packages); null when the peer closed before a new one. Throws
    /// <see cref="InvalidDataException"/> on a bad header or a message over the caps, so the caller drops
    /// the connection rather than reading garbage as lengths.
    /// </summary>
    public static async Task<Received?> ReadAsync(Stream s, CancellationToken ct, long maxFile = MaxFile)
    {
        var head = new byte[HeaderSize];
        int got = await s.ReadAtLeastAsync(head, HeaderSize, throwOnEndOfStream: false, ct);
        if (got == 0) return null;
        if (got < HeaderSize) throw new EndOfStreamException("the peer closed inside a header");
        var h = Check(head, null, maxFile);
        if (h.Count <= 1)
        {
            var one = new byte[(int)h.Length];
            await s.ReadExactlyAsync(one, ct);
            return new Received(h.Type, one);
        }
        // several packages: TCP keeps them in order, so join them as they come (some number from 1)
        using var all = new MemoryStream();
        long total = 0;
        for (uint i = 0; i < h.Count; i++)
        {
            if (i > 0)
            {
                await s.ReadExactlyAsync(head, ct);
                var next = Check(head, h, maxFile);
                h = h with { Length = next.Length };
            }
            total += (long)h.Length;
            if (total > (h.Type == TypeJson ? MaxJson : maxFile)) throw new InvalidDataException($"a message over {(h.Type == TypeJson ? MaxJson : maxFile)} bytes");
            var part = new byte[(int)h.Length];
            await s.ReadExactlyAsync(part, ct);
            all.Write(part);
        }
        return new Received(h.Type, all.ToArray());
    }

    private static Header Check(ReadOnlySpan<byte> head, Header? first, long maxFile)
    {
        var h = ReadHeader(head) ?? throw new InvalidDataException("not an MVR-xchange package");
        if (h.Type is not (TypeJson or TypeFile)) throw new InvalidDataException($"package type {h.Type}");
        if (h.Count > MaxPackages) throw new InvalidDataException($"{h.Count} packages");
        if (h.Length > (ulong)(h.Type == TypeJson ? MaxJson : maxFile)) throw new InvalidDataException($"a {h.Length}-byte payload");
        if (first is { } f && (h.Type != f.Type || h.Count != f.Count)) throw new InvalidDataException("a package of another message");
        return h;
    }

    public static async Task WriteAsync(Stream s, uint type, byte[] payload, CancellationToken ct)
    {
        var head = new byte[HeaderSize];
        WriteHeader(head, 0, 1, type, (ulong)payload.Length);
        await s.WriteAsync(head, ct);
        await s.WriteAsync(payload, ct);
        await s.FlushAsync(ct);
    }

    public static Task WriteAsync(Stream s, JsonObject message, CancellationToken ct) => WriteAsync(s, TypeJson, Encoding.UTF8.GetBytes(message.ToJsonString()), ct);

    // ---- messages ------------------------------------------------------------------------------------

    /// <summary>A JSON object, or null for anything else (bad UTF-8 or JSON, an array, too deep, too big).</summary>
    public static JsonObject? Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length > MaxJson) return null;
        try { return JsonNode.Parse(utf8, documentOptions: new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject; }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    public static string TypeOf(JsonObject m) => Str(m, "Type");

    public static string Str(JsonObject m, string key) => m[key] is JsonValue v && v.TryGetValue(out string? s) ? s ?? "" : m[key] is JsonValue n ? n.ToJsonString() : "";

    public static long Int(JsonObject m, string key)
    {
        if (m[key] is not JsonValue v) return 0;
        if (v.TryGetValue(out long l)) return l;
        if (v.TryGetValue(out double d)) return (long)d;
        return v.TryGetValue(out string? s) && long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out l) ? l : 0;
    }

    public static bool Bool(JsonObject m, string key) => m[key] is JsonValue v
        && (v.TryGetValue(out bool b) ? b : v.TryGetValue(out string? s) && s.Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>A UUID list sent as an array or as one string (empty string: none).</summary>
    public static string[] Uuids(JsonObject m, string key) => m[key] switch
    {
        JsonArray a => a.OfType<JsonValue>().Select(x => x.TryGetValue(out string? s) ? s : null).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray(),
        JsonValue v when v.TryGetValue(out string? s) && s.Length > 0 => new[] { s },
        _ => Array.Empty<string>(),
    };

    /// <summary>UUIDs compare by value: grandMA3 shows them upper case, others lower.</summary>
    public static bool Same(string? a, string? b) => string.Equals(a?.Trim('{', '}'), b?.Trim('{', '}'), StringComparison.OrdinalIgnoreCase);

    public static string NewUuid() => Guid.NewGuid().ToString("D");

    /// <summary>One revision a station offers: what a COMMIT, and a JOIN's <c>Commits</c>, carry.</summary>
    public sealed record Commit(string FileUuid, string StationUuid, long FileSize, string FileName, string Comment)
    {
        public int VerMajor { get; init; } = MvrXchange.VerMajor;
        public int VerMinor { get; init; } = MvrXchange.VerMinor;
        public string[] ForStations { get; init; } = Array.Empty<string>();

        public JsonObject ToJson(bool message = true)
        {
            var o = new JsonObject();
            if (message) o["Type"] = CommitType;
            o["verMajor"] = VerMajor;
            o["verMinor"] = VerMinor;
            o["FileSize"] = FileSize;
            o["FileUUID"] = FileUuid;
            o["StationUUID"] = StationUuid;
            o["ForStationsUUID"] = new JsonArray(ForStations.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
            o["Comment"] = Comment;
            o["FileName"] = FileName;
            return o;
        }

        /// <summary>A commit from JSON; null without a FileUUID (nothing could ask for it).</summary>
        public static Commit? From(JsonObject o, string? stationFallback = null)
        {
            string file = Str(o, "FileUUID");
            if (file.Length == 0) return null;
            string station = Str(o, "StationUUID");
            return new Commit(file, station.Length > 0 ? station : stationFallback ?? "", Math.Max(0, Int(o, "FileSize")), Str(o, "FileName"), Str(o, "Comment"))
            {
                VerMajor = (int)Int(o, "verMajor"), VerMinor = (int)Int(o, "verMinor"), ForStations = Uuids(o, "ForStationsUUID"),
            };
        }

        /// <summary>Whether a station is among those this commit is for (none listed: everyone).</summary>
        public bool For(string station) => ForStations.Length == 0 || ForStations.Any(s => Same(s, station));
    }

    /// <summary>MVR_JOIN, or its answer MVR_JOIN_RET: who we are and what we offer.</summary>
    public static JsonObject JoinMessage(string stationName, string stationUuid, IEnumerable<Commit> commits, bool answer = false, bool ok = true, string message = "")
    {
        var o = new JsonObject { ["Type"] = answer ? JoinRet : Join };
        if (answer) { o["OK"] = ok; o["Message"] = message; }
        o["Provider"] = Provider;
        o["StationName"] = stationName;
        o["StationUUID"] = stationUuid;
        o["verMajor"] = VerMajor;
        o["verMinor"] = VerMinor;
        o["Commits"] = new JsonArray(commits.Select(c => (JsonNode?)c.ToJson(false)).ToArray());
        return o;
    }

    /// <summary>The commits a JOIN or JOIN_RET lists (<c>Files</c> in older stations), the bad ones skipped.</summary>
    public static List<Commit> CommitsOf(JsonObject m)
    {
        string station = Str(m, "StationUUID");
        var list = new List<Commit>();
        if ((m["Commits"] ?? m["Files"]) is JsonArray a)
            foreach (var c in a.OfType<JsonObject>().Take(1024))
                if (Commit.From(c, station) is { } commit) list.Add(commit);
        return list;
    }

    public static JsonObject LeaveMessage(string stationUuid) => new() { ["Type"] = Leave, ["FromStationUUID"] = stationUuid };

    /// <summary>The station a LEAVE is from.</summary>
    public static string LeaverOf(JsonObject m) => Str(m, "FromStationUUID") is { Length: > 0 } s ? s : Str(m, "StationUUID");

    /// <summary>MVR_REQUEST: a file of a station (an empty FileUUID asks for its latest).</summary>
    public static JsonObject RequestMessage(string fileUuid, string fromStation) => new()
    {
        ["Type"] = Request,
        ["FileUUID"] = fileUuid,
        ["FromStationUUID"] = new JsonArray(fromStation.Length > 0 ? new JsonNode?[] { JsonValue.Create(fromStation) } : Array.Empty<JsonNode?>()),
    };

    /// <summary>An answer that carries only OK and Message (LEAVE_RET, COMMIT_RET, REQUEST_RET, NEW_SESSION_HOST_RET).</summary>
    public static JsonObject Ret(string type, bool ok, string message = "") => new() { ["Type"] = type, ["OK"] = ok, ["Message"] = message };
}
