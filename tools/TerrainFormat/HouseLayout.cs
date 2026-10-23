using System.Text.Json;
using System.Text.Json.Serialization;

namespace UnitSport.Terrain.Format;

/// <summary>
/// A house's interior drawn by a player on the web (#1186), approved by a moderator, then used by the
/// game instead of the generated interior of the building with that GWR <see cref="Egid"/>.
///
/// <para>
/// It follows the game's own interior model (<c>src/Interiors/InteriorLayout.cs</c>): per storey,
/// axis-aligned rooms whose edges are the walls, openings cut in those edges, straight stairs. Its frame
/// is the building's <see cref="BuildingOutline"/>: x along the long axis, z across, metres, origin at
/// <see cref="E"/>/<see cref="N"/> (LV95), turned by <see cref="YawDeg"/>.
/// </para>
/// </summary>
public sealed class HouseLayout
{
    public const int CurrentVersion = 1;

    /// <summary>Room types a house may use, by the game's <c>RoomType</c> names.</summary>
    public static readonly string[] RoomTypes =
    [
        "Hall", "Living", "Kitchen", "Dining", "Bedroom", "Bathroom", "WC", "Office", "Storage", "Garage",
        "Laundry", "GuestRoom", "HomeCinema", "Carnotzet", "MusicRoom", "Shelter", "Cellar", "Playroom",
        "Study", "Pantry", "Workshop", "Corridor",
    ];

    public static readonly string[] OpeningKinds = ["door", "window", "arch"];
    public static readonly string[] StairDirections = ["+x", "-x", "+z", "-z"];

    public const int MinLevel = -3, MaxLevel = 20, MaxRooms = 60, MaxOpenings = 200;
    public const double MaxCoord = 150, MinRoomSide = 0.8, FootprintSlack = 0.3;

