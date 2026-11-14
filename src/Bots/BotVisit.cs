using System;
using System.Collections.Generic;
using Godot;
using UnitSport.Interiors;

namespace UnitSport.Bots;

/// <summary>
/// A bot's visit to a building (#1330), a skill any brain may run: the nearest street door with a way in, through it
/// (the server opens it and builds the interior's collision in the bot's region, <see cref="InteriorManager.BotWantIn"/>),
/// to a room as far in as the ground floor goes (<see cref="InteriorNav"/>, rooms and doorways as a graph), a pause
/// there, and back out by the same door. Everything on the bot's own seams: <see cref="BotSkills.MoveTo"/> for the
/// walking, the door's map for the crossing (<see cref="InteriorManager.BotCross"/>). A locked door (#1766), a plan that
/// will not come, or a walk that makes no headway gives up: <see cref="Think"/> answers done, outside again if it can.
/// </summary>
public sealed class BotVisit
{
    private enum Phase { Find, ToDoor, In, Walk, Linger, Back, Out, Done }

    /// <summary>How far the doors are looked for.</summary>
    public const float FindRadius = 70f;
    /// <summary>Any one phase taking longer than this gives the visit up.</summary>
    private const double PhaseSeconds = 45;

    private Phase _phase;
    private double _phaseAt = double.NaN;
    private string? _door;
    private Vector3 _doorAt;
    private readonly List<Vector3> _route = new();
    private int _leg;
    private double _lingerUntil;
    private readonly Random _rng;
    private readonly HashSet<string> _tried;

    /// <summary>A visit; <paramref name="tried"/> are doors not to try again (shared across a bot's visits).</summary>
    public BotVisit(Random rng, HashSet<string> tried)
    {
        _rng = rng;
        _tried = tried;
    }

    /// <summary>The door it went in by, once it has one.</summary>
    public string? Door => _door;

    /// <summary>What it is doing, for logs.</summary>
    public string State => _phase.ToString();

