using Godot;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// Where one of a building's front doors is, in tile-local metres (X east, Y altitude, Z south —
/// the frame the building triangles and the chunk node share). <see cref="Outward"/> is horizontal.
/// </summary>
public readonly record struct DoorSpot(int Index, Vector3 Position, Vector3 Outward, float Width, float Height)
{
    /// <summary>Which of its building's doors this is: 0 the main one, 1 and up the extra ones (#498).</summary>
    public int Slot { get; init; }

    /// <summary>
    /// How this door's leaf moves, which is a fact about the door and not about its building
    /// (#498): a barn's side door is <see cref="DoorHang.Inward"/> on an Agricultural building.
    /// </summary>
    public DoorHang Hang { get; init; }

    /// <summary>
    /// Whether a ground vehicle is driven through this door: a barn's or a garage's main door, or
    /// a loading bay. Never a pedestrian side door, however big the barn it is on.
    /// </summary>
    public bool Vehicle { get; init; }

    /// <summary>
    /// What joins a garage door to the road in front of it (#558): a pavement with bollards or an
    /// access road. <see cref="LinkKind.None"/> (the default) on every other door.
    /// </summary>
    public GarageLink Link { get; init; }

    /// <summary>The building's kind, so door consumers need not keep the tile.</summary>
    public BuildingKind Kind { get; init; }
    /// <summary>A bank (<see cref="BuildingFootprint.IsBank"/>): a sign over the door, a teller desk inside.</summary>
    public bool Bank { get; init; }
    /// <summary>What it sells, if it is a shop (#273, <see cref="BuildingFootprint.ShopOf"/>): a sign over the door, a counter inside.</summary>
    public Loot.ShopType Shop { get; init; }

    public DoorKey KeyIn(TileId tile) => new(tile.E, tile.N, Index, Slot);
}

/// <summary>
/// A building's plan-view box and its door, derived from nothing but its wall triangles. The
/// <c>.bldg</c> format carries no outline, no orientation and no entrance, so all three are
/// reconstructed here — deterministically, because every client draws the door from this and
/// the server checks entry requests against it.
/// </summary>
public sealed record Footprint(
    BuildingKey Key, BuildingKind Kind,
    Vector2 Center, Vector2 AxisU, float Width, float Depth,
    DoorSpot Door)
{
    /// <summary>
    /// The building's other doors (#498), slot 1 and up: more entrances along a long facade and a
    /// side or back door, all of them plain pedestrian doors. Empty on a house or a shed.
    /// </summary>
    public IReadOnlyList<DoorSpot> Extra { get; init; } = Array.Empty<DoorSpot>();

    /// <summary>Every door of the building, the main one first.</summary>
    public IEnumerable<DoorSpot> Doors => Extra.Count == 0 ? [Door] : Extra.Prepend(Door);

    /// <summary>Back of the building, i.e. away from the door, horizontal unit (x, z).</summary>
    public Vector2 AxisV => new(-AxisU.Y, AxisU.X);

    /// <summary>
    /// Yaw of the interior frame: local +X maps to <see cref="AxisU"/>, local +Z to
    /// <see cref="AxisV"/>, which is a proper rotation (X × Y = Z) by construction.
    /// </summary>
    public float Yaw => Mathf.Atan2(-AxisU.Y, AxisU.X);

    /// <summary>The door's position along the entry wall, in interior-local X.</summary>
    public float EntryX
    {
        get
        {
            var d = new Vector2(Door.Position.X, Door.Position.Z) - Center;
            float half = Width * 0.5f - Door.Width * 0.5f - 0.35f;
            return half <= 0 ? 0 : Mathf.Clamp(d.Dot(AxisU), -half, half);
        }
    }
}

public static class BuildingFootprint
{
    /// <summary>
    /// Whether a building is a bank (#213). The data has no banks, so about one shop or office
    /// building in five of some size is one, by a stable hash of its key: a pure function of the tile,
    /// so the server's plan and every client's door sign agree without sending anything.
    /// </summary>
    public static bool IsBank(Footprint fp) => IsBank(fp.Key.ToString(), fp.Kind, fp.Width, fp.Depth);

    /// <summary>
    /// <see cref="IsBank(Footprint)"/> before the footprint exists: the garage door is placed in
    /// <see cref="Compute"/>, which has to know the generator will not plan the block as a bank
    /// (a bank has no ramp, and its garage door would read as locked, #558).
    /// </summary>
    public static bool IsBank(string key, BuildingKind kind, float width, float depth) =>
        kind == BuildingKind.Commercial && width * depth >= 60f && Math.Min(width, depth) >= 6f
        && (uint)InteriorGenerator.StableHash(key + "|bank") % 5 == 0;

    /// <summary>
    /// A building's shop (#273), the same pure function of the tile as <see cref="IsBank"/>, so the
    /// server's plan (<see cref="InteriorLayout.Shop"/>) and every client's door sign agree.
    /// <paramref name="rural"/>: its tile is countryside (<see cref="Loot.ShopTables.IsRural"/>).
    /// </summary>
    public static Loot.ShopType ShopOf(Footprint fp, bool rural) =>
        Loot.ShopTables.TypeFor(fp.Key.ToString(), fp.Kind, fp.Width * fp.Depth, IsBank(fp), rural);

    /// <summary>Rooms need somewhere to stand; a 1.5 m shed is still entered, as a 3 m box.</summary>
    public const float MinSide = 3.0f;

    /// <summary>Beyond this the interior is clamped — a 300 m warehouse is one hall either way.</summary>
    public const float MaxSide = 120f;

