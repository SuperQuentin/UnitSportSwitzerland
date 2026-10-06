using Godot;
using UnitSport.Core;
using UnitSport.Terrain;
using UnitSport.Terrain.Format;

namespace UnitSport.Interiors;

/// <summary>
/// <c>godot --path . -- --sitecheck[,out.png]</c> (#497): the five industrial sites planned,
/// validated, furnished and built, from hand-made buildings — no map, no server, because neither the
/// generated world nor the fixture world has an industrial building in it, and real tiles are a
/// 100 GB download.
///
/// <para>
/// For each of a warehouse, a works, a haulier's depot, a body shop and a dealership it asserts that
/// <see cref="BuildingTypes.SiteFor"/> actually picks that type for the building given, that the plan
/// passes <see cref="InteriorValidator"/>, that the hall got the layout its type calls for (racking,
/// a line, bays, plinths) rather than the generic scatter, that the service block was cut and every
/// one of its rooms is reachable, and that <see cref="InteriorMeshBuilder"/> builds the whole thing —
/// every new piece — without throwing and with collision on it. It writes an SVG plan per site to
/// <c>test_output/sites/</c>; with a shot, a picture from inside each hall beside it.
/// </para>
/// </summary>
public partial class SiteProbe : Node3D
{
    private readonly string? _shot;
    private bool _ok = true;
    private int _step = -1;
    private double _t;
    private readonly List<(BuildingType Site, InteriorLayout Layout, InteriorNode Node)> _built = new();
    /// <summary>How many facades the bay pass has stood in its row (#528).</summary>
    private int _facades;
    /// <summary>Where the apron shot looks (#583 phase 3), and the way out of the warehouse's front.</summary>
    private (Vector3 At, Vector3 Out)? _apron;
    /// <summary>Each site's tile and the building's index in it, for the yard pass (#516).</summary>
    private readonly List<(BuildingType Site, BuildingTile Tile, int Index)> _sites = new();

    public SiteProbe(string? shot) => _shot = shot;

    public static (bool Requested, string? Shot) ParseArgs() => CmdArgs.FlagWithShot("--sitecheck");

    private void Check(bool condition, string what)
    {
        GD.Print($"[site] {(condition ? "ok  " : "FAIL")} {what}");
        _ok &= condition;
    }

    /// <summary>Like <see cref="Check"/>, but silent when it holds: for a per-bay or per-slot rule.</summary>
    private void Assert(bool condition, string what)
    {
        if (condition) return;
        GD.Print($"[site] FAIL {what}");
        _ok = false;
    }

    /// <summary>
    /// One building to plan: the size and wall height that make <see cref="BuildingTypes.SiteFor"/>
    /// choose <see cref="Want"/>, and the kind it is in the cadastre.
    /// </summary>
    /// <param name="Turn">
    /// Degrees the solid is turned in plan. A box square to the world cannot catch a mirrored
    /// direction convention — the mirror and the truth agree on it — and every real Swiss building
    /// is at some angle, so one site is turned (#524's lesson, applied to the bays and the yard).
    /// </param>
    private readonly record struct Spec(BuildingType Want, BuildingKind Kind, float Width, float Depth,
        float Wall, float Turn = 0f);

    private static readonly Spec[] Specs =
    {
        // turned to no axis: its bays, its doorways and its yard all have to survive a real angle
        new(BuildingType.Warehouse, BuildingKind.Industrial, 58f, 34f, 8f, Turn: 31f),
        new(BuildingType.Factory, BuildingKind.Industrial, 46f, 28f, 12f),
        new(BuildingType.Depot, BuildingKind.Industrial, 26f, 18f, 7f),
        new(BuildingType.Mechanic, BuildingKind.Industrial, 16f, 12f, 6f),
        new(BuildingType.Dealership, BuildingKind.Garage, 26f, 16f, 6f),
    };

