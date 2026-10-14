using Godot;
using UnitSport.Core;
using UnitSport.Interiors;
using UnitSport.Terrain.Format;

namespace UnitSport.Terrain.Construction;

// Plain C# with Godot maths only: linked into the unit tests (docs/notes/general/testing.md).

/// <summary>How far a building site has got (#606), read from the surveyed volume against the GWR floor count.</summary>
public enum SitePhase : byte
{
    /// <summary>Not one storey up yet: base slab, starter bars, the first wall formwork.</summary>
    Foundations = 0,
    /// <summary>Part-way up: <see cref="ConstructionSite.BuiltStoreys"/> slabs, the top one in formwork.</summary>
    Shell = 1,
    /// <summary>At full height: the whole shell, wrapped in scaffolding, the facade being closed.</summary>
    ToppedOut = 2,
}

/// <summary>What a patch of a site's ground is used for.</summary>
public enum SiteZoneKind : byte { Office = 0, Toilets = 1, Skips = 2, Materials = 3, SoilHeap = 4 }

/// <summary>A top-slewing tower crane, or the low folding kind that stands on a house plot.</summary>
public enum CraneKind : byte { Tower = 0, SelfErecting = 1 }

/// <summary>
/// The machine a slot is for. The machines are built by later issues of #605; the planner only
/// says where each would stand, and the dormant provider (#616) maps a role to a <c>RideKind</c>.
/// Stored nowhere, but a role's number is part of the slot's identity: append only.
/// </summary>
public enum MachineRole : byte
{
    Excavator = 0, WheelLoader = 1, Tipper = 2, Mixer = 3, Telehandler = 4,
    MiniExcavator = 5, MiniDumper = 6, Roller = 7, Van = 8,
}

/// <summary>An oriented rectangle in tile-local plan metres (x east, z south).</summary>
public readonly record struct SiteRect(Vector2 Center, Vector2 AxisU, float Width, float Depth)
{
    public Vector2 AxisV => new(-AxisU.Y, AxisU.X);

    public Vector2[] Corners()
    {
        var u = AxisU * (Width / 2);
        var v = AxisV * (Depth / 2);
        return new[] { Center - u - v, Center + u - v, Center + u + v, Center - u + v };
    }

    public bool Contains(Vector2 p, float margin = 0f)
    {
        var d = p - Center;
        return Math.Abs(d.Dot(AxisU)) <= Width / 2 + margin && Math.Abs(d.Dot(AxisV)) <= Depth / 2 + margin;
    }

    /// <summary>Whether two rectangles come closer than <paramref name="gap"/> (separating axes).</summary>
    public bool Overlaps(SiteRect o, float gap = 0f)
    {
        foreach (var axis in new[] { AxisU, AxisV, o.AxisU, o.AxisV })
        {
            var (a0, a1) = Along(axis);
            var (b0, b1) = o.Along(axis);
            if (b0 - a1 >= gap || a0 - b1 >= gap) return false;
        }
        return true;
    }

    private (float Min, float Max) Along(Vector2 axis)
    {
        float min = float.MaxValue, max = float.MinValue;
        foreach (var c in Corners())
        {
            float t = c.Dot(axis);
            min = Math.Min(min, t);
            max = Math.Max(max, t);
        }
        return (min, max);
    }
}

/// <summary>A patch of the site set aside for one use, aligned with the site.</summary>
public sealed record SiteZone(SiteZoneKind Kind, SiteRect Rect);

/// <summary>
/// One crane. <see cref="RestYaw"/> is where the jib points when nothing moves it (the game's yaw:
/// 0 faces −Z); the clock-driven motion of #610 starts from it. <see cref="Inside"/>: the mast
/// stands in the building, and the shell (#608) leaves an opening in every slab round it.
/// </summary>
public sealed record CraneSpot(CraneKind Kind, Vector2 Base, float HookHeight, float JibLength, float RestYaw, bool Inside = false);

/// <summary>
/// Where one machine stands. <see cref="Ordinal"/> is its place in the phase's list, never a running
/// count, so a slot that cannot be placed leaves a gap instead of renaming the machines after it.
/// <see cref="Yaw"/> is the game's (0 faces −Z).
/// </summary>
public sealed record MachineSlot(MachineRole Role, int Ordinal, Vector2 At, float Yaw);

/// <summary>A straight run of hoarding panels, from <see cref="A"/> to <see cref="B"/>.</summary>
public readonly record struct HoardingRun(Vector2 A, Vector2 B);

/// <summary>
/// The excavation a site is dug into (#619), in the SITE frame (x along the plan box's <c>AxisU</c>,
/// z along <c>AxisV</c>, from its centre). <see cref="Area"/> is the hole: the plan box and a working
/// margin round it, and along one wall the <see cref="Lane"/> the machines' ramp runs down (the
/// street side's wherever the ground there is free; <see cref="Side"/> is that wall's outward normal),
/// from its top at the <see cref="TopEnd"/> end of the lane (+1: the end at the lane's greater x or z)
/// to the pit's floor. <see cref="Basements"/> storeys go below the ground.
/// </summary>
public sealed record SitePit(Rect2D Area, Rect2D Lane, bool LaneAlongX, int TopEnd, int Basements, Vector2 Side)
{
    /// <summary>How far the lane runs, m: the ramp and the flat at its foot.</summary>
    public float LaneLength => LaneAlongX ? Lane.Width : Lane.Depth;
    public float LaneWidth => LaneAlongX ? Lane.Depth : Lane.Width;
}

