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
///
/// <para>
/// Rooms are drawn as polygons; the interior builds rectangles. <see cref="RoomStrips"/> cuts each floor's rooms
/// into pieces (a right-angled room exactly, an angled wall as steps) and the pieces of one room are joined by
/// openings as tall as the room, so it reads as one space. Doors, arches and windows drawn on a wall go where
/// the pieces meet, or on a free side, nearest to where they were drawn.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    private const float MaxSkewDeg = 20f;
    /// <summary>An outer wall this close to the box's side is moved onto it, so the door and the windows find it.</summary>
    private const float SnapToBox = 0.6f;
    /// <summary>A drawn opening goes on a wall this close to where it was drawn, or nowhere (never across the room).</summary>
    private const double Placed = 0.75;

    /// <summary>
    /// Replaces <paramref name="l"/>'s floors with the house's: rooms, doorways, windows, the entry and the stairs.
    /// False, with the reason, when it does not fit the box; the caller then plans the building as usual.
    /// <paramref name="extra"/> are the pieces of a cut room other than its biggest, with their type: they are
    /// furnished as corridors, then given their type back (one room, one set of furniture).
    /// </summary>
    private static bool TryHouse(InteriorLayout l, Footprint fp, HouseLayout h, TileId tile, out string why,
        out List<(RoomPlan Piece, RoomType Type)> extra, out List<DrawnFloor> drawnFloors)
    {
        why = "";
        extra = new();
        drawnFloors = new();
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
        (double X, double Z) P(double x, double z) => (ex.Item1 * x + ez.Item1 * z + tx, ex.Item2 * x + ez.Item2 * z + tz);
        (double X0, double Z0, double X1, double Z1) R(double x0, double z0, double x1, double z1)
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
        var stairsOf = new List<List<(double X0, double Z0, double X1, double Z1, string Up)>>();
        var frontDoors = new List<(int Floor, RoomPlan Room, float Center)>(); // front doors drawn on the front wall
        foreach (var hf in h.Floors.OrderBy(f => f.Level))
        {
            int fi = floors.Count;
            var shapes = hf.Rooms.Select(r => r.Shape()!.Select(p => P(p.X, p.Z)).ToArray()).ToList();
            var stairs = hf.Stairs.Select(s => { var (x0, z0, x1, z1) = R(s.X0, s.Z0, s.X1, s.Z1); return (x0, z0, x1, z1, s.Up); }).ToList();
            stairsOf.Add(stairs);
            // an opening drawn on an angled wall keeps its stretch of wall whole, so it fits on one step
            var keep = hf.Openings.Select(o => { var (_, za) = P(o.X0, o.Z0); var (_, zb) = P(o.X1, o.Z1); return (Math.Min(za, zb) - 0.1, Math.Max(za, zb) + 0.1); })
                .Where(k => k.Item2 - k.Item1 > 0.25).ToList();
            var (cut, _) = RoomStrips.Cut(shapes, stairs.Select(s => (s.x0, s.z0, s.x1, s.z1)).ToList(), keep);

            // the box can be a little smaller than the drawing (two outlines, two rectangles): trim to it
            var fl = new FloorPlan();
            var roomOf = new List<int>();
            foreach (var p in cut)
            {
                var r = new RoomPlan
                {
                    X0 = (float)Math.Max(p.X0, -hw), X1 = (float)Math.Min(p.X1, hw),
                    Z0 = (float)Math.Max(p.Z0, -hd), Z1 = (float)Math.Min(p.Z1, hd),
                };
                if (r.Width < RoomStrips.MinPiece || r.Depth < RoomStrips.MinPiece / 2) continue;
                if (!Enum.TryParse<RoomType>(hf.Rooms[p.Room].Type, out var type)) { why = $"unknown room type {hf.Rooms[p.Room].Type}"; return false; }
                r.Type = type;
                fl.Rooms.Add(r);
                roomOf.Add(p.Room);
            }
            for (int room = 0; room < hf.Rooms.Count; room++)
                if (!roomOf.Contains(room)) { why = $"a {hf.Rooms[room].Type} on floor {hf.Level} falls outside the building's box"; return false; }
            SnapOuterWalls(fl.Rooms, hw, hd);
            // each piece's room, and every room as drawn, for MergePieces (#1197)
            drawnFloors.Add(new DrawnFloor(fl.Rooms.Select((r, i) => (r, roomOf[i])).ToDictionary(x => x.r, x => x.Item2),
                shapes.Select(s => s.Select(p => new Godot.Vector2((float)p.X, (float)p.Z)).ToArray()).ToList()));

            // one room cut into pieces: open between them, as tall as the room; the biggest keeps the furniture
            var pieces = fl.Rooms.Select((r, i) => new RoomPiece(roomOf[i], r.X0, r.Z0, r.X1, r.Z1)).ToList();
            var contacts = RoomStrips.Contacts(pieces);
            for (int room = 0; room < hf.Rooms.Count; room++)
            {
                var mine = Enumerable.Range(0, pieces.Count).Where(i => roomOf[i] == room).ToList();
                if (mine.Count < 2) continue;
                int main = mine.OrderByDescending(i => fl.Rooms[i].Area).First();
                foreach (int i in mine)
                {
                    fl.Rooms[i].Piece = true;
                    if (i != main) extra.Add((fl.Rooms[i], fl.Rooms[i].Type));
                }
            }
            foreach (var c in contacts.Where(c => roomOf[c.A] == roomOf[c.B]))
                Cut(fl, c, c.Lo, c.Hi, clear, OpeningKind.Arch);

            // the drawn openings, where the pieces of their rooms meet or stand free, nearest to where they were drawn
            foreach (var o in hf.Openings)
            {
                var on = Enumerable.Range(0, hf.Rooms.Count).Where(i => HouseLayout.OnEdge(hf.Rooms[i], o)).ToList();
                if (on.Count == 0) continue;
                var (mx, mz) = P((o.X0 + o.X1) / 2, (o.Z0 + o.Z1) / 2);
                double len = Math.Sqrt((o.X1 - o.X0) * (o.X1 - o.X0) + (o.Z1 - o.Z0) * (o.Z1 - o.Z0));
                if (on.Count >= 2 && o.Kind != "window")
                {
                    // between two rooms: on the stretch where a piece of one meets a piece of the other
                    var best = contacts.Where(c => (roomOf[c.A] == on[0] && roomOf[c.B] == on[1]) || (roomOf[c.A] == on[1] && roomOf[c.B] == on[0]))
                        .Where(c => c.Length >= 0.7 && DistTo(c, mx, mz) <= Placed).OrderBy(c => DistTo(c, mx, mz)).FirstOrDefault();
                    if (best.Length < 0.7) continue;
                    double w = Math.Min(len, best.Length - 0.1);
                    double along = best.AlongX ? mx : mz, mid = Math.Clamp(along, best.Lo + w / 2, best.Hi - w / 2);
                    Cut(fl, best, mid - w / 2, mid + w / 2, Math.Min(o.Kind == "arch" ? 2.4f : 2.1f, clear - 0.15f),
                        o.Kind == "arch" ? OpeningKind.Arch : OpeningKind.Door);
                    continue;
                }
                // on an outer wall: a window, or the drawn front door (the game's own door is the way in)
                var free = Enumerable.Range(0, pieces.Count).Where(i => roomOf[i] == on[0])
                    .SelectMany(i => Enumerable.Range(0, 4).SelectMany(side => RoomStrips.FreeAlong(pieces, contacts, i, side)
                        .Select(f => (Piece: i, Side: side, f.Lo, f.Hi))))
                    .Where(f => f.Hi - f.Lo >= 0.6 && FreeDist(pieces[f.Piece], f.Side, f.Lo, f.Hi, mx, mz) <= Placed)
                    .OrderBy(f => FreeDist(pieces[f.Piece], f.Side, f.Lo, f.Hi, mx, mz)).ToList();
                if (free.Count == 0) continue;
                var (pi, sd, lo, hi) = free[0];
                double fw = Math.Min(len, hi - lo - 0.2);
                double fa = sd is 0 or 2 ? mx : mz, fc = Math.Clamp(fa, lo + fw / 2, hi - fw / 2);
                var room = fl.Rooms[pi];
                if (o.Kind == "window")
                {
                    if (fw < 0.4) continue;
                    room.Openings.Add(new OpeningPlan
                    {
                        Side = (Side)sd, Center = (float)fc, Width = (float)fw, Bottom = 0.9f, Top = Math.Min(2.1f, clear - 0.2f), Kind = OpeningKind.Window,
                    });
                }
                else if (sd == 0 && Math.Abs(room.Z0 + hd) < 0.02f) frontDoors.Add((fi, room, (float)fc));
            }
            floors.Add(fl);
        }

        // stairs: a flight on its floor, a hole over it in the floor above with a rail on its open sides
        for (int fi = 0; fi < floors.Count; fi++)
            foreach (var (sx0, sz0, sx1, sz1, up) in stairsOf[fi])
            {
                if (fi + 1 >= floors.Count) { why = "a stair with no floor above"; return false; }
                float x0 = (float)sx0, z0 = (float)sz0, x1 = (float)sx1, z1 = (float)sz1;
                // the climb's direction, turned with the rest
                var (dx, dz) = up switch { "+x" => (1, 0), "-x" => (-1, 0), "-z" => (0, -1), _ => (0, 1) };
                var (gx, gz) = P(dx, dz);
                var (ox, oz) = P(0, 0);
                double runX = gx - ox, runZ = gz - oz;
                FlightPlan flight = Math.Abs(runZ) > 0.5
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
        var drawn = frontDoors.Where(d => d.Floor == l.Below && fronts.Contains(d.Room)).Select(d => ((float, RoomPlan)?)(d.Center, d.Room)).FirstOrDefault();
        float want = drawn?.Item1 ?? fp.EntryX;
        var entryRoom = drawn?.Item2 ?? fronts.OrderBy(r => want < r.X0 ? r.X0 - want : want > r.X1 ? want - r.X1 : 0).First();
        l.EntryX = Fit(want, entryRoom.X0 + ew / 2 + 0.2f, entryRoom.X1 - ew / 2 - 0.2f);
        entryRoom.Openings.RemoveAll(o => o.Side == Side.Front && Math.Abs(o.Center - l.EntryX) < (o.Width + ew) / 2 + 0.1f);
        entryRoom.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = ew, Bottom = 0, Top = Math.Min(l.DoorHeight, clear - 0.15f), Kind = OpeningKind.Entry,
        });
        l.House = h.Hash();
        return true;
    }

    /// <summary>A drawn floor as <see cref="TryHouse"/> cut it: each piece's drawn room, and every room's polygon in the plan frame.</summary>
    private sealed record DrawnFloor(Dictionary<RoomPlan, int> RoomOf, List<Godot.Vector2[]> Shapes);

    /// <summary>
    /// Each drawn room cut into pieces made one shaped room again (#1197): its polygon as drawn (an
    /// angled wall straight, not stepped), its biggest piece its core. What was cut in the other
    /// pieces' walls moves onto the polygon's edge it stands on; the openings between its own pieces
    /// go. A room holding a stair or under a stair's hole keeps its pieces (a floor with a hole in it
    /// is not drawn), and so does one whose cuts would not land on its outline. Kept only if the
    /// plan still validates.
    /// </summary>
    private static void MergePieces(InteriorLayout l, List<DrawnFloor> drawn, List<(RoomPlan Piece, RoomType Type)> extra)
    {
        string saved = l.ToJson();
        // the extra pieces by where they are, to find them again in the plan as it was
        var extraAt = extra.Select(x =>
        {
            int f = l.Floors.FindIndex(fl => fl.Rooms.Contains(x.Piece));
            return (F: f, I: l.Floors[f].Rooms.IndexOf(x.Piece), x.Type);
        }).ToList();
        int before = InteriorValidator.Validate(l).Count;
        float hw = l.Width / 2, hd = l.Depth / 2;
        bool merged = false;
        for (int f = 0; f < l.Floors.Count && f < drawn.Count; f++)
        {
            var floor = l.Floors[f];
            var (roomOf, shapes) = drawn[f];
            var above = f + 1 < l.Floors.Count ? l.Floors[f + 1] : null;
            for (int room = 0; room < shapes.Count; room++)
            {
                var pieces = floor.Rooms.Where(r => roomOf.TryGetValue(r, out int k) && k == room).ToList();
                if (pieces.Count < 2) continue;
                bool Holed(RoomPlan r)
                {
                    var rect = new RectPlan(r.X0, r.Z0, r.X1, r.Z1);
                    return floor.Holes.Any(h => h.Overlaps(rect)) || floor.AllFlights().Any(fl => fl.Area().Overlaps(rect))
                           || above != null && above.Holes.Any(h => h.Overlaps(rect));
                }
                if (pieces.Any(Holed)) { OutlineFailure += $" | {pieces[0].Type}: a stair in it"; continue; }
                // the drawing, counter-clockwise and inside the box
                var shape = RoomShapes.ClipToRect(shapes[room], -hw, -hd, hw, hd, out bool split);
                if (split || shape.Count < 3) { OutlineFailure += $" | {pieces[0].Type}: its drawing leaves the box{(split ? " in two" : "")}"; continue; }
                var main = pieces.OrderByDescending(r => r.Area).First();
                // every cut of the other pieces onto the outline, by edge; one that will not land keeps the pieces
                var moved = new List<(RoomPlan From, OpeningPlan O, OpeningPlan To)>();
                bool lands = true;
                foreach (var p in pieces)
                    foreach (var o in p.Openings)
                    {
                        if (o.Other >= 0 && pieces.Contains(floor.Rooms[o.Other])) continue;
                        var (at, _, along) = p.WallFrame(o);
                        var a = at - along * (o.Width / 2);
                        var b = at + along * (o.Width / 2);
                        bool On(int k, Godot.Vector2 q) => RoomShapes.SegmentDistance(shape[k], shape[(k + 1) % shape.Count], q) < 0.05f;
                        // on the outline as it is, or on the step an angled wall was cut into: the edge
                        // nearest it, the cut put on the drawn line (both rooms either side land on one point)
                        int exact = Enumerable.Range(0, shape.Count).FirstOrDefault(k => On(k, a) && On(k, b), -1);
                        // an entry stays exactly where the door leads in, on the front wall as drawn
                        if (o.Kind == OpeningKind.Entry && exact < 0) { lands = false; OutlineFailure += $" | {main.Type}: the entry is off its outline"; break; }
                        if (p == main && exact >= 0) continue;
                        int edge = Enumerable.Range(0, shape.Count).OrderBy(k => RoomShapes.SegmentDistance(shape[k], shape[(k + 1) % shape.Count], at)).First();
                        var e0 = shape[edge];
                        var e1 = shape[(edge + 1) % shape.Count];
                        float len = e0.DistanceTo(e1), c = (at - e0).Dot((e1 - e0) / len);
                        if (RoomShapes.SegmentDistance(e0, e1, at) > 0.35f || c < o.Width / 2 || c > len - o.Width / 2) { lands = false; OutlineFailure += $" | {main.Type}: a {o.Kind} {RoomShapes.SegmentDistance(e0, e1, at):F2} m off its drawn wall"; break; }
                        moved.Add((p, o, new OpeningPlan
                        {
                            Edge = edge, Center = c, Width = o.Width, Bottom = o.Bottom, Top = o.Top, Kind = o.Kind, Other = o.Other, Open = o.Open,
                        }));
                    }
                if (!lands) continue;
                // the main piece takes the whole room; the others go, and every index into the floor with them
                int mainIndex = floor.Rooms.IndexOf(main);
                main.SetShape(shape);
                main.Piece = false;
                main.Openings.RemoveAll(o => o.Other >= 0 && pieces.Contains(floor.Rooms[o.Other]) || moved.Any(m => m.O == o));
                main.Openings.AddRange(moved.Select(m => m.To));
                var gone = pieces.Where(p => p != main).Select(p => floor.Rooms.IndexOf(p)).OrderByDescending(i => i).ToList();
                // the rooms on the far side of a moved cut now open into the main piece
                foreach (var r in floor.Rooms)
                    foreach (var o in r.Openings)
                        if (o.Other >= 0 && gone.Contains(o.Other)) o.Other = mainIndex;
                foreach (int k in gone)
                {
                    floor.Rooms.RemoveAt(k);
                    foreach (var r in floor.Rooms)
                        foreach (var o in r.Openings)
                            if (o.Other > k) o.Other--;
                }
                extra.RemoveAll(x => pieces.Contains(x.Piece));
                merged = true;
            }
        }
        if (!merged) return;
        var after = InteriorValidator.Validate(l);
        if (after.Count <= before && l.Floors.All(fl => fl.Rooms.All(Drawable))) return;
        OutlineFailure += $" | merged, it does not validate: {string.Join("; ", after.Take(3))}";
        // as it was: the pieces
        var back = InteriorLayout.FromJson(saved)!;
        l.Floors = back.Floors;
        extra.Clear();
        extra.AddRange(extraAt.Select(x => (l.Floors[x.F].Rooms[x.I], x.Type)));
    }

    /// <summary>Cuts an opening from <paramref name="lo"/> to <paramref name="hi"/> where two pieces meet, on both of them.</summary>
    private static void Cut(FloorPlan fl, PieceContact c, double lo, double hi, float top, OpeningKind kind)
    {
        float center = (float)((lo + hi) / 2), width = (float)(hi - lo);
        fl.Rooms[c.A].Openings.Add(new OpeningPlan
        {
            Side = c.AlongX ? Side.Back : Side.Right, Center = center, Width = width, Bottom = 0, Top = top, Kind = kind, Other = c.B,
        });
        fl.Rooms[c.B].Openings.Add(new OpeningPlan
        {
            Side = c.AlongX ? Side.Front : Side.Left, Center = center, Width = width, Bottom = 0, Top = top, Kind = kind, Other = c.A,
        });
    }

    private static double DistTo(PieceContact c, double x, double z)
    {
        double along = c.AlongX ? x : z, across = c.AlongX ? z : x;
        double d = along < c.Lo ? c.Lo - along : along > c.Hi ? along - c.Hi : 0;
        return Math.Sqrt(d * d + (across - c.At) * (across - c.At));
    }

    private static double FreeDist(RoomPiece p, int side, double lo, double hi, double x, double z)
    {
        double at = side switch { 0 => p.Z0, 1 => p.X1, 2 => p.Z1, _ => p.X0 };
        var c = new PieceContact(0, 0, side is 0 or 2, at, lo, hi);
        return DistTo(c, x, z);
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

    /// <summary>Moves an outer wall within <see cref="SnapToBox"/> of the box onto it, unless another room is in the way.</summary>
    private static void SnapOuterWalls(List<RoomPlan> rooms, float hw, float hd)
    {
        foreach (var r in rooms)
        {
            bool Free(RectPlan grown) => !rooms.Any(o => o != r && grown.Overlaps(new RectPlan(o.X0, o.Z0, o.X1, o.Z1), 0.02f));
            if (r.X0 > -hw && r.X0 + hw < SnapToBox && Free(new RectPlan(-hw, r.Z0, r.X0, r.Z1))) r.X0 = -hw;
            if (r.X1 < hw && hw - r.X1 < SnapToBox && Free(new RectPlan(r.X1, r.Z0, hw, r.Z1))) r.X1 = hw;
            if (r.Z0 > -hd && r.Z0 + hd < SnapToBox && Free(new RectPlan(r.X0, -hd, r.X1, r.Z0))) r.Z0 = -hd;
            if (r.Z1 < hd && hd - r.Z1 < SnapToBox && Free(new RectPlan(r.X0, r.Z1, r.X1, hd))) r.Z1 = hd;
        }
    }
}
