using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

/// <summary>
/// <c>--pitcheck [shots] --chunks fixture:site --traffic 0</c> (#619): the pit is driven, walked and
/// dropped into, under the ground's real collision as a tile builds it (the height map with the pit's
/// quads out, the collar and the lining in the road cells, the site's piles and slab).
///
/// <list type="bullet">
/// <item><b>rays</b>: down through every metre of the pit and its rim: each meets the floor, the slab
/// or the ramp in the pit, and the ground just outside it, never nothing;</item>
/// <item><b>drops</b>: bodies let fall into the pit at its corners and middle come to rest on its floor;</item>
/// <item><b>the excavator</b> is lined up on the apron, driven over the ramp's top, down to its foot and
/// onto the middle of the slab, turned round there and driven back up and out, by pure pursuit on the
/// way in. At every waypoint it stands on what the plan says (ground, ramp, floor, slab);</item>
/// <item><b>the shell's basement</b>: a body the player's size walks the second site's way up from its
/// basement slab to its top slab, and across a footbridge from the rim onto its ground floor.</item>
/// </list>
/// Windowed with <c>shots</c>: pictures in <c>test_output/pit/</c>. Prints a RESULT line, exits 0 or 1.
/// </summary>
public partial class PitCheck : Node
{
    public static bool Requested => CmdArgs.Has("--pitcheck");
    private static bool Shots => (CmdArgs.Value("--pitcheck") ?? "").Split(',').Contains("shots");

    private readonly ChunkManager _chunks;
    private readonly WorldOrigin _origin;
    private FootPlayer? _player;
    private int _failures;
    private bool _started;
    private double _waited;
    private List<Dug>? _sites;

    public PitCheck(ChunkManager chunks, WorldOrigin origin)
    {
        _chunks = chunks;
        _origin = origin;
        Name = "PitCheck";
    }

    private void Expect(bool ok, string what)
    {
        if (!ok) _failures++;
        GD.Print($"[pitcheck] {(ok ? "ok  " : "FAIL")} {what}");
    }

    /// <summary>One site with its pit, as every peer works it out, and the way from its frame to the world.</summary>
    private sealed record Dug(ConstructionSite Site, ShellPlan Shell, PitPlan Pit, Func<Vector3, Vector3> World);

    public override void _PhysicsProcess(double delta)
    {
        if (_started) return;
        _waited += delta;
        if (_waited > 120) { Finish("no ground under the sites in 120 s"); return; }
        bool log = (int)(_waited / 5) != (int)((_waited - delta) / 5);
        var terrain = _chunks;
        if (terrain.Source is not { } source)
        {
            if (log) GD.Print("[pitcheck] waiting for the terrain's source");
            return;
        }
        // planned once, as every peer plans them, from the tiles round the spawn
        var (e0, n0) = SpawnPoint.ParseTarget();
        var sites = _sites ??= Find(_origin, source, TileId.FromLv95(e0, n0));
        if (sites.Count < 2) { Finish($"{sites.Count} site(s) with a pit near the spawn, 2 wanted"); return; }
        if (_player == null)
        {
            // on the first site's apron: a body is what the ground's collision is built round
            _player = new FootPlayer { Name = "PitDriver", Terrain = _chunks };
            AddChild(_player);
            _player.GlobalPosition = sites[0].World(sites[0].Pit.Route[0]) + Vector3.Up * 1.5f;
        }
        // the ground round both sites must have its collision, the pits cut in it
        foreach (var d in sites)
            foreach (var p in d.Pit.Route.Append(Vector3.Zero))
                if (!terrain.HasCollisionAt(d.World(p)))
                {
                    if (log) GD.Print($"[pitcheck] waiting for the collision at {d.World(p)} ({d.Site.Key})");
                    return;
                }
        if (sites.FirstOrDefault(d => !terrain.InPit(d.World(new Vector3(0, d.Pit.Floor + 0.5f, 0)))) is { } notYet)
        {
            if (log) GD.Print($"[pitcheck] waiting for the pit of {notYet.Site.Key} to be cut");
            return;
        }
        if (!_player.IsOnFloor()) return;
        _started = true;
        _ = Run(_player, sites);
    }

