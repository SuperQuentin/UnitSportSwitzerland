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
    public const float FindRadius = 150f;
    /// <summary>Any one phase taking longer than this gives the visit up.</summary>
    private const double PhaseSeconds = 45;

    private Phase _phase;
    private double _phaseAt = double.NaN;
    private string? _door;
    private Vector3 _doorAt;
    private readonly List<Vector3> _route = new();
    private int _leg;
    private double _lingerUntil;
    private float _toDoorMetres;
    private Vector3 _lastWish;
    private bool _routed;
    private float _best = float.MaxValue;
    private double _bestAt;

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Z).Length();

    /// <summary>Where it stands and the leg are in the plan, and what rays at four heights towards the leg meet (a stuck walk's log).</summary>
    private static string Diagnose(Bot bot, InteriorManager im, PhysicsDirectSpaceState3D space, Vector3 leg)
    {
        var body = bot.Body;
        var pos = body.GlobalPosition;
        var sb = new System.Text.StringBuilder();
        if (im.BotInterior(body) is var (_, placement))
        {
            var a = placement.AffineInverse() * pos;
            var b = placement.AffineInverse() * leg;
            sb.Append($"at plan ({a.X:F2}, {a.Y:F2}, {a.Z:F2}), leg ({b.X:F2}, {b.Y:F2}, {b.Z:F2})");
            foreach (float h in new[] { 0.1f, 0.3f, 0.6f, 1.2f })
            {
                var from = pos + Vector3.Up * h;
                var to = leg with { Y = from.Y };
                var hit = BotRays.Cast(space, from, to, body.GetRid());
                if (hit.Count == 0) { sb.Append($"; {h:F1} m clear"); continue; }
                var at = placement.AffineInverse() * hit["position"].AsVector3();
                var n = placement.Basis.Inverse() * hit["normal"].AsVector3();
                sb.Append($"; {h:F1} m hits at {from.DistanceTo(hit["position"].AsVector3()):F2} m, plan ({at.X:F2}, {at.Y:F2}, {at.Z:F2}) normal ({n.X:F1}, {n.Y:F1}, {n.Z:F1})");
            }
        }
        sb.Append($"; v {body.Velocity.Length():F2}, floor {body.IsOnFloor()}, wish {bot.Intent.Wish}");
        return sb.ToString();
    }
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
        // the walk to the door gets time for its length too (walking pace, round gardens and fences)
        double allowed = PhaseSeconds + (_phase == Phase.ToDoor ? _toDoorMetres / 1.2 : 0);
        if (now - _phaseAt > allowed && _phase != Phase.Linger)
        {
            string where = im.BotInterior(body) is var (gl, gp) ? $"plan ({PlanPoint(gp, body.GlobalPosition).X:F1}, {PlanPoint(gp, body.GlobalPosition).Y:F1}) room {InteriorNav.RoomAt(gl.GroundFloor, PlanPoint(gp, body.GlobalPosition))}" : "outside";
            string leg = _leg < _route.Count ? $"leg {_leg}/{_route.Count} {body.GlobalPosition.DistanceTo(_route[_leg]):F1} m off" : $"{_route.Count} legs";
            if (_leg < _route.Count)
            {
                // what stands between it and the leg: the first thing a knee-high ray meets
                var from = body.GlobalPosition + Vector3.Up * 0.45f;
                var to = _route[_leg] with { Y = from.Y };
                var hit = BotRays.Cast(space, from, to, body.GetRid());
                leg += hit.Count == 0 ? ", the way clear" : $", blocked at {from.DistanceTo(hit["position"].AsVector3()):F1} m by {(hit["collider"].AsGodotObject() as Node)?.Name}";
                leg += $", body y {body.GlobalPosition.Y:F2} leg y {_route[_leg].Y:F2}, nav stuck {bot.Nav.Stuck} following {bot.Nav.Following}, last wish {_lastWish}";
                // caught in something: a body-sized capsule at its place, and what it overlaps
                var q = new PhysicsShapeQueryParameters3D
                {
                    Shape = new CapsuleShape3D { Radius = 0.3f, Height = 1.7f },
                    Transform = new Transform3D(Basis.Identity, body.GlobalPosition + Vector3.Up * 0.9f),
                    Exclude = [body.GetRid()],
                };
                var over = space.IntersectShape(q, 4);
                leg += over.Count == 0 ? ", overlapping nothing" : $", overlapping {over.Count} ({string.Join(", ", System.Linq.Enumerable.Select(over, o => (o["collider"].AsGodotObject() as Node)?.Name.ToString() ?? "?"))})";
            }
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
                if (best is not { } b)
                {
                    if (now - _phaseAt <= 6) return false;
                    GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" finds no door to visit within {FindRadius:F0} m");
                    return true;
                }
                (_door, _doorAt) = (b.Door, b.At + b.Out * 1.5f);
                _toDoorMetres = bestD;
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
                if (BotSkills.MoveTo(bot, step, run: false, 0.9f, space, now) && state == BotDoorState.Ready) Go(Phase.In, now);
                return false;
            }
            case Phase.In:
            {
                im.BotWantIn(body, _door!);
                if (im.BotCross(body, bot.Id, _door!) || body.Indoors)
                {
                    Go(Phase.Walk, now);
                    return false;
                }
                // the last metre straight at the sill: MoveTo would take the facade beside the doorway for a wall and follow it
                if (im.BotDoor(body, _door!) is { } link) Straight(bot, link.Outside * new Vector3(0, 0, -0.3f));
                return false;
            }
            case Phase.Walk:
            case Phase.Back:
            {
                // the way from the walkable surface (stairs, holes and furniture in it): a map takes a sync or two to know
                // the interior after it was placed, so it is asked again for a few seconds
                if (!_routed)
                {
                    _leg = 0;
                    _routed = _phase == Phase.Walk ? PlanRoute(bot, im) : RouteBack(bot, im);
                    if (!_routed && now - _phaseAt < 5) return false;
                    _routed = true;
                    if (_route.Count == 0)
                    {
                        if (_phase == Phase.Walk) { _lingerUntil = now + 3; Go(Phase.Linger, now); }
                        else Go(Phase.Out, now);
                        return false;
                    }
                }
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
                // along the surface's points, straight: the path keeps clear of walls and furniture by construction, and
                // MoveTo's 2.2 m wall ray met a wall before nearly every corner and followed it off the path. No headway in
                // 2 s: the way asked again from where it stands
                var pos = body.GlobalPosition;
                if (Flat(_route[_leg] - pos) < 0.35f) { _leg++; _bestAt = now; _best = float.MaxValue; return false; }
                float d = Flat(_route[_leg] - pos);
                if (d < _best - 0.2f) (_best, _bestAt) = (d, now);
                else if (now - _bestAt > 2)
                {
                    GD.Print($"[bots] {body.Name} \"{bot.Profile.Name}\" no headway to leg {_leg}/{_route.Count} ({d:F1} m): the way again; {Diagnose(bot, im, space, _route[_leg])}");
                    (_routed, _best, _bestAt) = (false, float.MaxValue, now);
                    return false;
                }
                Straight(bot, _route[_leg]);
                _lastWish = bot.Intent.Wish;
                return false;
            }
            case Phase.Linger:
                if (now < _lingerUntil) return false;
                Go(Phase.Back, now);
                return false;
            case Phase.Out:
            {
                if (!body.Indoors) { Go(Phase.Done, now); return true; }
                im.BotWantIn(body, _door!);   // the door open, the interior kept, until it is out
                if (im.BotCross(body, bot.Id, _door!)) { Go(Phase.Done, now); return true; }
                if (im.BotDoor(body, _door!) is { } link) Straight(bot, link.Inside * new Vector3(0, 0, 0.3f));
                return false;
            }
            default:
                return true;
        }
    }

    /// <summary>A plain wish at a point, flat: the step through a doorway, where any wall in the way is the doorway's own jamb.</summary>
    private static void Straight(Bot bot, Vector3 target)
    {
        var to = target - bot.Body.GlobalPosition;
        to.Y = 0;
        bot.Intent.Wish = to.LengthSquared() > 0.0001f ? to.Normalized() : Vector3.Zero;
        bot.Intent.Run = false;
    }

    private void Go(Phase phase, double now)
    {
        _phase = phase;
        _phaseAt = now;
        _leg = 0;
        _routed = false;
        _route.Clear();
    }

    /// <summary>
    /// To the middle of a room as far in as the doorways go (<see cref="InteriorNav.Reachable"/>, the far end first), over
    /// the interior's walkable surface (<see cref="InteriorManager.BotPath"/>): the first room it reaches. False while the
    /// surface is not on the map yet.
    /// </summary>
    private bool PlanRoute(Bot bot, InteriorManager im)
    {
        _route.Clear();
        if (im.BotInterior(bot.Body) is not var (layout, placement)) return false;
        var floor = layout.GroundFloor;
        float y = layout.FloorY(Math.Min(layout.Below, layout.Floors.Count - 1));
        var pos = bot.Body.GlobalPosition;
        int start = InteriorNav.RoomAt(floor, PlanPoint(placement, pos));
        var rooms = start < 0 ? new List<int>() : InteriorNav.Reachable(floor, start);
        rooms.Reverse();
        // now and then one on the way, not always the very far end
        if (rooms.Count > 1 && _rng.Next(3) == 0) (rooms[0], rooms[1]) = (rooms[1], rooms[0]);
        foreach (int room in rooms)
        {
            var mid = InteriorNav.Middle(floor.Rooms[room]);
            var goal = placement * new Vector3(mid.X, y, mid.Y);
            var path = im.BotPath(bot.Body, pos, goal);
            if (path.Length == 0) return false;   // not on the map yet: asked again
            if (path[^1].DistanceTo(goal) > 1.2f) continue;   // a room the surface does not reach (behind furniture, up a stair)
            Take(path);
            GD.Print($"[bots] visit route in {layout.Key}: room {start} -> room {room} ({floor.Rooms[room].Type}), {_route.Count} points");
            return true;
        }
        return true;
    }

    /// <summary>From wherever it is inside (a cellar it fell into included) back to a step inside the door it came in by.</summary>
    private bool RouteBack(Bot bot, InteriorManager im)
    {
        _route.Clear();
        if (im.BotDoor(bot.Body, _door!) is not { } link) return false;
        var step = link.Inside * new Vector3(0, 0, -0.8f);
        var path = im.BotPath(bot.Body, bot.Body.GlobalPosition, step);
        if (path.Length == 0) return false;
        Take(path);
        _route.Add(step);
        return true;
    }

    /// <summary>A path's points as legs: the start (where it stands) left out, points closer than 0.4 m merged.</summary>
    private void Take(Vector3[] path)
    {
        for (int i = 1; i < path.Length; i++)
            if (_route.Count == 0 || _route[^1].DistanceTo(path[i]) > 0.4f || i == path.Length - 1) _route.Add(path[i]);
    }

    private static Vector2 PlanPoint(Transform3D placement, Vector3 world)
    {
        var local = placement.AffineInverse() * world;
        return new Vector2(local.X, local.Z);
    }
}
