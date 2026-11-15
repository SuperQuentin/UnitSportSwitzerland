using System;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Bots;

/// <summary>
/// <c>--shuttlebotcheck [moon] [shots]</c> offline (#1498): a bot flies the shuttle's mission through <c>/bot shuttle</c>,
/// on the bot system as on a server (<see cref="ShuttleBrain"/>, <see cref="ShuttlePilot"/> on the body's own controls),
/// a camera of the check's following it, so the world (and its frame between bodies) goes with it.
/// <list type="bullet">
/// <item><c>--world fixture --traffic 0</c>: the player beside the fixture airport's parked shuttle, <c>/bot shuttle 200</c>.
/// The bot walks to it, claims it, taxis onto the runway, takes off, drives once round the Earth at 200 km, coasts, burns
/// retrograde, re-enters, drives home and lands on the runway it left from, stopped on it. It parks the shuttle, stands up
/// and gets out by the hatch, on foot by the parked shuttle.</item>
/// <item><c>moon</c>, under <c>--frame earth --chunks fixture:straight --sun real --date 2027-07-15 --time 10</c>:
/// <c>/bot shuttle moon orbit</c>. A 400 km Earth orbit, the trip to the Moon, down onto the nearest free colony pad (the
/// touchdown under 3 m/s, inside the pad), a while there, up and home to an Earth orbit.</item>
/// </list>
/// Time runs as the pilot wants it (<see cref="ShuttlePilot.WantScale"/>, at most 8x; the drive between bodies at 1x).
/// RESULT line at the end; <c>shots</c> saves pictures in <c>test_output/shuttlebot/</c>.
/// </summary>
public partial class ShuttleBotCheck : Node
{
    public static bool Requested => CmdArgs.Has("--shuttlebotcheck");
    private static bool Moon => CmdArgs.Value("--shuttlebotcheck") is { } v && v.Contains("moon");
    private static bool Shots => CmdArgs.Value("--shuttlebotcheck") is { } v && v.Contains("shots") || CmdArgs.Has("--shots");

    private readonly Func<FootPlayer?> _me;
    private readonly Action _onFoot, _offFoot;
    private int _failures;
    private Camera3D? _camera;
    private FootPlayer? _followed;
    private const string Log = "[shuttlebot]";

    public ShuttleBotCheck(Func<FootPlayer?> me, Action onFoot, Action offFoot)
    {
        _me = me;
        _onFoot = onFoot;
        _offFoot = offFoot;
        Name = "ShuttleBotCheck";
    }

    public override async void _Ready()
    {
        try { await Run(); }
        catch (Exception e) { Expect(false, $"threw: {e}"); }
        Engine.TimeScale = 1;
        GD.Print(_failures == 0 ? $"{Log} RESULT: ok" : $"{Log} RESULT: FAILED ({_failures})");
        GetTree().Quit(_failures == 0 ? 0 : 1);
    }

