using System;
using System.Linq;
using Godot;
using UnitSport.Core;
using UnitSport.Player;
using UnitSport.Vehicles;

namespace UnitSport.Bots;

/// <summary>
/// A bot flies the space shuttle's mission (#1498, <c>/bot shuttle</c>, <c>docs/notes/bots/shuttle-pilot.md</c>): it walks
/// to the nearest parked shuttle (its stand at Geneva or Zurich), claims it (<see cref="BotDriving.GetIn"/>,
/// <see cref="VehicleManager.ServerClaim"/>), and <see cref="ShuttlePilot"/> flies it on the shuttle's own intents: the body's
/// <see cref="FootPlayer.FlyControls"/> read the pilot's <see cref="ShuttlePilot.Input"/> every physics step, the drive
/// through <see cref="FootPlayer.ShuttleDrive"/>, the target hold through <see cref="Airliner.Aim"/>, as a player's ] does.
/// Round the Earth (the turns asked) or to the Moon and down on a colony's pad, then home to the runway it left from,
/// parked, and out by the hatch (<see cref="FootPlayer.OutByShuttleHatch"/>), strolling about after.
///
/// <para>
/// Wherever the bot's body is simulated: offline (the game is its own server, #1974) and on a server alike; nothing here
/// reads the keys or the camera. Between bodies it needs a world whose frame crosses with it: offline that is the
/// camera's (follow it), on a server the per-body regions of C3b (#1975). <c>orbit</c> starts in a 400 km orbit (a new
/// shuttle where the bot stands, as the ride menu gives one): a show from orbit, back to an Earth orbit.
/// </para>
/// </summary>
public sealed class ShuttleBrain : IBotBrain
{
    /// <summary>What was asked: to the Moon, the orbit's height, the turns round the Earth, from orbit, the pad.</summary>
    public sealed record Ask(bool Moon, double Km = 400, int Turns = 1, bool FromOrbit = false, string? Pad = null);

    private readonly Ask _ask;
    private VehicleBody? _shuttle;
    private Func<FlightInput>? _saved;
    private bool _done, _parked, _failed;
    private double _since = -1;
    private readonly WanderBrain _after = new();

    public ShuttleBrain(Ask ask) => _ask = ask;

    /// <summary>The pilot once it is at the controls (checks read its stage).</summary>
    public ShuttlePilot? Pilot { get; private set; }

    /// <summary>The mission is over: flown and out (or given up), strolling now.</summary>
    public bool Done => _done;

    /// <summary>Why it gave up, if it did.</summary>
    public string? Failure { get; private set; }

    public string Status => _done ? "shuttle done" : Pilot is { } p ? $"shuttle: {p.Now}" : "shuttle: to the shuttle";

    /// <summary>How far it looks for a parked shuttle, m: an airport's cargo apron from its terminal.</summary>
    public const float FindRange = 5_000f;

