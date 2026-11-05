using System.Globalization;
using System.Text.Json.Nodes;

namespace UnitSport.Core;

// Plain C# (Godot's maths only): linked into the unit tests (docs/notes/core/saves-in-earth-positions.md).

/// <summary>
/// The in-place migrations of the saves that held LV95 positions and grid-north headings (#1503, G2 A2b), one per
/// store, on the file's JSON before the store reads it: each returns whether it changed anything, and the store then
/// writes the file back in the new form (no backup, the G2 plan's decision). Each is idempotent: a file at
/// <see cref="Current"/> goes through untouched. The stores whose shapes live in plain C# migrate in their own file
/// (<c>SleeperBook</c>, the adventure's <c>PinBook</c> and <c>TravelBook</c>, <c>AwakeSlots</c>, <c>MovieFile</c>,
/// <c>GhostTrack</c>); these are the ones read by a Godot node.
/// </summary>
public static class EarthSaves
{
    /// <summary>The version of every store migrated here: 1 (or none) is LV95 and grid north, 2 Earth places and true north.</summary>
    public const int Current = 2;

    /// <summary>Whether <paramref name="root"/> is already at <see cref="Current"/>; if not, stamps it (the caller converts the rest).</summary>
    private static bool Stamp(JsonObject root)
    {
        if (SavedPlace.VersionOf(root) >= Current) return false;
        root[SavedPlace.Version] = Current;
        return true;
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? list) =>
        list is JsonArray a ? a.OfType<JsonObject>() : Enumerable.Empty<JsonObject>();

    /// <summary><c>user://structures/*.json</c>: each structure's corner (<c>E</c>, <c>N</c>, <c>Altitude</c>) and <c>Yaw</c>.</summary>
    public static bool Structures(JsonObject root)
    {
        if (!Stamp(root)) return false;
        foreach (var s in Objects(root["Structures"])) SavedPlace.ToEarth(s, "E", "N", "Altitude", "Yaw");
        return true;
    }

    /// <summary>
    /// <c>user://placed/*.json</c>: each object's <c>E</c>, <c>N</c>, <c>Altitude</c>, its <c>Rotation</c> (grid axes) as
    /// the <c>turn</c> in the local east-up-south, and a zipline's payload (its high end) as ECEF (<see cref="ZipToEarth"/>).
    /// </summary>
    public static bool Placed(JsonObject root)
    {
        if (!Stamp(root)) return false;
        foreach (var o in Objects(root["Objects"]))
        {
            if (!SavedPlace.ToEarth(o, "E", "N", "Altitude")) continue;
            var at = SavedPlace.Of(o[SavedPlace.At]!);
            var rot = o["Rotation"] is JsonArray { Count: 4 } r
                ? new Godot.Quaternion((float)SavedPlace.Number(r[0]), (float)SavedPlace.Number(r[1]), (float)SavedPlace.Number(r[2]), (float)SavedPlace.Number(r[3], 1))
                : Godot.Quaternion.Identity;
            o.Remove("Rotation");
            o[SavedPlace.Turn] = SavedPlace.TurnNode(at, rot);
            if (o["Kind"] is JsonValue k && k.TryGetValue(out string? kind) && kind == "Zipline"
                && o["Payload"] is JsonValue p && p.TryGetValue(out string? payload))
                o["Payload"] = ZipToEarth(payload);
        }
        return true;
    }

    /// <summary>A photo's sidecar (<c>&lt;id&gt;.json</c> by its print): <c>E</c>, <c>N</c>, <c>Altitude</c>.</summary>
    public static bool Photo(JsonObject root)
    {
        if (!Stamp(root)) return false;
        SavedPlace.ToEarth(root, "E", "N", "Altitude");
        return true;
    }

    /// <summary>
    /// <c>user://settings.json</c> (camelCase): <c>lastLandingE</c>/<c>N</c> and <c>hostSpawnE</c>/<c>N</c> as
    /// <c>lastLanding</c> and <c>hostSpawn</c>, on the ground (altitude 0); a zero pair meant "none" and is dropped.
    /// </summary>
    public static bool Settings(JsonObject root)
    {
        // case-insensitive, as the settings are read
        string? Key(string name) => root.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (Key(SavedPlace.Version) is { } vk && root[vk] is JsonValue v && v.TryGetValue(out int version) && version >= Current) return false;
        foreach (var name in new[] { "lastLanding", "hostSpawn" })
        {
            string? ke = Key(name + "E"), kn = Key(name + "N");
            double e = ke != null ? SavedPlace.Number(root[ke]) : 0, n = kn != null ? SavedPlace.Number(root[kn]) : 0;
            if (ke != null) root.Remove(ke);
            if (kn != null) root.Remove(kn);
            if (e != 0 || n != 0) root[name] = SavedPlace.Node(GlobalPos.FromLv95(e, n, 0));
        }
        if (Key(SavedPlace.Version) is { } old) root.Remove(old);
        root[SavedPlace.Version] = Current;
        return true;
    }

    // ---- a zipline's payload --------------------------------------------------------------------------------------

    /// <summary>
    /// A zipline's payload as a record keeps it in memory (its high end: LV95 and the altitude, <c>e;n;alt</c>) as it
    /// is saved and sent (#1503): the same place in ECEF millimetres, <c>x;y;z</c>. Other text comes back unchanged.
    /// </summary>
    public static string ZipToEarth(string lv95)
    {
        if (Triple(lv95) is not { } s) return lv95;
        var (x, y, z) = GlobalPos.FromLv95(s.A, s.B, s.C).ToWire();
        return string.Create(CultureInfo.InvariantCulture, $"{x:F3};{y:F3};{z:F3}");
    }

    /// <summary>The inverse of <see cref="ZipToEarth"/>: the in-memory payload (<c>e;n;alt</c>, cm) of a saved or sent one.</summary>
    public static string ZipFromEarth(string ecef)
    {
        if (Triple(ecef) is not { } s) return ecef;
        var p = GlobalPos.FromWire(s.A, s.B, s.C).Lv95;
        return string.Create(CultureInfo.InvariantCulture, $"{p.E:F2};{p.N:F2};{p.H:F2}");
    }

    private static (double A, double B, double C)? Triple(string payload)
    {
        var p = payload.Split(';');
        return p.Length == 3
               && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double a)
               && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double b)
               && double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double c)
               && double.IsFinite(a) && double.IsFinite(b) && double.IsFinite(c)
            ? (a, b, c) : null;
    }
}