/// <summary>
/// A building site (#606): one <see cref="BuildingKind.UnderConstruction"/> building and the ground
/// round it, as <see cref="ConstructionSites.Plan"/> lays it out. Everything is in tile-local plan
/// metres, heights above <see cref="Base"/>.
/// </summary>
public sealed record ConstructionSite(
    string Key,
    SitePhase Phase,
    PlanBox Box,
    float Base,
    float Measured,
    float StoreyHeight,
    int BuiltStoreys,
    int TargetStoreys,
    SiteRect Area,
    Vector2 Front,
    Vector2 Gate,
    float GateWidth,
    IReadOnlyList<HoardingRun> Hoarding,
    IReadOnlyList<SiteZone> Zones,
    IReadOnlyList<CraneSpot> Cranes,
    IReadOnlyList<MachineSlot> Machines,
    SitePit? Pit = null)
{
    /// <summary>How high the building will stand when it is finished, m above <see cref="Base"/>.</summary>
    public float TargetHeight => TargetStoreys * StoreyHeight;

    /// <summary>A house plot: a self-erecting crane, the mini machines, two containers at most.</summary>
    public bool Small => ConstructionSites.IsSmall(Box, TargetStoreys);
}

/// <summary>
/// Lays out building sites (#606, part of #605): a pure function of one building, its plan box,
/// the street it faces and what stands round it, so the server and every client work out the same
/// site and nothing about it is ever sent or stored — the method of <see cref="BuildingTypes.SiteFor"/>.
///
/// <para>
/// <b>The phase comes from the data.</b> swissBUILDINGS3D models an <c>Im Bau</c> building as the
/// volume standing when it was surveyed, and GWR says how many floors it will have. A 1.3 m solid
/// with 6 GWR floors is a base slab; a 9 m one with 6 floors is three storeys of a six-storey shell.
/// </para>
///
/// <para>
/// <b>The site is a rectangle round the plan box</b>, aligned with it, with a deep margin on the
/// street side for the yard and a narrow one elsewhere. The cadastre's plots are not loaded, so the
/// hoarding is the rectangle with every panel that would stand on a road or in another building
/// dropped, never nudged (the <c>SiteYards.Blocked</c> rule). The notch of an L-shaped building is
/// inside the rectangle, so it becomes yard; the shell (#608) follows the real shape through
/// <see cref="PlanOutline"/>.
/// </para>
///
/// <para>
/// Every roll is <see cref="Fnv.Unit"/> of the key and its own question, so changing one roll never
/// reshuffles another, and a dropped slot leaves a gap (the dormant-slot ordinal rule).
/// </para>
/// </summary>
public static class ConstructionSites
{
    /// <summary>A storey of a building going up, floor to floor: a Swiss residential slab-to-slab height.</summary>
    public const float Storey = 3.0f;
    /// <summary>Below this the first storey's walls are not up: the site is still at its foundations.</summary>
    public const float FoundationsBelow = 2.6f;
    /// <summary>A building that has reached within this of its target is topped out.</summary>
    public const float ToppedOutWithin = 1.5f;
    /// <summary>A topped-out building whose storeys would be taller than this has more of them than GWR says.</summary>
    public const float MaxStorey = 4.5f;

    /// <summary>Nothing of the site may come closer to the walls than this: the scaffolding stands in it.</summary>
    public const float Scaffold = 1.5f;
    /// <summary>One panel of mobile fencing (a Bauzaun panel), m.</summary>
    public const float Panel = 3.5f;
    /// <summary>A plan box under this, of no more than <see cref="SmallStoreys"/>, is a house plot.</summary>
    public const float SmallArea = 350f;
    public const int SmallStoreys = 3;

    /// <summary>A tower crane's base (the cross frame on its blocks), and a self-erecting crane's, m square.</summary>
    public const float TowerBase = 4.6f, SelfErectingBase = 4.0f;
    /// <summary>The jib lengths a crane is made in: a crane is ordered to reach, within these.</summary>
    public const float TowerJibMin = 25f, TowerJibMax = 75f, SelfErectingJibMin = 16f, SelfErectingJibMax = 40f;

    /// <summary>The pit's working margin round the plan box (#619): the scaffolding stands in it, so it is <see cref="Scaffold"/>.</summary>
    public const float PitMargin = Scaffold;
    /// <summary>The ramp's lane along the front wall, m wide: a 20 t excavator's tracks, or the mini machines'.</summary>
    public const float PitLane = 4.6f, PitLaneSmall = 3.4f;
    /// <summary>A pit stays this far inside its tile: the next tile does not plan this one's sites, so it could not cut its share.</summary>
    public const float PitSeam = 3f;
    /// <summary>A shell keeps its pit open up to this many storeys; past that the trench round the basement is backfilled.</summary>
    public const int PitOpenUpTo = 2;
    /// <summary>Ground kept clear past the ramp's top, m: where a machine lines up to go down.</summary>
    public const float RampApron = 6f;
    /// <summary>
    /// A site with a pit is at least this wide beside the building, m: a 20 t excavator (9.5 m long)
    /// turns on the spot in the strip between the pit and the hoarding to go down the ramp; a mini one in less.
    /// </summary>
    public const float PitSide = 8f, PitSideSmall = 5.5f;
    /// <summary>On a site with a pit, the way kept across the materials where there is room, m: a telehandler coming at a pallet.</summary>
    public const float MaterialsApproach = 8f;

    public static bool IsSmall(PlanBox box, int targetStoreys) => box.Area < SmallArea && targetStoreys <= SmallStoreys;

    /// <summary>
    /// How many storeys a building has under the ground (#619). GWR does not say, so it is hashed: a
    /// house has its cellar, a block one or two, a big block two (the car park and the cellars).
    /// </summary>
    public static int Basements(string key, PlanBox box, int targetStoreys)
    {
        if (IsSmall(box, targetStoreys)) return 1;
        if (box.Area > 1500f || targetStoreys >= 6) return 2;
        return Fnv.Unit(key + "|site|basements") < 0.4 ? 2 : 1;
    }