    public double Think(Bot bot, double now)
    {
        if (_done) return _after.Think(bot, now);
        var body = bot.Body;
        if (_since < 0) _since = now;
        if (Pilot is { } pilot) return Flying(bot, pilot, now);
        if (_ask.FromOrbit) return FromOrbit(bot);

        // to the nearest parked shuttle, and in at the commander's seat
        if (_shuttle == null || !GodotObject.IsInstanceValid(_shuttle))
        {
            _shuttle = Nearest(bot);
            if (_shuttle == null) return GiveUp(bot, $"no parked shuttle within {FindRange / 1000:F0} km");
            GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" goes to the shuttle {_shuttle.Name}, {body.WhereIs(_shuttle).DistanceTo(body.GlobalPosition):F0} m off");
        }
        if (now - _since > 600) return GiveUp(bot, "could not get to the shuttle in 10 min");
        if (!BotDriving.GetIn(bot, _shuttle, bot.World.Space, now)) return 0.15;
        Start(bot, MissionFor(body, _ask));
        return 0.5;
    }

    /// <summary>The parked shuttle nearest the bot within <see cref="FindRange"/>, free to take; null if none.</summary>
    private static VehicleBody? Nearest(Bot bot)
    {
        if (VehicleManager.Instance is not { } vehicles) return null;
        return vehicles.GetChildren().OfType<VehicleBody>()
            .Where(v => v.Kind == RideKind.Shuttle && !v.Wrecked && vehicles.Enterable(v))
            .Select(v => (v, d: bot.Body.WhereIs(v).DistanceTo(bot.Body.GlobalPosition)))
            .Where(x => x.d < FindRange).OrderBy(x => x.d).Select(x => x.v).FirstOrDefault();
    }

    /// <summary>
    /// The mission from where the shuttle stands: the nearest airport's longest usable runway (<c>World.AirportStands</c>),
    /// the way with more of it ahead of the shuttle, taken off from and landed on; without one, a runway along its own heading.
    /// </summary>
    public static ShuttlePilot.Mission MissionFor(FootPlayer body, Ask ask)
    {
        var origin = body.Origin!;
        var g = origin.ToGlobal(body.GlobalPosition);
        var (e, n) = (g.Lv95.E, g.Lv95.N);
        float yaw = body.Vehicle is Airliner jet ? jet.State.Yaw : body.Rotation.Y;
        float heading = -(origin.Frame.IsAnchor ? TrueNorth.WorldYawToGrid(origin.Frame, body.GlobalPosition, yaw) : yaw);
        var runway = RunwayNear(body, e, n) ?? new ShuttlePilot.Runway(e, n, heading, 3_000f, 30f, g.Alt);
        GD.Print(FormattableString.Invariant($"[bots] {body.Name} flies the shuttle from a runway at {runway.E:F0}/{runway.N:F0}, heading {Mathf.RadToDeg(runway.Heading):F0}°, {runway.Length:F0} m, {runway.Elevation:F0} m up"));
        return ask.Moon
            ? new ShuttlePilot.Mission(runway, runway, Start: (e, n, heading), OrbitKm: ask.Km, RoundTheEarth: false, CoastSeconds: 10,
                Moon: true, Pad: ask.Pad, LandHome: !ask.FromOrbit)
            : new ShuttlePilot.Mission(runway, runway, Start: (e, n, heading), OrbitKm: ask.Km, RoundTheEarth: true, Turns: ask.Turns);
    }

    /// <summary>The nearest airport's longest usable runway within 5 km of (e, n), oriented with more of it ahead; null without one.</summary>
    private static ShuttlePilot.Runway? RunwayNear(FootPlayer body, double e, double n)
    {
        if (World.AirportStands.Instance?.Index is not { } index) return null;
        var best = index.Airports.Where(a => Math.Sqrt((a.E - e) * (a.E - e) + (a.N - n) * (a.N - n)) < FindRange)
            .SelectMany(a => a.Runways).Where(r => r.Usable).OrderByDescending(r => r.Length).FirstOrDefault();
        if (best == null) return null;
        var forward = new ShuttlePilot.Runway(best.E1, best.N1, (float)Math.Atan2(best.E2 - best.E1, best.N2 - best.N1), (float)best.Length, (float)best.Width * 0.5f, 0);
        var way = forward.Of(e, n).Along <= best.Length * 0.5 ? forward
            : new ShuttlePilot.Runway(best.E2, best.N2, (float)Math.Atan2(best.E1 - best.E2, best.N1 - best.N2), (float)best.Length, (float)best.Width * 0.5f, 0);
        return way with { Elevation = GroundAt(body, way.E, way.N) };
    }

    /// <summary>The ground's height over the sea at LV95 (e, n), m: the terrain there if this peer has it, else the body's own.</summary>
    private static double GroundAt(FootPlayer body, double e, double n)
    {
        var origin = body.Origin!;
        var at = origin.ToWorld(e, n, 0);
        if (body.Terrain is { } t && t.TryGetHeight(at, out float y)) return origin.AltitudeOf(at with { Y = y });
        return origin.AltitudeOf(body.GlobalPosition);
    }

    /// <summary><c>orbit</c>: a shuttle where the bot stands, put in a 400 km orbit (<see cref="FootPlayer.PutInOrbit"/>), the mission from there.</summary>
    private double FromOrbit(Bot bot)
    {
        var body = bot.Body;
        if (!body.IsOnFloor()) return 0.3;
        if (body.Vehicle is not Airliner { Kind: RideKind.Shuttle })
        {
            if (!body.SetRide(RideKind.Shuttle)) return GiveUp(bot, "no shuttle to take");
            return 0.5;
        }
        var mission = MissionFor(body, _ask);
        GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\": {body.PutInOrbit(_ask.Km)}");
        Start(bot, mission);
        Pilot!.StartInOrbit();
        return 0.5;
    }

    /// <summary>At the controls: the pilot flies from the body's own controls, every physics step.</summary>
    private void Start(Bot bot, ShuttlePilot.Mission mission)
    {
        var body = bot.Body;
        var pilot = Pilot = new ShuttlePilot(mission)
        {
            OwnsControls = false,
            Changed = (from, to) => GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" shuttle: {from} -> {to}"),
            Log = line => GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" shuttle: {line}"),
        };
        _saved = body.FlyControls;
        body.FlyControls = () =>
        {
            if (body.Vehicle is Airliner jet && body.Origin is { } origin)
            {
                double alt = origin.AltitudeOf(body.GlobalPosition);
                float agl = body.Clearance < 999f ? body.Clearance : (float)(alt - mission.To.Elevation);
                pilot.Step(body, jet, origin.Frame, agl, (float)body.GetPhysicsProcessDeltaTime());
                return pilot.Input;
            }
            return _saved?.Invoke() ?? default;
        };
        GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" at the shuttle's controls: {(mission.Moon ? "to the Moon" : $"{mission.Turns} turn(s) round the Earth")} at {mission.OrbitKm:F0} km");
    }

    private double Flying(Bot bot, ShuttlePilot pilot, double now)
    {
        var body = bot.Body;
        // out of it already (stood up into the cabin): out by the hatch once its deck is here
        if (_parked)
        {
            if (body.Aboard && body.OutByShuttleHatch()) return Finish(bot, "out by the hatch");
            if (body.Vehicle == null && !body.Aboard && now - _since > 10) return Finish(bot, "out");
            return 0.3;
        }
        if (body.Vehicle is not Airliner jet) return Finish(bot, "no longer at the controls");
        switch (pilot.Now)
        {
            case ShuttlePilot.Stage.Stopped when jet.State.OnGround:
                Park(bot);
                _since = now;
                return 0.3;
            case ShuttlePilot.Stage.Failed when !_failed:
                _failed = true;
                Failure = pilot.Failure;
                GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" shuttle: gave up: {pilot.Failure}");
                // on the ground it gets out; in the air it lets go of the controls (the craft flies on, as a player's would)
                if (jet.State.OnGround) { Park(bot); _since = now; }
                return 0.5;
        }
        return 0.5;
    }

    /// <summary>Lets go of the controls and stands up into the cabin (the shuttle parked where it stopped).</summary>
    private void Park(Bot bot)
    {
        var body = bot.Body;
        body.FlyControls = _saved;
        _parked = true;
        body.ExitVehicle();
        GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" parked the shuttle and stood up");
    }

    private double GiveUp(Bot bot, string why)
    {
        Failure = why;
        return Finish(bot, why);
    }

    private double Finish(Bot bot, string why)
    {
        if (Pilot != null && bot.Body.FlyControls != _saved) bot.Body.FlyControls = _saved;
        _done = true;
        GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" shuttle mission over: {why}");
        return 0.5;
    }
}
