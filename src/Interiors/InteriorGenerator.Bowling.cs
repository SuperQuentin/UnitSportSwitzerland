using System.Runtime.CompilerServices;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Bowling centres (#1235, part 2 of #1232): what is inside the solids <see cref="Venues"/> names
/// <c>leisure=bowling_alley</c>. Numbers are in <c>docs/notes/terrain/venue-bowling.md</c>.
///
/// <para>
/// Planned in a lane frame: <c>u</c> across the lanes, <c>v</c> from the entrance end to the pins,
/// mapped onto the plan's X/Z whichever way the building lies (<see cref="BowlFrame"/>): lanes run
/// along the long axis, the concourse at the end nearer the front door. Front to back: the concourse
/// (shoe counter, ball racks, the games, the bar when there is no mezzanine) with a column of WCs and
/// a store at its far end; the lane hall (a settee bay per pair, then the pairs of lanes themselves,
/// their pin decks, masking units and pinsetters); in a solid far longer or wider than that (a mall)
/// a plain hall beyond. With two storeys of height the lane hall rises through both and a mezzanine
/// over the concourse holds the bar and the pool tables behind a parapet over the lanes, up a
/// straight stair. Each pair of lanes is one piece with a stable index, which is what bowling for
/// real (the next epic) will hang its state on.
/// </para>
/// <para>
/// A building too short for even mini lanes is not planned here: the caller's ordinary rules furnish
/// it. Every plan is validated before it is kept; a two-level plan that fails is planned again on one
/// level, and one that still fails gives the building back too.
/// </para>
/// </summary>
public static partial class InteriorGenerator
{
    /// <summary>Bumped when these rules change, so stored bowling centres are planned again.</summary>
    public const int BowlingRules = 4;

    /// <summary>One pair of lanes across, capping boards included [S: 11 ft 6 in].</summary>
    public const float PairPitch = 3.50f;
    /// <summary>The walkway beside the outer lanes: past the 0.6 m a doorway in the side wall keeps clear (#498's extra doors).</summary>
    private const float LaneSideWalk = 0.8f;
    /// <summary>At most this many pairs: 26 lanes, the biggest Swiss centre (La Praille).</summary>
    private const int MaxPairs = 13;
    private const float ConcourseMin = 4.6f, ConcourseMax = 16f;
    /// <summary>A solid this much wider than its lanes need gives the rest to a plain hall (a mall).</summary>
    private const float GamesSideMax = 12f;
    /// <summary>A settee bay keeps this far off the arch from the concourse: a doorway is walked through.</summary>
    private const float ArchClear = 0.7f;

    /// <summary>
    /// A lane block: its length from the approach to the back of the service aisle, the parts of it,
    /// and the settee bay in front. Full lanes are regulation; mini lanes are what a bowl bar has.
    /// </summary>
    public sealed record LaneSpec(float Block, float Approach, float Lane, float Settee, bool Mini)
    {
        /// <summary>The pinsetter and its service aisle: what is left of the block behind the lane.</summary>
        public float Machine => Block - Approach - Lane;
    }

    /// <summary>Approach 4.6 m, lane to the pit end 19.2 m, pinsetter and aisle 3.2 m: 27 m [S].</summary>
    public static readonly LaneSpec FullLanes = new(27.0f, 4.6f, 19.2f, 3.4f, false);
    /// <summary>Mini bowling: about half the lane, a string pinsetter.</summary>
    public static readonly LaneSpec MiniLanes = new(13.2f, 2.6f, 9.4f, 2.8f, true);

    /// <summary>
    /// The lane frame: <c>u</c> across the lanes (−Across/2..Across/2), <c>v</c> along them from the
    /// concourse end (0) to the far end (Along). Lanes along Z start at the front wall; along X they
    /// start at the end nearer the door (<see cref="Dir"/>: +1 from −X, −1 from +X).
    /// </summary>
    internal readonly record struct BowlFrame(bool AlongZ, int Dir, float W, float D)
    {
        public float Along => AlongZ ? D : W;
        public float Across => AlongZ ? W : D;

        public RectPlan Rect(float u0, float v0, float u1, float v1) => AlongZ
            ? new RectPlan(u0, -D / 2 + v0, u1, -D / 2 + v1)
            : Dir > 0 ? new RectPlan(-W / 2 + v0, u0, -W / 2 + v1, u1) : new RectPlan(W / 2 - v1, u0, W / 2 - v0, u1);

        public (float X, float Z) Point(float u, float v) => AlongZ ? (u, -D / 2 + v) : Dir > 0 ? (-W / 2 + v, u) : (W / 2 - v, u);

        public float U(float x, float z) => AlongZ ? x : z;
        public float V(float x, float z) => AlongZ ? z + D / 2 : Dir > 0 ? x + W / 2 : W / 2 - x;

        /// <summary>A room's wall facing +v (toward the pins), −v, +u and −u.</summary>
        public Side VPlus => AlongZ ? Side.Back : Dir > 0 ? Side.Right : Side.Left;
        public Side VMinus => AlongZ ? Side.Front : Dir > 0 ? Side.Left : Side.Right;
        public Side UPlus => AlongZ ? Side.Right : Side.Back;
        public Side UMinus => AlongZ ? Side.Left : Side.Front;

        /// <summary>Where along a ±u wall a point at <paramref name="v"/> is, as <see cref="OpeningPlan.Center"/> counts.</summary>
        public float OnUWall(float v) => AlongZ ? -D / 2 + v : Point(0, v).X;

        /// <summary>The v of a point at <paramref name="c"/> along a ±u wall: <see cref="OnUWall"/> undone.</summary>
        public float FromUWall(float c) => AlongZ ? c + D / 2 : V(c, 0);

        /// <summary>Quarter turns that face a piece's front (its local +Z) toward +v, −v, +u or −u.</summary>
        public int FaceVPlus => AlongZ ? 0 : Dir > 0 ? 1 : 3;
        public int FaceVMinus => (FaceVPlus + 2) % 4;
        public int FaceUPlus => AlongZ ? 1 : 0;
        public int FaceUMinus => (FaceUPlus + 2) % 4;
    }

    /// <summary>What the furnishing pass needs of a planned centre, which the rooms alone do not say.</summary>
    private sealed class BowlPlan
    {
        public BowlFrame Frame;
        public LaneSpec Spec = FullLanes;
        public int Pairs;
        /// <summary>u of the first pair's edge.</summary>
        public float LaneU0;
        /// <summary>v where the lane hall starts (the concourse's depth), and where the lane blocks start.</summary>
        public float HallV0, BlockV0;
        public bool Mezzanine;
        /// <summary>The door in lane-frame coordinates.</summary>
        public float DoorU, DoorV;
        public RectPlan? Flight;
    }

    private static readonly ConditionalWeakTable<InteriorLayout, BowlPlan> BowlPlans = new();

    /// <summary>
    /// Plans a bowling centre into <paramref name="l"/>. False leaves the layout to the caller's rules
    /// (the solid is too small for lanes, or no plan validated).
    /// </summary>
    internal static bool TryBowling(InteriorLayout l, Footprint fp, Building b, float doorHeight, Random rng)
    {
        bool whole = Venues.WholeBuilding(b);
        float wall = b.MaxY - b.MinY;
        float h;
        int levels;
        if (whole)
        {
            // a box of our own: one tall level, or two when there is the height for a mezzanine
            levels = wall >= 7.6f ? 2 : 1;
            h = levels == 2 ? Math.Clamp(wall * 0.88f / 2, 3.2f, 4.4f) : Math.Clamp(wall * 0.85f, 3.0f, 5.5f);
        }
        else
        {
            // the bottom of a taller building: its own storeys, so the facade's windows line up
            var (sh, n) = Storeys(b);
            h = sh;
            levels = n >= 2 && sh >= 2.8f ? 2 : 1;
        }

        // what the caller's rules start from, given back as it was if no plan holds
        var (storey, entryX, entryW, type, shop) = (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop);
        for (int lv = levels; lv >= 1; lv--)
        {
            l.StoreyHeight = lv == levels ? h : whole ? Math.Clamp(wall * 0.85f, 3.0f, 5.5f) : h;
            (l.EntryX, l.EntryWidth) = (entryX, entryW);
            if (PlanBowling(l, fp, lv, doorHeight, rng, whole) && InteriorValidator.Validate(l).Count == 0) return true;
        }
        (l.StoreyHeight, l.EntryX, l.EntryWidth, l.Type, l.Shop) = (storey, entryX, entryW, type, shop);
        l.Floors.Clear();
        l.Below = 0;
        BowlPlans.Remove(l);
        return false;
    }

    private static bool PlanBowling(InteriorLayout l, Footprint fp, int levels, float doorHeight, Random rng, bool whole)
    {
        float W = l.Width, D = l.Depth;
        // lanes along the long axis; along X they start at the end nearer the door
        var frame = D >= W ? new BowlFrame(true, 1, W, D) : new BowlFrame(false, l.EntryX <= 0 ? 1 : -1, W, D);
        float along = frame.Along, across = frame.Across;
        var spec = along >= FullLanes.Block + FullLanes.Settee + ConcourseMin ? FullLanes
            : along >= MiniLanes.Block + MiniLanes.Settee + ConcourseMin ? MiniLanes : null;
        if (spec == null || across < PairPitch + 2 * LaneSideWalk) return false;

        // ---- along: concourse, settees, lanes; a plain hall past them in a long solid ---------------
        float concourse = Math.Min(along - spec.Block - spec.Settee, ConcourseMax);
        float block = spec.Block;
        float rest = along - concourse - spec.Settee - block;
        if (rest < 3f) { block += rest; rest = 0; }   // a sliver behind the machines is service aisle
        float hallV0 = concourse, blockV0 = concourse + spec.Settee, progV = blockV0 + block;

        // ---- across: the door decides which side the programme hugs in a mall -------------------------
        float doorU = frame.U(l.EntryX, -D / 2), doorV = frame.V(l.EntryX, -D / 2);
        // along X the door is in the long front wall: if it opens past the concourse, onto the lanes'
        // side, the walkway there is wide enough to walk in by
        float walkMinus = !frame.AlongZ && doorV > concourse - 0.9f ? 2.2f : LaneSideWalk;
        int pairs = Math.Clamp((int)((across - walkMinus - LaneSideWalk) / PairPitch), 1, MaxPairs);
        float lanesW = pairs * PairPitch;
        float progW = Math.Min(across, walkMinus + lanesW + LaneSideWalk + GamesSideMax);
        if (across - progW < 3f) progW = across;
        float uL = frame.AlongZ ? Fit(doorU - progW / 2, -across / 2, across / 2 - progW) : -across / 2;
        float uR = uL + progW;
        // the lanes keep to the door's side, any width to spare is a games side beyond the far lane
        float laneU0 = frame.AlongZ && doorU > (uL + uR) / 2 ? uR - LaneSideWalk - lanesW : uL + walkMinus;

        l.Type = BuildingType.Bowling;
        l.Shop = Loot.ShopType.None;
        l.Below = 0;
        l.Floors.Clear();
        l.Lifts.Clear();
        var ground = new FloorPlan();
        l.Floors.Add(ground);
        float clear = l.StoreyHeight - Slab;

        // ---- the service column at the concourse's end away from the door: WCs, then a store ---------
        bool svcAtR = !frame.AlongZ || doorU < (uL + uR) / 2;
        const float svcW = 2.6f;
        bool svc = progW >= svcW + 6f && concourse >= 2.4f
                   && !(frame.AlongZ && (svcAtR ? doorU > uR - svcW - 1.5f : doorU < uL + svcW + 1.5f));
        float cU0 = svc && !svcAtR ? uL + svcW : uL, cU1 = svc && svcAtR ? uR - svcW : uR;

        var hall = new RoomPlan { Type = RoomType.Concourse };
        SetRect(hall, frame.Rect(cU0, 0, cU1, hallV0));
        ground.Rooms.Add(hall);
        var lanes = new RoomPlan { Type = RoomType.BowlingLanes, Span = levels };
        SetRect(lanes, frame.Rect(uL, hallV0, uR, progV));
        ground.Rooms.Add(lanes);
        int iHall = 0, iLanes = 1;

        if (svc)
        {
            float su0 = svcAtR ? uR - svcW : uL, su1 = su0 + svcW;
            var program = new List<RoomType> { RoomType.WC };
            if (concourse >= 5.4f) program.Add(RoomType.WC);
            if (concourse - 2.6f * program.Count >= 1.8f) program.Add(RoomType.Storage);
            float step = concourse / program.Count;
            for (int i = 0; i < program.Count; i++)
            {
                var room = new RoomPlan { Type = program[i] };
                SetRect(room, frame.Rect(su0, step * i, su1, i == program.Count - 1 ? concourse : step * (i + 1)));
                ground.Rooms.Add(room);
                int ri = ground.Rooms.Count - 1;
                float c = frame.OnUWall(step * (i + 0.5f));
                float top = Math.Min(2.05f, clear - 0.2f);
                var side = svcAtR ? frame.UMinus : frame.UPlus;
                room.Openings.Add(new OpeningPlan { Side = side, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iHall });
                hall.Openings.Add(new OpeningPlan { Side = Opposite(side), Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = ri });
            }
        }

        // ---- the concourse opens onto the lanes over its whole width ---------------------------------
        float hallClear = l.ClearOf(hall), lanesClear = l.ClearOf(lanes);
        hall.Openings.Add(new OpeningPlan
        {
            Side = frame.VPlus, Center = (cU0 + cU1) / 2, Width = cU1 - cU0 - 0.4f,
            Top = hallClear - 0.05f, Kind = OpeningKind.Arch, Other = iLanes,
        });
        lanes.Openings.Add(new OpeningPlan
        {
            Side = frame.VMinus, Center = (uL + uR) / 2, Width = progW - 0.4f,
            Top = lanesClear - 0.2f, Kind = OpeningKind.Arch, Other = iHall,
        });

        // ---- a plain hall beyond the programme: behind the machines, beside the games side --------------
        if (rest >= 3f)
        {
            var back = new RoomPlan { Type = RoomType.Hall };
            SetRect(back, frame.Rect(uL, progV, uR, along));
            ground.Rooms.Add(back);
            int bi = ground.Rooms.Count - 1;
            float c = (laneU0 + lanesW + uR) / 2;
            if (uR - (laneU0 + lanesW) < InnerDoor + 0.4f) c = (uL + laneU0) / 2;
            float top = Math.Min(2.1f, clear - 0.2f);
            lanes.Openings.Add(new OpeningPlan { Side = frame.VPlus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = bi });
            back.Openings.Add(new OpeningPlan { Side = frame.VMinus, Center = c, Width = InnerDoor, Top = top, Kind = OpeningKind.Door, Other = iLanes });
        }
        if (progW < across)
        {
            bool right = uL <= -across / 2 + 0.01f;
            var side = new RoomPlan { Type = RoomType.Hall };
            SetRect(side, right ? frame.Rect(uR, 0, across / 2, along) : frame.Rect(-across / 2, 0, uL, along));
            ground.Rooms.Add(side);
            int si = ground.Rooms.Count - 1;
            float c = frame.OnUWall((hallV0 + blockV0) / 2);
            float top = Math.Min(2.1f, clear - 0.2f);
            var wallSide = right ? frame.UPlus : frame.UMinus;
            lanes.Openings.Add(new OpeningPlan { Side = wallSide, Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = si });
            side.Openings.Add(new OpeningPlan { Side = Opposite(wallSide), Center = c, Width = 1.6f, Top = top, Kind = OpeningKind.Door, Other = iLanes });
        }

        // ---- the way in --------------------------------------------------------------------------------
        l.EntryWidth = Math.Min(Math.Max(l.DoorWidth, 1.8f), W - 1.2f);
        l.EntryX = Fit(l.EntryX, -W / 2 + l.EntryWidth / 2 + 0.3f, W / 2 - l.EntryWidth / 2 - 0.3f);
        int entryRoom = RoomAt(ground, l.EntryX, -D / 2 + 0.1f);
        if (entryRoom < 0 || ground.Rooms[entryRoom].Type is RoomType.WC or RoomType.Storage) return false;
        var into = ground.Rooms[entryRoom];
        into.Openings.Add(new OpeningPlan
        {
            Side = Side.Front, Center = l.EntryX, Width = l.EntryWidth, Bottom = 0,
            Top = Math.Min(Math.Max(doorHeight, 2.1f), l.ClearOf(into) - 0.3f), Kind = OpeningKind.Entry,
        });

        var plan = new BowlPlan
        {
            Frame = frame, Spec = spec, Pairs = pairs, LaneU0 = laneU0, HallV0 = hallV0, BlockV0 = blockV0,
            DoorU = doorU, DoorV = doorV,
        };

        // ---- the mezzanine over the concourse, up a straight stair along the concourse's end wall -----
        if (levels == 2 && !Mezzanine(l, plan, hall, uL, uR, cU0, cU1, concourse)) return false;

        if (!whole)
            foreach (int f in Enumerable.Range(0, l.Floors.Count))
                AddWindows(l, l.Floors[f], f);
        // the lane hall is blind: a bowling hall has no windows on its lanes
        lanes.Openings.RemoveAll(o => o.Kind == OpeningKind.Window);

        BowlPlans.AddOrUpdate(l, plan);
        return true;
    }

    /// <summary>The mezzanine floor and the stair up to it. False when the concourse has no wall to run a stair along.</summary>
    private static bool Mezzanine(InteriorLayout l, BowlPlan plan, RoomPlan hall, float uL, float uR, float cU0, float cU1, float depth)
    {
        var frame = plan.Frame;
        float h = l.StoreyHeight;
        int steps = (int)MathF.Ceiling(h / GarageRule.StairRiser);
        float run = steps * 0.28f;
        const float laneW = 1.1f, landing = 1.3f;
        // along the concourse's end wall (v = 0), its top at the end away from the door
        bool upToR = plan.DoorU < (cU0 + cU1) / 2 || !frame.AlongZ;
        float top = upToR ? cU1 - 0.3f : cU0 + 0.3f, bottom = upToR ? top - run : top + run;
        float lo = Math.Min(top, bottom), hi = Math.Max(top, bottom);
        if (lo - landing < cU0 || hi + 0.3f > cU1 || depth < laneW + 2.4f) return false;
        // the foot of the stair and its landing keep off the doorway
        if (plan.DoorV < 0.5f && plan.DoorU > lo - landing - 1.6f && plan.DoorU < hi + 1.6f) return false;

        var lane = frame.Rect(lo, 0.15f, hi, 0.15f + laneW);
        var flight = frame.AlongZ
            ? new FlightPlan { AlongX = true, X0 = lane.Z0, X1 = lane.Z1, ZBottom = bottom, ZTop = top }
            : new FlightPlan { X0 = lane.X0, X1 = lane.X1, ZBottom = bottom, ZTop = top };
        l.Floors[0].Flight = flight;
        plan.Flight = flight.Area();

        var upper = new FloorPlan();
        l.Floors.Add(upper);
        var mezz = new RoomPlan { Type = RoomType.Mezzanine };
        SetRect(mezz, frame.Rect(uL, 0, uR, depth));
        upper.Rooms.Add(mezz);
        upper.Holes.Add(flight.Area());
        // the guard along the shaft's open edge
        var (ax, az) = frame.Point(lo, 0.15f + laneW);
        var (bx, bz) = frame.Point(hi, 0.15f + laneW);
        upper.Rails.Add(new RectPlan(ax, az, bx, bz));
        // a parapet over the lanes, open above it
        mezz.Openings.Add(new OpeningPlan
        {
            Side = frame.VPlus, Center = (uL + uR) / 2, Width = uR - uL - 0.4f, Bottom = 1.05f,
            Top = l.ClearOf(mezz) - 0.05f, Kind = OpeningKind.Arch,
        });
        plan.Mezzanine = true;
        return true;
    }

    private static void SetRect(RoomPlan r, RectPlan p)
    {
        r.X0 = p.X0; r.Z0 = p.Z0; r.X1 = p.X1; r.Z1 = p.Z1;
    }

    private static int RoomAt(FloorPlan floor, float x, float z)
    {
        for (int i = 0; i < floor.Rooms.Count; i++)
        {
            var r = floor.Rooms[i];
            if (x >= r.X0 - 0.01f && x <= r.X1 + 0.01f && z >= r.Z0 - 0.01f && z <= r.Z1 + 0.01f) return i;
        }
        return -1;
    }

    // ---- furnishing (HallLayout) -------------------------------------------------------------------

    /// <summary>The bowling pieces at the sizes this file places them, for the model viewer.</summary>
    private static readonly Piece[] BowlingPieces =
    {
        new(FurnitureType.LanePair, PairPitch, FullLanes.Block - RearAisle, 4.0f, false),
        new(FurnitureType.Settee, PairPitch - 0.3f, FullLanes.Settee - ArchClear - 0.1f, 1.0f, false),
        new(FurnitureType.ShoeCounter, 3.6f, 2.2f, 2.3f, true),
        new(FurnitureType.BallRack, 2.4f, 0.55f, 1.1f, true),
        new(FurnitureType.BarCounter, 4.0f, 2.2f, 2.2f, true),
        new(FurnitureType.BarStool, 0.4f, 0.4f, 0.78f, false),
        new(FurnitureType.PoolTable, 1.4f, 2.7f, 0.8f, false),
        new(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true),
        new(FurnitureType.ArcadeCabinet, 0.75f, 0.9f, 1.85f, true),
        new(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true),
    };

    /// <summary>Whole bowling centres in the model viewer: one on two levels, and a bowl bar with mini lanes.</summary>
    [Core.Showcase("Interiors", "Bowling centre")]
    private static IEnumerable<(string Name, Func<Godot.Node3D> Make)> ShowcaseBowling()
    {
        foreach (var (name, w, d, h) in new[] { ("Two levels", 30f, 48f, 8.5f), ("Bowl bar", 13f, 24f, 5f) })
            yield return (name, () =>
            {
                var id = new TileId(2600, 1200);
                var tile = new BuildingTile { Id = id, Buildings = new List<Building> { BowlingProbe.Box(500, 500, w, d, h, BuildingKind.Commercial) } };
                Venues.Override = new List<VenueRecord>
                {
                    new("n1", VenueType.Bowling, id.MinE + 500, id.MaxN - 500, VenueFix.Inside, VenueFlags.None, 0, "", name),
                };
                var layout = Generate(tile, 0, null, null)!;
                Venues.Override = null;
                var node = InteriorNode.Create(layout, InteriorMeshBuilder.Build(layout),
                    Styles.StyleKit.Material(Styles.MaterialRole.Interior), Godot.Transform3D.Identity);
                foreach (var leaf in node.GetChildren().OfType<DoorLeaf>()) leaf.Visible = false;
                return node;
            });
    }

    private static bool BowlingRoom(RoomType t) => t is RoomType.BowlingLanes or RoomType.Concourse or RoomType.Mezzanine;

    private static void BowlingLayout(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        if (!BowlPlans.TryGetValue(l, out var plan)) return;
        switch (r.Type)
        {
            case RoomType.BowlingLanes: LaneHall(l, f, r, plan, placed, blocked, rng); break;
            case RoomType.Concourse: Concourse(l, f, r, plan, placed, blocked, rng); break;
            case RoomType.Mezzanine: Gallery(l, f, r, plan, placed, blocked, rng); break;
        }
    }

    /// <summary>The pairs of lanes, a settee bay in front of each, and the games side beyond the far lane.</summary>
    private static void LaneHall(InteriorLayout l, int f, RoomPlan r, BowlPlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        float clear = l.ClearOf(r);
        float blockLen = BlockLength(r, fr, p);
        for (int i = 0; i < p.Pairs; i++)
        {
            float u = p.LaneU0 + PairPitch * (i + 0.5f);
            var (x, z) = fr.Point(u, p.BlockV0 + blockLen / 2);
            Put(l, f, FurnitureType.LanePair, x, z, fr.FaceVPlus, PairPitch, blockLen, clear, placed);
            // the bowlers' bay: curved benches round a table, facing their two lanes
            float sd = p.Spec.Settee - ArchClear - 0.1f;
            var (sx, sz) = fr.Point(u, p.HallV0 + ArchClear + sd / 2);
            Put(l, f, FurnitureType.Settee, sx, sz, fr.FaceVPlus, PairPitch - 0.3f, sd, 1.0f, placed);
        }

        // the far side of the hall, past the last lane: pool tables, as many as fit with room for a cue
        float uMin = Math.Min(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1)), uMax = Math.Max(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1));
        float g0 = p.LaneU0 + p.Pairs * PairPitch + LaneSideWalk, g1 = uMax;
        if (p.LaneU0 - LaneSideWalk - uMin > g1 - g0) (g0, g1) = (uMin, p.LaneU0 - LaneSideWalk);
        if (g1 - g0 >= 4.2f)
            PoolTables(l, f, r, fr, (g0 + g1) / 2, p.HallV0 + 1.0f, p.BlockV0 + blockLen - 1.0f, placed, blocked, horizontal: false);
    }

    /// <summary>
    /// How long the lane blocks are in this hall: from the settees to the service corridor behind the
    /// pinsetters, which keeps <see cref="RearAisle"/> along the far wall (and clear of a back door).
    /// </summary>
    private static float BlockLength(RoomPlan r, BowlFrame fr, BowlPlan p)
    {
        float v0 = fr.V(r.X0, r.Z0), v1 = fr.V(r.X1, r.Z1);
        return Math.Max(v0, v1) - p.BlockV0 - RearAisle;
    }

    /// <summary>The corridor behind the pinsetters, along the hall's far wall [S: 1.5 m min, part of it in the block].</summary>
    private const float RearAisle = 0.7f;

    /// <summary>
    /// Pool tables down a strip of the plan between <paramref name="v0"/> and <paramref name="v1"/>,
    /// centred on <paramref name="u"/>: a 9 ft table, 4.3 x 5.5 m with a cue's room round it [S].
    /// </summary>
    private static int PoolTables(InteriorLayout l, int f, RoomPlan r, BowlFrame fr, float u, float v0, float v1,
        List<RectPlan> placed, List<RectPlan> blocked, bool horizontal, int max = 8)
    {
        const float tw = 1.4f, td = 2.7f, pitchAlong = 5.4f;
        int n = 0;
        for (float v = v0 + pitchAlong / 2; v + pitchAlong / 2 <= v1 + 0.01f && n < max; v += pitchAlong)
        {
            var (x, z) = fr.Point(u, v);
            // the table's long side along v, as the strip runs
            int turns = fr.FaceVPlus;
            bool odd = turns % 2 == 1;
            float ew = odd ? td : tw, ed = odd ? tw : td;
            var rect = new RectPlan(x - ew / 2, z - ed / 2, x + ew / 2, z + ed / 2);
            if (!Free(r, rect, placed, blocked, 1.1f)) continue;
            Put(l, f, FurnitureType.PoolTable, x, z, turns, tw, td, 0.8f, placed);
            n++;
        }
        return n;
    }

    /// <summary>
    /// The concourse: the shoe counter by the door, house ball racks, the arcade corner and the
    /// darts, and on one level the bar, a few tables and the pool tables.
    /// </summary>
    private static void Concourse(InteriorLayout l, int f, RoomPlan r, BowlPlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        Carpet(l, f, r, placed);
        // the walk from the door to the lanes, and the strip along the arch, stay clear
        var (dx, dz) = fr.Point(p.DoorU, p.DoorV);
        blocked.Add(new RectPlan(dx - 1.6f, dz - 1.6f, dx + 1.6f, dz + 1.6f).Clip(r));
        blocked.Add(Strip(r, fr.VPlus, 1.2f));
        if (p.Flight is { } fl) blocked.Add(fl.Grow(1.2f));

        var near = Sides(fr, l, r);
        TryPlace(l, f, r, new Piece(FurnitureType.ShoeCounter, Math.Min(3.6f, Math.Max(2.4f, r.Width * 0.3f)), 2.2f, 2.3f, true),
            placed, blocked, rng, near.Door, near.DoorAt);
        // the bar before the games: a small centre may have room for only one of them
        if (!p.Mezzanine) Bar(l, f, r, placed, blocked, rng, near);
        TryPlace(l, f, r, new Piece(FurnitureType.BallRack, 2.4f, 0.55f, 1.1f, true), placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.BallRack, 2.4f, 0.55f, 1.1f, true), placed, blocked, rng);
        // the arcade corner: a pinball or two and a few cabinets, backs to the wall
        int pinballs = 1 + (Core.Fnv.Unit(l.Key + "|pinball") < 0.5 ? 1 : 0);
        for (int i = 0; i < pinballs; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Pinball, 0.75f, 1.4f, 1.93f, true), placed, blocked, rng, near.Far);
        int cabinets = 2 + (int)(Core.Fnv.Unit(l.Key + "|cabinets") * 3);
        for (int i = 0; i < cabinets; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.ArcadeCabinet, 0.75f, 0.9f, 1.85f, true), placed, blocked, rng, near.Far);
        TryPlace(l, f, r, new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), placed, blocked, rng, near.Far);
        // the pool tables and tables: here, unless a mezzanine has them; a big concourse gets some anyway
        if (!p.Mezzanine || r.Area >= 150f) Lounge(l, f, r, fr, placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
    }

    /// <summary>The mezzanine: the bar, tables along the parapet, the pool tables, the darts.</summary>
    private static void Gallery(InteriorLayout l, int f, RoomPlan r, BowlPlan p, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        var fr = p.Frame;
        Carpet(l, f, r, placed);
        if (p.Flight is { } fl) blocked.Add(fl.Grow(1.2f));
        var near = Sides(fr, l, r);
        Bar(l, f, r, placed, blocked, rng, near);
        Lounge(l, f, r, fr, placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.DartBoard, 0.7f, 0.15f, 2.0f, true), placed, blocked, rng);
        TryPlace(l, f, r, new Piece(FurnitureType.Plant, 0.5f, 0.5f, 1.4f, true), placed, blocked, rng);
    }

    /// <summary>The bar with its stools, away from the door; shorter, on any wall, when the long one finds no room.</summary>
    private static void Bar(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed, List<RectPlan> blocked,
        Random rng, (Side[] Door, float DoorAt, Side[] Far) near)
    {
        float barLen = Math.Clamp(r.Width * 0.35f, 3.0f, 7.0f);
        int before = l.Furniture.Count;
        foreach (var (len, sides) in new[] { (barLen, near.Far), (2.4f, null), (1.8f, null) })
        {
            TryPlace(l, f, r, new Piece(FurnitureType.BarCounter, len, 2.2f, 2.2f, true), placed, blocked, rng, sides);
            if (l.Furniture.Count > before) break;
        }
        if (l.Furniture.Count > before) Stools(l, f, r, l.Furniture[^1], placed, blocked);
    }

    /// <summary>The pool tables across the room's middle, and tables for two in what is left.</summary>
    private static void Lounge(InteriorLayout l, int f, RoomPlan r, BowlFrame fr, List<RectPlan> placed, List<RectPlan> blocked, Random rng)
    {
        // pool tables in a row across the middle when the room is deep enough for a table and its cues
        float vMid = (fr.V(r.X0, r.Z0) + fr.V(r.X1, r.Z1)) / 2;
        float depth = Math.Abs(fr.V(r.X1, r.Z1) - fr.V(r.X0, r.Z0));
        if (depth >= 6.4f)
        {
            float u0 = Math.Min(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1)), u1 = Math.Max(fr.U(r.X0, r.Z0), fr.U(r.X1, r.Z1));
            int n = 0;
            for (float u = u0 + 2.9f; u + 2.9f <= u1 && n < 6; u += 4.4f)
            {
                var (x, z) = fr.Point(u, vMid);
                int turns = fr.FaceVPlus;
                bool odd = turns % 2 == 1;
                var rect = new RectPlan(x - (odd ? 1.35f : 0.7f), z - (odd ? 0.7f : 1.35f), x + (odd ? 1.35f : 0.7f), z + (odd ? 0.7f : 1.35f));
                if (!Free(r, rect, placed, blocked, 1.1f)) continue;
                Put(l, f, FurnitureType.PoolTable, x, z, turns, 1.4f, 2.7f, 0.8f, placed);
                n++;
            }
        }
        for (int i = 0; i < 4; i++)
            TryPlace(l, f, r, new Piece(FurnitureType.Table, 0.8f, 0.8f, 0.75f, false), placed, blocked, rng);
    }

    /// <summary>Stools in front of a bar counter, 0.6 m apart.</summary>
    private static void Stools(InteriorLayout l, int f, RoomPlan r, FurniturePlan bar, List<RectPlan> placed, List<RectPlan> blocked)
    {
        // the counter's front is its local +Z: the stools stand 0.4 m out from it
        float fx = MathF.Sin(bar.Turns * MathF.PI / 2), fz = MathF.Cos(bar.Turns * MathF.PI / 2);
        float ax = MathF.Cos(bar.Turns * MathF.PI / 2), az = -MathF.Sin(bar.Turns * MathF.PI / 2);
        int n = Math.Max(1, (int)((bar.W - 0.6f) / 0.6f));
        for (int i = 0; i < n; i++)
        {
            float t = -bar.W / 2 + 0.6f + i * 0.6f;
            float x = bar.X + fx * (bar.D / 2 + 0.3f) + ax * t, z = bar.Z + fz * (bar.D / 2 + 0.3f) + az * t;
            var rect = new RectPlan(x - 0.2f, z - 0.2f, x + 0.2f, z + 0.2f);
            if (!Free(r, rect, placed, blocked, 0.02f)) continue;
            Put(l, f, FurnitureType.BarStool, x, z, bar.Turns, 0.4f, 0.4f, 0.78f, placed);
        }
    }

    /// <summary>The carpet's print over a room's floor, a hand in from the walls; walked over, so it blocks nothing.</summary>
    private static void Carpet(InteriorLayout l, int f, RoomPlan r, List<RectPlan> placed)
    {
        const float inset = WallInset + 0.05f;
        if (r.Width < 1f || r.Depth < 1f) return;
        Put(l, f, FurnitureType.Confetti, (r.X0 + r.X1) / 2, (r.Z0 + r.Z1) / 2, 0, r.Width - 2 * inset, r.Depth - 2 * inset, 0.005f, placed);
    }

    /// <summary>A strip <paramref name="deep"/> m deep along one wall of a room.</summary>
    private static RectPlan Strip(RoomPlan r, Side side, float deep) => side switch
    {
        Side.Front => new RectPlan(r.X0, r.Z0, r.X1, r.Z0 + deep),
        Side.Back => new RectPlan(r.X0, r.Z1 - deep, r.X1, r.Z1),
        Side.Left => new RectPlan(r.X0, r.Z0, r.X0 + deep, r.Z1),
        _ => new RectPlan(r.X1 - deep, r.Z0, r.X1, r.Z1),
    };

    /// <summary>
    /// The walls to try for what belongs by the door (the shoe counter) and for what goes away from it
    /// (the games, the bar): the end wall and the side by the door first, or the far side.
    /// </summary>
    private static (Side[] Door, float DoorAt, Side[] Far) Sides(BowlFrame fr, InteriorLayout l, RoomPlan r)
    {
        var (dx, dz) = (l.EntryX, -l.Depth / 2);
        bool doorOnEnd = fr.AlongZ;
        var doorSide = doorOnEnd ? fr.VMinus : fr.UMinus;
        // along the wall the door is in, the shoe counter wants to stand next to it
        float at = doorSide is Side.Front or Side.Back ? dx : dz;
        var farU = fr.AlongZ ? (l.EntryX < (r.X0 + r.X1) / 2 ? fr.UPlus : fr.UMinus) : fr.UPlus;
        return (new[] { doorSide, fr.UMinus, fr.UPlus }, at, new[] { farU, fr.VMinus, Opposite(farU) });
    }
}

internal static class RectPlanClip
{
    /// <summary>The part of a rectangle inside a room's.</summary>
    public static RectPlan Clip(this RectPlan p, RoomPlan r) =>
        new(Math.Max(p.X0, r.X0), Math.Max(p.Z0, r.Z0), Math.Min(p.X1, r.X1), Math.Min(p.Z1, r.Z1));
}
