using System.Collections.Generic;
using Godot;

namespace UnitSport.Interiors;

/// <summary>
/// The rooms of an interior's floor as a graph (#1330), for bots choosing where to go: its edges are the doorways and
/// arches between them, in plan coordinates (x, z). A room's neighbour through an opening is the room just beyond it
/// (half a metre past its centre), so a cut whose <see cref="OpeningPlan.Other"/> was never set still links. Windows
/// and street entries are no ways through. The walking itself goes over the interior's baked surface
/// (<see cref="InteriorManager.BotPath"/>), which knows stairs, holes and furniture.
/// </summary>
public static class InteriorNav
{
    /// <summary>The room of <paramref name="floor"/> holding a plan point, or −1.</summary>
    public static int RoomAt(FloorPlan floor, Vector2 at)
    {
        for (int i = 0; i < floor.Rooms.Count; i++)
            if (floor.Rooms[i].Holds(at.X, at.Y)) return i;
        return -1;
    }

    /// <summary>The ways out of a room on its floor: the opening's centre, its outward normal, and the room beyond.</summary>
    public static IEnumerable<(Vector2 At, Vector2 Out, int Room)> Ways(FloorPlan floor, int room)
    {
        var r = floor.Rooms[room];
        foreach (var o in r.Openings)
        {
            if (o.Kind is not (OpeningKind.Door or OpeningKind.Arch) || o.Width < 0.6f) continue;
            var (at, outward, _) = r.WallFrame(o);
            int next = RoomAt(floor, at + outward * 0.5f);
            if (next >= 0 && next != room) yield return (at, outward, next);
        }
    }

    /// <summary>The rooms reachable from <paramref name="start"/> on a floor, nearest (in openings) first.</summary>
    public static List<int> Reachable(FloorPlan floor, int start)
    {
        var seen = new List<int> { start };
        for (int i = 0; i < seen.Count; i++)
            foreach (var (_, _, next) in Ways(floor, seen[i]))
                if (!seen.Contains(next)) seen.Add(next);
        return seen;
    }

    /// <summary>A plan point in the middle of a room's floor (its rectangle's centre if it is in the room, else its first corner pulled in).</summary>
    public static Vector2 Middle(RoomPlan r)
    {
        var c = new Vector2((r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2);
        if (r.Holds(c.X, c.Y)) return c;
        var corners = r.Corners();
        var sum = Vector2.Zero;
        foreach (var p in corners) sum += p;
        return sum / corners.Length;
    }
}
