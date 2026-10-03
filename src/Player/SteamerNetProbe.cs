using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Fixture;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--steamernet A|B</c> with <c>--connect</c> on <c>--chunks fixture:lake</c>, the server calm with
/// an admin password (driven by <c>tools/steamernetcheck.sh</c>, #303): walking aboard the paddle
/// steamer another player drives, through a gamey swell, on both peers.
/// <list type="bullet">
/// <item>A (admin) takes the steamer out to deep water and opens its gangways; once B is aboard it
/// shuts them, makes the lake gamey and rings FULL AHEAD; under way it measures where it draws B on
/// its own (pitching, rolling) ship, then watches B go over the rail and swim, then climb back up
/// the boarding ladder on its hull (#384) as the ship goes on: B in its climbing pose, then on deck.</item>
/// <item>B builds itself a quay alongside A's steamer (piers are a follow-up), walks over the
/// gangway's plank aboard, along the main deck, up the stairs, aft on the upper deck, and stands
/// there under way; it measures where it stands on its copy of A's ship. Then over the rail, and
/// from the water up the hull's ladder under way, over the rail onto the deck.</item>
/// </list>
/// Both peers must agree where B is on the ship (the means of the two measures within 0.3 m), and B
/// must be drawn on A's deck, steady, never through it. Windowed (<c>SHOTS=1</c>), A saves its view
/// of B on deck to <c>test_output/steamer/&lt;style&gt;/remote_passenger.png</c>.
/// </summary>
public partial class SteamerNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--steamernet");

    public SteamerNetProbe(ItemController items) : base(items, "steamernet", "SN", "steamernet_") { }
    public SteamerNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static Vector3 At(double x, double y)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        WaterField.TryWorld(e + x, n + y, out var w);
        return w;
    }

    private const double AX = Lake.ShoreX + 450;
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private const float North = 0f;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => WaterField.TryGetStill(At(AX, 0), out _, out _)))
        {
            await Finish(0);
            return;
        }
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    // ---- the ship's frame ------------------------------------------------------------------------

    private static Vector3 Point(Node3D frame, float x, float y, float at) =>
        frame.GlobalTransform * BoatMeshBuilder.Flip(new Vector3(x, y, SteamerMeshBuilder.Z(at)));

    /// <summary>A world point on the ship as (authored x, height over the keel, station).</summary>
    private static Vector3 OnShip(Node3D frame, Vector3 world)
    {
        var l = frame.GlobalTransform.AffineInverse() * world;
        return new Vector3(-l.X, l.Y, SteamerMeshBuilder.Bow + l.Z);
    }

    private async Task<bool> Walk(FootPlayer me, Func<Node3D?> frame, (float X, float At)[] path, float y, double timeout = 60)
    {
        int i = 0;
        me.WalkControls = () =>
        {
            if (i >= path.Length || frame() is not { } f) return (Vector3.Zero, false);
            var to = (Point(f, path[i].X, y, path[i].At) - me.GlobalPosition) with { Y = 0 };
            if (to.Length() < 0.35f) { i++; return (Vector3.Zero, false); }
            if (i == path.Length - 1 && to.Length() < 2.5f) return (to.Normalized() * Mathf.Clamp(to.Length() / 2.5f, 0.2f, 1f), false);
            return (to.Normalized(), false);
        };
        bool done = await Until(() => i >= path.Length, timeout);
        me.WalkControls = () => (Vector3.Zero, false);
        if (!done && frame() is { } g) GD.Print($"{Log} stuck at {OnShip(g, me.GlobalPosition)} going to {path[Mathf.Min(i, path.Length - 1)]}, on '{me.DeckOn}', {me.WalkState}");
        return done;
    }

    /// <summary>Samples a point on the ship for <paramref name="seconds"/>: its mean (authored x, height, station), spread, frames, and frames off the deck.</summary>
    private async Task<(Vector3 Mean, float Spread, int N, int Off)> Sample(Func<(Node3D Frame, Vector3 World, bool Aboard)?> where, double seconds)
    {
        var all = new List<Vector3>();
        int off = 0;
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (Time.GetTicksMsec() / 1000.0 < end)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            if (where() is not { } w) { off++; continue; }
            if (!w.Aboard) off++;
            all.Add(OnShip(w.Frame, w.World));
        }
        if (all.Count == 0) return (Vector3.Zero, 99f, 0, off);
        var mean = Vector3.Zero;
        foreach (var p in all) mean += p;
        mean /= all.Count;
        float spread = 0f;
        foreach (var p in all) spread += (p - mean).LengthSquared();
        return (mean, Mathf.Sqrt(spread / all.Count), all.Count, off);
    }

    private string Line(string what, (Vector3 Mean, float Spread, int N, int Off) s) =>
        F($"{what} x {s.Mean.X:F3} y {s.Mean.Y:F3} at {s.Mean.Z:F3} spread {s.Spread:F3} n {s.N} off {s.Off}");

    private static bool Parse(string line, string key, out float v)
    {
        v = 0;
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.IndexOf(w, key);
        return i >= 0 && i + 1 < w.Length && float.TryParse(w[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    private Vector3? Heard3(string role, string what)
    {
        var line = _heard.LastOrDefault(l => l.Contains($"SN {role} {what} x "));
        if (line == null) return null;
        Parse(line, "x", out float x); Parse(line, "y", out float y); Parse(line, "at", out float at);
        return new Vector3(x, y, at);
    }

    // ---- A: the bridge --------------------------------------------------------------------------

    private async Task RunA(FootPlayer me)
    {
        Chat?.Send("/login test");
        await Seconds(1.5);
        bool ready = false;
        for (int i = 0; i < 40 && !ready; i++)
        {
            Say("hello");
            ready = await Heard("B", "ready", 3);
        }
        if (!ready) { Fail("B never got ready"); return; }

        Expect(me.SetRide(RideKind.Steamer), "A takes the paddle steamer");
        if (me.SteamerDriven is not { } steamer) { Fail("A is not on the steamer's bridge"); return; }
        var start = At(AX, 0);
        WaterField.TryLevelAt(start, out float level);
        me.PlaceBoat(start with { Y = level - 1.6f }, North);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(8);
        steamer.DoorsOpen = 3;
        Say("berthed");
        if (!await Heard("B", "aboard", 180)) { Fail("B never got aboard"); return; }
        steamer.DoorsOpen = 0;

        // a gamey lake, FULL AHEAD (four pushes of the lever), holding north
        Chat?.Send("/seastate gamey");
        Expect(await Until(() => WaterField.SeaState > 0.99f, 15), "the lake is gamey");
        for (int k = 0; k < 4; k++)
        {
            me.RideControls = () => new RideInput(1f, 0f, 0f, false);
            await Seconds(0.15);
            me.RideControls = () => new RideInput(0f, 0f, 0f, false);
            await Seconds(0.15);
        }
        Expect(steamer.Order == Telegraph.Max, $"the telegraph at {Telegraph.Name(steamer.Order)}");
        me.RideControls = () => new RideInput(0f, 0f, Mathf.Clamp(-3f * Mathf.AngleDifference(me.Rotation.Y, North), -1f, 1f), false);
        await Seconds(30);
        GD.Print(F($"{Log} under way at {me.BoatMotion.WaterSpeed * 3.6f:F1} km/h, pitch {Mathf.RadToDeg(me.BoatMotion.Pitch):F2}°, roll {Mathf.RadToDeg(me.BoatMotion.Roll):F2}°"));

        // where A draws B on A's own ship, as it pitches and rolls
        FootPlayer? B() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>().FirstOrDefault(p => p != me);
        Say("sample go");
        float pitch = 0f, roll = 0f;
        var s = await Sample(() =>
        {
            pitch = Mathf.Max(pitch, Mathf.Abs(Mathf.RadToDeg(me.BoatMotion.Pitch)));
            roll = Mathf.Max(roll, Mathf.Abs(Mathf.RadToDeg(me.BoatMotion.Roll)));
            return B() is { } b && me.Visual is { } v ? (v, b.GlobalPosition, b.DeckOn == me.Name.ToString()) : null;
        }, 12);
        Say(Line("sample", s));
        GD.Print(F($"{Log} the ship under B pitched up to {pitch:F2}°, rolled up to {roll:F2}°"));
        Expect(s.N > 200 && s.Off == 0, F($"B is drawn aboard A's ship every frame ({s.N} frames, {s.Off} not)"));
        Expect(Mathf.Abs(s.Mean.Y - SteamerMeshBuilder.UpperY) < 0.15f, F($"on its upper deck ({s.Mean.Y:F2} m over the keel)"));
        Expect(s.Spread < 0.25f, F($"steady on it, not shaken about ({s.Spread:F3} m)"));
        Expect(pitch + roll > 0.3f, "while the swell rocks the ship");
        if (await Until(() => Heard3("B", "sample") != null, 20) && Heard3("B", "sample") is { } b0)
        {
            float d = b0.DistanceTo(s.Mean);
            GD.Print(F($"{Log} B's own measure {b0}, A's {s.Mean}: {d:F3} m apart"));
            Expect(d < 0.3f, F($"both peers agree where B stands on the ship ({d:F3} m apart)"));
        }
        else Fail("B never said where it stands");

        if (DisplayServer.GetName() != "headless" && me.Visual is { } ship && B() is { } passenger)
        {
            var before = GetViewport().GetCamera3D();
            var cam = new Camera3D { Fov = 60f, Far = 4000f };
            AddChild(cam);
            float u = SteamerMeshBuilder.UpperY;
            void Place()
            {
                var t = ship.GlobalTransform;
                var eye = Point(ship, -2.6f, u + 2.0f, 50.0f);
                cam.GlobalTransform = new Transform3D(Basis.LookingAt(passenger.GlobalPosition + Vector3.Up * 1.1f - eye, t.Basis.Y.Normalized()), eye);
            }
            Place();
            cam.MakeCurrent();
            for (int i = 0; i < 6; i++) { Place(); await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); }
            var args = OS.GetCmdlineUserArgs();
            int si = Array.IndexOf(args, "--style");
            string style = si >= 0 && si + 1 < args.Length ? args[si + 1] : "default";
            string dir = ProjectSettings.GlobalizePath($"res://test_output/steamer/{style}");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "remote_passenger.png");
            GD.Print($"{Log} shot {file}: {GetViewport().GetTexture().GetImage().SavePng(file)}");
            before?.MakeCurrent();
            cam.QueueFree();
        }

        Say("overboard");
        bool swimming = await Until(() => B() is { IsSwimming: true, DeckOn: "" }, 20);
        var bb = B();
        float fromShip = bb != null ? MathX.FlatDistance(bb.GlobalPosition, me.GlobalPosition) : -1f;
        WaterField.TryLevelAt(bb?.GlobalPosition ?? me.GlobalPosition, out float surface);
        Expect(swimming, F($"A sees B over the rail, swimming, off the deck ({fromShip:F0} m from the ship's middle, {(bb?.GlobalPosition.Y ?? 0) - surface:F2} m from the surface)"));
        Expect(swimming && Mathf.Abs((bb?.GlobalPosition.Y ?? 0) - surface + 1.4f) < 1.5f, "in the water, not carried along on the deck");

        // back up the ladder on the hull, the ship going on (#384): A sees B climbing it, then aboard
        if (await Heard("B", "climbing", 40))
        {
            bool climbing = await Until(() => B() is { PoseKind: FootPlayer.PoseClimb }, 6);
            var onLadder = B();
            float off = onLadder != null ? MathX.FlatDistance(onLadder.GlobalPosition, me.GlobalPosition) : -1f;
            Expect(climbing, F($"A sees B climbing the hull's ladder ({off:F0} m from the ship's middle)"));
            bool aboard = await Until(() => B() is { DeckOn: { Length: > 0 } on } && on == me.Name.ToString(), 20);
            Expect(aboard, $"and then B on A's deck (DeckOn '{B()?.DeckOn}')");
        }
        else Fail("B never got onto the ladder");
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        steamer.Order = 0;
        Say("done");
        await Heard("B", "bye", 20);
    }

    // ---- B: the passenger ------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Say("ready");
        if (!await Heard("A", "berthed", 150)) { Fail("A never berthed"); return; }
        FootPlayer? A() => GetTree().GetNodesInGroup(FootPlayer.Group).OfType<FootPlayer>()
            .FirstOrDefault(p => p != me && p.Ride == RideKind.Steamer && p.Visual != null);
        Node3D? Ship() => A()?.Visual;
        if (!await Until(() => Ship() != null, 20)) { Fail("A's steamer is not here"); return; }
        await Seconds(2);
        var ship = Ship()!;
        Expect((A()!.BusDoors & 1) != 0, $"A's port gangway is open here (gates {A()!.BusDoors})");

        // a quay alongside the port gangway, its top at the plank's foot (piers are a follow-up)
        float d = SteamerMeshBuilder.DeckY, top = d - SteamerMeshBuilder.PlankDrop;
        float inner = SteamerMeshBuilder.PlankEdge + 0.5f, outer = inner + 7f;
        var size = new Vector3(outer - inner, 8f, 8.4f);
        var centre = Point(ship, (inner + outer) * 0.5f, top - size.Y * 0.5f, 44.2f);
        var yaw = ship.GlobalTransform.Basis.GetEuler().Y;
        var quay = new StaticBody3D { Name = "TestQuay" };
        quay.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        quay.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.52f) } });
        GetTree().CurrentScene.AddChild(quay);
        quay.GlobalTransform = new Transform3D(new Basis(Vector3.Up, yaw), centre);
        me.GlobalPosition = Point(ship, outer - 1.5f, top + 0.05f, 44.2f);
        me.Velocity = Vector3.Zero;
        await Seconds(1.5);
        GD.Print($"{Log} on the quay: {OnShip(ship, me.GlobalPosition)}, floor {me.IsOnFloor()}");

        // over the plank and aboard, along the main deck, up the stairs, aft on the upper deck
        bool walked = await Walk(me, Ship, new[] { (SteamerMeshBuilder.PlankEdge + 0.5f, 44.2f), (3.5f, 44.2f) }, d);
        Expect(walked && await Until(() => me.DeckOn == A()?.Name.ToString(), 5), $"B walks over the gangway's plank aboard A's steamer (on '{me.DeckOn}')");
        float u = SteamerMeshBuilder.UpperY, sx = SteamerMeshBuilder.StairX;
        walked = await Walk(me, Ship, new[] { (2.9f, 47f), (2.9f, 59f), (0f, 63f), (0f, 69.6f), (sx, 69.6f), (sx, 66f), (sx, 61.2f), (0.5f, 58f), (0f, 55f) }, d, 90);
        await Seconds(1);
        var here = OnShip(Ship()!, me.GlobalPosition);
        Expect(walked && Mathf.Abs(here.Y - u) < 0.2f && me.Aboard, F($"up the stairs to the upper deck ({here}, on '{me.DeckOn}')"));
        Say("aboard");

        if (!await Heard("A", "sample go", 120)) { Fail("A never sampled"); return; }
        me.MaxDeckTilt = 0f;
        var s = await Sample(() => Ship() is { } f ? (f, me.GlobalPosition, me.Aboard) : null, 12);
        Say(Line("sample", s));
        Expect(s.Off == 0 && Mathf.Abs(s.Mean.Y - u) < 0.15f, F($"B rides standing on the upper deck under way ({s.N} frames, {s.Off} off, {s.Mean.Y:F2} m over the keel)"));
        GD.Print(F($"{Log} the deck under B tilted up to {Mathf.RadToDeg(me.MaxDeckTilt):F2}°, stumble {me.Stumble.Length():F2} m/s"));
        if (await Until(() => Heard3("A", "sample") != null, 20) && Heard3("A", "sample") is { } a0)
        {
            float dist = a0.DistanceTo(s.Mean);
            Expect(dist < 0.3f, F($"both peers agree where B stands on the ship ({dist:F3} m apart)"));
        }
        else Fail("A never said where it draws B");

        if (!await Heard("A", "overboard", 60)) { Fail("A never said overboard"); return; }
        // over the rail on the upper deck, to port: up on its handrail, a step out
        float rail = SteamerMeshBuilder.UpperHalf - 0.05f;
        await Walk(me, Ship, new[] { (rail - 0.5f, 53.5f) }, u, 20);
        if (Ship() is { } g) me.GlobalPosition = Point(g, rail, u + 1.08f, 53.5f);
        int steps = 0;
        me.WalkControls = () => steps++ < 40 && Ship() is { } f
            ? (((Point(f, rail + 4f, u, 53.5f) - me.GlobalPosition) with { Y = 0 }).Normalized(), false) : (Vector3.Zero, false);
        bool swimming = await Until(() => me.IsSwimming, 10);
        me.WalkControls = null;
        Expect(swimming && !me.Aboard, $"B goes over the rail into the water, swimming (#301), the ship going on without it");
        Say("swimming");
        // by the ship's ladder (#384), as it goes on: onto it at once, and up
        await Until(() => false, 3);
        if (Ship() is { } h)
        {
            var by = Point(h, SteamerMeshBuilder.LadderX + 1.0f, SteamerMeshBuilder.LadderFoot + 0.7f, SteamerMeshBuilder.LadderAt);
            me.StartSwimmingAtSurface(by);
            bool on = me.TryInteract() && me.OnShipLadder;
            Expect(on, $"B swimming by the hull's ladder gets onto it ({me.GlobalPosition})");
            if (on)
            {
                me.ForceLadderClimb = 1f;
                Say("climbing");
                bool up = await Until(() => me.Aboard && !me.OnShipLadder, 12);
                me.ForceLadderClimb = null;
                Expect(up, $"B climbs it over the rail onto the deck, under way (aboard '{me.DeckOn}')");
            }
        }
        await Heard("A", "done", 30);
        Say("bye");
        quay.QueueFree();
    }
}
