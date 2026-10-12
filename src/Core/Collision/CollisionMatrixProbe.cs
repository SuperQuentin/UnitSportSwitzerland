using System.Threading.Tasks;
using Godot;
using UnitSport.Player;

namespace UnitSport.Core.Collision;

/// <summary>
/// <c>--collidecheck [--movers car,walk] [--targets a,b] --world flat</c> (#699,
/// <c>docs/notes/general/collision-matrix.md</c>): every <see cref="CollisionTargets"/> entry,
/// run into by every mover, on lanes aimed at what is <b>drawn</b> of it, judged by what
/// <b>collides</b>. Whether anything drawn is in the way is measured, not assumed: the drawn
/// triangles are clipped against the mover's corridor (its width, from its wheels or feet up to
/// its roof or head). Drawn in the way and gone through untouched is a <b>ghost</b>; nothing drawn
/// in the way and stopped anyway is an <b>invisible wall</b>. Both fail, unless
/// <see cref="CollisionTargets.Gap"/> lists that run; a gap line none of whose runs goes wrong
/// any more fails too (take it out).
///
/// <para>
/// Lanes: across the target's short side (its centre, and ±35 % of its length when longer than
/// 4 m) and along its long side (the centre, and ±35 % of its width when wider than 4 m: a wing).
/// Runs go in batches on a 200 m grid, the car at 7 m/s so a knock is never a crash that throws
/// the driver.
/// </para>
/// </summary>
public partial class CollisionMatrixProbe : Node3D
{
    public static bool Requested() => CmdArgs.Has("--collidecheck");

    private const float Spacing = 200f, RunUp = 14f, CarSpeed = 7f, Beyond = 1.5f;
    private const int Batch = 60;

    private readonly WorldOrigin _origin;
    public CollisionMatrixProbe(WorldOrigin origin) => _origin = origin;

    /// <summary>What a mover sweeps: its width, and from how high to how high over the ground.</summary>
    private readonly record struct Mover(float Width, float Low, float High);

    private sealed class Run
    {
        public required CollisionTarget Target;
        public required string Mover;
        public required RideKind Kind;
        public required Mover Size;
        public required string Lane;
        /// <summary>The target's drawn bounds in its own frame.</summary>
        public required Aabb Local;
        /// <summary>The target's drawn triangles in its own frame, three vertices each.</summary>
        public required List<Vector3> Tris;
        /// <summary>Along the travel and across it; the lane's offset across from the drawn centre.</summary>
        public required Vector3 Dir, Across;
        public required float Offset;
        public Node3D? Root, Body;
        public FootPlayer? Player;
        public Vector3 Start;
        public float Far, Rise;
        public bool Mounted, Contact, InWay, Settled, Ok;
        public double Deadline, StillSince = -1;
        public string Outcome = "", First = "";
        public (int Line, string Why)? Gap;
    }

    public override void _Ready()
    {
        // vehicles are parked under it, as in the game
        if (Vehicles.VehicleManager.Instance == null) Vehicles.VehicleManager.Create(this, null, _origin);
        _ = Go();
    }

    private async Task Go()
    {
        var filters = CmdArgs.Value("--targets")?.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries);
        var movers = (CmdArgs.Value("--movers") ?? "car,walk").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var targets = CollisionTargets.All()
            .Where(t => filters == null || filters.Any(f => t.Key.ToLowerInvariant().Contains(f)))
            .ToList();
        var kinds = movers.ToDictionary(m => m, m => m is "walk" or "foot" ? RideKind.OnFoot : RideProbe.KindNamed(m));
        var sizes = kinds.ToDictionary(k => k.Key, k => Measure(k.Value));
        var runs = new List<Run>();
        foreach (var t in targets)
        {
            var (drawn, tris) = LocalDrawn(t);
            foreach (var m in movers)
                foreach (var (lane, dir, across, offset) in Lanes(drawn))
                    runs.Add(new Run
                    {
                        Target = t, Mover = m, Kind = kinds[m], Size = sizes[m], Local = drawn, Tris = tris,
                        Lane = lane, Dir = dir, Across = across, Offset = offset,
                    });
        }
        GD.Print($"[collide] {targets.Count} targets x {movers.Length} movers: {runs.Count} runs");

        // the long runs together, so a batch lasts as long as its longest and no longer
        var ordered = runs.OrderBy(r => Mathf.Abs(r.Local.Size.Dot(r.Dir))).ToList();
        for (int i = 0; i < ordered.Count; i += Batch)
            await RunBatch(ordered.Skip(i).Take(Batch).ToList());
        // physics has its bad frames: a run that went wrong goes again, and counts as it does then
        var again = runs.Where(r => !r.Ok).ToList();
        foreach (var r in again) { r.First = r.Outcome; r.Contact = r.Settled = r.Mounted = false; r.StillSince = -1; r.Rise = 0f; }
        for (int i = 0; i < again.Count; i += Batch)
            await RunBatch(again.Skip(i).Take(Batch).ToList());

