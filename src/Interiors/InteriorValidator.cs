using System.Globalization;
using System.Text;

namespace UnitSport.Interiors;

/// <summary>
/// Checks the properties that make a plan walkable rather than merely drawn: rooms inside the
/// building and not overlapping, every doorway cut on both sides, every room reachable from the
/// front door, a flight between every pair of floors, furniture inside its room and out of the
/// doorways. The generator is built to satisfy all of these; this is how that claim is tested.
/// </summary>
public static class InteriorValidator
{
    public static List<string> Validate(InteriorLayout l)
    {
        var errors = new List<string>();
        float hw = l.Width / 2 + 0.02f, hd = l.Depth / 2 + 0.02f;
        if (l.Floors.Count == 0) { errors.Add("no floors"); return errors; }
        if (l.Entrances.Count == 0)
        {
            if (!l.GroundFloor.Rooms.Any(r => r.Openings.Any(o => o.Kind == OpeningKind.Entry && o.Side == Side.Front)))
                errors.Add("no entry door on the front wall");
        }
        else
        {
            var named = new HashSet<string>();
            foreach (var e in l.Entrances)
            {
                // on its own floor: the ground floor, or a raised door's (#827)
                if (e.Level != 0 && (l.EntranceFloor(e) < 0 || l.EntranceFloor(e) >= l.Floors.Count))
                    errors.Add($"entrance for {e.Door} is on floor {l.EntranceFloor(e)}, which the plan has not got");
                else if (!l.EntranceRooms(e).Any(r => r.Openings.Any(o => o.Kind == OpeningKind.Entry && OnWall(r, o, e.X, e.Z))))
                    errors.Add($"entrance for {e.Door} has no doorway at {e.X:F1},{e.Z:F1}{(e.Level != 0 ? $" on floor {l.EntranceFloor(e)}" : "")}");
                // two entrances for one door would give it two portals and two leaves (#498)
                if (!named.Add(e.Door)) errors.Add($"two entrances for door {e.Door}");
            }
        }

        for (int f = 0; f < l.Floors.Count; f++)
        {
            var rooms = l.Floors[f].Rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r.X0 < -hw || r.X1 > hw || r.Z0 < -hd || r.Z1 > hd)
                    errors.Add($"floor {f} room {i} {r.Type} outside the footprint");
                if (Math.Min(r.Width, r.Depth) < 1.0f && !r.Piece)
                    errors.Add($"floor {f} room {i} {r.Type} is {r.Width:F1}x{r.Depth:F1} m");
                if (r.Shape != null) ShapeErrors(l, f, i, hw, hd, errors);
                // An opening cannot be taller than the room it is cut in. Worth stating because a
                // room's headroom is no longer always the storey's: a site's service block is a
                // 2.6 m room inside a 9 m hall (RoomPlan.Clear, #497), so anything that sizes a
                // doorway from StoreyHeight rather than from the room would cut through its ceiling.
                float roomClear = l.ClearOf(r);
                foreach (var o in r.Openings)
                    if (o.Top > roomClear + 0.01f)
                        errors.Add($"floor {f} room {i} {r.Type}: a {o.Kind} {o.Top:F2} m tall in a {roomClear:F2} m room");
                for (int j = i + 1; j < rooms.Count; j++)
                    if (new RectPlan(r.X0, r.Z0, r.X1, r.Z1).Overlaps(new RectPlan(rooms[j].X0, rooms[j].Z0, rooms[j].X1, rooms[j].Z1), 0.02f))
                        errors.Add($"floor {f} rooms {i} and {j} overlap");
                // a room several storeys tall owns its rectangle on the floors it rises through
                for (int g = f + 1; g < Math.Min(l.Floors.Count, f + r.Span); g++)
                    foreach (var q in l.Floors[g].Rooms)
                        if (new RectPlan(r.X0, r.Z0, r.X1, r.Z1).Overlaps(new RectPlan(q.X0, q.Z0, q.X1, q.Z1), 0.02f))
                            errors.Add($"floor {g} room {q.Type} inside the {r.Span}-storey {r.Type} of floor {f}");
                foreach (var o in r.Openings.Where(o => o.Kind == OpeningKind.Door))
                {
                    if (o.Other < 0 || o.Other >= rooms.Count
                        || !rooms[o.Other].Openings.Any(q => q.Other == i && Matches(r, o, rooms[o.Other], q)))
                        errors.Add($"floor {f} room {i}: doorway without a matching cut on the other side");
                }
            }

