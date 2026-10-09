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
    private const float FrontLanding = GarageRule.FrontLanding;
    /// <summary>Back landing, behind the last step: also the width of a corridor running off it.</summary>
    private const float BackLanding = CorridorWidth;
    /// <summary>The building's own corridors, and so the back landing they run off (#576): wider than a flat's hall.</summary>
    private const float CorridorWidth = GarageRule.CorridorWidth;
    /// <summary>A stairwell's flight (#571): one lane each way, and the open well between them.</summary>
    internal const float StairLane = GarageRule.StairLane, StairEye = GarageRule.StairEye;
    private const float StairWidth = 2 * StairLane + StairEye;
    /// <summary>The half landing the two flights of a storey turn on, front to back.</summary>
    private const float MidLanding = GarageRule.MidLanding;
    /// <summary>Riser of a stairwell's steps, m: a public stair is gentler than a house's.</summary>
    private const float StairRiser = GarageRule.StairRiser;
    /// <summary>Width of the elevator column beside the stair.</summary>
    private const float LiftColumn = GarageRule.LiftColumn;
    private const float CabinDepth = 1.8f;
    private const float LiftDoor = 0.9f;
    private const float FlatDoor = 0.9f;
    /// <summary>A flat is never narrower than this along the wall it opens off.</summary>
    private const float MinFlatSide = GarageRule.MinFlatSide;
    /// <summary>Wall a doorway needs, jambs included.</summary>
    private const float WayMin = 1.4f;
    /// <summary>A side wider than this is served by a corridor off the back landing, flats front and back of it.</summary>
    private const float CorridorSide = 13.5f;
    /// <summary>Depth behind the stairwell past which a corridor runs on to the back facade.</summary>
    private const float DeepBack = 12f;
    private const float HallWidth = 1.3f;
    /// <summary>Share of flats whose front door is locked: cracked with the dial (#557).</summary>
    public const double LockedShare = 0.4;

    /// <summary>
    /// Whether a building is planned as an apartment block (#557): a block of flats, a big
    /// building of no particular kind (the old plans already treated those as flats), and some
    /// commercial blocks of three storeys or more, shops below and flats above. Not a bank.
    /// </summary>
    public static BuildingType ApartmentTypeFor(Footprint fp, BuildingKind kind, int storeys, bool bank) =>
        GarageRule.BlockType(fp.Key.ToString(), fp.Width * fp.Depth, kind, storeys, bank);

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
        public int Core = -1, Front = -1, Cabin = -1, Back = -1, Stair = -1, Passage = -1;
        /// <summary>The landing at the back, or the core where there is no stair and so no back landing.</summary>
        public int BackOrCore => Back >= 0 ? Back : Core;
        /// <summary>The facade door it was built for: slot 0 is the main door.</summary>
        public int Slot;
    }

    /// <summary>
    /// What a block planned as one wing of a bigger building (#577) is told: the building's basement
    /// count, so every wing has the same floors; where another wing joins it on a side other than
    /// its front (a corridor is run to that point, <see cref="Apt.Links"/>); and which stretches of
    /// its walls are not facades but the next wing, so no flat counts on a window there.
    /// </summary>
    internal sealed class AptOptions
    {
        public int? Below;
        public List<(Side Side, float At)> Links = new();
        public Func<Side, float, float, float>? Free;
        /// <summary>
        /// Its main door is where the next wing's corridor meets it (#598): the stairwell stays
        /// there and never slides to an end, or the two doorways would not meet.
        /// </summary>
        public bool Pinned;
    }

    /// <summary>
    /// The garage ramp's column (#558), in the plan's frame: wall to wall <c>X0..X1</c>; the run along Z
    /// from <c>Top</c> (the flat apron behind the door ends there, floor level of the ground floor) to
    /// <c>Foot</c> (basement floor); the slab over it stops at <c>HoleEnd</c>; <c>Slot</c> is the door.
    /// </summary>
    private sealed record RampColumn(float X0, float X1, float Top, float Foot, float HoleEnd, int Slot)
    {
        public float Center => (X0 + X1) / 2;
        /// <summary>The ground floor's ramp room and the basement's, set as they are laid out.</summary>
        public int GroundRoom = -1, BasementRoom = -1;

        // ---- along the facade (#694): the same numbers, but in X, which way the descent runs, and the room round it
        /// <summary>Whether the ramp runs along the facade: then <c>Top</c>, <c>Foot</c> and <c>HoleEnd</c> are plan X, <c>X0..X1</c> the ground floor ramp room.</summary>
        public bool Along;
        /// <summary>+1: the descent runs toward +X from the facade's low end, -1 the other way.</summary>
        public int Dir = 1;
        /// <summary>The lane's extent in Z (3.6 m, against the stairwell's half landing wall); the band is Z <c>-Hd..BandZ1</c>.</summary>
        public float LaneZ0, LaneZ1, BandZ1;
        /// <summary>Plan X where the basement's ramp room ends and the car park hall begins, and where that hall ends (the stairwell's wall); the band starts at <c>BandX0</c> (its garage end).</summary>
        public float ParkX, WellEdge, BandX0;
    }

    private sealed class Apt
    {
        public InteriorLayout L = null!;
        /// <summary>
        /// Front to back: the front landing to <see cref="RunZ0"/>, the flights to <see cref="RunZ1"/>,
        /// the half landing to <see cref="ZM"/>, the back landing to <see cref="ZB1"/>.
        /// </summary>
        public float Hw, Hd, RunZ0, RunZ1, ZM, ZB1, CoreW, Clear, Tread;
        public bool Lift, Stairs;
        /// <summary>
        /// Whether the stairwell has its passage beside the stair and a back landing behind it
        /// (#571). A block too narrow for both has neither: its flats open off the front landing.
        /// </summary>
        public bool Passage;
        public int Floors, Below;
        /// <summary>
        /// The garage ramp (#558) when the block has a vehicle door it can serve: its column of the plan
        /// (X0..X1 wall to wall), the Z where the flat apron ends and the descent starts (<c>Top</c>), its foot,
        /// and where the floor slab over it stops (<c>HoleEnd</c>); <c>Slot</c> is the garage door it serves.
        /// The column is kept out of every floor's flats; only the ground floor and the basement have rooms in it.
        /// </summary>
        public RampColumn? Ramp;
        public List<Well> Wells = new();
        public bool Mixed;
        /// <summary>The flat size this building runs to, m²: one block is studios, the next family flats.</summary>
        public float Target;
        /// <summary>Where another wing joins this one off its front (#577): a corridor runs to each.</summary>
        public List<(Side Side, float At)> Links = new();
        /// <summary>How much of a stretch [lo, hi] of the wall on a side faces out (not onto the next wing), m.</summary>
        public Func<Side, float, float, float> Free = (_, lo, hi) => hi - lo;
        /// <summary>Whether a stretch is a facade: enough of it faces out for a window.</summary>
        public bool Facade(Side s, float lo, float hi) => Free(s, lo, hi) >= WindowWall + 0.3f;
    }

    /// <summary>One rectangle planned as a block of flats: the whole building, or one wing of it (#577).</summary>
    internal static bool TryBlock(InteriorLayout l, Footprint fp, BuildingKind kind, int above, BuildingType type, Random rng, AptOptions o)
    {
        float W = l.Width, D = l.Depth, h = l.StoreyHeight;
        float hw = W / 2, hd = D / 2, clear = h - Slab;
        bool mixed = type == BuildingType.MixedUse;

        // ---- the stair, which sets the stairwell's depth -----------------------------------
        // a block with a garage door (#694) has the basement it leads to, whatever its own seed says
        bool garageDoor = fp.Doors.Any(d => d.Vehicle && d.Width > 0 && d.Link.Any);
        int below = o.Below ?? (garageDoor ? 1 : AptBasement(l.Key, mixed, above, W * D));   // a wing is told the building's
        int floors = above + below;
        bool stairs = floors > 1;
        // a stairwell climbs a storey in two flights round a half landing (#571): each flight is
        // half a storey's steps, and the stair is that, the half landing and the two floor landings deep
        int steps = (int)MathF.Ceiling(h / 2 / StairRiser);
        float tread = 0.28f;
        if (stairs && FrontLanding + tread * steps + MidLanding + BackLanding > D)
        {
            tread = (D - FrontLanding - MidLanding - BackLanding) / steps;
            if (tread < 0.22f)
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
        // the stair and, beside it, the passage from the front landing to the back one
        float coreW = stairs ? StairWidth + WalkWidth : 2.4f;
        bool passage = stairs;
        if (stairs && W < coreW + (lift ? LiftColumn : 0) + MinFlatSide)
        {
            passage = false;
            coreW = StairWidth;
        }
        float wellW = coreW + (lift ? LiftColumn : 0);
        if (W < wellW + MinFlatSide || D < 5f) return false;

        float mid = stairs ? MidLanding : 0;
        float back = passage || !stairs ? BackLanding : 0;
        float sd = FrontLanding + run + mid + back;
        // a strip behind the stairwell too thin to be a flat goes to the stairwell instead: the
        // lobby gets deeper
        if (D - sd < 5.0f) sd = D;
        float zB1 = -hd + sd, zM = zB1 - back, runZ1 = zM - mid, runZ0 = runZ1 - run;

        var a = new Apt
        {
            L = l, Hw = hw, Hd = hd, RunZ0 = runZ0, RunZ1 = runZ1, ZM = zM, ZB1 = zB1, CoreW = coreW, Clear = clear,
            Tread = tread, Lift = lift, Stairs = stairs, Passage = passage, Floors = floors, Below = below, Mixed = mixed,
            Target = 55f + 60f * (float)new Random(StableHash(l.Key + "|flatsize")).NextDouble(),
            Links = o.Links,
        };
        if (o.Free != null) a.Free = o.Free;

        // ---- one stairwell per front door, spaced so there are flats between ------------------
        var axisV = fp.AxisV;
        var cands = new List<(float X, int Slot)> { (fp.EntryX, fp.Door.Slot) };
        foreach (var d in fp.Extra)
        {
            var rel = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
            var outward = new Godot.Vector2(d.Outward.X, d.Outward.Z);
            // only a door on the front wall leads straight into a stairwell
            if (outward.Dot(axisV) > -0.8f || d.Vehicle) continue;   // the garage door has a ramp, not a stairwell (#558)
            cands.Add((rel.Dot(fp.AxisU), d.Slot));
        }
        float spacing = wellW + (mixed ? 12f : 2 * MinFlatSide + 0.6f);
        var kept = new List<(float X, int Slot)> { cands[0] };
        // where the next wing joins this one's front (#598) a stairwell is a must, not a nicety:
        // one flat between it and the next is enough
        foreach (var c in cands.Skip(1).OrderBy(c => Math.Abs(c.X - cands[0].X)))
            if (kept.All(k => Math.Abs(k.X - c.X) >= (c.Slot >= LinkSlot ? wellW + MinFlatSide + 0.3f : spacing))) kept.Add(c);
        foreach (var (x, slot) in kept.OrderBy(k => k.X))
        {
            float cx = Fit(x, -hw + wellW / 2, hw - wellW / 2);
            var w = new Well { X0 = cx - wellW / 2, X1 = cx + wellW / 2, DoorX = x, Slot = slot };
            if (a.Wells.Count > 0 && w.X0 < a.Wells[^1].X1 + MinFlatSide) continue;
            a.Wells.Add(w);
        }
        if (a.Wells.All(w => w.Slot != fp.Door.Slot)) return false;
        // A sliver at either end too thin for a flat: slide the stairwell flush, as the cored plan
        // does with its core. Its lobby's doorway then stands a little off the facade door, which
        // the plan box already allows (inside and outside line up plausibly, not exactly). A small
        // block with one stairwell goes to the nearer end outright: one flat a floor, as a Swiss
        // three-family house is, beats two slivers either side of the stair. Not in a wing
        // entered from the next one: its sliver is a box room instead.
        // The garage's lane (#694): a stairwell never slides into it. A square ramp's column, or the
        // along-the-facade ramp's band and car park hall from the end wall.
        (float Lo, float Hi)? lane = null;
        if (garageDoor && !o.Pinned)
        {
            var gd = fp.Doors.First(d => d.Vehicle && d.Width > 0 && d.Link.Any);
            float gxr = new Godot.Vector2(gd.Position.X - fp.Center.X, gd.Position.Z - fp.Center.Y).Dot(fp.AxisU);
            lane = gd.Ramp == GarageRule.RampKind.Along
                ? (gd.RampDir > 0 ? (gxr - GarageRule.AlongDoorX, gxr - GarageRule.AlongDoorX + GarageRule.AlongKeepOut(h)) : (gxr + GarageRule.AlongDoorX - GarageRule.AlongKeepOut(h), gxr + GarageRule.AlongDoorX))
                : (gxr - GarageRule.RampWidth / 2, gxr + GarageRule.RampWidth / 2);
        }
        bool Hits(float x0, float x1) => lane is { } ln && x1 + GarageRule.LaneGap > ln.Lo + 0.01f && x0 - GarageRule.LaneGap < ln.Hi - 0.01f;
        if (!o.Pinned)
        {
            var w0 = a.Wells[0];
            var wn = a.Wells[^1];
            float gl = w0.X0 + hw, gr = hw - wn.X1;
            bool small = a.Wells.Count == 1 && gl + gr < 2 * (MinFlatSide + 2.6f);
            if (gl > 0.01f && (gl < MinFlatSide || small && gl <= gr) && !Hits(w0.X0 - gl, w0.X1 - gl)) { w0.X0 -= gl; w0.X1 -= gl; }
            else if (gr > 0.01f && (gr < MinFlatSide || small) && !Hits(wn.X0 + gr, wn.X1 + gr)) { wn.X0 += gr; wn.X1 += gr; }
            gr = hw - wn.X1;
            if (a.Wells.Count > 1 && gr > 0.01f && gr < MinFlatSide) { wn.X0 += gr; wn.X1 += gr; }
        }

        // the garage ramp's column (#558): kept out of every floor's flats
        if (garageDoor) RampWhy = null;   // only the wing with the garage door says why it has no ramp
        if (!o.Pinned) a.Ramp = PlanRamp(a, fp);
        else if (fp.Doors.Any(d => d.Vehicle)) RampWhy = "entered from another wing";

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
    /// The column for the ramp behind the block's garage door (#558), or null when there is no such
    /// door or the plan has no room for it: the door is on the front wall (a ramp runs straight in
    /// from it), a basement car park strip runs across the back, the block is deep enough
    /// (<see cref="GarageRule.HasRamp"/>), and the lane stands clear of every stairwell. A door with no
    /// ramp reads as locked, as it did before the ramp existed.
    /// </summary>
    /// <summary>Why the last <see cref="PlanRamp"/> of this thread planned none (null: it did, or there was no garage door), for the real-data probe.</summary>
    internal static string? RampWhy;

    private static RampColumn? PlanRamp(Apt a, Footprint fp)
    {
        var l = a.L;
        RampColumn? No(string why) { RampWhy = why; return null; }
        if (!fp.Doors.Any(d => d.Vehicle && d.Width > 0)) return null;
        if (a.Below < 1 || !a.Stairs || !a.Passage) return No("no basement or no passage");
        foreach (var gdoor in fp.Doors)
            if (gdoor.Vehicle && gdoor.Width > 0 && gdoor.Ramp == GarageRule.RampKind.Along) return PlanAlong(a, fp, gdoor);
        bool strip = a.Hd - a.ZB1 >= GarageRule.StripDepth && l.Width >= GarageRule.StripWidth;
        if (!strip) return No($"no car park strip ({a.Hd - a.ZB1:F1} m behind the stairwells)");
        if (!GarageRule.HasRamp(l.Depth, l.StoreyHeight)) return No($"too shallow ({l.Depth:F1} m for {GarageRule.RampDepth(l.StoreyHeight):F1})");
        var axisV = fp.AxisV;
        foreach (var d in fp.Doors)
        {
            if (!d.Vehicle || d.Width <= 0) continue;
            var outward = new Godot.Vector2(d.Outward.X, d.Outward.Z);
            if (outward.Dot(axisV) > -0.8f) return No("the garage door is not on the front wall");
            var rel = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y);
            float x = rel.Dot(fp.AxisU);
            float x0 = x - GarageRule.RampWidth / 2, x1 = x + GarageRule.RampWidth / 2;
            if (x0 < -a.Hw + 0.3f || x1 > a.Hw - 0.3f) return No($"the door is {x:F1} m along a {l.Width:F1} m plan box (the facade is longer than the box)");
            // another wing joined off this one's end is reached by a corridor through the gap that holds the end wall's stairwell: a lane
            // standing outside every stairwell on that side would wall it off
            foreach (var (ls, _) in a.Links)
                if (ls == Side.Left && x0 < a.Wells[0].X0 || ls == Side.Right && x1 > a.Wells[^1].X1)
                    return No($"the lane stands between the stairwells and the end wall the next wing joins ({ls})");
            if (a.Wells.FirstOrDefault(w => x1 + 0.4f - 0.02f > w.X0 && x0 - 0.4f + 0.02f < w.X1) is { } hit)
                return No($"the lane ({x0:F1}..{x1:F1}) meets the stairwell at {hit.X0:F1}..{hit.X1:F1}");
            float top = -a.Hd + GarageRule.RampApron;
            float foot = -a.Hd + GarageRule.RampFoot(l.StoreyHeight);
            return new RampColumn(x0, x1, top, foot, top + RampProfile.HoleLength(l.StoreyHeight, a.Clear), d.Slot);
        }
        return null;
    }

    /// <summary>
    /// The ramp that runs along the facade (#694), behind the garage door <paramref name="d"/>: a band as deep as the
    /// stairwell's half landing wall, along the front wall from the garage's end of it, the descent starting
    /// <see cref="GarageRule.AlongTurnIn"/> from the band's start and ending in a car park hall that runs on to the
    /// first stairwell. Null (the door reads as locked) when a stairwell stands in it or the hall is too short.
    /// </summary>
    private static RampColumn? PlanAlong(Apt a, Footprint fp, DoorSpot d)
    {
        var l = a.L;
        RampColumn? No(string why) { RampWhy = why; return null; }
        float h = l.StoreyHeight;
        int dir = d.RampDir >= 0 ? 1 : -1;
        var axisV = fp.AxisV;
        if (new Godot.Vector2(d.Outward.X, d.Outward.Z).Dot(axisV) > -0.8f) return No("the garage door is not on the front wall");
        float gx = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y).Dot(fp.AxisU);
        float x0 = gx - dir * GarageRule.AlongDoorX;                    // the band's start, at the garage's end
        float X(float u) => x0 + dir * u;
        float uTop = GarageRule.EndMargin + GarageRule.AlongTurnIn, uFoot = uTop + RampProfile.Length(h), uPark = uFoot - 0.3f;
        if (x0 < -a.Hw - 0.01f || x0 > a.Hw + 0.01f) return No($"the band starts at {x0:F1}, outside the {l.Width:F1} m box");
        // the first stairwell past the band: the hall runs up to its wall; none may stand inside the band
        float wellEdge = float.NaN;
        foreach (var w in a.Wells)
        {
            float near = dir > 0 ? w.X0 : w.X1, far = dir > 0 ? w.X1 : w.X0;
            if (dir * (near - X(uPark)) < -0.01f) return No($"a stairwell at {w.X0:F1}..{w.X1:F1} stands in the ramp's band");
            if (float.IsNaN(wellEdge) || dir * (near - wellEdge) < 0) wellEdge = near;
        }
        if (float.IsNaN(wellEdge)) return No("no stairwell beyond the car park hall");
        float hall = dir * (wellEdge - X(uPark));
        if (hall < GarageRule.AlongParkLength - 0.6f) return No($"the car park hall is {hall:F1} m long");
        if (a.Hd - a.ZM < 3f) return No("no room behind the stairwell's half landing");
        float z1 = a.ZM - 0.1f, z0 = z1 - GarageRule.RampWidth;
        float uHole = uTop + RampProfile.HoleLength(h, a.Clear);
        return new RampColumn(Math.Min(x0, X(uHole)), Math.Max(x0, X(uHole)), X(uTop), X(uFoot), X(uHole), d.Slot)
        {
            Along = true, Dir = dir, LaneZ0 = z0, LaneZ1 = z1, BandZ1 = a.ZM, ParkX = X(uPark), WellEdge = wellEdge, BandX0 = x0,
        };
    }

    /// <summary>
    /// Where a block's stairwell rows end, front to back, as <see cref="TryBlock"/> lays them out
    /// for a block <paramref name="w"/> by <paramref name="d"/> of <paramref name="floors"/> floors
    /// (#577: the wing beyond a corridor's end must meet it there): the back landing from
    /// <c>ZM</c> to <c>ZB1</c>; null when the block has no back landing or no stair fits.
    /// </summary>
    internal static (float ZM, float ZB1)? BackLandingOf(float w, float d, float h, int floors)
    {
        bool stairs = floors > 1;
        int steps = (int)MathF.Ceiling(h / 2 / StairRiser);
        float tread = 0.28f;
        if (stairs && FrontLanding + tread * steps + MidLanding + BackLanding > d)
        {
            tread = (d - FrontLanding - MidLanding - BackLanding) / steps;
            if (tread < 0.22f) return null;
        }
        bool lift = floors >= 3;
        float coreW = stairs ? StairWidth + WalkWidth : 2.4f;
        if (stairs && w < coreW + (lift ? LiftColumn : 0) + MinFlatSide) return null;
        float run = stairs ? tread * steps : 0, mid = stairs ? MidLanding : 0;
        float sd = FrontLanding + run + mid + BackLanding;
        if (d - sd < 5.0f) sd = d;
        float zB1 = -d / 2 + sd;
        return (zB1 - BackLanding, zB1);
    }

    /// <summary>
    /// Whether the block has a basement, from its own seed like <see cref="Cellars"/>: nearly every
    /// Swiss block of flats has one, and any of some size certainly does (the shelter, the law said).
    /// </summary>
    private static int AptBasement(string key, bool mixed, int above, float area) =>
        GarageRule.Basement(key, mixed, above, area);

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
            float arch = Math.Min(2.3f, a.Clear - 0.2f);
            float open = a.Clear;
            if (!a.Stairs)
                w.Core = Add(rooms, new RoomPlan { X0 = c0, Z0 = -hd, X1 = c1, Z1 = a.ZB1, Type = lobby });
            else
            {
                // the front landing (the lobby on the ground floor), the enclosed stair open onto it,
                // the passage beside the stair, and the back landing across the whole stairwell (#571)
                float s1 = c0 + StairWidth;
                w.Core = Add(rooms, new RoomPlan { X0 = c0, Z0 = -hd, X1 = c1, Z1 = a.RunZ0, Type = lobby });
                w.Stair = Add(rooms, new RoomPlan { X0 = c0, Z0 = a.RunZ0, X1 = s1, Z1 = a.ZM, Type = RoomType.Stairwell });
                // no pier between the openings that meet here and no lintel over them: the front landing,
                // the stair, the passage and the lift lobby read as one open space (#680)
                Opening(rooms, w.Stair, Side.Front, w.Core, (c0 + s1) / 2, StairWidth, open, OpeningKind.Arch);
                if (a.Passage)
                {
                    w.Passage = Add(rooms, new RoomPlan { X0 = s1, Z0 = a.RunZ0, X1 = c1, Z1 = a.ZM, Type = RoomType.Landing });
                    w.Back = Add(rooms, new RoomPlan { X0 = c0, Z0 = a.ZM, X1 = w.X1, Z1 = a.ZB1, Type = RoomType.Landing });
                    Opening(rooms, w.Passage, Side.Front, w.Core, (s1 + c1) / 2, WalkWidth, open, OpeningKind.Arch);
                    // the stair's wall on the passage is a railing instead (Stair)
                    Opening(rooms, w.Stair, Side.Right, w.Passage, (a.RunZ0 + a.ZM) / 2, a.ZM - a.RunZ0, open, OpeningKind.Arch);
                    Opening(rooms, w.Passage, Side.Back, w.Back, (s1 + c1) / 2, WalkWidth - 0.2f, arch, OpeningKind.Arch);
                }
            }
            if (a.Lift)
            {
                float cab0 = a.ZM - CabinDepth;
                w.Front = Add(rooms, new RoomPlan { X0 = c1, Z0 = -hd, X1 = w.X1, Z1 = cab0, Type = lobby });
                w.Cabin = Add(rooms, new RoomPlan { X0 = c1, Z0 = cab0, X1 = w.X1, Z1 = a.ZM, Type = RoomType.Elevator });
                Opening(rooms, w.Core, Side.Right, w.Front, (-hd + a.RunZ0) / 2, a.RunZ0 + hd, open, OpeningKind.Arch);
                if (a.Passage && cab0 - a.RunZ0 >= 1.2f)
                    Opening(rooms, w.Passage, Side.Right, w.Front, (a.RunZ0 + cab0) / 2, cab0 - a.RunZ0, open, OpeningKind.Arch);
                Opening(rooms, w.Cabin, Side.Front, w.Front, (c1 + w.X1) / 2, LiftDoor, Math.Min(2.1f, a.Clear - 0.15f), OpeningKind.Door);
            }
            if (a.Stairs) Stair(floor, a, f, c0);
        }

        // ---- the garage ramp (#558) ---------------------------------------------------------
        // Its column holds no flats on any floor. The ground floor has a room under the garage door:
        // the flat apron, then the floor slab opens over the descent (the hole), which is the ramp's
        // flight, standing on the basement floor. The basement has its own room as far as the car
        // park, which the ramp runs on into.
        if (a.Ramp is { Along: true } along)
        {
            // along the facade (#694): the ground floor's room is the band as far as the slab opens over the descent;
            // the basement's runs to where the car park hall begins, and the flight runs along X
            if (level == 0)
            {
                along.GroundRoom = Add(rooms, new RoomPlan { X0 = along.X0, Z0 = -hd, X1 = along.X1, Z1 = along.BandZ1, Type = RoomType.Ramp });
                float h0 = Math.Min(along.Top, along.HoleEnd), h1 = Math.Max(along.Top, along.HoleEnd);
                floor.Holes.Add(new RectPlan(h0, along.LaneZ0, h1, along.LaneZ1));
            }
            else if (level == -1)
            {
                float b0 = Math.Min(along.BandX0, along.ParkX), b1 = Math.Max(along.BandX0, along.ParkX);
                along.BasementRoom = Add(rooms, new RoomPlan { X0 = b0, Z0 = -hd, X1 = b1, Z1 = along.BandZ1, Type = RoomType.Ramp });
                floor.Flights.Add(new FlightPlan
                {
                    Ramp = true, AlongX = true, X0 = along.LaneZ0, X1 = along.LaneZ1, ZBottom = along.Foot, ZTop = along.Top, From = 0, To = 1,
                });
            }
        }
        else if (a.Ramp is { } ramp)
        {
            if (level == 0)
            {
                ramp.GroundRoom = Add(rooms, new RoomPlan { X0 = ramp.X0, Z0 = -hd, X1 = ramp.X1, Z1 = ramp.HoleEnd, Type = RoomType.Ramp });
                floor.Holes.Add(new RectPlan(ramp.X0, ramp.Top, ramp.X1, ramp.HoleEnd));
            }
            else if (level == -1)
            {
                ramp.BasementRoom = Add(rooms, new RoomPlan { X0 = ramp.X0, Z0 = -hd, X1 = ramp.X1, Z1 = a.ZB1, Type = RoomType.Ramp });
                floor.Flights.Add(new FlightPlan
                {
                    Ramp = true, X0 = ramp.X0, X1 = ramp.X1, ZBottom = ramp.Foot, ZTop = ramp.Top, From = 0, To = 1,
                });
            }
        }

        // ---- the regions round them ----------------------------------------------------------
        var regions = new List<Region>();
        var wells = a.Wells;
        // what hangs off the back landing needs one: a block too narrow for the passage has none
        bool backed = a.Passage || !a.Stairs;
        bool carStrip = backed && what == AptFloor.Basement && a.Hd - a.ZB1 >= GarageRule.StripDepth && a.L.Width >= GarageRule.StripWidth && a.Ramp is not { Along: true };
        // A block much deeper than its stairwell: a corridor runs on from each back landing to the
        // back facade, and the flats behind the stairwells' depth open off it, both sides
        bool deep = backed && what != AptFloor.Basement && a.Hd - a.ZB1 > DeepBack;
        var spines = new Dictionary<Well, (int Room, float X0, float X1)>();
        if (deep)
            foreach (var w in wells)
            {
                float m = w.X0 + a.CoreW - 0.9f;
                float half = CorridorWidth / 2;
                int sp = Add(rooms, new RoomPlan { X0 = m - half, Z0 = a.ZB1, X1 = m + half, Z1 = a.Hd, Type = RoomType.Corridor });
                Opening(rooms, sp, Side.Front, w.BackOrCore, m, CorridorWidth - 0.4f, Math.Min(2.3f, a.Clear - 0.2f), OpeningKind.Arch);
                spines[w] = (sp, m - half, m + half);
            }
        float back = deep ? a.ZB1 : a.Hd;
        // the gaps between the stairwells; the garage ramp's column (#558) splits the gap it stands in
        // in two, each part reached from its own stairwell only
        var gaps = new List<(float G0, float G1, Well? Left, Well? Right)>();
        for (int i = 0; i <= wells.Count; i++)
        {
            float gg0 = i == 0 ? -a.Hw : wells[i - 1].X1, gg1 = i == wells.Count ? a.Hw : wells[i].X0;
            var gl = i > 0 ? wells[i - 1] : null;
            var gr = i < wells.Count ? wells[i] : null;
            if (a.Ramp is { Along: false } rp && rp.X0 >= gg0 - 0.01f && rp.X1 <= gg1 + 0.01f)
            {
                gaps.Add((gg0, rp.X0, gl, null));
                gaps.Add((rp.X1, gg1, null, gr));
            }
            else gaps.Add((gg0, gg1, gl, gr));
        }
        foreach (var (g0, g1, left, right) in gaps)
        {
            float width = g1 - g0;
            // beside the ramp and at no stairwell: nothing to open off
            if (left == null && right == null) continue;
            // a strip between the ramp's lane and a stairwell too thin for a flat stays solid (#694)
            if (a.Ramp is { Along: false } sq && width < MinFlatSide + 1.6f && (Math.Abs(g1 - sq.X0) < 0.01f || Math.Abs(g0 - sq.X1) < 0.01f)) continue;
            // along the facade (#694) the basement under the gap with the band is its own: the ramp room, the car park hall
            if (what == AptFloor.Basement && a.Ramp is { Along: true } ar && ar.ParkX >= g0 - 0.01f && ar.ParkX <= g1 + 0.01f)
            {
                AlongBasement(a, floor, regions, ar, g0, g1, left, right);
                continue;
            }
            if (deep)
            {
                // behind the stairwells' depth: from one spine to the next
                float b0 = left != null ? spines[left].X1 : g0, b1 = right != null ? spines[right].X0 : g1;
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
            // another wing joining this one at its end, or off its back in this gap, is reached by
            // the corridor off the back landing (#577)
            // (the part of a gap beside the garage's lane is not the end of the wall: it runs to the lane, not to the wing beyond)
            bool linked = backed && a.Links.Any(k => k.Side == Side.Left && left == null && g0 <= -a.Hw + 0.01f
                || k.Side == Side.Right && right == null && g1 >= a.Hw - 0.01f
                || k.Side == Side.Back && k.At > g0 && k.At < g1);
            bool corridor = linked || what switch
            {
                AptFloor.Basement => backed,
                AptFloor.Shops => false,
                _ => backed && !deep && width > (left != null && right != null ? 2 : 1) * CorridorSide,
            };
            if (corridor)
            {
                int cor = Add(rooms, new RoomPlan { X0 = g0, Z0 = a.ZM, X1 = g1, Z1 = a.ZB1, Type = RoomType.Corridor });
                float mid = (a.ZM + a.ZB1) / 2, archTop = Math.Min(2.3f, a.Clear - 0.2f);
                if (left != null) Opening(rooms, cor, Side.Left, left.BackOrCore, mid, BackLanding - 0.3f, archTop, OpeningKind.Arch);
                if (right != null) Opening(rooms, cor, Side.Right, right.BackOrCore, mid, BackLanding - 0.3f, archTop, OpeningKind.Arch);
                regions.Add(new Region(new RectPlan(g0, -hd, g1, a.ZM), Side.Back, new List<Way> { new(cor, g0, g1) }));
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
        if (backed && !deep && !carStrip && a.Hd - a.ZB1 >= 2.5f)
            foreach (var w in wells)
            {
                var ways = new List<Way> { new(w.BackOrCore, w.X0, w.X0 + a.CoreW) };
                if (a.Lift) ways.Add(new Way(w.Back, w.X0 + a.CoreW, w.X1));
                regions.Add(new Region(new RectPlan(w.X0, a.ZB1, w.X1, a.Hd), Side.Front, ways));
            }

        // the ramp's room on the ground floor (#694) is no flat's: cut out of whatever region it stands in
        if (level == 0 && a.Ramp is { Along: true, GroundRoom: >= 0 } groundRamp)
            regions = Carve(regions, new RectPlan(groundRamp.X0, -hd, groundRamp.X1, groundRamp.BandZ1));
        if (backed) LinkSpines(a, floor, regions, carStrip);

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

    /// <summary>
    /// Another wing joining this one off its back wall (#577): a corridor from the back landing (or
    /// the corridor off it) straight to that point, the regions it crosses cut either side of it and
    /// opening off it. The wing beyond meets it there.
    /// </summary>
    private static void LinkSpines(Apt a, FloorPlan floor, List<Region> regions, bool carStrip)
    {
        var rooms = floor.Rooms;
        float half = CorridorWidth / 2, archTop = Math.Min(2.3f, a.Clear - 0.2f);
        foreach (var (side, at) in a.Links)
        {
            if (side != Side.Back || a.Hd - a.ZB1 < 2.5f) continue;
            // what it starts from: whatever circulation ends on the back landing's line nearest
            // there, and it slides along to stand wholly in front of it (the wing joining this one
            // is planned after, and meets the corridor where it really is)
            int from = -1;
            float best = float.MaxValue;
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r.Type is not (RoomType.Corridor or RoomType.Landing) || Math.Abs(r.Z1 - a.ZB1) > 0.02f || r.X1 - r.X0 < CorridorWidth) continue;
                float d = Math.Max(0, Math.Max(r.X0 + half - at, at - (r.X1 - half)));
                if (d < best) { best = d; from = i; }
            }
            if (from < 0) continue;
            float x = Fit(at, rooms[from].X0 + half, rooms[from].X1 - half);
            // in the basement the car park already runs across the back
            if (carStrip) continue;
            int sp = Add(rooms, new RoomPlan { X0 = x - half, Z0 = a.ZB1, X1 = x + half, Z1 = a.Hd, Type = RoomType.Corridor });
            Opening(rooms, sp, Side.Front, from, x, CorridorWidth - 0.4f, archTop, OpeningKind.Arch);
            var cut = new List<Region>();
            foreach (var reg in regions)
            {
                var R = reg.R;
                if (R.X1 <= x - half + 0.01f || R.X0 >= x + half - 0.01f || R.Z1 <= a.ZB1 + 0.01f) { cut.Add(reg); continue; }
                // either side of the corridor: each part keeps the ways it still touches, or opens off it
                foreach (var (p0, p1, faces) in new[] { (R.X0, x - half, Side.Right), (x + half, R.X1, Side.Left) })
                {
                    if (p1 - p0 < 1.2f) continue;
                    var part = new RectPlan(p0, R.Z0, p1, R.Z1);
                    var keep = AlongX(reg.Side)
                        ? reg.Ways.Select(w => w with { Lo = Math.Max(w.Lo, p0), Hi = Math.Min(w.Hi, p1) }).Where(w => w.Hi - w.Lo >= WayMin).ToList()
                        : (reg.Side == Side.Left ? Math.Abs(p0 - R.X0) < 0.01f : Math.Abs(p1 - R.X1) < 0.01f) ? reg.Ways : new List<Way>();
                    cut.Add(keep.Count > 0
                        ? new Region(part, reg.Side, keep)
                        : new Region(part, faces, new List<Way> { new(sp, Math.Max(R.Z0, a.ZB1), R.Z1) }));
                }
            }
            regions.Clear();
            regions.AddRange(cut);
        }
    }

    /// <summary>
    /// The regions with a rectangle taken out of them (#694: the ramp's room on the ground floor): each region the cut
    /// touches becomes up to four pieces, each keeping the ways into it it still touches along its own wall (clipped
    /// to the piece), and dropped when none is left or it is too small to be a flat.
    /// </summary>
    private static List<Region> Carve(List<Region> regions, RectPlan cut)
    {
        var result = new List<Region>();
        foreach (var reg in regions)
        {
            var R = reg.R;
            if (R.X1 <= cut.X0 + 0.01f || R.X0 >= cut.X1 - 0.01f || R.Z1 <= cut.Z0 + 0.01f || R.Z0 >= cut.Z1 - 0.01f) { result.Add(reg); continue; }
            float cx0 = Math.Max(R.X0, cut.X0), cx1 = Math.Min(R.X1, cut.X1);
            var parts = new List<RectPlan>
            {
                new(R.X0, R.Z0, cx0, R.Z1), new(cx1, R.Z0, R.X1, R.Z1),
                new(cx0, R.Z0, cx1, cut.Z0), new(cx0, cut.Z1, cx1, R.Z1),
            };
            foreach (var p in parts)
            {
                if (p.X1 - p.X0 < 1.2f || p.Z1 - p.Z0 < 1.2f) continue;
                bool touches = reg.Side switch
                {
                    Side.Left => Math.Abs(p.X0 - R.X0) < 0.01f,
                    Side.Right => Math.Abs(p.X1 - R.X1) < 0.01f,
                    Side.Front => Math.Abs(p.Z0 - R.Z0) < 0.01f,
                    _ => Math.Abs(p.Z1 - R.Z1) < 0.01f,
                };
                if (!touches) continue;
                bool alongX = AlongX(reg.Side);
                float lo = alongX ? p.X0 : p.Z0, hi = alongX ? p.X1 : p.Z1;
                var ways = reg.Ways.Select(w => w with { Lo = Math.Max(w.Lo, lo), Hi = Math.Min(w.Hi, hi) }).Where(w => w.Hi - w.Lo >= WayMin).ToList();
                if (ways.Count > 0) result.Add(new Region(p, reg.Side, ways));
            }
        }
        return result;
    }

    /// <summary>
    /// The basement under the gap that holds the along-the-facade ramp's band (#694): the car park hall from where the
    /// ramp's room ends to the first stairwell's wall, the whole depth of the block, joined to the ramp room by a lane-wide
    /// arch and to the stairwell by doors; and behind the ramp room a region for the storerooms, reached through the hall.
    /// </summary>
    private static void AlongBasement(Apt a, FloorPlan floor, List<Region> regions, RampColumn r, float g0, float g1, Well? left, Well? right)
    {
        var rooms = floor.Rooms;
        float hd = a.Hd, top = Math.Min(2.05f, a.Clear - 0.2f);
        bool pos = r.Dir > 0;
        float c0 = pos ? r.ParkX : r.WellEdge, c1 = pos ? r.WellEdge : r.ParkX;
        int park = Add(rooms, new RoomPlan { X0 = c0, Z0 = -hd, X1 = c1, Z1 = hd, Type = RoomType.CarPark });
        if (r.BasementRoom >= 0)
            Opening(rooms, park, pos ? Side.Left : Side.Right, r.BasementRoom, (r.LaneZ0 + r.LaneZ1) / 2, GarageRule.RampWidth - 0.2f, a.Clear, OpeningKind.Arch);
        // a door to the stairwell the hall ends at, at its front landing (one, so the wall keeps room for bays)
        var well = pos ? right : left;
        if (well != null)
        {
            var side = pos ? Side.Right : Side.Left;
            int front = pos || well.Front < 0 ? well.Core : well.Front;
            Opening(rooms, park, side, front, (-hd + a.RunZ0) / 2, 1.2f, top, OpeningKind.Door);
        }
        // the storerooms behind the ramp room and beside the hall's start, off the hall
        float s0 = pos ? g0 : r.ParkX, s1 = pos ? r.ParkX : g1;
        if (s1 - s0 >= 2.4f && hd - r.BandZ1 >= 2.4f)
            regions.Add(new Region(new RectPlan(s0, r.BandZ1, s1, hd), pos ? Side.Right : Side.Left, new List<Way> { new(park, r.BandZ1 + 0.15f, hd - 0.15f) }));
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

    /// <summary>Where a region left of a stairwell may open a door: the front and the back landing, not the stair's wall.</summary>
    private static List<Way> LeftWays(Apt a, Well w) => !a.Stairs
        ? new List<Way> { new(w.Core, -a.Hd, a.ZB1) }
        : a.Passage
            ? new List<Way> { new(w.Core, -a.Hd, a.RunZ0), new(w.Back, a.ZM, a.ZB1) }
            : new List<Way> { new(w.Core, -a.Hd, a.RunZ0) };

    /// <summary>Right of a stairwell: the lobby in front of the elevator, or the passage beside the stair, and the back landing.</summary>
    private static List<Way> RightWays(Apt a, Well w) => a.Lift
        ? a.Passage
            ? new List<Way> { new(w.Front, -a.Hd, a.ZM - CabinDepth), new(w.Back, a.ZM, a.ZB1) }
            : new List<Way> { new(w.Front, -a.Hd, a.ZM - CabinDepth) }
        : !a.Stairs
            ? new List<Way> { new(w.Core, -a.Hd, a.ZB1) }
            : a.Passage
                ? new List<Way> { new(w.Core, -a.Hd, a.RunZ0), new(w.Passage, a.RunZ0, a.ZM), new(w.Back, a.ZM, a.ZB1) }
                : new List<Way> { new(w.Core, -a.Hd, a.RunZ0) };

    /// <summary>
    /// A block of flats' stair (#571): a storey in two flights round a half landing. Up from the
    /// front landing in lane A, half a storey, onto the half landing at the back; turn; up lane B
    /// to the next floor's front landing. A parapet with a handrail along the open well between
    /// the lanes; on the floors above the bottom the shaft is open (no slab), and on the top floor,
    /// where nothing climbs on, a rail across lane A's mouth. The first stairwell's first flight is
    /// the floor's <see cref="FloorPlan.Flight"/>, everything else is in <see cref="FloorPlan.Flights"/>.
    /// </summary>
    private static void Stair(FloorPlan floor, Apt a, int f, float c0)
    {
        float laneA0 = c0, laneA1 = c0 + StairLane, laneB0 = laneA1 + StairEye, laneB1 = c0 + StairWidth;
        if (f < a.Floors - 1)
        {
            var up = new FlightPlan { X0 = laneA0, X1 = laneA1, ZBottom = a.RunZ0, ZTop = a.RunZ1, From = 0, To = 0.5f, Parapet = +1 };
            var on = new FlightPlan { X0 = laneB0, X1 = laneB1, ZBottom = a.RunZ1, ZTop = a.RunZ0, From = 0.5f, To = 1, Parapet = -1 };
            if (floor.Flight == null) floor.Flight = up; else floor.Flights.Add(up);
            floor.Flights.Add(on);
            floor.Landings.Add(new LandingPlan { X0 = laneA0, Z0 = a.RunZ1, X1 = laneB1, Z1 = a.ZM, Level = 0.5f });
        }
        // the stair's side on the passage is an open railing, not a wall (#680)
        if (a.Passage) floor.Guards.Add(new RectPlan(laneB1, a.RunZ0, laneB1, a.ZM));
        if (f > 0)
        {
            floor.Holes.Add(new RectPlan(laneA0, a.RunZ0, laneB1, a.ZM));
            if (f == a.Floors - 1) floor.Rails.Add(new RectPlan(laneA0, a.RunZ0, laneB0, a.RunZ0));
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
        // a deep region whose only facade is across from its way: each flat needs a living room
        // and a bedroom side by side on that facade (#571), so they are cut no narrower
        bool farFacade = reg.Side switch
        {
            Side.Left => R.X1 >= a.Hw - 0.02f && a.Facade(Side.Right, R.Z0, R.Z1),
            Side.Right => R.X0 <= -a.Hw + 0.02f && a.Facade(Side.Left, R.Z0, R.Z1),
            Side.Front => R.Z1 >= a.Hd - 0.02f && a.Facade(Side.Back, R.X0, R.X1),
            _ => R.Z0 <= -a.Hd + 0.02f && a.Facade(Side.Front, R.X0, R.X1),
        };
        float least = farFacade && depth >= 9f ? StripMin(RoomType.Living) + StripMin(RoomType.Bedroom) + 0.4f : MinFlatSide;
        int want = Math.Clamp((int)MathF.Round(len * depth / target), 1, Math.Max(1, (int)(len / least)));
        // every flat keeps facade enough for a lit living room and bedroom (#577: in a wing, a
        // stretch of the region's far wall may be the next wing's); fewer, wider flats where a cut
        // would leave one short, else at least some facade each
        float Lit(List<float> c)
        {
            float least = float.MaxValue;
            for (int k = 0; k + 1 < c.Count; k++)
            {
                var piece = alongX ? new RectPlan(c[k], R.Z0, c[k + 1], R.Z1) : new RectPlan(R.X0, c[k], R.X1, c[k + 1]);
                least = Math.Min(least, FlatExt(a, piece, reg.Side).Total(piece.X1 - piece.X0, piece.Z1 - piece.Z0));
            }
            return least;
        }
        List<float>? cuts = null, some = null, any = null;
        for (int n = want; n >= 1 && cuts == null; n--)
        {
            var c = Cuts(reg.Ways, lo, hi, n, least);
            if (c == null) continue;
            any ??= c;
            float lit = Lit(c);
            if (lit >= TwoRooms) cuts = c;
            else if (lit >= WindowWall) some ??= c;
        }
        cuts ??= some ?? any;
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
    private static List<float>? Cuts(List<Way> ways, float lo, float hi, int n, float least = MinFlatSide)
    {
        bool Ok(List<float> c)
        {
            for (int k = 0; k + 1 < c.Count; k++)
                if (c[k + 1] - c[k] < least - 0.01f && n > 1 || !ways.Any(w => Overlap(w, c[k], c[k + 1]) >= WayMin))
                    return false;
            return true;
        }
        var even = Enumerable.Range(0, n + 1).Select(k => lo + (hi - lo) * k / n).ToList();
        if (Ok(even)) return even;
        if (n != 2) return null;
        float mid = (lo + hi) / 2;
        for (float d = 0.25f; d < (hi - lo) / 2 - least + MinFlatSide; d += 0.25f)
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

    /// <summary>Facade a flat needs for a living room and a bedroom each with a window, m.</summary>
    private static readonly float TwoRooms = StripMin(RoomType.Living) + StripMin(RoomType.Bedroom);

    /// <summary>
    /// Which walls of a flat are facades, in its own frame: its u = 0 end, its u = u end, its far
    /// wall; and how much of a stretch of one of them faces out (#577: part of a wall may be the
    /// next wing's), <paramref name="Free"/>(0, 1 or 2 for those, from, to).
    /// </summary>
    internal sealed record Ext(bool U0, bool U1, bool Far, Func<int, float, float, float> Free)
    {
        public static Ext Plain(bool u0, bool u1, bool far) => new(u0, u1, far, (_, a, b) => b - a);

        /// <summary>How much of a u x v flat's walls faces out, m.</summary>
        public float Total(float u, float v) => (U0 ? Free(0, 0, v) : 0) + (U1 ? Free(1, 0, v) : 0) + (Far ? Free(2, 0, u) : 0);
    }

    /// <summary>A room in the flat's own frame: u along the wall with the front door, v away from it.</summary>
    internal sealed record Local(RoomType Type, float U0, float V0, float U1, float V1);

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
    internal static List<Local> FlatRooms(float u, float v, float door, Ext ext, Random rng)
    {
        var program = FlatProgram(u * v, rng);
        // no more bedrooms than its facades can give a window each, beside the living room's (#571):
        // a deep flat lit only at its far end is a big flat with few rooms and a dark middle
        // (only the wall that really faces out: #577, part of one may be the next wing's)
        float facade = ext.Total(u, v);
        int beds = Math.Max(1, (int)((facade - 3.5f) / 2.8f));
        while (program.Count(p => p.Type == RoomType.Bedroom) > beds)
            program.Remove(program.Last(p => p.Type == RoomType.Bedroom));
        // too little facade for a living room and a bedroom both lit: one room for everything
        if (u * v < 38f || facade < TwoRooms) return Daylight(StudioFlat(u, v, door), u, v, ext);

        // every layout the shape allows, the house's usual one first; the one that leaves fewest
        // living rooms and bedrooms dark wins (#571), ties to the earlier
        var candidates = new List<(List<Local> Rooms, float Penalty)>();
        void Try(List<Local>? rooms, float penalty)
        {
            if (rooms != null) candidates.Add((rooms, penalty));
        }
        // each layout also mirrored end to end, its door and facades with it: where only part of
        // the far wall faces out (the next wing stands against the rest, #577), the mirror may put
        // the living room and the bedroom on the open part
        var mirror = new Ext(ext.U1, ext.U0, ext.Far, (which, a, b) =>
            which == 2 ? ext.Free(2, u - b, u - a) : ext.Free(1 - which, a, b));
        foreach (var (e, d, flip) in new[] { (ext, door, false), (mirror, u - door, true) })
        {
            if (u < 4.4f) break;
            List<Local>? M(List<Local>? rooms) =>
                flip ? rooms?.Select(r => r with { U0 = u - r.U1, U1 = u - r.U0 }).ToList() : rooms;
            float f = flip ? 0.05f : 0;
            // as wide as it is deep: a hall across the middle, wet rooms between it and the landing
            if (v >= 6.4f && u >= 6.0f && u >= 0.7f * v) Try(M(TFlat(u, v, d, e, program)), f);
            // deeper than it is wide: a hall straight in, rooms either side, living room at the far end
            Try(M(SpineFlat(u, v, d, e, program)), 0.5f + f);
            // shallow, or with its facades at the two ends of its door wall: a hall along the door wall
            if (v >= 3.6f) Try(M(GalleryFlat(u, v, d, e, program)), (v < 6.5f || !e.Far ? 0.2f : 1f) + f);
        }
        // a chain of rooms walked through, and one room for everything, only when nothing else fits
        Try(LinearFlat(u, v, program), u < 4.4f ? 0 : 8);
        Try(StudioFlat(u, v, door), u * v < 50 ? 4 : 25);
        var best = candidates.MinBy(c => Score(c.Rooms, u, v, ext, program.Count) + c.Penalty);
        return Daylight(best.Rooms, u, v, ext);
    }

    /// <summary>
    /// How much is wrong with a layout: no bed, kitchen or bathroom; a living room or a bedroom
    /// with no facade to put a window in (#571); rooms of the program it had to leave out.
    /// </summary>
    private static float Score(List<Local> rooms, float u, float v, Ext ext, int wanted)
    {
        float score = 0;
        if (!rooms.Any(r => r.Type == RoomType.Bedroom) || !rooms.Any(r => r.Type == RoomType.Kitchen)
            || !rooms.Any(r => r.Type == RoomType.Bathroom)) score += 1000;
        // a room only reached through a bedroom, a bathroom or a WC (#576)
        if (!Reachable(rooms)) score += 500;
        foreach (var r in rooms)
        {
            if (Lit(r, u, v, ext)) continue;
            if (r.Type == RoomType.Living) score += 30;
            else if (r.Type == RoomType.Bedroom) score += 12;
        }
        score += 3 * Math.Max(0, wanted - (rooms.Count - rooms.Count(r => r.Type == RoomType.Hall)));
        // and rooms shaped like rooms: nothing much longer than it is wide, nothing a bowling lane
        foreach (var r in rooms)
        {
            if (r.Type == RoomType.Hall) continue;
            float a = r.U1 - r.U0, b = r.V1 - r.V0, aspect = Math.Max(a, b) / Math.Max(0.1f, Math.Min(a, b));
            if (aspect > 2.6f) score += (aspect - 2.6f) * 4;
            if (Math.Max(a, b) > 8f && r.Type != RoomType.Living) score += (Math.Max(a, b) - 8f) * 2;
        }
        return score;
    }

    /// <summary>The least a studio's kitchenette is deep, m (the validator's floor for a room is 1.0).</summary>
    private const float MinKitchenette = 1.2f;

    /// <summary>A window fits in a wall this long (<see cref="AddWindows"/>: 1.1 m and 0.4 m either side).</summary>
    private const float WindowWall = 1.9f;

    /// <summary>
    /// Whether a room of a flat has a facade long enough for a window: its u = 0 or u = u end, or
    /// the far wall, whichever of those are the building's outside (the door wall never is).
    /// </summary>
    private static bool Lit(Local r, float u, float v, Ext ext) =>
        ext.U0 && r.U0 <= 0.01f && ext.Free(0, r.V0, r.V1) >= WindowWall
        || ext.U1 && r.U1 >= u - 0.01f && ext.Free(1, r.V0, r.V1) >= WindowWall
        || ext.Far && r.V1 >= v - 0.01f && ext.Free(2, r.U0, r.U1) >= WindowWall;

    /// <summary>
    /// The last say on daylight (#571): a living room or a bedroom left with no facade trades
    /// places with a room that can do without one (bathroom, WC, box room, kitchen, study) and is
    /// on a facade, where each fits the other's place; a spare bedroom nothing will trade with is
    /// a box room. A kitchen, a bathroom or a WC keeps a window only if it happens to have one.
    /// </summary>
    private static List<Local> Daylight(List<Local> rooms, float u, float v, Ext ext)
    {
        var list = new List<Local>(rooms);
        static float Least(Local r) => Math.Min(r.U1 - r.U0, r.V1 - r.V0);
        foreach (var need in new[] { RoomType.Living, RoomType.Bedroom })
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Type != need || Lit(list[i], u, v, ext)) continue;
                int swap = -1;
                float bestArea = 0;
                for (int j = 0; j < list.Count; j++)
                {
                    var c = list[j];
                    if (c.Type is not (RoomType.Bathroom or RoomType.WC or RoomType.Storage or RoomType.Kitchen or RoomType.Study)
                        || !Lit(c, u, v, ext)) continue;
                    if (Least(c) < StripMin(need) || Least(list[i]) < StripMin(c.Type)) continue;
                    float area = (c.U1 - c.U0) * (c.V1 - c.V0);
                    if (area > bestArea) { bestArea = area; swap = j; }
                }
                if (swap >= 0)
                {
                    var t = list[swap].Type;
                    var (was0, was1) = (list[swap], list[i]);
                    list[swap] = list[swap] with { Type = need };
                    list[i] = list[i] with { Type = t };
                    // not if it leaves a room only reached through the bedroom (#576)
                    if (Reachable(rooms) && !Reachable(list)) (list[swap], list[i]) = (was0, was1);
                }
                else if (need == RoomType.Bedroom && list.Count(r => r.Type == RoomType.Bedroom) > 1)
                    list[i] = list[i] with { Type = RoomType.Storage };
            }
        return list;
    }

    /// <summary>
    /// A hall straight in from the front door, rooms stacked either side of it, the living room
    /// and the kitchen across the far facade (a bedroom too if it is wide). Null if the program
    /// does not fit.
    /// </summary>
    private static List<Local>? SpineFlat(float u, float v, float door, Ext ext, List<FlatItem> program)
    {
        var dropped = new List<FlatItem>();
        var rooms = new List<Local>();
        float h0 = Fit(door - HallWidth / 2, 0, u - HallWidth), h1 = h0 + HallWidth;
        if (h0 > 0 && h0 < 2.3f) h0 = 0;
        if (u - h1 > 0 && u - h1 < 2.3f) h1 = u;
        bool sides = h0 > 0 || h1 < u;
        if (!sides) return null;

        // the far facade: living room across the hall's end, the kitchen beside it, a bedroom if wide
        bool far = v >= 7.0f;
        float lz = far ? Math.Clamp(v * 0.42f, 3.4f, 6.0f) : 0;
        if (v - lz < 2.4f) { far = false; lz = 0; }
        float sv = v - lz;
        var farItems = new List<FlatItem>();
        if (far)
        {
            farItems.Add(program.First(p => p.Type == RoomType.Living));
            if (!ext.U0 && !ext.U1)
            {
                // the far wall is its only facade: the bedrooms share it with the living room,
                // as many as fit, and the kitchen does without a window (#571)
                int beds = (int)((u - StripMin(RoomType.Living)) / StripMin(RoomType.Bedroom));
                farItems.AddRange(program.Where(p => p.Type == RoomType.Bedroom).Take(beds));
            }
            else
            {
                if (u >= 6.8f) farItems.Add(program.First(p => p.Type == RoomType.Kitchen));
                if (u >= 11f && program.Count(p => p.Type == RoomType.Bedroom) >= 2)
                    farItems.Add(program.Last(p => p.Type == RoomType.Bedroom));
            }
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
        return complete ? rooms : null;
    }

    /// <summary>
    /// A hall along the door wall and the rooms side by side behind it, each off it. With its
    /// far wall a facade, every room has a window, so the wet rooms go nearest the door and the
    /// living room furthest. Without (a flat between two stairwells, windows only at the two ends):
    /// the living room and the kitchen at one facade, the main bedroom at the other, the bathroom,
    /// the WC and the box room in the dark middle. Null if the rooms do not fit.
    /// </summary>
    private static List<Local>? GalleryFlat(float u, float v, float door, Ext ext, List<FlatItem> program)
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
    internal static List<Local> StudioFlat(float u, float v, float door)
    {
        float band = Math.Min(2.3f, v * 0.4f);
        float h0 = Fit(door - HallWidth / 2, 0, u - HallWidth), h1 = h0 + HallWidth;
        var rooms = new List<Local>();
        // the kitchenette of the stacked layout below is 35 % of what the band leaves: under 4.8 m deep that is under a metre
        // (0.98 m at 4.65, a validator reject in 12 of the first garage blocks, #694), so a flat that shallow takes the shallow layout
        float left0 = h0, right0 = u - h1;
        bool sideBySide = left0 >= 1.7f && right0 >= 1.9f || right0 >= 1.7f && left0 >= 1.9f;
        if (v < 4.6f || !sideBySide && (v - band) * 0.35f < MinKitchenette)
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

    /// <summary>
    /// A hall across the flat, a short way in from its front door: between the hall and the
    /// landing wall a band of the rooms that need no window (bathroom, WC, box room) either side
    /// of the entrance; beyond the hall, along the facades, the living room with the kitchen
    /// beside it and the bedrooms. Every room opens off the hall. Null if it does not fit.
    /// </summary>
    private static List<Local>? TFlat(float u, float v, float door, Ext ext, List<FlatItem> program)
    {
        const float hall = 1.2f;
        float band = v >= 8f ? 2.6f : 2.3f;
        float h0 = Fit(door - HallWidth / 2, 0, u - HallWidth), h1 = h0 + HallWidth;
        // a sliver beside the entrance is the entrance's own
        if (h0 < 1.1f) h0 = 0;
        if (u - h1 < 1.1f) h1 = u;
        var rooms = new List<Local> { new(RoomType.Hall, h0, 0, h1, band), new(RoomType.Hall, 0, band, u, band + hall) };

        var wet = program.Where(p => p.Type is RoomType.Bathroom or RoomType.WC or RoomType.Storage).ToList();
        var dry = program.Where(p => p.Type is not (RoomType.Bathroom or RoomType.WC or RoomType.Storage)).ToList();
        // the band either side of the entrance, the bathroom on the wider side
        var zones = new List<(float A, float B)>();
        if (h0 > 0) zones.Add((0, h0));
        if (h1 < u) zones.Add((h1, u));
        zones = zones.OrderByDescending(z => z.B - z.A).ToList();
        var lost = new List<FlatItem>();
        var shares = zones.Select(_ => new List<FlatItem>()).ToList();
        foreach (var it in wet.OrderByDescending(i => i.Keep))
        {
            int best = -1;
            float fill = float.MaxValue;
            for (int z = 0; z < zones.Count; z++)
            {
                float need = shares[z].Sum(i => StripMin(i.Type)) + StripMin(it.Type);
                float len = zones[z].B - zones[z].A;
                if (need > len || need / len >= fill) continue;
                fill = need / len;
                best = z;
            }
            if (best < 0) lost.Add(it);
            else shares[best].Add(it);
        }
        if (lost.Any(i => i.Type == RoomType.Bathroom)) return null;
        for (int z = 0; z < zones.Count; z++)
        {
            // a stretch with nothing for it is a cupboard
            if (shares[z].Count == 0) shares[z].Add(new FlatItem(RoomType.Storage, 1f, 0));
            foreach (var (it, a0, a1) in Strip(zones[z].A, zones[z].B, shares[z], lost))
                rooms.Add(new Local(it.Type, a0, 0, a1, band));
        }

        // the facade side: the living room at a facade end (away from the door if both are), the
        // kitchen beside it, the bedrooms on from there
        var kitchenLiving = dry.Where(p => p.Type is RoomType.Kitchen or RoomType.Living).OrderBy(p => p.Type == RoomType.Living);
        var ordered = dry.Where(p => p.Type is not (RoomType.Kitchen or RoomType.Living)).Concat(kitchenLiving).ToList();
        bool livingAtU = ext.U0 && ext.U1 ? door < u / 2 : ext.U1 || !ext.U0 && door < u / 2;
        foreach (var (it, a0, a1) in Strip(0, u, ordered, lost))
            rooms.Add(livingAtU
                ? new Local(it.Type, a0, band + hall, a1, v)
                : new Local(it.Type, u - a1, band + hall, u - a0, v));
        bool complete = rooms.Any(r => r.Type == RoomType.Bedroom) && rooms.Any(r => r.Type == RoomType.Kitchen)
            && rooms.Any(r => r.Type == RoomType.Bathroom);
        return complete ? rooms : null;
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
        var ext = FlatExt(a, piece, side);
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
    /// Which walls of a flat cut from <paramref name="piece"/>, its door on <paramref name="side"/>,
    /// are facades, in its own frame (<see cref="Ext"/>).
    /// </summary>
    private static Ext FlatExt(Apt a, RectPlan piece, Side side)
    {
        // which of the flat's walls are facades, in its own frame, and how much of a stretch of
        // each faces out: the ends of the door wall and the far wall, as sides of the block
        bool exL = piece.X0 <= -a.Hw + 0.02f && a.Facade(Side.Left, piece.Z0, piece.Z1);
        bool exR = piece.X1 >= a.Hw - 0.02f && a.Facade(Side.Right, piece.Z0, piece.Z1);
        bool exF = piece.Z0 <= -a.Hd + 0.02f && a.Facade(Side.Front, piece.X0, piece.X1);
        bool exB = piece.Z1 >= a.Hd - 0.02f && a.Facade(Side.Back, piece.X0, piece.X1);
        // (u0 end, u1 end, far wall) as block sides, and a local stretch as the block's
        var (s0, s1, s2) = side switch
        {
            Side.Front => (Side.Left, Side.Right, Side.Back),
            Side.Back => (Side.Left, Side.Right, Side.Front),
            Side.Left => (Side.Front, Side.Back, Side.Right),
            _ => (Side.Front, Side.Back, Side.Left),
        };
        (float, float) Span(int which, float p, float q) => (side, which) switch
        {
            // the ends run along v, the far wall along u
            (Side.Front, < 2) => (piece.Z0 + p, piece.Z0 + q),
            (Side.Back, < 2) => (piece.Z1 - q, piece.Z1 - p),
            (Side.Left, < 2) => (piece.X0 + p, piece.X0 + q),
            (Side.Right, < 2) => (piece.X1 - q, piece.X1 - p),
            (Side.Front or Side.Back, _) => (piece.X0 + p, piece.X0 + q),
            _ => (piece.Z0 + p, piece.Z0 + q),
        };
        return new Ext(
            side switch { Side.Front or Side.Back => exL, _ => exF },
            side switch { Side.Front or Side.Back => exR, _ => exB },
            side switch { Side.Front => exB, Side.Back => exF, Side.Left => exR, _ => exL },
            (which, p, q) =>
            {
                var (lo, hi) = Span(which, p, q);
                return a.Free(which == 0 ? s0 : which == 1 ? s1 : s2, lo, hi);
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
            // rooms open only off a hall, a living room or a kitchen (#576): a bedroom, a bathroom or
            // a WC has the one door. Only a plan with no other way in (the scoring keeps clear of
            // those) lets one be walked through.
            for (int pass = 0; pass < 2 && best == null; pass++)
            foreach (int i in inTree)
                for (int j = first; j < end; j++)
                {
                    if (inTree.Contains(j) || Touching(rooms[i], rooms[j]) is not { } t) continue;
                    var rf = rooms[i].Type;
                    var rt = rooms[j].Type;
                    if (pass == 0 && i != root && !Connector(rf)) continue;
                    // a flat's hall, the entrance and any hall off it, is where rooms open from
                    int score = i == root || rf == RoomType.Hall ? 40 + (rt is RoomType.Living or RoomType.Hall ? 5 : 0)
                        : rf == RoomType.Living && rt == RoomType.Kitchen || rf == RoomType.Kitchen && rt == RoomType.Living ? 35
                        : rf == RoomType.Living ? rt is RoomType.Study ? 10 : rt is RoomType.Bedroom ? 6 : 2
                        : 0;
                    if (rt is RoomType.Bathroom or RoomType.WC && rf is RoomType.Living or RoomType.Kitchen) score -= 8;
                    score += (int)Math.Min(t.S1 - t.S0, 4f);
                    if (score > bestScore) { bestScore = score; best = (i, j, t.Side, t.S0, t.S1); }
                }
            if (best is not { } e) return; // cannot happen on a cut rectangle; the validator would say so
            inTree.Add(e.B);
            var ta = rooms[e.A].Type;
            var tb = rooms[e.B].Type;
            // the entrance runs into the hall across the flat with no door between them
            bool open = (ta, tb) is (RoomType.Hall, RoomType.Hall) && e.S1 - e.S0 >= 1.2f
                || (ta, tb) is (RoomType.Living, RoomType.Kitchen) or (RoomType.Kitchen, RoomType.Living)
                && e.S1 - e.S0 >= 2.0f && rng.NextDouble() < 0.5;
            if (open)
            {
                float w = Math.Min(e.S1 - e.S0 - 0.4f, 2.6f);
                Opening(rooms, e.A, e.S, e.B, (e.S0 + e.S1) / 2, w, Math.Min(2.2f, clear - 0.2f), OpeningKind.Arch);
            }
            else AddDoor(rooms, new Edge(e.A, e.B, e.S, e.S0, e.S1), clear);
        }
    }

    /// <summary>
    /// The rooms of a flat that other rooms may open off (#576): its halls, the living room, the
    /// kitchen, a dining room, a shop's sales floor. A bedroom, a bathroom, a WC, a box room or a
    /// study is a dead end, with one door.
    /// </summary>
    private static bool Connector(RoomType t) =>
        t is RoomType.Hall or RoomType.Living or RoomType.Kitchen or RoomType.Dining or RoomType.Shop;

    /// <summary>
    /// Whether every room of a flat laid out in its own frame can be reached from its entrance hall
    /// (index 0) going only through <see cref="Connector"/> rooms, as <see cref="ConnectFlat"/> will
    /// open them.
    /// </summary>
    private static bool Reachable(List<Local> rooms)
    {
        const float eps = 0.02f, need = InnerDoor + 0.3f;
        static bool Touch(Local a, Local b, float eps, float need) =>
            (Math.Abs(a.U1 - b.U0) < eps || Math.Abs(b.U1 - a.U0) < eps) && Math.Min(a.V1, b.V1) - Math.Max(a.V0, b.V0) >= need
            || (Math.Abs(a.V1 - b.V0) < eps || Math.Abs(b.V1 - a.V0) < eps) && Math.Min(a.U1, b.U1) - Math.Max(a.U0, b.U0) >= need;
        var seen = new bool[rooms.Count];
        var queue = new Queue<int>();
        seen[0] = true;
        queue.Enqueue(0);
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            if (i != 0 && !Connector(rooms[i].Type)) continue;
            for (int j = 0; j < rooms.Count; j++)
                if (!seen[j] && Touch(rooms[i], rooms[j], eps, need)) { seen[j] = true; queue.Enqueue(j); }
        }
        return seen.All(x => x);
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
        int first = floor.Rooms.Count;
        foreach (var (it, z0, z1) in Strip(R.Z0, R.Z1, items, new List<FlatItem>()))
        {
            // the stockroom and the WC side by side behind the sales floor, each off it (#576)
            if (it.Type == RoomType.Storage && R.Z1 - R.Z0 >= 10f && R.X1 - R.X0 >= 4.5f)
            {
                float wc = Math.Min(1.6f, (R.X1 - R.X0) * 0.35f);
                floor.Rooms.Add(new RoomPlan { X0 = R.X0, Z0 = z0, X1 = R.X1 - wc, Z1 = z1, Type = RoomType.Storage, Unit = unit });
                floor.Rooms.Add(new RoomPlan { X0 = R.X1 - wc, Z0 = z0, X1 = R.X1, Z1 = z1, Type = RoomType.WC, Unit = unit });
                continue;
            }
            floor.Rooms.Add(new RoomPlan { X0 = R.X0, Z0 = z0, X1 = R.X1, Z1 = z1, Type = it.Type, Unit = unit });
        }
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
            // the garage ramp (#558) runs on into the car park: a lane-wide arch from its basement room
            if (a.Ramp is { BasementRoom: >= 0 } ramp)
                Opening(rooms, park, Side.Front, ramp.BasementRoom, ramp.Center, GarageRule.RampWidth - 0.3f, a.Clear, OpeningKind.Arch);
            // a door from every circulation room along its front: the stairwells meet in it too
            for (int i = 0; i < park; i++)
            {
                var r = rooms[i];
                if (r.Type is not (RoomType.Corridor or RoomType.Landing or RoomType.Lobby) || Math.Abs(r.Z1 - a.ZB1) > 0.02f) continue;
                if (r.X1 - r.X0 < WayMin) continue;
                Opening(rooms, park, Side.Front, i, (r.X0 + r.X1) / 2, 1.2f, top, OpeningKind.Door);
            }
        }
        else if (a.L.Width * a.L.Depth >= 300f && a.Ramp is not { Along: true })
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
            // the underground garage's door (#558) opens into the ground floor room over its ramp. A
            // block with no ramp for it (the lane would not clear a stairwell, a wing, a short block)
            // gives it no doorway: EntranceOf is null and it reads as locked, never a portal onto a stairwell
            RampColumn? serve = d.Vehicle && a.Ramp is { GroundRoom: >= 0 } rc && rc.Slot == d.Slot ? rc : null;
            if (d.Vehicle && serve == null) continue;
            float width = serve != null ? d.Width : Math.Min(d.Width, 1.8f);
            float height = Math.Min(d.Height, clear - 0.15f);
            var well = a.Wells.FirstOrDefault(w => w.Slot == d.Slot);
            bool main = d.Slot == fp.Door.Slot;
            RoomPlan? room = null;
            Side side = Side.Front;
            float center = 0;
            if (serve != null)
            {
                room = ground.Rooms[serve.GroundRoom];
                var gx = new Godot.Vector2(d.Position.X - fp.Center.X, d.Position.Z - fp.Center.Y).Dot(fp.AxisU);
                width = Math.Min(width, room.X1 - room.X0 - 0.5f);
                center = Fit(gx, room.X0 + width / 2 + 0.2f, room.X1 - width / 2 - 0.2f);
                room.Openings.Add(new OpeningPlan { Side = Side.Front, Center = center, Width = width, Top = height, Kind = OpeningKind.Entry });
            }
            else if (well != null)
            {
                // the lobby's front wall: the core's, or the bit before the elevator
                var core = ground.Rooms[well.Core];
                var front = well.Front >= 0 ? ground.Rooms[well.Front] : null;
                float want = main ? fp.EntryX : well.DoorX;
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
            if (main)
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
        // (a wing of a bigger building keeps it: its door may be a link, #577)
        if (l.Entrances.Count == 1 && fp.Extra.Count == 0 && fp.Door.Slot == 0) l.Entrances.Clear();
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
        RoomType.Landing or RoomType.Corridor or RoomType.Elevator or RoomType.Stairwell or RoomType.Ramp => Array.Empty<Piece>(),
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
        // the wall the cars come in by: where the ramp arrives (#694: a hall at the foot of a ramp along the facade), else its first door
        var arch = r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Arch && o.Other >= 0 && l.Floors[f].Rooms[o.Other].Type == RoomType.Ramp);
        var doorSide = arch?.Side ?? r.Openings.FirstOrDefault(o => o.Kind == OpeningKind.Door)?.Side ?? Side.Front;
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