    public int Version { get; set; } = CurrentVersion;
    public uint Egid { get; set; }
    public double E { get; set; }
    public double N { get; set; }
    public double YawDeg { get; set; }
    public double StoreyHeight { get; set; } = 2.7;
    public List<HouseFloor> Floors { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.Strict,
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static HouseLayout? FromJson(string json) => JsonSerializer.Deserialize<HouseLayout>(json, Json);

    /// <summary>
    /// Every problem with this layout, empty when it is fine. <paramref name="footprint"/> is the
    /// building's outline in this layout's frame (<see cref="BuildingOutline.LocalRings"/>); null skips
    /// the inside-the-footprint test.
    /// </summary>
    public List<string> Validate(IReadOnlyList<(double X, double Z)[]>? footprint)
    {
        var err = new List<string>();
        if (Version != CurrentVersion) err.Add($"unknown version {Version}");
        if (Egid == 0) err.Add("no building (EGID)");
        if (!(StoreyHeight >= 2.2 && StoreyHeight <= 4.5)) err.Add("storey height must be 2.2-4.5 m");
        if (Floors.Count == 0) err.Add("no floor");
        if (Floors.Count > MaxLevel - MinLevel + 1) err.Add("too many floors");
        if (Floors.Select(f => f.Level).Distinct().Count() != Floors.Count) err.Add("two floors share a level");
        if (!Floors.Any(f => f.Level == 0)) err.Add("no ground floor (level 0)");

        foreach (var f in Floors)
        {
            string at = $"floor {f.Level}";
            if (f.Level < MinLevel || f.Level > MaxLevel) err.Add($"{at}: level out of range");
            if (f.Rooms.Count == 0) err.Add($"{at}: no room");
            if (f.Rooms.Count > MaxRooms) err.Add($"{at}: more than {MaxRooms} rooms");
            if (f.Openings.Count > MaxOpenings) err.Add($"{at}: more than {MaxOpenings} openings");

            for (int i = 0; i < f.Rooms.Count; i++)
            {
                var r = f.Rooms[i];
                string rn = $"{at}, room {i + 1} ({r.Type})";
                if (!RoomTypes.Contains(r.Type)) err.Add($"{rn}: unknown type");
                if (!Coords(r.X0, r.Z0, r.X1, r.Z1)) { err.Add($"{rn}: bad coordinates"); continue; }
                if (r.X1 - r.X0 < MinRoomSide || r.Z1 - r.Z0 < MinRoomSide) err.Add($"{rn}: narrower than {MinRoomSide} m");
                for (int j = 0; j < i; j++)
                    if (Overlap(r.X0, r.Z0, r.X1, r.Z1, f.Rooms[j].X0, f.Rooms[j].Z0, f.Rooms[j].X1, f.Rooms[j].Z1))
                        err.Add($"{rn}: overlaps room {j + 1}");
                if (footprint != null && !(InsideOutline(footprint, r.X0, r.Z0) && InsideOutline(footprint, r.X1, r.Z0)
                                           && InsideOutline(footprint, r.X1, r.Z1) && InsideOutline(footprint, r.X0, r.Z1)))
                    err.Add($"{rn}: outside the building");
            }

            for (int i = 0; i < f.Openings.Count; i++)
            {
                var o = f.Openings[i];
                string on = $"{at}, {o.Kind} {i + 1}";
                if (!OpeningKinds.Contains(o.Kind)) { err.Add($"{on}: unknown kind"); continue; }
                if (!double.IsFinite(o.X0) || !double.IsFinite(o.Z0) || !double.IsFinite(o.X1) || !double.IsFinite(o.Z1)
                    || Math.Abs(o.X0) > MaxCoord || Math.Abs(o.X1) > MaxCoord || Math.Abs(o.Z0) > MaxCoord || Math.Abs(o.Z1) > MaxCoord)
                { err.Add($"{on}: bad coordinates"); continue; }
                bool alongX = Math.Abs(o.Z1 - o.Z0) < 0.01, alongZ = Math.Abs(o.X1 - o.X0) < 0.01;
                if (!alongX && !alongZ) { err.Add($"{on}: not straight along a wall"); continue; }
                double len = alongX ? Math.Abs(o.X1 - o.X0) : Math.Abs(o.Z1 - o.Z0);
                double max = o.Kind == "door" ? 3 : 5;
                if (len < 0.5 || len > max) err.Add($"{on}: width must be 0.5-{max} m");
                if (!f.Rooms.Any(r => OnEdge(r, o))) err.Add($"{on}: not on a room's wall");
            }

            var above = Floors.FirstOrDefault(g => g.Level == f.Level + 1);
            for (int i = 0; i < f.Stairs.Count; i++)
            {
                var s = f.Stairs[i];
                string sn = $"{at}, stair {i + 1}";
                if (!StairDirections.Contains(s.Up)) err.Add($"{sn}: unknown direction");
                if (!Coords(s.X0, s.Z0, s.X1, s.Z1)) { err.Add($"{sn}: bad coordinates"); continue; }
                bool runX = s.Up is "+x" or "-x";
                double lane = runX ? s.Z1 - s.Z0 : s.X1 - s.X0, run = runX ? s.X1 - s.X0 : s.Z1 - s.Z0;
                if (lane < 0.7 || lane > 2.5) err.Add($"{sn}: width must be 0.7-2.5 m");
                if (run < 1.5 || run > 8) err.Add($"{sn}: length must be 1.5-8 m");
                if (above == null) err.Add($"{sn}: no floor above to climb to");
                if (!f.Rooms.Any(r => s.X0 >= r.X0 - 0.01 && s.X1 <= r.X1 + 0.01 && s.Z0 >= r.Z0 - 0.01 && s.Z1 <= r.Z1 + 0.01))
                    err.Add($"{sn}: not inside one room");
            }
        }
        if (err.Count == 0) Reach(err);
        return err;
    }

    /// <summary>
    /// Every room can be walked to from the street, as the game's interior validator demands: through a
    /// door on an outer wall of the ground floor, then doors, arches and stairs.
    /// </summary>
    private void Reach(List<string> err)
    {
        var reached = new HashSet<(int Level, int Room)>();
        var queue = new Queue<(int, int)>();
        void Add(int level, int room) { if (room >= 0 && reached.Add((level, room))) queue.Enqueue((level, room)); }

        var links = new Dictionary<(int, int), List<(int, int)>>();
        void Link((int, int) a, (int, int) b)
        {
            (links.TryGetValue(a, out var la) ? la : links[a] = new()).Add(b);
            (links.TryGetValue(b, out var lb) ? lb : links[b] = new()).Add(a);
        }
        bool front = false;
        foreach (var f in Floors)
        {
            foreach (var o in f.Openings.Where(o => o.Kind is "door" or "arch"))
            {
                var on = Enumerable.Range(0, f.Rooms.Count).Where(i => OnEdge(f.Rooms[i], o)).ToList();
                if (on.Count >= 2) Link((f.Level, on[0]), (f.Level, on[1]));
                else if (on.Count == 1 && o.Kind == "door" && f.Level == 0) { front = true; Add(0, on[0]); }
            }
            var above = Floors.FirstOrDefault(g => g.Level == f.Level + 1);
            for (int i = 0; i < f.Stairs.Count; i++)
            {
                var s = f.Stairs[i];
                var (tx, tz) = s.TopEnd();
                int from = RoomAt(f, (s.X0 + s.X1) / 2, (s.Z0 + s.Z1) / 2), to = above == null ? -1 : RoomAt(above, tx, tz);
                if (to < 0) err.Add($"floor {f.Level}, stair {i + 1}: arrives outside any room of floor {f.Level + 1}");
                else if (from >= 0) Link((f.Level, from), (f.Level + 1, to));
            }
        }
        if (!front) { err.Add("no front door: put a door on an outer wall of the ground floor"); return; }
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (links.TryGetValue(at, out var next)) foreach (var n in next) Add(n.Item1, n.Item2);
        }
        foreach (var f in Floors)
            for (int i = 0; i < f.Rooms.Count; i++)
                if (!reached.Contains((f.Level, i)))
                    err.Add($"floor {f.Level}, room {i + 1} ({f.Rooms[i].Type}): no way in, add a door or an arch");
    }