            if (f < l.Floors.Count - 1)
            {
                // a floor with a way in of its own (a raised door, #827) needs no stair up to it:
                // reachability below still asks that every room of it is reached
                bool ownWay = l.Entrances.Any(e => e.Level != 0 && l.EntranceFloor(e) == f + 1);
                if (!ownWay && !Stairs(l.Floors[f]).Any()) errors.Add($"no stairs from floor {f}");
                foreach (var fl in l.Floors[f].AllFlights())
                {
                    var area = fl.Area();
                    if (fl.Ramp)
                    {
                        // a garage's ramp (#558) starts in one room and may run on into the next (the
                        // car park): both its ends stand in a room, and the floor above is open over it as
                        // far as a car passing under the slab needs
                        foreach (var (px, pz, what) in new[] { (fl.TopEnd.X, fl.TopEnd.Z, "top"), (fl.Bottom.X, fl.Bottom.Z, "foot") })
                            if (RoomAt(l.Floors[f], px, pz) < 0) errors.Add($"floor {f} ramp {what} outside any room");
                        float runDir = fl.RunDir, holeEnd = fl.ZTop + runDir * RampProfile.HoleLength(l.StoreyHeight * (fl.To - fl.From), l.StoreyHeight - InteriorGenerator.Slab);
                        float h0 = Math.Min(fl.ZTop, holeEnd), h1 = Math.Max(fl.ZTop, holeEnd);
                        var need = fl.AlongX ? new RectPlan(h0, fl.X0, h1, fl.X1) : new RectPlan(fl.X0, h0, fl.X1, h1);
                        if (!l.Floors[f + 1].Holes.Any(h => h.X0 <= need.X0 + 0.01f && h.X1 >= need.X1 - 0.01f
                                && h.Z0 <= need.Z0 + 0.01f && h.Z1 >= need.Z1 - 0.01f))
                            errors.Add($"floor {f + 1} has no opening over the ramp from below");
                        if (fl.X1 - fl.X0 < 3.0f) errors.Add($"floor {f} ramp is {fl.X1 - fl.X0:F1} m wide, a car needs 3");
                        continue;
                    }
                    if (!rooms.Any(core => area.X0 >= core.X0 - 0.01f && area.X1 <= core.X1 + 0.01f
                        && area.Z0 >= core.Z0 - 0.01f && area.Z1 <= core.Z1 + 0.01f))
                        errors.Add($"floor {f} stairs outside any room");
                    if (!l.Floors[f + 1].Holes.Any(h => h.X0 <= area.X0 + 0.01f && h.X1 >= area.X1 - 0.01f
                            && h.Z0 <= area.Z0 + 0.01f && h.Z1 >= area.Z1 - 0.01f))
                        errors.Add($"floor {f + 1} has no opening over the stairs from below");
                }
            }
        }

        errors.AddRange(Unreachable(l));

        // elevators: a cabin room on every floor they serve, and a doorway out of it
        foreach (var lift in l.Lifts)
            for (int g = lift.Bottom; g <= lift.Top; g++)
            {
                if (g < 0 || g >= l.Floors.Count) { errors.Add($"elevator serves missing floor {g}"); continue; }
                int r = RoomAt(l.Floors[g], (lift.X0 + lift.X1) / 2, (lift.Z0 + lift.Z1) / 2);
                if (r < 0 || l.Floors[g].Rooms[r].Type != RoomType.Elevator) errors.Add($"floor {g} has no elevator cabin at {lift.X0:F1},{lift.Z0:F1}");
                else if (!l.Floors[g].Rooms[r].Openings.Any(o => o.Kind == OpeningKind.Door && o.Side == lift.DoorSide && Math.Abs(o.Center - lift.DoorCenter) < 0.01f))
                    errors.Add($"floor {g} elevator cabin has no doorway on its {lift.DoorSide} side");
            }

        // doors with a leaf hang in a doorway that is there
        foreach (var d in l.InnerDoors)
        {
            if (d.Floor < 0 || d.Floor >= l.Floors.Count || d.Room < 0 || d.Room >= l.Floors[d.Floor].Rooms.Count)
            { errors.Add("inner door in a missing room"); continue; }
            // a doorway, or a cinema's service doorway: an open hole between floors with its sill up a few steps (#1279)
            if (!l.Floors[d.Floor].Rooms[d.Room].Openings.Any(o => (o.Kind == OpeningKind.Door || o.Kind == OpeningKind.Window && o.Open
                    && Math.Abs(o.Bottom - d.Bottom) < 0.01f) && o.Side == d.Side && Math.Abs(o.Center - d.Center) < 0.01f))
                errors.Add($"floor {d.Floor} inner door at {d.Center:F1} has no doorway");
        }

        // a vehicle door of a block of flats (#558) leads to a ramp down to its car park, wide enough
        // for a car, with the lane and the turn at its foot clear of furniture
        foreach (var e in l.Entrances.Where(e => e.Vehicle))
        {
            var ramps = l.Floors.SelectMany((fl, i) => fl.AllFlights().Where(x => x.Ramp).Select(x => (Floor: i, Flight: x))).ToList();
            if (l.Type is BuildingType.Apartments or BuildingType.MixedUse && !ramps.Any(r => r.Floor == l.Below - 1
                    && (r.Flight.AlongX || Math.Abs(r.Flight.TopEnd.X - e.X) < r.Flight.X1 - r.Flight.X0) && l.GroundFloor.Rooms.Any(q => q.Type == RoomType.Ramp && Inside(q, r.Flight.TopEnd.X, r.Flight.TopEnd.Z)
                        && q.Openings.Any(o => o.Kind == OpeningKind.Entry && OnWall(q, o, e.X, e.Z)))))
                errors.Add($"vehicle entrance for {e.Door} has no ramp behind it");
        }
        foreach (var (fi, ramp) in l.Floors.SelectMany((fl, i) => fl.AllFlights().Where(x => x.Ramp).Select(x => (i, x))))
        {
            var lane = ramp.Area();
            var mouth = ramp.FootZone(0, GarageRule.RampTurn, 0);
            foreach (var p in l.Furniture.Where(p => p.Floor == fi
                && p.Type is not (FurnitureType.FloorMarking or FurnitureType.Rug or FurnitureType.Confetti)))
            {
                bool odd = p.Turns % 2 == 1;
                float w = (odd ? p.D : p.W) / 2, d = (odd ? p.W : p.D) / 2;
                var rect = new RectPlan(p.X - w, p.Z - d, p.X + w, p.Z + d);
                if (rect.Overlaps(lane, 0.02f) || rect.Overlaps(mouth, 0.02f)) errors.Add($"{p.Type} on floor {fi} stands in a garage ramp's lane");
            }
        }

        // furniture
        var byFloor = l.Furniture.GroupBy(p => p.Floor);
        foreach (var group in byFloor)
        {
            if (group.Key < 0 || group.Key >= l.Floors.Count) { errors.Add("furniture on a missing floor"); continue; }
            var floor = l.Floors[group.Key];
            var rects = new List<(FurniturePlan P, RectPlan R)>();
            foreach (var p in group)
            {
                bool odd = p.Turns % 2 == 1;
                float w = (odd ? p.D : p.W) / 2, d = (odd ? p.W : p.D) / 2;
                var rect = new RectPlan(p.X - w, p.Z - d, p.X + w, p.Z + d);
                var room = floor.Rooms.FirstOrDefault(r => p.X > r.X0 && p.X < r.X1 && p.Z > r.Z0 && p.Z < r.Z1);
                if (room == null) { errors.Add($"{p.Type} on floor {p.Floor} is in no room"); continue; }
                if (rect.X0 < room.X0 || rect.X1 > room.X1 || rect.Z0 < room.Z0 || rect.Z1 > room.Z1)
                    errors.Add($"{p.Type} on floor {p.Floor} pokes through a wall");
                // stood on (a rug, the chancel step, a showroom plinth, paint on the floor) or
                // overhead (a bell, a cross on the wall, a crane beam)
                if (p.Type is not (FurnitureType.Rug or FurnitureType.Dais
                        or FurnitureType.ShowroomPlinth or FurnitureType.FloorMarking or FurnitureType.Confetti
                        or FurnitureType.CarLift or FurnitureType.BoothDeck or FurnitureType.ServiceSteps)
                    && p.Lift < 1.5f)
                {
                    foreach (var o in room.Openings.Where(o => o.Kind is OpeningKind.Door or OpeningKind.Entry or OpeningKind.Arch))
                        if (Doorway(room, o).Overlaps(rect)) errors.Add($"{p.Type} on floor {p.Floor} blocks a doorway");
                    foreach (var (q, r) in rects)
                        if (r.Overlaps(rect, 0.02f))
                            errors.Add($"{p.Type} and {q.Type} on floor {p.Floor} overlap");
                    rects.Add((p, rect));
                }
            }
        }
        return errors;
    }

    /// <summary>
    /// A shaped room (#1290): inside the footprint, counter-clockwise, and no other room's floor
    /// (on its floor, or rising into it) under any of it, sampled every 0.4 m away from its walls.
    /// </summary>
    private static void ShapeErrors(InteriorLayout l, int f, int i, float hw, float hd, List<string> errors)
    {
        var r = l.Floors[f].Rooms[i];
        var c = r.Corners();
        if (c.Any(p => p.X < -hw || p.X > hw || p.Y < -hd || p.Y > hd)) errors.Add($"floor {f} room {i} {r.Type}: its shape leaves the footprint");
        if (RoomShapes.Area(c) <= 0) { errors.Add($"floor {f} room {i} {r.Type}: its shape is wound backwards"); return; }
        float x0 = c.Min(p => p.X), x1 = c.Max(p => p.X), z0 = c.Min(p => p.Y), z1 = c.Max(p => p.Y);
        // only the rooms whose box meets the shape's (a big block has hundreds a floor)
        var near = new List<(int G, int J, RoomPlan O, Godot.Vector2[]? C)>();
        for (int g = 0; g <= f; g++)
            for (int j = 0; j < l.Floors[g].Rooms.Count; j++)
            {
                var o = l.Floors[g].Rooms[j];
                if (g == f && j == i || g + o.Span <= f) continue;
                var oc = o.Shape == null ? null : o.Corners();
                float ox0 = oc?.Min(p => p.X) ?? o.X0, ox1 = oc?.Max(p => p.X) ?? o.X1, oz0 = oc?.Min(p => p.Y) ?? o.Z0, oz1 = oc?.Max(p => p.Y) ?? o.Z1;
                if (ox1 > x0 && ox0 < x1 && oz1 > z0 && oz0 < z1) near.Add((g, j, o, oc));
            }
        if (near.Count == 0) return;
        for (float x = x0 + 0.2f; x < x1; x += 0.4f)
            for (float z = z0 + 0.2f; z < z1; z += 0.4f)
            {
                var q = new Godot.Vector2(x, z);
                if (!RoomShapes.Contains(c, q) || RoomShapes.EdgeDistance(c, q) < 0.1f) continue;
                foreach (var (g, j, o, oc) in near)
                {
                    bool under = oc == null
                        ? x > o.X0 + 0.1f && x < o.X1 - 0.1f && z > o.Z0 + 0.1f && z < o.Z1 - 0.1f
                        : RoomShapes.Contains(oc, q) && RoomShapes.EdgeDistance(oc, q) >= 0.1f;
                    if (under) { errors.Add($"floor {f} room {i} {r.Type}: its shape overlaps room {j} {o.Type} of floor {g}"); return; }
                }
            }
    }

    /// <summary>
    /// The stairs up from a floor, each as its first and last flight: a whole-storey flight on its
    /// own, or a flight to a half landing and the one on from it (#571) — the one starting at the
    /// height the other ends, on a landing both reach.
    /// </summary>
    private static IEnumerable<(FlightPlan First, FlightPlan Last)> Stairs(FloorPlan floor)
    {
        var flights = floor.AllFlights().ToList();
        foreach (var first in flights.Where(x => x.From <= 0.001f))
        {
            var at = first;
            for (int guard = 0; at.To < 0.999f && guard < 8; guard++)
            {
                var top = at.TopEnd;
                var landing = floor.Landings.FirstOrDefault(g => Math.Abs(g.Level - at.To) < 0.01f && Near(g, top.X, top.Z));
                var next = landing == null ? null : flights.FirstOrDefault(x => Math.Abs(x.From - at.To) < 0.01f
                    && Near(landing, x.Bottom.X, x.Bottom.Z));
                if (next == null) break;
                at = next;
            }
            if (at.To >= 0.999f) yield return (first, at);
        }
    }

    /// <summary>Whether a point is on a landing, give or take a few centimetres.</summary>
    private static bool Near(LandingPlan g, float x, float z) =>
        x >= g.X0 - 0.05f && x <= g.X1 + 0.05f && z >= g.Z0 - 0.05f && z <= g.Z1 + 0.05f;

    private static bool Inside(RoomPlan r, float x, float z) =>
        x >= r.X0 - 0.01f && x <= r.X1 + 0.01f && z >= r.Z0 - 0.01f && z <= r.Z1 + 0.01f;

    /// <summary>The room of a floor holding a point, or -1.</summary>
    private static int RoomAt(FloorPlan floor, float x, float z)
    {
        for (int i = 0; i < floor.Rooms.Count; i++)
            if (Inside(floor.Rooms[i], x, z)) return i;
        return -1;
    }

    /// <summary>
    /// The rooms no one can reach from the ways in, through doorways, up and down the stairs and in the
    /// elevators, as errors (what <see cref="Validate"/> reports; alone, it is cheap enough to ask often).
    /// </summary>
    internal static List<string> Unreachable(InteriorLayout l)
    {
        var errors = new List<string>();
        // a block of flats' upper floors are several stairwells that never meet (#557)
        var reached = new HashSet<(int F, int R)>();
        var queue = new Queue<(int F, int R)>();
        void Reach(int f, int r)
        {
            if (f >= 0 && f < l.Floors.Count && r >= 0 && r < l.Floors[f].Rooms.Count && reached.Add((f, r))) queue.Enqueue((f, r));
        }
        var groundRooms = l.GroundFloor.Rooms;
        for (int i = 0; i < groundRooms.Count; i++)
            if (groundRooms[i].Openings.Any(o => o.Kind == OpeningKind.Entry)) Reach(l.Below, i);
        // a raised door (#827) is a way in to the room its doorway is cut in, on its floor
        foreach (var e in l.Entrances)
        {
            int ef = l.EntranceFloor(e);
            if (e.Level == 0 || ef < 0 || ef >= l.Floors.Count) continue;
            var rooms = l.Floors[ef].Rooms;
            for (int i = 0; i < rooms.Count; i++)
                if (rooms[i].Openings.Any(o => o.Kind == OpeningKind.Entry && OnWall(rooms[i], o, e.X, e.Z))) Reach(ef, i);
        }
        if (reached.Count == 0) Reach(l.Below, 0);
        while (queue.Count > 0)
        {
            var (f, a) = queue.Dequeue();
            var floor = l.Floors[f];
            var room = floor.Rooms[a];
            foreach (var o in room.Openings)
                if (o.Kind is OpeningKind.Door or OpeningKind.Arch && o.Other >= 0) Reach(f, o.Other);
            // a stair from this room's floor, starting in it, lands in the room over its top: one
            // flight, or two round a half landing (#571)
            foreach (var (first, last) in Stairs(floor))
                if (Inside(room, first.Bottom.X, first.Bottom.Z) && f + 1 < l.Floors.Count)
                    Reach(f + 1, RoomAt(l.Floors[f + 1], last.TopEnd.X, last.TopEnd.Z));
            // and back down one arriving here
            if (f > 0)
                foreach (var (first, last) in Stairs(l.Floors[f - 1]))
                    if (Inside(room, last.TopEnd.X, last.TopEnd.Z)) Reach(f - 1, RoomAt(l.Floors[f - 1], first.Bottom.X, first.Bottom.Z));
            if (room.Type == RoomType.Elevator)
                foreach (var lift in l.Lifts)
                    if (lift.Contains((room.X0 + room.X1) / 2, (room.Z0 + room.Z1) / 2))
                        for (int g = lift.Bottom; g <= lift.Top; g++)
                            Reach(g, RoomAt(l.Floors[g], (lift.X0 + lift.X1) / 2, (lift.Z0 + lift.Z1) / 2));
        }
        for (int f = 0; f < l.Floors.Count; f++)
            for (int i = 0; i < l.Floors[f].Rooms.Count; i++)
                if (!reached.Contains((f, i))) errors.Add($"floor {f} room {i} {l.Floors[f].Rooms[i].Type} unreachable");
        return errors;
    }

    /// <summary>Whether an opening's middle is at (x, z) on its room's wall line.</summary>
    private static bool OnWall(RoomPlan r, OpeningPlan o, float x, float z)
    {
        // on the rectangle's wall line, or on a shaped room's edge (#1197)
        var p = r.WallFrame(o).At;
        return Math.Abs(p.X - x) < 0.05f && Math.Abs(p.Y - z) < 0.05f;
    }

    /// <summary>
    /// Whether two rooms' cuts are the one doorway: the same coordinate along a shared wall line, or,
    /// where either is on a shaped room's edge (#1197), the same point.
    /// </summary>
    private static bool Matches(RoomPlan r, OpeningPlan o, RoomPlan other, OpeningPlan q) =>
        o.Edge == null && q.Edge == null
            ? Math.Abs(q.Center - o.Center) < 0.01f
            : r.WallFrame(o).At.DistanceTo(other.WallFrame(q).At) < 0.05f;

    /// <summary>The strip just inside a doorway, which must stay clear.</summary>
    private static RectPlan Doorway(RoomPlan r, OpeningPlan o)
    {
        float half = o.Width / 2, deep = 0.6f;
        if (o.Edge != null)
        {
            var (at, outward, along) = r.WallFrame(o);
            var p = new[] { at - along * half, at + along * half, at - along * half - outward * deep, at + along * half - outward * deep };
            return new RectPlan(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
        }
        return o.Side switch
        {
            Side.Front => new RectPlan(o.Center - half, r.Z0, o.Center + half, r.Z0 + deep),
            Side.Back => new RectPlan(o.Center - half, r.Z1 - deep, o.Center + half, r.Z1),
            Side.Left => new RectPlan(r.X0, o.Center - half, r.X0 + deep, o.Center + half),
            _ => new RectPlan(r.X1 - deep, o.Center - half, r.X1, o.Center + half),
        };
    }

    /// <summary>Plan view of every floor side by side, the front wall at the bottom.</summary>
    public static string ToSvg(InteriorLayout l, IReadOnlyList<Godot.Vector2[]>? outline = null)
    {
        var inv = CultureInfo.InvariantCulture;
        const float S = 24f, Gap = 30f;
        float fw = l.Width * S, fd = l.Depth * S;
        var sb = new StringBuilder();
        string N(float v) => v.ToString("0.#", inv);
        float width = l.Floors.Count * (fw + Gap) + Gap, height = fd + 2 * Gap + 20;
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" font-family=\"sans-serif\" font-size=\"10\">");
        sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"#f4f1ea\"/>");
        sb.Append($"<text x=\"{Gap}\" y=\"16\" font-size=\"13\">{l.Key} {l.Kind} {l.Width:F1}x{l.Depth:F1} m, {l.Floors.Count} floor(s) of {l.StoreyHeight:F2} m</text>");
        for (int f = 0; f < l.Floors.Count; f++)
        {
            float ox = Gap + f * (fw + Gap) + fw / 2, oy = Gap + 20 + fd / 2;
            // Z grows toward the back; draw the back at the top
            float X(float x) => ox + x * S;
            float Y(float z) => oy - z * S;
            var floor = l.Floors[f];
            foreach (var r in floor.Rooms)
            {
                string fill = r.Type switch
                {
                    RoomType.Hall or RoomType.Landing or RoomType.Lobby => "#e8dcc4",
                    RoomType.Living or RoomType.Dining => "#f2c98a",
                    RoomType.Kitchen => "#c9e2b8",
                    RoomType.Bedroom => "#b8cde2",
                    RoomType.Bathroom or RoomType.WC => "#a8e0e0",
                    RoomType.WomensToilet => "#f0b0c8",
                    RoomType.MensToilet => "#a8c4f0",
                    RoomType.ToiletStall or RoomType.AccessibleToilet => "#a8e0e0",
                    RoomType.ToiletLobby => "#d0ecec",
                    RoomType.Shelter or RoomType.Vault => "#9aa29a",
                    RoomType.BankHall => "#efe2a8",
                    RoomType.Carnotzet or RoomType.Cellar or RoomType.Pantry => "#d8b89a",
                    RoomType.HomeCinema or RoomType.MusicRoom => "#b9a8d8",
                    RoomType.GuestRoom or RoomType.Playroom or RoomType.Study => "#c8d8f0",
                    RoomType.Corridor => "#e8dcc4",
                    RoomType.Elevator => "#7d8fa0",
                    RoomType.CarPark => "#b4b4b0",
                    RoomType.Shop => "#f0b8c8",
                    RoomType.Storage or RoomType.TechRoom => "#cfc6b8",
                    _ => "#ddd",
                };
                if (r.Shape != null)
                {
                    // a shaped room (#1290): its polygon, its core dashed inside it
                    var pts = string.Join(" ", r.Corners().Select(p => $"{N(X(p.X))},{N(Y(p.Y))}"));
                    sb.Append($"<polygon points=\"{pts}\" fill=\"{fill}\" stroke=\"#333\" stroke-width=\"2\"/>");
                    sb.Append($"<rect x=\"{N(X(r.X0))}\" y=\"{N(Y(r.Z1))}\" width=\"{N(r.Width * S)}\" height=\"{N(r.Depth * S)}\" fill=\"none\" stroke=\"#333\" stroke-width=\"0.6\" stroke-dasharray=\"3 3\"/>");
                }
                else
                    sb.Append($"<rect x=\"{N(X(r.X0))}\" y=\"{N(Y(r.Z1))}\" width=\"{N(r.Width * S)}\" height=\"{N(r.Depth * S)}\" fill=\"{fill}\" stroke=\"#333\" stroke-width=\"2\"/>");
                sb.Append($"<text x=\"{N(X((r.X0 + r.X1) / 2))}\" y=\"{N(Y((r.Z0 + r.Z1) / 2))}\" text-anchor=\"middle\">{r.Type}</text>");
                foreach (var o in r.Openings)
                {
                    var (at, _, dir) = r.WallFrame(o);
                    var a = at - dir * (o.Width / 2);
                    var b = at + dir * (o.Width / 2);
                    string col = o.Kind switch { OpeningKind.Window => "#4aa3ff", OpeningKind.Entry => "#d11", _ => "#fff" };
                    sb.Append($"<line x1=\"{N(X(a.X))}\" y1=\"{N(Y(a.Y))}\" x2=\"{N(X(b.X))}\" y2=\"{N(Y(b.Y))}\" stroke=\"{col}\" stroke-width=\"4\"/>");
                }
            }
            // the building's real outline, when the check has it
            foreach (var ring in outline ?? [])
                sb.Append($"<polygon points=\"{string.Join(" ", ring.Select(p => $"{N(X(p.X))},{N(Y(p.Y))}"))}\" fill=\"none\" stroke=\"#c40\" stroke-width=\"1.5\" stroke-dasharray=\"6 3\"/>");
            // with it, the real street doors on the facade (#1290): a doorway inside should stand at its ring
            if (outline != null)
                foreach (var e in l.AllEntrances().Where(e => l.EntranceFloor(e) == f))
                {
                    var (door, _) = InteriorGenerator.DoorInPlan(l, e);
                    sb.Append($"<circle cx=\"{N(X(door.X))}\" cy=\"{N(Y(door.Y))}\" r=\"5\" fill=\"none\" stroke=\"#d11\" stroke-width=\"2\"/>");
                }
            foreach (var h in floor.Holes)
                sb.Append($"<rect x=\"{N(X(h.X0))}\" y=\"{N(Y(h.Z1))}\" width=\"{N((h.X1 - h.X0) * S)}\" height=\"{N((h.Z1 - h.Z0) * S)}\" fill=\"#333\" fill-opacity=\"0.5\"/>");
            foreach (var g in floor.Landings)
                sb.Append($"<rect x=\"{N(X(g.X0))}\" y=\"{N(Y(g.Z1))}\" width=\"{N((g.X1 - g.X0) * S)}\" height=\"{N((g.Z1 - g.Z0) * S)}\" fill=\"#b8b2a6\"/>");
            foreach (var fl in floor.AllFlights())
            {
                var ar = fl.Area();
                sb.Append($"<rect x=\"{N(X(ar.X0))}\" y=\"{N(Y(ar.Z1))}\" width=\"{N((ar.X1 - ar.X0) * S)}\" height=\"{N((ar.Z1 - ar.Z0) * S)}\" fill=\"#a0784c\"/>");
                sb.Append($"<line x1=\"{N(X(fl.Bottom.X))}\" y1=\"{N(Y(fl.Bottom.Z))}\" x2=\"{N(X(fl.TopEnd.X))}\" y2=\"{N(Y(fl.TopEnd.Z))}\" stroke=\"#fff\" stroke-width=\"2\" marker-end=\"none\"/>");
            }
            // a flat's front door: a short bar across the doorway, red if it is locked
            foreach (var d in l.InnerDoors.Where(d => d.Floor == f))
            {
                var r = floor.Rooms[d.Room];
                float fixedC = d.Side switch { Side.Front => r.Z0, Side.Back => r.Z1, Side.Left => r.X0, _ => r.X1 };
                string col = d.Locked ? "#c00" : "#2a2";
                if (d.Side is Side.Front or Side.Back)
                    sb.Append($"<line x1=\"{N(X(d.Center - d.Width / 2))}\" y1=\"{N(Y(fixedC))}\" x2=\"{N(X(d.Center + d.Width / 2))}\" y2=\"{N(Y(fixedC))}\" stroke=\"{col}\" stroke-width=\"6\"/>");
                else
                    sb.Append($"<line x1=\"{N(X(fixedC))}\" y1=\"{N(Y(d.Center - d.Width / 2))}\" x2=\"{N(X(fixedC))}\" y2=\"{N(Y(d.Center + d.Width / 2))}\" stroke=\"{col}\" stroke-width=\"6\"/>");
            }
            foreach (var r in floor.Rooms.Where(r => r.Unit >= 0).GroupBy(r => r.Unit))
            {
                var first = r.First();
                sb.Append($"<text x=\"{N(X(first.X0) + 3)}\" y=\"{N(Y(first.Z1) + 10)}\" font-size=\"8\" fill=\"#844\">flat {r.Key}</text>");
            }
            foreach (var p in l.Furniture.Where(p => p.Floor == f))
            {
                bool odd = p.Turns % 2 == 1;
                float w = odd ? p.D : p.W, d = odd ? p.W : p.D;
                sb.Append($"<rect x=\"{N(X(p.X - w / 2))}\" y=\"{N(Y(p.Z + d / 2))}\" width=\"{N(w * S)}\" height=\"{N(d * S)}\" fill=\"#8a6\" fill-opacity=\"0.6\" stroke=\"#453\"/>");
                sb.Append($"<text x=\"{N(X(p.X))}\" y=\"{N(Y(p.Z) + 3)}\" text-anchor=\"middle\" font-size=\"7\" fill=\"#222\">{p.Type}</text>");
            }
            string level = f < l.Below ? "cellar" : f == l.Below ? "ground floor" : $"floor {f - l.Below}";
            sb.Append($"<text x=\"{N(ox - fw / 2)}\" y=\"{N(oy + fd / 2 + 14)}\">{level} (street at the bottom)</text>");
        }
        sb.Append("</svg>");
        return sb.ToString();
    }
}
