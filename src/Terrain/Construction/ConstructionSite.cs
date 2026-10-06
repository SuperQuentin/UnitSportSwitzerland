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
    IReadOnlyList<MachineSlot> Machines)
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

    public static bool IsSmall(PlanBox box, int targetStoreys) => box.Area < SmallArea && targetStoreys <= SmallStoreys;

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
    public static ConstructionSite? Plan(string key, Building b, PlanBox box, Vector2? street, Func<Vector2, bool> blocked)
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

        // ---- the site: deep at the front for the yard, narrow round the rest -------------------
        float size = Math.Clamp((box.Area - 150f) / 1500f, 0f, 1f);
        float frontM = (small ? 8f : 14f + 8f * size) + 2f * (float)R("front-margin");
        float sideM = 4.5f + 2f * (float)R("side-margin") + 3f * size;
        float backM = 3f + (float)R("back-margin");
        var area = new SiteRect(box.Center + front * ((frontM - backM) / 2), along,
            2 * hs + 2 * sideM, 2 * hf + frontM + backM);

        // the gate: on the front edge, in line with the street where it can be
        float gateWidth = small ? 4.5f : 6f;
        float frontEdge = hf + frontM;
        float gateAlong = street is { } st ? (st - box.Center).Dot(along) : 0f;
        gateAlong = Math.Clamp(gateAlong, -area.Width / 2 + gateWidth / 2 + 1f, area.Width / 2 - gateWidth / 2 - 1f);
        var gate = box.Center + front * frontEdge + along * gateAlong;

        var keepOut = new SiteRect(box.Center, box.AxisU, box.Width + 2 * Scaffold, box.Depth + 2 * Scaffold);
        // the way in, from the gate to the building, kept clear of everything
        float laneLength = frontM - Scaffold;
        var lane = new SiteRect(gate - front * (laneLength / 2), along, gateWidth, laneLength);

        var hoarding = Hoarding(area, gate, gateWidth, front, blocked);

        // ---- what stands in it ---------------------------------------------------------------
        // a 0.5 m grid on a house plot, 1 m on a block, 1.5 m on the biggest: a few thousand points each
        var placer = new Placer(area, keepOut, lane, blocked, small ? 0.5f : area.Width * area.Depth > 6000f ? 1.5f : 1f);
        var cranes = Cranes(key, box, phase, small, target * storey, front, placer);

        var zones = new List<SiteZone>();
        // a zone's width runs along the street; on a tight site it turns to run down a side, and
        // failing that it shrinks: a smaller heap is still a heap, none is a missing site
        void Zone(SiteZoneKind kind, float w, float d, Func<Vector2, float> score)
        {
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
            area, front, gate, gateWidth, hoarding, zones, cranes, machines);
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
    private sealed class Placer(SiteRect area, SiteRect keepOut, SiteRect lane, Func<Vector2, bool> blocked, float step)
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

        /// <summary>Gives back a rectangle <see cref="Place"/> took, when the caller decided against it.</summary>
        public void Release(SiteRect r) => _taken.Remove(r);

        private bool Fits(SiteRect r)
        {
            // the cheap tests first: the obstacle callback is the costly one
            if (r.Overlaps(keepOut) || r.Overlaps(lane)) return false;
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
            for (int k = 0; k + 1 < s.PointCount; k++)
            {
                var a = new Vector2(s.Points[k * 3], s.Points[k * 3 + 2]);
                var c = new Vector2(s.Points[k * 3 + 3], s.Points[k * 3 + 5]);
                if (Distance(own.Center, a, c) < reach + half) _roads.Add((a, c, half));
            }
        }
    }

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

    private static float Distance(Vector2 p, Vector2 a, Vector2 c)
    {
        var ac = c - a;
        float len2 = ac.LengthSquared();
        float t = len2 < 1e-6f ? 0f : Math.Clamp((p - a).Dot(ac) / len2, 0f, 1f);
        return p.DistanceTo(a + ac * t);
    }
}
