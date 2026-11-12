using Godot;
using UnitSport.Core;

namespace UnitSport.Terrain.Earth;

/// <summary>
/// <c>--dresscheck E,N</c> (#1704, <c>tools/earthdresscheck.sh</c>): the dressing services ported to the ground by #1694, on
/// an earth world's real leaves (B4's region on D:), on the server and on a client alike. Round a stop it counts what
/// each service has placed (parked fleets, airport stands, herds, birds, the Nyon steamer) and checks that what stands
/// on the ground is on it: a parked vehicle's height against the leaves' within <see cref="MaxOffM"/>. A stand-in's
/// records placed by LV95 maths land ~100,000 km away and count as none. <c>--dressexpect a,b</c>: the services that
/// must have something there (<c>fleet</c>, <c>stands</c>, <c>herds</c>, <c>birds</c>, <c>steamer</c>).
/// </summary>
public partial class DressCheck : Node
{
    private const double TimeoutS = 240, Reach = 600, HerdReach = 2500;
    /// <summary>How long the services get before the first count; <c>--dresssettle S</c> for a stop whose yards fill late.</summary>
    private static readonly double SettleS = double.TryParse(CmdArgs.Value("--dresssettle"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double s) ? s : 25;
    /// <summary>A slot this far off what stands under it is off; more than <see cref="MaxOffShare"/> of them, or one past <see cref="WorstOffM"/>, fails.</summary>
    private const float MaxOffM = 1.5f, WorstOffM = 6f;
    private const double MaxOffShare = 0.02;
    private int _checked, _off;
    /// <summary>How far a steamer's origin may sit from its still water level (its draught and more).</summary>
    private const float SteamerSlack = 4f;

    public static (double E, double N)? Stop => SpawnPoint.ParseLv95("--dresscheck");
    /// <summary><c>--dressverbose</c>: every pallet round the stop, by id, LV95 place and height off what is under it.</summary>
    private static readonly bool Verbose = CmdArgs.Has("--dressverbose");

    private readonly EarthWorld _earth;
    private readonly bool _server;
    private readonly GlobalPos _stop;
    private readonly HashSet<string> _expect;
    private double _since, _poll;
    private bool _done;

    public DressCheck(EarthWorld earth, bool server, double e, double n)
    {
        Name = "DressCheck";
        (_earth, _server) = (earth, server);
        _stop = GlobalPos.FromLv95(e, n, 0);
        _expect = new HashSet<string>((CmdArgs.Value("--dressexpect") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
    }

    private string Log => $"[dresscheck {(_server ? "server" : "client")}]";

    public override void _Process(double delta)
    {
        if (_done) return;
        _since += delta;
        if ((_poll += delta) < 1.0 || _since < SettleS) return;
        _poll = 0;
        _checked = _off = 0;
        var found = Count(out float worst, out string worstWhat);
        bool all = Expected().All(k => found.TryGetValue(k, out int c) && c > 0);
        if (!all && _since < Math.Max(TimeoutS, SettleS + 60)) return;
        _done = true;
        foreach (var (k, c) in found) GD.Print($"{Log} {k}: {c}");
        GD.Print(FormattableString.Invariant($"{Log} worst height off the ground: {worst:F2} m ({worstWhat}), {_off} of {_checked} slots over {MaxOffM:F1} m, after {_since:F0} s; {_earth.Status}"));
        var missing = Expected().Where(k => !found.TryGetValue(k, out int c) || c == 0).ToList();
        if (missing.Count > 0) GD.Print($"{Log} RESULT: FAILED (nothing from {string.Join(", ", missing)} within reach)");
        else if (worst > WorstOffM || _off > MaxOffShare * Math.Max(1, _checked))
            GD.Print(FormattableString.Invariant($"{Log} RESULT: FAILED ({_off} of {_checked} slots over {MaxOffM:F1} m off the ground, the worst {worstWhat} at {worst:F2} m)"));
        else GD.Print($"{Log} RESULT: ok");
    }

    /// <summary>The services expected here: <c>name</c> on both sides, <c>name@server</c> / <c>name@client</c> on one (the airport's stands are filled on the server).</summary>
    private IEnumerable<string> Expected()
    {
        string side = _server ? "server" : "client";
        foreach (var k in _expect)
        {
            int at = k.IndexOf('@');
            if (at < 0) yield return k;
            else if (k[(at + 1)..] == side) yield return k[..at];
        }
    }

    /// <summary>How far a point is from what stands under it (the ground, a car park's deck): a ray down; null with no collision there yet.</summary>
    private float? OffWhatIsUnder(Vector3 at)
    {
        var space = GetViewport()?.World3D?.DirectSpaceState;
        if (space == null) return null;
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 1f, at + Vector3.Down * 8f);   // from 1 m up: a canopy over a parked car is not what it stands on
        // the leaves' ground and buildings only: not the parked car's own body or a passer-by
        for (int k = 0; k < 6; k++)
        {
            var hit = space.IntersectRay(q);
            if (hit.Count == 0) return null;
            var body = hit["collider"].AsGodotObject() as Node;
            if (body is StaticBody3D s && (s.IsInGroup(EarthWorld.GroundGroup) || s.GetParent() is StaticBody3D g && g.IsInGroup(EarthWorld.GroundGroup)))
                return Mathf.Abs(((Vector3)hit["position"]).Y - at.Y);
            var ex = q.Exclude;
            ex.Add((Rid)hit["rid"]);
            q.Exclude = ex;
        }
        return null;
    }

    private bool _dumped;

    /// <summary><c>--dressyard E_N_I</c> (diagnosis, #1769): one site's yard, its facade and door, and the tile's roads near its pallets.</summary>
    private void DumpYard(IChunkSource source, string building)
    {
        if (!Interiors.BuildingKey.TryParse(building, out var key)) return;
        var tile = source.LoadBuildingsAsync(key.Tile).GetAwaiter().GetResult();
        var roads = source.LoadRoadsAsync(key.Tile).GetAwaiter().GetResult();
        var grid = source.LoadChunkAsync(key.Tile).GetAwaiter().GetResult();
        if (tile == null) { GD.Print($"{Log} yard {building}: no buildings"); return; }
        var map = Interiors.BuildingTypes.For(tile);
        if (map.Boxes[key.Index] is { } box)
            GD.Print(FormattableString.Invariant($"{Log} yard {building}: box centre {box.Center} u {box.AxisU} {box.Width:F1} x {box.Depth:F1}"));
        var fp = Interiors.BuildingFootprint.Compute(tile, key.Index, roads, grid);
        if (fp != null) GD.Print(FormattableString.Invariant($"{Log} yard {building}: door at {fp.Door.Position} out {fp.Door.Outward} w {fp.Door.Width:F1}"));
        foreach (var f in Vehicles.SiteYards.Fronts(tile, roads, grid))
            if (f.Yard.Owner == building)
                GD.Print(FormattableString.Invariant($"{Log} yard {building}: centre {f.Yard.X:F1},{f.Yard.Z:F1} heading {Mathf.RadToDeg(f.Yard.Heading):F1} depth {f.Yard.Depth:F1} facade {f.From:F1}..{f.To:F1}"));
        GD.Print($"{Log} yard {building}: roads {roads?.Segments.Count ?? -1}");
        if (roads == null) return;
        foreach (var p in Vehicles.SiteYards.Pallets(tile, roads, grid))
        {
            if (p.Building != building) continue;
            var (px, pz) = key.Tile.Local(p.E, p.N);
            var at = new Vector2((float)px, (float)pz);
            float best = float.MaxValue;
            string what = "";
            foreach (var seg in roads.Segments)
                for (int k = 0; k + 1 < seg.PointCount; k++)
                {
                    var a = new Vector2(seg.Points[k * 3], seg.Points[k * 3 + 2]);
                    var c = new Vector2(seg.Points[k * 3 + 3], seg.Points[k * 3 + 5]);
                    var ac = c - a;
                    float t = ac.LengthSquared() < 1e-6f ? 0 : Mathf.Clamp((at - a).Dot(ac) / ac.LengthSquared(), 0, 1);
                    float d = (a + ac * t).DistanceTo(at) - seg.Width / 2;
                    if (d < best) (best, what) = (d, FormattableString.Invariant($"{seg.Class} w {seg.Width:F1} aerial {Terrain.Format.RoadFormat.IsAerial(seg.Class)}"));
                }
            GD.Print(FormattableString.Invariant($"{Log} yard {building}: pallet y{p.Slot} at {px:F1},{pz:F1}: {best:F1} m off the nearest road's edge ({what})"));
        }
    }

    private Dictionary<string, int> Count(out float worst, out string worstWhat)
    {
        var origin = _earth.Origin;
        var ground = _earth.Ground;
        float w = 0;
        string ww = "nothing";
        var found = new Dictionary<string, int>();

        int fleet = 0;
        if (Vehicles.DormantVehicles.Instance is { } dormant)
            foreach (var s in dormant.Slots())
            {
                var at = GlobalPos.FromLv95(s.E, s.N, s.Height);
                if (at.HorizontalDistanceTo(_stop) > Reach) continue;
                fleet++;
                var world = origin.ToWorld(at);
                if (OffWhatIsUnder(world) is not { } off) continue;
                _checked++;
                if (off > MaxOffM) _off++;
                if (off > w) (w, ww) = (off, $"slot {s.Owner}#{s.Ordinal}");
            }
        found["fleet"] = fleet;

        // the yards' and the sites' pallets (#1769), drawn on a client only: on what stands under them, and never on a road
        int pallets = 0;
        foreach (var p in Items.PalletNode.All.Values)
        {
            if (!p.IsInsideTree() || p.Taken) continue;
            var world = p.GlobalPosition;
            var lv = origin.ToLv95(world);
            if (GlobalPos.FromLv95(lv.E, lv.N, 0).HorizontalDistanceTo(_stop) > Reach) continue;
            pallets++;
            float? under = OffWhatIsUnder(world);
            if (Verbose)
            {
                // where it stands in its own tile's frame: a leaf's stand-in holds the leaf in the middle of its 1000 m square
                string local = Interiors.BuildingKey.TryParse(p.Id.Split(':')[0], out var key)
                    && origin.ToGlobal(world).InTile(key.Tile) is var (x, _, z) ? FormattableString.Invariant($" in tile {x:F1},{z:F1}") : "";
                GD.Print(FormattableString.Invariant($"{Log} pallet {p.Id} at {lv.E:F1},{lv.N:F1}{local} off {under ?? -1:F2}"));
            }
            if (under is not { } off) continue;
            _checked++;
            if (off > MaxOffM) _off++;
            if (off > w) (w, ww) = (off, $"pallet {p.Id}");
        }
        found["pallets"] = pallets;
        if (Verbose && !_dumped && pallets > 0 && CmdArgs.Value("--dressyard") is { } yardKey && ground.Content is { } content)
        {
            _dumped = true;
            System.Threading.Tasks.Task.Run(() => DumpYard(content, yardKey));
        }

        int stands = 0, filled = 0;
        if (World.AirportStands.Instance is { } airports)
            foreach (var (stand, full) in airports.Known())
            {
                stands++;
                if (full) filled++;
            }
        found["stands"] = stands;
        found["stands filled"] = filled;

        int herds = 0, cows = 0;
        if (Farming.Herds.Instance is { } h2)
            foreach (var herd in h2.All)
            {
                if (GlobalPos.FromLv95(herd.E, herd.N, 0).HorizontalDistanceTo(_stop) > HerdReach) continue;
                herds++;
                cows += herd.Count;
            }
        found["herds"] = herds;
        found["cows"] = cows;

        // the server's birds are its own headless BirdLife's (no Instance there)
        found["birds"] = (Birds.BirdLife.Instance ?? FindBirds(GetTree().Root))?.Birds.Count ?? 0;

        int steamer = 0;
        foreach (var node in GetTree().Root.FindChildren(World.SteamerBerth.BerthName, "", true, false))
            if (node is Node3D n3)
            {
                steamer++;
                // its origin rides a draught under the still level: only a hull metres off its lake is wrong
                if (ground.TryGetWaterLevel(n3.GlobalPosition, out float level) && Mathf.Abs(n3.GlobalPosition.Y - level) - SteamerSlack > w)
                    (w, ww) = (Mathf.Abs(n3.GlobalPosition.Y - level) - SteamerSlack, "the steamer off its water");
            }
        found["steamer"] = steamer;
        worst = w;
        static Birds.BirdLife? FindBirds(Node n)
        {
            if (n is Birds.BirdLife b) return b;
            foreach (var c in n.GetChildren())
                if (FindBirds(c) is { } f) return f;
            return null;
        }
        worstWhat = ww;
        return found;
    }
}
