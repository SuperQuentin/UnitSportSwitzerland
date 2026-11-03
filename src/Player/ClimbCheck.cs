using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Terrain.Fixture;

namespace UnitSport.Player;

/// <summary>
/// <c>--climbcheck [shots] --chunks fixture:crag --systems ui,physics,adventure</c> (#1225): free climbing
/// and the stamina meter on the crag course, scripted, headless (with <c>shots</c>: windowed, PNGs into
/// <c>test_output/climb/</c>). Fixture worlds have Adventure off unless named: the meter and climbing
/// need it.
/// <list type="bullet">
/// <item>Grabs the rock wall from the ground after pushing into it, and in the air from a jump.</item>
/// <item>Climbs to the ledge (14 m) and stands on it by the mantle, rests to full, grabs the upper wall,
/// climbs on and tops out by the mantle (with the max raised to 200 through the <c>"stamina"</c> reward).</item>
/// <item>Empty on the scree slope: lets go at the height the rules predict (±1 m).</item>
/// <item>A climb-jump costs 18 and goes up; the glacier wall is refused; the block's wall is climbed and its roof topped.</item>
/// <item>Kick off from high on the wall into a wingsuit; Crouch lets go; in a match it does not grab.</item>
/// </list>
/// <c>--climbonly a,b</c> runs only those steps. Prints <c>[climbcheck] RESULT: ok</c> or <c>RESULT: FAILED (n)</c>.
/// </summary>
public partial class ClimbCheck : Node
{
    public static bool Requested => Array.IndexOf(OS.GetCmdlineUserArgs(), "--climbcheck") >= 0;

    private static bool ShotsMode
    {
        get
        {
            var args = OS.GetCmdlineUserArgs();
            int i = Array.IndexOf(args, "--climbcheck");
            return i >= 0 && i + 1 < args.Length && args[i + 1] == "shots";
        }
    }

    private readonly Func<FootPlayer?> _local;
    private int _failures;
    private FootPlayer _me = null!;
    private Vector3 _wish;
    private bool _run;

    public ClimbCheck(Func<FootPlayer?> local)
    {
        _local = local;
        Name = "ClimbCheck";
    }

    public override void _Ready() => _ = Run();

    private static void Log(string what) => GD.Print($"[climbcheck] {what}");

    private void Expect(bool ok, string what)
    {
        if (ok) Log($"ok   {what}");
        else
        {
            _failures++;
            GD.PrintErr($"[climbcheck] FAIL {what}");
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    private async Task<bool> Until(Func<bool> done, double seconds)
    {
        int step = 0;
        for (double t = 0; t < seconds; t += 0.05)
        {
            if (done()) return true;
            await Wait(0.05);
            // a trace of the body every 2 s of a long wait: where it got stuck when it fails
            if (++step % 40 == 0 && _me != null)
                Log($"     t {t:F0} s: {Height:F1} m up, climbing {_me.IsClimbing}, v {_me.Velocity.Length():F1} m/s, " +
                    $"{_me.StaminaMeter.Points:F0} points, mantling {_me.PoseKind}, {_me.ClimbRefused}");
        }
        return done();
    }

    private static (double E, double N) Start => SpawnPoint.ParseTarget();

    /// <summary>A point of the course (metres from the start, X east, Y north) in world space, at its ground.</summary>
    private Vector3 At(double x, double y, double above = 0)
    {
        var (e, n) = Start;
        return _me.Origin!.ToWorld(e + x, n + y, Crag.Ground(x, y) + above);
    }

    /// <summary>The body's height over the flat ground of the course (m).</summary>
    private float Height => (float)(_me.Origin!.ToGlobal(_me.GlobalPosition).Alt - Crag.Flat);

    private Vector3 East => (At(100, 0) - At(0, 0)) with { Y = 0 } is var d ? d.Normalized() : Vector3.Right;

    private static float YawOf(Vector3 d) => Mathf.Atan2(-d.X, -d.Z);

    private static void Press(string action) => Input.ActionPress(action);
    private static void Release(string action) => Input.ActionRelease(action);

    private void ReleaseAll()
    {
        foreach (var a in new[] { PlayerInput.Jump, PlayerInput.CrouchSlide, PlayerInput.Sprint })
            Input.ActionRelease(a);
        _wish = Vector3.Zero;
        _run = false;
    }

    private async Task Tap(string action)
    {
        Press(action);
        await Wait(0.1);
        Release(action);
    }

    private async Task Run()
    {
        FootPlayer? me = null;
        for (int i = 0; i < 1800; i++)
        {
            me = _local();
            if (me != null && me.IsOnFloor() && me.Origin != null) break;
            // the world starts in the fly camera: walk
            if (me == null && i % 50 == 25)
            {
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = true });
                Input.ParseInputEvent(new InputEventAction { Action = PlayerInput.ToggleMode, Pressed = false });
            }
            await Wait(0.1);
        }
        if (me == null) { Finish("no local player"); return; }
        _me = me;
        _me.WalkControls = () => (_wish, _run);
        if (ShotsMode) _me.DebugThirdPerson(true);
        // the crag's tiles under the wall
        if (!await Until(() => _me.Terrain != null && _me.Terrain.HasCollisionAt(At(Crag.WallX + 10, 0)), 90)) { Finish("the crag never loaded"); return; }
        Expect(_me.ClimbingAllowed, $"climbing is on here (adventure {_me.AdventureOn})");
        Expect(_me.StaminaApplies, "the meter counts here");

        try
        {
            var only = CmdArgs.Value("--climbonly")?.Split(',');
            bool Step(string name) => only == null || Array.IndexOf(only, name) >= 0;
            if (Step("ground")) await Ground();
            if (Step("air")) await Air();
            if (Step("ledge")) await Ledge();
            if (Step("empty")) await Empty();
            if (Step("jump")) await JumpCost();
            if (Step("glacier")) await Glacier();
            if (Step("block")) await Block();
            if (Step("kick")) await KickOff();
            if (Step("letgo")) await LetGo();
            if (Step("match")) await Match();
        }
        catch (Exception e) { _failures++; GD.PrintErr($"[climbcheck] FAIL exception {e}"); }
        ReleaseAll();
        Permissions.SetInMatch(false);
        _me.WalkControls = null;
        Finish(null);
    }

