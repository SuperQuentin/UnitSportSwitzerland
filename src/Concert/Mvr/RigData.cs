using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

namespace UnitSport.Concert.Mvr;

/// <summary>
/// A rig as data (#941): its fixture types (each channel's attribute, its kind, its ranges) and its
/// fixtures (type, group, place, home, yoke axis, DMX address), in the stadium frame. What an MVR
/// import makes, what the server checks and sends to every client of a hosted set (gzipped JSON, a
/// few kB for a stadium rig), and what <see cref="StagePlan.UseRig"/> patches in place of the
/// generated rig. Pure: no node, no file.
/// </summary>
public sealed partial class RigData
{
    public const int Version = 1;
    /// <summary>The server's limits on a rig a player sends.</summary>
    public const int MaxTypes = 128, MaxFixtures = 4000, MaxUniverses = 64, MaxBytes = 2 << 20;

    public sealed class TypeRow
    {
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("k")] public int Kind { get; set; }
        [JsonPropertyName("c")] public int[] Channels { get; set; } = Array.Empty<int>();
        [JsonPropertyName("pan")] public float PanRange { get; set; }
        [JsonPropertyName("tilt")] public float TiltRange { get; set; }
        [JsonPropertyName("b0")] public float BeamMin { get; set; } = 4f;
        [JsonPropertyName("b1")] public float BeamMax { get; set; } = 40f;
        /// <summary>
        /// An imported type's GDTFSpec and GDTFMode as its file named them (#959), so a re-export keeps
        /// the original GDTF (cached on the importing machine only); null for ours.
        /// </summary>
        [JsonPropertyName("gs")] public string? GdtfSpec { get; set; }
        [JsonPropertyName("gm")] public string? GdtfMode { get; set; }
    }

    public sealed class FixtureRow
    {
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("t")] public int Type { get; set; }
        [JsonPropertyName("g")] public int Group { get; set; }
        [JsonPropertyName("p")] public float[] Pos { get; set; } = new float[3];
        [JsonPropertyName("h")] public float[] Home { get; set; } = new float[3];
        [JsonPropertyName("u")] public float[] Up { get; set; } = new float[3];
        [JsonPropertyName("un")] public int Universe { get; set; }
        [JsonPropertyName("a")] public int Address { get; set; }
        /// <summary>A screen's size (m) and pixels; zero for anything else.</summary>
        [JsonPropertyName("s")] public float[]? Screen { get; set; }
        [JsonPropertyName("px")] public int PixelsX { get; set; }
        [JsonPropertyName("py")] public int PixelsY { get; set; }
        /// <summary>The MVR uuid and fixture ID it was imported with (#959), kept on re-export; null and 0 for ours.</summary>
        [JsonPropertyName("id")] public string? Uuid { get; set; }
        [JsonPropertyName("fid")] public int FixtureId { get; set; }
        /// <summary>
        /// Its focus point (#959): the pan from <see cref="Home"/> and tilt from the yoke axis (degrees)
        /// that meet it, and how far it is (m); 0 for none. Angles and a length, so a frame change keeps them.
        /// </summary>
        [JsonPropertyName("fp")] public float FocusPan { get; set; }
        [JsonPropertyName("ft")] public float FocusTilt { get; set; }
        [JsonPropertyName("fd")] public float FocusDistance { get; set; }
        /// <summary>DMXInvertPan / DMXInvertTilt.</summary>
        [JsonPropertyName("ip")] public bool InvertPan { get; set; }
        [JsonPropertyName("it")] public bool InvertTilt { get; set; }
        /// <summary>
        /// Its other DMX breaks (#959), each { break (2..), type, universe, address }: a GDTF mode whose
        /// channels span several start addresses, each break patched as a fixture of its own at its address.
        /// </summary>
        [JsonPropertyName("xb")] public List<int[]>? Breaks { get; set; }
    }

    /// <summary>The most DMX breaks a fixture may add.</summary>
    public const int MaxBreaks = 8;

    [JsonPropertyName("v")] public int Ver { get; set; } = Version;
    /// <summary>Where it came from (the file's name), for the chat and the desk.</summary>
    [JsonPropertyName("src")] public string Source { get; set; } = "";
    [JsonPropertyName("types")] public List<TypeRow> Types { get; set; } = new();
    [JsonPropertyName("fx")] public List<FixtureRow> Fixtures { get; set; } = new();

    public static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
    private static bool Bad(float[]? a) => a is not { Length: >= 3 } || !float.IsFinite(a[0]) || !float.IsFinite(a[1]) || !float.IsFinite(a[2]);
    public static Vector3 V(float[]? a) => a is { Length: >= 3 } ? new Vector3(a[0], a[1], a[2]) : Vector3.Zero;

    public static TypeRow Row(FixtureType t) => new()
    {
        Name = t.Name, Kind = (int)t.Kind, Channels = t.Channels.Select(c => (int)c).ToArray(),
        PanRange = t.PanRange, TiltRange = t.TiltRange, BeamMin = t.BeamMin, BeamMax = t.BeamMax,
    };

    public static FixtureType Type(TypeRow r) =>
        new(r.Name, (FixtureKind)r.Kind, r.Channels.Select(c => (Attr)c).ToArray(), r.PanRange, r.TiltRange, r.BeamMin, r.BeamMax);

    /// <summary>A patch as data: every fixture, its type shared by name.</summary>
    public static RigData FromPatch(DmxPatch patch, string source = "")
    {
        var rig = new RigData { Source = source };
        var index = new Dictionary<FixtureType, int>();
        foreach (var f in patch.Fixtures)
        {
            if (!index.TryGetValue(f.Type, out int t))
            {
                t = index[f.Type] = rig.Types.Count;
                rig.Types.Add(Row(f.Type));
            }
            rig.Fixtures.Add(new FixtureRow
            {
                Name = $"{f.Group} {f.Id + 1}", Type = t, Group = (int)f.Group,
                Pos = V(f.Position), Home = V(f.Home), Up = V(f.Up), Universe = f.Universe, Address = f.Address,
                Screen = f.ScreenSize != Vector3.Zero ? V(f.ScreenSize) : null, PixelsX = f.PixelsX, PixelsY = f.PixelsY,
                FocusPan = f.HomePan, FocusTilt = f.HomeTilt, InvertPan = f.InvertPan, InvertTilt = f.InvertTilt,
            });
        }
        return rig;
    }

    /// <summary>Patches every fixture at its own address; the types are made once each.</summary>
    public void PatchInto(DmxPatch patch)
    {
        var types = Types.Select(Type).ToArray();
        foreach (var r in Fixtures)
        {
            var type = types[r.Type];
            var f = patch.AddAt(type, (RigGroup)r.Group, V(r.Pos), V(r.Home), V(r.Up), r.Universe, r.Address);
            if (r.Screen != null) f = f with { ScreenSize = V(r.Screen), PixelsX = r.PixelsX, PixelsY = r.PixelsY };
            // the focus as far as the head reaches, nowhere for one that does not move
            float pan = type.PanRange / 2, tilt = type.TiltRange / 2;
            patch.Fixtures[^1] = f with
            {
                HomePan = Math.Clamp(r.FocusPan, -pan, pan), HomeTilt = Math.Clamp(r.FocusTilt, -tilt, tilt),
                InvertPan = r.InvertPan, InvertTilt = r.InvertTilt,
            };
            // another break: its channels at their own address, a fixture of their own in the same place
            if (r.Breaks != null)
                foreach (var b in r.Breaks)
                    patch.AddAt(types[b[1]], MvrFile.GroupOf(types[b[1]].Kind, V(r.Up)), V(r.Pos), V(r.Home), V(r.Up), b[2], b[3]);
        }
    }

    private static readonly JsonSerializerOptions Json = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };

    /// <summary>Gzipped JSON, for the wire and the file cache.</summary>
    public byte[] Pack()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            JsonSerializer.Serialize(gz, this, Json);
        return ms.ToArray();
    }

    /// <summary>Reads and checks a packed rig: null, with why, when it is not one or breaks a limit.</summary>
    public static RigData? Unpack(byte[] data, out string? error)
    {
        error = null;
        if (data.Length == 0 || data.Length > MaxBytes) { error = "no rig, or over 2 MB"; return null; }
        RigData? rig;
        try
        {
            using var ms = new MemoryStream(data);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var limited = new MemoryStream();
            var buf = new byte[81920];
            int n;
            while ((n = gz.Read(buf, 0, buf.Length)) > 0)
            {
                limited.Write(buf, 0, n);
                if (limited.Length > 8 * MaxBytes) { error = "the rig unpacks too large"; return null; }
            }
            limited.Position = 0;
            rig = JsonSerializer.Deserialize<RigData>(limited, Json);
        }
        catch (Exception e) when (e is JsonException or InvalidDataException or NotSupportedException)
        {
            error = "not a rig: " + e.Message;
            return null;
        }
        if (rig == null) { error = "empty"; return null; }
        error = rig.Check();
        return error == null ? rig : null;
    }

    /// <summary>Whether a rig is sane enough to build: counts, kinds, addresses inside their universes, places near the pitch.</summary>
    public string? Check()
    {
        if (Ver != Version) return $"rig version {Ver}, this game reads {Version}";
        if (Types.Count == 0 || Types.Count > MaxTypes) return $"{Types.Count} fixture types (1..{MaxTypes})";
        if (Fixtures.Count == 0 || Fixtures.Count > MaxFixtures) return $"{Fixtures.Count} fixtures (1..{MaxFixtures})";
        int kinds = Enum.GetValues<FixtureKind>().Length, attrs = Enum.GetValues<Attr>().Length, groups = Enum.GetValues<RigGroup>().Length;
        foreach (var t in Types)
        {
            if (t.Kind < 0 || t.Kind >= kinds) return $"type {t.Name}: unknown kind";
            if (t.Channels.Length == 0 || t.Channels.Length > DmxPatch.Channels) return $"type {t.Name}: {t.Channels.Length} channels";
            if (t.Channels.Any(c => c < 0 || c >= attrs)) return $"type {t.Name}: unknown channel";
            if (!float.IsFinite(t.PanRange) || !float.IsFinite(t.TiltRange) || t.PanRange is < 0 or > 720 || t.TiltRange is < 0 or > 360) return $"type {t.Name}: pan/tilt range";
            if (!float.IsFinite(t.BeamMin) || !float.IsFinite(t.BeamMax) || t.BeamMin is <= 0 or > 120 || t.BeamMax is <= 0 or > 120) return $"type {t.Name}: beam angle";
            if (t.GdtfSpec is { Length: > 256 } || t.GdtfMode is { Length: > 256 }) return $"type {t.Name}: GDTF name over 256 characters";
        }
        int patched = Fixtures.Count;
        foreach (var f in Fixtures)
        {
            if (f.Type < 0 || f.Type >= Types.Count) return $"{f.Name}: unknown type";
            if (f.Group < 0 || f.Group >= groups) return $"{f.Name}: unknown group";
            if (f.Universe < 0 || f.Universe >= MaxUniverses) return $"{f.Name}: universe {f.Universe + 1} (1..{MaxUniverses})";
            if (f.Address < 0 || f.Address + Types[f.Type].Channels.Length > DmxPatch.Channels) return $"{f.Name}: address {f.Address + 1} does not fit its universe";
            var p = V(f.Pos);
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z) || MathF.Abs(p.X) > 400 || MathF.Abs(p.Z) > 400 || p.Y is < -30 or > 200)
                return $"{f.Name}: placed {p} (within 400 m of the centre spot)";
            if (V(f.Up).LengthSquared() < 0.25f) return $"{f.Name}: no yoke axis";
            if (f.Screen != null && (f.PixelsX is < 1 or > 128 || f.PixelsY is < 1 or > 128)) return $"{f.Name}: screen pixels";
            if (Bad(f.Home) || Bad(f.Up)) return $"{f.Name}: home or yoke axis not a number";
            if (f.Screen != null && (Bad(f.Screen) || f.Screen.Any(v => v is < 0 or > 200))) return $"{f.Name}: screen size (0..200 m)";
            if (f.Uuid != null && (f.Uuid.Length > 64 || !Guid.TryParse(f.Uuid, out var g) || g == Guid.Empty)) return $"{f.Name}: uuid";
            if (f.FixtureId < 0) return $"{f.Name}: fixture ID";
            if (!float.IsFinite(f.FocusPan) || MathF.Abs(f.FocusPan) > 360 || !float.IsFinite(f.FocusTilt) || MathF.Abs(f.FocusTilt) > 180 || !float.IsFinite(f.FocusDistance) || f.FocusDistance is < 0 or > 1000)
                return $"{f.Name}: focus (pan within 360 degrees, tilt 180, 0..1000 m)";
            if (f.Breaks is { } breaks)
            {
                if (breaks.Count > MaxBreaks) return $"{f.Name}: {breaks.Count} DMX breaks (0..{MaxBreaks})";
                foreach (var b in breaks)
                {
                    if (b is not { Length: 4 } || b[0] is < 2 or > 64 || b[1] < 0 || b[1] >= Types.Count) return $"{f.Name}: DMX break";
                    if (b[2] < 0 || b[2] >= MaxUniverses || b[3] < 0 || b[3] + Types[b[1]].Channels.Length > DmxPatch.Channels)
                        return $"{f.Name}: break {b[0]} address does not fit its universe";
                }
                patched += breaks.Count;
            }
        }
        if (patched > MaxFixtures) return $"{patched} fixtures with their DMX breaks (1..{MaxFixtures})";
        return CheckObjects();
    }
}