    private List<Dug> Find(WorldOrigin origin, IChunkSource source, TileId around)
    {
        var found = new List<Dug>();
        for (int de = -1; de <= 1; de++)
            for (int dn = -1; dn <= 1; dn++)
            {
                var id = new TileId(around.E + de, around.N + dn);
                var tile = source.LoadBuildingsAsync(id).GetAwaiter().GetResult();
                if (tile == null || !SitePlans.HasSite(tile)) continue;
                var roads = source.LoadRoadsAsync(id).GetAwaiter().GetResult();
                var grid = source.LoadChunkAsync(id).GetAwaiter().GetResult();
                foreach (var site in SitePlans.For(tile, roads))
                {
                    if (site.Pit == null) continue;
                    var (shell, pit) = SiteShellBuilder.Dig(tile, site, grid);
                    if (pit == null) continue;
                    var s = site;
                    found.Add(new Dug(site, shell, pit, p =>
                    {
                        var t = SiteShellBuilder.ToTile(s, p);
                        return origin.ToWorld(id.MinE + t.X, id.MaxN - t.Z, t.Y);
                    }));
                }
            }
        // the course's order: the block's foundations first, the shell second
        return found.OrderBy(d => d.Site.Phase).ThenBy(d => d.Site.Small).ToList();
    }

