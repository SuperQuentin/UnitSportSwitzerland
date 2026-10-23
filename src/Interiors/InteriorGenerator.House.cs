using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// A house drawn by its owner on the web (#1186, <c>docs/notes/net/house-layouts.md</c>): an approved
/// <see cref="HouseLayout"/> planned into the interior frame instead of the generated rooms.
///
/// <para>
/// The layout is drawn in its own frame (the long side of the footprint's minimum rectangle); the interior's
/// is the footprint box with the door at its front. The two are the same building's boxes, so they differ by
/// a quarter turn or two and a small offset: the layout is turned by the nearest quarter turn, never by an odd
/// angle (the interior's rooms are square to its frame), and a layout more than <see cref="MaxSkewDeg"/> off
/// is refused. An interior lives under the world behind its door's portal, so its walls need not meet the
/// shell's; it only has to be one walkable plan with its entry on the front wall where the door leads in.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    private const float MaxSkewDeg = 20f;
    /// <summary>An outer wall this close to the box's side is moved onto it, so the door and the windows find it.</summary>
    private const float SnapToBox = 0.6f;

    /// <summary>
    /// Replaces <paramref name="l"/>'s floors with the house's: rooms, doorways, windows, the entry and the stairs.
    /// False, with the reason, when it does not fit the box; the caller then plans the building as usual.
    /// </summary>
    private static bool TryHouse(InteriorLayout l, Footprint fp, HouseLayout h, TileId tile, out string why)
    {
        why = "";
        // the layout's axes in tile-local metres (X east, Z south): x is yaw from east, z a quarter clockwise of it
        double a = h.YawDeg * Math.PI / 180;
        var (ux, uz) = ((Math.Cos(a), -Math.Sin(a)), (Math.Sin(a), Math.Cos(a)));
        double Ux = fp.AxisU.X, Uz = fp.AxisU.Y, n = Math.Sqrt(Ux * Ux + Uz * Uz);
        Ux /= n; Uz /= n;
        (double X, double Z) V = (-Uz, Ux);
        // where the layout's x axis points in the interior frame, to the nearest quarter turn
        double ang = Math.Atan2(ux.Item1 * V.X + ux.Item2 * V.Z, ux.Item1 * Ux + ux.Item2 * Uz);
        int q = (int)Math.Round(ang / (Math.PI / 2));
        double skew = Math.Abs(ang - q * Math.PI / 2) * 180 / Math.PI;
        if (skew > MaxSkewDeg) { why = $"turned {skew:F0} degrees from the building's box"; return false; }
        var (ex, ez) = (q & 3) switch
        {
            0 => ((1, 0), (0, 1)),
            1 => ((0, 1), (-1, 0)),
            2 => ((-1, 0), (0, -1)),
            _ => ((0, -1), (1, 0)),
        };
        double cx = h.E - tile.MinE - fp.Center.X, cz = tile.MaxN - h.N - fp.Center.Y;
        double tx = cx * Ux + cz * Uz, tz = cx * V.X + cz * V.Z;
        (float X, float Z) P(double x, double z) => ((float)(ex.Item1 * x + ez.Item1 * z + tx), (float)(ex.Item2 * x + ez.Item2 * z + tz));
        (float X0, float Z0, float X1, float Z1) R(double x0, double z0, double x1, double z1)
        {
            var (ax, az) = P(x0, z0);
            var (bx, bz) = P(x1, z1);
            return (Math.Min(ax, bx), Math.Min(az, bz), Math.Max(ax, bx), Math.Max(az, bz));
        }

        float hw = l.Width / 2, hd = l.Depth / 2;
        float storey = (float)h.StoreyHeight, clear = storey - Slab;
        var levels = h.Floors.Select(f => f.Level).OrderBy(v => v).ToList();
        for (int i = 1; i < levels.Count; i++)
            if (levels[i] != levels[i - 1] + 1) { why = $"no floor {levels[i - 1] + 1} between {levels[i - 1]} and {levels[i]}"; return false; }

        var floors = new List<FloorPlan>();
        var userDoors = new List<(int Floor, float Center, float Width)>(); // front doors drawn on the front wall
        foreach (var hf in h.Floors.OrderBy(f => f.Level))
        {
            var fl = new FloorPlan();
            foreach (var hr in hf.Rooms)
            {
                var (x0, z0, x1, z1) = R(hr.X0, hr.Z0, hr.X1, hr.Z1);
                if (!Enum.TryParse<RoomType>(hr.Type, out var type)) { why = $"unknown room type {hr.Type}"; return false; }
                fl.Rooms.Add(new RoomPlan { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Type = type });
            }
            // openings are found on the walls as drawn: snapping and trimming below move outer walls only across
            var drawnRooms = fl.Rooms.Select(r => new RectPlan(r.X0, r.Z0, r.X1, r.Z1)).ToList();
            SnapOuterWalls(fl.Rooms, hw, hd);
            // the box can be a little smaller than the drawing (two outlines, two rectangles): trim to it
            foreach (var r in fl.Rooms)
            {
                r.X0 = Math.Max(r.X0, -hw); r.X1 = Math.Min(r.X1, hw);
                r.Z0 = Math.Max(r.Z0, -hd); r.Z1 = Math.Min(r.Z1, hd);
                if (r.Width < 1.0f || r.Depth < 1.0f) { why = $"a {r.Type} on floor {hf.Level} falls outside the building's box"; return false; }
            }

            int floorIndex = floors.Count;
            foreach (var o in hf.Openings)
            {
                var (ax, az) = P(o.X0, o.Z0);
                var (bx, bz) = P(o.X1, o.Z1);
                bool alongX = Math.Abs(az - bz) < 0.02f;
                float c = alongX ? (ax + bx) / 2 : (az + bz) / 2, w = alongX ? Math.Abs(bx - ax) : Math.Abs(bz - az);
                float line = alongX ? (az + bz) / 2 : (ax + bx) / 2;
                var on = new List<(int Room, Side Side)>();
                for (int i = 0; i < fl.Rooms.Count; i++)
                    if (WallOf(drawnRooms[i], alongX, line, c, w) is { } side && Along(fl.Rooms[i], alongX, c, w)) on.Add((i, side));
                if (on.Count == 0) continue; // a room it sat on was trimmed away
                if (o.Kind == "window")
                {
                    if (on.Count > 1) continue; // a window between two rooms: not one the game draws
                    fl.Rooms[on[0].Room].Openings.Add(new OpeningPlan
                    {
                        Side = on[0].Side, Center = c, Width = w, Bottom = 0.9f, Top = Math.Min(2.1f, clear - 0.2f), Kind = OpeningKind.Window,
                    });
                    continue;
                }
                if (on.Count == 1)
                {
                    // a door to the outside: the game's own door is the way in, so only remember where the drawing put it
                    if (o.Kind == "door" && on[0].Side == Side.Front && Math.Abs(fl.Rooms[on[0].Room].Z0 + hd) < 0.02f) userDoors.Add((floorIndex, c, w));
                    continue;
                }
                var kind = o.Kind == "arch" ? OpeningKind.Arch : OpeningKind.Door;
                float top = Math.Min(kind == OpeningKind.Arch ? 2.4f : 2.1f, clear - 0.15f);
                var (ra, sa) = on[0];
                var (rb, sb) = on[1];
                fl.Rooms[ra].Openings.Add(new OpeningPlan { Side = sa, Center = c, Width = w, Bottom = 0, Top = top, Kind = kind, Other = rb });
                fl.Rooms[rb].Openings.Add(new OpeningPlan { Side = sb, Center = c, Width = w, Bottom = 0, Top = top, Kind = kind, Other = ra });
            }
            floors.Add(fl);
        }

        // stairs: a flight on its floor, a hole over it in the floor above with a rail on its open sides
        int fi = 0;
        foreach (var hf in h.Floors.OrderBy(f => f.Level))
        {
            foreach (var s in hf.Stairs)
            {
                if (fi + 1 >= floors.Count) { why = "a stair with no floor above"; return false; }
                var (x0, z0, x1, z1) = R(s.X0, s.Z0, s.X1, s.Z1);
                // the climb's direction, turned with the rest
                var (dx, dz) = s.Up switch { "+x" => (1, 0), "-x" => (-1, 0), "-z" => (0, -1), _ => (0, 1) };
                var (gx, gz) = P(dx, dz);
                var (ox, oz) = P(0, 0);
                float runX = gx - ox, runZ = gz - oz;
                FlightPlan flight = Math.Abs(runZ) > 0.5f
                    ? new FlightPlan { X0 = x0, X1 = x1, ZBottom = runZ > 0 ? z0 : z1, ZTop = runZ > 0 ? z1 : z0 }
                    : new FlightPlan { AlongX = true, X0 = z0, X1 = z1, ZBottom = runX > 0 ? x0 : x1, ZTop = runX > 0 ? x1 : x0 };
                var below = floors[fi];
                if (below.Flight == null) below.Flight = flight; else below.Flights.Add(flight);
                var above = floors[fi + 1];
                above.Holes.Add(new RectPlan(x0, z0, x1, z1));
                var room = above.Rooms.FirstOrDefault(r => x0 >= r.X0 - 0.01f && x1 <= r.X1 + 0.01f && z0 >= r.Z0 - 0.01f && z1 <= r.Z1 + 0.01f);
                // rails on every side of the hole but the one the stair arrives at, unless a wall stands there
                foreach (var (rx0, rz0, rx1, rz1) in new[] { (x0, z0, x1, z0), (x1, z0, x1, z1), (x0, z1, x1, z1), (x0, z0, x0, z1) })
                {
                    bool arrives = flight.AlongX
                        ? rx0 == rx1 && Math.Abs(rx0 - flight.ZTop) < 0.01f
                        : rz0 == rz1 && Math.Abs(rz0 - flight.ZTop) < 0.01f;
                    bool wall = room != null && (rx0 == rx1 ? Math.Abs(rx0 - room.X0) < 0.05f || Math.Abs(rx0 - room.X1) < 0.05f
                                                            : Math.Abs(rz0 - room.Z0) < 0.05f || Math.Abs(rz0 - room.Z1) < 0.05f);
                    if (!arrives && !wall) above.Rails.Add(new RectPlan(rx0, rz0, rx1, rz1));
                }
            }
            fi++;
        }

        l.Floors.Clear();
        l.Floors.AddRange(floors);
        l.Below = -levels[0];
        l.StoreyHeight = storey;
        for (int f = 0; f < floors.Count; f++)
            if (!floors[f].Rooms.Any(r => r.Openings.Any(o => o.Kind == OpeningKind.Window))) AddWindows(l, floors[f], f);

        // the entry: on a ground floor room standing on the front wall, where the drawing put the front door
        // if it is there, else as near the real door as that room allows
        var ground = l.Floors[l.Below];
        float ew = l.EntryWidth;
        var fronts = ground.Rooms.Where(r => Math.Abs(r.Z0 + hd) < 0.02f && r.Width >= ew + 0.4f).ToList();
        if (fronts.Count == 0 && ReachFront(ground, hd, ew, fp.EntryX, clear) is { } reached) fronts.Add(reached);
        if (fronts.Count == 0) { why = "no ground floor room can be reached from the front wall, where the door leads in"; return false; }
        var drawn = userDoors.Where(d => d.Floor == l.Below)
            .Select(d => (d.Center, Room: fronts.FirstOrDefault(r => d.Center >= r.X0 && d.Center <= r.X1)))
            .FirstOrDefault(d => d.Room != null);
        float want = drawn.Room != null ? drawn.Center : fp.EntryX;
        var entryRoom = drawn.Room ?? fronts.OrderBy(r => want < r.X0 ? r.X0 - want : want > r.X1 ? want - r.X1 : 0).First();
        l.EntryX = Fit(want, entryRoom.X0 + ew / 2 + 0.2f, entryRoom.X1 - ew / 2 - 0.2f);
        entryRoom.Openings.RemoveAll(o => o.Side == Side.Front && Math.Abs(o.Center - l.EntryX) < (o.Width + ew) / 2 + 0.1f);
        entryRoom.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = ew, Bottom = 0, Top = Math.Min(l.DoorHeight, clear - 0.15f), Kind = OpeningKind.Entry,
        });
        l.House = h.Hash();
        return true;
    }

    /// <summary>
    /// When no ground floor room stands on the front wall (the drawing covers part of a big building, or an L-shaped
    /// house turns its empty corner to the street): the room nearest the front, grown onto the wall when it is close,
    /// else joined to it by an entrance passage straight from the wall. The interior lives behind the door's portal, so
    /// the entry may stand anywhere along the front wall. The room the entry then opens into, or null when nothing fits.
    /// </summary>
    private static RoomPlan? ReachFront(FloorPlan ground, float hd, float ew, float wantX, float clear)
    {
        const float GrowUpTo = 1.5f;
        float lane = Math.Max(ew + 0.4f, 1.4f);
        foreach (var r in ground.Rooms.Where(r => r.Width >= lane).OrderBy(r => r.Z0))
        {
            float x = Fit(wantX, r.X0 + lane / 2, r.X1 - lane / 2);
            bool Free(RectPlan s) => !ground.Rooms.Any(o => o != r && s.Overlaps(new RectPlan(o.X0, o.Z0, o.X1, o.Z1), 0.02f));
            if (r.Z0 + hd <= GrowUpTo && Free(new RectPlan(r.X0, -hd, r.X1, r.Z0))) { r.Z0 = -hd; return r; }
            var passage = new RectPlan(x - lane / 2, -hd, x + lane / 2, r.Z0);
            if (r.Z0 + hd < 1.0f || !Free(passage)) continue;
            var hall = new RoomPlan { X0 = passage.X0, Z0 = passage.Z0, X1 = passage.X1, Z1 = passage.Z1, Type = RoomType.Corridor };
            int hi = ground.Rooms.Count, ri = ground.Rooms.IndexOf(r);
            float top = Math.Min(2.1f, clear - 0.15f), w = Math.Min(InnerDoor, lane - 0.3f);
            hall.Openings.Add(new OpeningPlan { Side = Side.Back, Center = x, Width = w, Bottom = 0, Top = top, Kind = OpeningKind.Door, Other = ri });
            r.Openings.Add(new OpeningPlan { Side = Side.Front, Center = x, Width = w, Bottom = 0, Top = top, Kind = OpeningKind.Door, Other = hi });
            ground.Rooms.Add(hall);
            return hall;
        }
        return null;
    }

    /// <summary>The side of the room whose wall holds the segment (along X at z = line, or along Z at x = line), or null.</summary>
    private static Side? WallOf(RectPlan r, bool alongX, float line, float center, float width)
    {
        const float eps = 0.03f;
        float lo = center - width / 2, hi = center + width / 2;
        if (alongX)
        {
            if (lo < r.X0 - eps || hi > r.X1 + eps) return null;
            if (Math.Abs(line - r.Z0) < eps) return Side.Front;
            if (Math.Abs(line - r.Z1) < eps) return Side.Back;
            return null;
        }
        if (lo < r.Z0 - eps || hi > r.Z1 + eps) return null;
        if (Math.Abs(line - r.X0) < eps) return Side.Left;
        if (Math.Abs(line - r.X1) < eps) return Side.Right;
        return null;
    }

    /// <summary>Whether the segment still lies along the room's wall once trimmed to the box.</summary>
    private static bool Along(RoomPlan r, bool alongX, float center, float width) =>
        alongX ? center - width / 2 >= r.X0 - 0.03f && center + width / 2 <= r.X1 + 0.03f
               : center - width / 2 >= r.Z0 - 0.03f && center + width / 2 <= r.Z1 + 0.03f;

    /// <summary>Moves an outer wall within <see cref="SnapToBox"/> of the box onto it, unless another room is in the way.</summary>
    private static void SnapOuterWalls(List<RoomPlan> rooms, float hw, float hd)
    {
        foreach (var r in rooms)
        {
            bool Free(RectPlan grown) => !rooms.Any(o => o != r && grown.Overlaps(new RectPlan(o.X0, o.Z0, o.X1, o.Z1), 0.02f));
            if (r.X0 > -hw && r.X0 + hw < SnapToBox && Free(new RectPlan(-hw, r.Z0, r.X0, r.Z1))) Move(r, Side.Left, -hw);
            if (r.X1 < hw && hw - r.X1 < SnapToBox && Free(new RectPlan(r.X1, r.Z0, hw, r.Z1))) Move(r, Side.Right, hw);
            if (r.Z0 > -hd && r.Z0 + hd < SnapToBox && Free(new RectPlan(r.X0, -hd, r.X1, r.Z0))) Move(r, Side.Front, -hd);
            if (r.Z1 < hd && hd - r.Z1 < SnapToBox && Free(new RectPlan(r.X0, r.Z1, r.X1, hd))) Move(r, Side.Back, hd);
        }
        static void Move(RoomPlan r, Side side, float to)
        {
            switch (side)
            {
                case Side.Left: r.X0 = to; break;
                case Side.Right: r.X1 = to; break;
                case Side.Front: r.Z0 = to; break;
                default: r.Z1 = to; break;
            }
        }
    }
}