    /// <summary>A rectangle of a site's frame (x along the plan box's <c>AxisU</c>, z along <c>AxisV</c>, from its centre) in tile-local plan metres.</summary>
    public static SiteRect InPlan(PlanBox box, Rect2D r) =>
        new(box.Center + box.AxisU * ((r.X0 + r.X1) / 2) + box.AxisV * ((r.Z0 + r.Z1) / 2), box.AxisU, r.Width, r.Depth);

    /// <summary>
    /// The pit a site at its foundations, or a shell not past <see cref="PitOpenUpTo"/> storeys, is dug
    /// into (#619), or null: a topped-out site, a pit that would cross its tile's edge
    /// (<see cref="PitSeam"/>), or one whose ground has a road or another building on it.
    /// </summary>
    private static SitePit? Pit(string key, PlanBox box, SitePhase phase, int built, int target, bool small,
        Vector2 front, Vector2 along, float gateAlong, Func<Vector2, bool> blocked)
    {
        if (phase != SitePhase.Foundations && !(phase == SitePhase.Shell && built <= PitOpenUpTo)) return null;
        // the lane along the street side; where a road or a neighbour has that ground, along a side
        // (the one toward the gate first, its ramp coming up at its street end), else the back.
        // A lane too short for the pit's depth even at the steepest ramp made gives way to the next one
        // long enough, then to the longest lane there is (a slope may still take the pit deeper, and
        // PitPlans leaves out a ramp it cannot make)
        int toGate = Math.Abs(gateAlong) > 0.5f ? Math.Sign(gateAlong) : 1;
        float drop = Basements(key, box, target) * Storey + 0.3f;
        SitePit? longest = null;
        foreach (var side in new[] { front, along * toGate, -along * toGate, -front })
        {
            if (PitOn(key, box, built, target, small, side, front, along, gateAlong, blocked) is not { } pit) continue;
            if (pit.LaneLength >= drop / PitPlans.MaxGrade + PitPlans.FootRoom) return pit;
            if (longest == null || pit.LaneLength > longest.LaneLength) longest = pit;
        }
        return longest;
    }

    /// <summary>The pit with its ramp's lane along the wall facing <paramref name="side"/>, or null where it cannot be dug.</summary>
    private static SitePit? PitOn(string key, PlanBox box, int built, int target, bool small, Vector2 side,
        Vector2 front, Vector2 along, float gateAlong, Func<Vector2, bool> blocked)
    {
        float hw = box.Width / 2, hd = box.Depth / 2, m = PitMargin, lw = small ? PitLaneSmall : PitLane;
        var area = new Rect2D(-hw - m, -hd - m, hw + m, hd + m);
        float fu = side.Dot(box.AxisU), fv = side.Dot(box.AxisV);
        bool alongX = Math.Abs(fv) > 0.5f;
        Rect2D lane;
        if (!alongX)
        {
            area = fu > 0 ? area with { X1 = area.X1 + lw } : area with { X0 = area.X0 - lw };
            lane = fu > 0 ? new Rect2D(hw + m, area.Z0, hw + m + lw, area.Z1) : new Rect2D(-hw - m - lw, area.Z0, -hw - m, area.Z1);
        }
        else
        {
            area = fv > 0 ? area with { Z1 = area.Z1 + lw } : area with { Z0 = area.Z0 - lw };
            lane = fv > 0 ? new Rect2D(area.X0, hd + m, area.X1, hd + m + lw) : new Rect2D(area.X0, -hd - m - lw, area.X1, -hd - m);
        }
        int top;
        var laneAxis = alongX ? box.AxisU : box.AxisV;
        if (Math.Abs(side.Dot(front)) > 0.5f)
        {
            // along the street side or the back: the ramp comes up at the end nearer the gate, so a
            // machine from the gate has the shorter way round
            int end = Math.Abs(gateAlong) > 0.5f ? Math.Sign(gateAlong) : Fnv.Unit(key + "|site|ramp") < 0.5 ? -1 : 1;
            top = end * Math.Sign(along.Dot(laneAxis));
        }
        // along a side: it comes up at its street end, into the yard
        else top = Math.Sign(front.Dot(laneAxis));

        // inside the tile, with the seam margin
        float max = (float)ChunkFormat.TileSizeM - PitSeam;
        foreach (var c in InPlan(box, area).Corners())
            if (c.X < PitSeam || c.Y < PitSeam || c.X > max || c.Y > max) return null;
        // nothing dug under a road or a neighbour: the ring round the building, a metre apart
        // (both edges sampled: a metre or less apart, from one to the other)
        int nx = Math.Max(1, (int)MathF.Ceiling(area.Width)), nz = Math.Max(1, (int)MathF.Ceiling(area.Depth));
        for (int i = 0; i <= nx; i++)
            for (int j = 0; j <= nz; j++)
            {
                float px = area.X0 + area.Width * i / nx, pz = area.Z0 + area.Depth * j / nz;
                if (Math.Abs(px) < hw + 0.5f && Math.Abs(pz) < hd + 0.5f) continue;
                if (blocked(box.Center + box.AxisU * px + box.AxisV * pz)) return null;
            }
        return new SitePit(area, lane, alongX, top, Basements(key, box, target),
            new Vector2(Math.Abs(fu) > 0.5f ? Math.Sign(fu) : 0, Math.Abs(fv) > 0.5f ? Math.Sign(fv) : 0));
    }

    /// <summary>Which wall a pit's lane is along, seen from the street: 0 the front, 1 a side, 2 the back; -1 no pit.</summary>
    public static int LaneWall(SitePit? pit, PlanBox box, Vector2 front)
    {
        if (pit == null) return -1;
        float d = pit.Side.Dot(new Vector2(front.Dot(box.AxisU), front.Dot(box.AxisV)));
        return d > 0.5f ? 0 : d < -0.5f ? 2 : 1;
    }