    /// <summary>The room of the floor holding the point, -1 for none.</summary>
    public static int RoomAt(HouseFloor f, double x, double z)
    {
        for (int i = 0; i < f.Rooms.Count; i++)
        {
            var r = f.Rooms[i];
            if (x >= r.X0 - 0.01 && x <= r.X1 + 0.01 && z >= r.Z0 - 0.01 && z <= r.Z1 + 0.01) return i;
        }
        return -1;
    }

    /// <summary>A short, stable fingerprint of the layout: a stored interior planned from another one is stale.</summary>
    public string Hash() =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(ToJson())))[..12];

    private static bool Coords(double x0, double z0, double x1, double z1) =>
        double.IsFinite(x0) && double.IsFinite(z0) && double.IsFinite(x1) && double.IsFinite(z1)
        && x0 < x1 && z0 < z1 && Math.Abs(x0) <= MaxCoord && Math.Abs(x1) <= MaxCoord
        && Math.Abs(z0) <= MaxCoord && Math.Abs(z1) <= MaxCoord;

    private static bool Overlap(double ax0, double az0, double ax1, double az1, double bx0, double bz0, double bx1, double bz1) =>
        ax0 < bx1 - 0.01 && bx0 < ax1 - 0.01 && az0 < bz1 - 0.01 && bz0 < az1 - 0.01;

    /// <summary>Whether the opening lies wholly on one edge of the room.</summary>
    public static bool OnEdge(HouseRoom r, HouseOpening o)
    {
        const double eps = 0.02;
        double lo, hi;
        if (Math.Abs(o.Z1 - o.Z0) < 0.01) // along x: on the room's z0 or z1 edge
        {
            if (Math.Abs(o.Z0 - r.Z0) > eps && Math.Abs(o.Z0 - r.Z1) > eps) return false;
            lo = Math.Min(o.X0, o.X1); hi = Math.Max(o.X0, o.X1);
            return lo >= r.X0 - eps && hi <= r.X1 + eps;
        }
        if (Math.Abs(o.X0 - r.X0) > eps && Math.Abs(o.X0 - r.X1) > eps) return false;
        lo = Math.Min(o.Z0, o.Z1); hi = Math.Max(o.Z0, o.Z1);
        return lo >= r.Z0 - eps && hi <= r.Z1 + eps;
    }

    /// <summary>Inside one of the rings (even-odd), or within <see cref="FootprintSlack"/> of an edge.</summary>
    public static bool InsideOutline(IReadOnlyList<(double X, double Z)[]> rings, double x, double z)
    {
        bool inside = false;
        foreach (var r in rings)
            for (int i = 0, j = r.Length - 1; i < r.Length; j = i++)
            {
                var a = r[i];
                var b = r[j];
                if ((a.Z > z) != (b.Z > z) && x < (b.X - a.X) * (z - a.Z) / (b.Z - a.Z) + a.X) inside = !inside;
                if (SegDist(x, z, a, b) <= FootprintSlack) return true;
            }
        return inside;
    }

    private static double SegDist(double x, double z, (double X, double Z) a, (double X, double Z) b)
    {
        double dx = b.X - a.X, dz = b.Z - a.Z, l2 = dx * dx + dz * dz;
        double t = l2 == 0 ? 0 : Math.Clamp(((x - a.X) * dx + (z - a.Z) * dz) / l2, 0, 1);
        double px = a.X + t * dx - x, pz = a.Z + t * dz - z;
        return Math.Sqrt(px * px + pz * pz);
    }
}

