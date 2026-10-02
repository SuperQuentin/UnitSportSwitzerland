using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Items;
using UnitSport.Terrain.Fixture;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--boatnet A|B</c> with <c>--connect</c> on <c>--chunks fixture:lake</c>, the server gamey with
/// an admin password (driven by <c>tools/boatnetcheck.sh</c>, #302): does another peer draw a boat
/// on the waves it draws?
/// <list type="bullet">
/// <item>A (admin) takes a speedboat out on the gamey lake: idling in the swell, then a slow run, then
/// it gets out and leaves the boat floating. Each time it measures how its own hull follows its own
/// waves (pitch against the surface's slope under the hull, its height over the surface) and says so.</item>
/// <item>B swims 30 m off and measures the same of A's copy against B's own copy of the waves at B's
/// own time: the copy rides the waves B draws (its pitch follows B's surface as A's follows A's, its
/// keel at A's depth under B's surface), and so does the boat left parked. Then B swims into the
/// parked boat's side and meets its hull where B draws it (#378).</item>
/// </list>
/// Windowed (<c>SHOTS=1</c>), B saves its view of A's boat to <c>test_output/boatnet_B_*.png</c>.
/// </summary>
public partial class BoatNetProbe : ChatProbe
{
    public static string? Role => RoleArg("--boatnet");

    public BoatNetProbe(ItemController items) : base(items, "boatnet", "BN", "boatnet_") { }
    public BoatNetProbe() : this(null!) { }

    protected override void Fail(string why) => Expect(false, why);