    /// <summary>
    /// Whether a site's materials have a way in for a machine (#619): a strip <see cref="MaterialsApproach"/>
    /// deep on the side the pallets face (away from the building), out of the building or its pit and clear of
    /// every other zone and machine. Where the yard has no room for one, the pallets of a site with a pit
    /// cannot be reached.
    /// </summary>
    public static bool MaterialsWayIn(ConstructionSite s)
    {
        if (s.Zones.FirstOrDefault(z => z.Kind == SiteZoneKind.Materials) is not { } m) return false;
        var r = m.Rect;
        var keep = s.Pit is { } p ? InPlan(s.Box, p.Area) : new SiteRect(s.Box.Center, s.Box.AxisU, s.Box.Width, s.Box.Depth);
        // the side the pallets face: away from the building (the dressing turns them to)
        int sign = (r.Center - s.Box.Center).Dot(r.AxisV) >= 0 ? 1 : -1;
        var way = new SiteRect(r.Center + r.AxisV * (sign * (r.Depth + MaterialsApproach) / 2), r.AxisU, r.Width, MaterialsApproach - 0.1f);
        return !way.Overlaps(keep) && !s.Zones.Any(z => z.Kind != SiteZoneKind.Materials && way.Overlaps(z.Rect))
            && !s.Machines.Any(x => way.Contains(x.At));
    }

    /// <summary>The ground kept clear past a pit's ramp top, in the site frame.</summary>
    public static Rect2D Apron(SitePit pit) => pit.LaneAlongX
        ? pit.TopEnd > 0 ? new Rect2D(pit.Area.X1, pit.Lane.Z0, pit.Area.X1 + RampApron, pit.Lane.Z1)
            : new Rect2D(pit.Area.X0 - RampApron, pit.Lane.Z0, pit.Area.X0, pit.Lane.Z1)
        : pit.TopEnd > 0 ? new Rect2D(pit.Lane.X0, pit.Area.Z1, pit.Lane.X1, pit.Area.Z1 + RampApron)
            : new Rect2D(pit.Lane.X0, pit.Area.Z0 - RampApron, pit.Lane.X1, pit.Area.Z0);

