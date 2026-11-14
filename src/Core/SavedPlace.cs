using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace UnitSport.Core;

// Plain C# (Godot's maths only, through GlobalPos): linked into the unit tests (docs/notes/core/saves-in-earth-positions.md).

/// <summary>
/// A <see cref="GlobalPos"/> in every JSON save (#1503, G2 A2b): <c>{"x": …, "y": …, "z": …}</c>, ECEF metres (WGS84),
/// through <see cref="GlobalPos.ToWire"/> and <see cref="GlobalPos.FromWire"/>, so the file means the same place
/// whatever <c>GlobalPos</c> holds inside: since G2 A1's last step (#1502) a plain copy of its ECEF doubles.
/// Put on the type: any record holding a <c>GlobalPos</c> property saves it this way.
/// </summary>
public sealed class GlobalPosJson : JsonConverter<GlobalPos>
{
    public override GlobalPos Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions options)
    {
        if (r.TokenType != JsonTokenType.StartObject) throw new JsonException("a place is {\"x\", \"y\", \"z\"}");
        double x = double.NaN, y = double.NaN, z = double.NaN;
        var body = BodyId.Earth;
        while (r.Read() && r.TokenType != JsonTokenType.EndObject)
        {
            if (r.TokenType != JsonTokenType.PropertyName) throw new JsonException("a place is {\"x\", \"y\", \"z\"}");
            string? name = r.GetString();
            r.Read();
            switch (name)
            {
                case "x": x = r.GetDouble(); break;
                case "y": y = r.GetDouble(); break;
                case "z": z = r.GetDouble(); break;
                case "b": body = (BodyId)r.GetByte(); break;
                default: r.Skip(); break;
            }
        }
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) throw new JsonException("a place needs x, y and z");
        return GlobalPos.On(body, new UnitSport.Terrain.Format.EcefPos(x, y, z));
    }

    public override void Write(Utf8JsonWriter w, GlobalPos p, JsonSerializerOptions options)
    {
        var (x, y, z) = p.ToWire();
        w.WriteStartObject();
        w.WriteNumber("x", x);
        w.WriteNumber("y", y);
        w.WriteNumber("z", z);
        // the body only off the Earth (#1847): every save of today stays byte for byte
        if (p.Body != BodyId.Earth) w.WriteNumber("b", (byte)p.Body);
        w.WriteEndObject();
    }
}

/// <summary>
/// The saves' shared shapes for a place and a heading (#1503, G2 A2b): a place is <see cref="GlobalPosJson"/>'s
/// <c>{x, y, z}</c> under <see cref="At"/>, a heading radians from true north at the place (Godot's sense: 0 faces
/// north, positive turns left) under <see cref="YawTrue"/>, a rotation the quaternion of the turn in the local
/// east-up-south there under <see cref="Turn"/>. Every store that had LV95 fields carries <see cref="Version"/>;
/// its migration (<see cref="ToEarth"/>, the stores' own steps in <see cref="EarthSaves"/>) is the only reader of
/// the old fields. In memory the records keep their <c>GlobalPos</c> and grid headings: they convert here, at the file.
/// </summary>
public static class SavedPlace
{
    public const string At = "at", YawTrue = "yawTrue", Turn = "turn", Version = "version";

    private static readonly JsonSerializerOptions Plain = new();

    /// <summary>A place as its save node.</summary>
    public static JsonNode Node(GlobalPos p) => JsonSerializer.SerializeToNode(p, Plain)!;

    /// <summary>The place a save node holds (throws <see cref="JsonException"/> on one that is not).</summary>
    public static GlobalPos Of(JsonNode node) => node.Deserialize<GlobalPos>(Plain);

    /// <summary>A number of a node, or <paramref name="fallback"/> when absent or not a number.</summary>
    public static double Number(JsonNode? node, double fallback = 0) =>
        node is JsonValue v && v.TryGetValue(out double d) ? d
        : node is JsonValue s && s.TryGetValue(out string? text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? d
        : fallback;

    /// <summary>The version a save's object says (<see cref="Version"/>), 1 when it has none: every save before A2b.</summary>
    public static int VersionOf(JsonObject o) => o[Version] is JsonValue v && v.TryGetValue(out int n) ? n : 1;

    /// <summary>
    /// One object's LV95 fields (<paramref name="e"/>, <paramref name="n"/>, the LHN95 <paramref name="alt"/>, 0 when
    /// absent or not named) replaced by its Earth place under <paramref name="at"/>, and its grid-north heading
    /// <paramref name="yaw"/>, when named, by a heading from true north there under <paramref name="yawTrue"/>. False
    /// (nothing changed) for an object without <paramref name="e"/> and <paramref name="n"/>.
    /// </summary>
    public static bool ToEarth(JsonObject o, string e, string n, string? alt = null, string? yaw = null, string at = At, string yawTrue = YawTrue)
    {
        if (o[e] is not JsonValue || o[n] is not JsonValue) return false;
        var g = GlobalPos.FromLv95(Number(o[e]), Number(o[n]), alt != null ? Number(o[alt]) : 0);
        o.Remove(e);
        o.Remove(n);
        if (alt != null) o.Remove(alt);
        o[at] = Node(g);
        if (yaw != null)
        {
            if (o[yaw] is JsonValue) o[yawTrue] = Lv95Grid.TrueYaw(g, (float)Number(o[yaw]));
            o.Remove(yaw);
        }
        return true;
    }

    /// <summary>A grid quaternion <c>[x, y, z, w]</c> (the LV95 grid's axes at <paramref name="at"/>) as the turn in the local east-up-south there.</summary>
    public static JsonArray TurnNode(GlobalPos at, Godot.Quaternion grid)
    {
        var q = Lv95Grid.Axes(at).ToEnu(grid);
        return new JsonArray(q.X, q.Y, q.Z, q.W);
    }

    /// <summary>A turn saved by <see cref="TurnNode"/> back in the LV95 grid's axes at <paramref name="at"/>; null for one that is not four numbers.</summary>
    public static Godot.Quaternion? GridTurn(GlobalPos at, float[]? turn) =>
        turn is { Length: 4 } t && t.All(float.IsFinite) ? Lv95Grid.Axes(at).FromEnu(new Godot.Quaternion(t[0], t[1], t[2], t[3])) : null;

    // ---- a list of places (the Versteckis and Battle Royale histories) ------------------------------------------

    /// <summary>
    /// A list of places as saved: <c>{"version": 2, "places": [{x, y, z}, …]}</c>; before A2b it was a bare array of
    /// LV95 <c>[e, n]</c> pairs, which <see cref="ReadPlaces"/> still reads (<paramref name="old"/> then true: write it back).
    /// </summary>
    public static JsonObject PlacesNode(IEnumerable<GlobalPos> places)
    {
        var list = new JsonArray();
        foreach (var p in places) list.Add(Node(p));
        return new JsonObject { [Version] = 2, ["places"] = list };
    }

    /// <inheritdoc cref="PlacesNode"/>
    public static List<GlobalPos> ReadPlaces(string json, out bool old)
    {
        var root = JsonNode.Parse(json);
        var list = new List<GlobalPos>();
        old = root is JsonArray;
        if (root is JsonArray pairs)
        {
            foreach (var pair in pairs)
                if (pair is JsonArray { Count: 2 } en)
                    list.Add(GlobalPos.FromLv95(Number(en[0], double.NaN), Number(en[1], double.NaN), 0));
            list.RemoveAll(p => !p.IsFinite);
        }
        else if (root is JsonObject o && o["places"] is JsonArray places)
            foreach (var node in places)
                if (node != null) list.Add(Of(node));
        return list;
    }
}
