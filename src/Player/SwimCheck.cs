using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;
using UnitSport.World;

namespace UnitSport.Player;

/// <summary>
/// <c>--swimcheck [shots] --chunks fixture:lake</c> (#301): swimming on the lake course, scripted,
/// headless (with <c>shots</c>: windowed, PNGs into <c>test_output/swim/</c>).
/// <list type="bullet">
/// <item>Walks in off the beach: swimming once the water is chest deep, head out, air full.</item>
/// <item>Swims out (sprint stroke) and rides a gamey swell: the body follows the surface.</item>
/// <item>Swims down where it looks, dives to the bed 7 m down (air draining), rises and floats up.</item>
/// <item>Climbs out onto a pontoon (the mantle), and wades out onto the beach.</item>
/// <item>Drowns with no air (knocked out by drowning damage) and wakes up on the beach.</item>
/// <item>A car sinks: the driver comes out swimming at the surface.</item>
/// <item>A 30 m drop into deep water costs nothing, into 1.6 m of water it hurts; a wingsuit
/// opened over the lake comes down swimming, uncrashed.</item>
/// <item>Wading (#380): ankle deep the walk keeps its pace, knee deep it slows a little, waist deep
/// a lot (a run more than a walk).</item>
/// <item>A crash ragdoll thrown into the lake (#380) plunges, floats limp at the surface on a chop,
/// then comes round swimming.</item>
/// </list>
/// <c>--swimonly a,b</c> runs only those steps (walkin swimout dive climb wadeout drown car highdive
/// wingsuit wade ragdoll). Prints <c>[swimcheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class SwimCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--swimcheck") >= 0;

    private static bool ShotsMode
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--swimcheck");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "shots";
        }
    }

    private readonly Func<FootPlayer?> _local;
    private int _failures;
    private FootPlayer _me = null!;
    private Vector3 _wish;
    private bool _run;

    public SwimCheck(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "SwimCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[swimcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[swimcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        for (double t = 0; t < seconds; t += 0.05)
        {
            if (done()) return true;
            await Wait(0.05);
        }
        return done();
    }

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    /// <summary>A point of the course (metres from the start, X east, Y north) in world space, at altitude y.</summary>
    private static Vector3 At(double x, double y, float alt = 0f)
    {
        var (e, n) = Start;
        WaterField.TryWorld(e + x, n + y, out var w);
        return w with { Y = alt };
    }

    private static float Bed(Vector3 world) =>
        WaterField.TryLv95(world, out double e, out double n) ? (float)Lake.Ground(e - Start.E, n - Start.N) : float.NaN;

    private static float Level => (float)Lake.Level;

    /// <summary>East on the course, in world space (the course's X axis).</summary>
    private static Vector3 East => (At(100, 0) - At(0, 0)).Normalized();

    /// <summary>The yaw that looks along a flat direction (−Z forward).</summary>
    private static float YawOf(Vector3 d) => Mathf.Atan2(-d.X, -d.Z);

    private static void Press(string action, bool down)
    {
        if (down) Input.ActionPress(action);
        else Input.ActionRelease(action);
    }

    private void ReleaseAll()
    {
        foreach (var a in new[] { PlayerInput.MoveForward, PlayerInput.Jump, PlayerInput.CrouchSlide, PlayerInput.Sprint })
            Input.ActionRelease(a);
        _wish = Vector3.Zero;
        _run = false;
    }

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
        _me = me;
        // scripted strokes: a direction in the world, not the view's (the camera can look elsewhere)
        _me.WalkControls = () => (_wish, _run);

        if (!await Until(() => WaterField.TryGetStill(At(Lake.ShoreX + 400, 0), out _, out _), 90)) { Finish("the lake's water layer never loaded"); return; }
        if (ShotsMode) _me.DebugThirdPerson(true);

        try
        {
            // --swimonly wade,ragdoll (#380): only those steps, for pictures or a quick look
            var only = CmdArgs.Value("--swimonly")?.Split(',');
            bool Step(string name) => only == null || Array.IndexOf(only, name) >= 0;
            if (Step("walkin")) await WalkIn();
            if (Step("swimout")) await SwimOut();
            if (Step("dive")) await Dive();
            if (Step("climb")) await ClimbOut();
            if (Step("wadeout")) await WadeOut();
            if (Step("drown")) await Drown();
            if (Step("car")) await CarSinks();
            if (Step("highdive")) await HighDives();
            if (Step("wingsuit")) await Wingsuit();
            if (Step("wade")) await Wade();
            if (Step("ragdoll")) await RagdollIntoLake();
        }
        catch (Exception e) { _failures++; GD.PrintErr($"[swimcheck] FAIL exception {e}"); }
        ReleaseAll();
        _me.WalkControls = null;
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { _failures++; GD.PrintErr($"[swimcheck] FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>Puts the player standing on the ground at a course point, still.</summary>
    private async Task StandAt(double x, double y)
    {
        ReleaseAll();
        var p = At(x, y);
        _me.GlobalPosition = p with { Y = Bed(p) + 0.3f };
        _me.Velocity = Vector3.Zero;
        await Until(() => _me.IsOnFloor() && !_me.IsSwimming, 5);
    }

    /// <summary>Floating at the surface over a course point, still.</summary>
    private async Task FloatAt(double x, double y)
    {
        ReleaseAll();
        Expect(_me.StartSwimmingAtSurface(At(x, y)), $"put in the water at {x:F0} m");
        await Wait(0.8);
    }

    private float Sub => Level - _me.GlobalPosition.Y;

    private Loot.Gathering? Gather => GetTree().Root.FindChild("Gathering", true, false) as Loot.Gathering;

    // ---- the checks ---------------------------------------------------------------------

    private async Task WalkIn()
    {
        Log("-- walk in off the beach");
        await StandAt(Lake.ShoreX - 4, 0);
        _me.LookYaw = YawOf(East);
        _wish = East;
        _run = true;
        float entered = float.NaN;
        await Until(() =>
        {
            if (_me.IsSwimming && float.IsNaN(entered)) entered = Sub;
            return _me.IsSwimming;
        }, 40);
        Expect(_me.IsSwimming, $"swimming once off the shelf's chest-deep water ({(_me.GlobalPosition - At(Lake.ShoreX, 0)).Slide(Vector3.Up).Length():F0} m out)");
        Expect(entered > 1.3f && entered < 1.6f, $"it starts with the chest under: feet {entered:F2} m down");
        await Wait(2.5);
        Expect(_me.IsSwimming && !_me.HeadUnderwater, $"head out at the surface (feet {_me.SwimDepth:F2} m down)");
        Expect(_me.Air >= FootPlayer.AirMax - 0.01f, $"air full ({_me.Air:F1} s)");
        Expect(_me.PoseKind == FootPlayer.PoseSwim && (int)_me.Anim.Z == (int)Avatar.SwimStyle.Crawl,
            $"the pose is the crawl (pose {_me.PoseKind}, style {_me.Anim.Z})");
        if (ShotsMode) await Shot("swim_crawl_third", yaw: YawOf(East) + 1.2f, pitch: -0.25f);
    }

    private async Task SwimOut()
    {
        Log("-- swim out, ride the swell");
        // the sprint stroke over 4 s
        var from = _me.GlobalPosition;
        await Wait(4);
        float easy = (_me.GlobalPosition - from).Slide(Vector3.Up).Length() / 4f;
        Expect(easy > _me.SprintSwimSpeed * 0.75f && easy < _me.SprintSwimSpeed * 1.2f,
            $"the sprint stroke makes {easy:F2} m/s (sprint {_me.SprintSwimSpeed:F1})");
        _run = false;
        await Wait(1.5);
        from = _me.GlobalPosition;
        await Wait(3);
        float pace = (_me.GlobalPosition - from).Slide(Vector3.Up).Length() / 3f;
        Expect(pace > _me.SwimSpeed * 0.75f && pace < _me.SwimSpeed * 1.25f, $"the easy stroke makes {pace:F2} m/s (swim {_me.SwimSpeed:F2})");

        // a gamey swell out in the deep: the swimmer rides it
        await SeaState("gamey", 1f);
        await FloatAt(Lake.ShoreX + 420, 0);
        float lo = float.MaxValue, hi = float.MinValue, err = 0f;
        for (int i = 0; i < 80; i++)
        {
            await Wait(0.1);
            WaterField.TryLevelAt(_me.GlobalPosition, out float level);
            float y = _me.GlobalPosition.Y;
            lo = Mathf.Min(lo, y);
            hi = Mathf.Max(hi, y);
            err = Mathf.Max(err, Mathf.Abs(level - 1.42f - y));
        }
        Expect(hi - lo > 0.4f, $"gamey: the swell lifts and drops the swimmer {hi - lo:F2} m");
        Expect(err < 0.4f, $"gamey: it rides the surface (worst {err:F2} m off its floating depth)");
        Expect(!_me.HeadUnderwater || err < 0.4f, "gamey: head out");
        Expect((int)_me.Anim.Z == (int)Avatar.SwimStyle.Tread, $"still: treading water (style {_me.Anim.Z})");
        if (ShotsMode)
        {
            await Shot("swim_gamey_tread", yaw: YawOf(East) + 2.4f, pitch: -0.15f);
            _wish = East.Rotated(Vector3.Up, 0.5f);
            await Wait(2);
            await Shot("swim_gamey_crawl", yaw: YawOf(East) + 1.4f, pitch: -0.2f);
            _wish = Vector3.Zero;
        }
        await SeaState("calm", 0f);
    }

    private async Task SeaState(string name, float value)
    {
        var chat = GetTree().Root.FindChild(Net.ChatManager.NodeName, true, false) as Net.ChatManager;
        chat?.Send($"/seastate {name}");
        Expect(await Until(() => Mathf.Abs(WaterField.SeaState - value) < 1e-3f, 3), $"/seastate {name}");
    }

    private async Task Dive()
    {
        Log("-- swim down where it looks, dive to the bed, back up");
        // the drop-off's top: about 7 m of water
        await FloatAt(Lake.ShoreX + 165, 0);
        float bed = Bed(_me.GlobalPosition);
        Log($"bed {Level - bed:F1} m down");
        // look down and swim forward: along the look
        _me.WalkControls = null;
        _me.LookYaw = YawOf(East);
        _me.LookPitch = -0.8f;
        Press(PlayerInput.MoveForward, true);
        float y0 = _me.GlobalPosition.Y;
        await Wait(2);
        float down = y0 - _me.GlobalPosition.Y;
        Expect(down > 1.2f, $"looking down, the stroke goes down ({down:F2} m in 2 s)");
        Expect(_me.HeadUnderwater, "head under");
        Expect((int)_me.Anim.Z == (int)Avatar.SwimStyle.Under, $"the underwater stroke (style {_me.Anim.Z})");
        Press(PlayerInput.MoveForward, false);
        _me.LookPitch = -0.2f;
        _me.WalkControls = () => (_wish, _run);
        // and crouch the rest of the way, stroking east (seen from the side for the picture)
        Press(PlayerInput.CrouchSlide, true);
        if (ShotsMode)
        {
            _wish = East;
            await Wait(0.9);
            await Shot("swim_under_third", yaw: YawOf(East) + 1.45f, pitch: 0.05f, settle: 0.25);
            _wish = Vector3.Zero;
        }
        bool reached = await Until(() => _me.IsOnFloor() || _me.GlobalPosition.Y - bed < 0.15f, 12);
        Expect(reached, $"crouch dives to the bed ({_me.GlobalPosition.Y - bed:F2} m above it)");
        float air = _me.Air;
        Expect(air < FootPlayer.AirMax - 2f, $"the air drains under water ({air:F1} s left)");
        Expect(_me.IsSwimming, "still swimming on the bed (too deep to stand)");
        if (ShotsMode)
        {
            _me.DebugThirdPerson(false);
            _me.LookPitch = -1.1f;
            await Wait(0.6);
            await Shot("swim_under_bed_first", null, null);
            _me.DebugThirdPerson(true);
        }
        Press(PlayerInput.CrouchSlide, false);
        // Jump rises
        Press(PlayerInput.Jump, true);
        float y1 = _me.GlobalPosition.Y;
        await Wait(1.5);
        Expect(_me.GlobalPosition.Y - y1 > 1.2f, $"Jump rises ({_me.GlobalPosition.Y - y1:F2} m in 1.5 s)");
        Press(PlayerInput.Jump, false);
        // idle, buoyancy brings it up
        bool up = await Until(() => !_me.HeadUnderwater, 20);
        Expect(up, "left alone, it floats back up to the surface");
        float low = _me.Air;
        await Wait(1.5);
        Expect(_me.Air > low + 5f, $"the air refills at the surface ({low:F1} -> {_me.Air:F1} s)");
    }

    private async Task ClimbOut()
    {
        Log("-- climb out onto a pontoon");
        // a 4 x 4 m pontoon standing on the shelf, its deck 0.5 m out of the water, 2 m of water round it
        var centre = At(Lake.ShoreX + 125, 30);
        float bed = Bed(centre);
        float top = Level + 0.5f;
        var pontoon = new StaticBody3D { Name = "Pontoon" };
        var size = new Vector3(4f, top - bed + 0.5f, 4f);
        pontoon.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
        pontoon.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = size },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.42f, 0.3f) },
        });
        GetTree().CurrentScene.AddChild(pontoon);
        pontoon.GlobalPosition = centre with { Y = top - size.Y * 0.5f };
        await Wait(0.2);

        await FloatAt(Lake.ShoreX + 125 - 9, 30);
        _me.LookYaw = YawOf(East) + 1.3f;
        _me.LookPitch = -0.2f;
        _wish = East;
        bool shot = false;
        bool climbed = false;
        for (double t = 0; t < 14 && !climbed; t += 0.02)
        {
            if (ShotsMode && !shot && !_me.IsSwimming)
            {
                // half way up the pull (0.36 s)
                shot = true;
                await Wait(0.14);
                await Shot("swim_climb_out", null, null, settle: 0);
            }
            climbed = !_me.IsSwimming && _me.IsOnFloor() && Mathf.Abs(_me.GlobalPosition.Y - top) < 0.25f;
            await Wait(0.02);
        }
        Expect(climbed, $"swims to the pontoon and climbs out onto its deck (feet {_me.GlobalPosition.Y - top:+0.00;-0.00} m from it, swimming {_me.IsSwimming})");
        _wish = Vector3.Zero;
        await Wait(0.5);
        pontoon.QueueFree();
    }

    private async Task WadeOut()
    {
        Log("-- swim back and wade out");
        await FloatAt(Lake.ShoreX + 100, 0);
        _me.LookYaw = YawOf(-East);
        _wish = -East;
        _run = true;
        bool walking = await Until(() => !_me.IsSwimming && _me.IsOnFloor(), 40);
        Expect(walking, $"standing again in the shallows ({Level - Bed(_me.GlobalPosition):F2} m of water)");
        bool ashore = await Until(() => !WaterField.TryGetStill(_me.GlobalPosition, out float still, out _) || still < Bed(_me.GlobalPosition) + 0.05f, 30);
        Expect(ashore, "walks out onto the beach");
        _wish = Vector3.Zero;
        _run = false;
        // stand a moment: this is where a knocked-out swimmer wakes up
        await Wait(2.6);
    }

    private async Task Drown()
    {
        Log("-- drown with no air");
        var beach = _me.GlobalPosition;
        float health = _me.Health;
        await FloatAt(Lake.ShoreX + 165, 0);
        _me.DebugSetAir(4f);
        Press(PlayerInput.CrouchSlide, true);
        await Until(() => _me.HeadUnderwater, 3);
        await Wait(3);
        if (ShotsMode)
        {
            _me.DebugThirdPerson(false);
            _me.LookPitch = -0.1f;
            await Wait(1.2);
            await Shot("swim_air_hud", null, null);
            _me.DebugThirdPerson(true);
        }
        bool hurt = await Until(() => _me.Health < health - 10f, 8);
        Expect(hurt, $"no air left: drowning hurts (health {_me.Health:F0})");
        bool knocked = await Until(() => _me.KnockedOut, 12);
        Press(PlayerInput.CrouchSlide, false);
        Expect(knocked, "and drowns: knocked out");
        bool woke = await Until(() => !_me.KnockedOut, 8);
        await Wait(1);
        Expect(woke && !_me.IsSwimming && _me.GlobalPosition.DistanceTo(beach) < 4f,
            $"wakes up on the beach where it last stood ({_me.GlobalPosition.DistanceTo(beach):F1} m from it, swimming {_me.IsSwimming})");
        Expect(await Until(() => _me.Air >= FootPlayer.AirMax - 0.01f, 7), $"its breath comes back ({_me.Air:F1} s)");
    }

    private async Task CarSinks()
    {
        Log("-- a car sinks: the driver swims");
        await StandAt(0, 0);
        var kind = CarCatalog.All[0].Kind;
        Expect(_me.SetRide(kind), "in a car on the beach");
        await Wait(0.5);
        _me.GlobalPosition = At(Lake.ShoreX + 400, 40, Level + 1f);
        _me.Velocity = Vector3.Zero;
        bool sank = await Until(() => _me.Ride == RideKind.OnFoot, 14);
        Expect(sank, "the car sinks");
        await Wait(0.1);
        Expect(_me.IsSwimming, "the driver comes out swimming");
        await Wait(2);
        Expect(_me.IsSwimming && !_me.HeadUnderwater && Mathf.Abs(_me.SwimDepth - 1.42f) < 0.35f,
            $"at the surface ({_me.SwimDepth:F2} m feet down, head under {_me.HeadUnderwater})");
    }

    private async Task HighDives()
    {
        Log("-- high dives");
        if (ShotsMode)
        {
            // a jump off a 6 m board, for the splash
            await StandAt(0, 0);
            _me.GlobalPosition = At(Lake.ShoreX + 400, -60, Level + 6f);
            _me.Velocity = Vector3.Zero;
            _me.LookYaw = YawOf(East) + 1.0f;
            _me.LookPitch = -0.75f;
            await Until(() => _me.IsSwimming, 4);
            await Wait(0.15);
            await Shot("swim_splash", null, null, settle: 0);
        }
        // 30 m into 25 m of water: free
        await StandAt(0, 0);
        float health = _me.Health;
        _me.GlobalPosition = At(Lake.ShoreX + 400, -40, Level + 30f);
        _me.Velocity = Vector3.Zero;
        bool inWater = false;
        for (double t = 0; t < 6 && !inWater; t += 0.02)
        {
            inWater = _me.IsSwimming;
            await Wait(0.02);
        }
        Expect(inWater, "a 30 m drop into the lake: swimming");
        await Wait(3);
        Expect(_me.Health >= health - 0.5f, $"into deep water: no fall damage (health {health:F0} -> {_me.Health:F0})");

        // 30 m into 1.6 m of water: the bed is hit hard
        await StandAt(0, 0);
        await Wait(7);   // regenerated to full
        health = _me.Health;
        var shallow = At(Lake.ShoreX + 95, -40);
        Log($"shallow: {Level - Bed(shallow):F2} m of water");
        _me.GlobalPosition = shallow with { Y = Level + 30f };
        _me.Velocity = Vector3.Zero;
        await Until(() => _me.IsSwimming, 6);
        await Wait(1.5);
        float lost = health - _me.Health;
        Expect(lost > 3f && lost < 90f, $"into 1.6 m of water: hurt, less than on land ({lost:F0} health; on land it would be {(Mathf.Sqrt(2 * 9.81f * 31.4f) - 11f) * 9f:F0})");
    }

    private async Task Wingsuit()
    {
        Log("-- a wingsuit comes down on the lake");
        await StandAt(0, 0);
        await Until(() => _me.Health >= FootPlayer.MaxHealth - 0.5f, 20);
        float health = _me.Health;
        _me.LookYaw = YawOf(East);
        _me.GlobalPosition = At(Lake.ShoreX + 400, 0, Level + 120f);
        _me.Velocity = East * 8f;
        await Wait(1.6);
        Press(PlayerInput.Jump, true);
        await Wait(0.15);
        Press(PlayerInput.Jump, false);
        Expect(await Until(() => _me.Ride == RideKind.Wingsuit, 2), $"the wingsuit opens ({_me.Ride})");
        bool down = await Until(() => _me.IsSwimming, 60);
        Expect(down, $"it comes down in the water, swimming ({_me.Ride})");
        await Wait(2);
        Expect(_me.Health >= health - 0.5f && !_me.KnockedOut, $"uncrashed (health {health:F0} -> {_me.Health:F0})");
    }

    /// <summary>Course x (metres from the start) on the beach's shelf where the still water is <paramref name="depth"/> deep along y.</summary>
    private static double ShelfAt(float depth, double y)
    {
        for (double x = Lake.ShoreX; x < Lake.ShoreX + 160; x += 0.5)
            if (Level - Bed(At(x, y)) >= depth) return x;
        return Lake.ShoreX + 160;
    }

    private async Task Wade()
    {
        Log("-- wading: slower with depth (#380)");
        _me.LookYaw = YawOf(East) + 1.25f;
        _me.LookPitch = -0.3f;
        var north = (At(0, 100) - At(0, 0)).Normalized();
        float walk = 0f, run = 0f;
        // water is collected standing in it, never from the shore (#380)
        await StandAt(Lake.ShoreX - 3, -60);
        _me.LookYaw = YawOf(East);
        await Wait(0.6);
        Expect(Gather?.Target != Loot.Gathering.Resource.Water, $"on the dry beach facing the lake: no water offered ({Gather?.Target})");
        if (ShotsMode)
        {
            await Shot("gather_shore_none", null, null);
            _me.LookYaw = YawOf(East) + 1.25f;
        }
        foreach (var (name, depth) in new[] { ("ankle", 0.12f), ("knee", Wading.Knee), ("waist", 1.0f) })
        {
            double x = ShelfAt(depth, -60);
            foreach (bool running in new[] { false, true })
            {
                // along the shelf, so the depth stays as it is
                await StandAt(x, -60);
                _wish = north;
                _run = running;
                await Wait(1.2);
                var from = _me.GlobalPosition;
                float wade = _me.WadeDepth;
                await Wait(2.5);
                float pace = (_me.GlobalPosition - from).Slide(Vector3.Up).Length() / 2.5f;
                float full = running ? _me.RunSpeed : _me.WalkSpeed;
                Log($"{name} deep ({wade:F2} m over the feet), {(running ? "running" : "walking")}: {pace:F2} m/s, {pace / full:F2} of its pace on land");
                Expect(!_me.IsSwimming, $"{name} deep: still on its feet");
                if (!running && Gather != null)
                    Expect(Gather.Target == Loot.Gathering.Resource.Water, $"{name} deep: water offered ({Gather.Target})");
                if (name == "ankle") Expect(pace > full * 0.92f, $"ankle deep: the pace is kept ({pace / full:F2})");
                else if (name == "knee") Expect(pace > full * 0.68f && pace < full * 0.93f, $"knee deep: a little slower ({pace / full:F2})");
                else
                {
                    Expect(pace < full * (running ? 0.35f : 0.55f), $"waist deep: much slower ({pace / full:F2})");
                    if (running) run = pace; else walk = pace;
                }
                if (ShotsMode && !running && name != "ankle")
                {
                    if (name == "knee") { _wish = Vector3.Zero; await Wait(0.5); await Shot("gather_wading", null, null); _wish = north; }
                    await Shot($"wade_{name}", null, null);
                    WaterField.TryLevelAt(_me.GlobalPosition, out float level);
                    await CloseShot($"wade_{name}_side", _me.GlobalPosition + north * 0.6f + Vector3.Up * 0.9f, level);
                }
            }
        }
        Expect(run < walk * 2.2f, $"waist deep a run gains little on a walk ({run:F2} against {walk:F2} m/s)");
        ReleaseAll();
    }

    private async Task RagdollIntoLake()
    {
        Log("-- a crash ragdoll into the lake (#380)");
        await SeaState("chop", 0.35f);
        await StandAt(0, 0);
        var from = At(Lake.ShoreX + 400, 20, Level + 4f);
        _me.DebugThrow(from, East * 9f + Vector3.Up * 3f);
        Expect(_me.Ragdolled, "thrown: limp");
        double t = 0, wetAt = -1, swimAt = -1, upAt = -1;
        float deepest = 0f, lo = float.MaxValue, hi = float.MinValue, travel = 0f;
        Vector3? settled = null;
        bool shot = false;
        double start = Time.GetTicksMsec() / 1000.0;
        while (t < 14)
        {
            await Wait(0.05);
            t = Time.GetTicksMsec() / 1000.0 - start;
            if (!_me.Ragdolled) { swimAt = t; break; }
            if (_me.RagdollPelvis is not { } hip || !WaterField.TryLevelAt(hip, out float level)) continue;
            float sub = level - hip.Y;
            if (wetAt < 0 && sub > 0f) wetAt = t;
            if (wetAt < 0) continue;
            deepest = Mathf.Max(deepest, sub);
            // back up from the plunge: the hips by the surface again, rising no more
            if (upAt < 0 && t - wetAt > 0.3 && sub < 0.6f) upAt = t;
            if (upAt < 0 || t - upAt < 0.5) continue;
            // floating: the hips' depth under the moving surface, and how far it drifts
            lo = Mathf.Min(lo, sub);
            hi = Mathf.Max(hi, sub);
            settled ??= hip;
            travel = (hip - settled.Value).Slide(Vector3.Up).Length();
            if (ShotsMode && !shot && t - upAt > 1.0)
            {
                shot = true;
                await Shot("ragdoll_float", null, null, settle: 0);
                await CloseShot("ragdoll_float_close", hip, level);
            }
        }
        Log($"in the water after {wetAt:F1} s, the hips {deepest:F2} m down at the deepest, back up after {upAt - wetAt:F1} s; afloat they ride {lo:F2}..{hi:F2} m under the surface, drifting {travel:F2} m; swimming after {swimAt:F1} s");
        Expect(wetAt > 0, "the body goes into the lake");
        Expect(deepest > 0.35f, $"it plunges under ({deepest:F2} m)");
        Expect(upAt > 0 && upAt - wetAt < 5, $"and comes back up ({upAt - wetAt:F1} s)");
        Expect(lo > -0.25f && hi < 0.75f, $"floating limp at the surface on the chop (hips {lo:F2}..{hi:F2} m under)");
        Expect(swimAt > 0 && swimAt - upAt >= 2.2, $"afloat a while as a ragdoll before coming round ({swimAt - upAt:F1} s)");
        Expect(swimAt > 0 && swimAt - wetAt < 9, "then swims");
        await Wait(1.5);
        Expect(_me.IsSwimming && !_me.HeadUnderwater, $"swimming at the surface (feet {_me.SwimDepth:F2} m down)");
        Expect(Gather?.Target != Loot.Gathering.Resource.Water, $"swimming: no water offered ({Gather?.Target})");
        await SeaState("calm", 0f);
    }

    // ---- screenshots ----------------------------------------------------------------------

    /// <summary>From a camera of its own, 3.5 m off and a metre over the water, looking at <paramref name="at"/>.</summary>
    private async Task CloseShot(string name, Vector3 at, float level)
    {
        var before = GetViewport().GetCamera3D();
        var side = (East.Cross(Vector3.Up) * 2.6f + East * -2.3f);
        var eye = at + side with { Y = 0f };
        eye.Y = level + 1.1f;
        var cam = new Camera3D { Fov = 55f };
        AddChild(cam);
        cam.GlobalTransform = new Transform3D(Basis.LookingAt(at with { Y = Mathf.Max(at.Y, level) - 0.1f } - eye, Vector3.Up), eye);
        cam.MakeCurrent();
        await Shot(name, null, null, settle: 0);
        before?.MakeCurrent();
        cam.QueueFree();
    }

    private async Task Shot(string name, float? yaw, float? pitch, double settle = 0.35)
    {
        if (yaw is { } y) _me.LookYaw = y;
        if (pitch is { } p) _me.LookPitch = p;
        if (settle > 0) await Wait(settle);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var dir = ProjectSettings.GlobalizePath("res://test_output/swim");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"shot {path}: {err}");
    }
}
