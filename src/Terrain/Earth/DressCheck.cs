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
    private const double TimeoutS = 240, SettleS = 25, Reach = 600, HerdReach = 2500;
    private const float MaxOffM = 2.5f;
    /// <summary>How far a steamer's origin may sit from its still water level (its draught and more).</summary>
    private const float SteamerSlack = 4f;

    public static (double E, double N)? Stop => SpawnPoint.ParseLv95("--dresscheck");

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
        var found = Count(out float worst, out string worstWhat);
        bool all = Expected().All(k => found.TryGetValue(k, out int c) && c > 0);
        if (!all && _since < TimeoutS) return;
        _done = true;
        foreach (var (k, c) in found) GD.Print($"{Log} {k}: {c}");
        GD.Print(FormattableString.Invariant($"{Log} worst height off the ground: {worst:F2} m ({worstWhat}), after {_since:F0} s; {_earth.Status}"));
        var missing = Expected().Where(k => !found.TryGetValue(k, out int c) || c == 0).ToList();
        if (missing.Count > 0) GD.Print($"{Log} RESULT: FAILED (nothing from {string.Join(", ", missing)} within reach)");
        else if (worst > MaxOffM) GD.Print(FormattableString.Invariant($"{Log} RESULT: FAILED ({worstWhat} is {worst:F2} m off the ground)"));
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
        var q = PhysicsRayQueryParameters3D.Create(at + Vector3.Up * 3f, at + Vector3.Down * 8f);
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
                if (OffWhatIsUnder(world) is { } off && off > w) (w, ww) = (off, $"slot {s.Owner}#{s.Ordinal}");
            }
        found["fleet"] = fleet;

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