    private void Expect(bool ok, string what)
    {
        GD.Print($"{Log} {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private async Task Seconds(double s) => await ToSignal(GetTree().CreateTimer(s), SceneTreeTimer.SignalName.Timeout);

    /// <summary>Until the condition holds, at most <paramref name="seconds"/> of game time.</summary>
    private async Task<bool> Until(Func<bool> condition, double seconds)
    {
        double end = GameClock.Now + seconds;
        while (!condition())
        {
            if (GameClock.Now > end) return false;
            await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
        }
        return true;
    }

    private static VehicleBody? ParkedShuttle() =>
        VehicleManager.Instance?.GetChildren().OfType<VehicleBody>().FirstOrDefault(v => v.Kind == RideKind.Shuttle && !v.Wrecked);

    private async Task Run()
    {
        if (!await Until(() => BotDirector.Instance is { Local: not null }, 60)) { Expect(false, "offline has a bot director"); return; }
        _onFoot();
        if (!await Until(() => _me() is { } me && me.IsOnFloor(), 120)) { Expect(false, "the player stands on the ground"); return; }
        var me = _me()!;
        string ask = Moon ? "shuttle moon orbit" : "shuttle 200 1";
        if (!Moon)
        {
            // beside the fixture airport's shuttle, parked at its cargo stand
            if (!await Until(() => ParkedShuttle() is { Posed: true }, 120)) { Expect(false, "a shuttle parked at the airport"); return; }
            var parked = ParkedShuttle()!;
            me.DebugLaunch(parked.GlobalPosition + parked.GlobalBasis.X * 40f + Vector3.Up * 2f, Vector3.Zero);
            if (!await Until(() => me.IsOnFloor(), 30)) { Expect(false, "the player stands beside the shuttle"); return; }
        }
        if (CmdArgs.Has("--asplayer"))
        {
            // the same taxi flown by the player (a diagnosis: the bot's body or the shuttle's ground?)
            var shuttle = ParkedShuttle()!;
            me.DebugLaunch(shuttle.ToGlobal(shuttle.Ride.EntryPoint) + Vector3.Up, Vector3.Zero);
            await Seconds(1);
            for (int i = 0; i < 20 && me.Vehicle == null; i++) { me.TryGetIn(); await Seconds(0.5); }
            if (me.Vehicle is not Airliner own) { Expect(false, "the player got in"); return; }
            var solo = new ShuttlePilot(ShuttleBrain.MissionFor(me, new ShuttleBrain.Ask(false, 200)));
            _followed = me;
            for (int i = 0; i < 1200 && solo.Now == ShuttlePilot.Stage.Taxi; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                if (me.Vehicle is not Airliner jet2) { Expect(false, $"the player's shuttle crashed taxiing at frame {i}"); return; }
                solo.Step(me, jet2, me.Origin!.Frame, me.Clearance, (float)GetPhysicsProcessDeltaTime());
            }
            Expect(solo.Now != ShuttlePilot.Stage.Taxi, $"the player taxied out ({solo.Now})");
            return;
        }
        string said = BotDirector.Instance!.Command(1, ask, true, _ => me);
        GD.Print($"{Log} /bot {ask}: {said}");
        var bot = BotDirector.Instance.All.FirstOrDefault(b => b.Brain is ShuttleBrain);
        if (bot == null) { Expect(false, "a bot took the mission"); return; }
        var brain = (ShuttleBrain)bot.Brain;
        var body = bot.Body;
        Expect(true, $"{bot.Profile.Name} ({body.Name}) has the mission");

        // the check's camera follows the bot: the world's origin (and between bodies its frame) goes with it
        _followed = body;
        _camera = new Camera3D { Fov = 60f, Near = 0.5f, Far = 2_000_000f };
        AddChild(_camera);
        if (Moon)
        {
            // no walker left on the Earth while the world goes to the Moon: back to the free camera, ours over it
            if (!await Until(() => brain.Pilot != null, 60)) { Expect(false, $"the bot at the controls ({brain.Status})"); return; }
            _offFoot();
        }
        else
        {
            if (!await Until(() => brain.Pilot != null || brain.Done, 300)) { Expect(false, $"the bot got to the shuttle and in ({brain.Status})"); return; }
            Expect(brain.Pilot != null, $"the bot walked to the shuttle and got in ({brain.Failure ?? "ok"})");
        }
        if (brain.Pilot is not { } pilot) return;
        if (CmdArgs.Has("--trace"))
            for (int i = 0; i < 20; i++)
            {
                await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame);
                GD.Print(FormattableString.Invariant($"{Log} trace {i}: at {body.GlobalPosition} v {body.Velocity} real {body.GetRealVelocity()} floor {body.IsOnFloor()} wall {body.IsOnWall()} ride {body.Ride} stage {pilot.Now} scale {Engine.TimeScale} want {pilot.WantScale}"));
            }

        double report = 0;
        var shot = new System.Collections.Generic.HashSet<ShuttlePilot.Stage>();
        bool flown = await Until(() =>
        {
            var stage = pilot.Now;
            // the trip between bodies at 1x (its frame crossing), the rest as the pilot wants it, at most 8x; never
            // touched on the ground: written there, the time scale knocked the taxiing shuttle as a 147 m/s crash
            double want = stage is ShuttlePilot.Stage.Cruise or ShuttlePilot.Stage.Clear ? 1.0 : Math.Min(pilot.WantScale, 8.0);
            if (body.Vehicle is Airliner { State.OnGround: false } && Engine.TimeScale != want) Engine.TimeScale = want;
            if (GameClock.Now > report)
            {
                report = GameClock.Now + 60;
                double alt = body.Origin?.AltitudeOf(body.GlobalPosition) ?? double.NaN;
                GD.Print(FormattableString.Invariant($"{Log} {stage} {pilot.StageTime:F0} s: {alt / 1000:F1} km over {body.Origin?.Body}, {(body.Vehicle as Airliner)?.State.Velocity.Length() ?? 0:F0} m/s"));
            }
            if (Shots && shot.Add(stage) && stage is ShuttlePilot.Stage.Roll or ShuttlePilot.Stage.Orbit or ShuttlePilot.Stage.Jets or ShuttlePilot.Stage.OnMoon or ShuttlePilot.Stage.Glide)
                _ = Shot(stage.ToString());
            return stage is ShuttlePilot.Stage.Stopped or ShuttlePilot.Stage.Failed || brain.Done;
        }, 4 * 3600);
        Engine.TimeScale = 1;
        Expect(flown && pilot.Now == ShuttlePilot.Stage.Stopped, $"the mission flown to its end ({pilot.Now}{(pilot.Failure != null ? ": " + pilot.Failure : "")})");
        if (pilot.Now != ShuttlePilot.Stage.Stopped) return;

        if (Moon)
        {
            Expect(pilot.LandedPad is { } pad && pilot.PadMiss <= pad.RadiusM,
                FormattableString.Invariant($"down on a Moon pad: {pilot.LandedPad?.Id ?? "none"}, {pilot.PadMiss:F1} m from its centre (radius {pilot.LandedPad?.RadiusM ?? 0:F0})"));
            Expect(pilot.OrbitApsides is { } o && o.Peri - CelestialBody.Earth.Shape.A > 75_000 && o.Apo - CelestialBody.Earth.Shape.A < 140_000,
                "home in an Earth orbit near 100 km");
            return;
        }
        Expect(pilot.TouchSink < 3f, FormattableString.Invariant($"a soft touchdown: {pilot.TouchSink:F2} m/s"));
        Expect(Math.Abs(pilot.StoppedAt.Across) < 30 && pilot.StoppedAt.Along > 0 && pilot.StoppedAt.Along < pilot.Plan.To.Length,
            FormattableString.Invariant($"stopped on the runway: {pilot.StoppedAt.Along:F0} m along, {pilot.StoppedAt.Across:F1} m across"));
        _ = Shot("stopped");
        bool outside = await Until(() => brain.Done, 60);
        Expect(outside && brain.Failure == null && body.Vehicle == null && !body.Aboard,
            $"parked and out by the hatch, on foot ({brain.Status}, vehicle {body.Vehicle?.Kind}, aboard {body.Aboard})");
        Expect(ParkedShuttle() is { } left && left.GlobalPosition.DistanceTo(body.GlobalPosition) < 40f, "the shuttle parked again beside the bot");
    }

    public override void _Process(double delta)
    {
        if (_camera == null || _followed == null || !IsInstanceValid(_followed)) return;
        if (!_camera.Current) _camera.MakeCurrent();
        var at = _followed.GlobalPosition;
        var up = _followed.Origin?.Frame.UpAt(at) ?? Vector3.Up;
        var back = _followed.GlobalBasis.Z;
        back = (back - up * back.Dot(up)).Normalized();
        if (!back.IsFinite() || back.LengthSquared() < 0.5f) back = Vector3.Back;
        _camera.GlobalPosition = at + back * 75f + up * 22f;
        _camera.LookAt(at + up * 6f, up);
    }

    private async Task Shot(string name)
    {
        if (DisplayServer.GetName() == "headless") return;
        for (int i = 0; i < 3; i++) await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        string dir = ProjectSettings.GlobalizePath("res://test_output/shuttlebot");
        System.IO.Directory.CreateDirectory(dir);
        string path = System.IO.Path.Combine(dir, (Moon ? "moon-" : "earth-") + name + ".png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        GD.Print($"{Log} wrote {path}");
    }
}
