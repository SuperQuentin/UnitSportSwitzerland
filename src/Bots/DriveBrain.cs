using Godot;

namespace UnitSport.Bots;

/// <summary>
/// A mode-free errand (#837 phase 3, <c>/bot drive</c>): to a point, taking the nearest car or bike in sight
/// when there is one (<see cref="BotDriving"/>), else on foot; out of the vehicle at the end, then a stroll
/// round there (<see cref="WanderBrain"/>). The free-roam check of driving, and a small example of a brain.
/// </summary>
public sealed class DriveBrain : IBotBrain
{
    private readonly Vector3 _goal;
    private Vehicles.VehicleBody? _car;
    private bool _done, _drove;
    private readonly WanderBrain _after = new();

    public DriveBrain(Vector3 goal) => _goal = goal;

    public string Status => _done ? "drive done" : _drove ? "drive: driving" : "drive: on foot";

    public double Think(Bot bot, double now)
    {
        if (_done) return _after.Think(bot, now);
        var space = bot.World.Space;
        if (BotDriving.Mounted(bot))
        {
            _drove = true;
            if (bot.Drive.Stopping || BotDriving.Towards(bot, _goal, now) is BotDriving.Progress.Arrived or BotDriving.Progress.Failed)
                if (BotDriving.GetOut(bot)) _done = true;
            return 0.15;
        }
        if (!_drove)
        {
            if (_car == null || !GodotObject.IsInstanceValid(_car)) _car = BotDriving.Spot(bot, 70f);
            if (_car != null)
            {
                BotDriving.GetIn(bot, _car, space, now);
                return 0.15;
            }
        }
        if (BotSkills.MoveTo(bot, _goal, run: true, BotDriving.Arrive, space, now)) _done = true;
        return 0.2;
    }
}
