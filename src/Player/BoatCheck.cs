using System.Globalization;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;
using UnitSport.Vehicles;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--boatcheck jetski|speedboat[,shots]</c> on <c>--chunks fixture:lake</c> (#302), headless or
/// windowed (<c>shots</c>: pictures in <c>test_output/boats/</c>). With the real input path
/// (<see cref="FootPlayer.RideControls"/>) on the real wave field:
/// <list type="bullet">
/// <item>calm: it floats level at its draft, still; flat out east it climbs the hump onto the
/// plane (time to plane, top speed); full helm at speed (turning circle, no capsize); slow into
/// the beach, it runs aground and stops;</item>
/// <item>gamey: flat out through the swell (pitch, air, roll; a jetski may throw its rider), then
/// left parked: it floats on the waves and drifts; calm again, it sleeps; and it is claimed back.</item>
/// </list>
/// Prints <c>[boatcheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class BoatCheck : Node
{
    public static string? Role
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--boatcheck");
            return i >= 0 ? (i + 1 < args.Length ? args[i + 1] : "jetski") : null;
        }
    }

    private readonly Func<FootPlayer?> _local;
    private readonly RideKind _kind;
    private readonly bool _shots;
    private readonly string _name;
    private int _failures;
    private Action? _each;
    private Camera3D? _cam;
    private Func<Transform3D>? _follow;

    public BoatCheck(string role, Func<FootPlayer?> local)
    {
        _local = local;
        var parts = role.Split(',');
        _name = parts[0] == "speedboat" ? "speedboat" : "jetski";
        _kind = _name == "speedboat" ? RideKind.Speedboat : RideKind.Jetski;
        _shots = parts.Length > 1 && parts[1] == "shots" && DisplayServer.GetName() != "headless";
        Name = "BoatCheck";
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

    private static void Log(string what) => GD.Print($"[boatcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[boatcheck] FAIL {what}");
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

    /// <summary>A point of the course (metres from the start, X east, Y north) in world space at altitude y.</summary>
    private static Vector3 At(double x, double y, float alt = 0f)
    {
        var (e, n) = SpawnPoint.ParseTarget();
        WaterField.TryWorld(e + x, n + y, out var w);
        return w with { Y = alt };
    }

    private static float Deg(float rad) => rad * 180f / Mathf.Pi;
    private const float East = -Mathf.Pi / 2f, West = Mathf.Pi / 2f;

    private async Task SeaState(string state, float value)
    {
        Chat?.Send($"/seastate {state}");
        await Until(() => Mathf.Abs(WaterField.SeaState - value) < 1e-4f, 3);
    }

    /// <summary>The helm: throttle, astern, steer, or hold a heading by the helm.</summary>
    private static Func<RideInput> Helm(FootPlayer me, float throttle, float steer = 0f, float? hold = null, float reverse = 0f) => () =>
    {
        float s = steer;
        if (hold is { } yaw) s = Mathf.Clamp(-2f * Mathf.AngleDifference(me.Rotation.Y, yaw), -1f, 1f);
        return new RideInput(throttle, reverse, s, false);
    };

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor()) break;
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        if (!await Until(() => WaterField.TryGetStill(At(Lake.ShoreX + 600, 0), out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }
        Log($"{_name} on the lake fixture, wave clock {WaterField.Now:F1} s");

        await SeaState("calm", 0f);
        Expect(me.SetRide(_kind), $"picked the {_name} on the beach");
        if (me.Vehicle is not Boat boat) { Finish("not in a boat"); return; }
        var spec = boat.Spec;
        if (_shots) AddChild(_cam = new Camera3D { Name = "BoatCheckCamera", Fov = 60f });

        await Calm(me, boat);
        await Plane(me, boat);
        await Turn(me, boat);
        await Aground(me, boat);
        await Gamey(me);
        await Parked(me);
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        _each = null;
        RideControlsOff();
        if (fatal != null) { _failures++; Log($"FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void RideControlsOff()
    {
        if (_local() is { } me) me.RideControls = null;
    }

    // ---- calm ---------------------------------------------------------------------------------

    private async Task Calm(FootPlayer me, Boat boat)
    {
        var at = At(Lake.ShoreX + 300, 0);
        WaterField.TryLevelAt(at, out float level);
        me.PlaceBoat(at with { Y = level - 0.2f }, East);
        me.RideControls = Helm(me, 0f);
        await Wait(8);
        var s = me.BoatMotion;
        WaterField.TryLevelAt(me.GlobalPosition, out level);
        float draft = level - me.GlobalPosition.Y;
        Log(string.Create(CultureInfo.InvariantCulture, $"calm, afloat: draft {draft:F2} m, pitch {Deg(s.Pitch):F1}°, roll {Deg(s.Roll):F1}°, drift {s.Velocity.Length():F2} m/s, immersion {s.Immersion:F2}"));
        Expect(draft > 0.1f && draft < 0.5f, $"floats at its draft ({draft:F2} m)");
        Expect(Mathf.Abs(Deg(s.Pitch)) < 4f && Mathf.Abs(Deg(s.Roll)) < 2f, "floats level in a calm");
        Expect(s.Velocity.Length() < 0.2f, "lies still in a calm");
        await Shot("idle_calm", () => Look(me, side: 1f, back: 0.6f, up: 0.3f, distance: 1.7f));
    }

    private async Task Plane(FootPlayer me, Boat boat)
    {
        var spec = boat.Spec;
        float plane = float.NaN, top = 0f, hump = 0f, t = 0f;
        bool shotSide = false, shotWake = false;
        _each = () =>
        {
            var s = me.BoatMotion;
            t += 1f / Engine.PhysicsTicksPerSecond;
            if (float.IsNaN(plane) && spec.Planing(s.WaterSpeed) > 0.95f) plane = t;
            if (t < 8f && s.WaterSpeed < spec.HumpSpeed * 1.4f) hump = Mathf.Max(hump, Deg(s.Pitch));
            top = Mathf.Max(top, s.WaterSpeed);
        };
        me.RideControls = Helm(me, 1f, hold: East);
        for (int i = 0; i < 40; i++)
        {
            await Wait(1);
            if (_shots && i == 18 && !shotWake) { shotWake = true; await Shot("speed_wake", () => Look(me, side: -0.6f, back: 1.6f, up: 0.55f, distance: 2.4f)); }
            if (_shots && i == 22 && !shotSide) { shotSide = true; await Shot("planing", () => Look(me, side: 1f, back: 0.05f, up: 0.1f, distance: 1.7f)); }
            if (_shots && i == 26) await Shot("hud", null);
        }
        _each = null;
        var st = me.BoatMotion;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"flat out, calm: on the plane in {plane:F1} s, top {top * 3.6f:F1} km/h ({top / 0.5144f:F1} kn, spec {spec.TopSpeed * 3.6f:F0}), bow up {hump:F1}° over the hump, running trim {Deg(st.Pitch):F1}°, immersion {st.Immersion:F2}"));
        Expect(plane < 7f, $"climbs onto the plane ({plane:F1} s)");
        Expect(top > spec.TopSpeed * 0.88f && top < spec.TopSpeed * 1.12f, $"reaches its top speed ({top * 3.6f:F0} km/h)");
        Expect(hump > 2f, $"bow up over the hump ({hump:F1}°)");
    }

    private async Task Turn(FootPlayer me, Boat boat)
    {
        float worst = 0f, t = 0f, speed = 0f;
        float minX = 1e9f, maxX = -1e9f, minZ = 1e9f, maxZ = -1e9f;
        _each = () =>
        {
            var s = me.BoatMotion;
            t += 1f / Engine.PhysicsTicksPerSecond;
            worst = Mathf.Max(worst, Mathf.Abs(Deg(s.Roll)));
            if (t < 8f) return;
            var p = me.GlobalPosition;
            minX = Mathf.Min(minX, p.X); maxX = Mathf.Max(maxX, p.X);
            minZ = Mathf.Min(minZ, p.Z); maxZ = Mathf.Max(maxZ, p.Z);
            speed = s.Velocity.Length();
        };
        me.RideControls = Helm(me, 1f, steer: 1f);
        await Wait(22);
        _each = null;
        float circle = 0.5f * (maxX - minX + maxZ - minZ);
        Log(string.Create(CultureInfo.InvariantCulture, $"full helm flat out: turning circle {circle:F0} m at {speed * 3.6f:F0} km/h, banked {Deg(me.BoatMotion.Roll):F0}° (worst {worst:F0}°)"));
        Expect(circle > 6f && circle < 100f, $"turns ({circle:F0} m circle)");
        Expect(worst < 50f && me.Ride == _kind, "no capsize in a calm");
        Expect(Deg(me.BoatMotion.Roll) > 1f, "leans into the turn");
    }

    private async Task Aground(FootPlayer me, Boat boat)
    {
        // slow ahead from the shelf into the beach
        var at = At(Lake.ShoreX + 110, 30);
        WaterField.TryLevelAt(at, out float level);
        me.PlaceBoat(at with { Y = level - 0.2f }, West);
        me.RideControls = Helm(me, 0.4f, hold: West);
        bool stopped = await Until(() => me.BoatMotion.Grounded && me.BoatMotion.Velocity.Length() < 0.6f, 40);
        var s = me.BoatMotion;
        float fromShore = WaterField.TryLv95(me.GlobalPosition, out double e, out _) ? (float)(e - SpawnPoint.ParseTarget().E - Lake.ShoreX) : float.NaN;
        Log(string.Create(CultureInfo.InvariantCulture, $"aground {Mathf.Abs(fromShore):F1} m {(fromShore < 0 ? "up the beach from" : "short of")} the waterline, {s.Velocity.Length():F2} m/s, bow {Deg(s.Pitch):F1}°"));
        Expect(stopped && fromShore > -12f, "runs aground on the beach and stops");
        me.RideControls = Helm(me, 0f);
    }

    // ---- gamey ---------------------------------------------------------------------------------

    private async Task Gamey(FootPlayer me)
    {
        await SeaState("gamey", 1f);
        var at = At(Lake.ShoreX + 400, -200);
        WaterField.TryLevelAt(at, out float level);
        me.PlaceBoat(at with { Y = level - 0.2f }, East);
        float pitchLo = 0f, pitchHi = 0f, air = 0f, worst = 0f;
        bool airShot = false, pitchShot = false, thrown = false;
        _each = () =>
        {
            if (me.Vehicle is not Boat) { thrown = true; return; }
            var s = me.BoatMotion;
            pitchLo = Mathf.Min(pitchLo, Deg(s.Pitch));
            pitchHi = Mathf.Max(pitchHi, Deg(s.Pitch));
            air = Mathf.Max(air, s.Airborne);
            worst = Mathf.Max(worst, Mathf.Abs(Deg(s.Roll)));
        };
        // a slow run first, pitching over the swell, then flat out
        me.RideControls = Helm(me, 0.25f, hold: East);
        for (int i = 0; i < 12 && !thrown; i++)
        {
            await Wait(0.5);
            if (_shots && !pitchShot && Mathf.Abs(Deg(me.BoatMotion.Pitch)) > 4f)
            {
                pitchShot = true;
                await Shot("pitching_swell", () => Look(me, side: 1f, back: 0.1f, up: 0.12f, distance: 1.9f));
            }
        }
        me.RideControls = Helm(me, 1f, hold: East);
        for (int i = 0; i < 50 && !thrown; i++)
        {
            await Wait(0.4);
            if (_shots && !airShot && me.BoatMotion.Airborne > 0.12f)
            {
                airShot = true;
                await Shot("airborne", () => Look(me, side: 1f, back: 0.2f, up: 0.08f, distance: 1.8f));
            }
        }
        _each = null;
        Log(string.Create(CultureInfo.InvariantCulture,
            $"gamey: pitch {pitchLo:F0}..{pitchHi:F0}°, longest in the air {air:F2} s, worst roll {worst:F0}°, {(thrown ? "the rider was thrown off" : "still aboard")}"));
        Expect(pitchHi - pitchLo > 4f, "the swell pitches it");
        if (_kind == RideKind.Speedboat) Expect(!thrown && worst < 70f, "the speedboat rides it out");
        if (thrown)
        {
            // a jetski's rider in the water: the machine floats on, riderless; back on it to go on
            Expect(me.Ride == RideKind.OnFoot && me.IsSwimming, "thrown off: swimming (#301)");
            await Wait(2);
            if (Nearest(me) is { } loose && IsInstanceValid(loose))
            {
                me.StartSwimmingAtSurface(loose.GlobalPosition + loose.GlobalTransform.Basis.X * (loose.Ride.ParkedBox.Size.X * 0.5f + 0.5f));
                await Wait(0.3);
                Expect(me.TryGetIn() && await Until(() => me.Ride == _kind, 5), "climbs back aboard from the water");
            }
            else Expect(false, "the riderless jetski floats on");
        }
        // let it come off the plane and settle before it is left
        me.RideControls = Helm(me, 0f);
        await Until(() => me.Vehicle is not Boat || me.BoatMotion.Velocity.Length() < 0.6f, 40);
    }

    private VehicleBody? Nearest(FootPlayer me)
    {
        VehicleBody? best = null;
        float d = 60f;
        if (VehicleManager.Instance is { } vehicles)
            foreach (var node in vehicles.GetChildren())
                if (node is VehicleBody { Wrecked: false } v && v.Kind == _kind && v.GlobalPosition.DistanceTo(me.GlobalPosition) < d)
                {
                    d = v.GlobalPosition.DistanceTo(me.GlobalPosition);
                    best = v;
                }
        return best;
    }

    private async Task Parked(FootPlayer me)
    {
        if (me.Ride != _kind) { Expect(false, "aboard to park it"); return; }
        var spot = me.GlobalPosition;
        me.RideControls = null;
        me.ExitVehicle();
        await Wait(1);
        var parked = Nearest(me);
        if (parked == null) { Expect(false, "parked: it is left in the world"); return; }
        var from = parked.GlobalPosition;
        float lo = 1e9f, hi = -1e9f, off = 0f;
        var boat = (Boat)parked.Ride;
        for (int i = 0; i < 150; i++)
        {
            await Wait(0.1);
            if (!IsInstanceValid(parked)) break;
            float y = parked.GlobalPosition.Y;
            lo = Mathf.Min(lo, y);
            hi = Mathf.Max(hi, y);
            if (boat.TrySurface(parked.GlobalPosition, parked.Rotation.Y, WaterField.Now, out float surface))
                off = Mathf.Max(off, Mathf.Abs(surface - y - 0.25f));
        }
        float drift = MathX.FlatDistance(from, parked.GlobalPosition);
        Log(string.Create(CultureInfo.InvariantCulture, $"parked, gamey: heaves {hi - lo:F2} m, drifts {drift:F2} m in 15 s, keel within {off:F2} m of a 25 cm draft"));
        Expect(hi - lo > 0.25f, "the parked boat rides the swell");
        Expect(drift > 0.05f, "and drifts");
        Expect(off < 0.6f, "floating at the surface, neither sunk nor flying");
        await Shot("parked_gamey", () => Look(parked, side: 1f, back: 0.4f, up: 0.25f, distance: 1.8f));

        await SeaState("calm", 0f);
        Expect(await Until(() => !IsInstanceValid(parked) || parked.Asleep, 40), "calm again, it sleeps");
        if (!IsInstanceValid(parked)) return;
        await Wait(2);
        await Shot("parked_calm", () => Look(parked, side: 1f, back: -0.5f, up: 0.25f, distance: 1.7f));
        // swimming up to it (#301), E climbs aboard
        Expect(me.StartSwimmingAtSurface(parked.GlobalPosition + parked.GlobalTransform.Basis.X * (boat.ParkedBox.Size.X * 0.5f + 0.5f)), "swims beside it");
        await Wait(0.5);
        Expect(me.IsSwimming && me.TryGetIn() && await Until(() => me.Ride == _kind, 5), "boards it from the water, claimed like any vehicle");
    }

    // ---- pictures -----------------------------------------------------------------------------

    /// <summary>A camera looking at a body from its side (+1 starboard), behind, above, at a distance in boat lengths.</summary>
    private Transform3D Look(Node3D body, float side, float back, float up, float distance)
    {
        float length = (body as FootPlayer)?.Vehicle?.ParkedBox.Size.Z ?? ((body as VehicleBody)?.Ride.ParkedBox.Size.Z ?? 5f);
        var b = body.GlobalTransform.Basis;
        var right = (b.X with { Y = 0 }).Normalized();
        var aft = (b.Z with { Y = 0 }).Normalized();
        var target = body.GlobalPosition + Vector3.Up * 0.7f;
        var eye = target + (right * side + aft * back).Normalized() * (length * distance) + Vector3.Up * (length * distance * up);
        return new Transform3D(Basis.LookingAt(target - eye, Vector3.Up), eye);
    }

    /// <summary>
    /// A picture into test_output/boats (windowed with <c>shots</c> only): from <paramref name="where"/>
    /// (followed while it settles), or through the player's own chase camera with the HUD when null.
    /// </summary>
    private async Task Shot(string name, Func<Transform3D>? where)
    {
        if (!_shots) return;
        var before = GetViewport().GetCamera3D();
        if (where != null && _cam != null)
        {
            _follow = where;
            _cam.GlobalTransform = where();
            _cam.MakeCurrent();
        }
        for (int i = 0; i < 4; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/boats");
        System.IO.Directory.CreateDirectory(dir);
        string file = System.IO.Path.Combine(dir, $"{_name}_{name}.png");
        var error = GetViewport().GetTexture().GetImage().SavePng(file);
        Log($"shot {file}: {error}");
        _follow = null;
        if (where != null) before?.MakeCurrent();
    }
}
