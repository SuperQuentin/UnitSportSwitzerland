using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Apartment blocks (#557): the inside of a block of flats, and of a city block with shops under
/// its flats, instead of one big house round a single stair core.
///
/// <para>
/// Every street entrance on the front wall gets its own <b>stairwell</b>: a lobby on the ground
/// floor, a switchback stair (the same two-lane flight stack as the cored plan) and, from three
/// levels up, an <b>elevator</b> in a column beside it, its cabin facing the lobby. The stairwell
/// repeats on every floor, and the flats open off its landings. A long block is several
/// stairwells side by side, each serving only its own flats, as a real one with several house
/// numbers is.
/// </para>
///
/// <para>
/// Between and beside the stairwells the plan is cut into <b>regions</b>, each touching the
/// stairwell (or a corridor running from its back landing) along one wall, and each region into
/// flats along that wall. A flat is laid out from its front door: an entrance hall straight in,
/// bathroom, WC and bedrooms either side of it, the living room and the kitchen across the far
/// facade. Every residential floor is the same plan, as the plumbing of a real one stacks.
/// </para>
///
/// <para>
/// The <b>basement</b> is shared: a corridor joins every stairwell's back landing across the
/// whole building, the car park takes the back of a deep block, and laundries, storage
/// compartments, the shelter and the boiler room fill the rest.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Front landing (the lobby on the ground floor), in front of the first step.</summary>
    private const float FrontLanding = 2.0f;
    /// <summary>Back landing, behind the last step: also the width of a corridor running off it.</summary>
    private const float BackLanding = 1.6f;
    /// <summary>Width of the elevator column beside the stair.</summary>
    private const float LiftColumn = 2.0f;
    private const float CabinDepth = 1.8f;
    private const float LiftDoor = 0.9f;
    private const float FlatDoor = 0.9f;
    /// <summary>A flat is never narrower than this along the wall it opens off.</summary>
    private const float MinFlatSide = 3.4f;
    /// <summary>Wall a doorway needs, jambs included.</summary>
    private const float WayMin = 1.4f;
    /// <summary>A side wider than this is served by a corridor off the back landing, flats front and back of it.</summary>
    private const float CorridorSide = 13.5f;
    /// <summary>Depth behind the stairwell past which a corridor runs on to the back facade.</summary>
    private const float DeepBack = 12f;
    private const float HallWidth = 1.3f;
    /// <summary>Share of flats whose front door is locked: cracked with the dial (#557).</summary>
    public const double LockedShare = 0.4;
    /// <summary>Share of tall commercial blocks that are shops under flats.</summary>
    private const double MixedShare = 0.6;

    /// <summary>
    /// Whether a building is planned as an apartment block (#557): a block of flats, a big
    /// building of no particular kind (the old plans already treated those as flats), and some
    /// commercial blocks of three storeys or more, shops below and flats above. Not a bank.
    /// </summary>
    public static BuildingType ApartmentTypeFor(Footprint fp, BuildingKind kind, int storeys, bool bank)
    {
        if (bank) return BuildingType.None;
        return kind switch
        {
            BuildingKind.Apartment => BuildingType.Apartments,
            BuildingKind.Other when storeys > 3 || fp.Width * fp.Depth >= 200 => BuildingType.Apartments,
            BuildingKind.Commercial when storeys >= 3 && Core.Fnv.Unit(fp.Key + "|mixed") < MixedShare => BuildingType.MixedUse,
            _ => BuildingType.None,
        };
    }

    private enum AptFloor { Flats, Shops, Basement }

    /// <summary>A way into a region: a stretch of a circulation room's wall it may open a door in.</summary>
    private sealed record Way(int Room, float Lo, float Hi);

    /// <summary>
    /// A rectangle to fill, touching circulation on its <see cref="Side"/> wall (the region's own
    /// wall, seen from inside it) along <see cref="Ways"/>.
    /// </summary>
    private sealed record Region(RectPlan R, Side Side, List<Way> Ways);

    private sealed class Well
    {
        public float X0, X1, DoorX;
        /// <summary>Room indices, the same on every floor: wells are laid first, in order.</summary>
        public int Core = -1, Front = -1, Cabin = -1, Back = -1;
        /// <summary>The facade door it was built for: slot 0 is the main door.</summary>
        public int Slot;
    }

    private sealed class Apt
    {
        public InteriorLayout L = null!;
        public float Hw, Hd, RunZ0, RunZ1, ZB1, CoreW, Clear, Tread;
        public bool Lift, Stairs;
        public int Floors, Below;
        public List<Well> Wells = new();
        public bool Mixed;
        /// <summary>The flat size this building runs to, m²: one block is studios, the next family flats.</summary>
        public float Target;
    }

    internal static bool TryApartments(InteriorLayout l, Footprint fp, BuildingKind kind, int above, BuildingType type, Random rng)
    {
        float W = l.Width, D = l.Depth, h = l.StoreyHeight;
        float hw = W / 2, hd = D / 2, clear = h - Slab;
        bool mixed = type == BuildingType.MixedUse;

        // ---- the stair, which sets the stairwell's depth -----------------------------------
        int below = AptBasement(l.Key, mixed, above, W * D);
        int floors = above + below;
        bool stairs = floors > 1;
        int steps = (int)MathF.Ceiling(h / 0.18f);
        float tread = 0.27f;
        if (stairs && FrontLanding + tread * steps + BackLanding > D)
        {
            tread = (D - FrontLanding - BackLanding) / steps;
            if (tread < 0.21f)
            {
                if (below == 0) return false;
                // a basement is what made it a stair: try again without one
                below = 0;
                floors = above;
                stairs = floors > 1;
                if (stairs) return false;
            }
        }
        float run = stairs ? tread * steps : 0;
        bool lift = floors >= 3;
        float coreW = stairs ? 2 * LaneWidth + WalkWidth : 2.4f;
        float wellW = coreW + (lift ? LiftColumn : 0);
        if (W < wellW + MinFlatSide || D < 5f) return false;

        float sd = FrontLanding + run + BackLanding;
        // a strip behind the stairwell too thin to be a flat goes to the stairwell instead: the
        // lobby gets deeper
        if (D - sd < 5.0f) sd = D;
        float zB1 = -hd + sd, runZ1 = zB1 - BackLanding, runZ0 = runZ1 - run;

        var a = new Apt
        {
            L = l, Hw = hw, Hd = hd, RunZ0 = runZ0, RunZ1 = runZ1, ZB1 = zB1, CoreW = coreW, Clear = clear,
            Tread = tread, Lift = lift, Stairs = stairs, Floors = floors, Below = below, Mixed = mixed,
            Target = 55f + 60f * (float)new Random(StableHash(l.Key + "|flatsize")).NextDouble(),
        };

        // ---- one stairwell per front door, spaced so there are flats between ------------------
        var axisV = fp.AxisV;
        var cands = new List<(float X, int Slot)> { (fp.EntryX, 0) };
        foreach (var d in fp.Extra)
        {
            var rel = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
            var outward = new Godot.Vector2(d.Outward.X, d.Outward.Z);
            // only a door on the front wall leads straight into a stairwell
            if (outward.Dot(axisV) > -0.8f) continue;
            cands.Add((rel.Dot(fp.AxisU), d.Slot));
        }
        float spacing = wellW + (mixed ? 12f : 2 * MinFlatSide + 0.6f);
        var kept = new List<(float X, int Slot)> { cands[0] };
        foreach (var c in cands.Skip(1).OrderBy(c => Math.Abs(c.X - cands[0].X)))
            if (kept.All(k => Math.Abs(k.X - c.X) >= spacing)) kept.Add(c);
        foreach (var (x, slot) in kept.OrderBy(k => k.X))
        {
            float cx = Fit(x, -hw + wellW / 2, hw - wellW / 2);
            var w = new Well { X0 = cx - wellW / 2, X1 = cx + wellW / 2, DoorX = x, Slot = slot };
            if (a.Wells.Count > 0 && w.X0 < a.Wells[^1].X1 + MinFlatSide) continue;
            a.Wells.Add(w);
        }
        if (a.Wells.All(w => w.Slot != 0)) return false;
        // A sliver at either end too thin for a flat: slide the stairwell flush, as the cored plan
        // does with its core. Its lobby's doorway then stands a little off the facade door, which
        // the plan box already allows (inside and outside line up plausibly, not exactly). A small
        // block with one stairwell goes to the nearer end outright: one flat a floor, as a Swiss
        // three-family house is, beats two slivers either side of the stair.
        {
            var w0 = a.Wells[0];
            var wn = a.Wells[^1];
            float gl = w0.X0 + hw, gr = hw - wn.X1;
            bool small = a.Wells.Count == 1 && gl + gr < 2 * (MinFlatSide + 2.6f);
            if (gl > 0.01f && (gl < MinFlatSide || small && gl <= gr)) { w0.X0 -= gl; w0.X1 -= gl; }
            else if (gr > 0.01f && (gr < MinFlatSide || small)) { wn.X0 += gr; wn.X1 += gr; }
            gr = hw - wn.X1;
            if (a.Wells.Count > 1 && gr > 0.01f && gr < MinFlatSide) { wn.X0 += gr; wn.X1 += gr; }
        }

        l.Type = type;
        l.Below = below;
        l.Floors.Clear();
        l.Lifts.Clear();
        l.InnerDoors.Clear();
        for (int f = 0; f < floors; f++)
        {
            int level = f - below;
            var what = level < 0 ? AptFloor.Basement : level == 0 && mixed ? AptFloor.Shops : AptFloor.Flats;
            // every floor of flats from the same seed, so the plan stacks: the same flats one over the other
            var frng = new Random(StableHash(l.Key + (what == AptFloor.Flats ? "|flats" : "|floor" + f)));
            l.Floors.Add(AptFloorPlan(a, f, level, what, frng));
        }

        if (lift)
            foreach (var w in a.Wells)
            {
                var cab = l.Floors[0].Rooms[w.Cabin];
                l.Lifts.Add(new LiftPlan
                {
                    X0 = cab.X0, Z0 = cab.Z0, X1 = cab.X1, Z1 = cab.Z1, Bottom = 0, Top = floors - 1,
                    DoorSide = Side.Front, DoorCenter = (cab.X0 + cab.X1) / 2, DoorWidth = LiftDoor,
                    DoorTop = Math.Min(2.1f, clear - 0.15f),
                });
            }

        AptEntrances(a, fp);
        for (int f = below; f < floors; f++) AddWindows(l, l.Floors[f], f - below);
        return true;
    }

    /// <summary>
    /// Whether the block has a basement, from its own seed like <see cref="Cellars"/>: nearly every
    /// Swiss block of flats has one, and any of some size certainly does (the shelter, the law said).
    /// </summary>
    private static int AptBasement(string key, bool mixed, int above, float area)
    {
        double chance = above >= 4 || area >= 400 ? 1.0 : mixed ? 0.9 : 0.85;
        return new Random(StableHash(key + "|cellar")).NextDouble() < chance ? 1 : 0;
    }

    // ---- one floor ---------------------------------------------------------------------------

    private static FloorPlan AptFloorPlan(Apt a, int f, int level, AptFloor what, Random rng)
    {
        var floor = new FloorPlan();
        var rooms = floor.Rooms;
        float hd = a.Hd, top = Math.Min(2.05f, a.Clear - 0.2f);

        // ---- the stairwells, first, so their rooms have the same indices on every floor -------
        foreach (var w in a.Wells)
        {
            float c0 = w.X0, c1 = w.X0 + a.CoreW;
            var lobby = level == 0 ? RoomType.Lobby : RoomType.Landing;
            w.Core = Add(rooms, new RoomPlan { X0 = c0, Z0 = -hd, X1 = c1, Z1 = a.ZB1, Type = lobby });
            if (a.Lift)
            {
                float cab0 = a.RunZ1 - CabinDepth;
                w.Front = Add(rooms, new RoomPlan { X0 = c1, Z0 = -hd, X1 = w.X1, Z1 = cab0, Type = lobby });
                w.Cabin = Add(rooms, new RoomPlan { X0 = c1, Z0 = cab0, X1 = w.X1, Z1 = a.RunZ1, Type = RoomType.Elevator });
                w.Back = Add(rooms, new RoomPlan { X0 = c1, Z0 = a.RunZ1, X1 = w.X1, Z1 = a.ZB1, Type = RoomType.Landing });
                Opening(rooms, w.Core, Side.Right, w.Front, (-hd + cab0) / 2, cab0 + hd - 0.4f, Math.Min(2.3f, a.Clear - 0.2f), OpeningKind.Arch);
                Opening(rooms, w.Core, Side.Right, w.Back, (a.RunZ1 + a.ZB1) / 2, BackLanding - 0.3f, Math.Min(2.3f, a.Clear - 0.2f), OpeningKind.Arch);
                Opening(rooms, w.Cabin, Side.Front, w.Front, (c1 + w.X1) / 2, LiftDoor, Math.Min(2.1f, a.Clear - 0.15f), OpeningKind.Door);
            }
            if (a.Stairs) Stair(floor, a, f, c0);
        }

        // ---- the regions round them ----------------------------------------------------------
        var regions = new List<Region>();
        var wells = a.Wells;
        bool carStrip = what == AptFloor.Basement && a.Hd - a.ZB1 >= 9.5f && a.L.Width >= 12f;
        // A block much deeper than its stairwell: a corridor runs on from each back landing to the
        // back facade, and the flats behind the stairwells' depth open off it, both sides
        bool deep = what != AptFloor.Basement && a.Hd - a.ZB1 > DeepBack;
        var spines = new Dictionary<Well, (int Room, float X0, float X1)>();
        if (deep)
            foreach (var w in wells)
            {
                float m = w.X0 + a.CoreW - 0.9f;
                int sp = Add(rooms, new RoomPlan { X0 = m - 0.8f, Z0 = a.ZB1, X1 = m + 0.8f, Z1 = a.Hd, Type = RoomType.Corridor });
                Opening(rooms, sp, Side.Front, w.Core, m, 1.3f, Math.Min(2.3f, a.Clear - 0.2f), OpeningKind.Arch);
                spines[w] = (sp, m - 0.8f, m + 0.8f);
            }
        float back = deep ? a.ZB1 : a.Hd;
        for (int i = 0; i <= wells.Count; i++)
        {
            float g0 = i == 0 ? -a.Hw : wells[i - 1].X1, g1 = i == wells.Count ? a.Hw : wells[i].X0;
            var left = i > 0 ? wells[i - 1] : null;
            var right = i < wells.Count ? wells[i] : null;
            float width = g1 - g0;
            if (deep)
            {
                // behind the stairwells' depth: from one spine to the next
                float b0 = left != null ? spines[left].X1 : -a.Hw, b1 = right != null ? spines[right].X0 : a.Hw;
                if (left != null && right != null && b1 - b0 >= 2 * MinFlatSide)
                {
                    float mid = (b0 + b1) / 2;
                    regions.Add(new Region(new RectPlan(b0, a.ZB1, mid, a.Hd), Side.Left, new List<Way> { new(spines[left].Room, a.ZB1, a.Hd) }));
                    regions.Add(new Region(new RectPlan(mid, a.ZB1, b1, a.Hd), Side.Right, new List<Way> { new(spines[right].Room, a.ZB1, a.Hd) }));
                }
                else if (b1 - b0 >= 1.2f)
                    regions.Add(left != null
                        ? new Region(new RectPlan(b0, a.ZB1, b1, a.Hd), Side.Left, new List<Way> { new(spines[left].Room, a.ZB1, a.Hd) })
                        : new Region(new RectPlan(b0, a.ZB1, b1, a.Hd), Side.Right, new List<Way> { new(spines[right!].Room, a.ZB1, a.Hd) }));
            }
            if (width < 1.2f) continue;
            bool corridor = what switch
            {
                AptFloor.Basement => true,
                AptFloor.Shops => false,
                _ => !deep && width > (left != null && right != null ? 2 : 1) * CorridorSide,
            };
            if (corridor)
            {
                int cor = Add(rooms, new RoomPlan { X0 = g0, Z0 = a.RunZ1, X1 = g1, Z1 = a.ZB1, Type = RoomType.Corridor });
                float mid = (a.RunZ1 + a.ZB1) / 2, archTop = Math.Min(2.3f, a.Clear - 0.2f);
                if (left != null) Opening(rooms, cor, Side.Left, a.Lift ? left.Back : left.Core, mid, BackLanding - 0.3f, archTop, OpeningKind.Arch);
                if (right != null) Opening(rooms, cor, Side.Right, right.Core, mid, BackLanding - 0.3f, archTop, OpeningKind.Arch);
                regions.Add(new Region(new RectPlan(g0, -hd, g1, a.RunZ1), Side.Back, new List<Way> { new(cor, g0, g1) }));
                if (!carStrip && a.Hd - a.ZB1 >= 2.5f)
                    regions.Add(new Region(new RectPlan(g0, a.ZB1, g1, a.Hd), Side.Front, new List<Way> { new(cor, g0, g1) }));
                continue;
            }
            if (left != null && right != null && width >= 2 * MinFlatSide)
            {
                float mid = (g0 + g1) / 2;
                regions.Add(new Region(new RectPlan(g0, -hd, mid, back), Side.Left, RightWays(a, left)));
                regions.Add(new Region(new RectPlan(mid, -hd, g1, back), Side.Right, LeftWays(a, right)));
            }
            else if (left != null)
                regions.Add(new Region(new RectPlan(g0, -hd, g1, back), Side.Left, RightWays(a, left)));
            else
                regions.Add(new Region(new RectPlan(g0, -hd, g1, back), Side.Right, LeftWays(a, right!)));
        }
        if (!deep && !carStrip && a.Hd - a.ZB1 >= 2.5f)
            foreach (var w in wells)
            {
                var ways = new List<Way> { new(w.Core, w.X0, w.X0 + a.CoreW) };
                if (a.Lift) ways.Add(new Way(w.Back, w.X0 + a.CoreW, w.X1));
                regions.Add(new Region(new RectPlan(w.X0, a.ZB1, w.X1, a.Hd), Side.Front, ways));
            }

        // ---- filled by what the floor is for -----------------------------------------------------
        int unit = 0;
        switch (what)
        {
            case AptFloor.Flats:
                foreach (var r in regions) FillFlats(a, floor, r, f, rng, ref unit);
                break;
            case AptFloor.Shops:
                foreach (var r in regions)
                    // a shop only where it has the street in front of it
                    if (r.Side is Side.Left or Side.Right && r.R.Z0 <= -hd + 0.01f) Shop(a, floor, r, f, rng, ref unit);
                    else FillFlats(a, floor, r, f, rng, ref unit);
                break;
            default:
                Basement(a, floor, regions, carStrip, rng);
                break;
        }
        return floor;
    }

    private static int Add(List<RoomPlan> rooms, RoomPlan r)
    {
        rooms.Add(r);
        return rooms.Count - 1;
    }


    /// <summary>A cut through the wall between room <paramref name="a"/> (on its side <paramref name="side"/>) and room <paramref name="b"/>.</summary>
    private static void Opening(List<RoomPlan> rooms, int a, Side side, int b, float center, float width, float top, OpeningKind kind)
    {
        rooms[a].Openings.Add(new OpeningPlan { Side = side, Center = center, Width = width, Top = top, Kind = kind, Other = b });
        rooms[b].Openings.Add(new OpeningPlan { Side = Opposite(side), Center = center, Width = width, Top = top, Kind = kind, Other = a });
    }

    /// <summary>Where a region left of a stairwell may open a door: the front and the back landing, not the stair's side.</summary>
    private static List<Way> LeftWays(Apt a, Well w) => a.Stairs
        ? new List<Way> { new(w.Core, -a.Hd, a.RunZ0), new(w.Core, a.RunZ1, a.ZB1) }
        : new List<Way> { new(w.Core, -a.Hd, a.ZB1) };

    /// <summary>Right of a stairwell: the lobby in front of the elevator and the back landing, or the walkway beside the stair.</summary>
    private static List<Way> RightWays(Apt a, Well w) => a.Lift
        ? new List<Way> { new(w.Front, -a.Hd, a.RunZ1 - CabinDepth), new(w.Back, a.RunZ1, a.ZB1) }
        : new List<Way> { new(w.Core, -a.Hd, a.ZB1) };

    /// <summary>
    /// The stairwell's switchback, exactly as the cored plan builds its own (<see cref="TryCored"/>):
    /// floor <paramref name="f"/>'s flight in lane A or B, the hole of the one from below, and rails.
    /// The first stairwell's flight is the floor's <see cref="FloorPlan.Flight"/>, the others go
    /// in <see cref="FloorPlan.Flights"/>.
    /// </summary>
    private static void Stair(FloorPlan floor, Apt a, int f, float c0)
    {
        float laneA0 = c0, laneA1 = c0 + LaneWidth, laneB1 = c0 + 2 * LaneWidth;
        float runZ0 = a.RunZ0, runZ1 = a.RunZ1;
        if (f < a.Floors - 1)
        {
            var flight = f % 2 == 0
                ? new FlightPlan { X0 = laneA0, X1 = laneA1, ZBottom = runZ0, ZTop = runZ1 }
                : new FlightPlan { X0 = laneA1, X1 = laneB1, ZBottom = runZ1, ZTop = runZ0 };
            if (floor.Flight == null) floor.Flight = flight;
            else floor.Flights.Add(flight);
        }
        if (f > 0)
        {
            bool holeA = (f - 1) % 2 == 0;
            floor.Holes.Add(holeA ? new RectPlan(laneA0, runZ0, laneA1, runZ1) : new RectPlan(laneA1, runZ0, laneB1, runZ1));
            floor.Rails.Add(new RectPlan(laneA1, runZ0, laneA1, runZ1));
            if (!holeA) floor.Rails.Add(new RectPlan(laneB1, runZ0, laneB1, runZ1));
        }
    }

    // ---- flats ---------------------------------------------------------------------------------

    private static bool AlongX(Side s) => s is Side.Front or Side.Back;

    /// <summary>Length of a way inside [lo, hi], each end kept clear of a corner.</summary>
    private static float Overlap(Way w, float lo, float hi) => Math.Min(w.Hi, hi - 0.15f) - Math.Max(w.Lo, lo + 0.15f);

    /// <summary>
    /// Cuts a region into flats along the wall it opens off, each flat with a way of its own, and
    /// lays each one out. A piece too small to live in is a storeroom off the landing.
    /// </summary>
    private static void FillFlats(Apt a, FloorPlan floor, Region reg, int f, Random rng, ref int unit)
    {
        var R = reg.R;
        bool alongX = AlongX(reg.Side);
        float lo = alongX ? R.X0 : R.Z0, hi = alongX ? R.X1 : R.Z1;
        float len = hi - lo, depth = alongX ? R.Z1 - R.Z0 : R.X1 - R.X0;
        if (depth < 2.0f || len < 1.2f) return;
        // a flat's size wanders a little round the block's own
        float target = a.Target * (0.8f + 0.4f * (float)rng.NextDouble());
        int want = Math.Clamp((int)MathF.Round(len * depth / target), 1, Math.Max(1, (int)(len / MinFlatSide)));
        List<float>? cuts = null;
        for (int n = want; n >= 1 && cuts == null; n--) cuts = Cuts(reg.Ways, lo, hi, n);
        if (cuts == null) return; // no way in at all: leave it solid

        for (int k = 0; k + 1 < cuts.Count; k++)
        {
            float p0 = cuts[k], p1 = cuts[k + 1];
            var piece = alongX ? new RectPlan(p0, R.Z0, p1, R.Z1) : new RectPlan(R.X0, p0, R.X1, p1);
            var way = reg.Ways.MaxBy(w => Overlap(w, p0, p1))!;
            float o0 = Math.Max(way.Lo, p0 + 0.15f), o1 = Math.Min(way.Hi, p1 - 0.15f);
            float c = Fit((o0 + o1) / 2, o0 + FlatDoor / 2 + 0.1f, o1 - FlatDoor / 2 - 0.1f);
            float area = (p1 - p0) * depth;
            if (area < 22f || Math.Min(p1 - p0, depth) < 2.6f)
            {
                // a box room: the building's storeroom, off the landing
                int store = Add(floor.Rooms, new RoomPlan { X0 = piece.X0, Z0 = piece.Z0, X1 = piece.X1, Z1 = piece.Z1, Type = RoomType.Storage });
                Opening(floor.Rooms, store, reg.Side, way.Room, c, FlatDoor, Math.Min(2.05f, a.Clear - 0.2f), OpeningKind.Door);
                continue;
            }
            Flat(a, floor, piece, reg.Side, c, o0 + FlatDoor / 2 + 0.1f, o1 - FlatDoor / 2 - 0.1f, way.Room, f, unit++, rng);
        }
    }

    /// <summary>
    /// Where to cut [<paramref name="lo"/>, <paramref name="hi"/>] into <paramref name="n"/> flats so
    /// each keeps <see cref="WayMin"/> of some way and <see cref="MinFlatSide"/> of length; null if
    /// it cannot be done. Even cuts first, then, for two, the nearest cut that works.
    /// </summary>
    private static List<float>? Cuts(List<Way> ways, float lo, float hi, int n)
    {
        bool Ok(List<float> c)
        {
            for (int k = 0; k + 1 < c.Count; k++)
                if (c[k + 1] - c[k] < MinFlatSide - 0.01f && n > 1 || !ways.Any(w => Overlap(w, c[k], c[k + 1]) >= WayMin))
                    return false;
            return true;
        }
        var even = Enumerable.Range(0, n + 1).Select(k => lo + (hi - lo) * k / n).ToList();
        if (Ok(even)) return even;
        if (n != 2) return null;
        float mid = (lo + hi) / 2;
        for (float d = 0.25f; d < (hi - lo) / 2; d += 0.25f)
            foreach (float s in new[] { -1f, 1f })
            {
                var c = new List<float> { lo, mid + s * d, hi };
                if (Ok(c)) return c;
            }
        return null;
    }

    private sealed record FlatItem(RoomType Type, float Weight, int Keep);

    /// <summary>
    /// What a flat of <paramref name="area"/> m² holds. <see cref="FlatItem.Keep"/> is how hard
    /// it is to give up when the plan is tight: a flat always has a bed, a kitchen and a bathroom.
    /// </summary>
    private static List<FlatItem> FlatProgram(float area, Random rng)
    {
        int beds = area < 62 ? 1 : area < 88 ? 2 : area < 118 ? 3 : 4;
        if (beds > 1 && rng.NextDouble() < 0.2) beds--;
        var p = new List<FlatItem>
        {
            new(RoomType.Living, 3.4f, 8), new(RoomType.Kitchen, 1.4f, 9), new(RoomType.Bathroom, 1.0f, 10),
            new(RoomType.Bedroom, 2.3f, 10),
        };
        for (int i = 1; i < beds; i++) p.Add(new FlatItem(RoomType.Bedroom, 2.0f, 6 - i));
        if (area >= 80) p.Add(new FlatItem(RoomType.WC, 0.55f, 3));
        if (area >= 70 && rng.NextDouble() < 0.6) p.Add(new FlatItem(RoomType.Storage, 0.45f, 1));
        if (area >= 110 && rng.NextDouble() < 0.4) p.Add(new FlatItem(RoomType.Study, 1.4f, 2));
        return p;
    }

    /// <summary>Order from the front door inward: the box room and the WC by the entrance, bedrooms away from it.</summary>
    private static int NearDoor(RoomType t) => t switch
    {
        RoomType.Storage => 0, RoomType.WC => 1, RoomType.Bathroom => 2, RoomType.Kitchen => 3,
        RoomType.Study => 4, RoomType.Bedroom => 5, _ => 6,
    };

    /// <summary>Least a room can be across the strip it is cut from.</summary>
    private static float StripMin(RoomType t) => t switch
    {
        RoomType.Storage => 1.0f, RoomType.WC => 1.1f, RoomType.Bathroom => 1.7f, RoomType.Kitchen => 1.9f,
        RoomType.Bedroom => 2.4f, RoomType.Living => 3.0f, RoomType.Hall => 1.2f, _ => 2.2f,
    };

    /// <summary>A room in the flat's own frame: u along the wall with the front door, v away from it.</summary>
    private sealed record Local(RoomType Type, float U0, float V0, float U1, float V1);

    /// <summary>
    /// Cuts [<paramref name="a"/>, <paramref name="b"/>] into consecutive slices, the order given:
    /// each its <see cref="StripMin"/>, and what is left shared by weight. When even the least of
    /// each does not fit, the item easiest to lose goes and the cut is redone; what was given up
    /// is returned for another strip to take.
    /// </summary>
    private static List<(FlatItem Item, float A, float B)> Strip(float a, float b, List<FlatItem> items, List<FlatItem> dropped)
    {
        var list = new List<FlatItem>(items);
        while (list.Count > 0)
        {
            float least = list.Sum(i => StripMin(i.Type));
            if (least <= b - a)
            {
                float total = list.Sum(i => i.Weight), spare = b - a - least, at = a;
                var slices = new List<(FlatItem, float, float)>();
                foreach (var it in list)
                {
                    float next = at + StripMin(it.Type) + spare * it.Weight / total;
                    slices.Add((it, at, next));
                    at = next;
                }
                return slices;
            }
            var drop = list.OrderBy(i => i.Keep).ThenBy(i => i.Weight).First();
            list.Remove(drop);
            dropped.Add(drop);
        }
        return new List<(FlatItem, float, float)>();
    }

    /// <summary>
    /// Lays one flat out in its own frame (<see cref="Local"/>): <paramref name="u"/> x <paramref name="v"/>,
    /// the front door at <paramref name="door"/> along v = 0. <paramref name="ext"/> says which of
    /// its other walls are the building's facades: the u = 0 end, the u = u end, the far wall.
    /// Returns the rooms, the entrance hall first.
    /// </summary>
    private static List<Local> FlatRooms(float u, float v, float door, (bool U0, bool U1, bool Far) ext, Random rng)
    {
        var program = FlatProgram(u * v, rng);
        var dropped = new List<FlatItem>();
        var rooms = new List<Local>();

        // narrow and deep: a chain of rooms away from the door. Shallow and wide, or with its
        // facades at the ends of the door wall rather than across from it (a flat between two
        // stairwells, its far wall the next flat's): a hall along the door wall, the rooms behind
        // it, the living room and a bedroom at the two facades.
        if (u * v < 38f) return StudioFlat(u, v, door);
        if (u < 4.4f) return LinearFlat(u, v, program);
        if (v >= 3.6f && (v < 6.5f || !ext.Far && (ext.U0 || ext.U1) && v <= 9.5f))
            return GalleryFlat(u, v, door, ext, program) ?? StudioFlat(u, v, door);

        float h0 = Fit(door - HallWidth / 2, 0, u - HallWidth), h1 = h0 + HallWidth;
        if (h0 > 0 && h0 < 2.3f) h0 = 0;
        if (u - h1 > 0 && u - h1 < 2.3f) h1 = u;
        bool sides = h0 > 0 || h1 < u;
        if (!sides) return LinearFlat(u, v, program);

        // the far facade: living room across the hall's end, the kitchen beside it, a bedroom if wide
        bool far = v >= 7.0f;
        float lz = far ? Math.Clamp(v * 0.42f, 3.4f, 6.0f) : 0;
        if (v - lz < 2.4f) { far = false; lz = 0; }
        float sv = v - lz;
        var farItems = new List<FlatItem>();
        if (far)
        {
            farItems.Add(program.First(p => p.Type == RoomType.Living));
            if (u >= 6.8f) farItems.Add(program.First(p => p.Type == RoomType.Kitchen));
            if (u >= 11f && program.Count(p => p.Type == RoomType.Bedroom) >= 2)
                farItems.Add(program.Last(p => p.Type == RoomType.Bedroom));
        }
        var sideItems = program.Except(farItems).OrderBy(p => NearDoor(p.Type)).ToList();

        rooms.Add(new Local(RoomType.Hall, h0, 0, h1, sv));
        if (far)
        {
            // the order that puts the living room most across the hall's end
            List<(FlatItem Item, float A, float B)>? best = null;
            float bestCover = float.MinValue;
            foreach (var order in Orders(farItems))
            {
                var tried = new List<FlatItem>();
                var s = Strip(0, u, order, tried);
                var liv = s.FirstOrDefault(x => x.Item.Type == RoomType.Living);
                float cover = liv.Item == null ? -99 : Math.Min(liv.B, h1) - Math.Max(liv.A, h0) - tried.Count * 10;
                if (cover > bestCover) { bestCover = cover; best = s; }
            }
            foreach (var (it, a0, a1) in best!) rooms.Add(new Local(it.Type, a0, sv, a1, v));
            foreach (var it in farItems.Where(x => best.All(s => s.Item != x))) dropped.Add(it);
        }

        // the two sides of the hall, a share each by area
        var zones = new List<(float A, float B)>();
        if (h0 > 0) zones.Add((0, h0));
        if (h1 < u) zones.Add((h1, u));
        var shares = zones.Select(_ => new List<FlatItem>()).ToList();
        var load = new float[zones.Count];
        float zoneTotal = zones.Sum(z => z.B - z.A);
        float itemTotal = Math.Max(0.01f, sideItems.Sum(i => i.Weight));
        foreach (var it in sideItems.OrderByDescending(i => i.Weight))
        {
            int bestZ = 0;
            float slack = float.MinValue;
            for (int z = 0; z < zones.Count; z++)
            {
                float s = (zones[z].B - zones[z].A) / zoneTotal - load[z] / itemTotal;
                if (s > slack) { slack = s; bestZ = z; }
            }
            shares[bestZ].Add(it);
            load[bestZ] += it.Weight;
        }
        var spill = new List<FlatItem>();
        // a side shallower than it is wide is a row of rooms going outward from the hall, each
        // opening into the next; otherwise rooms are stacked along the hall, each opening off it
        bool outward = sv < 4.5f && zones.Any(z => z.B - z.A >= 4.5f);
        for (int z = 0; z < zones.Count; z++)
        {
            var lost = new List<FlatItem>();
            var ordered = shares[z].OrderBy(p => NearDoor(p.Type)).ToList();
            if (outward)
            {
                bool leftward = zones[z].B <= h0 + 0.01f;
                foreach (var (it, a0, a1) in Strip(0, zones[z].B - zones[z].A, ordered, lost))
                    rooms.Add(leftward
                        ? new Local(it.Type, zones[z].B - a1, 0, zones[z].B - a0, sv)
                        : new Local(it.Type, zones[z].A + a0, 0, zones[z].A + a1, sv));
            }
            else
                foreach (var (it, a0, a1) in Strip(0, sv, ordered, lost))
                    rooms.Add(new Local(it.Type, zones[z].A, a0, zones[z].B, a1));
            spill.AddRange(lost);
        }
        // a room one side could not take, the other side may: redo that side with it
        if (spill.Count > 0 && zones.Count == 2 && !outward)
            foreach (var it in spill.OrderByDescending(i => i.Keep).ToList())
            {
                for (int z = 0; z < 2; z++)
                {
                    var with = shares[z].Append(it).OrderBy(p => NearDoor(p.Type)).ToList();
                    var lost = new List<FlatItem>();
                    var s = Strip(0, sv, with, lost);
                    if (lost.Count > 0) continue;
                    rooms.RemoveAll(r => r.Type != RoomType.Hall && r.V1 <= sv + 0.01f && r.U0 >= zones[z].A - 0.01f && r.U1 <= zones[z].B + 0.01f);
                    foreach (var (i2, a0, a1) in s) rooms.Add(new Local(i2.Type, zones[z].A, a0, zones[z].B, a1));
                    shares[z] = with;
                    spill.Remove(it);
                    break;
                }
            }
        dropped.AddRange(spill);

        bool complete = rooms.Any(r => r.Type == RoomType.Bedroom) && rooms.Any(r => r.Type == RoomType.Kitchen)
            && rooms.Any(r => r.Type == RoomType.Bathroom);
        return complete ? rooms : GalleryFlat(u, v, door, ext, program) ?? StudioFlat(u, v, door);
    }

    /// <summary>
    /// A hall along the door wall and the rooms side by side behind it, each off it. With its
    /// far wall a facade, every room has a window, so the wet rooms go nearest the door and the
    /// living room furthest. Without (a flat between two stairwells, windows only at the two ends):
    /// the living room and the kitchen at one facade, the main bedroom at the other, the bathroom,
    /// the WC and the box room in the dark middle. Null if the rooms do not fit.
    /// </summary>
    private static List<Local>? GalleryFlat(float u, float v, float door, (bool U0, bool U1, bool Far) ext, List<FlatItem> program)
    {
        const float hall = 1.2f;
        var rooms = new List<Local> { new(RoomType.Hall, 0, 0, u, hall) };
        var lost = new List<FlatItem>();
        List<FlatItem> ordered;
        // true: the list runs from u = u down to u = 0
        bool mirrored;
        if (ext.Far || !ext.U0 && !ext.U1)
        {
            ordered = program.OrderBy(p => NearDoor(p.Type)).ToList();
            mirrored = door > u / 2;
        }
        else
        {
            // listed with the living room last, at the end of the list's facade
            var living = program.Where(p => p.Type is RoomType.Kitchen or RoomType.Living).OrderBy(p => p.Type == RoomType.Living);
            var beds = program.Where(p => p.Type is RoomType.Bedroom or RoomType.Study).ToList();
            var wet = program.Where(p => p.Type is RoomType.Bathroom or RoomType.WC or RoomType.Storage).OrderBy(p => NearDoor(p.Type));
            bool both = ext.U0 && ext.U1;
            ordered = both
                ? beds.Take(1).Concat(wet).Concat(beds.Skip(1)).Concat(living).ToList()
                : wet.Concat(beds).Concat(living).ToList();
            // the living room at a facade: away from the door if both ends are
            bool livingAtU = both ? door < u / 2 : ext.U1;
            mirrored = !livingAtU;
        }
        foreach (var (it, a0, a1) in Strip(0, u, ordered, lost))
            rooms.Add(mirrored ? new Local(it.Type, u - a1, hall, u - a0, v) : new Local(it.Type, a0, hall, a1, v));
        bool complete = rooms.Any(r => r.Type == RoomType.Bedroom) && rooms.Any(r => r.Type == RoomType.Kitchen)
            && rooms.Any(r => r.Type == RoomType.Bathroom);
        return complete ? rooms : null;
    }

    /// <summary>
    /// A studio: a short entrance with the bathroom and the kitchenette either side of it, one
    /// room behind for sleeping and sitting (a bedroom, so it gets the bed). Where the door wall
    /// is too short for all three side by side, the kitchenette is a slice between them instead.
    /// </summary>
    private static List<Local> StudioFlat(float u, float v, float door)
    {
        float band = Math.Min(2.3f, v * 0.4f);
        float h0 = Fit(door - HallWidth / 2, 0, u - HallWidth), h1 = h0 + HallWidth;
        var rooms = new List<Local>();
        if (v < 4.6f)
        {
            // too shallow to stack: along the wall, the hall at the door, the bathroom over the
            // kitchenette beside it, the room beyond; a sliver the other side of the hall is a cupboard
            bool toRight = u - h1 >= h0;
            float c0 = toRight ? h1 : Math.Max(0, h0 - 2.0f), c1 = toRight ? Math.Min(u, h1 + 2.0f) : h0;
            rooms.Add(new Local(RoomType.Hall, h0, 0, h1, v));
            rooms.Add(new Local(RoomType.Bathroom, c0, 0, c1, v / 2));
            rooms.Add(new Local(RoomType.Kitchen, c0, v / 2, c1, v));
            rooms.Add(toRight ? new Local(RoomType.Bedroom, c1, 0, u, v) : new Local(RoomType.Bedroom, 0, 0, c0, v));
            float s0 = toRight ? 0 : h1, s1 = toRight ? h0 : u;
            if (s1 - s0 >= 1.0f) rooms.Add(new Local(RoomType.Storage, s0, 0, s1, v));
            else if (s1 - s0 > 0.01f) rooms[0] = toRight ? new Local(RoomType.Hall, 0, 0, h1, v) : new Local(RoomType.Hall, h0, 0, u, v);
            return rooms;
        }
        float left = h0, right = u - h1;
        if (left >= 1.7f && right >= 1.9f || right >= 1.7f && left >= 1.9f)
        {
            bool bathLeft = left >= 1.7f && right >= 1.9f;
            rooms.Add(new Local(RoomType.Hall, h0, 0, h1, band));
            rooms.Add(new Local(bathLeft ? RoomType.Bathroom : RoomType.Kitchen, 0, 0, h0, band));
            rooms.Add(new Local(bathLeft ? RoomType.Kitchen : RoomType.Bathroom, h1, 0, u, band));
            rooms.Add(new Local(RoomType.Bedroom, 0, band, u, v));
            return rooms;
        }
        // the hall to one end, the bathroom beside it, the kitchenette across the flat behind
        if (left < right) { h0 = 0; h1 = HallWidth; } else { h1 = u; h0 = u - HallWidth; }
        rooms.Add(new Local(RoomType.Hall, h0, 0, h1, band));
        rooms.Add(new Local(RoomType.Bathroom, h0 == 0 ? h1 : 0, 0, h0 == 0 ? u : h0, band));
        float k = Math.Min(2.0f, (v - band) * 0.35f);
        rooms.Add(new Local(RoomType.Kitchen, 0, band, u, band + k));
        rooms.Add(new Local(RoomType.Bedroom, 0, band + k, u, v));
        return rooms;
    }

    /// <summary>Every order of up to three items.</summary>
    private static IEnumerable<List<FlatItem>> Orders(List<FlatItem> items)
    {
        if (items.Count <= 1) { yield return items; yield break; }
        foreach (var first in items)
            foreach (var rest in Orders(items.Where(i => i != first).ToList()))
                yield return rest.Prepend(first).ToList();
    }

    /// <summary>
    /// A flat too narrow for rooms beside a hall: a small entrance, then the kitchen and the
    /// bathroom side by side off it, the living room and the bedrooms through it.
    /// </summary>
    private static List<Local> LinearFlat(float u, float v, List<FlatItem> program)
    {
        var rooms = new List<Local> { new(RoomType.Hall, 0, 0, u, Math.Min(1.6f, v * 0.2f)) };
        float v0 = rooms[0].V1;
        var rest = program.Where(p => p.Type is not (RoomType.WC or RoomType.Storage or RoomType.Study)).ToList();
        var wet = new List<FlatItem>();
        if (u >= 3.7f)
        {
            // kitchen and bathroom share the slice next to the hall, both off it
            float ku = u * 0.55f, sv = Math.Min(2.6f, (v - v0) * 0.3f);
            if (sv >= 1.9f)
            {
                rooms.Add(new Local(RoomType.Kitchen, 0, v0, ku, v0 + sv));
                rooms.Add(new Local(RoomType.Bathroom, ku, v0, u, v0 + sv));
                v0 += sv;
                rest.RemoveAll(p => p.Type is RoomType.Kitchen or RoomType.Bathroom);
            }
        }
        var order = rest.OrderBy(p => p.Type switch { RoomType.Bathroom => 0, RoomType.Kitchen => 1, RoomType.Living => 2, _ => 3 }).ToList();
        foreach (var (it, a0, a1) in Strip(v0, v, order, wet))
            rooms.Add(new Local(it.Type, 0, a0, u, a1));
        return rooms;
    }

    /// <summary>
    /// One flat in <paramref name="piece"/>, its front door on wall <paramref name="side"/> at
    /// <paramref name="door"/> off circulation room <paramref name="from"/>: rooms, the doorways
    /// between them, the front door, and its leaf (<see cref="InnerDoorPlan"/>).
    /// </summary>
    private static void Flat(Apt a, FloorPlan floor, RectPlan piece, Side side, float door, float lo, float hi, int from, int f, int unit, Random rng)
    {
        bool alongX = AlongX(side);
        float u = alongX ? piece.X1 - piece.X0 : piece.Z1 - piece.Z0;
        float v = alongX ? piece.Z1 - piece.Z0 : piece.X1 - piece.X0;
        float p0 = alongX ? piece.X0 : piece.Z0;
        // a flat too narrow for rooms both sides of its hall has the hall, and so the front door,
        // at one end of the wall it opens off: the end the door's stretch of wall is nearer to
        if (u < 7.4f && hi >= lo)
        {
            float atStart = p0 + HallWidth / 2, atEnd = p0 + u - HallWidth / 2;
            float s = Fit(atStart, lo, hi), e = Fit(atEnd, lo, hi);
            door = Math.Abs(s - atStart) <= Math.Abs(e - atEnd) ? s : e;
        }
        float du = door - p0;
        // which of the flat's walls are facades, in its own frame
        bool exL = piece.X0 <= -a.Hw + 0.02f, exR = piece.X1 >= a.Hw - 0.02f;
        bool exF = piece.Z0 <= -a.Hd + 0.02f, exB = piece.Z1 >= a.Hd - 0.02f;
        var ext = side switch
        {
            Side.Front => (exL, exR, exB),
            Side.Back => (exL, exR, exF),
            Side.Left => (exF, exB, exR),
            _ => (exF, exB, exL),
        };
        var local = FlatRooms(u, v, du, ext, rng);

        // back into the plan: v grows away from the door wall
        RoomPlan ToPlan(Local r)
        {
            float x0, x1, z0, z1;
            switch (side)
            {
                case Side.Front: x0 = piece.X0 + r.U0; x1 = piece.X0 + r.U1; z0 = piece.Z0 + r.V0; z1 = piece.Z0 + r.V1; break;
                case Side.Back: x0 = piece.X0 + r.U0; x1 = piece.X0 + r.U1; z0 = piece.Z1 - r.V1; z1 = piece.Z1 - r.V0; break;
                case Side.Left: z0 = piece.Z0 + r.U0; z1 = piece.Z0 + r.U1; x0 = piece.X0 + r.V0; x1 = piece.X0 + r.V1; break;
                default: z0 = piece.Z0 + r.U0; z1 = piece.Z0 + r.U1; x0 = piece.X1 - r.V1; x1 = piece.X1 - r.V0; break;
            }
            return new RoomPlan { X0 = x0, Z0 = z0, X1 = x1, Z1 = z1, Type = r.Type, Unit = unit };
        }
        int first = floor.Rooms.Count;
        foreach (var r in local) floor.Rooms.Add(ToPlan(r));
        int hall = first;
        ConnectFlat(floor.Rooms, first, floor.Rooms.Count, hall, a.Clear, rng);

        float top = Math.Min(2.05f, a.Clear - 0.2f);
        Opening(floor.Rooms, hall, side, from, door, FlatDoor, top, OpeningKind.Door);
        a.L.InnerDoors.Add(new InnerDoorPlan
        {
            Floor = f, Room = hall, Side = side, Center = door, Width = FlatDoor, Top = top, Unit = unit,
            Locked = Core.Fnv.Unit($"{a.L.Key}|lock|{f}|{unit}") < LockedShare,
        });
    }

    /// <summary>
    /// Doorways inside one flat: a spanning tree over shared walls grown from the entrance hall.
    /// Every room it can reach opens off the hall; the kitchen off the living room (half the time
    /// with no door at all: an open kitchen); a bathroom never off the living room or the kitchen
    /// if anything else will do.
    /// </summary>
    private static void ConnectFlat(List<RoomPlan> rooms, int first, int end, int root, float clear, Random rng)
    {
        var inTree = new HashSet<int> { root };
        while (inTree.Count < end - first)
        {
            (int A, int B, Side S, float S0, float S1)? best = null;
            int bestScore = int.MinValue;
            foreach (int i in inTree)
                for (int j = first; j < end; j++)
                {
                    if (inTree.Contains(j) || Touching(rooms[i], rooms[j]) is not { } t) continue;
                    var rf = rooms[i].Type;
                    var rt = rooms[j].Type;
                    int score = i == root ? 40 + (rt == RoomType.Living ? 5 : 0)
                        : rf == RoomType.Living && rt == RoomType.Kitchen || rf == RoomType.Kitchen && rt == RoomType.Living ? 35
                        : rf == RoomType.Living ? rt is RoomType.Study ? 10 : rt is RoomType.Bedroom ? 6 : 2
                        : rf == RoomType.Bedroom && rt == RoomType.Bathroom ? 12
                        : 0;
                    if (rt is RoomType.Bathroom or RoomType.WC && rf is RoomType.Living or RoomType.Kitchen) score -= 8;
                    score += (int)Math.Min(t.S1 - t.S0, 4f);
                    if (score > bestScore) { bestScore = score; best = (i, j, t.Side, t.S0, t.S1); }
                }
            if (best is not { } e) return; // cannot happen on a cut rectangle; the validator would say so
            inTree.Add(e.B);
            var ta = rooms[e.A].Type;
            var tb = rooms[e.B].Type;
            bool open = (ta, tb) is (RoomType.Living, RoomType.Kitchen) or (RoomType.Kitchen, RoomType.Living)
                && e.S1 - e.S0 >= 2.0f && rng.NextDouble() < 0.5;
            if (open)
            {
                float w = Math.Min(e.S1 - e.S0 - 0.4f, 2.6f);
                Opening(rooms, e.A, e.S, e.B, (e.S0 + e.S1) / 2, w, Math.Min(2.2f, clear - 0.2f), OpeningKind.Arch);
            }
            else AddDoor(rooms, new Edge(e.A, e.B, e.S, e.S0, e.S1), clear);
        }
    }

    /// <summary>The wall two rooms share, long enough for a doorway: which side of <paramref name="a"/> and the stretch along it.</summary>
    private static (Side Side, float S0, float S1)? Touching(RoomPlan a, RoomPlan b)
    {
        const float eps = 0.02f, need = InnerDoor + 0.3f;
        if (Math.Abs(a.X1 - b.X0) < eps || Math.Abs(b.X1 - a.X0) < eps)
        {
            float s0 = Math.Max(a.Z0, b.Z0), s1 = Math.Min(a.Z1, b.Z1);
            if (s1 - s0 >= need) return (Math.Abs(a.X1 - b.X0) < eps ? Side.Right : Side.Left, s0, s1);
        }
        if (Math.Abs(a.Z1 - b.Z0) < eps || Math.Abs(b.Z1 - a.Z0) < eps)
        {
            float s0 = Math.Max(a.X0, b.X0), s1 = Math.Min(a.X1, b.X1);
            if (s1 - s0 >= need) return (Math.Abs(a.Z1 - b.Z0) < eps ? Side.Back : Side.Front, s0, s1);
        }
        return null;
    }

    // ---- shops under flats -------------------------------------------------------------------

    /// <summary>
    /// A shop beside a stairwell on the ground floor of a mixed block: the sales floor along the
    /// street, its stockroom and WC behind. It opens onto the lobby; a street door that lands in it
    /// is its own entrance (<see cref="AptEntrances"/>).
    /// </summary>
    private static void Shop(Apt a, FloorPlan floor, Region reg, int f, Random rng, ref int unit)
    {
        var R = reg.R;
        if ((R.X1 - R.X0) * (R.Z1 - R.Z0) < 20f || R.X1 - R.X0 < 2.6f) return;
        var items = new List<FlatItem> { new(RoomType.Shop, 5f, 10) };
        if (R.Z1 - R.Z0 >= 8f) items.Add(new FlatItem(RoomType.Storage, 1.3f, 5));
        if (R.Z1 - R.Z0 >= 10f) items.Add(new FlatItem(RoomType.WC, 0.5f, 3));
        int first = floor.Rooms.Count;
        foreach (var (it, z0, z1) in Strip(R.Z0, R.Z1, items, new List<FlatItem>()))
            floor.Rooms.Add(new RoomPlan { X0 = R.X0, Z0 = z0, X1 = R.X1, Z1 = z1, Type = it.Type, Unit = unit });
        int shop = first;
        ConnectFlat(floor.Rooms, first, floor.Rooms.Count, shop, a.Clear, rng);
        var r = floor.Rooms[shop];
        var way = reg.Ways.MaxBy(w => Overlap(w, r.Z0, r.Z1))!;
        float o0 = Math.Max(way.Lo, r.Z0 + 0.15f), o1 = Math.Min(way.Hi, r.Z1 - 0.15f);
        if (o1 - o0 >= WayMin)
        {
            float c = Fit((o0 + o1) / 2, o0 + FlatDoor / 2 + 0.1f, o1 - FlatDoor / 2 - 0.1f);
            float top = Math.Min(2.05f, a.Clear - 0.2f);
            Opening(floor.Rooms, shop, reg.Side, way.Room, c, FlatDoor, top, OpeningKind.Door);
            a.L.InnerDoors.Add(new InnerDoorPlan { Floor = f, Room = shop, Side = reg.Side, Center = c, Width = FlatDoor, Top = top, Unit = unit });
        }
        unit++;
    }

    // ---- the basement --------------------------------------------------------------------------

    /// <summary>
    /// The shared basement: the car park across the back of a deep block (or in the biggest
    /// region of a big one), then every region cut along its corridor wall into the rooms a Swiss
    /// block keeps down there: a laundry per stairwell, the shelter, the boiler room and the
    /// tenants' storage compartments.
    /// </summary>
    private static void Basement(Apt a, FloorPlan floor, List<Region> regions, bool carStrip, Random rng)
    {
        var rooms = floor.Rooms;
        float top = Math.Min(2.05f, a.Clear - 0.2f);
        if (carStrip)
        {
            int park = Add(rooms, new RoomPlan { X0 = -a.Hw, Z0 = a.ZB1, X1 = a.Hw, Z1 = a.Hd, Type = RoomType.CarPark });
            // a door from every circulation room along its front: the stairwells meet in it too
            for (int i = 0; i < park; i++)
            {
                var r = rooms[i];
                if (r.Type is not (RoomType.Corridor or RoomType.Landing or RoomType.Lobby) || Math.Abs(r.Z1 - a.ZB1) > 0.02f) continue;
                if (r.X1 - r.X0 < WayMin) continue;
                Opening(rooms, park, Side.Front, i, (r.X0 + r.X1) / 2, 1.2f, top, OpeningKind.Door);
            }
        }
        else if (a.L.Width * a.L.Depth >= 300f)
        {
            // no strip deep enough: the biggest region that takes a row of bays and an aisle
            var fit = regions.Where(r => (AlongX(r.Side) ? r.R.X1 - r.R.X0 : r.R.Z1 - r.R.Z0) >= 7.5f
                    && (AlongX(r.Side) ? r.R.Z1 - r.R.Z0 : r.R.X1 - r.R.X0) >= 9.5f)
                .OrderByDescending(r => (r.R.X1 - r.R.X0) * (r.R.Z1 - r.R.Z0)).FirstOrDefault();
            if (fit != null)
            {
                regions.Remove(fit);
                var R = fit.R;
                int park = Add(rooms, new RoomPlan { X0 = R.X0, Z0 = R.Z0, X1 = R.X1, Z1 = R.Z1, Type = RoomType.CarPark });
                foreach (var w in fit.Ways)
                {
                    float lo = AlongX(fit.Side) ? R.X0 : R.Z0, hi = AlongX(fit.Side) ? R.X1 : R.Z1;
                    if (Overlap(w, lo, hi) < WayMin) continue;
                    float o0 = Math.Max(w.Lo, lo + 0.15f), o1 = Math.Min(w.Hi, hi - 0.15f);
                    Opening(rooms, park, fit.Side, w.Room, (o0 + o1) / 2, Math.Min(1.2f, o1 - o0 - 0.3f), top, OpeningKind.Door);
                }
            }
        }

        // the program, shared out by area
        float area = regions.Sum(r => (r.R.X1 - r.R.X0) * (r.R.Z1 - r.R.Z0));
        if (area < 4f) return;
        var program = new List<FlatItem>();
        foreach (var _ in a.Wells) program.Add(new FlatItem(RoomType.Laundry, 1.6f, 10));
        program.Add(new FlatItem(RoomType.Shelter, Math.Max(2.5f, area / 60f), 9));
        program.Add(new FlatItem(RoomType.TechRoom, 1.4f, 7));
        float used = program.Sum(p => p.Weight) * 9f;
        int cellars = Math.Clamp((int)MathF.Round((area - used) / 16f), 1, 40);
        for (int i = 0; i < cellars; i++) program.Add(new FlatItem(RoomType.Cellar, 1.6f, 4));

        var shares = regions.Select(_ => new List<FlatItem>()).ToList();
        var load = new float[regions.Count];
        float totalW = program.Sum(p => p.Weight);
        foreach (var it in program.OrderByDescending(p => p.Weight))
        {
            int best = -1;
            float slack = float.MinValue;
            for (int s = 0; s < regions.Count; s++)
            {
                var R = regions[s].R;
                float sl = (R.X1 - R.X0) * (R.Z1 - R.Z0) / area - load[s] / totalW;
                if (sl > slack) { slack = sl; best = s; }
            }
            shares[best].Add(it);
            load[best] += it.Weight;
        }
        for (int s = 0; s < regions.Count; s++)
        {
            var reg = regions[s];
            var R = reg.R;
            bool alongX = AlongX(reg.Side);
            float lo = alongX ? R.X0 : R.Z0, hi = alongX ? R.X1 : R.Z1;
            if (shares[s].Count == 0 || hi - lo < 1.2f || (alongX ? R.Z1 - R.Z0 : R.X1 - R.X0) < 1.6f) continue;
            // a laundry by its stairwell, the shelter and the boiler room deeper in
            var ordered = shares[s].OrderBy(i => i.Type switch { RoomType.Laundry => 0, RoomType.Cellar => 1, _ => 2 }).ToList();
            foreach (var (it, p0, p1) in Strip(lo, hi, ordered, new List<FlatItem>()))
            {
                var way = reg.Ways.MaxBy(w => Overlap(w, p0, p1))!;
                if (Overlap(way, p0, p1) < InnerDoor + 0.2f) continue;
                var piece = alongX ? new RectPlan(p0, R.Z0, p1, R.Z1) : new RectPlan(R.X0, p0, R.X1, p1);
                int room = Add(rooms, new RoomPlan { X0 = piece.X0, Z0 = piece.Z0, X1 = piece.X1, Z1 = piece.Z1, Type = it.Type });
                float o0 = Math.Max(way.Lo, p0 + 0.15f), o1 = Math.Min(way.Hi, p1 - 0.15f);
                float c = Fit((o0 + o1) / 2, o0 + InnerDoor / 2 + 0.05f, o1 - InnerDoor / 2 - 0.05f);
                Opening(rooms, room, reg.Side, way.Room, c, InnerDoor, top, OpeningKind.Door);
            }
        }
    }

    // ---- the street doors ----------------------------------------------------------------------

    /// <summary>
    /// A doorway for every facade door. A front door a stairwell was built for opens into its
    /// lobby; any other one into the nearest room on its wall that can take a street door —
    /// a lobby or a corridor first, then a shop, then a flat's hall or living room (a ground-floor
    /// flat's garden door). Never a bathroom or a bedroom. One that fits nowhere reads as locked.
    /// </summary>
    private static void AptEntrances(Apt a, Footprint fp)
    {
        var l = a.L;
        var ground = l.GroundFloor;
        float clear = a.Clear;
        l.Entrances.Clear();
        var axisV = fp.AxisV;
        foreach (var d in fp.Doors)
        {
            float width = Math.Min(d.Width, 1.8f);
            float height = Math.Min(d.Height, clear - 0.15f);
            var well = a.Wells.FirstOrDefault(w => w.Slot == d.Slot);
            RoomPlan? room = null;
            Side side = Side.Front;
            float center = 0;
            if (well != null)
            {
                // the lobby's front wall: the core's, or the bit before the elevator
                var core = ground.Rooms[well.Core];
                var front = well.Front >= 0 ? ground.Rooms[well.Front] : null;
                float want = d.Slot == 0 ? fp.EntryX : well.DoorX;
                room = front != null && want > core.X1 ? front : core;
                width = Math.Min(width, room.X1 - room.X0 - 0.5f);
                center = Fit(want, room.X0 + width / 2 + 0.2f, room.X1 - width / 2 - 0.2f);
                room.Openings.Add(new OpeningPlan { Side = Side.Front, Center = center, Width = width, Top = height, Kind = OpeningKind.Entry });
            }
            else
            {
                var rel = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
                var at = new Godot.Vector2(rel.Dot(fp.AxisU), rel.Dot(axisV));
                var outward = new Godot.Vector2(d.Outward.X, d.Outward.Z);
                var faces = new Godot.Vector2(outward.Dot(fp.AxisU), outward.Dot(axisV));
                width = Math.Min(width, 1.2f);
                if (width < 0.7f || height < 1.9f) continue;
                foreach (var s in new[] { Side.Front, Side.Right, Side.Back, Side.Left }
                             .OrderByDescending(x => Facing(x).X * faces.X + Facing(x).Z * faces.Y).ThenBy(x => (int)x))
                {
                    if (!FitEntry(l, ground, s, s is Side.Front or Side.Back ? at.X : at.Y, width, out var r, out float c, StreetRank)) continue;
                    if (Math.Min(d.Height, l.ClearOf(r) - 0.15f) < 1.9f) continue;
                    room = r;
                    side = s;
                    center = c;
                    r.Openings.Add(new OpeningPlan { Side = s, Center = c, Width = width, Top = height, Kind = OpeningKind.Entry });
                    break;
                }
                if (room == null) continue;
            }
            var (fx, fz) = Facing(side);
            var line = side switch
            {
                Side.Front => new Godot.Vector2(center, room.Z0),
                Side.Back => new Godot.Vector2(center, room.Z1),
                Side.Left => new Godot.Vector2(room.X0, center),
                _ => new Godot.Vector2(room.X1, center),
            };
            if (d.Slot == 0)
            {
                l.EntryX = center;
                l.EntryWidth = width;
            }
            l.Entrances.Add(new EntrancePlan
            {
                Door = new DoorKey(fp.Key, d.Slot).ToString(),
                X = line.X, Z = line.Y, InX = -fx, InZ = -fz, Width = width,
                DoorX = d.Position.X, DoorY = d.Position.Y, DoorZ = d.Position.Z,
                DoorOutX = d.Outward.X, DoorOutZ = d.Outward.Z,
                DoorWidth = d.Width, DoorHeight = d.Height, Hang = d.Hang, Vehicle = d.Vehicle,
            });
        }
        // one door is the plan's single front door: AllEntrances builds it from the fields above
        if (l.Entrances.Count == 1 && fp.Extra.Count == 0) l.Entrances.Clear();
    }

    /// <summary>Which rooms a side street door may open into, best first; -1 never.</summary>
    private static int StreetRank(RoomPlan r) => r.Type switch
    {
        RoomType.Lobby or RoomType.Landing or RoomType.Corridor => 0,
        RoomType.Shop => 1,
        RoomType.Hall or RoomType.Living => 2,
        RoomType.Kitchen or RoomType.Dining => 3,
        _ => -1,
    };

    // ---- furnishing what only a block of flats has -------------------------------------------

    /// <summary>
    /// What a block's shared rooms hold, where it differs from a house's (null: the usual pieces).
    /// The lobby has the letterboxes; landings and corridors stay bare; the laundry is a shared
    /// one, two of everything; the boiler room is the heating.
    /// </summary>
    private static Piece[]? AptPieces(RoomType t) => t switch
    {
        RoomType.Lobby => new[]
        {
            new Piece(FurnitureType.Mailboxes, 1.4f, 0.32f, 1.3f, true),
            new Piece(FurnitureType.Plant, 0.45f, 0.45f, 1.2f, true),
        },
        RoomType.Landing or RoomType.Corridor or RoomType.Elevator => Array.Empty<Piece>(),
        RoomType.Laundry => new[]
        {
            new Piece(FurnitureType.WashingMachine, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.WashingMachine, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.Dryer, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.Dryer, 0.6f, 0.6f, 0.85f, true),
            new Piece(FurnitureType.Sink, 0.6f, 0.45f, 0.85f, true),
            new Piece(FurnitureType.IroningBoard, 1.2f, 0.35f, 0.9f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.8f, true),
        },
        RoomType.TechRoom => new[]
        {
            new Piece(FurnitureType.WaterTank, 0.8f, 0.8f, 1.8f, true),
            new Piece(FurnitureType.Machine, 1.2f, 0.8f, 1.5f, true),
            new Piece(FurnitureType.WaterTank, 0.6f, 0.6f, 1.4f, true),
            new Piece(FurnitureType.Shelf, 1.0f, 0.4f, 1.8f, true),
            new Piece(FurnitureType.FireExtinguisher, 0.25f, 0.2f, 0.6f, true),
        },
        _ => null,
    };

    private static readonly Piece CagePiece = new(FurnitureType.StorageCage, 1.6f, 1.2f, 2.1f, true);

    /// <summary>The tenants' storage compartments: wire-mesh cages round the walls, as many as fit, and a bike rack.</summary>
    private static void Compartments(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        for (int i = 0; i < 12; i++)
        {
            int before = l.Furniture.Count;
            TryPlace(l, f, r, CagePiece, placed, blocked, rng);
            if (l.Furniture.Count == before) break;
        }
        TryPlace(l, f, r, new Piece(FurnitureType.BikeRack, 1.4f, 0.6f, 1.0f, true), placed, blocked, rng);
    }

    /// <summary>
    /// The car park: a row of bays along the wall across from its doors (and a second row along
    /// the door wall if it is deep enough for an aisle between them), painted out, a pillar every
    /// three bays, cars in most of them, bike racks along a wall. Props only: driving in is #558.
    /// </summary>
    private static void CarPark(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var doorSide = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door)?.Side ?? Side.Front;
        bool alongX = AlongX(doorSide);
        // the frame: u along the rows of bays, v from the door wall (0) to the far wall
        float u0 = alongX ? r.X0 : r.Z0, u1 = alongX ? r.X1 : r.Z1;
        float depth = alongX ? r.Z1 - r.Z0 : r.X1 - r.X0;
        RectPlan Rect(float a0, float v0, float a1, float v1) => doorSide switch
        {
            Side.Front => new RectPlan(a0, r.Z0 + v0, a1, r.Z0 + v1),
            Side.Back => new RectPlan(a0, r.Z1 - v1, a1, r.Z1 - v0),
            Side.Left => new RectPlan(r.X0 + v0, a0, r.X0 + v1, a1),
            _ => new RectPlan(r.X1 - v1, a0, r.X1 - v0, a1),
        };
        const float bayW = 2.5f, bayD = 5.0f, inset = WallInset + 0.05f;
        var rows = new List<(float V0, float V1)> { (depth - bayD - inset, depth - inset) };
        if (depth >= 2 * bayD + 5.5f) rows.Add((inset + 0.2f, inset + 0.2f + bayD));
        var marks = new List<RectPlan>();
        foreach (var (v0, v1) in rows)
        {
            int bays = (int)((u1 - u0 - 2 * inset) / bayW);
            float start = (u0 + u1) / 2 - bays * bayW / 2;
            for (int k = 0; k < bays; k++)
            {
                float a0 = start + k * bayW, a1 = a0 + bayW;
                var bay = Rect(a0, v0, a1, v1);
                if (blocked.Any(q => q.Overlaps(bay))) continue;
                Add(l, f, new Piece(FurnitureType.FloorMarking, alongX ? bayW : bayD, alongX ? bayD : bayW, 0.012f, false), bay, 0, marks);
                // a pillar between every third pair of bays, at the aisle end of the line
                if (k % 3 == 0 && k > 0)
                {
                    float pv = v0 < depth / 2 ? v1 - 0.2f : v0 + 0.2f;
                    var pillar = Rect(a0 - 0.18f, pv - 0.18f, a0 + 0.18f, pv + 0.18f);
                    if (Free(r, pillar, placed, blocked, 0.02f))
                        Add(l, f, new Piece(FurnitureType.Pillar, 0.36f, 0.36f, l.ClearOf(r), false), pillar, 0, placed);
                }
                if (rng.NextDouble() > 0.7) continue;
                var car = Rect((a0 + a1) / 2 - 0.9f, (v0 + v1) / 2 - 2.1f, (a0 + a1) / 2 + 0.9f, (v0 + v1) / 2 + 2.1f);
                if (!Free(r, car, placed, blocked, 0.05f)) continue;
                // a car lies across the row, nose to the aisle
                Add(l, f, new Piece(FurnitureType.Car, 1.8f, 4.2f, 1.4f, false), car, alongX ? 0 : 1, placed);
            }
        }
        for (int i = 0; i < 2; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.BikeRack, 1.6f, 0.6f, 1.0f, true), placed, blocked, rng);
    }
}