    /// <summary>A landmark store's entrance (#501): the glass front, as wide as a trolley crowd.</summary>
    public const float StoreDoorWidth = 7.0f, StoreDoorHeight = 3.4f;

    public static float DoorWidthFor(BuildingKind kind) => kind switch
    {
        BuildingKind.House or BuildingKind.Other => 1.0f,
        BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Civic or BuildingKind.Sacral => 1.8f,
        BuildingKind.Agricultural => 4.0f, // a barn's double door at the least, a hay wagon wide
        _ => 2.8f, // works, garages
    };

    /// <summary>A barn's pair spans its facade but for this much wall at each end, for the jambs.</summary>
    public const float BarnDoorMargin = 0.6f;

    /// <summary>However long the barn, its door stops here: a leaf is half of it, swinging out.</summary>
    public const float MaxBarnDoorWidth = 10f;

    /// <summary>
    /// Any door stops this far under the eave, for the lintel: on the long side the eave is the
    /// wall plate, on a gable end the level the slopes start from, so the door's top corners stay
    /// on wall instead of poking through the roof. A barn's door was the first to obey it (#135).
    /// </summary>
    public const float DoorUnderEave = 0.35f;

    /// <summary>
    /// No door is planned shorter than this, even under a very low eave: below it a doorway is a
    /// hatch, not a way in. A wall that cannot clear the kind's own door height is demoted
    /// (<see cref="LowWallPenalty"/>) so a taller wall takes the door where the building has one.
    /// </summary>
    public const float MinDoorHeight = 2.0f;

    /// <summary>A barn's pair is never planned shorter than this, low eave or not.</summary>
    public const float MinBarnDoorHeight = 2.5f;

    /// <summary>What a wall too low for its building's own door height loses in the ranking.</summary>
    public const float LowWallPenalty = 1.5f;

    /// <summary>
    /// A door on a wall whose eave is <paramref name="eave"/> metres above the sill: the kind's
    /// own <paramref name="want"/>, cut down to leave the lintel its wall, and never below the
    /// floor for its kind. Every door goes through here, so the baked facade door
    /// (<c>BuildingMeshBuilder.AppendDoor</c>) and the opening planned inside it are the same hole.
    /// </summary>
    public static float FitUnderEave(float want, float eave, bool barn) =>
        Math.Min(want, Math.Max(barn ? MinBarnDoorHeight : MinDoorHeight, eave - DoorUnderEave));

    public static float DoorHeightFor(BuildingKind kind) => kind switch
    {
        BuildingKind.Agricultural => 4.0f,
        _ => DoorWidthFor(kind) > 2f ? 2.8f : kind == BuildingKind.Sacral ? 2.6f : 2.1f,
    };

    /// <summary>
    /// The door's height in a building whose ground floor has <paramref name="clear"/> metres of
    /// headroom: a barn's as tall as its hall allows, a garage's too up to its usual height, others
    /// as <see cref="DoorHeightFor(BuildingKind)"/>. What a door on a *wall* ends up as is this cut
    /// down by <see cref="FitUnderEave"/> — every kind's, not just a barn's (#509) — and the
    /// interior plan takes the height the footprint settled on (<see cref="DoorSpot.Height"/>), so
    /// the two openings are the same hole.
    /// </summary>
    public static float DoorHeightFor(BuildingKind kind, float clear) => kind switch
    {
        BuildingKind.Agricultural => clear - 0.15f,
        BuildingKind.Garage => Math.Min(DoorHeightFor(kind), clear - 0.15f),
        _ => DoorHeightFor(kind),
    };

    /// <summary>
    /// Whether a kind of building's *main* door is driven through: a garage's or a barn's. It opens
    /// for a vehicle driving up to it, a vehicle crosses its portal, and the room behind keeps a
    /// lane clear. Per door this is <see cref="DoorSpot.Vehicle"/> (#498): a barn's pedestrian side
    /// door is not one, however big the barn, and a loading bay is one on an Industrial building.
    /// </summary>
    public static bool VehicleDoor(BuildingKind kind) => DoorBudget.VehicleFor(kind);

    /// <summary>
    /// How wide and tall a building's secondary pedestrian doors are (#498): the same as its main door where
    /// that is already a pedestrian one (a long shop front gets more shop doors), and a plain
    /// <see cref="DoorBudget.ServiceWidth"/> x <see cref="DoorBudget.ServiceHeight"/> door where the
    /// main one is wagon- or car-sized.
    /// </summary>
    public static (float Width, float Height) ServiceDoorFor(BuildingKind kind) =>
        DoorWidthFor(kind) <= 1.8f
            ? (DoorWidthFor(kind), DoorHeightFor(kind))
            : (DoorBudget.ServiceWidth, DoorBudget.ServiceHeight);

    /// <summary>The front door's leaf, linear: the facade's baked leaf and the interior's swinging one.</summary>
    public static Color DoorLeafColorFor(BuildingKind kind) => (kind switch
    {
        BuildingKind.Agricultural or BuildingKind.Annex => new Color(0.42f, 0.30f, 0.20f),
        BuildingKind.Industrial => new Color(0.46f, 0.50f, 0.54f),
        BuildingKind.Apartment or BuildingKind.Commercial or BuildingKind.Civic => new Color(0.22f, 0.26f, 0.30f),
        BuildingKind.Sacral => new Color(0.30f, 0.18f, 0.10f), // old oak
        _ => new Color(0.40f, 0.25f, 0.15f),
    }).SrgbToLinear();

    /// <summary>The door frame on the facade, linear.</summary>
    public static readonly Color DoorFrameColor = new Color(0.86f, 0.84f, 0.79f).SrgbToLinear();