public sealed class HouseFloor
{
    /// <summary>0 the ground floor, 1 the one above, -1 the first cellar.</summary>
    public int Level { get; set; }
    public List<HouseRoom> Rooms { get; set; } = new();
    public List<HouseOpening> Openings { get; set; } = new();
    public List<HouseStair> Stairs { get; set; } = new();
}

public sealed class HouseRoom
{
    public double X0 { get; set; }
    public double Z0 { get; set; }
    public double X1 { get; set; }
    public double Z1 { get; set; }
    public string Type { get; set; } = "Living";
}

/// <summary>A door, window or open arch: a segment on a room's edge, along x or along z.</summary>
public sealed class HouseOpening
{
    public string Kind { get; set; } = "door";
    public double X0 { get; set; }
    public double Z0 { get; set; }
    public double X1 { get; set; }
    public double Z1 { get; set; }
}

/// <summary>A straight stair to the next floor up: its rectangle and the way it climbs.</summary>
public sealed class HouseStair
{
    public double X0 { get; set; }
    public double Z0 { get; set; }
    public double X1 { get; set; }
    public double Z1 { get; set; }
    public string Up { get; set; } = "+z";

    /// <summary>Where it arrives on the floor above: the middle of its top end, a little inside.</summary>
    public (double X, double Z) TopEnd()
    {
        double cx = (X0 + X1) / 2, cz = (Z0 + Z1) / 2;
        return Up switch
        {
            "+x" => (X1 - 0.1, cz),
            "-x" => (X0 + 0.1, cz),
            "-z" => (cx, Z0 + 0.1),
            _ => (cx, Z1 - 0.1),
        };
    }
}

/// <summary>
/// The approved layouts the game uses (#1186): <c>house-layouts.json</c>, written by the layout service on every
/// approval or withdrawal, read by whoever plans interiors (the server; an offline game too). Re-read when the file
/// changes, checked at most every few seconds, so an approval reaches the buildings planned from then on.
/// </summary>
public static class HouseLayouts
{
    private static readonly object Gate = new();
    private static Dictionary<uint, (HouseLayout Layout, string Hash)> _byEgid = new();
    private static string? _path;
    private static DateTime _stamp;
    private static long _checked = long.MinValue;

    /// <summary>Problems worth a line in the log (a bad file); the game points it at its own logger.</summary>
    public static Action<string>? Warn { get; set; }

    /// <summary>The file to read; null for none.</summary>
    public static string? Path
    {
        get => _path;
        set { lock (Gate) { _path = value; _stamp = default; _checked = long.MinValue; _byEgid = new(); } }
    }

    /// <summary>The approved layout of a building, or null.</summary>
    public static HouseLayout? For(uint egid)
    {
        if (egid == 0) return null;
        lock (Gate) { Refresh(); return _byEgid.TryGetValue(egid, out var e) ? e.Layout : null; }
    }

    /// <summary>The fingerprint of a building's approved layout, "" when it has none.</summary>
    public static string HashFor(uint egid)
    {
        if (egid == 0) return "";
        lock (Gate) { Refresh(); return _byEgid.TryGetValue(egid, out var e) ? e.Hash : ""; }
    }

    /// <summary>Uses these layouts instead of a file (checks and tests).</summary>
    public static void Set(IEnumerable<HouseLayout> layouts)
    {
        lock (Gate)
        {
            _path = null;
            _byEgid = layouts.ToDictionary(l => l.Egid, l => (l, l.Hash()));
        }
    }

    /// <summary>The layouts of an export file's text, by EGID; a layout that breaks the rules is left out.</summary>
    public static Dictionary<uint, HouseLayout> Parse(string json, Action<string>? warn = null)
    {
        var all = new Dictionary<uint, HouseLayout>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("layouts", out var list)) return all;
        foreach (var e in list.EnumerateArray())
        {
            var l = e.Deserialize<HouseLayout>(HouseLayout.Json);
            if (l == null) continue;
            var problems = l.Validate(null);
            if (problems.Count > 0) { warn?.Invoke($"house layout {l.Egid}: {problems[0]}"); continue; }
            all[l.Egid] = l;
        }
        return all;
    }

    private static void Refresh()
    {
        if (_path == null || Environment.TickCount64 - _checked < 5000) return;
        _checked = Environment.TickCount64;
        try
        {
            var stamp = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : default;
            if (stamp == _stamp) return;
            _stamp = stamp;
            _byEgid = stamp == default ? new()
                : Parse(File.ReadAllText(_path), Warn).ToDictionary(kv => kv.Key, kv => (kv.Value, kv.Value.Hash()));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Warn?.Invoke($"house layouts {_path}: {ex.Message}");
        }
    }
}