    private static Vector3 At(double x, double y)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        WaterField.TryWorld(e + x, n + y, out var w);
        return w;
    }

    private const double AX = Lake.ShoreX + 420, BX = Lake.ShoreX + 420, BY = -32;
    private static string F(float v) => v.ToString("F3", CultureInfo.InvariantCulture);
    private static float Deg(float r) => r * 180f / Mathf.Pi;

    public override async void _Ready()
    {
        _role = Role ?? "A";
        if (!await Joined(150, () => WaterField.TryGetStill(At(AX, 0), out _, out _)))
        {
            await Finish(0);
            return;
        }
        if (!await Until(() => WaterField.SeaState > 0.99f, 20)) Fail($"the server's gamey sea state never arrived ({WaterField.SeaState})");
        if (_role == "A") await RunA(Me!); else await RunB(Me!);
        await Finish(2.0);
    }

    /// <summary>The slope of the surface under a hull, fore and aft (rad, bow up +), and the mean surface, at wave time t.</summary>
    private static bool Surface(Boat boat, Vector3 body, float yaw, double t, out float pitch, out float mean)
    {
        pitch = mean = 0f;
        var along = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw)) * (boat.Spec.Length / 3f);
        if (!WaterField.TryLevelAt(body + along, t, out float bow) || !WaterField.TryLevelAt(body - along, t, out float stern)) return false;
        pitch = Mathf.Atan2(bow - stern, boat.Spec.Length * 2f / 3f);
        return boat.TrySurface(body, yaw, t, out mean);
    }

    /// <summary>
    /// Samples a hull for <paramref name="seconds"/>: its pitch against the surface's slope under it
    /// (correlation, rms of each) and its keel's depth under the mean surface (mean, spread).
    /// </summary>
    private async Task<(float Corr, float RmsSurface, float RmsHull, float Depth, float DepthSpread, int N)> Sample(
        Func<(Boat Boat, Vector3 Body, float Yaw, float Pitch)?> hull, double seconds)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        var depths = new List<float>();
        double end = Time.GetTicksMsec() / 1000.0 + seconds;
        while (Time.GetTicksMsec() / 1000.0 < end)
        {
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
            if (hull() is not { } h) continue;
            if (!Surface(h.Boat, h.Body, h.Yaw, WaterField.Now, out float slope, out float mean)) continue;
            xs.Add(slope);
            ys.Add(h.Pitch);
            depths.Add(mean - h.Body.Y);
        }
        int n = xs.Count;
        if (n < 10) return (0, 0, 0, 0, 0, n);
        float mx = xs.Average(), my = ys.Average();
        float sxy = 0, sxx = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            sxy += (xs[i] - mx) * (ys[i] - my);
            sxx += (xs[i] - mx) * (xs[i] - mx);
            syy += (ys[i] - my) * (ys[i] - my);
        }
        float corr = sxy / Mathf.Max(1e-9f, Mathf.Sqrt(sxx * syy));
        float md = depths.Average();
        float spread = Mathf.Sqrt(depths.Sum(d => (d - md) * (d - md)) / n);
        return (corr, Mathf.Sqrt(sxx / n), Mathf.Sqrt(syy / n), md, spread, n);
    }

    private string Line((float Corr, float RmsSurface, float RmsHull, float Depth, float DepthSpread, int N) s) =>
        string.Create(CultureInfo.InvariantCulture,
            $"corr {s.Corr:F3} surface {Deg(s.RmsSurface):F2} hull {Deg(s.RmsHull):F2} depth {s.Depth:F3} spread {s.DepthSpread:F3} n {s.N}");

    private static bool Parse(string line, string key, out float v)
    {
        v = 0;
        var w = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int i = Array.IndexOf(w, key);
        return i >= 0 && i + 1 < w.Length && float.TryParse(w[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    // ---- A: the boat --------------------------------------------------------------------------

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

        Expect(me.SetRide(RideKind.Speedboat), "A takes a speedboat");
        var start = At(AX, 0);
        WaterField.TryLevelAt(start, out float level);
        me.PlaceBoat(start with { Y = level - 0.25f }, Mathf.Pi);   // bow north, beam to the swell
        (Boat, Vector3, float, float)? Own() => me.Vehicle is Boat b ? (b, me.GlobalPosition, me.Rotation.Y, b.State.Pitch) : null;

        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Seconds(4);
        foreach (var (what, throttle) in new[] { ("idle", 0f), ("running", 0.22f) })
        {
            me.RideControls = () => new RideInput(throttle, 0f, 0f, false);
            await Seconds(2);
            Say($"{what} go");
            var s = await Sample(Own, 8);
            Say($"{what} {Line(s)}");
            await Heard("B", $"seen {what}", 15);
        }

        // left floating: a parked boat, simulated here, drawn by B from what it is sent
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Until(() => me.BoatMotion.Velocity.Length() < 0.5f, 20);
        me.RideControls = null;
        me.ExitVehicle();
        await Seconds(2);
        VehicleBody? Parked()
        {
            if (VehicleManager.Instance is not { } vehicles) return null;
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody { Kind: RideKind.Speedboat } v && v.IsMultiplayerAuthority()) return v;
            return null;
        }
        Expect(Parked() != null, "A left the boat floating (an admin may park it)");
        if (Parked() is { } left)
            GD.Print($"{Log} over the side: swimming {me.IsSwimming}, {MathX.FlatDistance(me.GlobalPosition, left.GlobalPosition):F1} m from the boat, " +
                $"{me.GlobalPosition.Y - left.GlobalPosition.Y:F2} m above its keel");
        Say("parked go");
        var p = await Sample(() => Parked() is { } v ? ((Boat)v.Ride, v.GlobalPosition, v.Rotation.Y, ((Boat)v.Ride).State.Pitch) : null, 8);
        Say($"parked {Line(p)}");
        if (Parked() is { } still)
            GD.Print($"{Log} after: swimming {me.IsSwimming}, {MathX.FlatDistance(me.GlobalPosition, still.GlobalPosition):F1} m from the boat, " +
                $"{me.GlobalPosition.Y - still.GlobalPosition.Y:F2} m above its keel");
        await Heard("B", "seen parked", 15);
        // B swims into it (#378): kept here, floating, until B has touched it
        await Heard("B", "touched", 60);
    }

    // ---- B: the watcher ------------------------------------------------------------------------

    private async Task RunB(FootPlayer me)
    {
        if (!await Heard("A", "hello", 60)) { Fail("A never said hello"); return; }
        Expect(me.StartSwimmingAtSurface(At(BX, BY)), "B is in the water, 30 m off");
        await Seconds(1.0);
        Say("ready");

        // A's copy here, drawn: its node (the keel under the centre of mass) and the pose it is drawn in
        FootPlayer? Copy()
        {
            foreach (var node in GetTree().GetNodesInGroup(FootPlayer.Group))
                if (node is FootPlayer p && p != me && !p.IsMultiplayerAuthority() && p.Ride == RideKind.Speedboat) return p;
            return null;
        }
        var probe = Boat.For(RideKind.Speedboat)!;
        (Boat, Vector3, float, float)? Drawn() => Copy() is { } c
            ? (probe, c.GlobalPosition, c.GlobalRotation.Y, Mathf.Asin(Mathf.Clamp(-c.BodyPose.Basis.Z.Y, -1f, 1f))) : null;
        await Compare("idle", Drawn);
        await Compare("running", Drawn);

        VehicleBody? Parked()
        {
            if (VehicleManager.Instance is not { } vehicles) return null;
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody { Kind: RideKind.Speedboat } v && !v.IsMultiplayerAuthority()) return v;
            return null;
        }
        // a parked copy: where this peer draws it (VehicleBody.DrawBoat): its own waves plus the sent height
        await Compare("parked", () => Parked() is { } v
            ? (probe, v.GlobalPosition with { Y = probe.RemoteY(v.GlobalPosition, v.Rotation.Y, v.Heave) }, v.Rotation.Y,
                new BoatState { Attitude = v.Tilt }.Pitch) : null);
        await Touch(me, Parked());
    }

    /// <summary>
    /// #378: B swims into the side of A's parked boat, a copy here, and meets its hull where B draws it:
    /// on B's own waves at the sent height, at the sent attitude (eased), not at the copy's level body.
    /// </summary>
    private async Task Touch(FootPlayer me, VehicleBody? copy)
    {
        if (copy == null) { Fail("A's parked boat is not here to swim to"); Say("touched"); return; }
        bool shots = DisplayServer.GetName() != "headless";
        if (shots) HullTouch.Overlay(copy);
        var swim = HullTouch.Swim(this, me, copy, 10);
        if (shots)
        {
            await Until(() => !IsInstanceValid(copy) || me.GetSlideCollisionCount() > 0
                && HullTouch.Hull(copy, out _, out var box, out _) && Mathf.RadToDeg(box.Basis.Y.Normalized().AngleTo(Vector3.Up)) > 3f, 6);
            if (IsInstanceValid(copy))
            {
                // from out on the water past the swimmer, a little above it, at the hull it touches
                var before = GetViewport().GetCamera3D();
                var cam = new Camera3D { Fov = 55f };
                AddChild(cam);
                var across = (copy.GlobalTransform.Basis.X with { Y = 0 }).Normalized();
                if ((me.GlobalPosition - copy.GlobalPosition).Dot(across) < 0f) across = -across;
                var aft = (copy.GlobalTransform.Basis.Z with { Y = 0 }).Normalized();
                var target = (me.GlobalPosition + copy.GlobalPosition) * 0.5f + Vector3.Up * 1.2f;
                var eye = target + across * 6.5f + aft * 3.5f + Vector3.Up * 2.2f;
                cam.GlobalTransform = new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
                cam.MakeCurrent();
                for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
                GD.Print($"{Log} shot {Shot("B_hull_touch")}");
                before?.MakeCurrent();
                cam.QueueFree();
            }
        }
        var touch = await swim;
        GD.Print($"{Log} swimming into A's parked boat here, gamey: {touch}");
        Expect(touch.Frames > 100 && touch.WorstPose < 0.03f, "its collision box is posed as B draws it (B's waves, A's height over them and attitude)");
        Expect(touch.MaxTilt > 1.5f, $"the box pitches and rolls with it here ({touch.MaxTilt:F1}°)");
        // a copy's body follows the 20 Hz stream, swept there by Jolt over a step, its shape re-posed each frame: a few cm
        Expect(touch.Contacts > 10 && touch.WorstOff < 0.12f, "B meets the hull where B draws it");
        Expect(touch.Deepest < 2.1f && touch.UnderFor < 1.5f, "and is not pushed under by it");
        Say("touched");
    }

    private async Task Compare(string what, Func<(Boat, Vector3, float, float)?> drawn)
    {
        if (!await Heard("A", $"{what} go", 60)) { Fail($"A never said '{what} go'"); return; }
        var b = await Sample(drawn, 8);
        if (!await Until(() => _heard.Any(l => l.Contains($"BN A {what} corr")), 20)) { Fail($"A never measured '{what}'"); return; }
        string a = _heard.Last(l => l.Contains($"BN A {what} corr"));
        Parse(a, "corr", out float ac);
        Parse(a, "depth", out float ad);
        Parse(a, "surface", out float asurf);
        GD.Print($"{Log} {what}: A's own boat on A's waves: {a[(a.IndexOf("corr"))..]}");
        GD.Print($"{Log} {what}: A's boat here, on B's waves: {Line(b)}");
        Expect(b.N > 100, $"{what}: A's boat is here ({b.N} frames)");
        Expect(Deg(b.RmsSurface) > 0.5f && Mathf.Abs(Deg(b.RmsSurface) - asurf) < 0.6f,
            $"{what}: the swell under it is the same on both peers ({Deg(b.RmsSurface):F2}° here, {asurf:F2}° at A)");
        Expect(b.Corr > 0.5f && b.Corr > ac - 0.25f,
            $"{what}: its pitch follows B's waves as A's follows A's (corr {b.Corr:F2} here, {ac:F2} at A)");
        Expect(Mathf.Abs(b.Depth - ad) < 0.06f && b.Depth > 0.02f && b.Depth < 0.6f,
            $"{what}: its keel is as deep under B's surface as under A's ({b.Depth:F3} m here, {ad:F3} m at A)");
        if (DisplayServer.GetName() != "headless" && Me is { } me && drawn() is { } d)
        {
            // from a little above the swimmer (its eye is in the swell), 15 m off the boat, looking at it
            var before = GetViewport().GetCamera3D();
            var cam = new Camera3D { Fov = 55f };
            AddChild(cam);
            var boatAt = d.Item2 + Vector3.Up * 0.8f;
            var off = (me.GlobalPosition - boatAt) with { Y = 0 };
            var eye = boatAt + (off.LengthSquared() > 1f ? off.Normalized() : Vector3.Back) * 15f + Vector3.Up * 2.5f;
            cam.GlobalTransform = new Transform3D(Basis.LookingAt(boatAt - eye, Vector3.Up), eye);
            cam.MakeCurrent();
            for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GD.Print($"{Log} shot {Shot("B_" + what)}");
            before?.MakeCurrent();
            cam.QueueFree();
        }
        Say($"seen {what}");
    }
}