    /// <summary>The front door's street-side handle, linear: on the facade and on the swinging leaf.</summary>
    public static readonly Color DoorHandleColor = DoorFrameColor * 0.7f;

    /// <summary>
    /// Every door of every building of a tile, in building order and, within a building, main door
    /// first (#498). A building with no wall to hang one on still contributes its empty door spot,
    /// so nothing downstream has to tell "no doors" from "not computed".
    /// </summary>
    public static DoorSpot[] ComputeDoors(BuildingTile tile, RoadTile? roads, ChunkGrid? grid)
    {
        var roadIndex = (RoadPoints.Build(roads), RoadPoints.Build(roads, paths: true));
        bool rural = Loot.ShopTables.IsRural(tile.Buildings.Count);
        var types = BuildingTypes.For(tile);
        var doors = new List<DoorSpot>(tile.Buildings.Count);
        for (int i = 0; i < tile.Buildings.Count; i++)
        {
            var kind = tile.Buildings[i].Kind;
            // a building site has no door: its shell is walked into in the world (#608)
            var fp = kind == BuildingKind.UnderConstruction ? null : Compute(tile, i, roadIndex, grid);
            // a landmark's shop is given by where it is, not by the key's hash (#501), and the main
            // door carries it so the sign over it reads IKEA rather than whatever the roll said
            var shop = types.TypeOf(i) == BuildingType.Ikea ? Loot.ShopType.Ikea
                : fp != null ? ShopOf(fp, rural)
                : Loot.ShopType.None;
            // an empty spot still names its own building, so the array can be read by Index
            // the sign over the door, and the shop behind it, belong to the building: they go on
            // its main door only, or a long shop front would grow a sign per entrance
            doors.Add((fp?.Door ?? new DoorSpot(i, Vector3.Zero, Vector3.Forward, 0f, 0f)) with
            {
                Kind = kind, Bank = fp != null && IsBank(fp), Shop = shop,
            });
            if (fp == null) continue;
            foreach (var extra in fp.Extra) doors.Add(extra with { Kind = kind });
        }
        return doors.ToArray();
    }

    public static Footprint? Compute(BuildingTile tile, int index, RoadTile? roads, ChunkGrid? grid) =>
        Compute(tile, index, (RoadPoints.Build(roads), RoadPoints.Build(roads, paths: true)), grid);

    /// <summary>A tile's street centrelines as a garage door's road link reads them (#694 probe).</summary>
    internal static List<GarageLink.Road> StreetRoads(RoadTile? roads) => RoadPoints.Build(roads).Roads;

    /// <summary>The street point a front door here would face, as <see cref="Compute"/> aims doors.</summary>
    public static Vector2? StreetNear(RoadTile? roads, Vector2 at) =>
        RoadPoints.Build(roads).Nearest(at, 60f) ?? RoadPoints.Build(roads, paths: true).Nearest(at, 40f);

