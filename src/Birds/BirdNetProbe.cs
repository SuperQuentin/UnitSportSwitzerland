using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Net;
using UnitSport.Player;

namespace UnitSport.Birds;

/// <summary>
/// <c>--birdnetcheck A|B</c> with <c>--connect</c> (driven by <c>tools/birdnetcheck.sh</c>): the shared birds
/// between two real clients and a dedicated server (#143). Both stand at the same place.
/// <list type="bullet">
/// <item>both must be sent birds by the server, and the same ones (same id, same place);</item>
/// <item>A walks up to a resting bird and shoots it through the real item path: the server must kill it, A's
/// journal must score it, B must see the same bird fall, and B's journal must stay as it was;</item>
/// <item>A fires a second shot into the air: the server flushes the birds around it and B must see resting
/// birds take off;</item>
/// <item>an aircraft gun round (<see cref="BirdLife.TracerHit"/>) through a live bird: the server kills it
/// and A's journal scores it.</item>
/// <item>A walks up to a resting bird (no shot): the server flushes it and B must see it take off.</item>
/// </list>
/// Windowed, each role saves zoomed pictures of those moments to <c>test_output/birdnet_&lt;role&gt;_*.png</c>.
/// Roles talk through chat lines. Scratch inventory; the journal is the machine's real <c>user://birds.json</c>.
/// </summary>
public partial class BirdNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--birdnetcheck");

    public BirdNetProbe(ItemController items) : base(items, "birdnet", "PN") { }
    public BirdNetProbe() : this(null!) { }

    /// <summary>A failure here counts and carries on (the RESULT line comes at the end), it does not quit.</summary>
    protected override void Fail(string why) => Expect(false, why);
    private BirdLife? Life => BirdLife.Instance;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        ProcessPriority = 1000;
        if (!await Joined(150, () => Life != null && Life.Net != null))
        {
            await Finish(0);
            return;
        }
        var life = Life!;
        Expect(!life.Authority, "this client is not the authority on the birds");
        Expect(Enumerable.Range(0, BirdCatalog.All.Length).All(i => BirdCatalog.All[i].Index == i), "species index = catalogue position (the wire name)");
        life.Month = 10;   // the same hunting season everywhere

        Expect(await Until(() => life.Birds.Count(b => b.Remote) >= 3, 90), $"the server sent birds ({life.Birds.Count(b => b.Remote)})");
        await Seconds(2.0);
        if (Town) { if (_role == "A") await TownA(Me!, life); else await TownB(Me!, life); }
        else if (_role == "A") await RunA(Me!, life); else await RunB(Me!, life);

        await Finish(1.0);
    }

    private int _firstId;

    private string Ids(BirdLife life, FootPlayer me)
    {
        var near = life.Birds
            .Where(b => b.Remote && b.Id > 0 && b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming)
            .OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).Take(10).ToList();
        _firstId = near.Count > 0 ? near[0].Id : 0;
        return string.Join(";", near.Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Id}:{b.Node.GlobalPosition.X:F0}:{b.Node.GlobalPosition.Z:F0}")));
    }

    private async Task RunA(FootPlayer me, BirdLife life)
    {
        // B may not be listening yet: repeat until it answers
        bool ready = false;
        for (int i = 0; i < 30 && !ready; i++)
        {
            Say("ids " + Ids(life, me));
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never joined"); return; }
        if (life.Birds.FirstOrDefault(b => b.Id == _firstId) is { } first) await Snap(me, "shared", first);

        int gun = Enumerable.Range(0, Inventory.Size).FirstOrDefault(i => _items.Inventory[i].Id == ItemId.Shotgun, -1);
        if (gun < 0) { Fail("no shotgun"); return; }
        if (!Enumerable.Range(0, Inventory.Size).Any(i => _items.Inventory[i].Id == ItemId.Shells)) _items.Inventory.Add(ItemId.Shells, 10);
        var home = me.GlobalPosition;

        // walk up to resting birds until one is shot: trees and slopes may be in the way of a try
        Bird? killed = null;
        int before = life.Journal.Score;
        var tried = new HashSet<int>();
        for (int attempt = 0; attempt < 6 && killed == null; attempt++)
        {
            var target = life.Birds.Where(b => b.Remote && b.Id > 0 && !tried.Contains(b.Id)
                    && b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming
                    && b.Node.GlobalPosition.DistanceTo(home) < 120f
                    && b.FlushDistance + 4f < 28f)
                .OrderBy(b => b.Node.GlobalPosition.DistanceTo(home)).FirstOrDefault();
            if (target == null) break;
            tried.Add(target.Id);
            float stand = target.FlushDistance + 4f;
            var away = (home - target.Node.GlobalPosition) with { Y = 0 };
            var spot = target.Node.GlobalPosition + (away.LengthSquared() < 1f ? Vector3.Back : away.Normalized()) * stand;
            spot.Y = life.Ground(spot) + 1.5f;
            me.LeaveInterior(spot, 0f);
            me.Velocity = Vector3.Zero;
            await Until(() => me.IsOnFloor(), 5);
            await Seconds(1.0);   // the server learns where we are; the bird has to still be there
            if (target.State is not (Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming)) continue;
            // in a town a house may stand between: a shot only at a bird the eye can see (a miss
            // would flush every bird around and leave nothing to try next)
            var sight = me.GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(
                me.EyePosition, target.Centre + Vector3.Up * 0.1f, uint.MaxValue, new Godot.Collections.Array<Rid> { me.GetRid() }));
            if (sight.Count > 0) { GD.Print($"[birdnet A] #{target.Id} not in sight from {stand:F0} m, next"); continue; }
            await Aim(me, target.Centre);
            GD.Print($"[birdnet A] aimed: {-me.Camera.GlobalTransform.Basis.Z.Dot((target.Centre - me.EyePosition).Normalized()):F4}");
            int shells = CountOf(ItemId.Shells);
            GD.Print($"[birdnet A] shooting #{target.Id} {target.Species.Name} at {target.Node.GlobalPosition.DistanceTo(me.GlobalPosition):F1} m");
            _items.UseSlot(me, gun);
            Expect(CountOf(ItemId.Shells) == shells - 1, "the item path spent a shell");
            if (await Until(() => life.Journal.Score != before, 4)) killed = target;
            await Seconds(1.0);
        }
        Expect(killed != null, "the shot was reported, the server killed the bird and A's journal scored it");
        if (killed != null)
        {
            Expect(await Until(() => killed.State is Bird.Mode.Falling or Bird.Mode.Dead, 3), $"the bird fell on A's screen ({killed.State})");
            Expect(await Until(() => killed.State == Bird.Mode.Dead, 8), "...and lies dead");
            await Snap(me, "kill", killed);
            Say($"shot {killed.Id} {killed.Species.Name.Replace(' ', '_')}");
        }
        else Say("shot 0 none");

        // a shot into the air: everything resting within 150 m takes off, for B too. The first shot
        // flushed everything around: B waits for newly spawned birds to settle near A first.
        await Seconds(1.0);
        // LV95: B's world space is not this one (every peer has its own origin, #185)
        Say(string.Create(CultureInfo.InvariantCulture, $"at {me.Global.E:F0} {me.Global.N:F0}"));
        if (!await Heard("B", "flushready", 70)) { Fail("B not ready for the flush"); return; }
        me.LookPitch = 1.2f;
        await Seconds(0.3);
        _items.UseSlot(me, gun);
        Say("fired");
        await Heard("B", "done", 30);

        // an aircraft gun round (CombatManager -> TracerHit) through a live bird: reported, killed by
        // the server, scored here
        var bird = life.Birds.Where(x => x.Remote && x.State is not (Bird.Mode.Falling or Bird.Mode.Dead))
            .OrderBy(x => x.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        Expect(bird != null, "a live bird for the gun round");
        if (bird != null)
        {
            int score = life.Journal.Score;
            var dir = (bird.Centre - me.EyePosition).Normalized();
            var hit = life.TracerHit(me.EyePosition, bird.Centre + dir * 5f, mine: true);
            Expect(hit == bird, $"the round passes through #{bird.Id} {bird.Species.Name}");
            Expect(await Until(() => life.Journal.Score != score, 4) && bird.State is Bird.Mode.Falling or Bird.Mode.Dead,
                $"the server killed it and A's journal scored it ({bird.State})");
        }

        // a flush by approach alone: A walks up to a resting bird, B watches it take off
        Bird? Resting() => life.Birds.Where(b => b.Remote && b.State is Bird.Mode.Ground or Bird.Mode.Perched
                && b.Node.GlobalPosition.DistanceTo(me.GlobalPosition) is > 25f and < 120f)
            .OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        await Until(() => Resting() != null, 60);
        var calm = Resting();
        Expect(calm != null, "a resting bird to walk up to");
        Say($"approach {calm?.Id ?? 0}");
        if (calm == null || !await Heard("B", "watching", 30)) { Fail("B is not watching the approach"); return; }
        float flushAt = calm.FlushDistance;
        var from = (me.GlobalPosition - calm.Node.GlobalPosition) with { Y = 0 };
        var near = calm.Node.GlobalPosition + (from.LengthSquared() < 1f ? Vector3.Back : from.Normalized()) * flushAt * 0.5f;
        near.Y = life.Ground(near) + 1.5f;
        me.LeaveInterior(near, 0f);
        me.Velocity = Vector3.Zero;
        Expect(await Until(() => calm.State == Bird.Mode.Flying, 6), $"#{calm.Id} took off when A came within {flushAt * 0.5f:F0} m ({calm.State})");
        await Heard("B", "seen", 20);
    }

    private async Task RunB(FootPlayer me, BirdLife life)
    {
        // A may not be listening yet (its ground loads at its own pace): say it until A answers
        bool heard = false;
        for (int i = 0; i < 30 && !heard; i++)
        {
            Say("ready");
            heard = await Heard("A", "ids", 3);
        }
        Expect(heard, "heard A's birds");
        var line = _heard.LastOrDefault(l => l.Contains("PN A ids")) ?? "";
        var theirs = line[(line.IndexOf("ids", StringComparison.Ordinal) + 3)..].Trim().Split(';', StringSplitOptions.RemoveEmptyEntries);
        int shared = 0;
        foreach (var t in theirs)
        {
            var f = t.Split(':');
            if (f.Length < 3 || !int.TryParse(f[0], out int id)) continue;
            var mine = life.Birds.FirstOrDefault(b => b.Id == id);
            if (mine != null && Mathf.Abs(mine.Node.GlobalPosition.X - float.Parse(f[1], CultureInfo.InvariantCulture)) < 8f
                && Mathf.Abs(mine.Node.GlobalPosition.Z - float.Parse(f[2], CultureInfo.InvariantCulture)) < 8f) shared++;
        }
        if (theirs.Length > 0 && int.TryParse(theirs[0].Split(':')[0], out int firstId) && life.Birds.FirstOrDefault(b => b.Id == firstId) is { } first)
            await Snap(me, "shared", first);
        Expect(shared >= 1, $"the same birds in the same places as A: {shared} of {theirs.Length} (ids {string.Join(" ", theirs.Select(t => t.Split(':')[0]))})");

        int score = life.Journal.Score;
        Expect(await Heard("A", "shot", 90), "A shot");
        var shot = _heard.LastOrDefault(l => l.Contains("PN A shot")) ?? "";
        var parts = shot.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int dead = parts.Length > 4 && int.TryParse(parts[^2], out int d) ? d : 0;
        Expect(dead > 0, $"A bagged bird #{dead}");
        if (dead > 0)
        {
            var bird = life.Birds.FirstOrDefault(b => b.Id == dead);
            Expect(bird == null || await Until(() => bird.State is Bird.Mode.Falling or Bird.Mode.Dead || bird.Gone, 6),
                $"the same bird is down on B's screen too ({bird?.State.ToString() ?? "already gone"})");
            if (bird != null) await Snap(me, "kill", bird);
        }
        Expect(life.Journal.Score == score, "the kill is not in B's journal");

        // flush: resting birds near A (new ones, the first shot flushed the rest), then A's air shot
        Expect(await Heard("A", "at", 30), "A says where it stands");
        var at = (_heard.LastOrDefault(l => l.Contains("PN A at")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shooter = at.Length >= 2
            ? new Core.GlobalPos(double.Parse(at[^2], CultureInfo.InvariantCulture), double.Parse(at[^1], CultureInfo.InvariantCulture), 0)
            : me.Global;
        // in this client's world space now, whatever it was when A said it
        Vector3 Shooter() => life.Origin.ToWorld(shooter);
        HashSet<int> Resting() => life.Birds.Where(b => b.State is Bird.Mode.Ground or Bird.Mode.Perched or Bird.Mode.Swimming
            && new Vector2(b.Node.GlobalPosition.X - Shooter().X, b.Node.GlobalPosition.Z - Shooter().Z).Length() < 130f).Select(b => b.Id).ToHashSet();
        await Until(() => Resting().Count >= 2, 50);
        var resting = Resting();
        GD.Print($"[birdnet B] {resting.Count} resting birds within 130 m of A before the flush");
        Expect(resting.Count > 0, "resting birds near A to flush");
        Say("flushready");
        Expect(await Heard("A", "fired", 40), "A fired into the air");
        Expect(resting.Count > 0 && await Until(() => life.Birds.Count(b => resting.Contains(b.Id) && b.State is Bird.Mode.Flying) * 2 >= resting.Count, 6),
            $"resting birds took off on B's screen after A's shot ({life.Birds.Count(b => resting.Contains(b.Id) && b.State is Bird.Mode.Flying)} of {resting.Count})");
        Say("done");

        // A walks up to a resting bird: it must take off on B's screen, A having fired nothing
        Expect(await Heard("A", "approach", 120), "A picked a bird to walk up to");
        var ap = (_heard.LastOrDefault(l => l.Contains("PN A approach")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var calm = ap.Length > 0 && int.TryParse(ap[^1], out int calmId) ? life.Birds.FirstOrDefault(b => b.Id == calmId) : null;
        Expect(calm != null && calm.State is Bird.Mode.Ground or Bird.Mode.Perched, $"B has that bird at rest ({calm?.State.ToString() ?? "unknown"})");
        if (calm != null) await Snap(me, "approach_before", calm);
        Say("watching");
        if (calm != null)
        {
            Expect(await Until(() => calm.State == Bird.Mode.Flying, 8), $"#{calm.Id} took off on B's screen when A walked up ({calm.State})");
            await Snap(me, "approach_after", calm, zoom: false);
        }
        Say("seen");
    }

    /// <summary>Turns the view toward a point (yaw and pitch, either sign convention) until the camera looks at it.</summary>
    private async Task Aim(FootPlayer me, Vector3 at)
    {
        var d = (at - me.EyePosition).Normalized();
        float pitch = Mathf.Asin(d.Y);
        float sign = 1f;
        foreach (float yaw in new[] { Mathf.Atan2(-d.X, -d.Z), Mathf.Atan2(d.X, d.Z) })
        {
            me.LookYaw = yaw;
            me.LookPitch = pitch;
            await Seconds(0.25);
            if (-me.Camera.GlobalTransform.Basis.Z.Dot(d) > 0.9f) break;
            sign = -1f;
        }
        // then close the last degrees from where the camera really is (it sits off the eye and bobs)
        for (int i = 0; i < 6; i++)
        {
            var f = -me.Camera.GlobalTransform.Basis.Z;
            d = (at - me.Camera.GlobalPosition).Normalized();
            if (f.Dot(d) > 0.9998f) return;
            var ff = new Vector2(f.X, f.Z); var df = new Vector2(d.X, d.Z);
            float yawErr = Mathf.Atan2(ff.X * df.Y - ff.Y * df.X, ff.Dot(df));   // signed, about the vertical
            me.LookYaw -= sign * yawErr;
            me.LookPitch += Mathf.Asin(Mathf.Clamp(d.Y, -1f, 1f)) - Mathf.Asin(Mathf.Clamp(f.Y, -1f, 1f));
            await Seconds(0.15);
        }
    }

    /// <summary>Windowed only: looks at a bird through a narrow lens (a frame ~8 m across) and saves the picture.</summary>
    private Task Snap(FootPlayer me, string name, Bird bird, bool zoom = true) => Snap(me, name, () => bird.Centre, zoom ? 8f : 0f);

    /// <summary>Windowed only: looks at a point through a lens framing <paramref name="width"/> m there (0: the normal view) and saves the picture.</summary>
    private Action? _lens;

    public override void _Process(double delta) => _lens?.Invoke();

    private async Task Snap(FootPlayer me, string name, Func<Vector3> at, float width)
    {
        if (DisplayServer.GetName() == "headless") return;
        await Aim(me, at());
        float normal = me.Camera.Fov;
        var home = me.Camera.Transform;
        // in a town the view from where the player stands is often a wall: look from a clear spot near the target
        Vector3? eye = Blocked(me, me.Camera.GlobalPosition, at()) ? ClearSpot(me, at()) : null;
        float Fov() => width > 0f ? Mathf.Clamp(Mathf.RadToDeg(2f * Mathf.Atan(width * 0.5f / Mathf.Max(1f, me.Camera.GlobalPosition.DistanceTo(at())))), 2f, 70f) : normal;
        // the direction holds when set after the player's own _Process; the FOV only right before the draw
        Action lens = () =>
        {
            if (!IsInstanceValid(me)) return;
            if (eye is { } e) me.Camera.GlobalPosition = e;
            me.Camera.LookAt(at());
        };
        Action fov = () => { if (IsInstanceValid(me)) me.Camera.Fov = Fov(); };
        RenderingServer.FramePreDraw += fov;
        _lens = lens;
        await Seconds(0.6);
        var dir = ProjectSettings.GlobalizePath("res://test_output");
        System.IO.Directory.CreateDirectory(dir);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(dir, $"birdnet_{_role}_{name}.png"));
        _lens = null;
        RenderingServer.FramePreDraw -= fov;
        if (eye != null && IsInstanceValid(me)) me.Camera.Transform = home;
        GD.Print($"{Log} picture {name}");
    }

    private static bool Blocked(FootPlayer me, Vector3 from, Vector3 to)
    {
        var space = me.GetWorld3D().DirectSpaceState;
        var ex = new Godot.Collections.Array<Rid> { me.GetRid() };
        var end = to + (from - to).Normalized() * 0.6f;   // stop short of the bird's own perch
        // both ways: a ray that starts inside a building does not hit its walls from within
        return space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, end, uint.MaxValue, ex)).Count > 0
            || space.IntersectRay(PhysicsRayQueryParameters3D.Create(end, from, uint.MaxValue, ex)).Count > 0;
    }

    /// <summary>A camera spot 8–20 m from <paramref name="target"/>, a little above it, with a clear line to it; null if none.</summary>
    private static Vector3? ClearSpot(FootPlayer me, Vector3 target)
    {
        foreach (float dist in new[] { 8f, 14f, 20f })
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.Tau / 16f;
                var e = target + new Vector3(Mathf.Cos(a) * dist, dist * 0.35f, Mathf.Sin(a) * dist);
                if (!Blocked(me, e, target)) return e;
            }
        return null;
    }

    /// <summary><c>--birdtown</c>: the town part of #143 instead of the hunt (tools/birdnetcheck.sh with TOWN=1).</summary>
    private static bool Town => Array.IndexOf(OS.GetCmdlineUserArgs(), "--birdtown") >= 0;

    private float Above(BirdLife life, Bird b) => b.Node.GlobalPosition.Y - life.Ground(b.Node.GlobalPosition);

    /// <summary>
    /// Town, A: pictures of the town birds; stands under a pigeon on an eave or a ledge until it lets go
    /// on A (A's screen splat, B sees it land on A); then walks up to a town bird on the ground (it
    /// lets A much closer than a field bird) and B watches it take off and land again.
    /// </summary>
    private async Task TownA(FootPlayer me, BirdLife life)
    {
        Expect(await Until(() => life.IsTown(me.GlobalPosition), 30), "the buildings here make a town");
        Expect(await Until(() => life.Birds.Count(b => b.Remote && b.Town) >= 6, 90), $"town birds around ({life.Birds.Count(b => b.Remote && b.Town)})");
        await Seconds(3.0);
        var kinds = life.Birds.Where(b => b.Remote && b.Town).GroupBy(b => b.Species.Name).Select(g => $"{g.Key} x{g.Count()}");
        GD.Print($"[birdnet A] town birds: {string.Join(", ", kinds)}");
        bool ready = false;
        for (int i = 0; i < 30 && !ready; i++) { Say("town"); ready = await Heard("B", "ready", 3); }
        if (!ready) { Fail("B never joined"); return; }
        var home = me.GlobalPosition;

        // a pigeon high enough on a building that standing under it does not scare it
        var space = me.GetWorld3D().DirectSpaceState;
        Vector3? Under(Bird b)
        {
            var o = new Vector3(Mathf.Sin(b.Yaw), 0, Mathf.Cos(b.Yaw)) * 0.8f;
            var top = b.Node.GlobalPosition + o;
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(top, top + Vector3.Down * 60f, uint.MaxValue, new Godot.Collections.Array<Rid> { me.GetRid() }));
            if (hit.Count == 0) return null;
            var floor = hit["position"].AsVector3();
            return Mathf.Abs(floor.Y - life.Ground(floor)) < 0.6f ? floor : null;   // the street, not a lower roof
        }
        Bird? pigeon = null;
        Vector3 spot = default;
        await Until(() =>
        {
            foreach (var b in life.Birds.Where(b => b.Remote && b.Town && b.Species.Body == BodyPlan.Pigeon && b.State == Bird.Mode.Perched && Above(life, b) > 5.5f)
                         .OrderBy(b => b.Node.GlobalPosition.DistanceTo(home)))
                if (Under(b) is { } u) { pigeon = b; spot = u; return true; }
            return false;
        }, 120);
        Expect(pigeon != null, "a pigeon on an eave or a ledge to stand under");
        if (pigeon != null)
        {
            await Snap(me, "town_pigeon", pigeon);
            Say($"under {pigeon.Id}");
            me.LeaveInterior(spot + Vector3.Up * 0.3f, 0f);
            me.Velocity = Vector3.Zero;
            await Until(() => me.IsOnFloor(), 5);
            int splats = life.Splats;
            bool hit = await Until(() => life.Splats > splats || pigeon.State != Bird.Mode.Perched, 60) && life.Splats > splats;
            Expect(hit, $"#{pigeon.Id} let go on A ({pigeon.State}, {me.GlobalPosition.DistanceTo(pigeon.Node.GlobalPosition):F1} m below)");
            if (hit) await Snap(me, "splat_screen", () => me.EyePosition + -me.Camera.GlobalTransform.Basis.Z * 10f, 0f);
            Say(hit ? "splat" : "nosplat");
            await Heard("B", "seensplat", 20);
            me.LeaveInterior(home, 0f);
            await Seconds(2.0);
        }

        // a tame town bird on the street: A walks right up to it
        Bird? Walker() => life.Birds.Where(b => b.Remote && b.Town && b.State == Bird.Mode.Ground && b.Node.GlobalPosition.DistanceTo(me.GlobalPosition) is > 15f and < 120f)
            .OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        await Until(() => Walker() != null, 90);
        var calm = Walker();
        Expect(calm != null, "a town bird on the ground");
        Say($"approach {calm?.Id ?? 0}");
        if (calm == null || !await Heard("B", "watching", 30)) { Fail("B is not watching the approach"); return; }
        // a field bird would have gone long before: a town bird sits until A is within a few metres
        var from = (me.GlobalPosition - calm.Node.GlobalPosition) with { Y = 0 };
        var dirAway = from.LengthSquared() < 1f ? Vector3.Back : from.Normalized();
        var close = calm.Node.GlobalPosition + dirAway * (calm.FlushDistance + 3f);
        me.LeaveInterior(close with { Y = life.Ground(close) + 1.5f }, 0f);
        await Seconds(2.5);
        Expect(calm.State == Bird.Mode.Ground, $"#{calm.Id} {calm.Species.Name} let A come within {calm.FlushDistance + 3f:F0} m ({calm.State})");
        var nearer = calm.Node.GlobalPosition + dirAway * (calm.FlushDistance * 0.4f);
        me.LeaveInterior(nearer with { Y = life.Ground(nearer) + 1.5f }, 0f);
        Expect(await Until(() => calm.State == Bird.Mode.Flying, 6), $"#{calm.Id} took off when A came within {calm.FlushDistance * 0.4f:F1} m ({calm.State})");
        me.LeaveInterior(home, 0f);
        await Heard("B", "landed", 60);
    }

    private async Task TownB(FootPlayer me, BirdLife life)
    {
        Expect(await Until(() => life.Birds.Count(b => b.Remote && b.Town) >= 6, 90), "town birds around");
        bool heard = false;
        for (int i = 0; i < 30 && !heard; i++) { Say("ready"); heard = await Heard("A", "town", 3); }
        Expect(heard, "heard A");
        // counted before the pictures: A may already stand under its pigeon (and be hit) while B takes them
        int drops = life.DropsOnOthers;
        // what B sees of the town: a bird high on a roof, one on the street, and a flock aloft
        var roof = life.Birds.Where(b => b.Town && b.State == Bird.Mode.Perched).OrderByDescending(b => Above(life, b)).FirstOrDefault();
        if (roof != null) await Snap(me, "town_roof", () => roof.Centre, 14f);
        var street = life.Birds.Where(b => b.Town && b.State == Bird.Mode.Ground).OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        if (street != null) await Snap(me, "town_street", () => street.Centre, 8f);
        var aloft = life.Birds.Where(b => b.Town && b.State == Bird.Mode.Flying).OrderBy(b => b.Node.GlobalPosition.DistanceTo(me.GlobalPosition)).FirstOrDefault();
        if (aloft != null) await Snap(me, "town_flying", () => aloft.Centre, 30f);

        // A stands under a pigeon: B must see the dropping land on A
        Expect(await Heard("A", "under", 150), "A stands under a pigeon");
        Expect(await Heard("A", "splat", 70) || _heard.Any(l => l.Contains("PN A nosplat")), "A says how it went");
        bool seen = await Until(() => life.DropsOnOthers > drops, 5);
        Expect(seen, $"B saw a dropping land on A (peer {life.LastVictim})");
        if (seen && life.GetNodeOrNull<Node3D>("../Players/" + life.LastVictim) is { } a)
        {
            await Seconds(1.0);
            await Snap(me, "splat_on_A", () => a.GlobalPosition + Vector3.Up * 1.4f, 2.5f);
        }
        Say("seensplat");

        // A walks up to a town bird: B sees it go, and come down again somewhere
        Expect(await Heard("A", "approach", 150), "A picked a town bird to walk up to");
        var ap = (_heard.LastOrDefault(l => l.Contains("PN A approach")) ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var calm = ap.Length > 0 && int.TryParse(ap[^1], out int calmId) ? life.Birds.FirstOrDefault(b => b.Id == calmId) : null;
        if (calm != null) await Snap(me, "town_approach_before", calm);
        Say("watching");
        if (calm != null)
        {
            Expect(await Until(() => calm.State == Bird.Mode.Flying, 15), $"#{calm.Id} took off on B's screen ({calm.State})");
            await Snap(me, "town_approach_after", () => calm.Centre, 20f);
            bool down = await Until(() => calm.State is Bird.Mode.Perched or Bird.Mode.Ground || calm.Gone, 50) && !calm.Gone;
            Expect(down, $"#{calm.Id} landed again ({calm.State}, {Above(life, calm):F1} m up)");
            if (down) await Snap(me, "town_landed", calm);
        }
        Say("landed");
    }
}