    private void Finish(string? fatal)
    {
        if (fatal != null) { _failures++; GD.PrintErr($"[climbcheck] FAIL {fatal}"); }
        Log(_failures == 0 ? "RESULT: ok" : $"RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    /// <summary>Stands the player on the ground at a course point, still, facing east, the meter full.</summary>
    private async Task StandAt(double x, double y)
    {
        ReleaseAll();
        if (_me.Ride != RideKind.OnFoot) _me.SetRide(RideKind.OnFoot);
        await Until(() => !_me.IsClimbing, 1);
        _me.GlobalPosition = At(x, y, 0.3);
        _me.Velocity = Vector3.Zero;
        _me.LookYaw = YawOf(East);
        await Until(() => _me.IsOnFloor() && !_me.IsClimbing, 5);
        _me.DebugSetStamina(_me.StaminaMeter.Max);
        await Wait(0.2);
    }

    /// <summary>Walks east into the face until it grabs.</summary>
    private async Task<bool> Grab(double seconds = 3)
    {
        _wish = East;
        return await Until(() => _me.IsClimbing, seconds);
    }

    // ---- the steps ------------------------------------------------------------------------

    private async Task Ground()
    {
        Log("-- grab the wall from the ground");
        await StandAt(Crag.WallX - 1.5, 0);
        Expect(await Grab(), $"pushing into the rock wall grabs it ({_me.ClimbRefused})");
        _wish = Vector3.Zero;
        await Wait(1.0);
        Expect(_me.IsClimbing && _me.PoseKind == FootPlayer.PoseWallClimb, "it holds on, in the wall-climbing pose");
        float lean = _me.ClimbNormal.Y;
        Expect(lean > 0.05f && lean < 0.35f, $"the face it holds is the 80° wall (normal y {lean:F2})");
        await Shot("wall_hold");
        await Tap(PlayerInput.CrouchSlide);
        await Until(() => _me.IsOnFloor() && !_me.IsClimbing, 3);
        // a garden wall is a mantle, not a climb: a 1 m step pushed into stays a walk
    }

    private async Task Air()
    {
        Log("-- grab the wall from a jump");
        await StandAt(Crag.WallX - 2.5, 0);
        _wish = Vector3.Zero;
        await Tap(PlayerInput.Jump);
        await Wait(0.15);
        _wish = East;
        Expect(await Until(() => _me.IsClimbing, 2), $"jumping at the wall grabs it in the air ({_me.ClimbRefused})");
        await Tap(PlayerInput.CrouchSlide);
        await Until(() => _me.IsOnFloor() && !_me.IsClimbing, 4);
    }

    private async Task Ledge()
    {
        Log("-- to the ledge, rest, on to the top (max 200 from the reward)");
        if (Adventure.AdventureService.Instance is { } adventure)
        {
            adventure.Grant(Adventure.AdventureService.LocalPeer, new Adventure.Unlock(Adventure.StaminaUpgrades.Kind, "+10"), "climbcheck");
            Expect(await Until(() => _me.StaminaMeter.Steps == 10, 2), $"the \"stamina\" +10 reward raises the max to 200 ({_me.StaminaMeter.Max:F0})");
        }
        await StandAt(Crag.WallX - 1.5, 0);
        Expect(await Grab(), "grabs the lower wall");
        float before = _me.StaminaMeter.Points;
        await Shot("wall_climb");
        await Shot("ring_third", own: true);
        if (ShotsMode)
        {
            _me.DebugThirdPerson(false);
            _me.LookPitch = 0.6f;
            await Shot("ring_first", own: true);
            _me.DebugThirdPerson(true);
        }
        bool onLedge = await Until(() => !_me.IsClimbing && _me.IsOnFloor() && Height > Crag.LedgeH - 1, 25);
        float spent = before - _me.StaminaMeter.Points;
        Expect(onLedge, $"climbs {Crag.LedgeH} m and stands on the ledge (at {Height:F1} m, {_me.ClimbRefused})");
        // 14 m at 1.2 m/s along an 80° face: ~12 s at 9 /s
        Expect(spent > 80f && spent < 135f, $"the climb cost {spent:F0} points (about 9 a second)");
        _wish = Vector3.Zero;
        Expect(await Until(() => _me.StaminaMeter.IsFull, 12), $"resting on the ledge refills it ({_me.StaminaMeter.Points:F0})");
        _wish = East;
        Expect(await Until(() => _me.IsClimbing, 3), "grabs the upper wall from the ledge");
        bool top = await Until(() => !_me.IsClimbing && _me.IsOnFloor() && Height > Crag.WallH - 1, 30);
        Expect(top, $"tops out by the mantle onto the top at {Crag.WallH} m (at {Height:F1} m, {_me.ClimbRefused})");
        _me.SetStaminaSteps(0);
    }

    private async Task Empty()
    {
        Log("-- empty on the scree slope");
        await StandAt(Crag.WallX - 1.5, Crag.ScreeY);
        Expect(await Grab(), $"grabs the 55° scree slope ({_me.ClimbRefused})");
        Expect(_me.ClimbLoose, "scree is loose: it costs more");
        const float points = 40f;
        _me.DebugSetStamina(points);
        float from = Height;
        float top = from;
        var t0 = GameClock.Now;
        await Until(() => { top = Mathf.Max(top, Height); return !_me.IsClimbing; }, 15);
        double took = GameClock.Now - t0;
        // the rules: 9 /s x 1.3 loose; up the face at 1.2 m/s, a 55° face climbs sin 55° of it
        float predicted = points / (9f * Stamina.LooseFactor) * 1.2f * Mathf.Sin(Mathf.DegToRad(55f));
        Expect(_me.ClimbRefused == "empty", $"it lets go when empty ({_me.ClimbRefused}) after {took:F1} s");
        Expect(Mathf.Abs(top - from - predicted) <= 1f, $"at the height the rules predict: {top - from:F1} m, predicted {predicted:F1} m");
        Expect(_me.StaminaMeter.Winded, "winded");
        _wish = East;
        await Until(() => _me.IsOnFloor(), 6);
        await Wait(0.5);
        Expect(!_me.IsClimbing, "winded: no grabbing again");
    }

    private async Task JumpCost()
    {
        Log("-- a climb-jump");
        await StandAt(Crag.WallX - 1.5, 0);
        Expect(await Grab(), "grabs the wall");
        await Wait(1.0);
        float p0 = _me.StaminaMeter.Points, h0 = Height;
        await Tap(PlayerInput.Jump);
        await Wait(0.15);
        float paid = p0 - _me.StaminaMeter.Points;
        Expect(paid >= 18f && paid < 21f, $"a climb-jump costs 18 (paid {paid:F1} with the hold)");
        await Wait(0.4);
        Expect(Height - h0 > 1.4f, $"and goes up the face ({Height - h0:F1} m)");
        await Tap(PlayerInput.CrouchSlide);
        await Until(() => _me.IsOnFloor() && !_me.IsClimbing, 5);
    }

    private async Task Glacier()
    {
        Log("-- the glacier wall");
        await StandAt(Crag.WallX - 1.5, Crag.GlacierY);
        bool grabbed = await Grab(2);
        Expect(!grabbed, $"ice is not climbed ({_me.ClimbRefused})");
        Expect(_me.ClimbRefused?.Contains("Glacier") == true, $"refused for its cover ({_me.ClimbRefused})");
        _wish = Vector3.Zero;
    }

    private async Task Block()
    {
        Log("-- the block's wall and roof");
        double west = Crag.BlockX - Crag.BlockSize / 2;
        await StandAt(west - 1.5, 0);
        Expect(await Grab(), $"grabs the block's wall ({_me.ClimbRefused})");
        bool roof = await Until(() => !_me.IsClimbing && _me.IsOnFloor() && Height > Crag.BlockH - 0.6f, 20);
        Expect(roof, $"tops out onto its roof (at {Height:F1} m, {_me.ClimbRefused})");
        _wish = Vector3.Zero;
    }

    private async Task KickOff()
    {
        Log("-- kick off into a wingsuit");
        await StandAt(Crag.WallX - 1.5, 0);
        _me.StaminaInfiniteCommand = true;
        Expect(await Grab(), "grabs the wall");
        // up past the ledge: the wall's upper half
        await Until(() => !_me.IsClimbing && _me.IsOnFloor(), 20);   // on the ledge
        Expect(await Until(() => _me.IsClimbing, 3), "grabs the upper wall");
        await Until(() => Height > Crag.WallH - 3f, 20);   // high: the base jump wants 12 m of air under the feet
        _wish = -East;   // back
        await Wait(0.05);
        Press(PlayerInput.Jump);
        await Wait(0.1);
        Release(PlayerInput.Jump);
        Expect(!_me.IsClimbing && _me.ClimbRefused == "kicked off", $"back + jump kicks off ({_me.ClimbRefused})");
        // still holding back, running: away from the face, clear of the ledge under it
        _run = true;
        // falling, and past the ledge: clear air under the feet (the base jump wants 12 m of it)
        await Until(() => _me.Velocity.Y < -3.5f && _me.Terrain!.TryGetHeight(_me.GlobalPosition, out float g) && _me.GlobalPosition.Y - g > 13f, 3);
        await Tap(PlayerInput.Jump);
        Expect(await Until(() => _me.Ride == RideKind.Wingsuit, 1), $"Jump while falling opens the wingsuit ({_me.Ride}, {Height:F1} m up)");
        _wish = Vector3.Zero;
        _me.StaminaInfiniteCommand = false;
        await StandAt(-5, 0);
    }

    private async Task LetGo()
    {
        Log("-- let go");
        await StandAt(Crag.WallX - 1.5, 0);
        Expect(await Grab(), "grabs the wall");
        await Wait(2.0);
        _wish = Vector3.Zero;   // the stick let go with the hands
        await Tap(PlayerInput.CrouchSlide);
        Expect(!_me.IsClimbing && _me.ClimbRefused == "let go", "Crouch lets go");
        Expect(await Until(() => _me.IsOnFloor(), 4), "and it falls back to the ground");
    }

    private async Task Match()
    {
        Log("-- in a Battle Royale match");
        await StandAt(Crag.WallX - 1.5, 0);
        Permissions.SetInMatch(true);
        Expect(!_me.ClimbingAllowed && !_me.StaminaApplies, "a match: no free climbing, no meter");
        Expect(!await Grab(2), "pushing into the wall does not grab it");
        Permissions.SetInMatch(false);
        _wish = Vector3.Zero;
    }

    // ---- screenshots ----------------------------------------------------------------------

    private async Task Shot(string name, bool own = false)
    {
        if (!ShotsMode) return;
        await Wait(1.2);
        if (own)
        {
            // the player's own view: the HUD's stamina ring where the player sees it
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            Save(name);
            return;
        }
        var before = GetViewport().GetCamera3D();
        var side = East.Cross(Vector3.Up);
        var at = _me.GlobalPosition + Vector3.Up * 1.1f;
        var eye = at - East * 4.5f + side * 3.2f + Vector3.Up * 0.6f;
        var cam = new Camera3D { Fov = 55f };
        AddChild(cam);
        cam.GlobalTransform = new Transform3D(Basis.LookingAt(at - eye, Vector3.Up), eye);
        cam.MakeCurrent();
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        Save(name);
        before?.MakeCurrent();
        cam.QueueFree();
    }

    private void Save(string name)
    {
        var dir = ProjectSettings.GlobalizePath("res://test_output/climb");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, name + ".png");
        var err = GetViewport().GetTexture().GetImage().SavePng(path);
        Log($"shot {path}: {err}");
    }
}