    private async Task Frames(int n)
    {
        for (int i = 0; i < n; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    /// <summary>What a ray straight down from over a point meets, and its height, or null.</summary>
    private float? Down(Vector3 at, float from, float depth, Rid exclude = default)
    {
        var space = GetViewport().World3D.DirectSpaceState;
        var q = PhysicsRayQueryParameters3D.Create(at with { Y = from }, at with { Y = from - depth });
        if (exclude.IsValid) q.Exclude = new Godot.Collections.Array<Rid> { exclude };
        var hit = space.IntersectRay(q);
        return hit.Count > 0 ? ((Vector3)hit["position"]).Y : null;
    }

    private async Task Run(FootPlayer me, List<Dug> sites)
    {
        var block = sites.First(d => d.Site.Phase == SitePhase.Foundations && !d.Site.Small);
        GD.Print($"[pitcheck] {sites.Count} sites with a pit: " + string.Join("; ", sites.Select(d =>
            $"{d.Site.Key} {d.Site.Phase} {d.Site.Pit!.Basements} basement(s), floor {d.Pit.Floor:F2}, ramp {d.Pit.RampLength:F1} m at {100 * d.Pit.Grade:F0} %")));

        foreach (var d in sites) Rays(d);
        await Drops(sites[0]);
        await Excavator(me, block);
        if (sites.FirstOrDefault(d => d.Site.Phase == SitePhase.Shell) is { } shell) await Walks(shell);
        Finish(null);
    }

    /// <summary>A ray down every metre of the pit and of a ring just outside it.</summary>
    private void Rays(Dug d)
    {
        var a = d.Site.Pit!.Area;
        float rim = d.Pit.TopOuter.Y;
        int holes = 0, outside = 0, count = 0;
        float lowest = float.MaxValue;
        for (float x = a.X0 - 0.4f; x <= a.X1 + 0.4f; x += 1f)
            for (float z = a.Z0 - 0.4f; z <= a.Z1 + 0.4f; z += 1f)
            {
                bool inPit = a.Contains(x, z, -0.05f);
                var at = d.World(new Vector3(x, 0, z));
                var hit = Down(at, rim + 30f, 60f);
                count++;
                if (hit == null) { holes++; continue; }
                lowest = Math.Min(lowest, hit.Value);
                // just outside: the ground (or the piles and rail standing on it), never the pit's floor
                if (!inPit && hit.Value < rim - 0.6f) outside++;
            }
        Expect(holes == 0, $"{d.Site.Key}: rays down through {count} points of the pit and its rim meet something: {holes} met nothing");
        Expect(outside == 0, $"{d.Site.Key}: just outside the pit the ground is there: {outside} ray(s) went down past it");
        Expect(lowest >= d.Pit.Floor - 0.05f, $"{d.Site.Key}: nothing under the pit's floor is reached (lowest {lowest:F2}, floor {d.Pit.Floor:F2})");
    }

    /// <summary>Capsules let fall into the pit at its corners and middle.</summary>
    private async Task Drops(Dug d)
    {
        var a = d.Site.Pit!.Area;
        var spots = new[]
        {
            new Vector3(0, 0, 0), new Vector3(a.X0 + 0.6f, 0, a.Z0 + 0.6f), new Vector3(a.X1 - 0.6f, 0, a.Z0 + 0.6f),
            new Vector3(a.X1 - 0.6f, 0, a.Z1 - 0.6f), new Vector3(a.X0 + 0.6f, 0, a.Z1 - 0.6f),
        };
        var bodies = new List<RigidBody3D>();
        foreach (var s in spots)
        {
            var body = new RigidBody3D { Name = "PitDrop", Mass = 80f };
            body.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f } });
            AddChild(body);
            body.GlobalPosition = d.World(s) with { Y = d.Pit.TopOuter.Y + 4f };
            bodies.Add(body);
        }
        await Frames(240);
        for (int k = 0; k < bodies.Count; k++)
        {
            float feet = bodies[k].GlobalPosition.Y - 0.9f;
            // on the gravel, the slab, the stair core's first lift or the ramp: in the pit and at rest,
            // never under its floor
            Expect(feet > d.Pit.Floor - 0.25f && feet < d.Pit.TopOuter.Y + 0.2f && bodies[k].LinearVelocity.Length() < 0.5f,
                $"{d.Site.Key}: a body dropped into the pit at {spots[k]} rests at {feet:F2} (floor {d.Pit.Floor:F2})");
            bodies[k].QueueFree();
        }
    }

    // ---- the excavator ------------------------------------------------------------------------

    private async Task Excavator(FootPlayer me, Dug d)
    {
        var route = d.Pit.Route.Select(d.World).ToList();
        // standing on the ramp's top since the start, in the lane
        for (int i = 0; i < 300 && !me.IsOnFloor(); i++) await Frames(1);
        Expect(me.SetRide(RideKind.Excavator), $"mounted the excavator on the ramp's top, {new Vector2(me.GlobalPosition.X - route[0].X, me.GlobalPosition.Z - route[0].Z).Length():F1} m from its start");
        var dir = route[1] - route[0];
        me.Rotation = new Vector3(0, Mathf.Atan2(-dir.X, -dir.Z), 0);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(60);
        await Shot("01-on-the-ramp", d, new Vector3(-14f, 10f, 0f));

        // in: down the ramp, along the floor beside the slab
        if (!await DriveTo(me, route[1], "the ramp's foot")) return;
        await Shot("02-at-the-foot", d, new Vector3(0f, 10f, 0f));
        if (!await DriveTo(me, route[2], "the floor beside the slab")) return;
        await Shot("03-in-the-pit", d, new Vector3(0f, 16f, 6f));
        // out: backed up the lane and the ramp, through the mouth onto the ground
        if (!await DriveTo(me, route[1], "the ramp's foot (backing out)", reverse: true)) return;
        if (!await DriveTo(me, d.World(d.Pit.Exit), "the ground past the ramp's mouth (backing out)", reverse: true)) return;
        Expect(!_chunks.InPit(me.GlobalPosition with { Y = me.GlobalPosition.Y - 1f }) || me.GlobalPosition.Y > d.Pit.TopOuter.Y,
            "the excavator is out of the pit");
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(30);
        await Shot("04-out", d, new Vector3(-14f, 10f, 0f));
        me.RideControls = null;
    }

    /// <summary>
    /// Drives to a point: turns on the spot while it points more than 15° off, else crawls at it,
    /// steering. Checks on the way that it never sinks under the floor, and at the point that it stands
    /// on what the plan says is there.
    /// </summary>
    private async Task<bool> DriveTo(FootPlayer me, Vector3 target, string name, bool reverse = false)
    {
        float lowest = me.GlobalPosition.Y;
        double t0 = GameClock.Now;
        while (true)
        {
            var pos = me.GlobalPosition;
            var to = new Vector3(target.X - pos.X, 0, target.Z - pos.Z);
            if (to.Length() < 1.2f) break;
            if (GameClock.Now - t0 > 60)
            {
                Expect(false, $"the excavator reaches {name}: stuck {to.Length():F1} m off at {pos}");
                me.RideControls = null;
                return false;
            }
            // the way it goes: its nose, or backing up its tail
            var fwd = reverse ? me.GlobalBasis.Z : -me.GlobalBasis.Z;
            float err = new Vector3(fwd.X, 0, fwd.Z).SignedAngleTo(to, Vector3.Up);
            // steer is the right stick's: turning left (err > 0) is a negative steer, and on the spot
            // it turns the house's tail as its nose
            float steer = Math.Clamp(-err * 2.5f, -1f, 1f);
            float limit = reverse ? 0.12f : 0.26f;
            me.RideControls = Math.Abs(err) > limit
                ? () => new RideInput(0f, 0f, steer, false)
                : reverse
                    ? () => new RideInput(0f, 1f, 0f, false)
                    : () => new RideInput(1f, 0f, steer, false);
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            lowest = Math.Min(lowest, me.GlobalPosition.Y);
            if ((int)(GameClock.Now * 2) != (int)((GameClock.Now - GetPhysicsProcessDeltaTime()) * 2))
                GD.Print($"[pitcheck]      to {name}: {to.Length():F1} m off, heading off by {Mathf.RadToDeg(err):F0} deg, "
                    + $"{me.GroundSpeed:F1} m/s, y {me.GlobalPosition.Y:F2}, yaw {Mathf.RadToDeg(me.Rotation.Y):F0}");
        }
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Frames(45);
        var at = me.GlobalPosition;
        float? under = Down(at, at.Y + 1.5f, 4f, me.GetRid());
        Expect(under is { } u && Math.Abs(u - target.Y) < 0.35f,
            $"the excavator at {name} in {GameClock.Now - t0:F0} s stands on {under:F2} (planned {target.Y:F2}), lowest {lowest:F2}");
        return true;
    }

    // ---- the shell over its pit ----------------------------------------------------------------

    private async Task Walks(Dug d)
    {
        var up = d.Shell.Route.Select(d.World).ToList();
        await Walk($"{d.Site.Key}: up from the basement", up);
        foreach (var b in d.Pit.Bridges.Take(1))
        {
            float floorY = d.Shell.Levels[d.Shell.Basements];
            var outside = new Vector3(b.From.X + b.Normal.X * 2.5f, d.Pit.TopOuter.Y, b.From.Y + b.Normal.Y * 2.5f);
            var inside = new Vector3(b.To.X - b.Normal.X * 1.2f, floorY, b.To.Y - b.Normal.Y * 1.2f);
            await Walk($"{d.Site.Key}: over the footbridge onto the ground floor", new List<Vector3> { d.World(outside), d.World(inside) });
        }
    }

    /// <summary>A capsule the player's size walking a route, feet at each point's height on arrival.</summary>
    private async Task Walk(string name, List<Vector3> route)
    {
        var walker = new CharacterBody3D { Name = "PitWalker", FloorMaxAngle = Mathf.DegToRad(52f), FloorSnapLength = 0.4f };
        walker.AddChild(new CollisionShape3D { Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.8f } });
        AddChild(walker);
        walker.GlobalPosition = route[0] + Vector3.Up * 0.95f;
        await Frames(10);
        for (int k = 1; k < route.Count; k++)
        {
            double t0 = GameClock.Now;
            while (true)
            {
                var at = walker.GlobalPosition;
                var flat = new Vector2(route[k].X - at.X, route[k].Z - at.Z);
                if (flat.Length() < 0.3f) break;
                if (GameClock.Now - t0 > 8)
                {
                    Expect(false, $"{name}: stuck before point {k} of {route.Count}, feet at {at.Y - 0.9f:F2}");
                    walker.QueueFree();
                    return;
                }
                var dir = flat.Normalized() * Math.Min(3f, flat.Length() * 30f);
                var v = walker.Velocity;
                walker.Velocity = new Vector3(dir.X, walker.IsOnFloor() ? 0f : v.Y - 9.81f / 60f, dir.Y);
                walker.MoveAndSlide();
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            }
            float feet = walker.GlobalPosition.Y - 0.9f;
            if (Math.Abs(feet - route[k].Y) > 0.45f)
            {
                Expect(false, $"{name}: point {k} reached with feet at {feet:F2}, planned {route[k].Y:F2}");
                walker.QueueFree();
                return;
            }
        }
        Expect(true, $"{name}: walked {route.Count} points, feet at {walker.GlobalPosition.Y - 0.9f:F2} (planned {route[^1].Y:F2})");
        walker.QueueFree();
    }

    // ---- pictures and the end ------------------------------------------------------------------

    private int _shot;

    private async Task Shot(string name, Dug d, Vector3 offset)
    {
        if (!Shots || DisplayServer.GetName() == "headless") return;
        var was = GetViewport().GetCamera3D();
        var cam = new Camera3D { Fov = 60f, Near = 0.05f };
        AddChild(cam);
        var me = _player;
        var look = me?.GlobalPosition ?? d.World(Vector3.Zero);
        var a = d.Site.Pit!.Area;
        cam.GlobalPosition = d.World(new Vector3((a.X0 + a.X1) / 2, d.Pit.TopOuter.Y, a.Z1 + 4f)) + offset;
        cam.LookAt(look, Vector3.Up);
        cam.MakeCurrent();
        for (int i = 0; i < 8; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/pit");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, $"{++_shot:D2}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"[pitcheck] wrote {path}");
        cam.QueueFree();
        was?.MakeCurrent();
    }

    private void Finish(string? why)
    {
        if (why != null) Expect(false, why);
        GD.Print($"[pitcheck] RESULT: {(_failures == 0 ? "ok" : $"FAILED ({_failures})")}");
        GetTree().Quit(_failures == 0 ? 0 : 1);
        SetPhysicsProcess(false);
    }
}