    /// <summary>
    /// Plans the site of one <see cref="BuildingKind.UnderConstruction"/> building, or null for any
    /// other kind.
    /// </summary>
    /// <param name="key">The building's key (<c>BuildingKey.ToString()</c>), the seed of every roll.</param>
    /// <param name="street">The street point the site faces (<c>BuildingFootprint.StreetNear</c>), if any.</param>
    /// <param name="blocked">
    /// Whether a plan point is on a road or inside another building. The site's own box is kept out
    /// by the planner itself; the callback may report it too.
    /// </param>
    /// <param name="undiggable">
    /// Whether a plan point may not be dug for the pit (#619): <see cref="SiteObstacles.Undiggable"/>,
    /// which keeps it off a road's sidewalks too. Null: <paramref name="blocked"/>.
    /// </param>
    public static ConstructionSite? Plan(string key, Building b, PlanBox box, Vector2? street, Func<Vector2, bool> blocked,
        Func<Vector2, bool>? undiggable = null)
    {
        if (b.Kind != BuildingKind.UnderConstruction || box.Width < 1f || box.Depth < 1f) return null;
        double R(string q) => Fnv.Unit(key + "|site|" + q);

        var (phase, measured, storey, built, target) = Progress(key, b, box);
        bool small = IsSmall(box, target);

        // ---- the frame: the box side facing the street is the front --------------------------
        var toStreet = street is { } s && (s - box.Center).LengthSquared() > 1e-4f
            ? (s - box.Center).Normalized()
            : Vector2.FromAngle((float)(R("front") * Math.Tau));
        Vector2 front = box.AxisU;
        float best = float.MinValue;
        foreach (var n in new[] { box.AxisU, -box.AxisU, box.AxisV, -box.AxisV })
            if (n.Dot(toStreet) > best) { best = n.Dot(toStreet); front = n; }
        var along = new Vector2(-front.Y, front.X);
        float hf = Math.Abs(front.Dot(box.AxisU)) > 0.5f ? box.Width / 2 : box.Depth / 2;
        float hs = Math.Abs(front.Dot(box.AxisU)) > 0.5f ? box.Depth / 2 : box.Width / 2;

        // ---- the pit (#619): the ground dug out under the building, the ramp along the front --
        var pit = Pit(key, box, phase, built, target, small, front, along,
            street is { } sp ? (sp - box.Center).Dot(along) : 0f, undiggable ?? blocked);

        // ---- the site: deep at the front for the yard, narrow round the rest -------------------
        float size = Math.Clamp((box.Area - 150f) / 1500f, 0f, 1f);
        // the ramp's lane takes a strip of the yard on its wall's side: the site grows by as much there
        int laneWall = LaneWall(pit, box, front);
        float laneW = pit?.LaneWidth ?? 0f;
        float frontM = (small ? 8f : 14f + 8f * size) + 2f * (float)R("front-margin") + (laneWall == 0 ? laneW : 0f);
        float sideM = 4.5f + 2f * (float)R("side-margin") + 3f * size;
        // a ramp along the front or the back comes up at a side: room there for a machine to come
        // round and turn into it
        if (laneWall is 0 or 2) sideM = Math.Max(sideM, small ? PitSideSmall : PitSide);
        if (laneWall == 1) sideM += laneW;
        float backM = 3f + (float)R("back-margin") + (laneWall == 2 ? laneW : 0f);
        var area = new SiteRect(box.Center + front * ((frontM - backM) / 2), along,
            2 * hs + 2 * sideM, 2 * hf + frontM + backM);

        // the gate: on the front edge, in line with the street where it can be
        float gateWidth = small ? 4.5f : 6f;
        float frontEdge = hf + frontM;
        float gateAlong = street is { } st ? (st - box.Center).Dot(along) : 0f;
        gateAlong = Math.Clamp(gateAlong, -area.Width / 2 + gateWidth / 2 + 1f, area.Width / 2 - gateWidth / 2 - 1f);
        var gate = box.Center + front * frontEdge + along * gateAlong;

        // the building and its scaffolding, or the whole pit with its ramp
        var keepOut = pit == null
            ? new SiteRect(box.Center, box.AxisU, box.Width + 2 * Scaffold, box.Depth + 2 * Scaffold)
            : InPlan(box, pit.Area);
        // the way in, from the gate to the building, kept clear of everything
        float laneLength = frontM - Scaffold;
        var lane = new SiteRect(gate - front * (laneLength / 2), along, gateWidth, laneLength);
        var clear = new List<SiteRect> { keepOut, lane };
        if (pit != null) clear.Add(InPlan(box, Apron(pit)));

        var hoarding = Hoarding(area, gate, gateWidth, front, blocked);

        // ---- what stands in it ---------------------------------------------------------------
        // a 0.5 m grid on a house plot, 1 m on a block, 1.5 m on the biggest: a few thousand points each
        var placer = new Placer(area, clear, blocked, small ? 0.5f : area.Width * area.Depth > 6000f ? 1.5f : 1f);
        var cranes = Cranes(key, box, phase, small, target * storey, front, placer);

        var zones = new List<SiteZone>();
        // a zone's width runs along the street; on a tight site it turns to run down a side, and
        // failing that it shrinks: a smaller heap is still a heap, none is a missing site
        void Zone(SiteZoneKind kind, float w, float d, Func<Vector2, float> score)
        {
            // the materials on a site with a pit (#619): with a way in kept for a machine across the
            // pallets, where the yard the machines left has room for one; the pit took the scaffolding's
            // ground that way was. Failing that, where they stood before (decided with the user: the
            // machines keep their room first)
            if (kind == SiteZoneKind.Materials && pit != null)
                foreach (float scale in new[] { 1f, 0.7f })
                    foreach (var axis in new[] { front, along })
                        if (placer.PlaceWithWay(w * scale, d * scale, MaterialsApproach, axis, score, box.Center) is { } r)
                        {
                            zones.Add(new SiteZone(kind, r));
                            return;
                        }
            foreach (float scale in new[] { 1f, 0.7f })
                foreach (var axis in new[] { front, along })
                    if (placer.Place(w * scale, d * scale, axis, score) is { } r)
                    {
                        zones.Add(new SiteZone(kind, r));
                        return;
                    }
        }
        // containers stand two high: one stack on a house plot, two side by side on a big site
        int stacks = small ? 1 : box.Area > 1500f ? 2 : 1 + (R("office") < 0.5 ? 1 : 0);
        Zone(SiteZoneKind.Office, 6.4f, 2.8f * stacks, p => p.DistanceTo(gate));
        var office = zones.Count > 0 ? zones[^1].Rect.Center : gate;
        Zone(SiteZoneKind.Toilets, 2.8f, 1.6f, p => p.DistanceTo(office));
        if (phase != SitePhase.ToppedOut)
        {
            // the dug-out soil waits for the backfill, as far from the gate as the site allows; it is
            // the thing that says "foundations" from the street, so it is found room before the rest
            bool dug = phase == SitePhase.Foundations;
            float hw = dug ? (small ? 7f : 9f + 5f * size) : 5f, hd = dug ? (small ? 5f : 7f + 3f * size) : 4f;
            Zone(SiteZoneKind.SoilHeap, hw, hd, p => -p.DistanceTo(gate));
        }
        // the machines before the materials and the skips: they are what a player comes for, and
        // one that finds no room leaves a gap in the ordinals
        var machines = new List<MachineSlot>();
        var roles = Roles(phase, small);
        for (int k = 0; k < roles.Count; k++)
        {
            var role = roles[k];
            if (role.Chance < 1 && R($"machine{k}") >= role.Chance) continue;
            var (w, d) = MachineSize(role.Role);
            // an excavator works at the dig, everything else waits by the way in
            bool atDig = role.Role is MachineRole.Excavator or MachineRole.MiniExcavator && phase == SitePhase.Foundations;
            var goal = atDig ? box.Center : gate;
            Func<Vector2, float> score = p => p.DistanceTo(goal);
            // the long side runs out from the building, so it drives straight out of the gate; failing
            // that it parks along the building, down one side
            var axis = front;
            var r = placer.Place(w, d, front, score);
            if (r == null)
            {
                axis = along;
                r = placer.Place(w, d, along, score);
            }
            if (r is not { } at) continue;
            // nose towards the work or the gate: yaw 0 faces -Z, so the heading whose facing is v is
            // atan2(-v.x, -v.y)
            var facing = axis.Dot(goal - at.Center) >= 0 ? axis : -axis;
            machines.Add(new MachineSlot(role.Role, k, at.Center, Mathf.Atan2(-facing.X, -facing.Y)));
        }


        // materials go where the crane's hook reaches them, or failing a crane by the building
        var hook = cranes.Count > 0 ? cranes[0].Base : box.Center;
        Zone(SiteZoneKind.Materials, small ? 6f : 8f + 4f * size, small ? 4f : 6f, p => p.DistanceTo(hook));
        Zone(SiteZoneKind.Skips, small ? 4.4f : 9f, 2.6f, p => p.DistanceTo(gate) + 4f);

        return new ConstructionSite(key, phase, box, b.MinY, measured, storey, built, target,
            area, front, gate, gateWidth, hoarding, zones, cranes, machines, pit);
    }

