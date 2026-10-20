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
    }

    [JsonPropertyName("v")] public int Ver { get; set; } = Version;
    /// <summary>Where it came from (the file's name), for the chat and the desk.</summary>
    [JsonPropertyName("src")] public string Source { get; set; } = "";
    [JsonPropertyName("types")] public List<TypeRow> Types { get; set; } = new();
    [JsonPropertyName("fx")] public List<FixtureRow> Fixtures { get; set; } = new();

    public static float[] V(Vector3 v) => new[] { v.X, v.Y, v.Z };
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
            var f = patch.AddAt(types[r.Type], (RigGroup)r.Group, V(r.Pos), V(r.Home), V(r.Up), r.Universe, r.Address);
            if (r.Screen != null)
                patch.Fixtures[^1] = f with { ScreenSize = V(r.Screen), PixelsX = r.PixelsX, PixelsY = r.PixelsY };
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
        }
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
        }
        return CheckObjects();
    }
}