        Report(runs);
    }

    private static Mover Measure(RideKind kind)
    {
        if (kind == RideKind.OnFoot || Rideable.Create(kind) is not { } ride) return new Mover(0.6f, 0.1f, 1.7f);
        var visual = ride.BuildVisual(0);
        var box = Avatar.MeshBounds.Of(visual);
        visual.Free();
        return new Mover(box.Size.X, 0.1f, box.End.Y);
    }

    /// <summary>What a target draws, in its own frame: built off the tree, measured and kept.</summary>
    private (Aabb, List<Vector3>) LocalDrawn(CollisionTarget t)
    {
        var node = t.Drawn?.Invoke() ?? t.Build(_origin, Transform3D.Identity);
        var box = Avatar.MeshBounds.Of(node);
        var tris = CollisionTargets.DrawnTriangles(node);
        node.Free();
        return (box, tris);
    }

    private static IEnumerable<(string, Vector3, Vector3, float)> Lanes(Aabb drawn)
    {
        bool longZ = drawn.Size.Z >= drawn.Size.X;
        var alongLong = longZ ? Vector3.Back : Vector3.Right;
        var alongShort = longZ ? Vector3.Right : Vector3.Back;
        float longSize = Mathf.Max(drawn.Size.X, drawn.Size.Z), shortSize = Mathf.Min(drawn.Size.X, drawn.Size.Z);

        // across the short side: the broadside, along its length
        yield return ("side", alongShort, alongLong, 0f);
        if (longSize > 4f)
        {
            yield return ("side-", alongShort, alongLong, -0.35f * longSize);
            yield return ("side+", alongShort, alongLong, 0.35f * longSize);
        }
        // along the long side: nose on, and its wings
        yield return ("end", alongLong, alongShort, 0f);
        if (shortSize > 4f)
        {
            yield return ("end-", alongLong, alongShort, -0.35f * shortSize);
            yield return ("end+", alongLong, alongShort, 0.35f * shortSize);
        }
    }

    private async Task RunBatch(List<Run> batch)
    {
        int cols = (int)Mathf.Ceil(Mathf.Sqrt(batch.Count));
        for (int i = 0; i < batch.Count; i++)
        {
            var r = batch[i];
            // off the world origin: a vehicle parked at (0, 0) ended up on its neighbour
            var centre = new Vector3((i % cols + 1) * Spacing, TestWorld.GroundY, (i / cols + 1) * Spacing);
            r.Root = new Node3D { Name = $"Run{i}" };
            AddChild(r.Root);
            r.Root.GlobalPosition = centre;
            r.Body = CollisionTargets.Spawn(r.Target, r.Root, _origin, new Transform3D(Basis.Identity, centre));
        }
        // a parked vehicle drops the hand's breadth it was put up by, and settles
        await Seconds(1.0);

        foreach (var r in batch)
        {
            var xf = r.Body!.GlobalTransform;
            var drawn = xf * r.Local;
            var c = drawn.GetCenter();
            float half = Mathf.Abs(drawn.Size.Dot(r.Dir)) * 0.5f;
            var aim = new Vector3(c.X, 0, c.Z) + r.Across * r.Offset;
            r.InWay = CollisionTargets.Crosses(r.Tris.Select(v => xf * v).ToList(), Corridor(aim, r.Across, r.Size));
            r.Start = aim - r.Dir * (half + RunUp) + Vector3.Up * (TestWorld.GroundY + 1.2f);
            r.Far = RunUp + 2f * half + Beyond;

            var p = new FootPlayer { Name = $"Mover_{r.Root!.Name}" };
            p.Rotation = new Vector3(0, Mathf.Atan2(-r.Dir.X, -r.Dir.Z), 0);
            // put at its start before it enters the tree: added at the run's centre first, Jolt swept it
            // out from under the target, and a parked combine standing on it was carried along 20 m
            p.Position = r.Start - r.Root.GlobalPosition;
            r.Root.AddChild(p);
            p.GlobalPosition = r.Start;
            p.DebugLaunch(r.Start, Vector3.Zero);
            r.Player = p;
            r.Deadline = GameClock.Now + 4.0 + r.Far / (r.Kind == RideKind.OnFoot ? 2.5 : 4.0);
            if (r.Kind == RideKind.OnFoot)
            {
                var dir = r.Dir;
                p.WalkControls = () => (dir, true);
                r.Mounted = true;
            }
        }

        while (batch.Any(r => !r.Settled))
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            foreach (var r in batch.Where(r => !r.Settled)) Step(r);
        }
        foreach (var r in batch) { r.Body!.QueueFree(); r.Root!.QueueFree(); }
        await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }

    private void Step(Run r)
    {
        var p = r.Player!;
        if (!r.Mounted)
        {
            if (!p.IsOnFloor()) { if (GameClock.Now > r.Deadline) Settle(r, "never mounted"); return; }
            r.Mounted = p.SetRide(r.Kind);
            if (!r.Mounted) { Settle(r, "mount refused"); return; }
            p.RideControls = () => new RideInput(p.RideSpeed < CarSpeed ? 1f : 0f, 0f, 0f, false);
        }

        for (int i = 0; i < p.GetSlideCollisionCount(); i++)
        {
            var hit = p.GetSlideCollision(i).GetCollider();
            // a walkable vehicle's deck is the walker's own copy of it (FootPlayer.Deck): only it is in this lane
            if (CollisionTargets.Owns(r.Body!, hit) || hit is StaticBody3D deck && deck.GetParent() == p && deck.Name.ToString().StartsWith("Deck_"))
                r.Contact = true;
        }

        float progress = (p.GlobalPosition - r.Start).Dot(r.Dir);
        r.Rise = Mathf.Max(r.Rise, p.GlobalPosition.Y - r.Start.Y);
        float speed = new Vector2(p.Velocity.X, p.Velocity.Z).Length();
        if (progress >= r.Far) { Settle(r, "passed"); return; }
        if (p.Ragdolled) { Settle(r, "thrown"); return; }
        if (speed < 0.3f && progress > 1f)
        {
            if (r.StillSince < 0) r.StillSince = GameClock.Now;
            else if (GameClock.Now - r.StillSince > 1.0)
            {
                // nothing but the target is in a lane: a stop past the start is it
                Settle(r, progress > 2f ? "blocked" : $"stuck at {progress:F1} m");
                return;
            }
        }
        else r.StillSince = -1;
        if (GameClock.Now > r.Deadline) Settle(r, progress > 2f ? "blocked" : $"stuck at {progress:F1} m");
    }

    private void Settle(Run r, string outcome)
    {
        r.Settled = true;
        // what it means depends on whether anything drawn stood in the way
        r.Outcome = outcome switch
        {
            "passed" => !r.InWay ? "clear" : r.Contact ? "over" : "ghost",
            "blocked" or "thrown" => r.InWay ? outcome : "invisible wall",
            _ => outcome,
        };
        r.Gap = CollisionTargets.Gap(r.Target, r.Mover, r.Lane);
        bool right = r.Outcome is "clear" or "over" or "blocked" or "thrown";
        r.Ok = right || r.Gap != null && r.Outcome is "ghost" or "invisible wall";
        if (r.Player is { } p) { p.RideControls = null; p.WalkControls = null; }
    }

    private void Report(List<Run> runs)
    {
        foreach (var r in runs)
            GD.Print($"[collide] {(r.Ok ? "ok  " : "FAIL")} {r.Target.Key,-48} {r.Mover,-6} {r.Lane,-6} {r.Outcome}{(r.Rise > 0.5f ? $", up {r.Rise:F1} m" : "")}"
                + (r.First != "" && r.First != r.Outcome ? $"  (flaky: {r.First} the first time)" : "")
                + (r.Gap is { } gap && r.Outcome is "ghost" or "invisible wall" ? $"  (known gap: {gap.Why})" : ""));
        var failed = runs.Where(r => !r.Ok).Select(r => $"{r.Target.Name} {r.Mover} {r.Lane} {r.Outcome}").ToList();
        // a gap line whose every run now behaves: fixed, take it out
        foreach (var line in runs.Where(r => r.Gap != null).GroupBy(r => r.Gap!.Value.Line))
            if (line.All(r => r.Outcome is not ("ghost" or "invisible wall")))
            {
                GD.Print($"[collide] FAIL gap fixed, take its line out of CollisionTargets: {CollisionTargets.GapLine(line.Key)}");
                failed.Add($"gap fixed: {CollisionTargets.GapLine(line.Key)}");
            }
        int gaps = runs.Count(r => r.Gap != null && r.Outcome is "ghost" or "invisible wall");
        GD.Print(failed.Count == 0
            ? $"[collide] RESULT: ok, {runs.Count} runs ({gaps} on known gaps)"
            : $"[collide] RESULT: FAILED {failed.Count}: " + string.Join(", ", failed.Take(30)));
        GetTree().Quit(failed.Count == 0 ? 0 : 1);
    }

    /// <summary>The box a mover sweeps on its lane: its width across, its height up, endless along.</summary>
    private static Aabb Corridor(Vector3 aim, Vector3 across, Mover m)
    {
        var along = Vector3.One - across.Abs() - Vector3.Up;
        var lo = -along * 1e4f + across * (aim.Dot(across) - m.Width * 0.5f) + Vector3.Up * (TestWorld.GroundY + m.Low);
        var hi = along * 1e4f + across * (aim.Dot(across) + m.Width * 0.5f) + Vector3.Up * (TestWorld.GroundY + m.High);
        return new Aabb(lo, hi - lo);
    }

    private async Task Seconds(double s)
    {
        double end = GameClock.Now + s;
        while (GameClock.Now < end) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
    }
}
