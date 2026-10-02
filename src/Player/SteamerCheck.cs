using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Avatar;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--steamercheck [shots]</c> on <c>--chunks fixture:lake</c> (#303), one client offline, headless
/// or windowed (<c>shots</c>: pictures in <c>test_output/steamer/&lt;style&gt;/</c>). With the real input
/// paths (the telegraph rung by the throttle and brake, the walk by <see cref="FootPlayer.WalkControls"/>):
/// <list type="bullet">
/// <item>calm: the steamer floats level at its draught; the berth search finds deep water off the
/// fixture's shelf; four pushes ring FULL AHEAD, it gathers way slowly to ~29 km/h with a wake; FULL
/// ASTERN stops it, long;</item>
/// <item>up from the wheel into the wheelhouse, the sea gamey: out and aft along the upper deck,
/// standing there in the swell (carried by the tilting deck, the stumble feels the heel), down the
/// stairs, along the side deck into the saloon, the gangway's gate opened by its button and out on
/// the plank, forward to the bow (38 m from the middle: the deck is still built), over the rail into
/// the water swimming, back up the gangway's ladder, and into a saloon seat and up again.</item>
/// </list>
/// Prints <c>[steamercheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class SteamerCheck : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--steamercheck");
            return i < 0 ? null : i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : "";
        }
    }

    private readonly Func<FootPlayer?> _local;
    private readonly bool _shots;
    private readonly string _role;

    /// <summary><c>walk</c>: straight from the calm float to the walk (no run ahead and astern).</summary>
    private readonly bool _walkOnly;
    private int _failures;
    private Action? _each;
    private Camera3D? _cam;
    private Func<Transform3D>? _follow;

    public SteamerCheck(string role, Func<FootPlayer?> local)
    {
        _local = local;
        _shots = role.Contains("shots") && DisplayServer.GetName() != "headless";
        _walkOnly = role.Contains("walk");
        _role = role;
        Name = "SteamerCheck";
    }

    public override void _Ready()
    {
        MouseCapture.Disabled = true;
        _ = Run();
    }

    public override void _PhysicsProcess(double delta) => _each?.Invoke();

    public override void _Process(double delta)
    {
        if (_cam != null && _follow != null) _cam.GlobalTransform = _follow();
    }

    private static void Log(string what) => GD.Print($"[steamercheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            Log($"FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.1)
        {
            if (done()) return true;
            await Wait(0.1);
        }
        return done();
    }

    private Net.ChatManager? Chat => GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) as Net.ChatManager;

    private static Vector3 At(double x, double y)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        WaterField.TryWorld(e + x, n + y, out var w);
        return w;
    }

    private static float Deg(float rad) => rad * 180f / Mathf.Pi;
    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    private const float East = -Mathf.Pi / 2f;

    private async Task SeaState(string state, float value)
    {
        Chat?.Send($"/seastate {state}");
        await Until(() => Mathf.Abs(WaterField.SeaState - value) < 1e-4f, 3);
    }

    // ---- the ship's frame ------------------------------------------------------------------------

    private VehicleBody? Parked() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.Steamer && !v.IsQueuedForDeletion());

    /// <summary>The steamer's frame as drawn: the driven one's visual, else the parked one's.</summary>
    private Node3D? Frame(FootPlayer me) => me.Ride == RideKind.Steamer ? me.Visual : Parked()?.Visual;

    /// <summary>A point of the ship (authored x: + port, height over the keel, station aft of the stem), world.</summary>
    private Vector3? Deck(FootPlayer me, float x, float y, float at) =>
        Frame(me) is { } f ? f.GlobalTransform * BoatMeshBuilder.Flip(new Vector3(x, y, SteamerMeshBuilder.Z(at))) : null;

    /// <summary>Where the player stands on the ship: authored x, height over the keel, station.</summary>
    private (float X, float Y, float At) Where(FootPlayer me)
    {
        if (Frame(me) is not { } f) return (float.NaN, float.NaN, float.NaN);
        var l = f.GlobalTransform.AffineInverse() * me.GlobalPosition;
        return (-l.X, l.Y, SteamerMeshBuilder.Bow + l.Z);
    }

    private string WhereText(FootPlayer me)
    {
        var w = Where(me);
        return F($"x {w.X:F2} y {w.Y:F2} at {w.At:F1}, on '{me.DeckOn}', floor {me.IsOnFloor()}");
    }

    /// <summary>Walks a path of (authored x, station) points on the deck at height <paramref name="y"/>; false if it never got there.</summary>
    private async Task<bool> Walk(FootPlayer me, (float X, float At)[] path, float y, bool run = false, double timeout = 40)
    {
        int i = 0;
        me.WalkControls = () =>
        {
            if (i >= path.Length || Deck(me, path[i].X, y, path[i].At) is not { } target) return (Vector3.Zero, false);
            var to = (target - me.GlobalPosition) with { Y = 0 };
            if (to.Length() < 0.35f) { i++; return (Vector3.Zero, false); }
            // the last stretch at an amble, easing in: running, a walker overshot the spot by a metre
            if (i == path.Length - 1 && to.Length() < 2.5f) return (to.Normalized() * Mathf.Clamp(to.Length() / 2.5f, 0.2f, 1f), false);
            return (to.Normalized(), run);
        };
        bool done = await Until(() => i >= path.Length, timeout);
        me.WalkControls = () => (Vector3.Zero, false);
        if (!done) Log($"  stuck walking to {path[Mathf.Min(i, path.Length - 1)]}: {WhereText(me)}, {me.WalkState}");
        return done;
    }

    // ---- the run -------------------------------------------------------------------------------

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                // the client starts in the fly camera: on foot
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        if (_shots)
        {
            AddChild(_cam = new Camera3D { Name = "SteamerCheckCamera", Fov = 60f, Far = 4000f });
            me.ViewForCheck(true);
        }
        if (_role.Contains("nyon")) { await Nyon(me); Finish(null); return; }
        if (!await Until(() => WaterField.TryGetStill(At(Lake.ShoreX + 700, 0), out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }
        Log(F($"on the lake fixture, wave clock {WaterField.Now:F1} s"));

        await SeaState("calm", 0f);
        Berth(me);
        if (!Expect2(me.SetRide(RideKind.Steamer), "picked the paddle steamer on the beach (RideKind 123)")) { Finish("no steamer"); return; }
        if (me.SteamerDriven is not { } steamer) { Finish("not in the steamer"); return; }

        await Calm(me, steamer);
        if (!_walkOnly)
        {
            await Ahead(me, steamer);
            await Astern(me, steamer);
        }
        if (!await UpFromTheWheel(me)) { Finish("never stood up into the wheelhouse"); return; }
        await SeaState("gamey", 1f);
        await UpperDeck(me);
        await StairsAndSaloon(me);
        await Gangway(me);
        await Bow(me);
        await Overboard(me);
        await Seat(me);
        await Boarding(me);
        Finish(null);
    }

    /// <summary>
    /// <c>nyon</c> (real tiles, <c>--at</c> the landing): the steamer the berth placed lies in deep water
    /// by the CGN pier and floats at its draught; its pictures from the lake, the town behind.
    /// </summary>
    private async Task Nyon(FootPlayer me)
    {
        VehicleBody? Berth() => VehicleManager.Instance?.GetNodeOrNull<VehicleBody>(SteamerBerth.BerthName);
        if (!await Until(() => Berth() != null, 90)) { Expect(false, "the steamer is placed at the Nyon landing"); return; }
        await Wait(12);
        var v = Berth()!;
        var chunks = me.Terrain!;
        chunks.TryGetWater(v.GlobalPosition, out float still, out _);
        chunks.TryGetHeight(v.GlobalPosition, out float bed);
        float draught = still - v.GlobalPosition.Y;
        var (e, n) = chunks.Origin!.ToLv95(v.GlobalPosition);
        Log(F($"at Nyon: LV95 {e:F0}/{n:F0}, heading {Mathf.RadToDeg(v.Rotation.Y):F0}°, draught {draught:F2} m, {still - bed:F1} m of water, keel {v.GlobalPosition.Y - bed:F1} m over the bed"));
        Expect(draught > 1.45f && draught < 1.9f, "floats at its draught");
        Expect(v.GlobalPosition.Y - bed > 0.3f, "clear of the lake bed");
        // from the open lake toward the town: the way the water deepens, 90 m off, 14 m up
        var offshore = Vector3.Zero;
        for (int j = 0; j < 16; j++)
        {
            var dir = new Vector3(Mathf.Cos(Mathf.Tau * j / 16f), 0, Mathf.Sin(Mathf.Tau * j / 16f));
            var p = v.GlobalPosition + dir * 150f;
            if (chunks.TryGetWater(p, out float s2, out _) && chunks.TryGetHeight(p, out float b2)) offshore += dir * Mathf.Max(0f, s2 - b2);
        }
        offshore = offshore.LengthSquared() > 1e-4f ? offshore.Normalized() : Vector3.Back;
        var side = offshore.Rotated(Vector3.Up, 0.45f);
        await Shot("parked_nyon", () =>
        {
            var target = v.GlobalPosition + Vector3.Up * 6f;
            var eye = v.GlobalPosition + side * 95f + Vector3.Up * 16f;
            return new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
        });
        await Shot("parked_nyon_close", () =>
        {
            var target = v.GlobalPosition + Vector3.Up * 4f;
            var eye = v.GlobalPosition + offshore.Rotated(Vector3.Up, -0.6f) * 50f + Vector3.Up * 7f;
            return new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
        });
    }

    private bool Expect2(bool ok, string what) { Expect(ok, what); return ok; }

    private void Finish(string? fatal)
    {
        _each = null;
        if (_local() is { } me) { me.RideControls = null; me.WalkControls = null; }
        if (fatal != null) { _failures++; Log($"FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>The berth search (<see cref="SteamerBerth.FindBerth"/>) from the fixture's shore: deep water past the shelf.</summary>
    private void Berth(FootPlayer me)
    {
        if (me.Terrain is not { } chunks) { Expect(false, "terrain for the berth search"); return; }
        var shore = At(Lake.ShoreX + 5, 200);
        var berth = SteamerBerth.FindBerth(chunks, shore);
        Expect(berth != null, berth is { } b
            ? F($"the berth search finds water to float it {b.FromLanding:F0} m off the shore ({b.Depth:F1} m deep, heading {Deg(b.Yaw):F0}°)")
            : "the berth search finds water to float it off the fixture's shore");
        if (berth is { } found)
            Expect(found.FromLanding > Lake.ShelfM - 30 && found.Depth >= SteamerLines.Draught + SteamerBerth.Clearance,
                "past the shelf, where the bed drops off");
    }

    /// <summary>Pulses an input <paramref name="n"/> times: the telegraph steps once per push.</summary>
    private async Task Ring(FootPlayer me, int n, bool ahead)
    {
        for (int k = 0; k < n; k++)
        {
            me.RideControls = () => new RideInput(ahead ? 1f : 0f, ahead ? 0f : 1f, 0f, false);
            await Wait(0.15);
            me.RideControls = () => new RideInput(0f, 0f, 0f, false);
            await Wait(0.15);
        }
    }

    private Func<RideInput> Hold(FootPlayer me, float yaw) => () =>
        new RideInput(0f, 0f, Mathf.Clamp(-3f * Mathf.AngleDifference(me.Rotation.Y, yaw), -1f, 1f), false);

    private async Task Calm(FootPlayer me, Steamer steamer)
    {
        var at = At(Lake.ShoreX + 600, 0);
        WaterField.TryLevelAt(at, out float level);
        me.PlaceBoat(at with { Y = level - 1.6f }, East);
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Wait(10);
        var s = me.BoatMotion;
        WaterField.TryLevelAt(me.GlobalPosition, out level);
        float draught = level - me.GlobalPosition.Y;
        Log(F($"calm, afloat: draught {draught:F2} m (La Suisse 1.68), pitch {Deg(s.Pitch):F2}°, roll {Deg(s.Roll):F2}°, drift {s.Velocity.Length():F2} m/s"));
        Expect(draught > 1.45f && draught < 1.9f, "floats at its draught");
        Expect(Mathf.Abs(Deg(s.Pitch)) < 1f && Mathf.Abs(Deg(s.Roll)) < 1f, "floats level in a calm");
        Expect(me.DeckSetsBuilt == 0, "its driver builds no decks of its own");
        await Shot("parked_calm", () => Look(me, side: -1f, back: -0.35f, up: 0.16f, distance: 0.75f));
    }

    private async Task Ahead(FootPlayer me, Steamer steamer)
    {
        await Ring(me, 4, ahead: true);
        Expect(steamer.Order == Telegraph.Max, $"four pushes ring {Telegraph.Name(steamer.Order)} (FULL AHEAD)");
        me.RideControls = Hold(me, East);
        float at10 = 0f, t = 0f;
        _each = () =>
        {
            t += 1f / Engine.PhysicsTicksPerSecond;
            if (t < 10f) at10 = me.BoatMotion.WaterSpeed;
        };
        for (int i = 0; i < 55; i++)
        {
            await Wait(1);
            if (i == 44) await Shot("underway_wake", () => Look(me, side: 0.75f, back: 1f, up: 0.38f, distance: 1.05f));
            if (i == 48 && _shots)
            {
                // the helmsman's own view, with the HUD: first person for the picture, not saved
                me.ViewForCheck(false);
                await Wait(0.5);
                await Shot("wheelhouse_hud", null);
                me.ViewForCheck(true);
            }
        }
        _each = null;
        float speed = me.BoatMotion.WaterSpeed;
        Log(F($"FULL AHEAD from rest: {at10 * 3.6f:F1} km/h after 10 s, {speed * 3.6f:F1} km/h after 55 s, shaft {me.BoatMotion.Shaft:F2}, {steamer.Rpm:F0} rpm"));
        Expect(at10 < 3f, "gathers way slowly");
        Expect(speed > 5.5f, "and runs at speed with the paddles turning");
    }

    private async Task Astern(FootPlayer me, Steamer steamer)
    {
        var from = me.GlobalPosition;
        float v0 = me.BoatMotion.WaterSpeed;
        await Ring(me, 8, ahead: false);
        Expect(steamer.Order == -Telegraph.Max, $"eight pulls ring {Telegraph.Name(steamer.Order)} (FULL ASTERN)");
        me.RideControls = Hold(me, East);
        double start = Time.GetTicksMsec() / 1000.0;
        bool stopped = await Until(() => me.BoatMotion.WaterSpeed < 0.1f, 120);
        float took = (float)(Time.GetTicksMsec() / 1000.0 - start);
        float run = MathX.FlatDistance(from, me.GlobalPosition);
        Log(F($"FULL ASTERN from {v0 * 3.6f:F0} km/h: stopped in {took:F0} s and {run:F0} m"));
        Expect(stopped && took > 15f && run > 60f, "a long stop");
        await Ring(me, 4, ahead: true);
        Expect(steamer.Order == 0, $"back to {Telegraph.Name(steamer.Order)}");
        me.RideControls = () => new RideInput(0f, 0f, 0f, false);
        await Until(() => me.BoatMotion.Velocity.Length() < 0.3f, 40);
    }

    private async Task<bool> UpFromTheWheel(FootPlayer me)
    {
        me.RideControls = null;
        me.ExitVehicle();
        bool aboard = await Until(() => me.Aboard && me.DeckOn.StartsWith("v:"), 8);
        Expect(aboard, $"up from the wheel: standing in the wheelhouse of the parked steamer ({WhereText(me)})");
        await Wait(1);
        var w = Where(me);
        Expect(Mathf.Abs(w.Y - SteamerMeshBuilder.UpperY) < 0.3f && w.At > SteamerMeshBuilder.HouseFrom && w.At < SteamerMeshBuilder.HouseTo,
            F($"on the wheelhouse floor ({w.Y:F2} m over the keel, station {w.At:F1})"));
        return aboard;
    }

    private async Task UpperDeck(FootPlayer me)
    {
        me.MaxDeckTilt = 0f;
        me.MaxDeckAccel = 0f;
        float u = SteamerMeshBuilder.UpperY;
        Expect(await Walk(me, new[] { (0f, 34.6f), (1.5f, 35.6f), (1.5f, 41f), (0f, 43f), (0f, 55f) }, u),
            $"out of the wheelhouse and aft along the upper deck ({WhereText(me)})");
        // standing in the swell: carried by the deck as it heaves, pitches and rolls
        float lo = 99f, hi = -99f, worst = 0f, heaveLo = 9999f, heaveHi = -9999f;
        string start = me.DeckOn;
        bool off = false;
        _each = () =>
        {
            var w = Where(me);
            lo = Mathf.Min(lo, w.Y); hi = Mathf.Max(hi, w.Y);
            worst = Mathf.Max(worst, Mathf.Abs(w.X) + Mathf.Abs(w.At - 55f));
            if (me.DeckOn != start) off = true;
            if (Parked() is { } p) { heaveLo = Mathf.Min(heaveLo, p.GlobalPosition.Y); heaveHi = Mathf.Max(heaveHi, p.GlobalPosition.Y); }
        };
        for (int i = 0; i < 20; i++)
        {
            await Wait(1);
            if (i == 10) await Shot("upper_deck_swell", () => OnShip(me, 0.3f, u + 1.75f, 61f, 0f, u + 1.3f, 40f));
        }
        _each = null;
        Log(F($"20 s standing on the upper deck, gamey: feet {lo - u:F2}..{hi - u:F2} m off the deck, wandered {worst:F2} m, ") +
            F($"deck tilt up to {Deg(me.MaxDeckTilt):F2}°, deck acceleration up to {me.MaxDeckAccel:F2} m/s², stumble {me.Stumble.Length():F2} m/s, the ship heaving {heaveHi - heaveLo:F2} m"));
        Expect(!off && me.Aboard, "stays aboard the whole time");
        Expect(lo - u > -0.1f && hi - u < 0.35f, "on the deck, neither through it nor thrown off it");
        Expect(me.MaxDeckTilt > Mathf.DegToRad(0.2f), "the deck heels and trims under the walker (the stumble feels it)");
        Expect(heaveHi - heaveLo > 0.08f, "the parked steamer rides the swell");
        Expect(worst < 2.5f, "and the walker is not slid across the deck");
    }

    private async Task StairsAndSaloon(FootPlayer me)
    {
        float u = SteamerMeshBuilder.UpperY, d = SteamerMeshBuilder.DeckY, sx = SteamerMeshBuilder.StairX;
        Expect(await Walk(me, new[] { (0f, 60.5f), (sx, 61.2f), (sx, 66f), (sx, 69.5f) }, u), $"down the stairs to the main deck ({WhereText(me)})");
        await Wait(0.5);
        Expect(Mathf.Abs(Where(me).Y - d) < 0.25f, F($"on the main deck at the stairs' foot ({Where(me).Y:F2})"));
        Expect(await Walk(me, new[] { (sx, 66f), (sx, 61.2f), (0f, 60.5f) }, d), $"and back up them ({WhereText(me)})");
        await Wait(0.5);
        Expect(Mathf.Abs(Where(me).Y - u) < 0.25f, F($"on the upper deck again ({Where(me).Y:F2})"));
        // down again, then forward under the upper deck between the two stairs, and along the side deck
        Expect(await Walk(me, new[] { (sx, 61.2f), (sx, 66f), (sx, 69.6f), (0f, 69.6f), (0f, 63f), (sx, 59f), (sx, 47f), (sx, 30f), (2.35f, 28.3f), (2.35f, 27f), (0.3f, 26.8f), (0.3f, 22f) }, d, run: true, timeout: 70),
            $"along the side deck past the paddle box and into the saloon ({WhereText(me)})");
        await Wait(0.5);
        var w = Where(me);
        Expect(Mathf.Abs(w.Y - d) < 0.25f && w.At > SteamerMeshBuilder.SaloonFrom && w.At < SteamerMeshBuilder.SaloonTo && Mathf.Abs(w.X) < SteamerMeshBuilder.SaloonHalf,
            F($"in the saloon ({w.X:F1}, station {w.At:F1})"));
        await Shot("saloon", () => OnShip(me, 0f, d + 1.65f, 27.6f, 0f, d + 1.2f, 14f));
    }

    private async Task Gangway(FootPlayer me)
    {
        float d = SteamerMeshBuilder.DeckY;
        Expect(await Walk(me, new[] { (0.3f, 26.8f), (2.35f, 27f), (2.35f, 28.3f), (2.9f, 30f), (3.5f, 42.9f) }, d, run: true), $"out to the port gangway ({WhereText(me)})");
        await Wait(0.5);
        var parked = Parked();
        if (me.ButtonInReach() == null && parked?.Visual is { } pv)
            foreach (var b in parked.Ride.Decks[0].Buttons)
                Log(F($"  button {b.Door} at {b.At} (node), chest {pv.GlobalTransform.AffineInverse() * (me.GlobalPosition + Vector3.Up * 1.1f)} (node), {(pv.GlobalTransform * b.At).DistanceTo(me.GlobalPosition + Vector3.Up * 1.1f):F2} m"));
        Expect(me.ButtonInReach() is { Door: 0 }, $"the gangway's button in reach ({me.ButtonInReach()?.Door})");
        Expect(me.TryInteract() && await Until(() => (parked?.BusDoors ?? 0) == 1, 3), $"E at the button opens the port gangway (gates {parked?.BusDoors})");
        Expect(await Walk(me, new[] { (3.5f, 44.2f), (5.3f, 44.2f) }, d - 0.2f), $"out on the plank ({WhereText(me)})");
        await Wait(0.5);
        Expect(me.Aboard && Where(me).Y < d - 0.05f, F($"on the plank, still aboard ({Where(me).Y:F2} over the keel)"));
        await Shot("gangway", () => OnShip(me, 16f, 4.6f, 52f, 4.2f, 3.8f, 44.2f));
        Expect(await Walk(me, new[] { (3.5f, 44.2f), (3.5f, 42.9f) }, d), $"back aboard ({WhereText(me)})");
    }

    private async Task Bow(FootPlayer me)
    {
        float d = SteamerMeshBuilder.DeckY;
        Expect(await Walk(me, new[] { (3.8f, 40f), (3.8f, 29f), (3.8f, 12f), (1.6f, 6.5f), (1.0f, 2.4f) }, d, run: true, timeout: 50), $"forward to the bow ({WhereText(me)})");
        await Wait(1);
        float fromMiddle = Parked() is { } p ? MathX.FlatDistance(p.GlobalPosition, me.GlobalPosition) : 0f;
        Expect(me.Aboard && me.DeckSetsBuilt > 0 && Mathf.Abs(Where(me).Y - d) < 0.25f,
            F($"at the bow, {fromMiddle:F1} m from the ship's middle: still aboard, on its deck ({me.DeckSetsBuilt} deck sets built)"));
        Expect(fromMiddle > 30f, "farther out than a bus's 30 m deck reach");
    }

    private async Task Overboard(FootPlayer me)
    {
        float d = SteamerMeshBuilder.DeckY, at = 12f;
        float rail = SteamerMeshBuilder.DeckHalf(SteamerMeshBuilder.Z(at)) - 0.05f;
        Expect(await Walk(me, new[] { (2.0f, 9.5f), (rail - 0.6f, at) }, d), $"to the rail on the foredeck ({WhereText(me)})");
        // over the rail: up on its cap, then a step out
        if (Deck(me, rail, d + SteamerMeshBuilder.RailHeight + 0.05f, at) is { } top) me.GlobalPosition = top;
        int i = 0;
        me.WalkControls = () => i++ < 30 && Deck(me, rail + 3f, d, at) is { } out_ ? (((out_ - me.GlobalPosition) with { Y = 0 }).Normalized(), false) : (Vector3.Zero, false);
        bool swimming = await Until(() => me.IsSwimming, 8);
        me.WalkControls = () => (Vector3.Zero, false);
        Expect(swimming && !me.Aboard, $"over the rail: in the water, swimming (#301), not carried ({WhereText(me)})");
        // back up the gangway's ladder
        if (Deck(me, SteamerMeshBuilder.DeckHalf(SteamerMeshBuilder.Z(44.2f)) + 1.8f, d, 44.2f) is { } foot) me.StartSwimmingAtSurface(foot);
        await Wait(0.5);
        Expect(me.IsSwimming && me.TryInteract(), "E swimming beside the gangway");
        bool aboard = await Until(() => me.Aboard && !me.IsSwimming, 6);
        Expect(aboard && Mathf.Abs(Where(me).Y - d) < 0.3f, $"climbs its ladder onto the deck ({WhereText(me)})");
    }

    private async Task Seat(FootPlayer me)
    {
        float d = SteamerMeshBuilder.DeckY;
        Expect(await Walk(me, new[] { (3.5f, 42.9f), (2.9f, 30f), (2.35f, 28.3f), (2.35f, 27f), (0.3f, 26.4f), (0.45f, 26f) }, d), $"back to the saloon's last row ({WhereText(me)})");
        Expect(me.TryInteract() && await Until(() => me.Ride == RideKind.Steamer && me.SeatIndex > 0, 5), $"E sits down (seat {me.SeatIndex}, the steamer taken driverless)");
        await Wait(2);
        bool up = me.TryInteract();
        for (int i = 0; i < 16 && !(me.Ride == RideKind.OnFoot && me.Aboard); i++)
        {
            await Wait(0.5);
            Log(F($"  standing up: ride {me.Ride}, at {me.GlobalPosition}, parked {Parked()?.GlobalPosition}, posed {Parked()?.Posed}, {WhereText(me)}, {me.WalkState}"));
        }
        Expect(up && me.Ride == RideKind.OnFoot && me.Aboard, $"E stands up into the saloon ({WhereText(me)})");
    }

    /// <summary>
    /// E from a quay beside the parked steamer, both ways of the setting "Board ships on deck": off (the
    /// default) it takes the wheel, as a bus; on, it puts the player on deck by the gangway.
    /// </summary>
    private async Task Boarding(FootPlayer me)
    {
        await SeaState("calm", 0f);
        await Wait(3);
        var settings = GameSettings.Current;
        bool was = settings.BoardShipsOnDeck;
        float d = SteamerMeshBuilder.DeckY, top = d - SteamerMeshBuilder.PlankDrop;
        float inner = SteamerMeshBuilder.PlankEdge + 0.5f, outer = inner + 7f;
        var size = new Vector3(outer - inner, 8f, 8.4f);
        var quay = new StaticBody3D { Name = "TestQuay" };
        quay.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        quay.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = size }, MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.52f) } });
        GetTree().CurrentScene.AddChild(quay);
        // on the quay at the port gangway, the ship as it lies now
        async Task<bool> OnQuay()
        {
            if (Frame(me) is not { } f) return false;
            var t = f.GlobalTransform;
            quay.GlobalTransform = new Transform3D(new Basis(Vector3.Up, t.Basis.GetEuler().Y),
                t * BoatMeshBuilder.Flip(new Vector3((inner + outer) * 0.5f, top - size.Y * 0.5f, SteamerMeshBuilder.Z(44.2f))));
            me.GlobalPosition = t * BoatMeshBuilder.Flip(new Vector3(SteamerMeshBuilder.PlankEdge + 1.6f, top + 0.05f, SteamerMeshBuilder.Z(44.2f)));
            me.Velocity = Vector3.Zero;
            await Wait(1.5);
            return !me.Aboard && me.Ride == RideKind.OnFoot;
        }

        settings.BoardShipsOnDeck = false;
        Expect(await OnQuay(), $"on a quay beside the port gangway ({WhereText(me)})");
        Expect(me.TryInteract() && await Until(() => me.Ride == RideKind.Steamer && me.SeatIndex == 0, 5),
            "\"Board ships on deck\" off (the default): E from the quay takes the wheel, as a bus");
        me.ExitVehicle();
        await Until(() => me.Aboard, 8);

        settings.BoardShipsOnDeck = true;
        Expect(await OnQuay(), $"back on the quay ({WhereText(me)})");
        bool aboard = me.TryInteract() && await Until(() => me.Ride == RideKind.OnFoot && me.Aboard, 6);
        await Wait(1);
        var w = Where(me);
        Expect(aboard && Mathf.Abs(w.Y - d) < 0.25f && w.At > SteamerMeshBuilder.GangFrom - 0.5f && w.At < SteamerMeshBuilder.GangTo + 0.5f,
            F($"\"Board ships on deck\" on: E from the quay puts the player on deck at the gangway ({WhereText(me)})"));
        await Shot("board_on_deck", () => OnShip(me, 2.4f, d + 1.7f, 38.8f, 3.9f, d + 0.9f, 44.4f));
        settings.BoardShipsOnDeck = was;
        quay.QueueFree();
    }

    // ---- pictures ---------------------------------------------------------------------------------

    /// <summary>A camera looking at the ship from its side (+1 starboard), behind, above, at a distance in ship lengths.</summary>
    private Transform3D Look(FootPlayer me, float side, float back, float up, float distance)
    {
        var body = (Node3D?)Parked() ?? me;
        var b = body.GlobalTransform.Basis;
        var right = (b.X with { Y = 0 }).Normalized();
        var aft = (b.Z with { Y = 0 }).Normalized();
        var target = body.GlobalPosition + Vector3.Up * 5f;
        var eye = target + (right * side + aft * back).Normalized() * (76f * distance) + Vector3.Up * (76f * distance * up);
        return new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
    }

    /// <summary>A camera fixed to the ship, from (x, y, station) toward (x, y, station), authored: the horizon tilts as the deck does.</summary>
    private Transform3D OnShip(FootPlayer me, float x, float y, float at, float tx, float ty, float tat)
    {
        if (Frame(me) is not { } f) return Transform3D.Identity;
        var t = f.GlobalTransform;
        var eye = t * BoatMeshBuilder.Flip(new Vector3(x, y, SteamerMeshBuilder.Z(at)));
        var target = t * BoatMeshBuilder.Flip(new Vector3(tx, ty, SteamerMeshBuilder.Z(tat)));
        return new Transform3D(Basis.LookingAt(target - eye, t.Basis.Y.Normalized()), eye);
    }

    /// <summary>A picture: from <paramref name="where"/> (followed while it settles), or through the player's own camera with the HUD when null.</summary>
    private async Task Shot(string name, Func<Transform3D>? where)
    {
        if (!_shots || _cam == null) return;
        var before = GetViewport().GetCamera3D();
        if (where != null)
        {
            _follow = where;
            _cam.GlobalTransform = where();
            _cam.MakeCurrent();
        }
        for (int i = 0; i < 6; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var args = OS.GetCmdlineUserArgs();
        int si = Array.IndexOf(args, "--style");
        string style = si >= 0 && si + 1 < args.Length ? args[si + 1].Replace('+', 'p').Replace('-', 'm') : "default";
        string dir = ProjectSettings.GlobalizePath($"res://test_output/steamer/{style}");
        System.IO.Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir, $"{name}.png");
        var error = GetViewport().GetTexture().GetImage().SavePng(file);
        Log($"shot {file}: {error}");
        _follow = null;
        if (where != null) before?.MakeCurrent();
    }
}