    /// <summary>
    /// One thought. True once the visit is over (back outside, or given up); the intent is then left standing.
    /// </summary>
    public bool Think(Bot bot, double now, PhysicsDirectSpaceState3D space)
    {
        var body = bot.Body;
        var im = InteriorManager.Instance;
        bot.Intent = BotIntent.Stand;
        if (im == null) return true;
        if (double.IsNaN(_phaseAt)) _phaseAt = now;
        if (now - _phaseAt > PhaseSeconds && _phase != Phase.Linger)
        {
            string where = im.BotInterior(body) is var (gl, gp) ? $"plan ({PlanPoint(gp, body.GlobalPosition).X:F1}, {PlanPoint(gp, body.GlobalPosition).Y:F1}) room {InteriorNav.RoomAt(gl.GroundFloor, PlanPoint(gp, body.GlobalPosition))}" : "outside";
            string leg = _leg < _route.Count ? $"leg {_leg}/{_route.Count} {body.GlobalPosition.DistanceTo(_route[_leg]):F1} m off" : $"{_route.Count} legs";
            GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" gives up its visit in {_phase} ({_door}): {where}, {leg}, wish {bot.Intent.Wish}, v {body.Velocity.Length():F1}");
            if (_door != null) _tried.Add(_door);
            // inside, the way out is still the point: a last go at the door, then done
            if (body.Indoors && _phase != Phase.Out) { Go(Phase.Back, now); return false; }
            return true;
        }
        switch (_phase)
        {
            case Phase.Find:
            {
                (string Door, Vector3 At, Vector3 Out)? best = null;
                float bestD = float.MaxValue;
                foreach (var d in im.BotDoorsNear(body, FindRadius))
                {
                    if (_tried.Contains(d.Door)) continue;
                    float dist = body.GlobalPosition.DistanceTo(d.At);
                    if (dist < bestD) (best, bestD) = (d, dist);
                }
                // the tiles' doors are worked out in the background: ask again in a while, then give up
                if (best is not { } b) return now - _phaseAt > 6;
                (_door, _doorAt) = (b.Door, b.At + b.Out * 1.5f);
                Go(Phase.ToDoor, now);
                return false;
            }
            case Phase.ToDoor:
            {
                var state = im.BotWantIn(body, _door!);
                if (state == BotDoorState.Locked)
                {
                    _tried.Add(_door!);
                    Go(Phase.Find, now);
                    return false;
                }
                // ready: the doorstep from the door's own frame (on the facade, the street at +Z)
                var step = state == BotDoorState.Ready && im.BotDoor(body, _door!) is { } link ? link.Outside * new Vector3(0, 0, 1.2f) : _doorAt;
                if (BotSkills.MoveTo(bot, step, run: false, 0.5f, space, now) && state == BotDoorState.Ready) Go(Phase.In, now);
                return false;
            }
            case Phase.In:
            {
                im.BotWantIn(body, _door!);
                if (im.BotCross(body, bot.Id, _door!) || body.Indoors)
                {
                    PlanRoute(bot, im);
                    Go(_route.Count > 0 ? Phase.Walk : Phase.Linger, now);
                    _lingerUntil = now + 3;
                    return false;
                }
                if (im.BotDoor(body, _door!) is { } link) BotSkills.MoveTo(bot, link.Outside * new Vector3(0, 0, -0.3f), run: false, 0.1f, space, now);
                return false;
            }
            case Phase.Walk:
            case Phase.Back:
            {
                if (_leg >= _route.Count)
                {
                    if (_phase == Phase.Walk)
                    {
                        _lingerUntil = now + 4 + _rng.NextDouble() * 6;
                        Go(Phase.Linger, now);
                    }
                    else Go(Phase.Out, now);
                    return false;
                }
                if (BotSkills.MoveTo(bot, _route[_leg], run: false, 0.45f, space, now)) _leg++;
                return false;
            }
            case Phase.Linger:
                if (now < _lingerUntil) return false;
                RouteBack(bot, im);
                Go(_route.Count > 0 ? Phase.Back : Phase.Out, now);
                return false;
            case Phase.Out:
            {
                if (!body.Indoors) { Go(Phase.Done, now); return true; }
                im.BotWantIn(body, _door!);   // the door open, the interior kept, until it is out
                if (im.BotCross(body, bot.Id, _door!)) { Go(Phase.Done, now); return true; }
                if (im.BotDoor(body, _door!) is { } link) BotSkills.MoveTo(bot, link.Inside * new Vector3(0, 0, 0.3f), run: false, 0.1f, space, now);
                return false;
            }
            default:
                return true;
        }
    }

    private void Go(Phase phase, double now)
    {
        _phase = phase;
        _phaseAt = now;
        _leg = 0;
    }

    /// <summary>From where it stands in the doorway to the middle of a room as far in as the floor goes.</summary>
    private void PlanRoute(Bot bot, InteriorManager im)
    {
        _route.Clear();
        if (im.BotInterior(bot.Body) is not var (layout, placement)) return;
        var floor = layout.GroundFloor;
        var at = PlanPoint(placement, bot.Body.GlobalPosition);
        int start = InteriorNav.RoomAt(floor, at);
        if (start < 0) return;
        var rooms = InteriorNav.Reachable(floor, start);
        // the far end, or somewhere on the way there
        int goal = rooms[rooms.Count - 1 - _rng.Next(Math.Min(2, rooms.Count))];
        Route(layout, placement, at, InteriorNav.Middle(floor.Rooms[goal]));
    }

    /// <summary>From where it lingers back to a step inside the door it came in by.</summary>
    private void RouteBack(Bot bot, InteriorManager im)
    {
        _route.Clear();
        if (im.BotInterior(bot.Body) is not var (layout, placement) || im.BotDoor(bot.Body, _door!) is not { } link) return;
        var step = PlanPoint(placement, link.Inside * new Vector3(0, 0, -0.8f));
        Route(layout, placement, PlanPoint(placement, bot.Body.GlobalPosition), step);
    }

    private void Route(InteriorLayout layout, Transform3D placement, Vector2 from, Vector2 to)
    {
        var floor = layout.GroundFloor;
        float y = layout.FloorY(Math.Min(layout.Below, layout.Floors.Count - 1));
        foreach (var p in InteriorNav.Path(floor, from, to) ?? [to])
            _route.Add(placement * new Vector3(p.X, y, p.Y));
    }

    private static Vector2 PlanPoint(Transform3D placement, Vector3 world)
    {
        var local = placement.AffineInverse() * world;
        return new Vector2(local.X, local.Z);
    }
}