    private static Footprint? Compute(BuildingTile tile, int index, (RoadPoints Streets, RoadPoints Paths) roads, ChunkGrid? grid)
    {
        var b = tile.Buildings[index];
        var key = new BuildingKey(tile.Id.E, tile.Id.N, index);

        // ---- plan box, shared with type detection ------------------------------------------
        var map = BuildingTypes.For(tile);
        if (map.Boxes[index] is not { } box) return null;
        Vector2 u = box.AxisU, center = box.Center;
        float w = box.Width, dpt = box.Depth;
        // a church's tower gets a church door, whatever kind the cadastre match made it
        var group = map.GroupOf(index);
        var kind = group?.Type == BuildingType.Church ? BuildingKind.Sacral : b.Kind;
        // the other solids of the same building: a door on a wall one of them stands against
        // would open into it
        var fellows = group?.Members.Where(m => m != index && map.Boxes[m] != null).Select(m => map.Boxes[m]!.Value).ToList();
        bool Covered(Vector2 xz) => fellows != null && fellows.Any(f => f.DistanceTo(xz) < 0.6f);

        // ---- wall triangles -------------------------------------------------------------
        var walls = new List<(Vector3 A, Vector3 B, Vector3 C, Vector2 N)>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f || Mathf.Abs(n.Y / len) >= BuildingTriangles.RoofNormalY) continue;
            var flat = new Vector2(n.X, n.Z);
            if (flat.LengthSquared() < 1e-10f) continue;
            walls.Add((a, c, d, flat.Normalized()));
        }

        // the roof laid flat: what is under the building and what is not (#577), so a wall on the
        // inside of an L or round a courtyard faces out of the building, not away from its box
        var roof = new List<(Vector2 A, Vector2 B, Vector2 C)>();
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f || Mathf.Abs(n.Y / len) < BuildingTriangles.RoofNormalY) continue;
            roof.Add((new Vector2(a.X, a.Z), new Vector2(c.X, c.Z), new Vector2(d.X, d.Z)));
        }
        bool Under(Vector2 p) => roof.Any(r => InTriangle(p, r.A, r.B, r.C));

        // ---- facade facets: coplanar wall triangles, merged along their wall line --------
        var facets = new Dictionary<(int, int), Facet>();
        var cuts = new List<(Vector2 Mid, Vector2 Normal, float Ground)>();
        foreach (var (a, c, d, n0) in walls)
        {
            var mid = new Vector2((a.X + c.X + d.X) / 3f, (a.Z + c.Z + d.Z) / 3f);
            // TIN winding is not consistent; outward is away from the box centre, unless the roof
            // says otherwise: roofed a step out and open a step in is a wall facing into the
            // building, the inner corner of an L or a courtyard's wall (#577). A step clears the eaves.
            var n = n0.Dot(mid - center) < 0 ? -n0 : n0;
            if (roof.Count > 0 && Under(mid + n * 1.2f) && !Under(mid - n * 1.2f)) n = -n;
            int angle = Mathf.RoundToInt(Mathf.RadToDeg(Mathf.Atan2(n.Y, n.X)) / 6f);
            // tight: coplanar TIN triangles agree to the millimetre, while a recess or a bay only
            // 0.3 m back is a different wall - a coarse bin merged the two and put the door in
            // the air between them
            int offset = Mathf.RoundToInt(n.Dot(mid) / 0.08f);
            var k = (angle, offset);
            if (!facets.TryGetValue(k, out var f)) facets[k] = f = new Facet(n, n.Dot(mid));
            // The run is the wall's cross-section a metre above the ground in front of it, not the
            // triangle's full width: a gable, or an upper storey over a porch roof, is wall where
            // no door can go, and taking its whole extent put doors in mid-air under it.
            float groundHere = grid != null
                ? (float)grid.SampleMeshHeight(tile.Id.MinE + mid.X, tile.Id.MaxN - mid.Y)
                : b.MinY + 0.8f;
            if (!CutAt(a, c, d, Math.Max(groundHere, b.MinY) + 1.0f, out var p0, out var p1)) continue;
            cuts.Add(((p0 + p1) * 0.5f, n, Math.Max(groundHere, b.MinY)));
            var t = new Vector2(-n.Y, n.X);
            float u0 = t.Dot(p0), u1 = t.Dot(p1);
            f.Spans.Add((Math.Min(u0, u1), Math.Max(u0, u1)));
        }

        float doorW = DoorWidthFor(kind);
        float doorH = DoorHeightFor(kind, InteriorGenerator.Storeys(b).Height - InteriorGenerator.Slab);
        // a landmark store's entrance is a wall of glass, not a front door: a 1.8 m Commercial door
        // on 190 m of blue sheet is the detail that makes it read as a warehouse again (#501)
        if (group?.Type == BuildingType.Ikea)
        {
            doorW = Math.Min(StoreDoorWidth, w * 0.25f);
            doorH = Math.Min(StoreDoorHeight, InteriorGenerator.Storeys(b).Height - InteriorGenerator.Slab - 0.15f);
        }
        // a door's worth of height, for judging a wall and for the odd doors that are no barn gate
        float plainH = Math.Min(doorH, DoorHeightFor(kind));
        bool barn = DoorLeaf.SwingsOut(kind);
        var roadTarget = roads.Streets.Nearest(center, 60f) ?? roads.Paths.Nearest(center, 40f);

        var ranked = new List<Cand>();
        foreach (var f in facets.Values)
        {
            var (s0, s1) = f.LongestRun();
            float length = s1 - s0;
            if (length < 0.9f) continue;
            // a barn's pair spans nearly the whole wall, its leaves standing out square when open
            float width = barn ? Math.Min(length - 2 * BarnDoorMargin, MaxBarnDoorWidth) : Math.Min(doorW, length - 0.3f);
            var t = new Vector2(-f.Normal.Y, f.Normal.X);
            var xz = f.Normal * f.Offset + t * ((s0 + s1) * 0.5f);
            if (Covered(xz)) continue;

            float ground = grid != null
                ? (float)grid.SampleMeshHeight(tile.Id.MinE + xz.X, tile.Id.MaxN - xz.Y)
                : b.MinY + 0.8f;
            float baseY = Math.Max(ground, b.MinY);
            // every door stops under the eave, not just a barn's (#509): a 2.6 m shed used to take
            // the 2.8 m door of a works and push it through its own roof
            float headroom = box.Eave - baseY;
            float height = FitUnderEave(doorH, headroom, barn);

            float score = Math.Min(length, 12f) * 0.08f;
            if (roadTarget is { } r)
            {
                var to = r - xz;
                float dist = to.Length();
                if (dist > 1e-3f) score += f.Normal.Dot(to / dist) * 1.5f;
                score -= dist * 0.01f;
            }
            else score += f.Normal.Dot(new Vector2(0.3f, 0.95f)) * 0.3f; // south-ish, like Swiss entrances
            if (ground < b.MinY - 0.6f) score -= 2f;          // on stilts over a slope
            if (ground > b.MaxY - plainH - 0.3f) score -= 3f; // buried side of a hillside house
            if (width < doorW * 0.8f) score -= 1f;
            // too little wall here for the door this building wants: it can still take a shorter
            // one, but the uphill end of a hillside house loses to the end with wall to spare
            if (headroom - DoorUnderEave < doorH - 0.01f) score -= LowWallPenalty;

            var pos = new Vector3(xz.X + f.Normal.X * 0.03f, baseY, xz.Y + f.Normal.Y * 0.03f);
            ranked.Add(new Cand(score, f.Normal, f.Offset, s0, s1,
                new DoorSpot(index, pos, new Vector3(f.Normal.X, 0, f.Normal.Y), Math.Max(0.7f, width), height)
                {
                    Hang = DoorBudget.HangFor(kind), Vehicle = DoorBudget.VehicleFor(kind),
                }));
        }

        // best-scoring door that is actually on a wall; the score alone can pick a run whose
        // middle falls in a gap the cross-section never saw (a pillar between two bays)
        DoorSpot door = default;
        Cand? main = null;
        bool found = false;
        foreach (var candidate in ranked.OrderByDescending(r => r.Score))
            if (DoorOnWall(b, candidate.Door)) { door = candidate.Door; main = candidate; found = true; break; }
        if (!found && cuts.Count > 0)
        {
            // a round tank or a many-sided silo: no flat run is door-wide, so stand the door on
            // whichever real piece of wall faces the street best
            var aim = roadTarget ?? center + new Vector2(0.3f, 0.95f) * 50f;
            var open = cuts.Where(k => !Covered(k.Mid)).ToList();
            var cut = (open.Count > 0 ? open : cuts).MaxBy(k => k.Normal.Dot((aim - k.Mid).Normalized()) - k.Mid.DistanceTo(aim) * 0.01f);
            var pos = new Vector3(cut.Mid.X + cut.Normal.X * 0.03f, cut.Ground, cut.Mid.Y + cut.Normal.Y * 0.03f);
            door = new DoorSpot(index, pos, new Vector3(cut.Normal.X, 0, cut.Normal.Y), Math.Min(doorW, 1.0f),
                FitUnderEave(plainH, box.Eave - cut.Ground, barn: false))
            {
                Hang = DoorBudget.HangFor(kind), Vehicle = DoorBudget.VehicleFor(kind),
            };
            found = true;
        }
        if (!found && ranked.Count > 0) { main = ranked.MaxBy(r => r.Score); door = main!.Door; found = true; }
        // No wall anywhere a metre above the ground: a roof on posts, a canopy, a reservoir sunk
        // into the slope. Nothing to walk into, so no door (Width 0) rather than one in mid-air.
        if (!found)
        {
            var v = new Vector2(-u.Y, u.X);
            var xz = center - v * (dpt * 0.5f);
            door = new DoorSpot(index, new Vector3(xz.X, b.MinY, xz.Y), new Vector3(-v.X, 0, -v.Y), 0f, plainH);
        }

        // ---- the building's other doors (#498) -------------------------------------------
        // A 100 m block with one front door reads as a prop. Doors go first along the wall the
        // main door stands on, DoorBudget.Spacing apart, then on the next-best walls (a back or
        // side door), until the building's budget is spent. All of them are service doors: plain
        // pedestrian doors, so a barn's pair and a garage's roll-up door gain a man-sized one.
        var extras = new List<DoorSpot>();
        int budget = DoorBudget.Total(kind, w, dpt);

        // ---- loading bays (#528) ----------------------------------------------------------
        // An industrial site's front wall is mostly bays, so they are placed before the generic
        // street-front rule and claim the budget first: a works with one pedestrian door and no way
        // to get a trailer inside is the thing this fixes. They go on the wall the MAIN door is on,
        // which is the wall `SiteYards` lays the yard in front of (#516) — so the bays face their
        // own fleet rather than the back hedge.
        var site = BuildingTypes.SiteFor(key.ToString(), b.Kind, box.Width, box.Depth, b.MaxY - b.MinY);
        // A showroom's front is glass, not a shutter. The building is GKLAS 1242 and so
        // `BuildingKind.Garage`, whose main door defaults to a roll-up one — right for a workshop,
        // wrong for the one site type whose front wall is meant to be looked through. Per door, as
        // #498 made possible.
        if (site == BuildingType.Dealership)
            door = door with { Hang = DoorHang.Inward, Vehicle = false };

        // the wall itself, base to eave: a bay cannot be taller than the wall it is cut in
        float wallHeight = Math.Max(0f, box.Eave - b.MinY);
        if (found && door.Width > 0 && main != null && site != BuildingType.None
            && DoorBudget.Bays(site, main.S1 - main.S0, wallHeight) is { } bays)
        {
            float mid = (main.S0 + main.S1) * 0.5f;
            var t = new Vector2(-main.Normal.Y, main.Normal.X);
            foreach (float off in DoorBudget.BayOffsets(main.S1 - main.S0, door.Width / 2, bays))
            {
                if (extras.Count + 1 >= budget) break;
                var xz = main.Normal * main.Offset + t * (mid + off);
                if (Covered(xz)) continue;
                float ground = grid != null
                    ? (float)grid.SampleMeshHeight(tile.Id.MinE + xz.X, tile.Id.MaxN - xz.Y)
                    : b.MinY + 0.8f;
                float baseY = Math.Max(ground, b.MinY);
                if (ground < b.MinY - 0.6f || ground > b.MaxY - bays.Height - 0.3f) continue;
                var bay = new DoorSpot(index,
                    new Vector3(xz.X + main.Normal.X * 0.03f, baseY, xz.Y + main.Normal.Y * 0.03f),
                    new Vector3(main.Normal.X, 0, main.Normal.Y), bays.Width, bays.Height)
                {
                    Slot = extras.Count + 1, Hang = DoorHang.RollUp, Vehicle = true,
                };
                if (!DoorOnWall(b, bay)) continue;
                // the pier between two bays, not a pedestrian door's 3 m, or every bay after the
                // first is rejected; the main door still keeps its own elbow room
                if (extras.Any(q => TooClose(q, bay, bays.Pier))) continue;
                if (TooClose(door, bay, DoorBudget.BayToDoorGap)) continue;
                extras.Add(bay);
            }
        }

        // ---- an underground garage door (#558) -------------------------------------------
        // Only a block of flats with two front doors, a basement car park (GarageRule, the same
        // predicate the generator's basement answers to) and a good roll, and only where a road
        // can be reached from it: no road in front, no door. It claims its slot in the budget
        // before the pedestrian doors, on the front wall between two of its entrances.
        if (found && door.Width > 0 && main != null && extras.Count + 1 < budget && roads.Streets.Roads.Count > 0
            && roof.Count > 0 && PlannedAsBox(b, center, u, new Vector2(door.Outward.X, door.Outward.Z), w, dpt))
        {
            var (storeyH, above) = InteriorGenerator.Storeys(b);
            var type = GarageRule.BlockType(key.ToString(), w * dpt, kind, above, false);
            // the generator plans a bank as a bank, never as a block with a car park behind a ramp
            if (IsBank(key.ToString(), kind, Mathf.Clamp(w, MinSide, MaxSide), Mathf.Clamp(dpt, MinSide, MaxSide))) type = BuildingType.None;
            var frontOut = new Vector2(door.Outward.X, door.Outward.Z);
            // the plan's own frame: the box edge the main door is on is its front wall
            bool frontAlongV = Math.Abs(frontOut.Dot(u)) < 0.5f;
            float frontW = Mathf.Clamp(frontAlongV ? w : dpt, MinSide, MaxSide), frontD = Mathf.Clamp(frontAlongV ? dpt : w, MinSide, MaxSide);
            int frontDoors = Math.Min(DoorBudget.AlongRun(main.S1 - main.S0, DoorBudget.ServiceWidth, DoorBudget.MaxPerWall).Length, budget - 1);
            if (GarageRule.Wanted(key.ToString(), type, above, frontW, frontD, storeyH, frontDoors))
            {
                DoorSpot? Garage(Cand c, float off)
                {
                    float gw = GarageRule.Width;
                    float mid = (c.S0 + c.S1) * 0.5f + off;
                    if (mid - gw / 2 < c.S0 + DoorBudget.EndMargin || mid + gw / 2 > c.S1 - DoorBudget.EndMargin) return null;
                    var t = new Vector2(-c.Normal.Y, c.Normal.X);
                    var xz = c.Normal * c.Offset + t * mid;
                    if (Covered(xz)) return null;
                    // inside the plan box (a facade longer than its 120 m clamp has doors past it)
                    if (Math.Abs((xz - center).Dot(t)) > frontW / 2 - GarageRule.RampWidth / 2 - 0.5f) return null;
                    float ground = grid != null
                        ? (float)grid.SampleMeshHeight(tile.Id.MinE + xz.X, tile.Id.MaxN - xz.Y)
                        : b.MinY + 0.8f;
                    float baseY = Math.Max(ground, b.MinY);
                    float gh = Math.Min(GarageRule.Height, box.Eave - baseY - DoorUnderEave);
                    if (gh < 2.2f || ground < b.MinY - 0.6f || ground > b.MaxY - gh - 0.3f) return null;
                    var link = GarageLink.Choose(xz + c.Normal * 0.03f, c.Normal, roads.Streets.Roads);
                    if (!link.Any) return null;
                    if (link.Kind == LinkKind.Stub && grid != null)
                    {
                        // the stub is a straight line between two heights: where the ground between them
                        // stands higher it would lie buried, so the stub is humped over it
                        var outV = c.Normal;
                        var samples = new List<(float, float)>();
                        for (int i = 1; i < 16; i++)
                            foreach (float side in new[] { -2.2f, -1f, 0f, 1f, 2.2f })
                            {
                                float at = i / 16f, o = link.Length * at;
                                var p = xz + outV * o + t * side;
                                float gy = (float)grid.SampleMeshHeight(tile.Id.MinE + p.X, tile.Id.MaxN - p.Y);
                                samples.Add((at, gy - (baseY + (link.RoadY - baseY) * at) - 0.03f));
                            }
                        if (!GarageLink.Humpable(samples)) return null;
                        link = link with { Hump = GarageLink.HumpFor(samples) };
                    }
                    var spot = new DoorSpot(index,
                        new Vector3(xz.X + c.Normal.X * 0.03f, baseY, xz.Y + c.Normal.Y * 0.03f),
                        new Vector3(c.Normal.X, 0, c.Normal.Y), gw, gh)
                    {
                        Slot = extras.Count + 1, Hang = DoorHang.RollUp, Vehicle = true, Link = link,
                    };
                    // clear of every other door by a stairwell and the lane (GarageRule.StairClear): the ramp
                    // runs between two stairwells, and a pedestrian door within reach would put one in it
                    bool clear = new Vector2(door.Position.X, door.Position.Z).DistanceTo(xz) >= GarageRule.StairClear
                        && extras.All(q => new Vector2(q.Position.X, q.Position.Z).DistanceTo(xz) >= GarageRule.StairClear);
                    return DoorOnWall(b, spot) && clear && !TooClose(door, spot) && !extras.Any(q => TooClose(q, spot)) ? spot : null;
                }
                DoorSpot? garage = null;
                // On the front wall, halfway between two entrances (which stand Spacing apart): the ramp
                // behind it runs straight in from the door between two stairwells (PR 2). A door on any
                // other wall had no ramp to lead to.
                var entrances = DoorBudget.AlongRun(main.S1 - main.S0, DoorBudget.ServiceWidth, budget - 1);
                bool Entrance(float at) => entrances.Any(e => Math.Abs(e - at) < 0.5f);
                for (int k = 1; garage == null && k <= DoorBudget.MaxPerWall; k++)
                    foreach (float off in new[] { DoorBudget.Spacing * (k - 0.5f), -DoorBudget.Spacing * (k - 0.5f) })
                        // with an entrance, and so a stairwell, on both sides of it: the lane then
                        // stands in the gap between two of them
                        if (Entrance(off - DoorBudget.Spacing / 2) && Entrance(off + DoorBudget.Spacing / 2)
                            && (garage = Garage(main, off)) != null) break;
                if (garage is { } g) extras.Add(g);
            }
        }

        if (found && door.Width > 0 && budget > 1 + extras.Count)
        {
            var (serviceW, serviceH) = ServiceDoorFor(kind);
            var placed = new List<DoorSpot> { door };
            placed.AddRange(extras);   // the bays are already on the wall and keep their room
            var order = new List<Cand>();
            if (main != null) order.Add(main);
            order.AddRange(ranked.OrderByDescending(r => r.Score).Where(c => c != main));
            foreach (var c in order)
            {
                if (placed.Count >= budget) break;
                float mid = (c.S0 + c.S1) * 0.5f;
                var t = new Vector2(-c.Normal.Y, c.Normal.X);
                foreach (float off in DoorBudget.AlongRun(c.S1 - c.S0, serviceW, DoorBudget.MaxPerWall))
                {
                    if (placed.Count >= budget) break;
                    if (off == 0 && c == main) continue; // the main door already stands there
                    var xz = c.Normal * c.Offset + t * (mid + off);
                    if (Covered(xz)) continue;
                    float ground = grid != null
                        ? (float)grid.SampleMeshHeight(tile.Id.MinE + xz.X, tile.Id.MaxN - xz.Y)
                        : b.MinY + 0.8f;
                    float baseY = Math.Max(ground, b.MinY);
                    // not on stilts over a slope, not on the buried side of a hillside house, and
                    // not on a solid too low to take a door at all. An extra door is a fixed
                    // pedestrian size, so a wall with no room for one simply does not get one
                    // (#509) — unlike the main door, which is cut down to fit.
                    if (ground < b.MinY - 0.6f || ground > b.MaxY - serviceH - 0.3f) continue;
                    if (box.Eave - baseY - DoorUnderEave < serviceH) continue;
                    var spot = new DoorSpot(index,
                        new Vector3(xz.X + c.Normal.X * 0.03f, baseY, xz.Y + c.Normal.Y * 0.03f),
                        new Vector3(c.Normal.X, 0, c.Normal.Y), serviceW, serviceH)
                    {
                        // a plain pedestrian door, whatever the building is: a barn's side door
                        // swings into the hall and no tractor comes through it
                        Slot = placed.Count, Hang = DoorHang.Inward, Vehicle = false,
                    };
                    if (!DoorOnWall(b, spot)) continue;
                    if (placed.Any(q => TooClose(q, spot))) continue;
                    // a pedestrian door (and its stairwell) keeps clear of a garage door's ramp (#558)
                    if (placed.Any(q => q.Link.Any && new Vector2(q.Position.X, q.Position.Z).DistanceTo(xz) < GarageRule.StairClear)) continue;
                    placed.Add(spot);
                    extras.Add(spot);
                }
            }
        }

        // ---- interior frame: the box edge the door is on becomes local -Z ----------------
        var outward = new Vector2(door.Outward.X, door.Outward.Z);
        var candidates = new[] { u, -u, new Vector2(-u.Y, u.X), new Vector2(u.Y, -u.X) };
        var edge = candidates.OrderByDescending(c => c.Dot(outward)).First();
        var back = -edge;
        var axisU = new Vector2(back.Y, -back.X); // so that AxisV == back
        bool alongU = Mathf.Abs(axisU.Dot(u)) > 0.5f;
        float width2 = alongU ? w : dpt, depth2 = alongU ? dpt : w;

        return new Footprint(key, kind, center, axisU,
            Mathf.Clamp(width2, MinSide, MaxSide), Mathf.Clamp(depth2, MinSide, MaxSide), door)
        {
            Extra = extras,
        };
    }

    /// <summary>One wall run a door could stand on: its plane, the run along it, and the door it would be.</summary>
    private sealed record Cand(float Score, Vector2 Normal, float Offset, float S0, float S1, DoorSpot Door);

    private static float Cross2(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool InTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 o, Vector2 u, Vector2 v) => (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);
        float d1 = Cross(a, b, p), d2 = Cross(b, c, p), d3 = Cross(c, a, p);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    /// <summary>
    /// Whether two doors of one building are too near each other to both be real, edge to edge:
    /// on the same wall, or round a corner, where two walls' runs both reach the same corner.
    /// </summary>
    private static bool TooClose(DoorSpot a, DoorSpot b, float gap = DoorBudget.MinGap)
    {
        var d = new Vector2(a.Position.X - b.Position.X, a.Position.Z - b.Position.Z);
        return d.Length() < a.Width / 2 + b.Width / 2 + gap;
    }

    /// <summary>Where a triangle crosses the horizontal plane at <paramref name="y"/>, in plan (x, z).</summary>
    private static bool CutAt(Vector3 a, Vector3 b, Vector3 c, float y, out Vector2 p0, out Vector2 p1)
    {
        var hits = new List<Vector2>(3);
        void Edge(Vector3 u, Vector3 v)
        {
            if ((u.Y - y) * (v.Y - y) > 0 || Mathf.Abs(u.Y - v.Y) < 1e-6f) return;
            float s = (y - u.Y) / (v.Y - u.Y);
            hits.Add(new Vector2(u.X + (v.X - u.X) * s, u.Z + (v.Z - u.Z) * s));
        }
        Edge(a, b); Edge(b, c); Edge(c, a);
        p0 = p1 = default;
        if (hits.Count < 2) return false;
        p0 = hits[0];
        p1 = hits[1].DistanceSquaredTo(p0) > (hits.Count > 2 ? hits[2].DistanceSquaredTo(p0) : -1) ? hits[1] : hits[2];
        return p0.DistanceSquaredTo(p1) > 1e-6f;
    }

    /// <summary>
    /// Whether the generator plans this building as its one box and not wing by wing (<see cref="PlanOutline"/>
    /// on the same frame <c>Compute</c> ends with): a garage's ramp (#558) is planned only in a whole block.
    /// </summary>
    internal static bool PlannedAsBox(Building b, Vector2 center, Vector2 u, Vector2 outward, float w, float dpt)
    {
        var candidates = new[] { u, -u, new Vector2(-u.Y, u.X), new Vector2(u.Y, -u.X) };
        var edge = candidates.OrderByDescending(c => c.Dot(outward)).First();
        var back = -edge;
        var axisU = new Vector2(back.Y, -back.X);
        bool alongU = Mathf.Abs(axisU.Dot(u)) > 0.5f;
        float width = alongU ? w : dpt, depth = alongU ? dpt : w;
        return PlanOutline.Wings(b, center, axisU, Mathf.Clamp(width, MinSide, MaxSide), Mathf.Clamp(depth, MinSide, MaxSide)) is not { Count: >= 1 };
    }

    /// <summary>
    /// Whether the door's centre, a metre up, lies on one of the building's own triangles, within
    /// 12 cm of it. A door that only scores well can still hang in the air beside the wall.
    /// </summary>
    public static bool DoorOnWall(Building b, DoorSpot door)
    {
        var p = door.Position + Vector3.Up * Math.Min(1.0f, door.Height * 0.5f) - door.Outward * 0.03f;
        for (int t = 0; t < b.TriangleCount; t++)
        {
            var (a, c, d) = b.Tri(t);
            var n = (c - a).Cross(d - a);
            float len = n.Length();
            if (len < 1e-6f) continue;
            n /= len;
            float dist = (p - a).Dot(n);
            if (Mathf.Abs(dist) > 0.12f) continue;
            var q = p - n * dist;
            // barycentric, with a little slack so a point on a shared edge counts
            var v0 = c - a; var v1 = d - a; var v2 = q - a;
            float d00 = v0.Dot(v0), d01 = v0.Dot(v1), d11 = v1.Dot(v1), d20 = v2.Dot(v0), d21 = v2.Dot(v1);
            float den = d00 * d11 - d01 * d01;
            if (Mathf.Abs(den) < 1e-9f) continue;
            float v = (d11 * d20 - d01 * d21) / den, w = (d00 * d21 - d01 * d20) / den;
            if (v >= -0.02f && w >= -0.02f && v + w <= 1.02f) return true;
        }
        return false;
    }

    private sealed class Facet
    {
        public readonly Vector2 Normal;
        public readonly float Offset;
        public readonly List<(float, float)> Spans = new();
        public Facet(Vector2 normal, float offset) { Normal = normal; Offset = offset; }

        /// <summary>Longest continuous stretch of wall on this line (two wings of a U are two runs).</summary>
        public (float, float) LongestRun()
        {
            if (Spans.Count == 0) return (0, 0); // wall entirely above door height
            Spans.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            (float, float) best = (0, 0), cur = Spans[0];
            foreach (var s in Spans)
            {
                if (s.Item1 <= cur.Item2 + 0.05f) cur.Item2 = Math.Max(cur.Item2, s.Item2);
                else
                {
                    if (cur.Item2 - cur.Item1 > best.Item2 - best.Item1) best = cur;
                    cur = s;
                }
            }
            return cur.Item2 - cur.Item1 > best.Item2 - best.Item1 ? cur : best;
        }
    }

    /// <summary>Road vertices hashed into 20 m cells, so a tile's thousand buildings each find their street quickly.</summary>
    private sealed class RoadPoints
    {
        private const float Cell = 20f;
        private readonly Dictionary<(int, int), List<Vector2>> _cells = new();

        /// <summary>The streets' and motorways' centrelines, for a garage door's road link (#558); empty in the paths index.</summary>
        public readonly List<GarageLink.Road> Roads = new();

        /// <summary>
        /// Streets only by default: a front door faces the street, and a footpath or farm track
        /// running past the back garden is often nearer than the road in front. With
        /// <paramref name="paths"/> the index holds paths and tracks instead, the fallback for a
        /// building no street reaches.
        /// </summary>
        public static RoadPoints Build(RoadTile? roads, bool paths = false)
        {
            var r = new RoadPoints();
            if (roads == null) return r;
            foreach (var s in roads.Segments)
            {
                if (!paths && s.Class < RoadClass.Railway && s.PointCount >= 2) r.Roads.Add(new GarageLink.Road(s.Class, s.Width, s.Points));
                // somewhere a front door faces: not motorways, rail or rivers
                if (s.Class is RoadClass.Motorway or RoadClass.Expressway or RoadClass.Ramp
                    || s.Class >= RoadClass.Railway) continue;
                bool isPath = s.Class is RoadClass.Track or RoadClass.Path or RoadClass.Link;
                if (isPath != paths) continue;
                for (int i = 0; i < s.PointCount; i++)
                {
                    var p = new Vector2(s.Points[i * 3], s.Points[i * 3 + 2]);
                    var k = ((int)MathF.Floor(p.X / Cell), (int)MathF.Floor(p.Y / Cell));
                    if (!r._cells.TryGetValue(k, out var list)) r._cells[k] = list = new();
                    list.Add(p);
                }
            }
            return r;
        }

        public Vector2? Nearest(Vector2 at, float reach)
        {
            if (_cells.Count == 0) return null;
            int span = (int)MathF.Ceiling(reach / Cell);
            int cx = (int)MathF.Floor(at.X / Cell), cz = (int)MathF.Floor(at.Y / Cell);
            Vector2? best = null;
            float bestD = reach * reach;
            for (int x = cx - span; x <= cx + span; x++)
                for (int z = cz - span; z <= cz + span; z++)
                    if (_cells.TryGetValue((x, z), out var list))
                        foreach (var p in list)
                        {
                            float d = p.DistanceSquaredTo(at);
                            if (d < bestD) { bestD = d; best = p; }
                        }
            return best;
        }
    }
}
