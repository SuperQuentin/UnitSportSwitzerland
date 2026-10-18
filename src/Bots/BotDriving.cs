using System.Threading.Tasks;
using Godot;
using UnitSport.Player;
using UnitSport.Terrain.Format;
using UnitSport.Vehicles;

namespace UnitSport.Bots;

/// <summary>A bot's drive under way (<see cref="BotDriving"/>): the route being planned, the pilot, where to.</summary>
public sealed class BotDrive
{
    public Task<RaceRoute?>? Planning;
    public AutoPilot? Pilot;
    public Vector3 Goal;
    /// <summary>Braking to get out: the pilot's <c>Finished</c>, then out once slow.</summary>
    public bool Stopping;
    /// <summary>Since when it has made no headway (a car on its nose, in a hedge): it gets out and walks.</summary>
    public double StillSince;
}

/// <summary>
/// Driving (#837 phase 3), a skill every mode's brain can use: spot a parked car or bike in sight, walk to
/// it, get in through the server's claim (<see cref="VehicleManager.ServerClaim"/>, the checks a player's
/// request has), plan a road route there off the main thread (<see cref="RaceRoute.BuildAsync"/>, minor roads
/// and up, no stairs or tunnels), drive it with the race NPCs' pilot (<see cref="AutoPilot"/>, through
/// <c>RideControls</c>), and brake and get out (<see cref="FootPlayer.ExitVehicle"/>, parked again for anyone,
/// under its own name). The bot is a passenger of none of this: it reads where cars are as a player sees them.
/// </summary>
public static class BotDriving
{
    /// <summary>How close it gets in from (a player's reach, about).</summary>
    public const float Reach = 3.2f;
    /// <summary>Within this of where it was going, or of the route's end, it stops and gets out.</summary>
    public const float Arrive = 45f;
    /// <summary>Slower than this for <see cref="StuckSeconds"/> while it means to drive: stuck, out it gets.</summary>
    private const float StuckSpeed = 1f;
    private const double StuckSeconds = 6;

    /// <summary>Whether it sits in (or on) a vehicle it could drive.</summary>
    public static bool Mounted(Bot bot) => bot.Body.Vehicle is { IsVehicle: true };

    /// <summary>
    /// The nearest parked vehicle it could drive and can see, within <paramref name="range"/> m: not burnt
    /// out, not being claimed, a kind the pilot drives (cars, motorbikes, bicycles).
    /// </summary>
    public static VehicleBody? Spot(Bot bot, float range)
    {
        if (VehicleManager.Instance is not { } vehicles) return null;
        var eye = bot.Body.EyePosition;
        VehicleBody? best = null;
        float bestD = range;
        foreach (var node in vehicles.GetChildren())
        {
            // the cheap test first: most of a match's vehicles are far off
            if (node is not VehicleBody v) continue;
            float d = v.GlobalPosition.DistanceTo(bot.Body.GlobalPosition);
            if (d >= bestD || v.Kind == RideKind.OnFoot || !vehicles.Enterable(v) || !AutoPilot.Drives(v.Kind)) continue;
            // in sight, or close enough to know it is there
            if (d > 25f && !BotRays.Clear(bot.World.Space, eye, v.GlobalPosition + Vector3.Up * 0.8f, bot.Body.GetRid())) continue;
            best = v;
            bestD = d;
        }
        return best;
    }

    /// <summary>Walks to <paramref name="car"/> and gets in. True once in it (or false while still walking, or when someone was quicker).</summary>
    public static bool GetIn(Bot bot, VehicleBody car, PhysicsDirectSpaceState3D space, double now)
    {
        if (!GodotObject.IsInstanceValid(car)) return false;
        var entry = car.Ride.EntryPoint != Vector3.Zero ? car.ToGlobal(car.Ride.EntryPoint) : car.GlobalPosition;
        if (entry.DistanceTo(bot.Body.GlobalPosition) > Reach + car.Ride.ParkedBox.Size.X * 0.5f)
        {
            BotSkills.MoveTo(bot, entry, run: true, 1.5f, space, now);
            return false;
        }
        bot.Intent.Wish = Vector3.Zero;
        if (VehicleManager.Instance?.ServerClaim(car) is not { } state) return false;
        bot.Body.TakeVehicle(state, 0);
        bot.Drive.Pilot = null;
        bot.Drive.Planning = null;
        bot.Drive.Stopping = false;
        bot.Drive.StillSince = now;
        GD.Print($"[bots] {bot.Body.Name} \"{bot.Profile.Name}\" got into {state.Name} ({state.Kind})");
        return true;
    }

    public enum Progress { Planning, Driving, Arrived, Failed }

    /// <summary>
    /// In a vehicle, towards <paramref name="goal"/>: plans the route the first time (off the main thread),
    /// then lets the pilot drive. <see cref="Progress.Arrived"/> near the goal or the route's end,
    /// <see cref="Progress.Failed"/> with no route or stuck: either way the caller gets out (<see cref="GetOut"/>).
    /// </summary>
    public static Progress Towards(Bot bot, Vector3 goal, double now)
    {
        var body = bot.Body;
        var drive = bot.Drive;
        if (drive.Pilot == null)
        {
            if (drive.Planning == null)
            {
                if (body.Terrain?.Source is not { } source || body.Origin is not { } origin) return Progress.Failed;
                drive.Goal = goal;
                drive.Planning = RaceRoute.BuildAsync(source, origin, body.GlobalPosition, default, RoadClass.Minor, goal);
                return Progress.Planning;
            }
            if (!drive.Planning.IsCompleted) return Progress.Planning;
            if (!drive.Planning.IsCompletedSuccessfully || drive.Planning.Result is not { } route || AutoPilot.For(route, body) is not { } pilot)
                return Progress.Failed;
            drive.Pilot = pilot;
            drive.StillSince = now;
            GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" drives {route.Centre.Count} points towards {goal}");
        }
        var pos = body.GlobalPosition;
        var end = drive.Pilot.Route.Centre[^1];
        if (Flat(goal - pos) < Arrive || Flat(end - pos) < Arrive * 0.5f) return Progress.Arrived;
        if (body.RideSpeed > StuckSpeed) drive.StillSince = now;
        else if (now - drive.StillSince > StuckSeconds) return Progress.Failed;
        return Progress.Driving;
    }

    /// <summary>Brakes (the pilot's finish), then gets out once slow. True once on foot.</summary>
    public static bool GetOut(Bot bot)
    {
        var body = bot.Body;
        if (!Mounted(bot)) { Forget(bot); return true; }
        bot.Drive.Stopping = true;
        if (bot.Drive.Pilot is { } pilot) pilot.Finished = true;
        if (body.RideSpeed > 1.5f) return false;
        body.ExitVehicle();
        GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" got out");
        Forget(bot);
        return true;
    }

    private static void Forget(Bot bot)
    {
        bot.Drive.Pilot = null;
        bot.Drive.Planning = null;
        bot.Drive.Stopping = false;
    }

    /// <summary>What the body's <c>RideControls</c> read: the pilot when there is one, else the handbrake.</summary>
    public static RideInput Controls(Bot bot, float dt) =>
        bot.Drive.Pilot is { } pilot ? pilot.Drive(dt, true, World.RaceManager.Others(bot.Body)) : new RideInput(0f, 0f, 0f, false, Handbrake: true);

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();
}