    public override void _Ready()
    {
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.06f, 0.06f, 0.08f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.85f, 0.85f, 0.85f),
            },
        });

        string svgDir = ProjectSettings.GlobalizePath("res://test_output/sites");
        DirAccess.MakeDirRecursiveAbsolute(svgDir);
        var material = Styles.StyleKit.Material(Styles.MaterialRole.Interior);

        foreach (var spec in Specs)
        {
            // the building's own key has to be one the hash sends to this type: the data invents the
            // industry, so the probe has to look for a building that is the one it wants to look at
            var (tile, index, key) = Find(spec);
            if (key == null)
            {
                Check(false, $"{spec.Want}: no {spec.Width:F0}x{spec.Depth:F0} m {spec.Kind} is one in 400 keys");
                continue;
            }

            var layout = InteriorGenerator.Generate(tile!, index, null, null);
            if (layout == null) { Check(false, $"{spec.Want} {key}: planned"); continue; }
            File.WriteAllText(Path.Combine(svgDir, $"{spec.Want}_{key}.svg"), InteriorValidator.ToSvg(layout));

            Check(layout.Type == spec.Want, $"{key} is planned as a {spec.Want} (got {layout.Type})");
            var problems = InteriorValidator.Validate(layout);
            Check(problems.Count == 0, $"{spec.Want} plan is valid" +
                (problems.Count > 0 ? ": " + string.Join("; ", problems.Take(3)) : ""));

            // the loading bays (#528): a works' front wall is mostly doors a trailer goes through
            var fp = BuildingFootprint.Compute(tile!, index, null, null);
            var bays = fp?.Doors.Where(d => d.Vehicle && d.Hang == DoorHang.RollUp).ToList() ?? new();
            bool wantsBays = spec.Want != BuildingType.Dealership;
            Check(wantsBays == bays.Count > 0, $"{spec.Want} {(wantsBays ? "has" : "has no")} loading bays ({bays.Count})");
            if (bays.Count > 1)
            {
                var along = new Vector2(-bays[0].Outward.Z, bays[0].Outward.X);
                Assert(bays.All(d => d.Outward.IsEqualApprox(bays[0].Outward)), $"{spec.Want}: bays share a wall");
                Assert(bays.All(d => Mathf.IsEqualApprox(d.Width, bays[0].Width)), $"{spec.Want}: bays are the same width");
                // Along the wall, each neighbouring pair is either a pier apart — the strip of
                // wall that carries the two lintels — or has the main door standing between them,
                // because the bays fill both sides of it.
                float Along(Vector3 at) => along.Dot(new Vector2(at.X, at.Z));
                var sorted = bays.OrderBy(d => Along(d.Position)).ToList();
                float mainAt = Along(fp!.Door.Position);
                for (int k = 1; k < sorted.Count; k++)
                {
                    float a = Along(sorted[k - 1].Position), c = Along(sorted[k].Position);
                    float gap = c - a - sorted[k].Width;
                    bool acrossTheDoor = mainAt > a && mainAt < c;
                    Assert(gap > 0.2f && (gap < 2.5f || acrossTheDoor),
                        $"{spec.Want}: a {gap:F2} m gap between two bays with no door in it");
                }
            }
            float wall = tile!.Buildings[index].MaxY - tile.Buildings[index].MinY;
            foreach (var d in bays)
                Assert(d.Height <= wall, $"{spec.Want}: a {d.Height:F1} m bay in a {wall:F1} m wall");

            // the facade with its doors on it, in a row for the picture: the baked roll-up slats
            // are what a bay looks like from the yard, and nothing but a look can check them
            if (fp != null && BuildingMeshBuilder.Build(tile, fp.Doors.Select(d => d with { Index = index }).ToArray()) is { } facade)
                AddChild(new MeshInstance3D
                {
                    Name = $"Facade{_facades}", Position = new Vector3(_facades++ * 150f, 0, 900f),
                    Mesh = Terrain.ChunkNode.ToArrayMesh(facade, Styles.StyleKit.Material(Styles.MaterialRole.Building)),
                });

            var ground = layout.GroundFloor;
            var hall = ground.Rooms[0];
            Check(ground.Rooms.Count > 1, $"{spec.Want} has a service block ({ground.Rooms.Count - 1} room(s): "
                + string.Join(", ", ground.Rooms.Skip(1).Select(r => r.Type)) + ")");
            // the block is low, inside the tall hall, not a storey of its own
            foreach (var r in ground.Rooms.Skip(1))
                Check(layout.ClearOf(r) < layout.ClearOf(hall) - 0.5f,
                    $"{spec.Want} {r.Type} is a low room ({layout.ClearOf(r):F1} m) in a {layout.ClearOf(hall):F1} m hall");

            // the hall's own layout, not the generic scatter
            var want = spec.Want switch
            {
                BuildingType.Warehouse => FurnitureType.PalletRack,
                BuildingType.Factory => FurnitureType.Conveyor,
                BuildingType.Depot or BuildingType.Mechanic => FurnitureType.CarLift,
                _ => FurnitureType.ShowroomPlinth,
            };
            int n = layout.Furniture.Count(f => f.Type == want);
            Check(n > 0, $"{spec.Want} hall has {want} ({n})");
            Check(layout.Furniture.Any(f => f.Type == FurnitureType.FloorMarking)
                  || spec.Want == BuildingType.Dealership, $"{spec.Want} floor is marked out");
            // a workshop has one up on a ramp, a showroom a car on every plinth
            if (spec.Want is BuildingType.Depot or BuildingType.Mechanic)
            {
                var onRamp = spec.Want == BuildingType.Depot ? FurnitureType.TruckProp : FurnitureType.Car;
                var up = layout.Furniture.FirstOrDefault(f => f.Type == onRamp);
                Check(up is { Lift: > 0.2f }, $"{spec.Want} has a {onRamp} up on a lift"
                    + (up == null ? "" : $" (lift {up.Lift:F2} m)"));
            }
            if (spec.Want == BuildingType.Dealership)
                Check(layout.Furniture.Count(f => f.Type == FurnitureType.Car)
                      == layout.Furniture.Count(f => f.Type == FurnitureType.ShowroomPlinth),
                    "every showroom plinth has a car on it");
            GD.Print($"[site] {spec.Want} {key}: {layout.Width:F0}x{layout.Depth:F0} m, "
                + $"{layout.StoreyHeight:F1} m storey, {layout.Furniture.Count} pieces — "
                + string.Join(", ", layout.Furniture.GroupBy(f => f.Type).OrderByDescending(g => g.Count())
                    .Take(6).Select(g => $"{g.Key} {g.Count()}")));

            // nothing may stand in the lane in from the door a vehicle drives through
            if (BuildingTypes.DrivenInto(spec.Want))
            {
                var entry = hall.Openings.First(o => o.Kind == OpeningKind.Entry);
                float half = entry.Width / 2;
                bool clear = !layout.Furniture.Any(f => f.Floor == layout.Below
                    && f.Type != FurnitureType.FloorMarking
                    && Math.Abs(f.X - entry.Center) < half + Math.Max(f.W, f.D) / 2
                    && f.Z < hall.Z0 + 2.5f);
                Check(clear, $"{spec.Want}'s way in is clear of furniture");
            }

            // the mesh: every new piece built, and the hall solid enough to stand in
            InteriorMeshBuilder.MeshData data;
            try { data = InteriorMeshBuilder.Build(layout); }
            catch (Exception e) { Check(false, $"{spec.Want} mesh builds ({e.GetType().Name}: {e.Message})"); continue; }
            Check(data.Vertices.Length > 0, $"{spec.Want} mesh has {data.Vertices.Length} vertices");
            Check(data.Collision.Length > 0, $"{spec.Want} mesh has {data.Collision.Length / 3} collision triangles");

            var node = InteriorNode.Create(layout, data, material,
                new Transform3D(Basis.Identity, new Vector3(_built.Count * 400f, 0, 0)));
            AddChild(node);
            _built.Add((spec.Want, layout, node));
            _sites.Add((spec.Want, tile!, index));
        }

        Check(_built.Count == Specs.Length, $"all {Specs.Length} sites built ({_built.Count})");
        GD.Print($"[site] plans written to {svgDir}");
        Ordinary();
        Yards();

        if (_shot == null || DisplayServer.GetName() == "headless") { Finish(); return; }
        AddChild(new Camera3D { Name = "Eye", Fov = 75f });
    }

    /// <summary>
    /// The yard outside each site (#516, #496 phase 3): the fleet `DormantSlots.ForSite` parks on
    /// the open ground in front of the building. Asserts the trade's own mix, that nothing stands
    /// inside the building it belongs to, and — the part no unit test can reach — that every goods
    /// vehicle actually builds the <c>Rideable</c> and the mesh it will wake as. It then stands the
    /// sites and their yards in a row for the picture, which is the only way to see that an artic
    /// is an artic and not a hatchback with a lorry's name.
    /// </summary>
    private void Yards()
    {
        var material = Styles.StyleKit.Material(Styles.MaterialRole.Building);
        int drawnHeavies = 0, drawnForklifts = 0;
        for (int i = 0; i < _sites.Count; i++)
        {
            var (site, tile, index) = _sites[i];
            var yards = Vehicles.SiteYards.For(tile, null, null);
            var mine = yards.Where(y => y.Owner == new BuildingKey(tile.Id.E, tile.Id.N, index).ToString()).ToList();
            Check(mine.Count == 1, $"{site} has a yard ({mine.Count})");
            if (mine.Count != 1) continue;

            var slots = new List<Vehicles.VehicleSlot>();
            Vehicles.DormantSlots.ForSite(tile.Id, mine, ProbeCars, ProbeHeavies, ProbeTrailers,
                (int)Player.RideKind.Trailer, slots);
            Check(slots.Count > 0, $"{site} yard has a fleet ({slots.Count})");

            var box = BuildingTypes.For(tile).Boxes[index]!.Value;
            foreach (var s in slots)
            {
                var at = new Vector2((float)(s.E - tile.Id.MinE), (float)(tile.Id.MaxN - s.N));
                if (box.DistanceTo(at) >= 1f) continue;
                Check(false, $"{site}: a {s.KindId} stands inside its own building");
                break;
            }

            // A vehicle's NOSE must be further from the building than its tail. This is the one
            // assertion that can see a mirrored heading, and the reason it is here: the yard frame
            // and the test that walks it back both use the same formula, so a mirror is invisible
            // to them — #499 shipped exactly that bug in `ToGodotHeading` (correct square to north,
            // wrong by twice the axis otherwise) and both of its angle checks passed, because they
            // measured |angle| off a world axis and +14 and -14 look alike. A physical fact does
            // not care which convention is right.
            foreach (var s in slots.Take(6))
            {
                var at = new Vector2((float)(s.E - tile.Id.MinE), (float)(tile.Id.MaxN - s.N));
                var facing = new Vector2(-Mathf.Sin(s.Yaw), -Mathf.Cos(s.Yaw));   // RoadSignBuilder.Frame
                float nose = box.DistanceTo(at + facing * 2.5f), tail = box.DistanceTo(at - facing * 2.5f);
                // a working yard faces either way, so only the forecourt's tidy rows can be judged
                if (site != BuildingType.Dealership || nose > tail) continue;
                Check(false, $"{site}: a slot faces its own building (nose {nose:F1} m, tail {tail:F1} m)");
                break;
            }

            int heavy = slots.Count(s => s.Train != 0 || Player.HeavyCatalog.For((Player.RideKind)s.KindId) != null);
            bool wantsHeavy = site is BuildingType.Depot or BuildingType.Warehouse;
            Check(wantsHeavy == heavy > 0,
                $"{site} yard {(wantsHeavy ? "has" : "has no")} goods vehicles ({heavy} of {slots.Count})");

            // The apron (#583 phase 3): pallets by trade along the facade and clear of every door,
            // measured against the footprint's doors themselves rather than the spans the row was
            // laid out from; and beside a warehouse's or a works' front a forklift, drawn below
            // through the dormant layer's look like the rest of the fleet.
            var front = Vehicles.SiteYards.Fronts(tile, null, null).First(f => f.Yard.Owner == mine[0].Owner);
            var pallets = Vehicles.SiteYards.Pallets(tile, null, null, new() { front });
            bool wantsPallets = Items.SitePallets.FillFor((int)site) > 0f;
            Check(wantsPallets == pallets.Count > 0, $"{site} apron {(wantsPallets ? "has" : "has no")} pallets ({pallets.Count})");
            var fp = BuildingFootprint.Compute(tile, index, null, null)!;
            foreach (var p in pallets)
            {
                var at = new Vector2((float)(p.E - tile.Id.MinE), (float)(tile.Id.MaxN - p.N));
                if (box.DistanceTo(at) < Items.SitePallets.Out - 0.7f) { Check(false, $"{site}: pallet {p.Id} stands in its building"); break; }
                if (fp.Doors.FirstOrDefault(d => new Vector2(d.Position.X, d.Position.Z).DistanceTo(at) < d.Width / 2 + 0.9f) is { Width: > 0 } door)
                {
                    Check(false, $"{site}: pallet {p.Id} stands in front of a {door.Width:F1} m door");
                    break;
                }
            }
            var lift = Vehicles.DormantSlots.ForkliftOf(tile.Id, front, (int)Player.RideKind.Forklift);
            bool wantsLift = site is BuildingType.Warehouse or BuildingType.Factory;
            Check(wantsLift == (lift != null), $"{site} {(wantsLift ? "keeps a" : "keeps no")} forklift on its apron");
            if (lift is { } forklift)
            {
                var at = new Vector2((float)(forklift.E - tile.Id.MinE), (float)(tile.Id.MaxN - forklift.N));
                float clear = pallets.Count == 0 ? float.MaxValue
                    : pallets.Min(p => at.DistanceTo(new Vector2((float)(p.E - tile.Id.MinE), (float)(tile.Id.MaxN - p.N))));
                Check(box.DistanceTo(at) >= 1.5f && clear >= 2f,
                    $"{site}'s forklift stands clear of the building ({box.DistanceTo(at):F1} m) and of its pallets ({clear:F1} m)");
                Check(fp.Doors.All(d => new Vector2(d.Position.X, d.Position.Z).DistanceTo(at) >= d.Width / 2 + 1.5f),
                    $"{site}'s forklift is in front of no door");
                slots.Add(forklift);
            }

            // the exterior and its yard, in a row well away from the interiors
            var origin = new Vector3(i * YardRowPitch, 0, 1200f);
            if (site == BuildingType.Warehouse && lift is { } shown)
            {
                // for the apron shot: between the forklift and its nearest pallet, seen from the yard
                Vector3 Local(double e, double n) => origin + new Vector3((float)(e - tile.Id.MinE), 0, (float)(tile.Id.MaxN - n));
                var at = Local(shown.E, shown.N);
                var near = pallets.Count == 0 ? at : pallets.Select(p => Local(p.E, p.N)).MinBy(p => p.DistanceTo(at));
                float h = front.Yard.Heading;
                _apron = ((at + near) / 2, new Vector3(-Mathf.Sin(h), 0, -Mathf.Cos(h)));
            }
            if (BuildingMeshBuilder.Build(tile) is { } mesh)
                AddChild(new MeshInstance3D
                {
                    Name = $"Hall{i}", Position = origin,
                    Mesh = Terrain.ChunkNode.ToArrayMesh(mesh, material),
                });
            foreach (var s in slots)
            {
                var state = new Vehicles.VehicleState((Player.RideKind)s.KindId,
                    new Core.GlobalPos(s.E, s.N, s.Height), s.Yaw, Vector3.Zero, 0f,
                    EngineOn: false, Wrecked: false, Throttle: 0f, SpawnedAt: 0,
                    Train: s.Train, Load: s.Load);
                if (state.CreateRide() is not { } ride) { Check(false, $"{site}: slot {s.Ordinal} builds no ride"); continue; }
                var node = new Node3D
                {
                    Position = origin + new Vector3((float)(s.E - tile.Id.MinE), 0, (float)(tile.Id.MaxN - s.N)),
                    Basis = new Basis(Vector3.Up, s.Yaw),
                };
                // drawn through the dormant layer's own look (#552): the merged parked model, every
                // section posed, exactly what a yard shows until somebody touches the lorry
                var look = Vehicles.DormantLooks.For(Vehicles.DormantLooks.KeyOf(s), () => ride, drawn: true);
                if (look?.Mesh is not { } merged || look.Triangles == 0) { Check(false, $"{site}: slot {s.Ordinal} has no dormant look"); continue; }
                node.AddChild(new MeshInstance3D { Mesh = merged });
                AddChild(node);
                if (s.Train != 0 || Player.HeavyCatalog.For((Player.RideKind)s.KindId) != null) drawnHeavies++;
                if (s.KindId == (int)Player.RideKind.Forklift) drawnForklifts++;
            }
            foreach (var p in pallets)
            {
                var node = Items.PalletNode.Create(p.Id, p.Load, Avatar.HumanMeshBuilder.FigureMaterial());
                node.Transform = new Transform3D(new Basis(Vector3.Up, p.Yaw),
                    origin + new Vector3((float)(p.E - tile.Id.MinE), 0, (float)(tile.Id.MaxN - p.N)));
                AddChild(node);
            }
        }
        Check(drawnForklifts == 2, $"the warehouse's and the works' forklifts build their dormant look ({drawnForklifts})");
        Check(drawnHeavies > 0, $"goods vehicles build their dormant look from their real parked model ({drawnHeavies})");
    }

    /// <summary>How far apart the five sites stand in the yard row, metres.</summary>
    private const float YardRowPitch = 170f;

    /// <summary>The catalogues the yard pass fills from, as <c>DormantVehicles</c> does.</summary>
    private static readonly int[] ProbeCars = Player.CarCatalog.All
        .Where(c => c.Body.Shape is Avatar.BodyShape.Hatchback or Avatar.BodyShape.Sedan)
        .Select(c => (int)c.Kind).ToArray();

    private static readonly int[] ProbeHeavies = Player.HeavyCatalog.All
        .Where(h => h.Takes != Player.Coupling.None || h.Label.Contains("rigid"))
        .Select(h => (int)h.Kind).ToArray();

    private static readonly int[] ProbeTrailers = Enumerable.Range(0, Player.TrailerCatalog.All.Count)
        .SelectMany(i => new[] { Player.TrailerCatalog.Code(i, 0f), Player.TrailerCatalog.Code(i, 1f) })
        .ToArray();

    /// <summary>
    /// Ordinary buildings still plan and still validate. Sites are planned on a new branch of
    /// <c>InteriorGenerator.Generate</c>, and the validator has a new rule (an opening may not be
    /// taller than its room), so both need to be shown not to have disturbed the houses, barns,
    /// garages, shops and churches that were there first.
    /// </summary>
    private void Ordinary()
    {
        var kinds = new (BuildingKind Kind, float W, float D, float Wall)[]
        {
            (BuildingKind.House, 9f, 11f, 7.5f),
            (BuildingKind.House, 6f, 7f, 5.5f),
            (BuildingKind.Apartment, 17f, 13f, 15f),
            (BuildingKind.Commercial, 14f, 12f, 9f),
            (BuildingKind.Civic, 24f, 16f, 11f),
            (BuildingKind.Agricultural, 14f, 10f, 8f),
            (BuildingKind.Garage, 3.4f, 6.2f, 2.8f),
            (BuildingKind.Annex, 5f, 4f, 3f),
            (BuildingKind.Sacral, 11f, 24f, 12f),
            (BuildingKind.Other, 10f, 9f, 8f),
            (BuildingKind.Industrial, 7f, 6f, 4.5f),   // under SiteMinArea: still the plain Workshop
        };
        int planned = 0, bad = 0;
        foreach (var (kind, w, d, wall) in kinds)
            for (int i = 0; i < 24; i++)
            {
                var tile = new BuildingTile { Id = new TileId(0, 0), Buildings = new List<Building>() };
                for (int k = 0; k < i; k++) tile.Buildings.Add(Pad());
                tile.Buildings.Add(new Building
                {
                    Kind = kind, Floors = 0, MinY = 0, MaxY = wall, Triangles = Box(w, d, wall),
                });
                if (InteriorGenerator.Generate(tile, i, null, null) is not { } l) continue;
                planned++;
                if (BuildingTypes.IsSite(l.Type))
                {
                    Check(false, $"{kind} {w}x{d} m was planned as a {l.Type}");
                    continue;
                }
                var problems = InteriorValidator.Validate(l);
                if (problems.Count == 0) continue;
                if (bad++ < 4) GD.Print($"[site] invalid {kind} 0_0_{i}: {string.Join("; ", problems.Take(3))}");
            }
        Check(planned > 200, $"{planned} ordinary buildings planned");
        Check(bad == 0, $"every ordinary plan is still valid ({bad} of {planned} failed)");
    }

    /// <summary>
    /// A tile holding one building of <paramref name="spec"/>'s size whose key the site hash sends to
    /// the type wanted, and the index it is at. Returns the key text, or null if none of 400 is.
    /// </summary>
    /// <summary>
    /// One of the hand-made sites, building and all, for another check to plan and stand in: the
    /// pallet check (#583) forks pallets in the turned warehouse.
    /// </summary>
    internal static (BuildingTile? Tile, int Index, string? Key) FindSite(BuildingType want) => Find(Specs.First(s => s.Want == want));

    private static (BuildingTile? Tile, int Index, string? Key) Find(Spec spec)
    {
        for (int i = 0; i < 400; i++)
        {
            string key = $"0_0_{i}";
            if (BuildingTypes.SiteFor(key, spec.Kind, spec.Width, spec.Depth, spec.Wall) != spec.Want) continue;
            var tile = new BuildingTile { Id = new TileId(0, 0), Buildings = new List<Building>() };
            // index i, so the key the generator derives is the key the hash was asked about
            for (int k = 0; k < i; k++) tile.Buildings.Add(Pad());
            tile.Buildings.Add(new Building
            {
                Kind = spec.Kind, Floors = 0, MinY = 0, MaxY = spec.Wall,
                Triangles = Box(spec.Width, spec.Depth, spec.Wall, 0f, spec.Turn),
            });
            return (tile, i, key);
        }
        return (null, -1, null);
    }

    /// <summary>A 1 m cube well away from the one being looked at: a filler so the real building's index matches its key.</summary>
    private static Building Pad() => new()
    {
        Kind = BuildingKind.Annex, MinY = 0, MaxY = 1f, Triangles = Box(1f, 1f, 1f, 400f),
    };

    /// <summary>A flat-roofed box centred on (<paramref name="at"/>, 0), wound outward like a real solid.</summary>
    private static float[] Box(float w, float d, float h, float at = 0f, float turn = 0f)
    {
        float x0 = at - w / 2, x1 = at + w / 2, z0 = -d / 2, z1 = d / 2;
        var t = new List<float>();
        // turned about the box's own centre, so a turned solid keeps the place an upright one had
        float cos = Mathf.Cos(Mathf.DegToRad(turn)), sin = Mathf.Sin(Mathf.DegToRad(turn));
        Vector3 Turned(Vector3 v)
        {
            float dx = v.X - at, dz = v.Z;
            return new Vector3(at + dx * cos - dz * sin, v.Y, dx * sin + dz * cos);
        }
        void Tri(Vector3 ra, Vector3 rb, Vector3 rc)
        {
            var (a, b, c) = (Turned(ra), Turned(rb), Turned(rc));
            t.AddRange(new[] { a.X, a.Y, a.Z, c.X, c.Y, c.Z, b.X, b.Y, b.Z });
        }
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 e) { Tri(a, b, c); Tri(a, c, e); }
        Quad(new(x0, 0, z1), new(x1, 0, z1), new(x1, h, z1), new(x0, h, z1));  // south
        Quad(new(x1, 0, z0), new(x0, 0, z0), new(x0, h, z0), new(x1, h, z0));  // north
        Quad(new(x0, 0, z0), new(x0, 0, z1), new(x0, h, z1), new(x0, h, z0));  // west
        Quad(new(x1, 0, z1), new(x1, 0, z0), new(x1, h, z0), new(x1, h, z1));  // east
        Quad(new(x0, h, z1), new(x1, h, z1), new(x1, h, z0), new(x0, h, z0));  // the flat roof
        return t.ToArray();
    }

    /// <summary>Windowed: one picture per hall, taken from just inside its door looking down it.</summary>
    public override void _Process(double delta)
    {
        if (_shot == null) return;
        _t += delta;
        if (_t < 0.4) return;
        _t = 0;
        if (_step >= 0 && _step < _built.Count)
        {
            var image = GetViewport().GetTexture().GetImage();
            image.SavePng(_shot.Replace(".png", $"_{_built[_step].Site}".ToLowerInvariant() + ".png"));
        }
        _step++;
        // Two more frames after the walk through the interiors, because neither is a room: the
        // yard full of lorries (#516) and a works' front wall of bays (#528).
        if (_step == _built.Count)
        {
            var eye = GetNodeOrNull<Camera3D>("Eye");
            if (eye == null) { Finish(); return; }
            // the haulier's depot: the one yard whose point is that an artic is an artic
            int depot = Math.Max(0, _sites.FindIndex(x => x.Site == BuildingType.Depot));
            float x = depot * YardRowPitch;
            eye.GlobalPosition = new Vector3(x - 34f, 26f, 1252f);
            eye.LookAt(new Vector3(x, 1.5f, 1196f), Vector3.Up);
            eye.MakeCurrent();
            return;
        }
        if (_step == _built.Count + 1)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_yards.png"));
            var eye = GetNodeOrNull<Camera3D>("Eye");
            if (eye == null) { Finish(); return; }
            // the warehouse is the first facade in the row, and a synthetic tile with no roads
            // puts its door on the south wall (+Z), so the yard side is beyond it
            eye.GlobalPosition = new Vector3(-30f, 12f, 968f);
            eye.LookAt(new Vector3(4f, 3.5f, 917f), Vector3.Up);
            eye.MakeCurrent();
            return;
        }
        if (_step == _built.Count + 2)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_bays.png"));
            var eye = GetNodeOrNull<Camera3D>("Eye");
            if (eye == null || _apron is not { } apron) { Finish(); return; }
            // the warehouse's apron (#583 phase 3): its pallets along the dock and its forklift,
            // from out in the yard
            eye.GlobalPosition = apron.At + apron.Out * 14f + Vector3.Up * 6f;
            eye.LookAt(apron.At + Vector3.Up * 0.8f, Vector3.Up);
            eye.MakeCurrent();
            return;
        }
        if (_step > _built.Count + 2)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_apron.png"));
            Finish();
            return;
        }

        var (_, layout, node) = _built[_step];
        var hall = layout.GroundFloor.Rooms[0];
        var camera = GetNodeOrNull<Camera3D>("Eye");
        if (camera == null) { Finish(); return; }
        // a corner of the hall, a little above eye height, looking across it: a whole-room view
        // rather than a close-up of whatever stands in the bay the door happens to be on
        float floor = layout.FloorY(layout.Below);
        var inside = node.GlobalTransform
            * new Vector3(hall.X0 + 1.2f, floor + Math.Min(2.6f, layout.ClearOf(hall) - 0.6f), hall.Z0 + 1.2f);
        camera.GlobalPosition = inside;
        camera.LookAt(node.GlobalTransform
            * new Vector3((hall.X0 + hall.X1) / 2, floor + 1.0f, (hall.Z0 + hall.Z1) / 2), Vector3.Up);
        camera.MakeCurrent();
    }

    private void Finish()
    {
        GD.Print($"[site] RESULT: {(_ok ? "ok" : "FAILED")}");
        GetTree().Quit(_ok ? 0 : 1);
        SetProcess(false);
    }
}
