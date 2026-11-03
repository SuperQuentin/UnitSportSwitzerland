using System;
using Godot;

namespace UnitSport.Bots;

/// <summary>
/// The phase 1 brain (#837): a target that strolls round where it was put, stopping now and then,
/// so a remote peer sees it walk, turn and stand, and a player has something alive to shoot. No
/// senses, no goals: <c>BrBrain</c> replaces it in phase 2.
/// </summary>
public sealed class WanderBrain : IBotBrain
{
    /// <summary>How far from home it strolls.</summary>
    public const float Range = 25f;
    private const float Arrived = 1.5f;

    /// <summary>Walking this long without covering <see cref="StuckMetres"/>: the goal is out of reach, pick another.</summary>
    private const double StuckSeconds = 3;
    private const float StuckMetres = 1f;

    private Vector3? _home;
    private Vector3? _goal;
    private double _restUntil;
    private Random? _rng;
    private Vector3 _progressFrom;
    private double _progressAt;

    public double Think(Bot bot, double now)
    {
        var body = bot.Body;
        _rng ??= new Random(bot.N * 7349);
        _home ??= body.GlobalPosition;
        bot.Intent = BotIntent.Stand;
        if (body.Eliminated || body.KnockedOut) return 0.5;
        // a UFO sighting (#863): look up and wave, or run out from under a beam
        if (BotUfo.React(bot, now)) return 0.25;
        // a player's birthday near (#1423): walk up and wish them a happy birthday
        if (BotBirthday.React(bot, now)) return 0.25;
        if (now < _restUntil) return 0.5;

        // pressed against a wall or a house: a new goal, from where it stands
        if (_goal != null && now - _progressAt > StuckSeconds)
        {
            if (Flat(body.GlobalPosition - _progressFrom).Length() < StuckMetres) _goal = null;
            _progressFrom = body.GlobalPosition;
            _progressAt = now;
        }
        if (_goal is not { } goal || Flat(goal - body.GlobalPosition).Length() < Arrived)
        {
            if (_goal != null && _rng.NextDouble() < 0.5)
            {
                _goal = null;
                _restUntil = now + 2 + _rng.NextDouble() * 4;   // a pause, looking round
                return 0.5;
            }
            double a = _rng.NextDouble() * Math.Tau, r = Range * Math.Sqrt(_rng.NextDouble());
            _goal = goal = _home.Value + new Vector3((float)(Math.Cos(a) * r), 0, (float)(Math.Sin(a) * r));
            _progressFrom = body.GlobalPosition;
            _progressAt = now;
        }
        var to = Flat(goal - body.GlobalPosition);
        bot.Intent.Wish = to.LengthSquared() > 0.01f ? to.Normalized() : Vector3.Zero;
        return 0.2;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
}
