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
    private readonly record struct Spec(BuildingType Want, BuildingKind Kind, float Width, float Depth, float Wall);

    private static readonly Spec[] Specs =
    {
        new(BuildingType.Warehouse, BuildingKind.Industrial, 58f, 34f, 8f),
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
        }

        Check(_built.Count == Specs.Length, $"all {Specs.Length} sites built ({_built.Count})");
        GD.Print($"[site] plans written to {svgDir}");
        Ordinary();

        if (_shot == null || DisplayServer.GetName() == "headless") { Finish(); return; }
        AddChild(new Camera3D { Name = "Eye", Fov = 75f });
    }

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
                Triangles = Box(spec.Width, spec.Depth, spec.Wall),
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
    private static float[] Box(float w, float d, float h, float at = 0f)
    {
        float x0 = at - w / 2, x1 = at + w / 2, z0 = -d / 2, z1 = d / 2;
        var t = new List<float>();
        void Tri(Vector3 a, Vector3 b, Vector3 c) =>
            t.AddRange(new[] { a.X, a.Y, a.Z, c.X, c.Y, c.Z, b.X, b.Y, b.Z });
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
        // one last frame across a works' front wall: a row of roll-up bays beside its office door
        if (_step == _built.Count)
        {
            var eye = GetNodeOrNull<Camera3D>("Eye");
            if (eye == null) { Finish(); return; }
            // the warehouse is the first facade in the row, and a synthetic tile with no roads
            // puts its door on the south wall (+Z), so the yard side is beyond it
            eye.GlobalPosition = new Vector3(-30f, 12f, 968f);
            eye.LookAt(new Vector3(4f, 3.5f, 917f), Vector3.Up);
            eye.MakeCurrent();
            return;
        }
        if (_step > _built.Count)
        {
            GetViewport().GetTexture().GetImage().SavePng(_shot.Replace(".png", "_bays.png"));
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
