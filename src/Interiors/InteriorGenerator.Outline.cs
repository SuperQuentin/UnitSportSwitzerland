using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Interiors that follow the building's real outline (#1290), angled facades included, close
/// enough rather than exact.
///
/// <para>
/// A plan is made of rectangles, as ever; afterwards each room may take a polygon
/// (<see cref="RoomPlan.Shape"/>) and keeps its rectangle as its core. Two fits, chosen by how the
/// plan was made:
/// </para>
/// <list type="bullet">
/// <item><b>Extend</b>: a plan inside the outline (a house on its biggest front rectangle, flats
/// on their wings) grows each facade room out to it, from every point of its facade wall
/// straight out until it leaves the building or meets another room. An angled facade comes out as
/// one straight angled wall; a corner both walls grow into follows the outline round. A street
/// door's stretch stays where it is (a recessed entrance). A growth deeper than
/// <see cref="DeepAt"/> off a room others open off is a room of its own (the arm of an L).</item>
/// <item><b>Clip</b>: a plan on the whole box (a hall, a barn, a works) has each room cut by the
/// outline.</item>
/// </list>
/// <para>
/// Rooms holding a stair, a shaft, a lift or a ramp keep their rectangle. Anything that does not
/// validate as well as the plan did before goes back to the rectangles.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>A growth deeper than this, off a room others open off, is a room of its own.</summary>
    public const float DeepAt = 4.0f;
    /// <summary>Less growth than this is none: the wall stays.</summary>
    private const float MinReach = 0.15f;
    private const float MaxReach = 60f;
    /// <summary>How far past a wall a point is tested as "outside" it.</summary>
    private const float Step = 0.02f;
    /// <summary>A side street door whose doorway inside stands further than this from it, m, reads as locked.</summary>
    private const float FarDoor = 3f;

    /// <summary>Off only for <c>--outlinesurvey --no-outlines</c>: plans as before #1290, to compare.</summary>
    internal static bool FollowOutlines = true;

    /// <summary>The building's roof on the outline's grid in the plan frame, or null when it is its box.</summary>
    internal static bool[,]? OutlineOf(Footprint fp, Building b) =>
        PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth);

    /// <summary>The outline's rings in the plan's frame.</summary>
    internal static List<Vector2[]> RingsFor(InteriorLayout l, bool[,] grid) => PlanOutline.Rings(grid, l.Width, l.Depth);

    /// <summary>An entrance's real door in the plan's frame: where it stands on the facade and the way it faces out.</summary>
    internal static (Vector2 At, Vector2 Out) DoorInPlan(InteriorLayout l, EntrancePlan e)
    {
        var u = new Vector2(MathF.Cos(l.Yaw), -MathF.Sin(l.Yaw));
        var v = new Vector2(-u.Y, u.X);
        var d = new Vector2(e.DoorX - l.CenterX, e.DoorZ - l.CenterZ);
        var o = new Vector2(e.DoorOutX, e.DoorOutZ);
        return (new(d.Dot(u), d.Dot(v)), new Vector2(o.Dot(u), o.Dot(v)).Normalized());
    }

    /// <summary>How far an entrance's doorway stands from its real door, m, and how far it is turned from it, degrees.</summary>
    internal static (float Gap, float Turn) DoorGap(InteriorLayout l, EntrancePlan e)
    {
        var (at, outward) = DoorInPlan(l, e);
        var inward = new Vector2(e.InX, e.InZ).Normalized();
        float cos = Math.Clamp(-inward.Dot(outward), -1, 1);
        return (at.DistanceTo(new Vector2(e.X, e.Z)), MathF.Acos(cos) * 180 / MathF.PI);
    }

    // ---- a cored plan on the biggest front rectangle --------------------------------------------

    /// <summary>
    /// A plan (<paramref name="plan"/>: a house's or an office's <see cref="TryCored"/>, a barn's
    /// <see cref="SingleRoom"/>) made on the biggest rectangle of the outline that stands behind the
    /// main door, then moved into the building's frame and grown out to the outline later: an
    /// L-shaped house no longer has rooms in its yard. Its other doors are fitted there too. False
    /// (the plan untouched) when no such rectangle is worth it.
    /// </summary>
    private static bool TryOnCore(InteriorLayout l, Footprint fp, Building b, Func<bool> plan)
    {
        float margin = l.EntryWidth / 2 + 0.35f;
        if (PlanOutline.FrontCore(b, fp.Center, fp.AxisU, fp.Width, fp.Depth, l.EntryX - margin, l.EntryX + margin, 4.5f) is not { } core)
            return false;
        float cw = core.X1 - core.X0, cd = core.Z1 - core.Z0;
        // the box itself, or nearly: the plain plan, clipped later if need be
        if (cw * cd >= PlanOutline.Boxy * fp.Width * fp.Depth || cw * cd < 25f) return false;
        float cx = (core.X0 + core.X1) / 2, cz = (core.Z0 + core.Z1) / 2;
        var (w, d, x) = (l.Width, l.Depth, l.EntryX);
        (l.Width, l.Depth, l.EntryX) = (cw, cd, x - cx);
        bool ok = plan();
        // the ways in written out, the main one too: its doorway is the core's front, which may
        // stand back from the box's (a recessed entrance in a bent front)
        if (ok) Entrances(l, fp with { Center = fp.Center + fp.AxisU * cx + fp.AxisV * cz, Width = cw, Depth = cd });
        (l.Width, l.Depth, l.EntryX) = (w, d, x);
        if (!ok)
        {
            l.Floors.Clear();
            l.Entrances.Clear();
            l.InnerDoors.Clear();
            l.Lifts.Clear();
            l.Below = 0;
            return false;
        }
        Shift(l, cx, cz);
        return true;
    }

    /// <summary>Everything of a plan moved by (dx, dz) in its own frame.</summary>
    private static void Shift(InteriorLayout l, float dx, float dz)
    {
        static float Along(Side s, float dx, float dz) => s is Side.Front or Side.Back ? dx : dz;
        void Rect(RectPlan r) { r.X0 += dx; r.X1 += dx; r.Z0 += dz; r.Z1 += dz; }
        foreach (var floor in l.Floors)
        {
            foreach (var r in floor.Rooms)
            {
                r.X0 += dx; r.X1 += dx; r.Z0 += dz; r.Z1 += dz;
                if (r.Shape != null) r.SetShape(r.Corners().Select(p => p + new Vector2(dx, dz)).ToArray());
                foreach (var o in r.Openings) if (o.Edge == null) o.Center += Along(o.Side, dx, dz);
            }
            floor.Holes.ForEach(Rect);
            floor.Rails.ForEach(Rect);
            floor.Guards.ForEach(Rect);
            foreach (var g in floor.Landings) { g.X0 += dx; g.X1 += dx; g.Z0 += dz; g.Z1 += dz; }
            foreach (var fl in floor.AllFlights())
            {
                float across = fl.AlongX ? dz : dx, run = fl.AlongX ? dx : dz;
                fl.X0 += across; fl.X1 += across; fl.ZBottom += run; fl.ZTop += run;
            }
        }
        foreach (var lift in l.Lifts)
        {
            lift.X0 += dx; lift.X1 += dx; lift.Z0 += dz; lift.Z1 += dz;
            lift.DoorCenter += Along(lift.DoorSide, dx, dz);
        }
        foreach (var d in l.InnerDoors) d.Center += Along(d.Side, dx, dz);
        foreach (var e in l.Entrances) { e.X += dx; e.Z += dz; }
        foreach (var p in l.Furniture) { p.X += dx; p.Z += dz; }
        l.EntryX += dx;
    }

    // ---- fitting a finished plan ----------------------------------------------------------------

    /// <summary>
    /// The plan's rooms fitted to the outline: extended out to it when the plan lies inside it, cut
    /// by it when the plan is the box. Kept only if it validates as well as before.
    /// </summary>
    internal static void FitOutline(InteriorLayout l, bool[,] grid)
    {
        OutlineFailure = null;
        if (l.Floors.Count == 0) return;
        var rings = RingsFor(l, grid);
        if (rings.Count == 0) return;
        var before = InteriorValidator.Validate(l);
        // what the fit may change, kept whole to go back to
        string saved = l.ToJson();
        bool inside = l.Floors.All(fl => fl.Rooms.All(r => RectInside(rings, r.X0, r.Z0, r.X1, r.Z1, 0.3f)));
        bool Good()
        {
            var after = InteriorValidator.Validate(l);
            if (after.Count > before.Count) return Unfit($"{(inside ? "grown" : "cut")}, it does not validate: {string.Join("; ", after.Except(before).Take(3))}");
            if (l.Floors.SelectMany(fl => fl.Rooms).FirstOrDefault(r => !Drawable(r)) is { } bad) return Unfit($"the {bad.Type}'s shape does not triangulate");
            return true;
        }
        void Back()
        {
            var back = InteriorLayout.FromJson(saved)!;
            l.Floors = back.Floors;
            l.InnerDoors = back.InnerDoors;
            l.Entrances = back.Entrances;
        }
        if ((inside ? Extend(l, rings) : Clip(l, rings)) && Good()) return;
        Back();
        // a room dropped out in the yard may have been the way to another: cut again, dropping none
        if (!inside && Clip(l, rings, drop: false) && Good()) return;
        Back();
    }

    /// <summary>
    /// Why the last fit on this thread went back to the rectangles, or null if it did not (for
    /// checks: <c>--shapedcheck</c> prints it).
    /// </summary>
    [ThreadStatic] internal static string? OutlineFailure;

    private static bool Unfit(string why)
    {
        OutlineFailure = why;
        return false;
    }

    /// <summary>
    /// How well a plan's ground floor follows the outline, on a 0.5 m grid: m² of the outline it
    /// fills, less twice the m² it stands outside it (a room in the yard is worse than an empty corner).
    /// </summary>
    private static float OutlineScore(InteriorLayout l, Footprint fp, Building b)
    {
        if (l.Floors.Count == 0 || PlanOutline.FineGrid(b, fp.Center, fp.AxisU, fp.Width, fp.Depth) is not { } grid) return 0;
        var rings = RingsFor(l, grid);
        var rooms = l.GroundFloor.Rooms;
        float score = 0;
        for (float x = -l.Width / 2 + 0.25f; x < l.Width / 2; x += 0.5f)
            for (float z = -l.Depth / 2 + 0.25f; z < l.Depth / 2; z += 0.5f)
            {
                bool room = false;
                foreach (var r in rooms)
                    if (r.Holds(x, z)) { room = true; break; }
                if (room) score += RoomShapes.Contains(rings, new Vector2(x, z)) ? 0.25f : -0.5f;
            }
        return score;
    }

    /// <summary>Whether a rectangle, shrunk by <paramref name="by"/>, lies inside the rings.</summary>
    private static bool RectInside(List<Vector2[]> rings, float x0, float z0, float x1, float z1, float by)
    {
        x0 += by; z0 += by; x1 -= by; z1 -= by;
        if (x1 <= x0 || z1 <= z0) return true;
        foreach (var q in new[] { new Vector2(x0, z0), new Vector2(x1, z0), new Vector2(x1, z1), new Vector2(x0, z1), new Vector2((x0 + x1) / 2, (z0 + z1) / 2) })
            if (!RoomShapes.Contains(rings, q)) return false;
        // a notch of the outline reaching into it between the corners
        foreach (var r in rings)
            foreach (var v in r)
                if (v.X > x0 && v.X < x1 && v.Y > z0 && v.Y < z1) return false;
        return true;
    }

    /// <summary>Whether a room may take a shape: no stair, shaft, lift, ramp or shelter in it.</summary>
    private static bool Shapeable(InteriorLayout l, int f, RoomPlan r)
    {
        if (r.Type is RoomType.Stairwell or RoomType.Elevator or RoomType.Ramp or RoomType.Shelter or RoomType.Vault) return false;
        var rect = new RectPlan(r.X0, r.Z0, r.X1, r.Z1);
        var floor = l.Floors[f];
        if (floor.Holes.Any(h => h.Overlaps(rect)) || floor.AllFlights().Any(fl => fl.Area().Overlaps(rect))
            || floor.Landings.Any(g => rect.Overlaps(new RectPlan(g.X0, g.Z0, g.X1, g.Z1)))) return false;
        int top = f + r.Span;
        if (top < l.Floors.Count && l.Floors[top].Holes.Any(h => h.Overlaps(rect))) return false;
        return !l.Lifts.Any(lift => rect.Overlaps(new RectPlan(lift.X0, lift.Z0, lift.X1, lift.Z1)));
    }

    /// <summary>A shape that triangulates into its own area: simple, so its floor is whole.</summary>
    private static bool Drawable(RoomPlan r)
    {
        if (r.Shape == null) return true;
        var c = r.Corners();
        float area = RoomShapes.Area(c);
        if (area <= 0) return false;
        var tris = RoomShapes.Triangulate(c);
        float sum = 0;
        for (int i = 0; i + 2 < tris.Count; i += 3)
            sum += Math.Abs(RoomShapes.Area(new[] { c[tris[i]], c[tris[i + 1]], c[tris[i + 2]] }));
        return Math.Abs(sum - area) < 0.02f * area + 0.01f;
    }

    // ---- extend ------------------------------------------------------------------------------

    /// <summary>One wall of a rectangle, walked counter-clockwise: from its start, the room on the left.</summary>
    private readonly record struct WallLine(Side Side, Vector2 Origin, Vector2 Along, Vector2 Out, float Length)
    {
        public Vector2 At(float u, float d = 0) => Origin + Along * u + Out * d;
        /// <summary>The opening coordinate (<see cref="OpeningPlan.Center"/>) of a point u along it.</summary>
        public float Coord(float u) => Side is Side.Front or Side.Back ? At(u).X : At(u).Y;
        public float U(float coord) => Side switch
        {
            Side.Front => coord - Origin.X,
            Side.Right => coord - Origin.Y,
            Side.Back => Origin.X - coord,
            _ => Origin.Y - coord,
        };
    }

    private static WallLine[] Lines(RoomPlan r) =>
    [
        new(Side.Front, new(r.X0, r.Z0), new(1, 0), new(0, -1), r.Width),
        new(Side.Right, new(r.X1, r.Z0), new(0, 1), new(1, 0), r.Depth),
        new(Side.Back, new(r.X1, r.Z1), new(-1, 0), new(0, 1), r.Width),
        new(Side.Left, new(r.X0, r.Z1), new(0, -1), new(-1, 0), r.Depth),
    ];

    /// <summary>A stretch of a wall and how far the room grows out over it, at each end; and what it met there.</summary>
    private readonly record struct Reach(float U0, float D0, float U1, float D1, int Ring, int Edge);

    private static bool Extend(InteriorLayout l, List<Vector2[]> rings)
    {
        var claims = l.Floors.Select(_ => new List<Vector2[]>()).ToList();
        var fresh = new HashSet<RoomPlan>();
        var cache = new FloorRects();
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var floor = l.Floors[f];
            int count = floor.Rooms.Count;
            // the planned rooms, then the arms' rooms they made: those grow too, but make no arms
            for (int i = 0; i < floor.Rooms.Count; i++)
            {
                var r = floor.Rooms[i];
                // a room with a stair keeps its rectangle, but may still have an arm's rooms off it
                bool shapeable = Shapeable(l, f, r);
                bool arms = i < count;
                if (!shapeable && !(arms && Splits(l, r) && r.Type is not (RoomType.Elevator or RoomType.Ramp))) continue;
                if (Grow(l, f, i, rings, claims[f], arms, fresh, cache, shapeable) is not { } shape) continue;
                r.SetShape(shape);
                for (int g = f; g < Math.Min(l.Floors.Count, f + r.Span); g++) claims[g].Add(shape);
            }
        }
        MoveEntries(l);
        EdgeWindows(l, rings, fresh);
        return true;
    }

    /// <summary>A floor's rooms' rectangles as rings, built once a floor (and again when an arm adds rooms).</summary>
    private sealed class FloorRects
    {
        public int Floor = -1, Count = -1;
        public readonly List<(RoomPlan Room, Vector2[] Ring)> All = new();
    }

    /// <summary>The other rooms' rectangles on a floor (with those rising into it from below), as rings.</summary>
    private static List<Vector2[]> Others(InteriorLayout l, int f, RoomPlan self, FloorRects cache)
    {
        if (cache.Floor != f || cache.Count != l.Floors[f].Rooms.Count)
        {
            cache.All.Clear();
            for (int g = 0; g <= f; g++)
                foreach (var q in l.Floors[g].Rooms)
                    if (g + q.Span > f)
                        cache.All.Add((q, [new(q.X0, q.Z0), new(q.X1, q.Z0), new(q.X1, q.Z1), new(q.X0, q.Z1)]));
            (cache.Floor, cache.Count) = (f, l.Floors[f].Rooms.Count);
        }
        var o = new List<Vector2[]>(cache.All.Count);
        foreach (var (q, ring) in cache.All)
            if (q != self) o.Add(ring);
        return o;
    }

    /// <summary>
    /// Room <paramref name="index"/> of floor <paramref name="f"/> grown out to the outline: its
    /// polygon, or null if it does not grow. A deep stretch off a room others open off becomes a
    /// room of its own, appended to the floor, with a door into this one.
    /// </summary>
    /// <param name="shapeable">False for a room with a stair: it only gets its arms' rooms, never a shape.</param>
    private static Vector2[]? Grow(InteriorLayout l, int f, int index, List<Vector2[]> rings, List<Vector2[]> claims, bool arms,
        HashSet<RoomPlan> fresh, FloorRects cache, bool shapeable = true)
    {
        var floor = l.Floors[f];
        var r = floor.Rooms[index];
        var allOthers = Others(l, f, r, cache);
        allOthers.AddRange(claims);
        // everything a ray from a wall can meet: the outline first, so its rings keep their indices
        var allWalls = new List<IReadOnlyList<Vector2>>(rings);
        allWalls.AddRange(allOthers);

        var lines = Lines(r);
        var reaches = new List<Reach>[4];
        var freed = new List<(float Lo, float Hi)>[4];
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            // a way in or out of the building keeps its wall (a recessed entrance), but for a street door
            // standing out in front of it: the room grows over it out to the facade, where the doorway goes
            // after (MoveEntries)
            (float Lo, float Hi) Stretch(OpeningPlan o) => (Math.Min(w.U(o.Center - o.Width / 2), w.U(o.Center + o.Width / 2)) - 0.3f,
                Math.Max(w.U(o.Center - o.Width / 2), w.U(o.Center + o.Width / 2)) + 0.3f);
            var ways = r.Openings
                .Where(o => o.Edge == null && o.Side == w.Side && (o.Kind == OpeningKind.Entry || o.Kind is OpeningKind.Door or OpeningKind.Arch && o.Other < 0))
                .ToList();
            bool alone = floor.Rooms.Count == 1 || HallLike(r.Type);
            var keep = ways.Where(o => !DoorAhead(l, r, w, o, alone)).Select(Stretch).ToList();
            freed[s] = ways.Where(o => DoorAhead(l, r, w, o, alone)).Select(Stretch).ToList();
            var cuts = new List<float> { 0, w.Length };
            foreach (var (lo, hi) in keep) { cuts.Add(lo); cuts.Add(hi); }
            // only what stands in front of this wall, out to the box's side, can be met (a big block has
            // hundreds of rooms a floor); the far ones are left empty so every ring keeps its index
            float reach = w.Out.X > 0.5f ? l.Width / 2 - w.Origin.X : w.Out.X < -0.5f ? w.Origin.X + l.Width / 2
                : w.Out.Y > 0.5f ? l.Depth / 2 - w.Origin.Y : w.Origin.Y + l.Depth / 2;
            var sweep = new[] { w.At(0), w.At(w.Length), w.At(0, reach), w.At(w.Length, reach) };
            float sx0 = sweep.Min(p => p.X) - 0.05f, sx1 = sweep.Max(p => p.X) + 0.05f, sz0 = sweep.Min(p => p.Y) - 0.05f, sz1 = sweep.Max(p => p.Y) + 0.05f;
            bool Near(IReadOnlyList<Vector2> ring) =>
                ring.Max(p => p.X) >= sx0 && ring.Min(p => p.X) <= sx1 && ring.Max(p => p.Y) >= sz0 && ring.Min(p => p.Y) <= sz1;
            var walls = allWalls.Select((ring, i) => i < rings.Count || Near(ring) ? ring : Array.Empty<Vector2>()).ToList();
            var others = allOthers.Where(Near).ToList();
            bool Blocked(Vector2 p) => !RoomShapes.Contains(rings, p) || others.Any(o => RoomShapes.Contains(o, p));
            // where anything could change: every corner seen from the wall, and every edge crossing its line
            foreach (var ring in walls)
                for (int k = 0; k < ring.Count; k++)
                {
                    var v = ring[k];
                    cuts.Add((v - w.Origin).Dot(w.Along));
                    var nv = ring[(k + 1) % ring.Count];
                    float da = (v - w.Origin).Dot(w.Out), db = (nv - w.Origin).Dot(w.Out);
                    if (da * db < 0)
                    {
                        var x = v + (nv - v) * (da / (da - db));
                        cuts.Add((x - w.Origin).Dot(w.Along));
                    }
                }
            cuts = cuts.Where(u => u >= 0 && u <= w.Length).OrderBy(u => u).ToList();
            var list = reaches[s] = new List<Reach>();
            float last = float.NaN;
            foreach (float u in cuts)
            {
                if (!float.IsNaN(last) && u - last > 0.01f)
                {
                    float ua = last, ub = u, um = (ua + ub) / 2;
                    var p = w.At(um, Step);
                    if (keep.Any(k => um > k.Lo && um < k.Hi) || Blocked(p)) list.Add(new Reach(ua, 0, ub, 0, -1, -1));
                    else
                    {
                        float t = RoomShapes.Cast(walls, p, w.Out, MaxReach, out int ring, out int edge);
                        if (ring < 0 || t + Step < MinReach) list.Add(new Reach(ua, 0, ub, 0, -1, -1));
                        else
                        {
                            var e = walls[ring];
                            var a = e[edge];
                            var b = e[(edge + 1) % e.Count];
                            // past where the edge crosses the wall line it would be behind it: none
                            float Depth(float uu) => LineHit(w.At(uu), w.Out, a, b) is { } h ? Math.Clamp(h, 0, MaxReach) : t + Step;
                            list.Add(new Reach(ua, Depth(ua), ub, Depth(ub), ring, edge));
                        }
                    }
                }
                last = u;
            }
        }

        // ---- the arm of an L: a deep growth off a room others open off gets rooms of its own ----
        // (as rectangles, which grow out to the outline in their turn), and this room is grown again
        if (arms && Splits(l, r))
        {
            bool added = false;
            for (int s = 0; s < 4; s++)
                foreach (var run in Runs(reaches[s]).ToList())
                {
                    if (run.Max(x => Math.Max(x.D0, x.D1)) < DeepAt || run[^1].U1 - run[0].U0 < 2.6f) continue;
                    // never rooms over the way in to a street door: it is this room's
                    if (freed[s].Any(k => k.Lo < run[^1].U1 && k.Hi > run[0].U0)) continue;
                    var (bu0, bu1, bd) = RunCore(run);
                    added |= bd >= 2.4f && bu1 - bu0 >= 2.4f && Arm(l, f, index, lines[s], bu0, bu1, bd, fresh);
                }
            if (added) return Grow(l, f, index, rings, claims, false, fresh, cache, shapeable);
        }
        if (!shapeable) return null;

        // a room grows across a slanted facade's sliver, not down a whole wing: deeper than that
        // it is a room no one would build (a WC 25 m long). A hall alone on its floor fills the building.
        if (!(floor.Rooms.Count == 1 || HallLike(r.Type)))
            for (int s = 0; s < 4; s++)
            {
                var list = reaches[s];
                for (int k = 0; k < list.Count; k++)
                    if (list[k].D0 > DeepAt || list[k].D1 > DeepAt)
                        list[k] = list[k] with { D0 = Math.Min(list[k].D0, DeepAt), D1 = Math.Min(list[k].D1, DeepAt), Ring = -1 };
            }

        if (reaches.All(list => list.All(x => x.D0 < MinReach && x.D1 < MinReach))) return null;

        // ---- the polygon, wall by wall, round the corners ---------------------------------------
        var pts = new List<Vector2>();
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            var list = reaches[s];
            var prevLine = lines[(s + 3) % 4];
            var prevList = reaches[(s + 3) % 4];
            var end = prevList[^1];
            var start = list[0];
            var corner = w.Origin;
            // where both walls grow and both met the same ring of the outline, follow it round
            List<Vector2>? fill = null;
            if (end.D1 >= MinReach && start.D0 >= MinReach && end.Ring >= 0 && end.Ring == start.Ring && end.Ring < rings.Count)
                fill = Round(rings[end.Ring], end.Edge, start.Edge, corner, prevLine.Out, w.Out, allOthers);
            if (fill != null) pts.AddRange(fill);
            else pts.Add(corner);
            foreach (var x in list)
            {
                pts.Add(w.At(x.U0, x.D0));
                pts.Add(w.At(x.U1, x.D1));
            }
        }
        var shape = RoomShapes.Tidy(pts);
        if (shape.Count < 3 || RoomShapes.Area(shape) < r.Area + 0.1f) return null;

        // the walls it grew past are gone, and their windows with them
        for (int s = 0; s < 4; s++)
        {
            var w = lines[s];
            foreach (var x in reaches[s].Where(x => x.D0 >= MinReach || x.D1 >= MinReach))
                r.Openings.RemoveAll(o => o.Edge == null && o.Side == w.Side && o.Kind == OpeningKind.Window
                                          && w.U(o.Center) + o.Width / 2 > x.U0 && w.U(o.Center) - o.Width / 2 < x.U1);
        }
        return shape.ToArray();
    }

    /// <summary>
    /// Whether opening <paramref name="o"/> on wall <paramref name="w"/> is a street door's doorway whose real
    /// door stands out in front of the wall, facing its way and near enough for the room to grow to it: the
    /// wall then grows over it, and the doorway moves out to the door.
    /// </summary>
    private static bool DoorAhead(InteriorLayout l, RoomPlan r, WallLine w, OpeningPlan o, bool alone)
    {
        if (o.Kind != OpeningKind.Entry) return false;
        var at = r.WallFrame(o).At;
        if (EntranceOf(l, at) is not { } e) return false;
        var (door, outward) = DoorInPlan(l, e);
        float ahead = (door - at).Dot(w.Out), aside = Math.Abs((door - at).Dot(w.Along));
        return outward.Dot(w.Out) > 0.5f && ahead > 0.1f && ahead <= (alone ? MaxReach : DeepAt) && aside <= o.Width / 2 + 1f;
    }

    /// <summary>The ground-floor entrance whose doorway is at <paramref name="at"/>, or null.</summary>
    private static EntrancePlan? EntranceOf(InteriorLayout l, Vector2 at) =>
        l.AllEntrances().FirstOrDefault(e => e.Level == 0 && Math.Abs(e.X - at.X) < 0.05f && Math.Abs(e.Z - at.Y) < 0.05f);

    // ---- the doorways out to their doors -------------------------------------------------------

    /// <summary>
    /// Every ground-floor way in moved to where its real door stands on the facade: into the wall of
    /// the room whose shape meets the outline there, as near the door as the wall allows, facing the way
    /// the door faces. Stepping through the door from the street then lands where the facade has it,
    /// not on the box's side behind it. A doorway already at its door stays. Then every doorway the fit
    /// left off its room's walls, or still more than <see cref="FarDoor"/> from its door: another street
    /// door than the main one loses it (it reads as locked, #498) unless that shuts off rooms; else its
    /// room takes back its rectangle, whose wall still has it.
    /// </summary>
    /// <summary>The doors of their own (#1292) of the building being planned, by key: <see cref="MoveEntries"/> never locks them.</summary>
    [ThreadStatic] internal static HashSet<string>? UnitDoors;

    private static void MoveEntries(InteriorLayout l)
    {
        if (l.Entrances.Count == 0) l.Entrances.AddRange(l.AllEntrances());
        int gf = Math.Min(l.Below, l.Floors.Count - 1);
        var rooms = l.Floors[gf].Rooms;
        foreach (var e in l.Entrances)
        {
            if (e.Level != 0) continue;
            var (from, cut) = EntryCut(rooms, new Vector2(e.X, e.Z));
            // a garage door's doorway is where its ramp down is (#558): it stays
            if (from == null || cut == null || from.Type == RoomType.Ramp) continue;
            var (gap, turn) = DoorGap(l, e);
            if (gap < 0.3f && turn < 5f) continue;
            var (door, outward) = DoorInPlan(l, e);
            if (!FitDoorway(l, gf, from, cut, door, outward, out var to, out int edge, out float u, out var at, out var normal)) continue;
            from.Openings.Remove(cut);
            // the windows it lands on go
            to.Openings.RemoveAll(q => q.Kind == OpeningKind.Window && OnEdge(to, q, edge, u, cut.Width / 2 + 0.2f));
            var moved = new OpeningPlan { Width = cut.Width, Bottom = cut.Bottom, Top = Math.Min(cut.Top, l.ClearOf(to) - 0.15f), Kind = OpeningKind.Entry };
            var side = Facing(normal);
            if (to.Shape != null) (moved.Edge, moved.Center) = (edge, u);
            else (moved.Side, moved.Center) = (side, side is Side.Front or Side.Back ? at.X : at.Y);
            to.Openings.Add(moved);
            (e.X, e.Z, e.InX, e.InZ) = (at.X, at.Y, -normal.X, -normal.Y);
        }

        // a doorway the fit left off its room's walls, and another street door's still far from its door:
        // a side door on a courtyard or an arm no room reaches would open onto the far side of the building,
        // so it reads as locked (#498), unless it is the only way to some of it (a wing's stairwell)
        int? unreached = null;
        var locked = new HashSet<EntrancePlan>();
        for (int i = l.Entrances.Count - 1; i >= 0; i--)
        {
            var e = l.Entrances[i];
            if (e.Level != 0 || locked.Contains(e)) continue;
            var (r, cut) = EntryCut(rooms, new Vector2(e.X, e.Z));
            if (r?.Type == RoomType.Ramp) continue;
            bool off = r != null && cut != null && r.Shape != null && cut.Edge == null && !OnShape(r, cut);
            // the main door, and a shop's or a venue's own door (#1292): the only way into its unit, never locked
            bool keep = i == 0 || UnitDoors?.Contains(e.Door) == true;
            bool far = !keep && DoorGap(l, e).Gap > FarDoor;
            if (!off && !far) continue;
            if (!keep)
            {
                unreached ??= Unreached(l);
                r?.Openings.Remove(cut!);
                l.Entrances.Remove(e);
                if (Unreached(l) <= unreached) { locked.Add(e); continue; }
                if (cut != null) r!.Openings.Add(cut);
                l.Entrances.Insert(i, e);
            }
            // its room is its rectangle again, so the doorway is on its wall; a doorway moved onto the
            // shape's edges goes, and its door reads as locked
            if (!off) continue;
            foreach (var q in r!.Openings.Where(q => q.Edge != null).ToList())
            {
                if (q.Kind == OpeningKind.Entry && EntranceOf(l, r.WallFrame(q).At) is { } other) locked.Add(other);
                r.Openings.Remove(q);
            }
            r.Shape = null;
        }
        l.Entrances.RemoveAll(locked.Contains);
    }

    /// <summary>Why vestibules were or were not made (the survey prints it).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> VestibuleWhy = new();

    /// <summary>The plans a vestibule was made in (the survey draws a few).</summary>
    internal static readonly System.Collections.Concurrent.ConcurrentQueue<string> VestibuleKeys = new();

    /// <summary>How deep a vestibule may run in from its door to the first room (#1304).</summary>
    private const float MaxVestibule = 20f;

    /// <summary>
    /// A way in behind each street door that has no room behind it (#1304): a door on a stretch of the outline the
    /// rooms do not reach (the far arm of a civic block, the bit of a works' roof its hall pieces leave) reads as
    /// locked. A vestibule 1.8 m wide runs in square from it, a four-sided shape, to the wall of the first room it
    /// meets up to <see cref="MaxVestibule"/> deep, that wall at most 60° off the door's, and opens into the room
    /// through an arch: if the room is one a street door may open into (<see cref="FitDoorway"/>'s rule), the
    /// vestibule stays inside the outline and clear of every room, and the plan validates no worse.
    /// </summary>
    internal static void Vestibules(InteriorLayout l, Footprint fp, bool[,] grid)
    {
        if (l.Floors.Count == 0) return;
        var rings = RingsFor(l, grid);
        if (rings.Count == 0) return;
        if (l.Entrances.Count == 0) l.Entrances.AddRange(l.AllEntrances());
        int gf = Math.Min(l.Below, l.Floors.Count - 1);
        var rooms = l.Floors[gf].Rooms;
        bool flats = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
        var u = new Vector2(MathF.Cos(l.Yaw), -MathF.Sin(l.Yaw));
        var v = new Vector2(-u.Y, u.X);
        float clear = l.StoreyHeight - Slab, bw = l.Width / 2, bd = l.Depth / 2;
        const float hw = 0.9f;
        int? before = null;
        static void Why(string w) => VestibuleWhy[w.Length > 70 ? w[..70] : w] = VestibuleWhy.GetValueOrDefault(w.Length > 70 ? w[..70] : w) + 1;
        foreach (var d in fp.Doors)
        {
            if (d.Level != 0 || d.Width <= 0 || d.Vehicle) continue;
            string key = new DoorKey(fp.Key, d.Slot).ToString();
            if (l.Entrances.Any(e => e.Door == key)) continue;
            var rel = new Vector2(d.Position.X - l.CenterX, d.Position.Z - l.CenterZ);
            var at = new Vector2(rel.Dot(u), rel.Dot(v));
            var o = new Vector2(d.Outward.X, d.Outward.Z);
            var outward = new Vector2(o.Dot(u), o.Dot(v)).Normalized();
            var n = -outward;
            var t = new Vector2(-outward.Y, outward.X);
            if (rooms.Any(r => r.Holds(at.X + n.X * 0.6f, at.Y + n.Y * 0.6f)))
            {
                // a room right behind it, and no doorway yet: the door's own was never planned (the box's side had
                // no room for it), so it is fitted here as MoveEntries fits the others
                var cut = new OpeningPlan { Width = Math.Min(d.Width, 1.2f), Top = Math.Min(d.Height, clear - 0.15f), Kind = OpeningKind.Entry };
                if (!FitDoorway(l, gf, new RoomPlan(), cut, at, outward, out var room, out int edge, out float along, out var spot, out var normal)) { Why("a room behind, no doorway fits"); continue; }
                before ??= InteriorValidator.Validate(l).Count;
                var removed = room.Openings.Where(q => q.Kind == OpeningKind.Window && OnEdge(room, q, edge, along, cut.Width / 2 + 0.2f)).ToList();
                room.Openings.RemoveAll(removed.Contains);
                var moved = new OpeningPlan { Width = cut.Width, Top = Math.Min(cut.Top, l.ClearOf(room) - 0.15f), Kind = OpeningKind.Entry };
                var sd = Facing(normal);
                if (room.Shape != null) (moved.Edge, moved.Center) = (edge, along);
                else (moved.Side, moved.Center) = (sd, sd is Side.Front or Side.Back ? spot.X : spot.Y);
                room.Openings.Add(moved);
                var fitted = new EntrancePlan
                {
                    Door = key, X = spot.X, Z = spot.Y, InX = -normal.X, InZ = -normal.Y, Width = cut.Width,
                    DoorX = d.Position.X, DoorY = d.Position.Y, DoorZ = d.Position.Z, DoorOutX = d.Outward.X, DoorOutZ = d.Outward.Z,
                    DoorWidth = d.Width, DoorHeight = d.Height, Hang = d.Hang, Vehicle = false,
                };
                l.Entrances.Add(fitted);
                if (InteriorValidator.Validate(l).Count <= before) { Why("a room behind, doorway fitted"); continue; }
                Why("a room behind, invalid");
                l.Entrances.Remove(fitted);
                room.Openings.Remove(moved);
                room.Openings.AddRange(removed);
                continue;
            }
            // the first room straight in
            RoomPlan? to = null;
            float hitAt = 0;
            for (float k = 0.6f; k <= MaxVestibule && to == null; k += 0.25f)
            {
                to = rooms.FirstOrDefault(r => r.Holds(at.X + n.X * k, at.Y + n.Y * k));
                hitAt = k;
            }
            if (to == null) { Why("no room in reach"); continue; }
            if (to.Type is RoomType.Elevator or RoomType.Vault or RoomType.Shelter or RoomType.Ramp or RoomType.Stairwell) { Why("a stair or lift room"); continue; }
            if (flats ? StreetRank(to) is < 0 or > 1
                : to.Type is RoomType.Bedroom or RoomType.GuestRoom or RoomType.Bathroom or RoomType.WC or RoomType.ToiletStall or RoomType.Laundry) { Why("a private room"); continue; }
            // the wall of it the door's centre line crosses
            var c = to.Corners();
            int wall = -1;
            float best = float.MaxValue;
            for (int k = 0; k < c.Length; k++)
            {
                var a0 = c[k];
                var a1 = c[(k + 1) % c.Length];
                if (LineHit(at, n, a0, a1) is not { } hit || hit < 0 || hit > hitAt + 0.5f || hit >= best) continue;
                var p = at + n * hit;
                var e = a1 - a0;
                float along0 = (p - a0).Dot(e) / e.LengthSquared();
                if (along0 < -0.001f || along0 > 1.001f) continue;
                (best, wall) = (hit, k);
            }
            if (wall < 0) { Why("no wall crossed"); continue; }
            var wa = c[wall];
            var wb = c[(wall + 1) % c.Length];
            float wlen = wa.DistanceTo(wb);
            var wAlong = (wb - wa) / wlen;
            var wOut = new Vector2(wAlong.Y, -wAlong.X);
            if (wOut.Dot(outward) < 0.5f) { Why("the wall is turned away"); continue; }
            var fa = at - t * hw;
            var fb = at + t * hw;
            if (LineHit(fa, n, wa, wb) is not { } ha || LineHit(fb, n, wa, wb) is not { } hb || ha < 0.3f || hb < 0.3f) { Why("no wall crossed"); continue; }
            var ea = fa + n * ha;
            var eb = fb + n * hb;
            float sa = (ea - wa).Dot(wAlong), sb = (eb - wa).Dot(wAlong);
            if (Math.Min(sa, sb) < 0.2f || Math.Max(sa, sb) > wlen - 0.2f) { Why("beside the room"); continue; }
            var quad = new List<Vector2> { fa, fb, eb, ea };
            if (RoomShapes.Area(quad) < 0) quad.Reverse();
            if (quad.Any(q => q.X < -bw || q.X > bw || q.Y < -bd || q.Y > bd)) { Why("leaves the box"); continue; }
            // inside the outline and clear of the rooms, a step in from its edges
            bool ok = true;
            float x0 = quad.Min(q => q.X), x1 = quad.Max(q => q.X), z0 = quad.Min(q => q.Y), z1 = quad.Max(q => q.Y);
            var arr = quad.ToArray();
            for (float x = x0 + 0.1f; x < x1 && ok; x += 0.25f)
                for (float z = z0 + 0.1f; z < z1 && ok; z += 0.25f)
                {
                    var q = new Vector2(x, z);
                    if (!RoomShapes.Contains(arr, q) || RoomShapes.EdgeDistance(arr, q) < 0.1f) continue;
                    if (!RoomShapes.Contains(rings, q)) { Why("leaves the outline"); ok = false; }
                    else if (rooms.Any(r => r.Holds(x, z))) { Why("runs through a room"); ok = false; }
                }
            if (!ok) continue;
            // its core: a metre square round its middle, inside it and clear of the others' cores
            var mid = (fa + fb + ea + eb) / 4;
            var core = new RectPlan(mid.X - 0.5f, mid.Y - 0.5f, mid.X + 0.5f, mid.Y + 0.5f);
            if (new[] { new Vector2(core.X0, core.Z0), new Vector2(core.X1, core.Z0), new Vector2(core.X1, core.Z1), new Vector2(core.X0, core.Z1) }.Any(q => !RoomShapes.Contains(arr, q))
                || rooms.Any(r => new RectPlan(r.X0, r.Z0, r.X1, r.Z1).Overlaps(core, 0.02f))) { Why("too short for its core"); continue; }
            float width = Math.Min(d.Width, 1.2f);
            float atWall = ((ea + eb) / 2 - wa).Dot(wAlong);
            var into = new OpeningPlan { Width = width, Top = Math.Min(2.2f, clear - 0.2f), Kind = OpeningKind.Arch, Other = rooms.Count };
            if (to.Shape != null) (into.Edge, into.Center) = (wall, atWall);
            else
            {
                into.Side = wOut.Y < -0.5f ? Side.Front : wOut.Y > 0.5f ? Side.Back : wOut.X < 0 ? Side.Left : Side.Right;
                var pm = (ea + eb) / 2;
                into.Center = into.Side is Side.Front or Side.Back ? pm.X : pm.Y;
            }
            var probe = to.WallFrame(into).At;
            if (to.Openings.Any(q => q.Kind != OpeningKind.Window && to.WallFrame(q).At.DistanceTo(probe) < (q.Width + width) / 2 + 0.3f)) { Why("another opening"); continue; }
            before ??= InteriorValidator.Validate(l).Count;
            var hall = new RoomPlan { X0 = core.X0, Z0 = core.Z0, X1 = core.X1, Z1 = core.Z1, Type = RoomType.Corridor, Shape = quad.SelectMany(q => new[] { q.X, q.Y }).ToArray() };
            var cs = hall.Corners();
            int Edge(Vector2 p0, Vector2 p1)
            {
                for (int k = 0; k < cs.Length; k++)
                    if (cs[k].DistanceTo(p0) < 0.01f && cs[(k + 1) % cs.Length].DistanceTo(p1) < 0.01f
                        || cs[k].DistanceTo(p1) < 0.01f && cs[(k + 1) % cs.Length].DistanceTo(p0) < 0.01f) return k;
                return -1;
            }
            int front = Edge(fa, fb), back = Edge(ea, eb);
            if (front < 0 || back < 0) { Why("no edges"); continue; }
            hall.Openings.Add(new OpeningPlan { Edge = front, Center = hw, Width = width, Top = Math.Min(d.Height, clear - 0.15f), Kind = OpeningKind.Entry });
            hall.Openings.Add(new OpeningPlan { Edge = back, Center = (eb - ea).Length() / 2, Width = width, Top = into.Top, Kind = OpeningKind.Arch, Other = rooms.IndexOf(to) });
            var windows = to.Openings.Where(q => q.Kind == OpeningKind.Window && to.WallFrame(q).At.DistanceTo(probe) < (q.Width + width) / 2 + 0.2f).ToList();
            to.Openings.RemoveAll(windows.Contains);
            to.Openings.Add(into);
            rooms.Add(hall);
            var entrance = new EntrancePlan
            {
                Door = key, X = hall.WallFrame(hall.Openings[0]).At.X, Z = hall.WallFrame(hall.Openings[0]).At.Y, InX = n.X, InZ = n.Y, Width = width,
                DoorX = d.Position.X, DoorY = d.Position.Y, DoorZ = d.Position.Z, DoorOutX = d.Outward.X, DoorOutZ = d.Outward.Z,
                DoorWidth = d.Width, DoorHeight = d.Height, Hang = d.Hang, Vehicle = false,
            };
            l.Entrances.Add(entrance);
            var after = InteriorValidator.Validate(l);
            if (after.Count <= before) { Why("made"); VestibuleKeys.Enqueue(l.Key); continue; }
            Why("invalid: " + System.Text.RegularExpressions.Regex.Replace(after.Last(), @"[\d.]+", "#"));
            // it does not hold together: as it was
            l.Entrances.Remove(entrance);
            rooms.RemoveAt(rooms.Count - 1);
            to.Openings.Remove(into);
            to.Openings.AddRange(windows);
        }
    }

    /// <summary>How many rooms of a plan no one can reach, as the validator counts them.</summary>
    private static int Unreached(InteriorLayout l) => InteriorValidator.Unreachable(l).Count;

    /// <summary>The ground-floor room and its street doorway at <paramref name="at"/>, or nulls.</summary>
    private static (RoomPlan? Room, OpeningPlan? Cut) EntryCut(List<RoomPlan> rooms, Vector2 at)
    {
        foreach (var r in rooms)
            foreach (var o in r.Openings)
                if (o.Kind == OpeningKind.Entry && r.WallFrame(o).At.DistanceTo(at) < 0.05f) return (r, o);
        return (null, null);
    }

    /// <summary>Whether an opening on a shaped room's rectangle's wall line stands on its shape's edges.</summary>
    private static bool OnShape(RoomPlan r, OpeningPlan o)
    {
        var (at, _, along) = r.WallFrame(o);
        var c = r.Corners();
        return RoomShapes.EdgeDistance(c, at - along * (o.Width / 2)) < 0.03f && RoomShapes.EdgeDistance(c, at + along * (o.Width / 2)) < 0.03f;
    }

    /// <summary>Whether opening <paramref name="q"/> stands on edge <paramref name="edge"/> of <paramref name="r"/>, within <paramref name="half"/> of u along it.</summary>
    private static bool OnEdge(RoomPlan r, OpeningPlan q, int edge, float u, float half)
    {
        var c = r.Corners();
        var a = c[edge % c.Length];
        var b = c[(edge + 1) % c.Length];
        var along = (b - a).Normalized();
        var at = r.WallFrame(q).At;
        if (Math.Abs((at - a).Dot(new Vector2(along.Y, -along.X))) > 0.03f) return false;
        return Math.Abs((at - a).Dot(along) - u) < half + q.Width / 2;
    }

    /// <summary>
    /// Where a doorway as wide as <paramref name="cut"/> fits for the door at <paramref name="door"/> facing
    /// <paramref name="outward"/>: on a facade edge (no room outside it) of a ground-floor room, facing
    /// within 40° of the door, within 0.6 m of it and slid along at most 0.8 m, clear of the other ways
    /// through that wall and of a stair, a hole, a landing or a lift just inside. Never into a room no one
    /// walks in from the street (a bedroom, a bathroom: an arm's rooms are often those; in a block of flats
    /// anything but the stairs' lobby, a corridor or a shop) but the one it was in, which wins a tie.
    /// </summary>
    private static bool FitDoorway(InteriorLayout l, int f, RoomPlan from, OpeningPlan cut, Vector2 door, Vector2 outward,
        out RoomPlan to, out int edge, out float u, out Vector2 at, out Vector2 normal)
    {
        (to, edge, u, at, normal) = (null!, -1, 0, default, default);
        var floor = l.Floors[f];
        float w = cut.Width, best = float.MaxValue;
        // in a block of flats a street door opens onto the stairs or a shop, never into a flat
        bool flats = l.Type is BuildingType.Apartments or BuildingType.MixedUse;
        foreach (var r in floor.Rooms)
        {
            if (r.Type is RoomType.Elevator or RoomType.Vault or RoomType.Shelter) continue;
            if (r != from && (flats ? StreetRank(r) is < 0 or > 1
                    : r.Type is RoomType.Bedroom or RoomType.GuestRoom or RoomType.Bathroom or RoomType.WC or RoomType.ToiletStall or RoomType.Laundry)) continue;
            var c = r.Corners();
            for (int k = 0; k < c.Length; k++)
            {
                var a = c[k];
                var b = c[(k + 1) % c.Length];
                float len = a.DistanceTo(b);
                if (len < w + 0.5f) continue;
                var along = (b - a) / len;
                var n = new Vector2(along.Y, -along.X);
                if (n.Dot(outward) < 0.76f) continue;
                float off = Math.Abs((door - a).Dot(n));
                float t = (door - a).Dot(along), tc = Math.Clamp(t, w / 2 + 0.25f, len - w / 2 - 0.25f);
                if (off > 0.6f || Math.Abs(t - tc) > 0.8f) continue;
                float score = off + Math.Abs(t - tc) + (r == from ? 0 : 0.3f);
                if (score >= best) continue;
                var p = a + along * tc;
                // a facade: no room outside it
                var probe = p + n * 0.2f;
                if (floor.Rooms.Any(q => q != r && q.Holds(probe.X, probe.Y))) continue;
                if (r.Openings.Any(q => q != cut && q.Kind != OpeningKind.Window && OnEdge(r, q, k, tc, w / 2 + 0.3f))) continue;
                var inner = new[] { p - along * (w / 2), p + along * (w / 2), p - along * (w / 2) - n * 1.2f, p + along * (w / 2) - n * 1.2f };
                var box = new RectPlan(inner.Min(q => q.X), inner.Min(q => q.Y), inner.Max(q => q.X), inner.Max(q => q.Y));
                if (floor.AllFlights().Any(fl => fl.Area().Overlaps(box)) || floor.Holes.Any(h => h.Overlaps(box))
                    || floor.Landings.Any(g => box.Overlaps(new RectPlan(g.X0, g.Z0, g.X1, g.Z1)))
                    || l.Lifts.Any(lift => box.Overlaps(new RectPlan(lift.X0, lift.Z0, lift.X1, lift.Z1)))) continue;
                (best, to, edge, u, at, normal) = (score, r, k, tc, p, n);
            }
        }
        return to != null;
    }

    /// <summary>Distance along a ray to the line through a and b (not only the segment), or null if parallel.</summary>
    private static float? LineHit(Vector2 o, Vector2 d, Vector2 a, Vector2 b)
    {
        var e = b - a;
        float den = d.X * e.Y - d.Y * e.X;
        if (Math.Abs(den) < 1e-9f) return null;
        var w = a - o;
        return (w.X * e.Y - w.Y * e.X) / den;
    }

    /// <summary>The stretches of a wall that grow, each as its run of reaches.</summary>
    private static IEnumerable<List<Reach>> Runs(List<Reach> list)
    {
        var run = new List<Reach>();
        foreach (var x in list)
        {
            if (x.D0 >= MinReach || x.D1 >= MinReach) { run.Add(x); continue; }
            if (run.Count > 0) yield return run;
            run = new List<Reach>();
        }
        if (run.Count > 0) yield return run;
    }

    /// <summary>
    /// The outline's corners from where one wall's growth met it to where the next one's did, if
    /// they all lie in the corner beyond the room and in no other room: the corner of the room
    /// follows the building round. Null when it would not.
    /// </summary>
    private static List<Vector2>? Round(Vector2[] ring, int from, int to, Vector2 corner, Vector2 out0, Vector2 out1, List<Vector2[]> others)
    {
        var o = new List<Vector2>();
        // one facade edge cutting the corner: straight across, nothing between
        if (from == to) return o;
        int n = ring.Length;
        // the vertices after the edge one growth met, up to the start of the edge the other met
        for (int k = (from + 1) % n, guard = 0; guard < 8; k = (k + 1) % n, guard++)
        {
            var v = ring[k];
            if ((v - corner).Dot(out0) < -0.02f || (v - corner).Dot(out1) < -0.02f) return null;
            if (others.Any(q => RoomShapes.Contains(q, v))) return null;
            o.Add(v);
            if (k == to) return o;
        }
        return null;
    }

    /// <summary>
    /// Rooms a deep growth gets rooms of its own off: in a block of flats the ones others open off
    /// (#576), elsewhere any but a wet room or one shut by its use.
    /// </summary>
    private static bool Splits(InteriorLayout l, RoomPlan r) => l.Type is BuildingType.Apartments or BuildingType.MixedUse
        ? r.Type is RoomType.Hall or RoomType.Living or RoomType.Kitchen or RoomType.Dining or RoomType.Corridor
        : r.Type is not (RoomType.Bathroom or RoomType.WC or RoomType.Vault or RoomType.Shelter or RoomType.Elevator
            or RoomType.Stairwell or RoomType.Ramp or RoomType.TechRoom);

    /// <summary>A room that is a hall: it goes on round a corner as itself, not as rooms off it.</summary>
    private static bool HallLike(RoomType t) => t is RoomType.Barn or RoomType.Garage or RoomType.WarehouseHall
        or RoomType.ProductionHall or RoomType.IkeaMarket or RoomType.Nave or RoomType.TruckBay or RoomType.Workshop;

    /// <summary>The biggest rectangle of a run's growth that stands on the wall: its stretch and depth.</summary>
    private static (float U0, float U1, float D) RunCore(List<Reach> run)
    {
        var us = run.Select(x => x.U0).Append(run[^1].U1).ToList();
        float best = 0, bu0 = run[0].U0, bu1 = run[^1].U1, bd = 0;
        for (int i = 0; i < us.Count; i++)
            for (int j = i + 1; j < us.Count; j++)
            {
                float d = run.Where(x => x.U1 > us[i] + 1e-4f && x.U0 < us[j] - 1e-4f).Min(x => Math.Min(x.D0, x.D1));
                if ((us[j] - us[i]) * d > best) { best = (us[j] - us[i]) * d; bu0 = us[i]; bu1 = us[j]; bd = d; }
            }
        return (bu0, bu1, bd);
    }

    /// <summary>The side of a rectangle that faces the way <paramref name="v"/> points.</summary>
    private static Side Facing(Vector2 v) =>
        Math.Abs(v.X) > Math.Abs(v.Y) ? (v.X > 0 ? Side.Right : Side.Left) : (v.Y > 0 ? Side.Back : Side.Front);

    /// <summary>
    /// Rooms for the arm of an L (or any deep stretch of building) off room <paramref name="index"/>'s
    /// wall <paramref name="w"/>, filling the rectangle u0..u1 along it and <paramref name="depth"/>
    /// out: a shallow one side by side, each with a door into the room; a deep one a corridor
    /// straight out with rooms either side of it, every room a door onto it. Bedrooms, studies and
    /// box rooms in a home, offices in an office, storage under ground. False if not one door fits.
    /// </summary>
    private static bool Arm(InteriorLayout l, int f, int index, WallLine w, float u0, float u1, float depth, HashSet<RoomPlan> fresh)
    {
        var floor = l.Floors[f];
        var r = floor.Rooms[index];
        float top = Math.Min(2.1f, l.ClearOf(r) - 0.1f);
        const float doorW = 0.9f, corridor = 1.3f, room = 4.5f, least = 2.6f;
        var toWall = Facing(-w.Out);
        var along = Facing(w.Along);
        var back = Facing(-w.Along);
        RoomType TypeOf(float area) =>
            f < l.Below ? RoomType.Storage
            : r.Type is RoomType.Office ? RoomType.Office
            : area >= 9f ? RoomType.Bedroom : area >= 5f ? RoomType.Study : RoomType.Storage;
        int Add(float ua, float da, float ub, float db, RoomType type)
        {
            var a = w.At(ua, da);
            var b = w.At(ub, db);
            var q = new RoomPlan
            {
                X0 = Math.Min(a.X, b.X), Z0 = Math.Min(a.Y, b.Y), X1 = Math.Max(a.X, b.X), Z1 = Math.Max(a.Y, b.Y),
                Type = type, Unit = r.Unit, Clear = r.Clear,
            };
            floor.Rooms.Add(q);
            fresh.Add(q);
            return floor.Rooms.Count - 1;
        }
        static float Coord(Side s, Vector2 p) => s is Side.Front or Side.Back ? p.X : p.Y;
        void Door(int a, Side sa, int b, Vector2 at, OpeningKind kind = OpeningKind.Door)
        {
            var sb = (Side)(((int)sa + 2) % 4);
            floor.Rooms[a].Openings.Add(new OpeningPlan { Side = sa, Center = Coord(sa, at), Width = doorW, Top = top, Kind = kind, Other = b });
            floor.Rooms[b].Openings.Add(new OpeningPlan { Side = sb, Center = Coord(sb, at), Width = doorW, Top = top, Kind = kind, Other = a });
        }
        // a door in this room's wall at u, clear of the cuts already in it, and of a stair or a shaft
        // inside it (a stair hall's wall)
        bool Free(float u)
        {
            if (u - doorW / 2 < u0 + 0.3f || u + doorW / 2 > u1 - 0.3f) return false;
            if (r.Openings.Any(o => o.Edge == null && o.Side == w.Side && o.Kind != OpeningKind.Window
                                    && Math.Abs(w.U(o.Center) - u) < (o.Width + doorW) / 2 + 0.3f)) return false;
            var a = w.At(u - doorW / 2, -1.2f);
            var b = w.At(u + doorW / 2, 0);
            var inside = new RectPlan(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
            return !floor.AllFlights().Any(fl => fl.Area().Overlaps(inside)) && !floor.Holes.Any(h => h.Overlaps(inside))
                   && !floor.Landings.Any(g => inside.Overlaps(new RectPlan(g.X0, g.Z0, g.X1, g.Z1)));
        }
        float? Near(float want, float lo, float hi)
        {
            for (float step = 0; step <= (hi - lo) / 2; step += 0.1f)
                foreach (float u in new[] { want - step, want + step })
                    if (u - doorW / 2 >= lo + 0.2f && u + doorW / 2 <= hi - 0.2f && Free(u)) return u;
            return null;
        }

        float width = u1 - u0;
        int made = floor.Rooms.Count;
        if (HallLike(r.Type))
        {
            // a hall goes on into the next wing, through an opening as wide as the wing allows
            float aw = Math.Min(width - 0.6f, 4f);
            if (aw < doorW) return false;
            int q = Add(u0, 0, u1, depth, r.Type);
            var c = w.At((u0 + u1) / 2);
            var sb = (Side)(((int)toWall + 2) % 4);
            floor.Rooms[q].Openings.Add(new OpeningPlan { Side = toWall, Center = Coord(toWall, c), Width = aw, Top = top, Kind = OpeningKind.Arch, Other = index });
            r.Openings.Add(new OpeningPlan { Side = sb, Center = Coord(sb, c), Width = aw, Top = top, Kind = OpeningKind.Arch, Other = q });
        }
        else if (depth < 1.5f * room || width < least + corridor)
        {
            // shallow, or too narrow for a corridor: rooms side by side, each off this room
            int n = Math.Max(1, (int)MathF.Round(width / room));
            while (n > 1 && width / n < least) n--;
            for (int k = 0; k < n; k++)
            {
                float a = u0 + width * k / n, b = u0 + width * (k + 1) / n;
                if (Near((a + b) / 2, a, b) is not { } du) continue;
                int q = Add(a, 0, b, depth, TypeOf((b - a) * depth));
                Door(q, toWall, index, w.At(du));
            }
        }
        else
        {
            // a corridor straight out, in the middle if rooms fit both sides of it, else at one side
            bool both = width >= 2 * least + corridor;
            float c0 = both ? u0 + (width - corridor) / 2 : u0, c1 = c0 + corridor;
            if (Near((c0 + c1) / 2, c0 - 0.2f, c1 + 0.2f) is not { } cu) return false;
            int hall = Add(c0, 0, c1, depth, r.Type is RoomType.Office ? RoomType.Corridor : RoomType.Hall);
            Door(hall, toWall, index, w.At(cu), OpeningKind.Arch);
            int m = Math.Max(1, (int)MathF.Round(depth / room));
            foreach (var (a, b, side) in both
                         ? new[] { (u0, c0, along), (c1, u1, back) }
                         : new[] { (c1, u1, back) })
                for (int k = 0; k < m; k++)
                {
                    float da = depth * k / m, db = depth * (k + 1) / m;
                    int q = Add(a, da, b, db, TypeOf((b - a) * (db - da)));
                    // its door onto the corridor, half way along it
                    var at = w.At(side == along ? b : a, (da + db) / 2);
                    Door(q, side, hall, at);
                }
        }
        if (floor.Rooms.Count == made) return false;
        // this room's wall there is inside now: its windows go
        r.Openings.RemoveAll(o => o.Kind == OpeningKind.Window && o.Edge == null && o.Side == w.Side
                                  && w.U(o.Center) + o.Width / 2 > u0 && w.U(o.Center) - o.Width / 2 < u1);
        return true;
    }

    // ---- clip --------------------------------------------------------------------------------

    /// <summary>
    /// Every room the outline cuts takes the cut, room by room: one that cannot (a stair in it, a
    /// courtyard in it, cut in two, a doorway left off its walls) keeps its rectangle, and the rest
    /// are still cut. A room out in the yard goes when <paramref name="drop"/> (and nothing is reached
    /// only through it, which the validator tells). False, with the first reason, if nothing changed.
    /// </summary>
    private static bool Clip(InteriorLayout l, List<Vector2[]> rings, bool drop = true)
    {
        var outers = rings.Where(r => RoomShapes.Area(r) > 0).ToList();
        var holes = rings.Where(r => RoomShapes.Area(r) < 0).ToList();
        string? why = null;
        bool changed = false;
        bool Keep(string reason)
        {
            why ??= reason;
            return false;
        }
        for (int f = 0; f < l.Floors.Count; f++)
        {
            var gone = new List<int>();
            for (int i = 0; i < l.Floors[f].Rooms.Count; i++)
            {
                var r = l.Floors[f].Rooms[i];
                if (RectInside(rings, r.X0, r.Z0, r.X1, r.Z1, 0.01f)) continue;
                if (!Shapeable(l, f, r))
                {
                    // it keeps a rectangle, but a smaller one inside the building if its stair still fits
                    if (Shrink(l, f, r, rings, out bool inside)) changed = true;
                    if (!inside) Keep($"floor {f} {r.Type} holds a stair or a shaft and leaves the outline");
                    continue;
                }
                // a courtyard in the room: cutting it out would need a hole in the floor
                if (holes.Any(h => h.Any(v => v.X > r.X0 && v.X < r.X1 && v.Y > r.Z0 && v.Y < r.Z1))) { Keep($"floor {f} {r.Type}: a courtyard inside it"); continue; }
                var pieces = new List<List<Vector2>>();
                bool split = false;
                foreach (var o in outers)
                {
                    var p = RoomShapes.ClipToRect(o, r.X0, r.Z0, r.X1, r.Z1, out bool twice);
                    if (p.Count < 3 || RoomShapes.Area(p) < 0.25f) continue;
                    split |= twice;
                    pieces.Add(p);
                }
                if (split) { Keep($"floor {f} {r.Type}: the outline meets it twice"); continue; }
                // a room out in the yard goes; one cut in two would need two rooms
                if (pieces.Count == 0 || pieces.Count == 1 && RoomShapes.Area(pieces[0]) < 1.5f)
                {
                    if (drop) gone.Add(i);
                    continue;
                }
                if (pieces.Count != 1) { Keep($"floor {f} {r.Type}: cut in {pieces.Count}"); continue; }
                var shape = pieces[0];
                // every cut in its walls still on a wall: a window that is not goes, a street doorway moves
                // out to its door after (MoveEntries), anything else keeps the rectangle
                var off = new List<OpeningPlan>();
                OpeningPlan? bad = null;
                foreach (var o in r.Openings)
                {
                    if (o.Edge != null || o.Kind == OpeningKind.Entry) continue;
                    var (at, _, along) = r.WallFrame(o);
                    if (RoomShapes.EdgeDistance(shape, at - along * (o.Width / 2)) < 0.03f
                        && RoomShapes.EdgeDistance(shape, at + along * (o.Width / 2)) < 0.03f) continue;
                    if (o.Kind != OpeningKind.Window) { bad = o; break; }
                    off.Add(o);
                }
                if (bad != null) { Keep($"floor {f} {r.Type}: a {bad.Kind} off the outline"); continue; }
                foreach (var o in off) r.Openings.Remove(o);
                r.SetShape(shape);
                changed = true;
            }
            for (int k = gone.Count - 1; k >= 0; k--)
                if (Drop(l, f, gone[k])) changed = true;
                else Keep($"floor {f}: {l.Floors[f].Rooms[gone[k]].Type} out in the yard is a way in");
        }
        if (!changed) return Unfit(why ?? "nothing to cut");
        if (why != null) OutlineFailure = $"partly: {why}";
        MoveEntries(l);
        EdgeWindows(l, rings);
        return true;
    }

    /// <summary>
    /// A room with a stair or a shaft (which keeps a rectangle: its floor or ceiling has a hole) that
    /// leaves the outline, made a smaller rectangle inside it: each of its walls standing out of the
    /// building steps in, 0.25 m at a time, as long as what it holds stays in it (its flights, the holes
    /// in its floor and ceiling, landings, a lift), the doorways in its other walls stay on them, and no
    /// wall with a door to another room moves. A street doorway moves with its wall, and its entrance;
    /// the windows of a wall that moved go. As far in as it gets: <paramref name="inside"/> says whether
    /// it is all inside now (a stair against a wall out of the building holds that wall). False, the
    /// room untouched, if no wall could move.
    /// </summary>
    private static bool Shrink(InteriorLayout l, int f, RoomPlan r, List<Vector2[]> rings, out bool inside)
    {
        inside = false;
        const float step = 0.25f;
        var floor = l.Floors[f];
        var rect = new RectPlan(r.X0, r.Z0, r.X1, r.Z1);
        // what must stay inside it
        var keep = new List<RectPlan>();
        keep.AddRange(floor.AllFlights().Select(fl => fl.Area()).Where(a => a.Overlaps(rect)));
        keep.AddRange(floor.Holes.Where(h => h.Overlaps(rect)));
        if (f + r.Span < l.Floors.Count) keep.AddRange(l.Floors[f + r.Span].Holes.Where(h => h.Overlaps(rect)));
        keep.AddRange(floor.Landings.Select(g => new RectPlan(g.X0, g.Z0, g.X1, g.Z1)).Where(g => g.Overlaps(rect)));
        keep.AddRange(l.Lifts.Select(q => new RectPlan(q.X0, q.Z0, q.X1, q.Z1)).Where(q => q.Overlaps(rect)));
        float kx0 = keep.Count > 0 ? keep.Min(k => k.X0) : r.X1, kx1 = keep.Count > 0 ? keep.Max(k => k.X1) : r.X0;
        float kz0 = keep.Count > 0 ? keep.Min(k => k.Z0) : r.Z1, kz1 = keep.Count > 0 ? keep.Max(k => k.Z1) : r.Z0;
        // a wall with a door to another room stays where it is
        bool Fixed(Side side) => r.Openings.Any(o => o.Edge == null && o.Side == side && o.Kind is OpeningKind.Door or OpeningKind.Arch && o.Other >= 0)
            || l.InnerDoors.Any(d => d.Floor == f && floor.Rooms.IndexOf(r) == d.Room && d.Side == side);
        float x0 = r.X0, z0 = r.Z0, x1 = r.X1, z1 = r.Z1;
        bool Out(Vector2 a, Vector2 b)
        {
            for (int i = 0; i <= 4; i++)
                if (!RoomShapes.Contains(rings, a + (b - a) * (0.05f + 0.9f * i / 4))) return true;
            return false;
        }
        for (int guard = 0; guard < 200 && !RectInside(rings, x0, z0, x1, z1, 0.01f); guard++)
        {
            bool moved = false;
            const float e = 0.03f;
            if (Out(new(x0 + e, z0 + e), new(x1 - e, z0 + e)) && !Fixed(Side.Front) && z0 + step <= Math.Min(kz0, z1 - 1.5f)) { z0 += step; moved = true; }
            if (Out(new(x0 + e, z1 - e), new(x1 - e, z1 - e)) && !Fixed(Side.Back) && z1 - step >= Math.Max(kz1, z0 + 1.5f)) { z1 -= step; moved = true; }
            if (Out(new(x0 + e, z0 + e), new(x0 + e, z1 - e)) && !Fixed(Side.Left) && x0 + step <= Math.Min(kx0, x1 - 1.5f)) { x0 += step; moved = true; }
            if (Out(new(x1 - e, z0 + e), new(x1 - e, z1 - e)) && !Fixed(Side.Right) && x1 - step >= Math.Max(kx1, x0 + 1.5f)) { x1 -= step; moved = true; }
            if (!moved) break;
        }
        if (x0 == r.X0 && z0 == r.Z0 && x1 == r.X1 && z1 == r.Z1) return false;
        if (l.Entrances.Count == 0) l.Entrances.AddRange(l.AllEntrances());
        // every doorway still on its wall's stretch
        foreach (var o in r.Openings)
        {
            if (o.Edge != null || o.Kind == OpeningKind.Window) continue;
            float lo = o.Side is Side.Front or Side.Back ? x0 : z0, hi = o.Side is Side.Front or Side.Back ? x1 : z1;
            if (o.Center - o.Width / 2 < lo + 0.1f || o.Center + o.Width / 2 > hi - 0.1f) return false;
        }
        var moves = new[] { (Side.Front, z0 != r.Z0), (Side.Back, z1 != r.Z1), (Side.Left, x0 != r.X0), (Side.Right, x1 != r.X1) };
        foreach (var (side, moved) in moves)
        {
            if (!moved) continue;
            r.Openings.RemoveAll(o => o.Edge == null && o.Side == side && o.Kind == OpeningKind.Window);
            // a street doorway goes with its wall
            foreach (var o in r.Openings.Where(o => o.Edge == null && o.Side == side && o.Kind == OpeningKind.Entry))
                if (EntranceOf(l, r.WallFrame(o).At) is { } e)
                    (e.X, e.Z) = side switch { Side.Front => (e.X, z0), Side.Back => (e.X, z1), Side.Left => (x0, e.Z), _ => (x1, e.Z) };
        }
        // windows on the walls that stayed, but now past their ends
        r.Openings.RemoveAll(o => o.Edge == null && o.Kind == OpeningKind.Window
            && (o.Side is Side.Front or Side.Back ? o.Center - o.Width / 2 < x0 || o.Center + o.Width / 2 > x1 : o.Center - o.Width / 2 < z0 || o.Center + o.Width / 2 > z1));
        (r.X0, r.Z0, r.X1, r.Z1) = (x0, z0, x1, z1);
        inside = RectInside(rings, x0, z0, x1, z1, 0.01f);
        return true;
    }

    /// <summary>
    /// A room taken out of its floor: the doorways into it walled up, the later rooms' indices one
    /// down. False for a room that is a way in, or holds a flat's front door.
    /// </summary>
    private static bool Drop(InteriorLayout l, int f, int k)
    {
        var rooms = l.Floors[f].Rooms;
        if (rooms[k].Openings.Any(o => o.Kind == OpeningKind.Entry) || l.InnerDoors.Any(d => d.Floor == f && d.Room == k)) return false;
        rooms.RemoveAt(k);
        foreach (var r in rooms)
        {
            r.Openings.RemoveAll(o => o.Other == k && o.Kind is OpeningKind.Door or OpeningKind.Arch or OpeningKind.Window);
            foreach (var o in r.Openings) if (o.Other > k) o.Other--;
        }
        foreach (var d in l.InnerDoors) if (d.Floor == f && d.Room > k) d.Room--;
        return true;
    }

    // ---- windows on the new facades ------------------------------------------------------------

    /// <summary>
    /// Windows on every edge of a shaped room that is a facade and not on its rectangle's wall
    /// lines (those kept theirs), and on every facade of a room an arm made (<paramref name="fresh"/>,
    /// which the planner's windows never saw), spaced as <see cref="AddWindows"/> spaces them.
    /// Above ground only.
    /// </summary>
    private static void EdgeWindows(InteriorLayout l, List<Vector2[]> rings, HashSet<RoomPlan>? fresh = null)
    {
        float clear = l.StoreyHeight - Slab;
        for (int f = l.Below; f < l.Floors.Count; f++)
        {
            var rooms = l.Floors[f].Rooms;
            foreach (var r in rooms)
            {
                bool made = fresh?.Contains(r) == true;
                if (r.Shape == null && !made || r.Type is RoomType.Vault or RoomType.Shelter or RoomType.Elevator or RoomType.Ramp) continue;
                var c = r.Corners();
                for (int k = 0; k < c.Length; k++)
                {
                    var a = c[k];
                    var b = c[(k + 1) % c.Length];
                    float len = a.DistanceTo(b);
                    bool onRect = OnRectLine(r, a, b);
                    if (len < 1.5f || onRect && !made) continue;
                    var dir = (b - a) / len;
                    var outward = new Vector2(dir.Y, -dir.X);
                    var probe = (a + b) / 2 + outward * 0.15f;
                    if (RoomShapes.Contains(rings, probe) || rooms.Any(q => q != r && q.Holds(probe.X, probe.Y))) continue;
                    var (width, bottom, top, spacing) = WindowSize(r.Type, false, clear, len);
                    if (top - bottom < 0.4f || len < width + 0.8f) continue;
                    int count = Math.Max(1, (int)(len / spacing));
                    var side = Facing(outward);
                    for (int i = 0; i < count; i++)
                    {
                        var at = a + dir * (len * (i + 0.5f) / count);
                        // on the rectangle's own wall: addressed by its side, clear of the doors in it
                        if (onRect)
                        {
                            float coord = side is Side.Front or Side.Back ? at.X : at.Y;
                            if (Blocked(r, side, coord, width)) continue;
                            r.Openings.Add(new OpeningPlan { Side = side, Center = coord, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Window });
                        }
                        else if (!r.Openings.Any(q => q.Kind != OpeningKind.Window && OnEdge(r, q, k, len * (i + 0.5f) / count, width / 2 + 0.2f)))
                            r.Openings.Add(new OpeningPlan
                            {
                                Edge = k, Center = len * (i + 0.5f) / count, Width = width, Bottom = bottom, Top = top, Kind = OpeningKind.Window,
                            });
                    }
                }
            }
        }
    }

    /// <summary>Whether an edge lies on one of the rectangle's own wall lines.</summary>
    private static bool OnRectLine(RoomPlan r, Vector2 a, Vector2 b)
    {
        const float e = 0.02f;
        bool H(float z) => Math.Abs(a.Y - z) < e && Math.Abs(b.Y - z) < e && Math.Min(a.X, b.X) >= r.X0 - e && Math.Max(a.X, b.X) <= r.X1 + e;
        bool V(float x) => Math.Abs(a.X - x) < e && Math.Abs(b.X - x) < e && Math.Min(a.Y, b.Y) >= r.Z0 - e && Math.Max(a.Y, b.Y) <= r.Z1 + e;
        return H(r.Z0) || H(r.Z1) || V(r.X0) || V(r.X1);
    }

    // ---- small helpers for the rest of the generator --------------------------------------------

    /// <summary>The strip in front of an opening on a shaped room's edge, as a box round it.</summary>
    private static RectPlan Across(RoomPlan r, OpeningPlan o, float half, float deep)
    {
        var (at, outward, along) = r.WallFrame(o);
        var p = new[] { at - along * half, at + along * half, at - along * half - outward * deep, at + along * half - outward * deep };
        return new RectPlan(p.Min(q => q.X), p.Min(q => q.Y), p.Max(q => q.X), p.Max(q => q.Y));
    }

    /// <summary>Whether a piece's footprint stands inside a shaped room, <paramref name="inset"/> off its walls.</summary>
    private static bool InShape(RoomPlan r, RectPlan p, float inset)
    {
        var c = r.Corners();
        foreach (var q in new[] { new Vector2(p.X0, p.Z0), new Vector2(p.X1, p.Z0), new Vector2(p.X1, p.Z1), new Vector2(p.X0, p.Z1) })
            if (!RoomShapes.Contains(c, q) || RoomShapes.EdgeDistance(c, q) < inset - 1e-3f) return false;
        // an angled wall's corner poking in between the piece's corners
        foreach (var v in c)
            if (v.X > p.X0 + 1e-3f && v.X < p.X1 - 1e-3f && v.Y > p.Z0 + 1e-3f && v.Y < p.Z1 - 1e-3f) return false;
        return true;
    }
}