    /// <summary>
    /// The phase and the storeys of a site from its surveyed height and its GWR floor count:
    /// (phase, measured height, storey height, storeys built, storeys it will have).
    /// </summary>
    public static (SitePhase Phase, float Measured, float Storey, int Built, int Target) Progress(string key, Building b, PlanBox box)
    {
        float h = Math.Max(0f, box.Eave - b.MinY);
        int floors = b.Floors;
        // no floor count (8% of real sites): as high as a building of that footprint usually goes
        int guess = box.Area < 300f ? 2 + (int)(Fnv.Unit(key + "|site|floors") * 2)
            : box.Area < 1000f ? 3 + (int)(Fnv.Unit(key + "|site|floors") * 3)
            : 4 + (int)(Fnv.Unit(key + "|site|floors") * 4);

        if (h < FoundationsBelow)
            return (SitePhase.Foundations, h, Storey, 0, floors > 0 ? floors : guess);

        int built = Math.Max(1, (int)(h / Storey + 0.15f));
        bool topped = floors > 0
            ? h >= floors * Storey - ToppedOutWithin
            : Fnv.Unit(key + "|site|topped") < 0.35;
        if (topped)
        {
            // as many storeys as GWR says, or more where that would make each one a hall
            int n = Math.Max(Math.Max(1, floors), (int)MathF.Round(h / MaxStorey + 0.49f));
            return (SitePhase.ToppedOut, h, h / n, n, n);
        }
        return (SitePhase.Shell, h, Storey, built, Math.Max(floors > 0 ? floors : guess, built + 1));
    }

    /// <summary>The panels round the site, merged into runs, less the gate and anything on a road or in a building.</summary>
    private static List<HoardingRun> Hoarding(SiteRect area, Vector2 gate, float gateWidth, Vector2 front, Func<Vector2, bool> blocked)
    {
        var runs = new List<HoardingRun>();
        var c = area.Corners();
        for (int e = 0; e < 4; e++)
        {
            Vector2 a = c[e], z = c[(e + 1) % 4];
            float length = a.DistanceTo(z);
            int n = Math.Max(1, (int)MathF.Ceiling(length / Panel));
            Vector2? start = null, end = null;
            for (int k = 0; k < n; k++)
            {
                var p0 = a.Lerp(z, (float)k / n);
                var p1 = a.Lerp(z, (float)(k + 1) / n);
                var mid = (p0 + p1) / 2;
                bool open = blocked(p0) || blocked(mid) || blocked(p1)
                    // the gate: the panels across it, on the front edge only
                    || Math.Abs((mid - gate).Dot(front)) < 0.1f && mid.DistanceTo(gate) < gateWidth / 2 + Panel / 2;
                if (open)
                {
                    if (start is { } s0 && end is { } e0) runs.Add(new HoardingRun(s0, e0));
                    start = end = null;
                    continue;
                }
                start ??= p0;
                end = p1;
            }
            if (start is { } s1 && end is { } e1) runs.Add(new HoardingRun(s1, e1));
        }
        return runs;
    }

    /// <summary>
    /// The cranes: none on a site that has finished with them, one self-erecting crane on a house
    /// plot, one to three tower cranes on anything bigger, one per share of the building's long side,
    /// each with a jib that reaches the corners of its share: outside the scaffolding where there is
    /// room and the jib reaches from there, otherwise inside the building.
    /// </summary>
    private static List<CraneSpot> Cranes(string key, PlanBox box, SitePhase phase, bool small, float targetHeight,
        Vector2 front, Placer placer)
    {
        double R(string q) => Fnv.Unit(key + "|site|" + q);
        var cranes = new List<CraneSpot>();
        // a topped-out shell often has its crane still, until the roof and the facade are done
        if (phase == SitePhase.ToppedOut && R("crane-kept") >= 0.4) return cranes;
        // a house's foundations are dug and poured from the ground as often as by crane
        if (small && phase == SitePhase.Foundations && R("crane-early") >= 0.6) return cranes;

        var kind = small ? CraneKind.SelfErecting : CraneKind.Tower;
        float side = kind == CraneKind.Tower ? TowerBase : SelfErectingBase;
        int count = small ? 1 : 1 + (box.Long > 55f ? 1 : 0) + (box.Long > 100f ? 1 : 0);
        var lng = box.LongAxis;
        var across = new Vector2(-lng.Y, lng.X);
        if (across.Dot(front) < 0) across = -across;
        float halfLong = box.Long / 2, halfShare = halfLong / count;
        for (int k = 0; k < count; k++)
        {
            // the k-th share of a LONG wall, just outside the scaffolding: the street side's long
            // wall first, the other one if it has room. Spreading along the short wall (the street
            // side of a deep building) bunched three cranes on 40 m and maxed out every jib.
            var share = box.Center + lng * (-halfLong + (2 * k + 1) * halfShare);
            float out_ = box.Short / 2 + Scaffold + side / 2 + 0.5f;
            Vector2 near = share + across * out_, far = share - across * out_;
            var shareCorners = new[]
            {
                share - lng * halfShare - across * (box.Short / 2), share + lng * halfShare - across * (box.Short / 2),
                share + lng * halfShare + across * (box.Short / 2), share - lng * halfShare + across * (box.Short / 2),
            };
            float Reach(Vector2 at) => shareCorners.Max(c => c.DistanceTo(at));

            Vector2 at;
            bool inside = false;
            var r = placer.Place(side, side, front, p => Math.Min(p.DistanceTo(near), p.DistanceTo(far) + 6f));
            if (r is { } outside && kind == CraneKind.Tower
                && (Math.Min(outside.Center.DistanceTo(near), outside.Center.DistanceTo(far)) > 6f
                    || Reach(outside.Center) + 4f > TowerJibMax))
            {
                // no room by its share of the wall, or a jib from there cannot reach the far side:
                // the crane stands in the building, through an opening the slabs are cast round,
                // as it does on a tight plot or a deep block
                placer.Release(outside);
                r = null;
            }
            if (r is { } placed) at = placed.Center;
            else if (kind == CraneKind.Tower) { at = share; inside = true; }
            else continue;

            float jib = kind == CraneKind.Tower
                ? Math.Clamp(Reach(at) + 4f, TowerJibMin, TowerJibMax)
                : Math.Clamp(Reach(at) + 2f, SelfErectingJibMin, SelfErectingJibMax);
            float hook = kind == CraneKind.Tower
                ? Math.Max(20f, targetHeight + 8f + 4f * (float)R($"hook{k}"))
                : Math.Max(12f, targetHeight + 4f);
            cranes.Add(new CraneSpot(kind, at, hook, jib, (float)(R($"rest{k}") * Math.Tau), inside));
        }
        return cranes;
    }

    /// <summary>The machines a site of that phase has, with the chance of each: the ordinal is the place in this list.</summary>
    public static IReadOnlyList<(MachineRole Role, double Chance)> Roles(SitePhase phase, bool small) => (phase, small) switch
    {
        (SitePhase.Foundations, false) => new[]
        {
            (MachineRole.Excavator, 1.0), (MachineRole.WheelLoader, 1.0), (MachineRole.Tipper, 1.0),
            (MachineRole.Tipper, 0.5), (MachineRole.Van, 1.0),
        },
        (SitePhase.Foundations, true) => new[]
        {
            (MachineRole.MiniExcavator, 1.0), (MachineRole.MiniDumper, 1.0), (MachineRole.Tipper, 0.5),
            (MachineRole.Van, 1.0),
        },
        (SitePhase.Shell, false) => new[]
        {
            (MachineRole.Mixer, 1.0), (MachineRole.Telehandler, 1.0), (MachineRole.MiniDumper, 1.0),
            (MachineRole.Van, 1.0), (MachineRole.Excavator, 0.3),
        },
        (SitePhase.Shell, true) => new[]
        {
            (MachineRole.MiniDumper, 1.0), (MachineRole.Telehandler, 0.5), (MachineRole.Van, 1.0),
        },
        (SitePhase.ToppedOut, false) => new[]
        {
            (MachineRole.Telehandler, 1.0), (MachineRole.Roller, 0.4), (MachineRole.Van, 1.0), (MachineRole.Van, 0.7),
        },
        _ => new[] { (MachineRole.Van, 1.0), (MachineRole.MiniDumper, 0.5) },
    };

    /// <summary>The ground a machine takes when parked, m across by m long (an excavator with its arm folded).</summary>
    public static (float Width, float Length) MachineSize(MachineRole role) => role switch
    {
        MachineRole.Excavator => (3.0f, 9.5f),
        MachineRole.WheelLoader => (3.0f, 8.2f),
        MachineRole.Tipper => (2.6f, 9.6f),
        MachineRole.Mixer => (2.6f, 10.2f),
        MachineRole.Telehandler => (2.5f, 6.0f),
        MachineRole.MiniExcavator => (1.8f, 4.6f),
        MachineRole.MiniDumper => (1.4f, 3.0f),
        MachineRole.Roller => (1.8f, 4.4f),
        _ => (2.1f, 6.0f),   // a van
    };

    /// <summary>
    /// Finds room for rectangles inside the site: on a grid, clear of the building and its
    /// scaffolding, the way in, everything placed before, the hoarding line, roads and other
    /// buildings, at the best score. The grid is tried best score first and the first point that
    /// fits wins, so a site costs a handful of obstacle tests per thing, not one per grid point;
    /// ties go to grid order (a stable sort), which is the same on every peer.
    /// </summary>
    private sealed class Placer(SiteRect area, IReadOnlyList<SiteRect> clear, Func<Vector2, bool> blocked, float step)
    {
        private readonly List<SiteRect> _taken = new();
        private readonly List<Vector2> _grid = Grid(area, step);
        /// <summary>Between two things in the yard, and between a thing and the hoarding: room to walk.</summary>
        private const float Gap = 0.8f;

        private static List<Vector2> Grid(SiteRect area, float step)
        {
            var grid = new List<Vector2>();
            float hw = area.Width / 2 - Gap, hd = area.Depth / 2 - Gap;
            for (float a = -hw; a <= hw; a += step)
                for (float d = -hd; d <= hd; d += step)
                    grid.Add(area.Center + area.AxisU * a + area.AxisV * d);
            return grid;
        }

        /// <param name="axis">The direction <paramref name="depth"/> runs in.</param>
        public SiteRect? Place(float width, float depth, Vector2 axis, Func<Vector2, float> score)
        {
            // as a SiteRect, Width runs along AxisU and Depth along AxisV = (-u.y, u.x) = axis
            var u = new Vector2(axis.Y, -axis.X);
            var order = new int[_grid.Count];
            var scores = new float[_grid.Count];
            for (int k = 0; k < order.Length; k++)
            {
                order[k] = k;
                scores[k] = score(_grid[k]);
            }
            Array.Sort(order, (a, b) => scores[a] != scores[b] ? scores[a].CompareTo(scores[b]) : a.CompareTo(b));
            foreach (int k in order)
            {
                var r = new SiteRect(_grid[k], u, width, depth);
                if (!Fits(r)) continue;
                _taken.Add(r);
                return r;
            }
            return null;
        }

        /// <summary>
        /// Like <see cref="Place"/>, with a way in <paramref name="way"/> m deep along its long side away
        /// from the <paramref name="building"/> kept for a machine (#619: the dressing turns the materials'
        /// pallets to face that way): the way may lie over the clear ways (the gate's lane, the ramp's
        /// apron) but not over the first clear rectangle (the building or its pit), nothing placed, the
        /// hoarding's line, a road or a building; it is kept as taken.
        /// </summary>
        public SiteRect? PlaceWithWay(float width, float depth, float way, Vector2 axis, Func<Vector2, float> score, Vector2 building)
        {
            var u = new Vector2(axis.Y, -axis.X);
            var order = new int[_grid.Count];
            var scores = new float[_grid.Count];
            for (int k = 0; k < order.Length; k++)
            {
                order[k] = k;
                scores[k] = score(_grid[k]);
            }
            Array.Sort(order, (a, b) => scores[a] != scores[b] ? scores[a].CompareTo(scores[b]) : a.CompareTo(b));
            foreach (int k in order)
            {
                var r = new SiteRect(_grid[k], u, width, depth);
                if (!Fits(r)) continue;
                int sign = (r.Center - building).Dot(axis) >= 0 ? 1 : -1;
                var w = new SiteRect(r.Center + axis * (sign * (depth + way) / 2), u, width, way);
                if (!WayFits(w)) continue;
                _taken.Add(r);
                _taken.Add(w);
                return r;
            }
            return null;
        }

        private bool WayFits(SiteRect w)
        {
            if (w.Overlaps(clear[0])) return false;
            foreach (var t in _taken)
                if (w.Overlaps(t)) return false;
            var corners = w.Corners();
            foreach (var p in corners)
                if (!area.Contains(p, -Gap)) return false;
            foreach (var p in corners)
                if (blocked(p)) return false;
            return !blocked(w.Center);
        }

        /// <summary>Gives back a rectangle <see cref="Place"/> took, when the caller decided against it.</summary>
        public void Release(SiteRect r) => _taken.Remove(r);

        private bool Fits(SiteRect r)
        {
            // the cheap tests first: the obstacle callback is the costly one
            foreach (var c in clear)
                if (r.Overlaps(c)) return false;
            foreach (var t in _taken)
                if (r.Overlaps(t, Gap)) return false;
            var corners = r.Corners();
            foreach (var p in corners)
                if (!area.Contains(p, -Gap)) return false;
            if (blocked(r.Center)) return false;
            foreach (var p in corners)
                if (blocked(p)) return false;
            return true;
        }
    }
}

/// <summary>
/// What a site may not stand on (#606): the other buildings and the roads round one building,
/// gathered once so that asking about a point costs a few tests, not a walk over the tile. Pure,
/// from the tile's own bytes, the same on every peer. Footpaths count: a site does not fence one off.
/// </summary>
public sealed class SiteObstacles
{
    private readonly List<PlanBox> _boxes = new();
    private readonly List<(Vector2 A, Vector2 B, float Half)> _roads = new();
    private readonly float _clearance;

    /// <param name="boxes">The tile's plan boxes, by building (<c>BuildingTypes.For(tile).Boxes</c>).</param>
    /// <param name="self">The site's own building, left out: the planner keeps clear of it itself.</param>
    /// <param name="clearance">How close to a road's edge or a wall a point may come.</param>
    public SiteObstacles(IReadOnlyList<PlanBox?> boxes, int self, RoadTile? roads, float clearance = 0.3f)
    {
        _clearance = clearance;
        if (boxes[self] is not { } own) return;
        // far enough to take in the deepest yard a planner can lay out, and a corner of it
        float reach = MathF.Sqrt(own.Width * own.Width + own.Depth * own.Depth) / 2 + 30f;
        for (int i = 0; i < boxes.Count; i++)
            if (i != self && boxes[i] is { } b && b.DistanceTo(own.Center) < reach) _boxes.Add(b);
        if (roads == null) return;
        foreach (var s in roads.Segments)
        {
            if (RoadFormat.IsAerial(s.Class)) continue;
            float half = s.Width * 0.5f;
            float side = Math.Max(s.Attributes.Left.Reach, s.Attributes.Right.Reach);
            for (int k = 0; k + 1 < s.PointCount; k++)
            {
                var a = new Vector2(s.Points[k * 3], s.Points[k * 3 + 2]);
                var c = new Vector2(s.Points[k * 3 + 3], s.Points[k * 3 + 5]);
                if (Distance(own.Center, a, c) < reach + half + side)
                {
                    _roads.Add((a, c, half));
                    _sides.Add(side);
                }
            }
        }
    }

    /// <summary>Each road piece's sidewalks, verges and paths beside its carriageway, m: its wider side.</summary>
    private readonly List<float> _sides = new();

    public int Count => _boxes.Count + _roads.Count;

    /// <summary>Whether a plan point is on a road or in another building.</summary>
    public bool Blocked(Vector2 p)
    {
        foreach (var b in _boxes)
            if (b.DistanceTo(p) < _clearance) return true;
        foreach (var (a, c, half) in _roads)
            if (Distance(p, a, c) < half + _clearance) return true;
        return false;
    }

    /// <summary>
    /// Whether a plan point may not be dug (#619): in another building, or on a road or its
    /// sidewalk, verge or path, which stand beside the carriageway and would be left over the pit.
    /// </summary>
    public bool Undiggable(Vector2 p)
    {
        foreach (var b in _boxes)
            if (b.DistanceTo(p) < _clearance) return true;
        for (int k = 0; k < _roads.Count; k++)
        {
            var (a, c, half) = _roads[k];
            if (Distance(p, a, c) < half + _sides[k] + _clearance) return true;
        }
        return false;
    }

    private static float Distance(Vector2 p, Vector2 a, Vector2 c)
    {
        var ac = c - a;
        float len2 = ac.LengthSquared();
        float t = len2 < 1e-6f ? 0f : Math.Clamp((p - a).Dot(ac) / len2, 0f, 1f);
        return p.DistanceTo(a + ac * t);
    }
}
